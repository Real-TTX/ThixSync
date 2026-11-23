using System.Text.Json;
using MatCloud.Contract;
using ThixSync.Core.Infrastructure;
using ThixSync.Core.Models;

namespace ThixSync.Core.Business;

/// <summary>
/// ThixSync's items in the general sync of MatCloud (app "sync"): collection "connections" (key = the GUID, data = JSON of name, type,
/// fields and computer – never the secret) and collection "jobs" (key = the GUID, data = JSON of the whole job definition). Deleting
/// either is a tombstone. The last writer wins by UpdatedAt (MatCloud's rule). Runs, protocol and the state of the jobs stay on each
/// computer.
/// </summary>
internal static class CloudSyncItems
{
    public const string Connections = "connections";
    public const string Jobs = "jobs";
    public const string ConnectionRow = "connection";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The table of a pending row.</summary>
    public static string TableOf(PendingRow row) => row.Kind == ConnectionRow ? "Connection" : "Job";

    /// <summary>The item for a pending row (null when the row is gone meanwhile or too large for the cloud).</summary>
    public static SyncItemDto? ToItem(PendingRow row)
    {
        var collection = row.Kind == ConnectionRow ? Connections : Jobs;
        if (row.IsDeleted)
        {
            return new SyncItemDto { Collection = collection, Key = row.Id.ToString("D"), UpdatedAt = row.ClientUpdateDate, IsDeleted = true };
        }

        var data = row.Kind == ConnectionRow ? ConnectionData(row.Id) : JobData(row.Id);
        if (data is null || data.Length > SyncContract.MaxDataLength)
        {
            return null;
        }

        return new SyncItemDto { Collection = collection, Key = row.Id.ToString("D"), Data = data, UpdatedAt = row.ClientUpdateDate };
    }

    /// <summary>Applies an item of the cloud here (when it is newer than what this computer has).</summary>
    public static void Apply(SyncItemDto item)
    {
        if (!Guid.TryParse(item.Key, out var id))
        {
            return;
        }

        try
        {
            switch (item.Collection)
            {
                case Connections:
                    ApplyConnection(id, item);
                    break;
                case Jobs:
                    ApplyJob(id, item);
                    break;
            }
        }
        catch (Exception exception) when (exception is JsonException or FormatException or NotSupportedException)
        {
            Log.Warning($"Sync: item {item.Collection}/{item.Key} could not be read ({exception.GetType().Name})");
        }
    }

    private static void ApplyConnection(Guid id, SyncItemDto item)
    {
        var local = SyncStore.ClientUpdateDate("Connection", id);
        if (local is { } known && known >= item.UpdatedAt)
        {
            return;
        }

        if (item.IsDeleted || item.Data is null)
        {
            if (local is not null)
            {
                ConnectionService.ApplyRemoteDelete(id);
            }

            return;
        }

        var data = JsonSerializer.Deserialize<ConnectionDto>(item.Data, Json) ?? throw new JsonException("empty");
        if (!Enum.IsDefined(data.Kind))
        {
            return; // a type of a newer ThixSync: it stays in the cloud until this computer knows it
        }

        var existing = SyncStore.FindConnection(id, out _);
        ConnectionService.ApplyRemote(new Connection
        {
            Id = id,
            Name = data.Name,
            Kind = data.Kind,
            Settings = new Dictionary<string, string>(data.Settings ?? [], StringComparer.Ordinal),
            DeviceName = data.DeviceName ?? "",
            HasSecret = existing?.HasSecret ?? false,
            CreateDate = existing?.CreateDate ?? data.CreateDate ?? item.UpdatedAt,
            UpdateDate = item.UpdatedAt,
        });
    }

    private static void ApplyJob(Guid id, SyncItemDto item)
    {
        var local = SyncStore.ClientUpdateDate("Job", id);
        if (local is { } known && known >= item.UpdatedAt)
        {
            return;
        }

        if (item.IsDeleted || item.Data is null)
        {
            if (local is not null)
            {
                JobService.ApplyRemoteDelete(id);
            }

            return;
        }

        var data = JsonSerializer.Deserialize<JobDto>(item.Data, Json) ?? throw new JsonException("empty");
        var existing = SyncStore.FindJob(id, out _);
        JobService.ApplyRemote(new SyncJob
        {
            Id = id,
            Name = data.Name,
            Description = data.Description ?? "",
            Mode = Enum.IsDefined(data.Mode) ? data.Mode : SyncMode.TwoWay,
            SourceConnectionId = data.SourceConnectionId,
            TargetConnectionId = data.TargetConnectionId,
            Pairs = data.Definition?.Pairs ?? [],
            Filter = data.Definition?.Filter ?? new JobFilter(),
            Options = data.Definition?.Options ?? new JobOptions(),
            Schedule = data.Definition?.Schedule ?? new JobSchedule(),
            IsPaused = data.IsPaused,
            RunsOnDevice = data.RunsOnDevice ?? "",
            CreateDate = existing?.CreateDate ?? data.CreateDate ?? item.UpdatedAt,
            UpdateDate = item.UpdatedAt,
        });
    }

    private static string? ConnectionData(Guid id)
    {
        var connection = SyncStore.FindConnection(id, out var isDeleted);
        if (connection is null || isDeleted)
        {
            return null;
        }

        return JsonSerializer.Serialize(new ConnectionDto(connection.Name, connection.Kind, new Dictionary<string, string>(connection.Settings),
            connection.DeviceName, connection.CreateDate), Json);
    }

    private static string? JobData(Guid id)
    {
        var job = SyncStore.FindJob(id, out var isDeleted);
        if (job is null || isDeleted)
        {
            return null;
        }

        return JsonSerializer.Serialize(new JobDto(job.Name, job.Description, job.Mode, job.SourceConnectionId, job.TargetConnectionId,
            JobDefinition.Of(job), job.IsPaused, job.RunsOnDevice, job.CreateDate), Json);
    }

    private sealed record ConnectionDto(string Name, ConnectionKind Kind, Dictionary<string, string>? Settings, string? DeviceName, DateTimeOffset? CreateDate);

    private sealed record JobDto(string Name, string? Description, SyncMode Mode, Guid SourceConnectionId, Guid TargetConnectionId,
        JobDefinition? Definition, bool IsPaused, string? RunsOnDevice, DateTimeOffset? CreateDate);
}
