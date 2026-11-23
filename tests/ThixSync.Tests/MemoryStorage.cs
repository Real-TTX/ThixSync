using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using ThixSync.Core.Models;
using ThixSync.Core.Storage;

namespace ThixSync.Tests;

/// <summary>
/// A storage in memory for the engine tests (one "drive" per bucket name): its features can be chosen (no modification times,
/// case-insensitive, coarse times, no move, a hash kind), folders can be made unreadable, operations can fail N times with any
/// error, and writes can be slowed down. Every operation is counted.
/// </summary>
internal sealed class MemoryDrive
{
    private readonly ConcurrentDictionary<string, MemoryFile> _files;
    private readonly ConcurrentDictionary<string, string> _folders;
    private readonly List<FailureRule> _failures = [];
    private readonly Lock _gate = new();
    private long _uploadTicks = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks;

    public MemoryDrive(StorageFeatures features = StorageFeatures.SetModifiedTime | StorageFeatures.Move | StorageFeatures.RealFolders, string? hashKind = null)
    {
        Features = features;
        HashKind = hashKind;
        var comparer = features.HasFlag(StorageFeatures.CaseSensitive) ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        _files = new ConcurrentDictionary<string, MemoryFile>(comparer);
        _folders = new ConcurrentDictionary<string, string>(comparer);
        Unreadable = new ConcurrentDictionary<string, bool>(comparer);
    }

    public StorageFeatures Features { get; }

    /// <summary>"md5" (hashes in the listing) or null.</summary>
    public string? HashKind { get; }

    public int MaxParallelism { get; set; } = 8;

    /// <summary>Folders (full paths) whose content cannot be listed.</summary>
    public ConcurrentDictionary<string, bool> Unreadable { get; }

    /// <summary>Each write waits this long (cancellable) before it stores the file.</summary>
    public TimeSpan WriteDelay { get; set; }

    /// <summary>Called after each stored write (path).</summary>
    public Action<string>? OnWrite { get; set; }

    public ConcurrentQueue<(string Operation, string Path)> Operations { get; } = new();

    public int Count(string operation) => Operations.Count(entry => entry.Operation == operation);

    public void ClearOperations() => Operations.Clear();

    /// <summary>The next <paramref name="times"/> operations of a kind on a path containing <paramref name="pathContains"/> fail.</summary>
    public void Fail(string operation, string pathContains, int times, Func<Exception> error)
    {
        lock (_gate)
        {
            _failures.Add(new FailureRule(operation, pathContains, times, error));
        }
    }

    public void Put(string path, string content, DateTimeOffset modified) => Store(path, Encoding.UTF8.GetBytes(content), modified);

    public void PutBytes(string path, byte[] content, DateTimeOffset modified) => Store(path, content, modified);

    public string? Read(string path) => _files.TryGetValue(StoragePath.Normalize(path), out var file) ? Encoding.UTF8.GetString(file.Content) : null;

    public bool Exists(string path) => _files.ContainsKey(StoragePath.Normalize(path));

    public MemoryFile? Get(string path) => _files.GetValueOrDefault(StoragePath.Normalize(path));

    /// <summary>The files (full paths), ordered.</summary>
    public List<string> Files => [.. _files.Values.Select(file => file.Path).Order(StringComparer.Ordinal)];

    public void Remove(string path) => _files.TryRemove(StoragePath.Normalize(path), out _);

    /// <summary>Renames a file in place (keeps content and time), as a user would.</summary>
    public void Rename(string from, string to)
    {
        if (_files.TryRemove(StoragePath.Normalize(from), out var file))
        {
            Store(to, file.Content, file.Modified);
        }
    }

    internal void Check(string operation, string path)
    {
        Operations.Enqueue((operation, path));
        lock (_gate)
        {
            var rule = _failures.FirstOrDefault(rule => rule.Operation == operation && rule.Times > 0 && path.Contains(rule.PathContains, StringComparison.Ordinal));
            if (rule is null)
            {
                return;
            }

            rule.Times--;
            throw rule.Error();
        }
    }

    internal MemoryFile Store(string path, byte[] content, DateTimeOffset modified)
    {
        var normalized = StoragePath.Normalize(path);
        var hash = HashKind == "md5" ? $"md5:{Convert.ToHexStringLower(MD5.HashData(content))}" : null;
        var name = _files.TryGetValue(normalized, out var existing) ? existing.Path : normalized;
        var file = new MemoryFile(name, content, modified, hash);
        _files[normalized] = file;
        if (Features.HasFlag(StorageFeatures.RealFolders))
        {
            for (var parent = StoragePath.ParentOf(normalized); parent.Length > 0; parent = StoragePath.ParentOf(parent))
            {
                _folders.TryAdd(parent, parent);
            }
        }

        return file;
    }

    internal DateTimeOffset NextUploadTime() => new(Interlocked.Add(ref _uploadTicks, TimeSpan.TicksPerMinute), TimeSpan.Zero);

    internal bool FolderExists(string path) =>
        path.Length == 0 || _folders.ContainsKey(path) || _files.Keys.Any(file => StoragePath.IsInside(file, path, StringComparison.OrdinalIgnoreCase) && file.Length > path.Length);

    internal void AddFolder(string path)
    {
        for (var folder = StoragePath.Normalize(path); folder.Length > 0; folder = StoragePath.ParentOf(folder))
        {
            _folders.TryAdd(folder, folder);
        }
    }

    internal IEnumerable<MemoryFile> AllFiles => _files.Values;

    internal IEnumerable<string> AllFolders => _folders.Values;

    internal void RemoveFile(string path) => _files.TryRemove(path, out _);

    internal void RemoveFolderTree(string path)
    {
        var comparison = Features.HasFlag(StorageFeatures.CaseSensitive) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        foreach (var file in _files.Keys.Where(file => StoragePath.IsInside(file, path, comparison)).ToList())
        {
            _files.TryRemove(file, out _);
        }

        foreach (var folder in _folders.Keys.Where(folder => StoragePath.IsInside(folder, path, comparison)).ToList())
        {
            _folders.TryRemove(folder, out _);
        }
    }

    private sealed class FailureRule(string operation, string pathContains, int times, Func<Exception> error)
    {
        public string Operation { get; } = operation;

        public string PathContains { get; } = pathContains;

        public int Times { get; set; } = times;

        public Func<Exception> Error { get; } = error;
    }
}

internal sealed record MemoryFile(string Path, byte[] Content, DateTimeOffset Modified, string? Hash);

/// <summary>A storage rooted at a folder of a <see cref="MemoryDrive"/>.</summary>
internal sealed class MemoryStorage(MemoryDrive drive, string root) : IStorage
{
    public ConnectionKind Kind => ConnectionKind.S3;

    public StorageCapabilities Capabilities { get; } = new()
    {
        Features = drive.Features,
        MaxParallelism = drive.MaxParallelism,
        HashKind = drive.HashKind,
    };

    public string DisplayName => $"memory://{root}";

    public Task ConnectAsync(bool createRoot, CancellationToken cancellationToken)
    {
        drive.Check("connect", root);
        if (drive.FolderExists(root))
        {
            return Task.CompletedTask;
        }

        if (!createRoot)
        {
            throw new StorageException(StorageErrorKind.NotFound, "Den Ordner gibt es nicht.");
        }

        drive.AddFolder(root);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<StorageEntry> ListAsync(string folder, Action<string, Exception>? onProblem, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        var start = Full(folder);
        drive.Check("list", start);
        var comparison = drive.Features.HasFlag(StorageFeatures.CaseSensitive) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var unreadable = drive.Unreadable.Keys.Where(path => StoragePath.IsInside(path, start, comparison)).ToList();
        foreach (var path in unreadable)
        {
            onProblem?.Invoke(Relative(path), new StorageException(StorageErrorKind.AccessDenied, "Kein Zugriff."));
        }

        bool Hidden(string path) => unreadable.Any(folder => path.Length > folder.Length && StoragePath.IsInside(path, folder, comparison));

        var folders = new HashSet<string>(drive.AllFolders, comparison == StringComparison.Ordinal ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        foreach (var file in drive.AllFiles.ToList())
        {
            for (var parent = StoragePath.ParentOf(file.Path); parent.Length > 0; parent = StoragePath.ParentOf(parent))
            {
                folders.Add(parent);
            }
        }

        foreach (var path in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (path.Length > start.Length && StoragePath.IsInside(path, start, comparison) && !Hidden(path))
            {
                yield return new StorageEntry(Relative(path), true, 0, default) { IsHidden = StoragePath.NameOf(path).StartsWith('.') };
            }
        }

        foreach (var file in drive.AllFiles.ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.Path.Length > start.Length && StoragePath.IsInside(file.Path, start, comparison) && !Hidden(file.Path))
            {
                yield return ToEntry(file);
            }
        }
    }

    public Task<StorageEntry?> GetAsync(string path, CancellationToken cancellationToken)
    {
        var full = Full(path);
        drive.Check("get", full);
        if (drive.Get(full) is { } file)
        {
            return Task.FromResult<StorageEntry?>(ToEntry(file));
        }

        return Task.FromResult<StorageEntry?>(drive.AllFolders.Any(folder => string.Equals(folder, full, StringComparison.OrdinalIgnoreCase))
            ? new StorageEntry(Relative(full), true, 0, default)
            : null);
    }

    public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        var full = Full(path);
        drive.Check("read", full);
        var file = drive.Get(full) ?? throw new StorageException(StorageErrorKind.NotFound, "Nicht gefunden.");
        return Task.FromResult<Stream>(new MemoryStream(file.Content, writable: false));
    }

    public async Task<StorageEntry> WriteAsync(string path, Stream content, long length, DateTimeOffset modified, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var full = Full(path);
        drive.Check("write", full);
        if (drive.WriteDelay > TimeSpan.Zero)
        {
            await Task.Delay(drive.WriteDelay, cancellationToken);
        }

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length != length)
        {
            throw new StorageException(StorageErrorKind.ChangedWhileReading, "Die Länge stimmt nicht.");
        }

        var time = !drive.Features.HasFlag(StorageFeatures.SetModifiedTime) ? drive.NextUploadTime()
            : drive.Features.HasFlag(StorageFeatures.CoarseTime) ? new DateTimeOffset(modified.UtcTicks / (2 * TimeSpan.TicksPerSecond) * (2 * TimeSpan.TicksPerSecond), TimeSpan.Zero)
            : modified;
        var file = drive.Store(full, buffer.ToArray(), time);
        drive.Operations.Enqueue(("written", full));
        drive.OnWrite?.Invoke(full);
        return ToEntry(file);
    }

    public Task CreateFolderAsync(string path, CancellationToken cancellationToken)
    {
        var full = Full(path);
        drive.Check("mkdir", full);
        drive.AddFolder(full);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string path, bool isFolder, CancellationToken cancellationToken)
    {
        var full = Full(path);
        drive.Check("delete", full);
        if (isFolder)
        {
            drive.RemoveFolderTree(full);
        }
        else
        {
            drive.RemoveFile(full);
        }

        return Task.CompletedTask;
    }

    public Task MoveAsync(string fromPath, string toPath, CancellationToken cancellationToken)
    {
        if (!drive.Features.HasFlag(StorageFeatures.Move))
        {
            throw new NotSupportedException();
        }

        var from = Full(fromPath);
        var to = Full(toPath);
        drive.Check("move", from);
        if (drive.Get(from) is { } file)
        {
            drive.RemoveFile(from);
            drive.Store(to, file.Content, file.Modified);
            return Task.CompletedTask;
        }

        var comparison = drive.Features.HasFlag(StorageFeatures.CaseSensitive) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var moved = drive.AllFiles.Where(each => StoragePath.IsInside(each.Path, from, comparison)).ToList();
        if (moved.Count == 0 && !drive.AllFolders.Any(folder => string.Equals(folder, from, comparison)))
        {
            throw new StorageException(StorageErrorKind.NotFound, "Nicht gefunden.");
        }

        drive.RemoveFolderTree(from);
        drive.AddFolder(to);
        foreach (var each in moved)
        {
            drive.Store(to + each.Path[from.Length..], each.Content, each.Modified);
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private string Full(string path) => StoragePath.Combine(root, StoragePath.Normalize(path));

    private string Relative(string full) => root.Length == 0 ? full : full.Length == root.Length ? "" : full[(root.Length + 1)..];

    private StorageEntry ToEntry(MemoryFile file) =>
        new(Relative(file.Path), false, file.Content.Length, file.Modified) { Hash = file.Hash, IsHidden = StoragePath.NameOf(file.Path).StartsWith('.') };
}

/// <summary>Opens <see cref="MemoryStorage"/> for connections of type S3: the bucket names the drive, the folder of a pair the root.</summary>
internal sealed class MemoryProvider : IStorageProvider
{
    public static ConcurrentDictionary<string, MemoryDrive> Drives { get; } = new();

    public IReadOnlyList<ConnectionKind> Kinds { get; } = [ConnectionKind.S3];

    public IStorage Open(StorageContext context) =>
        new MemoryStorage(Drives.GetOrAdd(context.Connection.Get(ConnectionFields.Bucket), _ => new MemoryDrive()), StoragePath.Normalize(context.Folder));
}
