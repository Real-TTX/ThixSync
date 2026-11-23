using System.Globalization;
using ThixSync.Core.Storage;
using ThixSync.Core.Storage.Providers.Local;

namespace ThixSync.Core.Business.Engine;

/// <summary>
/// Old versions of overwritten and deleted files, kept on the side they were taken from in
/// <c>.thixsync/versions/&lt;yyyyMMdd-HHmmss of the run start&gt;/&lt;path&gt;</c> (moved there when the storage can, otherwise
/// copied), and pruned after a run by age and by number per file.
/// </summary>
internal sealed class Versions(DateTimeOffset runStart)
{
    /// <summary>The folder of the versions below the root of a side.</summary>
    public const string Root = ".thixsync/versions";

    private const string StampFormat = "yyyyMMdd-HHmmss";

    /// <summary>The folder name of this run's versions (local time of the run start).</summary>
    public string Stamp { get; } = runStart.ToLocalTime().ToString(StampFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// Keeps the file (or folder) <paramref name="entry"/> as a version. A move takes it away from its place; a copy leaves it
    /// (and deletes it afterwards when <paramref name="forDelete"/>). <paramref name="preferCopy"/> copies even when the storage could
    /// move (the place stays filled until the new file replaces it). Returns the version path and whether the file left its place.
    /// </summary>
    public async Task<(string Path, bool Moved)> KeepAsync(RunContext run, IStorage storage, StorageEntry entry, bool forDelete, bool preferCopy,
        AdaptiveLimiter lane)
    {
        var versionPath = await FreePathAsync(run, storage, entry.Path, lane);
        if (storage.Capabilities.Has(StorageFeatures.Move) && (!preferCopy || entry.IsFolder))
        {
            await run.Retry.RunAsync(lane, token => storage.MoveAsync(entry.Path, versionPath, token), run.Token);
            return (versionPath, true);
        }

        if (entry.IsFolder)
        {
            throw new StorageException(StorageErrorKind.Unknown, "Dieser Speicher kann einen Ordner nicht als Ganzes in die Versionen verschieben.");
        }

        if (storage is LocalStorage local)
        {
            await run.Retry.RunAsync(lane, token => local.CopyFromLocalAsync(local.GetLocalPath(entry.Path), versionPath, entry.Modified, token), run.Token);
        }
        else
        {
            await run.Retry.RunAsync(lane, async token =>
            {
                await using var input = await storage.OpenReadAsync(entry.Path, token);
                return await storage.WriteAsync(versionPath, input, entry.Size, entry.Modified, null, token);
            }, run.Token);
        }

        if (forDelete)
        {
            await run.Retry.RunAsync(lane, token => storage.DeleteAsync(entry.Path, false, token), run.Token);
        }

        return (versionPath, forDelete);
    }

    /// <summary>
    /// Removes versions older than <paramref name="keepDays"/> and beyond the newest <paramref name="keepCount"/> of each file (0: no
    /// limit). Folders of a run whose versions are all gone go too. Returns how many versions were removed.
    /// </summary>
    public static async Task<int> PruneAsync(IStorage storage, int keepDays, int keepCount, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (keepDays <= 0 && keepCount <= 0)
        {
            return 0;
        }

        var versions = new List<(string Path, string Stamp, DateTime Time, string File)>();
        var filesPerStamp = new Dictionary<string, int>(StringComparer.Ordinal);
        await foreach (var entry in storage.ListAsync(Root, null, cancellationToken))
        {
            if (entry.IsFolder || !StoragePath.IsInside(entry.Path, Root, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relative = StoragePath.RelativeTo(entry.Path, Root);
            var slash = relative.IndexOf('/');
            if (slash <= 0
                || !DateTime.TryParseExact(relative[..slash], StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var time))
            {
                continue;
            }

            var stamp = relative[..slash];
            versions.Add((entry.Path, stamp, time, relative[(slash + 1)..]));
            filesPerStamp[stamp] = filesPerStamp.GetValueOrDefault(stamp) + 1;
        }

        var oldest = keepDays > 0 ? now.LocalDateTime.AddDays(-keepDays) : DateTime.MinValue;
        var removedPerStamp = new Dictionary<string, int>(StringComparer.Ordinal);
        var removed = 0;
        foreach (var group in versions.GroupBy(version => version.File, StringComparer.OrdinalIgnoreCase))
        {
            var index = 0;
            foreach (var version in group.OrderByDescending(version => version.Time))
            {
                var tooMany = keepCount > 0 && index >= keepCount;
                index++;
                if (!tooMany && version.Time >= oldest)
                {
                    continue;
                }

                await storage.DeleteAsync(version.Path, false, cancellationToken);
                removedPerStamp[version.Stamp] = removedPerStamp.GetValueOrDefault(version.Stamp) + 1;
                removed++;
            }
        }

        foreach (var (stamp, count) in removedPerStamp)
        {
            if (count == filesPerStamp[stamp])
            {
                await storage.DeleteAsync($"{Root}/{stamp}", true, cancellationToken);
            }
        }

        return removed;
    }

    /// <summary>The version path of a file in this run (another number when the run kept that file already).</summary>
    private async Task<string> FreePathAsync(RunContext run, IStorage storage, string path, AdaptiveLimiter lane)
    {
        var candidate = $"{Root}/{Stamp}/{path}";
        for (var number = 2; ; number++)
        {
            var existing = await run.Retry.RunAsync(lane, token => storage.GetAsync(candidate, token), run.Token);
            if (existing is null)
            {
                return candidate;
            }

            var name = StoragePath.NameOf(path);
            var dot = name.LastIndexOf('.');
            var numbered = dot > 0 ? $"{name[..dot]} ({number}){name[dot..]}" : $"{name} ({number})";
            var parent = StoragePath.ParentOf(path);
            candidate = $"{Root}/{Stamp}/{(parent.Length == 0 ? numbered : $"{parent}/{numbered}")}";
        }
    }
}
