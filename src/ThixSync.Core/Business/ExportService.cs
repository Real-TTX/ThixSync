using System.Text.Json;
using System.Text.Json.Serialization;
using ThixSync.Core.Infrastructure;
using ThixSync.Core.Models;
using ThixSync.Core.Storage;

namespace ThixSync.Core.Business;

/// <summary>The content of an export file: connections and jobs, never a secret.</summary>
public sealed record ConfigurationExport
{
    public const string FormatName = "thixsync-configuration";
    public const int CurrentVersion = 1;

    public string Format { get; init; } = "";

    public int Version { get; init; } = CurrentVersion;

    public DateTimeOffset ExportedAt { get; init; }

    /// <summary>The ThixSync that wrote the file.</summary>
    public string AppVersion { get; init; } = "";

    /// <summary>The computer that wrote the file.</summary>
    public string DeviceName { get; init; } = "";

    public IReadOnlyList<Connection> Connections { get; init; } = [];

    public IReadOnlyList<SyncJob> Jobs { get; init; } = [];
}

/// <summary>What an entry of an export is.</summary>
public enum ImportItemKind
{
    Connection = 0,
    Job = 1,
}

/// <summary>What the import did with an entry.</summary>
public enum ImportOutcome
{
    Imported = 0,

    /// <summary>It is here already (same id, or the same computer / drive, or a connection of the same name and type).</summary>
    Skipped = 1,

    /// <summary>A check refused it (name taken, folder not valid here, policy); <see cref="ImportItem.Message"/> says why.</summary>
    Failed = 2,
}

/// <summary>One entry of the import report.</summary>
public sealed record ImportItem(ImportItemKind Kind, Guid Id, string Name, ImportOutcome Outcome, string Message);

/// <summary>The report of an import.</summary>
public sealed record ImportResult(IReadOnlyList<ImportItem> Items)
{
    public int Imported => Items.Count(item => item.Outcome == ImportOutcome.Imported);

    public int Skipped => Items.Count(item => item.Outcome == ImportOutcome.Skipped);

    public int Failed => Items.Count(item => item.Outcome == ImportOutcome.Failed);
}

/// <summary>
/// Export and import of the configuration (the CLI's <c>export</c> / <c>import</c>; a menu of the app may offer the same): every
/// connection and job as JSON, <b>without secrets</b> – passwords, keys and sign-ins stay on the computer they were entered on.
/// <para>
/// Import keeps the ids, so importing the same file twice changes nothing, and the jobs keep pointing at their connections:
/// <list type="bullet">
/// <item>An entry whose id exists here is skipped (the one here wins; nothing is overwritten).</item>
/// <item>A connection of this computer (or of a drive, by its volume id) that exists here under another id, and a connection of the
/// same name and type, are taken as the same: the jobs of the file use the one here.</item>
/// <item>Anything else goes through the checks of <see cref="ConnectionService"/> and <see cref="JobService"/>; what they refuse
/// (a name that is taken, a folder that is no full path on this system, a policy) is reported and left out.</item>
/// <item>Imported connections that need a secret show "Kennwort fehlt" until the user enters it (like a connection from the cloud).</item>
/// </list>
/// </para>
/// </summary>
public static class ExportService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Every connection and job of this computer as the JSON of an export file.</summary>
    public static string Export()
    {
        var export = new ConfigurationExport
        {
            Format = ConfigurationExport.FormatName,
            ExportedAt = AppClock.UtcNow,
            AppVersion = AppInfo.Version,
            DeviceName = AppInfo.MachineName,
            Connections = [.. ConnectionService.List().Select(connection => connection with { HasSecret = false, SyncState = SyncState.Synced })],
            Jobs = [.. JobService.List().Select(job => job with { SyncState = SyncState.Synced })],
        };
        return JsonSerializer.Serialize(export, Json);
    }

    /// <summary>Reads an export file's JSON. Throws <see cref="BusinessException"/> when it is none.</summary>
    public static ConfigurationExport Read(string json)
    {
        ConfigurationExport? export;
        try
        {
            export = JsonSerializer.Deserialize<ConfigurationExport>(json, Json);
        }
        catch (JsonException)
        {
            throw new BusinessException("Die Datei ist kein Export von ThixSync (kein gültiges JSON).");
        }

        if (export is null || export.Format != ConfigurationExport.FormatName)
        {
            throw new BusinessException("Die Datei ist kein Export von ThixSync.");
        }

        if (export.Version > ConfigurationExport.CurrentVersion)
        {
            throw new BusinessException("Die Datei stammt von einer neueren ThixSync-Version – bitte ThixSync aktualisieren.");
        }

        return export;
    }

    /// <summary>Imports the connections and jobs of an export file (rules: see the class). Returns what happened to each entry.</summary>
    public static ImportResult Import(string json)
    {
        AccessPolicy.EnsureConfigurationEditable();
        var export = Read(json);
        var items = new List<ImportItem>();
        var connectionIds = new Dictionary<Guid, Guid>();
        foreach (var connection in export.Connections)
        {
            items.Add(ImportConnection(connection, connectionIds));
        }

        foreach (var job in export.Jobs)
        {
            items.Add(ImportJob(job, connectionIds));
        }

        return new ImportResult(items);
    }

    private static ImportItem ImportConnection(Connection connection, Dictionary<Guid, Guid> connectionIds)
    {
        ImportItem Item(ImportOutcome outcome, string message) => new(ImportItemKind.Connection, connection.Id, connection.Name, outcome, message);

        if (!Enum.IsDefined(connection.Kind))
        {
            return Item(ImportOutcome.Failed, "Diesen Verbindungstyp kennt diese ThixSync-Version nicht.");
        }

        if (ConnectionService.Find(connection.Id) is not null)
        {
            connectionIds[connection.Id] = connection.Id;
            return Item(ImportOutcome.Skipped, "Gibt es hier schon.");
        }

        var same = SameConnectionHere(connection);
        if (same is not null)
        {
            connectionIds[connection.Id] = same.Id;
            return Item(ImportOutcome.Skipped, $"Gibt es hier schon als „{same.Name}“.");
        }

        try
        {
            var imported = ConnectionService.Import(connection);
            connectionIds[connection.Id] = imported.Id;
            return Item(ImportOutcome.Imported, ConnectionService.NeedsSecret(imported)
                ? $"{ConnectionCatalog.Get(imported.Kind).SecretLabel ?? "Anmeldung"} fehlt noch – bitte in ThixSync eingeben."
                : "");
        }
        catch (BusinessException exception)
        {
            return Item(ImportOutcome.Failed, exception.Message);
        }
    }

    private static ImportItem ImportJob(SyncJob job, Dictionary<Guid, Guid> connectionIds)
    {
        ImportItem Item(ImportOutcome outcome, string message) => new(ImportItemKind.Job, job.Id, job.Name, outcome, message);

        if (JobService.Find(job.Id) is not null)
        {
            return Item(ImportOutcome.Skipped, "Gibt es hier schon.");
        }

        try
        {
            JobService.Save(job with
            {
                SourceConnectionId = connectionIds.GetValueOrDefault(job.SourceConnectionId, job.SourceConnectionId),
                TargetConnectionId = connectionIds.GetValueOrDefault(job.TargetConnectionId, job.TargetConnectionId),
                CreateDate = default,
                SyncState = SyncState.Pending,
            });
            return Item(ImportOutcome.Imported, "");
        }
        catch (BusinessException exception)
        {
            return Item(ImportOutcome.Failed, exception.Message);
        }
    }

    /// <summary>The connection here that is the same place: this computer / the same drive of a computer, or the same name and type.</summary>
    private static Connection? SameConnectionHere(Connection connection)
    {
        var type = ConnectionCatalog.Get(connection.Kind);
        return ConnectionService.List().FirstOrDefault(existing =>
            existing.Kind == connection.Kind
            && (type.IsBoundToDevice
                ? string.Equals(existing.DeviceName, connection.DeviceName, StringComparison.OrdinalIgnoreCase)
                  && existing.Get(ConnectionFields.VolumeId) == connection.Get(ConnectionFields.VolumeId)
                : string.Equals(existing.Name.Trim(), connection.Name.Trim(), StringComparison.CurrentCultureIgnoreCase)));
    }
}
