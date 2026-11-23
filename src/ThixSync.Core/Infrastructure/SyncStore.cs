using System.Text.Json;
using Microsoft.Data.Sqlite;
using ThixSync.Core.Models;

namespace ThixSync.Core.Infrastructure;

/// <summary>
/// Connections, jobs and the protocol on disk (SQLite, sync.db). Only the business layer uses this class; the UI never sees a query.
/// The state of each job (what both sides had after the last run) lives apart, one database per job (Engine/JobStateStore).
///
/// Tables follow the relational rules of the project: PascalCase names, a 64-bit key "Id", the audit columns
/// CreateDate/CreateUserId/UpdateDate/UpdateUserId and UpdateState (0 deleted, 1 created, 2 updated). A deleted connection or
/// job stays as a tombstone (UpdateState 0, without content) until the cloud has it. "SyncId" is the key in the cloud (a GUID).
/// Dates are Unix milliseconds (UTC). The definition of a job (pairs, filter, options, schedule) is JSON in "Definition".
/// </summary>
internal static class SyncStore
{
    /// <summary>The one local user of a desktop installation (CreateUserId / UpdateUserId).</summary>
    public const long LocalUserId = 1;

    /// <summary>The lines of a run's protocol that are kept at most (the counts in Run are always complete).</summary>
    public const int MaxEntriesPerRun = 20_000;

    private const int SchemaVersion = 1;

    private static readonly Lock Gate = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static SqliteConnection? _connection;
    private static string? _openedPath;

    // ---- Connections -----------------------------------------------------------------------------------------------

    public static List<Connection> LoadConnections()
    {
        lock (Gate)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = $"SELECT {ConnectionColumns} FROM Connection WHERE UpdateState <> 0 ORDER BY Name";
            using var reader = command.ExecuteReader();
            var connections = new List<Connection>();
            while (reader.Read())
            {
                connections.Add(ReadConnection(reader));
            }

            return connections;
        }
    }

    /// <summary>A connection (also a deleted one: <paramref name="isDeleted"/>), or null.</summary>
    public static Connection? FindConnection(Guid id, out bool isDeleted)
    {
        lock (Gate)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = $"SELECT {ConnectionColumns}, UpdateState FROM Connection WHERE SyncId = $id";
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                isDeleted = false;
                return null;
            }

            isDeleted = reader.GetInt32(10) == 0;
            return ReadConnection(reader);
        }
    }

    /// <summary>Inserts or replaces a connection (by its SyncId). <paramref name="protectedSecret"/> null keeps the stored secret.</summary>
    public static void SaveConnection(Connection connection, string? protectedSecret, bool? hasSecret)
    {
        lock (Gate)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Connection (SyncId, Name, Kind, Settings, DeviceName, ProtectedSecret, HasSecret, ClientUpdateDate, SyncState,
                                        CreateDate, CreateUserId, UpdateDate, UpdateUserId, UpdateState)
                VALUES ($id, $name, $kind, $settings, $device, COALESCE($secret, ''), COALESCE($hasSecret, 0), $clientUpdate, $syncState, $created, $userId, $now, $userId, 1)
                ON CONFLICT (SyncId) DO UPDATE SET Name = excluded.Name, Kind = excluded.Kind, Settings = excluded.Settings,
                    DeviceName = excluded.DeviceName, ProtectedSecret = COALESCE($secret, Connection.ProtectedSecret),
                    HasSecret = COALESCE($hasSecret, Connection.HasSecret), ClientUpdateDate = excluded.ClientUpdateDate, SyncState = excluded.SyncState,
                    UpdateDate = excluded.UpdateDate, UpdateUserId = excluded.UpdateUserId, UpdateState = 2;
                """;
            command.Parameters.AddWithValue("$id", connection.Id.ToString("D"));
            command.Parameters.AddWithValue("$name", connection.Name);
            command.Parameters.AddWithValue("$kind", (int)connection.Kind);
            command.Parameters.AddWithValue("$settings", JsonSerializer.Serialize(connection.Settings, Json));
            command.Parameters.AddWithValue("$device", connection.DeviceName);
            command.Parameters.AddWithValue("$secret", (object?)protectedSecret ?? DBNull.Value);
            command.Parameters.AddWithValue("$hasSecret", hasSecret is null ? DBNull.Value : hasSecret.Value ? 1 : 0);
            command.Parameters.AddWithValue("$clientUpdate", ToMilliseconds(connection.UpdateDate));
            command.Parameters.AddWithValue("$syncState", (int)connection.SyncState);
            command.Parameters.AddWithValue("$created", ToMilliseconds(connection.CreateDate));
            command.Parameters.AddWithValue("$now", Now());
            command.Parameters.AddWithValue("$userId", LocalUserId);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Changes only the fields of a connection (what a provider learned), without making it newer for the cloud.</summary>
    public static void SaveConnectionSettings(Guid id, IReadOnlyDictionary<string, string> settings)
    {
        lock (Gate)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = "UPDATE Connection SET Settings = $settings, UpdateDate = $now WHERE SyncId = $id AND UpdateState <> 0";
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.Parameters.AddWithValue("$settings", JsonSerializer.Serialize(settings, Json));
            command.Parameters.AddWithValue("$now", Now());
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Replaces only the secret of a connection.</summary>
    public static void SaveSecret(Guid id, string protectedSecret, bool hasSecret)
    {
        lock (Gate)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = "UPDATE Connection SET ProtectedSecret = $secret, HasSecret = $has, UpdateDate = $now WHERE SyncId = $id";
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.Parameters.AddWithValue("$secret", protectedSecret);
            command.Parameters.AddWithValue("$has", hasSecret ? 1 : 0);
            command.Parameters.AddWithValue("$now", Now());
            command.ExecuteNonQuery();
        }
    }

    public static string ReadProtectedSecret(Guid id)
    {
        lock (Gate)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = "SELECT ProtectedSecret FROM Connection WHERE SyncId = $id";
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            return command.ExecuteScalar() as string ?? "";
        }
    }

    public static void DeleteConnection(Guid id, DateTimeOffset when, bool keepTombstone) =>
        MarkDeleted("Connection", id, when, keepTombstone, "Name = '', Settings = '{}', ProtectedSecret = '', HasSecret = 0");

    // ---- Jobs ------------------------------------------------------------------------------------------------------

    public static List<SyncJob> LoadJobs()
    {
        lock (Gate)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = $"SELECT {JobColumns} FROM Job WHERE UpdateState <> 0 ORDER BY Name";
            using var reader = command.ExecuteReader();
            var jobs = new List<SyncJob>();
            while (reader.Read())
            {
                jobs.Add(ReadJob(reader));
            }

            return jobs;
        }
    }

    public static SyncJob? FindJob(Guid id, out bool isDeleted)
    {
        lock (Gate)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = $"SELECT {JobColumns}, UpdateState FROM Job WHERE SyncId = $id";
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                isDeleted = false;
                return null;
            }

            isDeleted = reader.GetInt32(12) == 0;
            return ReadJob(reader);
        }
    }

    public static void SaveJob(SyncJob job)
    {
        lock (Gate)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Job (SyncId, Name, Description, Mode, SourceConnectionSyncId, TargetConnectionSyncId, Definition, IsPaused, RunsOnDevice,
                                 ClientUpdateDate, SyncState, CreateDate, CreateUserId, UpdateDate, UpdateUserId, UpdateState)
                VALUES ($id, $name, $description, $mode, $source, $target, $definition, $paused, $device,
                        $clientUpdate, $syncState, $created, $userId, $now, $userId, 1)
                ON CONFLICT (SyncId) DO UPDATE SET Name = excluded.Name, Description = excluded.Description, Mode = excluded.Mode,
                    SourceConnectionSyncId = excluded.SourceConnectionSyncId, TargetConnectionSyncId = excluded.TargetConnectionSyncId,
                    Definition = excluded.Definition, IsPaused = excluded.IsPaused, RunsOnDevice = excluded.RunsOnDevice,
                    ClientUpdateDate = excluded.ClientUpdateDate, SyncState = excluded.SyncState,
                    UpdateDate = excluded.UpdateDate, UpdateUserId = excluded.UpdateUserId, UpdateState = 2;
                """;
            command.Parameters.AddWithValue("$id", job.Id.ToString("D"));
            command.Parameters.AddWithValue("$name", job.Name);
            command.Parameters.AddWithValue("$description", job.Description);
            command.Parameters.AddWithValue("$mode", (int)job.Mode);
            command.Parameters.AddWithValue("$source", job.SourceConnectionId.ToString("D"));
            command.Parameters.AddWithValue("$target", job.TargetConnectionId.ToString("D"));
            command.Parameters.AddWithValue("$definition", JsonSerializer.Serialize(JobDefinition.Of(job), Json));
            command.Parameters.AddWithValue("$paused", job.IsPaused ? 1 : 0);
            command.Parameters.AddWithValue("$device", job.RunsOnDevice);
            command.Parameters.AddWithValue("$clientUpdate", ToMilliseconds(job.UpdateDate));
            command.Parameters.AddWithValue("$syncState", (int)job.SyncState);
            command.Parameters.AddWithValue("$created", ToMilliseconds(job.CreateDate));
            command.Parameters.AddWithValue("$now", Now());
            command.Parameters.AddWithValue("$userId", LocalUserId);
            command.ExecuteNonQuery();
        }
    }

    public static void DeleteJob(Guid id, DateTimeOffset when, bool keepTombstone) =>
        MarkDeleted("Job", id, when, keepTombstone, "Name = '', Description = '', Definition = '{}'");

    // ---- Runs ------------------------------------------------------------------------------------------------------

    /// <summary>Inserts or updates a run (the runner writes it when it starts and again when it ends).</summary>
    public static void SaveRun(SyncRun run)
    {
        lock (Gate)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Run (SyncId, JobSyncId, JobName, RunTrigger, Outcome, StartedAt, FinishedAt, FilesCopied, FilesDeleted, FilesMoved, FoldersCreated,
                                 BytesCopied, Conflicts, Errors, FilesScanned, Summary, DeviceName, CreateDate, CreateUserId, UpdateDate, UpdateUserId, UpdateState)
                VALUES ($id, $job, $jobName, $trigger, $outcome, $started, $finished, $copied, $deleted, $moved, $folders,
                        $bytes, $conflicts, $errors, $scanned, $summary, $device, $now, $user, $now, $user, 1)
                ON CONFLICT (SyncId) DO UPDATE SET Outcome = excluded.Outcome, FinishedAt = excluded.FinishedAt, FilesCopied = excluded.FilesCopied,
                    FilesDeleted = excluded.FilesDeleted, FilesMoved = excluded.FilesMoved, FoldersCreated = excluded.FoldersCreated,
                    BytesCopied = excluded.BytesCopied, Conflicts = excluded.Conflicts, Errors = excluded.Errors, FilesScanned = excluded.FilesScanned,
                    Summary = excluded.Summary, UpdateDate = excluded.UpdateDate, UpdateUserId = excluded.UpdateUserId, UpdateState = 2;
                """;
            command.Parameters.AddWithValue("$id", run.Id.ToString("D"));
            command.Parameters.AddWithValue("$job", run.JobId.ToString("D"));
            command.Parameters.AddWithValue("$jobName", run.JobName);
            command.Parameters.AddWithValue("$trigger", (int)run.Trigger);
            command.Parameters.AddWithValue("$outcome", (int)run.Outcome);
            command.Parameters.AddWithValue("$started", ToMilliseconds(run.StartedAt));
            command.Parameters.AddWithValue("$finished", run.FinishedAt is { } finished ? ToMilliseconds(finished) : DBNull.Value);
            command.Parameters.AddWithValue("$copied", run.FilesCopied);
            command.Parameters.AddWithValue("$deleted", run.FilesDeleted);
            command.Parameters.AddWithValue("$moved", run.FilesMoved);
            command.Parameters.AddWithValue("$folders", run.FoldersCreated);
            command.Parameters.AddWithValue("$bytes", run.BytesCopied);
            command.Parameters.AddWithValue("$conflicts", run.Conflicts);
            command.Parameters.AddWithValue("$errors", run.Errors);
            command.Parameters.AddWithValue("$scanned", run.FilesScanned);
            command.Parameters.AddWithValue("$summary", run.Summary);
            command.Parameters.AddWithValue("$device", run.DeviceName);
            command.Parameters.AddWithValue("$now", Now());
            command.Parameters.AddWithValue("$user", LocalUserId);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>The runs, newest first; <paramref name="jobId"/> limits them to one job.</summary>
    public static List<SyncRun> LoadRuns(Guid? jobId, int limit, int offset = 0)
    {
        lock (Gate)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = $"SELECT {RunColumns} FROM Run WHERE UpdateState <> 0 {(jobId is null ? "" : "AND JobSyncId = $job")} ORDER BY StartedAt DESC LIMIT $limit OFFSET $offset";
            if (jobId is { } job)
            {
                command.Parameters.AddWithValue("$job", job.ToString("D"));
            }

            command.Parameters.AddWithValue("$limit", limit);
            command.Parameters.AddWithValue("$offset", offset);
            using var reader = command.ExecuteReader();
            var runs = new List<SyncRun>();
            while (reader.Read())
            {
                runs.Add(ReadRun(reader));
            }

            return runs;
        }
    }

    public static int CountRuns(Guid? jobId)
    {
        lock (Gate)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM Run WHERE UpdateState <> 0 {(jobId is null ? "" : "AND JobSyncId = $job")}";
            if (jobId is { } job)
            {
                command.Parameters.AddWithValue("$job", job.ToString("D"));
            }

            return Convert.ToInt32(command.ExecuteScalar());
        }
    }

    public static SyncRun? FindRun(Guid id)
    {
        lock (Gate)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = $"SELECT {RunColumns} FROM Run WHERE SyncId = $id AND UpdateState <> 0";
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadRun(reader) : null;
        }
    }

    /// <summary>The newest finished run of every job (the cards: last sync, state).</summary>
    public static Dictionary<Guid, SyncRun> LatestRunPerJob()
    {
        lock (Gate)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = $"""
                SELECT {RunColumns} FROM Run r WHERE UpdateState <> 0 AND Outcome NOT IN (0, 5)
                  AND StartedAt = (SELECT MAX(StartedAt) FROM Run x WHERE x.JobSyncId = r.JobSyncId AND x.UpdateState <> 0 AND x.Outcome NOT IN (0, 5))
                """;
            using var reader = command.ExecuteReader();
            var runs = new Dictionary<Guid, SyncRun>();
            while (reader.Read())
            {
                var run = ReadRun(reader);
                runs[run.JobId] = run;
            }

            return runs;
        }
    }

    /// <summary>Appends protocol lines to a run (in one transaction; beyond <see cref="MaxEntriesPerRun"/> only errors are kept).</summary>
    public static void AddEntries(Guid runId, IReadOnlyList<RunEntry> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        lock (Gate)
        {
            using var transaction = Connection.BeginTransaction();
            using var count = Connection.CreateCommand();
            count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*) FROM RunEntry WHERE RunId = (SELECT Id FROM Run WHERE SyncId = $run)";
            count.Parameters.AddWithValue("$run", runId.ToString("D"));
            var existing = Convert.ToInt32(count.ExecuteScalar());

            using var command = Connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO RunEntry (RunId, At, Level, Action, Path, Bytes, Message, CreateDate, CreateUserId, UpdateDate, UpdateUserId)
                VALUES ((SELECT Id FROM Run WHERE SyncId = $run), $at, $level, $action, $path, $bytes, $message, $now, $user, $now, $user)
                """;
            var run = command.Parameters.Add("$run", SqliteType.Text);
            var at = command.Parameters.Add("$at", SqliteType.Integer);
            var level = command.Parameters.Add("$level", SqliteType.Integer);
            var action = command.Parameters.Add("$action", SqliteType.Integer);
            var path = command.Parameters.Add("$path", SqliteType.Text);
            var bytes = command.Parameters.Add("$bytes", SqliteType.Integer);
            var message = command.Parameters.Add("$message", SqliteType.Text);
            command.Parameters.AddWithValue("$now", Now());
            command.Parameters.AddWithValue("$user", LocalUserId);
            run.Value = runId.ToString("D");
            foreach (var entry in entries)
            {
                if (existing >= MaxEntriesPerRun && entry.Level != RunEntryLevel.Error)
                {
                    continue;
                }

                at.Value = ToMilliseconds(entry.At);
                level.Value = (int)entry.Level;
                action.Value = entry.Action is { } value ? (int)value : DBNull.Value;
                path.Value = entry.Path;
                bytes.Value = entry.Bytes;
                message.Value = entry.Message;
                command.ExecuteNonQuery();
                existing++;
            }

            transaction.Commit();
        }
    }

    public static List<RunEntry> LoadEntries(Guid runId, int limit, int offset = 0)
    {
        lock (Gate)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = """
                SELECT e.At, e.Level, e.Action, e.Path, e.Bytes, e.Message FROM RunEntry e JOIN Run r ON r.Id = e.RunId
                WHERE r.SyncId = $run ORDER BY e.Id LIMIT $limit OFFSET $offset
                """;
            command.Parameters.AddWithValue("$run", runId.ToString("D"));
            command.Parameters.AddWithValue("$limit", limit);
            command.Parameters.AddWithValue("$offset", offset);
            using var reader = command.ExecuteReader();
            var entries = new List<RunEntry>();
            while (reader.Read())
            {
                entries.Add(new RunEntry
                {
                    At = FromMilliseconds(reader.GetInt64(0)),
                    Level = (RunEntryLevel)reader.GetInt32(1),
                    Action = reader.IsDBNull(2) ? null : (PlanAction)reader.GetInt32(2),
                    Path = reader.GetString(3),
                    Bytes = reader.GetInt64(4),
                    Message = reader.GetString(5),
                });
            }

            return entries;
        }
    }

    /// <summary>Removes runs (and their protocol) that started before <paramref name="before"/>.</summary>
    public static int DeleteRunsBefore(DateTimeOffset before)
    {
        lock (Gate)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = "DELETE FROM Run WHERE StartedAt < $before AND Outcome <> 0";
            command.Parameters.AddWithValue("$before", ToMilliseconds(before));
            return command.ExecuteNonQuery();
        }
    }

    /// <summary>Runs that were still "Running" when the program ended (crash, power off) become Cancelled.</summary>
    public static void CloseInterruptedRuns(DateTimeOffset when)
    {
        lock (Gate)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = "UPDATE Run SET Outcome = 4, FinishedAt = $when, Summary = 'Abgebrochen (ThixSync wurde beendet)' WHERE Outcome = 0";
            command.Parameters.AddWithValue("$when", ToMilliseconds(when));
            command.ExecuteNonQuery();
        }
    }

    // ---- Cloud sync ------------------------------------------------------------------------------------------------

    /// <summary>What has to go to the cloud: rows that changed here (SyncState Pending), tombstones included.</summary>
    public static List<PendingRow> Pending(int limit)
    {
        lock (Gate)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = """
                SELECT 'connection', SyncId, ClientUpdateDate, UpdateState FROM Connection WHERE SyncState = 1
                UNION ALL
                SELECT 'job', SyncId, ClientUpdateDate, UpdateState FROM Job WHERE SyncState = 1
                LIMIT $limit
                """;
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            var rows = new List<PendingRow>();
            while (reader.Read())
            {
                rows.Add(new PendingRow(reader.GetString(0), Guid.Parse(reader.GetString(1)), FromMilliseconds(reader.GetInt64(2)), reader.GetInt32(3) == 0));
            }

            return rows;
        }
    }

    /// <summary>Everything goes up again (a new connection to the cloud): every row becomes Pending.</summary>
    public static void MarkAllPending()
    {
        lock (Gate)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = "UPDATE Connection SET SyncState = 1 WHERE SyncState <> 2; UPDATE Job SET SyncState = 1 WHERE SyncState <> 2;";
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Without the cloud nobody needs the tombstones.</summary>
    public static void RemoveTombstones()
    {
        lock (Gate)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = "DELETE FROM Connection WHERE UpdateState = 0; DELETE FROM Job WHERE UpdateState = 0;";
            command.ExecuteNonQuery();
        }
    }

    /// <summary>The time a row was last changed on a device (null: not there).</summary>
    public static DateTimeOffset? ClientUpdateDate(string table, Guid id)
    {
        lock (Gate)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = $"SELECT ClientUpdateDate FROM {table} WHERE SyncId = $id";
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            return command.ExecuteScalar() is long value ? FromMilliseconds(value) : null;
        }
    }

    /// <summary>The cloud has the row as it was sent: synced (unless it changed again meanwhile); a tombstone goes.</summary>
    public static void SetSynced(string table, Guid id, DateTimeOffset sentUpdate)
    {
        lock (Gate)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = $"""
                DELETE FROM {table} WHERE SyncId = $id AND UpdateState = 0 AND ClientUpdateDate <= $sent;
                UPDATE {table} SET SyncState = 0 WHERE SyncId = $id AND ClientUpdateDate <= $sent AND SyncState = 1;
                """;
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.Parameters.AddWithValue("$sent", ToMilliseconds(sentUpdate));
            command.ExecuteNonQuery();
        }
    }

    public static void SetSyncState(string table, Guid id, SyncState state)
    {
        lock (Gate)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = $"UPDATE {table} SET SyncState = $state WHERE SyncId = $id";
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.Parameters.AddWithValue("$state", (int)state);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// A number that changes when ANOTHER process (the CLI, a second app instance) committed a change to the database since the last
    /// call (SQLite's data_version). The services compare it to drop their caches.
    /// </summary>
    public static long DataVersion()
    {
        lock (Gate)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = "PRAGMA data_version";
            return Convert.ToInt64(command.ExecuteScalar());
        }
    }

    /// <summary>Closes the database (exit, tests).</summary>
    public static void Close()
    {
        lock (Gate)
        {
            _connection?.Dispose();
            _connection = null;
            _openedPath = null;
            SqliteConnection.ClearAllPools();
        }
    }

    // ---- Plumbing --------------------------------------------------------------------------------------------------

    private const string ConnectionColumns = "SyncId, Name, Kind, Settings, DeviceName, HasSecret, CreateDate, ClientUpdateDate, SyncState, ProtectedSecret";

    private const string JobColumns =
        "SyncId, Name, Description, Mode, SourceConnectionSyncId, TargetConnectionSyncId, Definition, IsPaused, RunsOnDevice, CreateDate, ClientUpdateDate, SyncState";

    private const string RunColumns =
        "SyncId, JobSyncId, JobName, RunTrigger, Outcome, StartedAt, FinishedAt, FilesCopied, FilesDeleted, FilesMoved, FoldersCreated, BytesCopied, Conflicts, Errors, FilesScanned, Summary, DeviceName";

    private static SqliteConnection Connection
    {
        get
        {
            var path = AppPaths.DatabaseFile;
            if (_connection is not null && _openedPath == path)
            {
                return _connection;
            }

            _connection?.Dispose();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 30 }.ToString());
            _connection.Open();
            _openedPath = path;
            CreateSchema(_connection);
            return _connection;
        }
    }

    private static void CreateSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            PRAGMA journal_mode = WAL;
            PRAGMA busy_timeout = 10000;
            PRAGMA foreign_keys = ON;
            CREATE TABLE IF NOT EXISTS Connection (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SyncId TEXT NOT NULL UNIQUE,
                Name TEXT NOT NULL,
                Kind INTEGER NOT NULL,
                Settings TEXT NOT NULL DEFAULT '{"{}"}',
                DeviceName TEXT NOT NULL DEFAULT '',
                ProtectedSecret TEXT NOT NULL DEFAULT '',
                HasSecret INTEGER NOT NULL DEFAULT 0,
                ClientUpdateDate INTEGER NOT NULL,
                SyncState INTEGER NOT NULL DEFAULT 0,
                CreateDate INTEGER NOT NULL,
                CreateUserId INTEGER NOT NULL,
                UpdateDate INTEGER NOT NULL,
                UpdateUserId INTEGER NOT NULL,
                UpdateState INTEGER NOT NULL DEFAULT 1
            );
            CREATE TABLE IF NOT EXISTS Job (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SyncId TEXT NOT NULL UNIQUE,
                Name TEXT NOT NULL,
                Description TEXT NOT NULL DEFAULT '',
                Mode INTEGER NOT NULL,
                SourceConnectionSyncId TEXT NOT NULL,
                TargetConnectionSyncId TEXT NOT NULL,
                Definition TEXT NOT NULL,
                IsPaused INTEGER NOT NULL DEFAULT 0,
                RunsOnDevice TEXT NOT NULL DEFAULT '',
                ClientUpdateDate INTEGER NOT NULL,
                SyncState INTEGER NOT NULL DEFAULT 0,
                CreateDate INTEGER NOT NULL,
                CreateUserId INTEGER NOT NULL,
                UpdateDate INTEGER NOT NULL,
                UpdateUserId INTEGER NOT NULL,
                UpdateState INTEGER NOT NULL DEFAULT 1
            );
            CREATE TABLE IF NOT EXISTS Run (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SyncId TEXT NOT NULL UNIQUE,
                JobSyncId TEXT NOT NULL,
                JobName TEXT NOT NULL,
                RunTrigger INTEGER NOT NULL,
                Outcome INTEGER NOT NULL,
                StartedAt INTEGER NOT NULL,
                FinishedAt INTEGER NULL,
                FilesCopied INTEGER NOT NULL DEFAULT 0,
                FilesDeleted INTEGER NOT NULL DEFAULT 0,
                FilesMoved INTEGER NOT NULL DEFAULT 0,
                FoldersCreated INTEGER NOT NULL DEFAULT 0,
                BytesCopied INTEGER NOT NULL DEFAULT 0,
                Conflicts INTEGER NOT NULL DEFAULT 0,
                Errors INTEGER NOT NULL DEFAULT 0,
                FilesScanned INTEGER NOT NULL DEFAULT 0,
                Summary TEXT NOT NULL DEFAULT '',
                DeviceName TEXT NOT NULL DEFAULT '',
                CreateDate INTEGER NOT NULL,
                CreateUserId INTEGER NOT NULL,
                UpdateDate INTEGER NOT NULL,
                UpdateUserId INTEGER NOT NULL,
                UpdateState INTEGER NOT NULL DEFAULT 1
            );
            CREATE INDEX IF NOT EXISTS IX_Run_Job ON Run (JobSyncId, StartedAt);
            CREATE INDEX IF NOT EXISTS IX_Run_Started ON Run (StartedAt);
            CREATE TABLE IF NOT EXISTS RunEntry (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                RunId INTEGER NOT NULL REFERENCES Run (Id) ON DELETE CASCADE,
                At INTEGER NOT NULL,
                Level INTEGER NOT NULL,
                Action INTEGER NULL,
                Path TEXT NOT NULL DEFAULT '',
                Bytes INTEGER NOT NULL DEFAULT 0,
                Message TEXT NOT NULL DEFAULT '',
                CreateDate INTEGER NOT NULL,
                CreateUserId INTEGER NOT NULL,
                UpdateDate INTEGER NOT NULL,
                UpdateUserId INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_RunEntry_Run ON RunEntry (RunId, Id);
            PRAGMA user_version = {SchemaVersion};
            """;
        command.ExecuteNonQuery();
    }

    private static void MarkDeleted(string table, Guid id, DateTimeOffset when, bool keepTombstone, string clear)
    {
        lock (Gate)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = keepTombstone
                ? $"UPDATE {table} SET {clear}, ClientUpdateDate = $when, SyncState = 1, UpdateDate = $now, UpdateUserId = $user, UpdateState = 0 WHERE SyncId = $id"
                : $"DELETE FROM {table} WHERE SyncId = $id";
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.Parameters.AddWithValue("$when", ToMilliseconds(when));
            command.Parameters.AddWithValue("$now", Now());
            command.Parameters.AddWithValue("$user", LocalUserId);
            command.ExecuteNonQuery();
        }
    }

    private static Connection ReadConnection(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        Name = reader.GetString(1),
        Kind = (ConnectionKind)reader.GetInt32(2),
        Settings = ReadSettings(reader.GetString(3)),
        DeviceName = reader.GetString(4),
        HasSecret = reader.GetInt32(5) != 0,
        CreateDate = FromMilliseconds(reader.GetInt64(6)),
        UpdateDate = FromMilliseconds(reader.GetInt64(7)),
        SyncState = (SyncState)reader.GetInt32(8),
    };

    private static Dictionary<string, string> ReadSettings(string json)
    {
        try
        {
            var settings = JsonSerializer.Deserialize<Dictionary<string, string>>(json, Json);
            return settings is null ? new(StringComparer.Ordinal) : new(settings, StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new(StringComparer.Ordinal);
        }
    }

    private static SyncJob ReadJob(SqliteDataReader reader)
    {
        JobDefinition definition;
        try
        {
            definition = JsonSerializer.Deserialize<JobDefinition>(reader.GetString(6), Json) ?? new JobDefinition();
        }
        catch (JsonException)
        {
            definition = new JobDefinition();
        }

        return new SyncJob
        {
            Id = Guid.Parse(reader.GetString(0)),
            Name = reader.GetString(1),
            Description = reader.GetString(2),
            Mode = (SyncMode)reader.GetInt32(3),
            SourceConnectionId = Guid.Parse(reader.GetString(4)),
            TargetConnectionId = Guid.Parse(reader.GetString(5)),
            Pairs = definition.Pairs ?? [],
            Filter = definition.Filter ?? new JobFilter(),
            Options = definition.Options ?? new JobOptions(),
            Schedule = definition.Schedule ?? new JobSchedule(),
            IsPaused = reader.GetInt32(7) != 0,
            RunsOnDevice = reader.GetString(8),
            CreateDate = FromMilliseconds(reader.GetInt64(9)),
            UpdateDate = FromMilliseconds(reader.GetInt64(10)),
            SyncState = (SyncState)reader.GetInt32(11),
        };
    }

    private static SyncRun ReadRun(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        JobId = Guid.Parse(reader.GetString(1)),
        JobName = reader.GetString(2),
        Trigger = (RunTrigger)reader.GetInt32(3),
        Outcome = (RunOutcome)reader.GetInt32(4),
        StartedAt = FromMilliseconds(reader.GetInt64(5)),
        FinishedAt = reader.IsDBNull(6) ? null : FromMilliseconds(reader.GetInt64(6)),
        FilesCopied = reader.GetInt64(7),
        FilesDeleted = reader.GetInt64(8),
        FilesMoved = reader.GetInt64(9),
        FoldersCreated = reader.GetInt64(10),
        BytesCopied = reader.GetInt64(11),
        Conflicts = reader.GetInt64(12),
        Errors = reader.GetInt64(13),
        FilesScanned = reader.GetInt64(14),
        Summary = reader.GetString(15),
        DeviceName = reader.GetString(16),
    };

    private static long Now() => AppClock.UtcNow.ToUnixTimeMilliseconds();

    private static long ToMilliseconds(DateTimeOffset value) => value.ToUnixTimeMilliseconds();

    private static DateTimeOffset FromMilliseconds(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value);
}

/// <summary>The part of a job that is stored (and synced) as JSON.</summary>
internal sealed record JobDefinition
{
    public List<FolderPair>? Pairs { get; init; }

    public JobFilter? Filter { get; init; }

    public JobOptions? Options { get; init; }

    public JobSchedule? Schedule { get; init; }

    public static JobDefinition Of(SyncJob job) => new()
    {
        Pairs = [.. job.Pairs],
        Filter = job.Filter,
        Options = job.Options,
        Schedule = job.Schedule,
    };
}

/// <summary>A row that has to go to the cloud.</summary>
internal sealed record PendingRow(string Kind, Guid Id, DateTimeOffset ClientUpdateDate, bool IsDeleted);
