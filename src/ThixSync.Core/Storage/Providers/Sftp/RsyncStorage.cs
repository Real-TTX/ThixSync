using System.Runtime.CompilerServices;
using Renci.SshNet;
using ThixSync.Core.Infrastructure;
using ThixSync.Core.Models;

namespace ThixSync.Core.Storage.Providers.Sftp;

/// <summary>
/// "rsync über SSH": the SFTP storage plus commands on the server over the same SSH sign-in. The whole tree is listed with one
/// <c>find … -printf</c> (one round trip instead of one per folder – much faster with many small files); servers whose find has no
/// -printf (BSD) or that allow no commands are listed over SFTP. Whole folders are deleted with <c>rm -rf</c>. Files travel whole
/// over SFTP: the rsync delta protocol itself is not spoken (the rsync path of the connection is kept for a later delta mode).
/// </summary>
internal sealed class RsyncStorage : SftpStorage
{
    private readonly SemaphoreSlim _shellGate = new(1, 1);
    private SshClient? _shell;
    private int _commands; // 0 unknown, 1 find -printf works, -1 use SFTP (no -printf, no commands allowed)

    public RsyncStorage(SshEndpoint endpoint, string root, string displayName, int parallelism = 8)
        : base(ConnectionKind.RsyncSsh, endpoint, root, displayName, parallelism)
    {
    }

    public override async IAsyncEnumerable<StorageEntry> ListAsync(string folder, Action<string, Exception>? onProblem, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var start = StoragePath.Normalize(folder);
        if (Volatile.Read(ref _commands) >= 0)
        {
            var shell = await GetShellAsync(cancellationToken);
            using var command = shell.CreateCommand(RemoteShell.FindCommand(Absolute(start)));
            var execution = Start(command, cancellationToken);
            var pending = new List<byte>(64 * 1024);
            var buffer = new byte[64 * 1024];
            var started = false;
            try
            {
                while (true)
                {
                    var read = await ReadAsync(command.OutputStream, buffer, cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    pending.AddRange(buffer.AsSpan(0, read));
                    foreach (var record in RemoteShell.TakeRecords(pending))
                    {
                        started |= RemoteShell.IsStartRecord(record);
                        if (started && RemoteShell.ParseFindRecord(record, start) is { } entry)
                        {
                            yield return entry;
                        }
                    }
                }

                await WaitAsync(execution, cancellationToken);
            }
            finally
            {
                if (!execution.IsCompleted)
                {
                    StopQuietly(command);
                }

                _ = execution.ContinueWith(task => task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }

            if (started)
            {
                Volatile.Write(ref _commands, 1);
                if (command.ExitStatus != 0)
                {
                    // find ran but could not read some folders: they are reported, the rest of the listing stands
                    ReportProblems(command.Error, onProblem);
                }

                yield break;
            }

            // find did not run (no -printf, no commands allowed, the folder is missing): SFTP lists – and tells what is wrong
            if (RemoteShell.FindLacksPrintf(command.Error) || command.ExitStatus is null or 0 or 126 or 127)
            {
                Log.Info("rsync over SSH: find -printf is not available on the server, listing over SFTP");
                Volatile.Write(ref _commands, -1);
            }
        }

        await foreach (var entry in base.ListAsync(start, onProblem, cancellationToken))
        {
            yield return entry;
        }
    }

    public override async ValueTask DisposeAsync()
    {
        _shell?.Dispose();
        _shellGate.Dispose();
        await base.DisposeAsync();
    }

    protected override async Task DeleteFolderAsync(string absolute, string path, CancellationToken cancellationToken)
    {
        // never the root, never anything outside it
        if (Volatile.Read(ref _commands) < 0 || absolute == Root || !absolute.StartsWith(Root.TrimEnd('/') + "/", StringComparison.Ordinal))
        {
            await base.DeleteFolderAsync(absolute, path, cancellationToken);
            return;
        }

        var shell = await GetShellAsync(cancellationToken);
        using var command = shell.CreateCommand(RemoteShell.RemoveTreeCommand(absolute));
        await WaitAsync(Start(command, cancellationToken), cancellationToken);

        // only what SFTP sees counts (a server that allows SFTP only answers every command with nothing); whatever rm left
        // (no commands allowed, a protected file) is deleted over SFTP, which names the file it cannot delete
        if (command.ExitStatus != 0 || await GetAsync(path, cancellationToken) is not null)
        {
            await base.DeleteFolderAsync(absolute, path, cancellationToken);
        }
    }

    /// <summary>Starts a command and closes its input at once: nothing on the server waits for input that never comes.</summary>
    private static Task Start(SshCommand command, CancellationToken cancellationToken)
    {
        var execution = command.ExecuteAsync(cancellationToken);
        try
        {
            command.CreateInputStream().Dispose();
        }
        catch (InvalidOperationException)
        {
            // it has ended already
        }

        return execution;
    }

    private async Task<SshClient> GetShellAsync(CancellationToken cancellationToken)
    {
        await _shellGate.WaitAsync(cancellationToken);
        try
        {
            if (_shell is { IsConnected: true })
            {
                return _shell;
            }

            _shell?.Dispose();
            _shell = await Endpoint.ConnectShellAsync(cancellationToken);
            return _shell;
        }
        finally
        {
            _shellGate.Release();
        }
    }

    private void ReportProblems(string errors, Action<string, Exception>? onProblem)
    {
        if (onProblem is null)
        {
            return;
        }

        foreach (var line in errors.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // find: '/backup/Fotos/privat': Permission denied
            var open = line.IndexOf('\'');
            var close = line.LastIndexOf("':", StringComparison.Ordinal);
            var absolute = open >= 0 && close > open ? line[(open + 1)..close] : "";
            var relative = absolute.StartsWith(Root.TrimEnd('/') + "/", StringComparison.Ordinal) ? absolute[(Root.TrimEnd('/').Length + 1)..] : "";
            var kind = line.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) ? StorageErrorKind.AccessDenied : StorageErrorKind.Unknown;
            onProblem(relative, new StorageException(kind, kind == StorageErrorKind.AccessDenied
                ? $"Kein Zugriff{(relative.Length > 0 ? $" ({relative})" : "")}."
                : "Ein Ordner auf dem Server konnte nicht gelesen werden."));
        }
    }

    private static async Task<int> ReadAsync(Stream output, byte[] buffer, CancellationToken cancellationToken)
    {
        try
        {
            // the output of SSH.NET blocks while it waits for data: read on the thread pool
            return await Task.Run(() => output.Read(buffer, 0, buffer.Length), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw SshErrors.Map(exception, "", cancellationToken);
        }
    }

    private static async Task WaitAsync(Task execution, CancellationToken cancellationToken)
    {
        try
        {
            await execution;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw SshErrors.Map(exception, "", cancellationToken);
        }
    }

    private static void StopQuietly(SshCommand command)
    {
        try
        {
            command.CancelAsync(forceKill: true, millisecondsTimeout: 200);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException or Renci.SshNet.Common.SshException)
        {
            // it ended on its own
        }
    }
}
