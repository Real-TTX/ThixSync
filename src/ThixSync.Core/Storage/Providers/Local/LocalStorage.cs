using System.Collections.Concurrent;
using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ThixSync.Core.Models;

namespace ThixSync.Core.Storage.Providers.Local;

/// <summary>
/// A folder of this computer (also a mounted share: <c>\\server\share\…</c>, <c>/Volumes/…</c>). Lists with many threads at once
/// (FileSystemEnumerable: size and time come with the directory entry, no extra call per file), writes to a temporary name and
/// renames (a cancelled copy never leaves a half file), keeps the modification time, skips links and junctions (no loops, nothing
/// outside the root). Other providers (SMB, NFS, external drives) open their mounted path with this class.
/// </summary>
public class LocalStorage : ILocalStorage
{
    /// <summary>The prefix of the temporary files while writing (the engine's filter leaves them out).</summary>
    public const string TempPrefix = ".~thixsync-";

    private const int BufferSize = 1024 * 1024;

    private readonly string _root;
    private readonly bool _isNetworkPath;

    public LocalStorage(string root, ConnectionKind kind, string? displayName = null)
    {
        _root = Path.GetFullPath(root);
        Kind = kind;
        DisplayName = displayName ?? _root;
        _isNetworkPath = _root.StartsWith(@"\\", StringComparison.Ordinal) || _root.StartsWith("/Volumes/", StringComparison.Ordinal) && kind != ConnectionKind.ExternalDrive;
        Capabilities = new StorageCapabilities
        {
            Features = StorageFeatures.SetModifiedTime | StorageFeatures.Move | StorageFeatures.RealFolders | StorageFeatures.LocalFileSystem
                | (OperatingSystem.IsLinux() ? StorageFeatures.CaseSensitive : 0)
                | (IsCoarseTimeVolume(_root) ? StorageFeatures.CoarseTime : 0),
            MaxParallelism = _isNetworkPath ? 8 : 16,
            InvalidNameCharacters = OperatingSystem.IsWindows() ? "<>:\"|?*\\" : ":",
        };
    }

    public ConnectionKind Kind { get; }

    public StorageCapabilities Capabilities { get; }

    public string DisplayName { get; }

    /// <summary>The root folder on this computer.</summary>
    public string Root => _root;

    public string GetLocalPath(string path)
    {
        var normalized = StoragePath.Normalize(path);
        return normalized.Length == 0 ? _root : Path.Combine(_root, normalized.Replace('/', Path.DirectorySeparatorChar));
    }

    public virtual Task ConnectAsync(bool createRoot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Directory.Exists(_root))
        {
            return Task.CompletedTask;
        }

        if (!createRoot)
        {
            throw new StorageException(StorageErrorKind.NotFound, $"Den Ordner {DisplayName} gibt es nicht (oder das Laufwerk ist nicht angeschlossen).");
        }

        return Run(() => Directory.CreateDirectory(_root), "");
    }

    public async IAsyncEnumerable<StorageEntry> ListAsync(string folder, Action<string, Exception>? onProblem, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var start = StoragePath.Normalize(folder);
        var channel = Channel.CreateBounded<StorageEntry>(new BoundedChannelOptions(8192) { SingleReader = true });
        var pending = new ConcurrentQueue<string>();
        pending.Enqueue(start);
        var walk = Task.Run(async () =>
        {
            try
            {
                await WalkAsync(pending, channel.Writer, onProblem, cancellationToken);
                channel.Writer.TryComplete();
            }
            catch (Exception exception)
            {
                channel.Writer.TryComplete(exception);
            }
        }, cancellationToken);

        await foreach (var entry in channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return entry;
        }

        await walk;
    }

    public Task<StorageEntry?> GetAsync(string path, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(path);
        var full = GetLocalPath(normalized);
        return Run<StorageEntry?>(() =>
        {
            var info = new FileInfo(full);
            if (info.Exists)
            {
                return ToEntry(normalized, info);
            }

            var folderInfo = new DirectoryInfo(full);
            return folderInfo.Exists ? ToEntry(normalized, folderInfo) : null;
        }, normalized);
    }

    public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(path);
        return Run<Stream>(() => new FileStream(GetLocalPath(normalized), new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            BufferSize = BufferSize,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        }), normalized);
    }

    public async Task<StorageEntry> WriteAsync(string path, Stream content, long length, DateTimeOffset modified, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(path);
        var target = GetLocalPath(normalized);
        var temp = TempPathFor(target);
        try
        {
            await Run(() => Directory.CreateDirectory(Path.GetDirectoryName(target)!), normalized);
            await using (var output = await Run(() => new FileStream(temp, new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = BufferSize,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                PreallocationSize = length > 0 ? length : 0,
            }), normalized))
            {
                await CopyAsync(content, output, progress, cancellationToken);
            }

            await Run(() =>
            {
                File.SetLastWriteTimeUtc(temp, modified.UtcDateTime);
                ReplaceWith(temp, target);
            }, normalized);
            return ToEntry(normalized, new FileInfo(target));
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>Copies a file of this computer into this storage with the operating system (fast, sparse/compressed aware).</summary>
    public async Task<StorageEntry> CopyFromLocalAsync(string sourceFile, string path, DateTimeOffset modified, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(path);
        var target = GetLocalPath(normalized);
        var temp = TempPathFor(target);
        try
        {
            await Task.Run(() => Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(sourceFile, temp, overwrite: true);
                File.SetLastWriteTimeUtc(temp, modified.UtcDateTime);
                ReplaceWith(temp, target);
            }, normalized), cancellationToken);
            return ToEntry(normalized, new FileInfo(target));
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    public Task CreateFolderAsync(string path, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(path);
        return Run(() => Directory.CreateDirectory(GetLocalPath(normalized)), normalized);
    }

    public Task DeleteAsync(string path, bool isFolder, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(path);
        if (normalized.Length == 0)
        {
            throw new StorageException(StorageErrorKind.AccessDenied, "Der Wurzelordner eines Auftrags wird nie gelöscht.");
        }

        var full = GetLocalPath(normalized);
        return Run(() =>
        {
            if (isFolder)
            {
                if (Directory.Exists(full))
                {
                    ClearReadOnly(full);
                    Directory.Delete(full, recursive: true);
                }

                return;
            }

            if (File.Exists(full))
            {
                File.SetAttributes(full, FileAttributes.Normal);
                File.Delete(full);
            }
        }, normalized);
    }

    public Task MoveAsync(string fromPath, string toPath, CancellationToken cancellationToken)
    {
        var from = GetLocalPath(StoragePath.Normalize(fromPath));
        var to = GetLocalPath(StoragePath.Normalize(toPath));
        return Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            if (Directory.Exists(from))
            {
                Directory.Move(from, to);
            }
            else
            {
                File.Move(from, to, overwrite: true);
            }
        }, StoragePath.Normalize(fromPath));
    }

    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    /// <summary>Maps an exception of the file system to a <see cref="StorageException"/> (German message, the relative path only).</summary>
    public static StorageException Map(Exception exception, string path)
    {
        var where = path.Length == 0 ? "" : $" ({path})";
        const int SharingViolation = unchecked((int)0x80070020);
        const int LockViolation = unchecked((int)0x80070021);
        const int DiskFull = unchecked((int)0x80070070);
        const int HandleDiskFull = unchecked((int)0x80070027);
        const int NetworkNameDeleted = unchecked((int)0x80070040);
        const int BadNetPath = unchecked((int)0x80070035);
        return exception switch
        {
            StorageException storage => storage,
            UnauthorizedAccessException => new StorageException(StorageErrorKind.AccessDenied, $"Kein Zugriff{where}.", exception),
            FileNotFoundException or DirectoryNotFoundException => new StorageException(StorageErrorKind.NotFound, $"Nicht gefunden{where}.", exception),
            PathTooLongException => new StorageException(StorageErrorKind.InvalidName, $"Der Pfad ist zu lang{where}.", exception),
            IOException io when io.HResult is SharingViolation or LockViolation =>
                new StorageException(StorageErrorKind.Locked, $"Die Datei wird gerade von einem anderen Programm verwendet{where}.", exception),
            IOException io when io.HResult is DiskFull or HandleDiskFull || io.HResult == 28 /* ENOSPC */ =>
                new StorageException(StorageErrorKind.QuotaExceeded, "Auf dem Laufwerk ist kein Platz mehr.", exception),
            IOException io when io.HResult is NetworkNameDeleted or BadNetPath =>
                new StorageException(StorageErrorKind.Unreachable, $"Die Netzwerkverbindung ist weg{where}.", exception),
            IOException => new StorageException(StorageErrorKind.Transient, $"{exception.Message}{where}", exception),
            ArgumentException => new StorageException(StorageErrorKind.InvalidName, $"Ungültiger Name{where}.", exception),
            _ => new StorageException(StorageErrorKind.Unknown, $"{exception.Message}{where}", exception),
        };
    }

    private async Task WalkAsync(ConcurrentQueue<string> pending, ChannelWriter<StorageEntry> writer, Action<string, Exception>? onProblem, CancellationToken cancellationToken)
    {
        // a pool of workers takes folders from the queue; each folder is read once (files and subfolders in one enumeration)
        var workers = Math.Clamp(Capabilities.MaxParallelism, 2, 16);
        var active = 0;
        var tasks = Enumerable.Range(0, workers).Select(_ => Task.Run(async () =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!pending.TryDequeue(out var folder))
                {
                    if (Volatile.Read(ref active) == 0 && pending.IsEmpty)
                    {
                        return;
                    }

                    await Task.Delay(1, cancellationToken);
                    continue;
                }

                Interlocked.Increment(ref active);
                try
                {
                    foreach (var entry in ReadFolder(folder, onProblem))
                    {
                        if (entry.IsFolder)
                        {
                            pending.Enqueue(entry.Path);
                        }

                        await writer.WriteAsync(entry, cancellationToken);
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            }
        }, cancellationToken)).ToArray();
        await Task.WhenAll(tasks);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private List<StorageEntry> ReadFolder(string folder, Action<string, Exception>? onProblem)
    {
        var full = GetLocalPath(folder);
        var prefix = folder.Length == 0 ? "" : folder + "/";
        var entries = new List<StorageEntry>();
        try
        {
            var enumerable = new FileSystemEnumerable<StorageEntry?>(full, (ref FileSystemEntry entry) =>
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return null; // links and junctions are not followed and not copied
                }

                var name = entry.FileName.ToString();
                if (name.StartsWith(TempPrefix, StringComparison.Ordinal))
                {
                    return null;
                }

                var hidden = (entry.Attributes & FileAttributes.Hidden) != 0 || name.StartsWith('.');
                var system = (entry.Attributes & FileAttributes.System) != 0;
                return new StorageEntry(prefix + name, entry.IsDirectory, entry.IsDirectory ? 0 : entry.Length, entry.LastWriteTimeUtc)
                {
                    IsHidden = hidden,
                    IsSystem = system,
                };
            }, new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0, ReturnSpecialDirectories = false });

            foreach (var entry in enumerable)
            {
                if (entry is { } value)
                {
                    entries.Add(value);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            onProblem?.Invoke(folder, Map(exception, folder));
        }

        return entries;
    }

    private static StorageEntry ToEntry(string path, FileSystemInfo info) =>
        new(path, info is DirectoryInfo, info is FileInfo file ? file.Length : 0, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero))
        {
            IsHidden = (info.Attributes & FileAttributes.Hidden) != 0 || info.Name.StartsWith('.'),
            IsSystem = (info.Attributes & FileAttributes.System) != 0,
        };

    private static async Task CopyAsync(Stream source, Stream target, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                progress?.Report(read);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void ReplaceWith(string temp, string target)
    {
        if (File.Exists(target))
        {
            var attributes = File.GetAttributes(target);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(target, attributes & ~FileAttributes.ReadOnly);
            }
        }

        File.Move(temp, target, overwrite: true);
    }

    private static string TempPathFor(string target) =>
        Path.Combine(Path.GetDirectoryName(target)!, $"{TempPrefix}{Guid.NewGuid():N}.tmp");

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void ClearReadOnly(string folder)
    {
        foreach (var file in Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true }))
        {
            var attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            }
        }
    }

    private static bool IsCoarseTimeVolume(string root)
    {
        try
        {
            if (root.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return false;
            }

            var format = new DriveInfo(Path.GetPathRoot(root)!).DriveFormat;
            return format.StartsWith("FAT", StringComparison.OrdinalIgnoreCase) || format.Equals("exFAT", StringComparison.OrdinalIgnoreCase)
                || format.Equals("msdos", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static Task Run(Action action, string path)
    {
        try
        {
            action();
            return Task.CompletedTask;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Task.FromException(Map(exception, path));
        }
    }

    private static Task<T> Run<T>(Func<T> action, string path)
    {
        try
        {
            return Task.FromResult(action());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Task.FromException<T>(Map(exception, path));
        }
    }
}
