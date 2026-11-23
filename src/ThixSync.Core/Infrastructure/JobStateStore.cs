using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace ThixSync.Core.Infrastructure;

/// <summary>
/// What each side had at one path after the last successful handling of that path (one row of a job's state). Each side keeps its
/// own values (as its listing or its write returned them): a storage that cannot keep modification times is compared with itself.
/// Times are UTC.
/// </summary>
internal sealed record StateRow(
    int PairIndex,
    string Path,
    bool IsFolder,
    long SourceSize,
    DateTimeOffset SourceModified,
    string? SourceHash,
    long TargetSize,
    DateTimeOffset TargetModified,
    string? TargetHash);

/// <summary>
/// What the state knows about a folder pair as a whole: the fingerprint of its folders and connections (another pair at the same
/// index starts like a first run), the change-feed cursors of both sides and when old versions were pruned last.
/// </summary>
internal sealed record PairState(int PairIndex, string Fingerprint, string? SourceCursor, string? TargetCursor, DateTimeOffset? LastPrune);

/// <summary>
/// The state of one job: one SQLite file per job (<c>state/&lt;job&gt;.db</c>), loaded into memory at the start of a run and written
/// back in batches (one transaction, prepared statements) while the run goes on, so a cancelled or crashed run keeps what it finished.
/// The state is a cache of the engine on this computer (never synced, rebuilt by a first-run comparison when lost): it keeps the key
/// <c>Id</c> of the relational rules but no audit columns, so a million rows stay small.
/// </summary>
internal sealed class JobStateStore : IDisposable
{
    private const int SchemaVersion = 1;

    private readonly Lock _gate = new();
    private readonly SqliteConnection _connection;

    private JobStateStore(SqliteConnection connection)
    {
        _connection = connection;
    }

    /// <summary>The file of a job's state.</summary>
    public static string FileOf(Guid jobId) => Path.Combine(AppPaths.StateDirectory, $"{jobId:D}.db");

    public static bool Exists(Guid jobId) => File.Exists(FileOf(jobId));

    /// <summary>
    /// Opens (and creates) the state of a job. A file that cannot be read is kept aside as <c>.broken</c> and a new, empty state is
    /// started: without a state the engine compares like a first run and never deletes.
    /// </summary>
    public static JobStateStore Open(Guid jobId)
    {
        Directory.CreateDirectory(AppPaths.StateDirectory);
        var file = FileOf(jobId);
        try
        {
            return OpenFile(file);
        }
        catch (SqliteException exception)
        {
            Log.Warning($"The state of job {jobId:D} could not be read ({exception.SqliteErrorCode}); starting over like a first run");
            SqliteConnection.ClearAllPools();
            MoveAside(file);
            return OpenFile(file);
        }
    }

    /// <summary>Deletes the state of a job (the next run compares both sides like a first run). Best effort.</summary>
    public static void Delete(Guid jobId)
    {
        var file = FileOf(jobId);
        foreach (var path in new[] { file, file + "-wal", file + "-shm" })
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Log.Warning($"The state of job {jobId:D} could not be deleted ({exception.GetType().Name})");
            }
        }
    }

    /// <summary>The fingerprint of a pair: its connections and folders (a change makes the pair start like a first run).</summary>
    public static string FingerprintOf(Guid sourceConnectionId, string sourcePath, Guid targetConnectionId, string targetPath)
    {
        var text = $"{sourceConnectionId:D}\n{sourcePath}\n{targetConnectionId:D}\n{targetPath}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>The pairs the state knows, by index.</summary>
    public Dictionary<int, PairState> LoadPairs()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT PairIndex, Fingerprint, SourceCursor, TargetCursor, LastPrune FROM PairState";
            using var reader = command.ExecuteReader();
            var pairs = new Dictionary<int, PairState>();
            while (reader.Read())
            {
                var index = reader.GetInt32(0);
                pairs[index] = new PairState(index, reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : FromTicks(reader.GetInt64(4)));
            }

            return pairs;
        }
    }

    /// <summary>Every row of a pair.</summary>
    public List<StateRow> LoadRows(int pairIndex)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT Path, IsFolder, SourceSize, SourceModified, SourceHash, TargetSize, TargetModified, TargetHash
                FROM Entry WHERE PairIndex = $pair
                """;
            command.Parameters.AddWithValue("$pair", pairIndex);
            using var reader = command.ExecuteReader();
            var rows = new List<StateRow>();
            while (reader.Read())
            {
                rows.Add(new StateRow(pairIndex, reader.GetString(0), reader.GetInt64(1) != 0,
                    reader.GetInt64(2), FromTicks(reader.GetInt64(3)), reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetInt64(5), FromTicks(reader.GetInt64(6)), reader.IsDBNull(7) ? null : reader.GetString(7)));
            }

            return rows;
        }
    }

    /// <summary>Writes changed rows and removes deleted ones in one transaction (deletes first).</summary>
    public void Save(IReadOnlyCollection<StateRow> upserts, IReadOnlyCollection<(int PairIndex, string Path)> deletes)
    {
        if (upserts.Count == 0 && deletes.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            if (deletes.Count > 0)
            {
                using var delete = _connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM Entry WHERE PairIndex = $pair AND Path = $path";
                var pair = delete.Parameters.Add("$pair", SqliteType.Integer);
                var path = delete.Parameters.Add("$path", SqliteType.Text);
                delete.Prepare();
                foreach (var (pairIndex, rowPath) in deletes)
                {
                    pair.Value = pairIndex;
                    path.Value = rowPath;
                    delete.ExecuteNonQuery();
                }
            }

            if (upserts.Count > 0)
            {
                using var upsert = _connection.CreateCommand();
                upsert.Transaction = transaction;
                upsert.CommandText = """
                    INSERT INTO Entry (PairIndex, Path, IsFolder, SourceSize, SourceModified, SourceHash, TargetSize, TargetModified, TargetHash)
                    VALUES ($pair, $path, $folder, $sourceSize, $sourceModified, $sourceHash, $targetSize, $targetModified, $targetHash)
                    ON CONFLICT (PairIndex, Path) DO UPDATE SET IsFolder = excluded.IsFolder,
                        SourceSize = excluded.SourceSize, SourceModified = excluded.SourceModified, SourceHash = excluded.SourceHash,
                        TargetSize = excluded.TargetSize, TargetModified = excluded.TargetModified, TargetHash = excluded.TargetHash
                    """;
                var pair = upsert.Parameters.Add("$pair", SqliteType.Integer);
                var path = upsert.Parameters.Add("$path", SqliteType.Text);
                var folder = upsert.Parameters.Add("$folder", SqliteType.Integer);
                var sourceSize = upsert.Parameters.Add("$sourceSize", SqliteType.Integer);
                var sourceModified = upsert.Parameters.Add("$sourceModified", SqliteType.Integer);
                var sourceHash = upsert.Parameters.Add("$sourceHash", SqliteType.Text);
                var targetSize = upsert.Parameters.Add("$targetSize", SqliteType.Integer);
                var targetModified = upsert.Parameters.Add("$targetModified", SqliteType.Integer);
                var targetHash = upsert.Parameters.Add("$targetHash", SqliteType.Text);
                upsert.Prepare();
                foreach (var row in upserts)
                {
                    pair.Value = row.PairIndex;
                    path.Value = row.Path;
                    folder.Value = row.IsFolder ? 1 : 0;
                    sourceSize.Value = row.SourceSize;
                    sourceModified.Value = row.SourceModified.UtcTicks;
                    sourceHash.Value = (object?)row.SourceHash ?? DBNull.Value;
                    targetSize.Value = row.TargetSize;
                    targetModified.Value = row.TargetModified.UtcTicks;
                    targetHash.Value = (object?)row.TargetHash ?? DBNull.Value;
                    upsert.ExecuteNonQuery();
                }
            }

            transaction.Commit();
        }
    }

    /// <summary>Starts a pair over (its folders or connections changed): its rows go, the new fingerprint is kept.</summary>
    public void ResetPair(int pairIndex, string fingerprint)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            using (var delete = _connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM Entry WHERE PairIndex = $pair";
                delete.Parameters.AddWithValue("$pair", pairIndex);
                delete.ExecuteNonQuery();
            }

            WritePair(new PairState(pairIndex, fingerprint, null, null, null), transaction);
            transaction.Commit();
        }
    }

    /// <summary>Keeps what the state knows about a pair (cursors, last pruning).</summary>
    public void SavePair(PairState pair)
    {
        lock (_gate)
        {
            WritePair(pair, null);
        }
    }

    /// <summary>Forgets the pairs from <paramref name="count"/> on (the job has fewer pairs now).</summary>
    public void RemovePairsFrom(int count)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM Entry WHERE PairIndex >= $count; DELETE FROM PairState WHERE PairIndex >= $count;";
            command.Parameters.AddWithValue("$count", count);
            command.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _connection.Dispose();
        }
    }

    private static JobStateStore OpenFile(string file)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false, DefaultTimeout = 30 }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA busy_timeout = 10000;
                PRAGMA user_version = {SchemaVersion};
                CREATE TABLE IF NOT EXISTS PairState (
                    Id INTEGER PRIMARY KEY,
                    PairIndex INTEGER NOT NULL UNIQUE,
                    Fingerprint TEXT NOT NULL,
                    SourceCursor TEXT,
                    TargetCursor TEXT,
                    LastPrune INTEGER
                );
                CREATE TABLE IF NOT EXISTS Entry (
                    Id INTEGER PRIMARY KEY,
                    PairIndex INTEGER NOT NULL,
                    Path TEXT NOT NULL,
                    IsFolder INTEGER NOT NULL,
                    SourceSize INTEGER NOT NULL,
                    SourceModified INTEGER NOT NULL,
                    SourceHash TEXT,
                    TargetSize INTEGER NOT NULL,
                    TargetModified INTEGER NOT NULL,
                    TargetHash TEXT,
                    UNIQUE (PairIndex, Path)
                );
                """;
            command.ExecuteNonQuery();
            return new JobStateStore(connection);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private void WritePair(PairState pair, SqliteTransaction? transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO PairState (PairIndex, Fingerprint, SourceCursor, TargetCursor, LastPrune) VALUES ($pair, $fingerprint, $source, $target, $prune)
            ON CONFLICT (PairIndex) DO UPDATE SET Fingerprint = excluded.Fingerprint, SourceCursor = excluded.SourceCursor,
                TargetCursor = excluded.TargetCursor, LastPrune = excluded.LastPrune
            """;
        command.Parameters.AddWithValue("$pair", pair.PairIndex);
        command.Parameters.AddWithValue("$fingerprint", pair.Fingerprint);
        command.Parameters.AddWithValue("$source", (object?)pair.SourceCursor ?? DBNull.Value);
        command.Parameters.AddWithValue("$target", (object?)pair.TargetCursor ?? DBNull.Value);
        command.Parameters.AddWithValue("$prune", pair.LastPrune is { } prune ? prune.UtcTicks : DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static void MoveAside(string file)
    {
        var broken = $"{file}.broken-{DateTime.Now:yyyyMMdd-HHmmss}";
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try
            {
                if (File.Exists(file + suffix))
                {
                    File.Move(file + suffix, broken + suffix, overwrite: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Log.Warning($"A broken job state could not be moved aside ({exception.GetType().Name})");
            }
        }
    }

    private static DateTimeOffset FromTicks(long ticks) => new(ticks, TimeSpan.Zero);
}
