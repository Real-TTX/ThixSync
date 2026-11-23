using System.Reflection;
using ThixSync.Core.Models;

namespace ThixSync.Core.Storage;

/// <summary>
/// What a provider gets to open a storage: the connection (fields), the folder inside it, the secret, and the ways back to the
/// business layer for what a provider learns while connecting (a renewed OAuth token, the host key seen the first time).
/// </summary>
public sealed record StorageContext
{
    public required Connection Connection { get; init; }

    /// <summary>
    /// The folder the storage is rooted at, as the user wrote it for this connection type: an absolute local path for
    /// <see cref="ConnectionKind.Local"/> ("C:\Users\Anna\Dokumente", "/Users/anna/Documents"), otherwise a path below the
    /// connection's base folder ("Fotos/2024", "/Fotos/2024"). The provider combines it with <see cref="ConnectionFields.BasePath"/>.
    /// </summary>
    public string Folder { get; init; } = "";

    /// <summary>The secret of the connection (password, secret key, refresh token JSON, pairing key); null when none is kept.</summary>
    public string? Secret { get; init; }

    /// <summary>Keeps a new secret (a refreshed OAuth token). Safe to call from any thread.</summary>
    public Func<string, CancellationToken, Task> SaveSecretAsync { get; init; } = (_, _) => Task.CompletedTask;

    /// <summary>Keeps a field the provider learned (the SSH host key, the FTP certificate, the signed-in account …).</summary>
    public Func<string, string, CancellationToken, Task> SaveSettingAsync { get; init; } = (_, _, _) => Task.CompletedTask;

    /// <summary>The name of this computer (Remote, conflicts).</summary>
    public string DeviceName { get; init; } = Environment.MachineName;
}

/// <summary>
/// Opens storages of one or more connection types. Implementations live in <c>Storage/Providers/…</c>, have a public parameterless
/// constructor and are found by <see cref="StorageProviders"/> (no list to edit).
/// </summary>
public interface IStorageProvider
{
    IReadOnlyList<ConnectionKind> Kinds { get; }

    /// <summary>A storage for the context (not yet connected: the engine calls <see cref="IStorage.ConnectAsync"/>).</summary>
    IStorage Open(StorageContext context);
}

/// <summary>A provider that can show the folders of a connection (the folder picker of the job form).</summary>
public interface IFolderBrowser
{
    /// <summary>The folders directly below <paramref name="folder"/> (same notation as <see cref="StorageContext.Folder"/>).</summary>
    Task<IReadOnlyList<string>> ListFoldersAsync(StorageContext context, string folder, CancellationToken cancellationToken);
}

/// <summary>Every provider of the program (found once by reflection over this assembly; tests may add their own).</summary>
public static class StorageProviders
{
    private static readonly Lock Gate = new();
    private static Dictionary<ConnectionKind, IStorageProvider>? _byKind;
    private static readonly Dictionary<ConnectionKind, IStorageProvider> Overrides = [];

    /// <summary>The provider of a type, or null when none is built in yet.</summary>
    public static IStorageProvider? Find(ConnectionKind kind)
    {
        lock (Gate)
        {
            if (Overrides.TryGetValue(kind, out var overridden))
            {
                return overridden;
            }

            _byKind ??= Discover();
            return _byKind.GetValueOrDefault(kind);
        }
    }

    public static bool IsSupported(ConnectionKind kind) => Find(kind) is not null;

    /// <summary>Tests: replace the provider of a type (null removes every override).</summary>
    internal static void OverrideForTests(ConnectionKind? kind, IStorageProvider? provider)
    {
        lock (Gate)
        {
            if (kind is null)
            {
                Overrides.Clear();
                return;
            }

            if (provider is null)
            {
                Overrides.Remove(kind.Value);
            }
            else
            {
                Overrides[kind.Value] = provider;
            }
        }
    }

    private static Dictionary<ConnectionKind, IStorageProvider> Discover()
    {
        var found = new Dictionary<ConnectionKind, IStorageProvider>();
        foreach (var type in typeof(StorageProviders).Assembly.GetTypes())
        {
            if (type.IsAbstract || type.IsInterface || !typeof(IStorageProvider).IsAssignableFrom(type) || type.GetConstructor(Type.EmptyTypes) is null)
            {
                continue;
            }

            var provider = (IStorageProvider)Activator.CreateInstance(type)!;
            foreach (var kind in provider.Kinds)
            {
                found[kind] = provider;
            }
        }

        return found;
    }
}
