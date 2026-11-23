using System.Globalization;
using ThixSync.Core.Infrastructure;
using ThixSync.Core.Models;
using ThixSync.Core.Storage;

namespace ThixSync.Core.Business;

/// <summary>
/// The connections (page "Geräte"): list, create, change, delete, test, open. Every check is here (fields of the type, names,
/// policies, "is this connection usable on this computer"); the UI only offers. The secret of a connection is kept protected on this
/// computer (DPAPI / Keychain) and never synced; a connection that comes from the cloud has no secret here until the user enters it.
/// </summary>
public static class ConnectionService
{
    private static readonly Lock Gate = new();
    private static List<Connection>? _cache;

    /// <summary>Raised after any change (any thread).</summary>
    public static event EventHandler? Changed;

    /// <summary>All connections, by name. "Dieser Computer" always exists (<see cref="EnsureThisComputer"/>).</summary>
    public static IReadOnlyList<Connection> List()
    {
        lock (Gate)
        {
            return _cache ??= SyncStore.LoadConnections();
        }
    }

    public static Connection? Find(Guid id) => List().FirstOrDefault(connection => connection.Id == id);

    public static Connection Get(Guid id) =>
        Find(id) ?? throw new BusinessException("Diese Verbindung gibt es nicht mehr.");

    /// <summary>
    /// Searches, filters and sorts the list (the toolbar of the page). <paramref name="search"/> matches name, type and address.
    /// </summary>
    public static IReadOnlyList<Connection> Query(string? search, ConnectionGroup? group, ConnectionSort sort)
    {
        IEnumerable<Connection> connections = List();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var text = search.Trim();
            connections = connections.Where(connection =>
                connection.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                || ConnectionCatalog.Get(connection.Kind).Name.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                || Address(connection).Contains(text, StringComparison.CurrentCultureIgnoreCase));
        }

        if (group is { } wanted)
        {
            connections = connections.Where(connection => ConnectionCatalog.Get(connection.Kind).Group == wanted);
        }

        return sort switch
        {
            ConnectionSort.Type => [.. connections.OrderBy(connection => ConnectionCatalog.Get(connection.Kind).Group).ThenBy(connection => ConnectionCatalog.Get(connection.Kind).Name).ThenBy(connection => connection.Name, StringComparer.CurrentCultureIgnoreCase)],
            ConnectionSort.Changed => [.. connections.OrderByDescending(connection => connection.UpdateDate)],
            _ => [.. connections.OrderBy(connection => connection.Name, StringComparer.CurrentCultureIgnoreCase)],
        };
    }

    /// <summary>The connection "Dieser Computer" of this computer (made on first use).</summary>
    public static Connection EnsureThisComputer()
    {
        var device = AppInfo.MachineName;
        var existing = List().FirstOrDefault(connection => connection.Kind == ConnectionKind.Local && string.Equals(connection.DeviceName, device, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }

        var now = AppClock.UtcNow;
        var connection = new Connection
        {
            Name = device,
            Kind = ConnectionKind.Local,
            DeviceName = device,
            CreateDate = now,
            UpdateDate = now,
            SyncState = SyncState.Pending,
        };
        SyncStore.SaveConnection(connection, null, false);
        ChangedNow();
        return connection;
    }

    /// <summary>A short address for lists ("nas.local/daten", "s3://bucket/prefix", "Google Drive · anna@…").</summary>
    public static string Address(Connection connection)
    {
        string Value(string key) => ConnectionCatalog.Value(connection, key);
        var basePath = Value(ConnectionFields.BasePath).Trim('/');
        string WithBase(string text) => basePath.Length == 0 ? text : $"{text.TrimEnd('/')}/{basePath}";
        return connection.Kind switch
        {
            ConnectionKind.Local => connection.DeviceName,
            ConnectionKind.ExternalDrive => $"{Value(ConnectionFields.VolumeLabel)} · {connection.DeviceName}",
            ConnectionKind.Smb => WithBase($@"\\{Value(ConnectionFields.Host)}\{Value(ConnectionFields.Share)}"),
            ConnectionKind.Nfs => WithBase($"{Value(ConnectionFields.Host)}:{Value(ConnectionFields.Share)}"),
            ConnectionKind.Synology or ConnectionKind.Qnap => WithBase($"{Value(ConnectionFields.Protocol)}://{Value(ConnectionFields.Host)}" +
                (Value(ConnectionFields.Protocol) == "smb" ? $"/{Value(ConnectionFields.Share)}" : "")),
            ConnectionKind.Sftp or ConnectionKind.RsyncSsh => WithBase($"{Value(ConnectionFields.UserName)}@{Value(ConnectionFields.Host)}"),
            ConnectionKind.Ftp => WithBase($"{(Value(ConnectionFields.Encryption) == "none" ? "ftp" : "ftps")}://{Value(ConnectionFields.Host)}"),
            ConnectionKind.WebDav or ConnectionKind.Nextcloud or ConnectionKind.Seafile => WithBase(Value(ConnectionFields.Url)),
            ConnectionKind.HetznerStorageBox => WithBase(HetznerHost(connection)),
            ConnectionKind.S3 or ConnectionKind.BackblazeB2 or ConnectionKind.Wasabi or ConnectionKind.GoogleCloudStorage =>
                WithBase($"{Value(ConnectionFields.Bucket)}"),
            ConnectionKind.AzureBlob => WithBase($"{Value(ConnectionFields.AccountName)}/{Value(ConnectionFields.Container)}"),
            ConnectionKind.SharePoint => WithBase(Value(ConnectionFields.SiteUrl)),
            ConnectionKind.ThixSyncRemote => $"{Value(ConnectionFields.Host)} · {Value(ConnectionFields.RemoteShare)}",
            ConnectionKind.MatCloud => WithBase(SettingsService.Get().Cloud.ServerUrl),
            _ => WithBase(Value(ConnectionFields.SignedInAs).Length > 0 ? Value(ConnectionFields.SignedInAs) : Value(ConnectionFields.UserName)),
        };
    }

    /// <summary>The server of a Hetzner Storage Box (from the user name when no host is set).</summary>
    public static string HetznerHost(Connection connection)
    {
        var host = connection.Get(ConnectionFields.Host);
        if (host.Length > 0)
        {
            return host;
        }

        var user = connection.Get(ConnectionFields.UserName);
        var main = user.Split('-')[0];
        return main.Length == 0 ? "" : $"{main}.your-storagebox.de";
    }

    /// <summary>Whether this computer can reach a connection (a local one only on its computer).</summary>
    public static bool IsUsableHere(Connection connection, out string reason)
    {
        var type = ConnectionCatalog.Get(connection.Kind);
        if (type.IsBoundToDevice && !string.Equals(connection.DeviceName, AppInfo.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            reason = $"Gehört zu {connection.DeviceName}.";
            return false;
        }

        if (!StorageProviders.IsSupported(connection.Kind))
        {
            reason = "Dieser Verbindungstyp ist in dieser Version noch nicht verfügbar.";
            return false;
        }

        if (connection.Kind == ConnectionKind.MatCloud && !CloudSyncService.IsConnected)
        {
            reason = "Diesen Computer zuerst mit der MatCloud verbinden (Einstellungen › Cloud).";
            return false;
        }

        if (NeedsSecret(connection))
        {
            reason = type.Auth == AuthKind.OAuth ? "Auf diesem Computer noch nicht angemeldet." : $"{type.SecretLabel} fehlt auf diesem Computer.";
            return false;
        }

        reason = "";
        return true;
    }

    /// <summary>The secret is required but not kept on this computer (e.g. the connection came from the cloud).</summary>
    public static bool NeedsSecret(Connection connection)
    {
        var type = ConnectionCatalog.Get(connection.Kind);
        if (connection.HasSecret)
        {
            return false;
        }

        return type.Auth == AuthKind.OAuth || type.Auth == AuthKind.Pairing || type.IsSecretRequired(connection.Settings);
    }

    /// <summary>
    /// Saves a connection after every check. <paramref name="secret"/>: null keeps the stored secret, "" removes it, anything else
    /// replaces it. Returns the saved connection.
    /// </summary>
    public static Connection Save(Connection connection, string? secret)
    {
        AccessPolicy.EnsureConfigurationEditable();
        var type = ConnectionCatalog.Get(connection.Kind);
        if (!type.IsAvailable)
        {
            throw new BusinessException("Dieser Verbindungstyp ist auf diesem Computer nicht verfügbar.", "Kind");
        }

        AccessPolicy.EnsureConnectionKindAllowed(connection.Kind);
        var existing = Find(connection.Id);
        if (existing is not null && existing.Kind != connection.Kind)
        {
            throw new BusinessException("Der Typ einer Verbindung lässt sich nicht ändern – lege eine neue an.", "Kind");
        }

        var name = connection.Name.Trim();
        if (name.Length == 0)
        {
            throw new BusinessException("Bitte einen Namen eingeben.", nameof(Connection.Name));
        }

        if (name.Length > 100)
        {
            throw new BusinessException("Der Name ist zu lang (höchstens 100 Zeichen).", nameof(Connection.Name));
        }

        if (List().Any(other => other.Id != connection.Id && string.Equals(other.Name, name, StringComparison.CurrentCultureIgnoreCase)))
        {
            throw new BusinessException("Eine Verbindung mit diesem Namen gibt es schon.", nameof(Connection.Name));
        }

        var settings = CleanSettings(type, connection.Settings);
        Validate(type, settings);
        if (type.IsSecretRequired(settings) && secret is { Length: 0 })
        {
            throw new BusinessException($"Bitte {type.SecretLabel} eingeben.", "Secret");
        }

        if (type.IsSecretRequired(settings) && secret is null && existing is null)
        {
            throw new BusinessException($"Bitte {type.SecretLabel} eingeben.", "Secret");
        }

        var now = AppClock.UtcNow;
        var saved = connection with
        {
            Name = name,
            Settings = settings,
            DeviceName = type.IsBoundToDevice ? (existing?.DeviceName is { Length: > 0 } device ? device : AppInfo.MachineName) : "",
            CreateDate = existing?.CreateDate ?? now,
            UpdateDate = now,
            SyncState = SyncState.Pending,
        };

        string? protectedSecret = null;
        bool? hasSecret = null;
        if (secret is not null)
        {
            protectedSecret = secret.Length == 0 ? "" : TokenStore.Save(SecretAccount(saved.Id), secret);
            if (secret.Length == 0)
            {
                TokenStore.Delete(SecretAccount(saved.Id));
            }

            hasSecret = secret.Length > 0;
        }

        SyncStore.SaveConnection(saved, protectedSecret, hasSecret);
        ChangedNow();
        CloudSyncService.Kick();
        return Get(saved.Id);
    }

    /// <summary>
    /// Adds a connection of an export (<see cref="ExportService"/>) with its id, the same checks as <see cref="Save"/> but without a
    /// secret – like a connection that came from the cloud, it waits for the user to enter it here. A connection bound to a computer
    /// keeps its computer.
    /// </summary>
    internal static Connection Import(Connection connection)
    {
        AccessPolicy.EnsureConfigurationEditable();
        var type = ConnectionCatalog.Get(connection.Kind);
        if (!type.IsAvailable)
        {
            throw new BusinessException("Dieser Verbindungstyp ist auf diesem Computer nicht verfügbar.", "Kind");
        }

        AccessPolicy.EnsureConnectionKindAllowed(connection.Kind);
        if (Find(connection.Id) is not null)
        {
            throw new BusinessException("Diese Verbindung gibt es schon.");
        }

        var name = connection.Name.Trim();
        if (name.Length is 0 or > 100)
        {
            throw new BusinessException("Der Name fehlt oder ist zu lang (höchstens 100 Zeichen).", nameof(Connection.Name));
        }

        if (List().Any(other => string.Equals(other.Name, name, StringComparison.CurrentCultureIgnoreCase)))
        {
            throw new BusinessException("Eine Verbindung mit diesem Namen gibt es schon.", nameof(Connection.Name));
        }

        var settings = CleanSettings(type, connection.Settings);
        Validate(type, settings);
        var now = AppClock.UtcNow;
        var imported = connection with
        {
            Name = name,
            Settings = settings,
            DeviceName = type.IsBoundToDevice ? (connection.DeviceName.Trim().Length > 0 ? connection.DeviceName.Trim() : AppInfo.MachineName) : "",
            HasSecret = false,
            CreateDate = now,
            UpdateDate = now,
            SyncState = SyncState.Pending,
        };

        SyncStore.SaveConnection(imported, null, false);
        ChangedNow();
        CloudSyncService.Kick();
        return Get(imported.Id);
    }

    /// <summary>Deletes a connection (everywhere). Not while a job uses it.</summary>
    public static void Delete(Guid id)
    {
        AccessPolicy.EnsureConfigurationEditable();
        var connection = Get(id);
        var users = JobService.List().Where(job => job.SourceConnectionId == id || job.TargetConnectionId == id).Select(job => job.Name).ToList();
        if (users.Count > 0)
        {
            throw new BusinessException($"Die Verbindung wird noch von {string.Join(", ", users.Select(name => $"„{name}“"))} verwendet.");
        }

        TokenStore.Delete(SecretAccount(connection.Id));
        SyncStore.DeleteConnection(id, AppClock.UtcNow, CloudSyncService.IsConnected);
        ChangedNow();
        CloudSyncService.Kick();
    }

    /// <summary>Opens a storage of a connection at a folder (not connected yet). Checks that this computer may use it.</summary>
    public static IStorage Open(Connection connection, string folder)
    {
        if (!IsUsableHere(connection, out var reason))
        {
            throw new BusinessException($"„{connection.Name}“ ist hier nicht nutzbar: {reason}");
        }

        var provider = StorageProviders.Find(connection.Kind)!;
        return provider.Open(ContextFor(connection, folder));
    }

    /// <summary>Connects to a connection (with its root) and says whether it works (the button "Verbindung testen").</summary>
    public static async Task<ConnectionTestResult> TestAsync(Connection connection, string? secret, CancellationToken cancellationToken)
    {
        var started = AppClock.UtcNow;
        try
        {
            var provider = StorageProviders.Find(connection.Kind)
                ?? throw new BusinessException("Dieser Verbindungstyp ist in dieser Version noch nicht verfügbar.");
            var context = ContextFor(connection, "") with { Secret = secret ?? ReadSecret(connection.Id) };
            await using var storage = provider.Open(context);
            await storage.ConnectAsync(createRoot: false, cancellationToken);
            var count = 0;
            await foreach (var _ in storage.ListAsync("", null, cancellationToken))
            {
                if (++count >= 50)
                {
                    break;
                }
            }

            var time = AppClock.UtcNow - started;
            return new ConnectionTestResult(true, $"Verbunden mit {storage.DisplayName} ({time.TotalMilliseconds.ToString("0", CultureInfo.CurrentCulture)} ms).");
        }
        catch (StorageException exception)
        {
            return new ConnectionTestResult(false, exception.Message);
        }
        catch (BusinessException exception)
        {
            return new ConnectionTestResult(false, exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or HttpRequestException or TimeoutException or NotSupportedException)
        {
            return new ConnectionTestResult(false, exception.Message);
        }
    }

    /// <summary>The folders below a folder of a connection (the folder picker of the job form).</summary>
    public static async Task<IReadOnlyList<string>> ListFoldersAsync(Connection connection, string folder, CancellationToken cancellationToken)
    {
        if (!IsUsableHere(connection, out var reason))
        {
            throw new BusinessException(reason);
        }

        var provider = StorageProviders.Find(connection.Kind)!;
        var context = ContextFor(connection, folder);
        if (provider is IFolderBrowser browser)
        {
            return await browser.ListFoldersAsync(context, folder, cancellationToken);
        }

        await using var storage = provider.Open(context);
        await storage.ConnectAsync(createRoot: false, cancellationToken);
        var folders = new List<string>();
        await foreach (var entry in storage.ListAsync("", null, cancellationToken))
        {
            if (entry.IsFolder && !entry.Path.Contains('/'))
            {
                folders.Add(entry.Path);
            }
        }

        return [.. folders.Order(StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>A copy of the cloud is applied here (never the secret; a kept secret stays).</summary>
    internal static void ApplyRemote(Connection connection)
    {
        SyncStore.SaveConnection(connection with { SyncState = SyncState.Synced }, null, null);
        ChangedNow();
    }

    internal static void ApplyRemoteDelete(Guid id)
    {
        TokenStore.Delete(SecretAccount(id));
        SyncStore.DeleteConnection(id, AppClock.UtcNow, keepTombstone: false);
        ChangedNow();
    }

    /// <summary>The secret of a connection (null: none kept). Only the business layer and the providers see it.</summary>
    internal static string? ReadSecret(Guid id)
    {
        var connection = SyncStore.FindConnection(id, out var isDeleted);
        if (connection is null || isDeleted || !connection.HasSecret)
        {
            return null;
        }

        return TokenStore.Read(SecretAccount(id), SyncStore.ReadProtectedSecret(id));
    }

    /// <summary>Keeps a new secret (a provider renewed a token, OAuth sign-in finished).</summary>
    internal static void SaveSecret(Guid id, string secret)
    {
        var protectedValue = TokenStore.Save(SecretAccount(id), secret);
        SyncStore.SaveSecret(id, protectedValue, secret.Length > 0);
        ChangedNow();
    }

    /// <summary>Keeps a field a provider learned (host key, certificate, signed-in account) without making the connection newer.</summary>
    internal static void SaveLearnedSetting(Guid id, string key, string value)
    {
        var connection = Find(id);
        if (connection is null || connection.Get(key) == value)
        {
            return;
        }

        SyncStore.SaveConnectionSettings(id, connection.With(key, value).Settings);
        ChangedNow();
    }

    internal static void ResetForTests()
    {
        lock (Gate)
        {
            _cache = null;
        }
    }

    internal static StorageContext ContextFor(Connection connection, string folder) => new()
    {
        Connection = connection,
        Folder = folder,
        Secret = ReadSecret(connection.Id),
        DeviceName = AppInfo.MachineName,
        SaveSecretAsync = (secret, _) =>
        {
            SaveSecret(connection.Id, secret);
            return Task.CompletedTask;
        },
        SaveSettingAsync = (key, value, _) =>
        {
            SaveLearnedSetting(connection.Id, key, value);
            return Task.CompletedTask;
        },
    };

    private static string SecretAccount(Guid id) => $"connection:{id:D}";

    private static Dictionary<string, string> CleanSettings(ConnectionType type, IReadOnlyDictionary<string, string> settings)
    {
        var known = type.Fields.Select(field => field.Key).ToHashSet(StringComparer.Ordinal);
        var clean = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in settings)
        {
            if (known.Contains(key))
            {
                clean[key] = (value ?? "").Trim();
            }
        }

        return clean;
    }

    private static void Validate(ConnectionType type, Dictionary<string, string> settings)
    {
        foreach (var field in type.Fields)
        {
            if (!field.IsVisible(settings, type.Fields))
            {
                continue;
            }

            var value = settings.GetValueOrDefault(field.Key, "");
            if (field.Required && value.Length == 0 && field.Default.Length == 0 && field.Type != FieldType.ReadOnly)
            {
                throw new BusinessException($"Bitte „{field.Label}“ angeben.", field.Key);
            }

            if (value.Length == 0)
            {
                continue;
            }

            switch (field.Type)
            {
                case FieldType.Number when !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) || number is < 1 or > 65535:
                    throw new BusinessException($"„{field.Label}“ muss eine Zahl zwischen 1 und 65535 sein.", field.Key);
                case FieldType.Url when !Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp):
                    throw new BusinessException($"„{field.Label}“ muss eine Adresse mit https:// sein.", field.Key);
                case FieldType.Choice when field.Choices.Count > 0 && !field.Choices.Any(choice => choice.Value == value):
                    throw new BusinessException($"Diese Auswahl gibt es für „{field.Label}“ nicht.", field.Key);
                case FieldType.Toggle when value is not ("true" or "false"):
                    throw new BusinessException($"„{field.Label}“ ist ein Schalter (true/false).", field.Key);
            }

            if (field.Key == ConnectionFields.Host && (value.Contains('/') || value.Contains('\\') || value.Contains(' ')))
            {
                throw new BusinessException("Bitte nur den Namen oder die Adresse des Servers eingeben (ohne Pfad und Protokoll).", field.Key);
            }
        }
    }

    private static void ChangedNow()
    {
        lock (Gate)
        {
            _cache = null;
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }
}

/// <summary>The sort orders of the connection list.</summary>
public enum ConnectionSort
{
    Name = 0,
    Type = 1,
    Changed = 2,
}

/// <summary>The answer of "Verbindung testen".</summary>
public sealed record ConnectionTestResult(bool Success, string Message);
