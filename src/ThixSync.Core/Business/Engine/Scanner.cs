using System.Collections.Concurrent;
using ThixSync.Core.Models;
using ThixSync.Core.Storage;

namespace ThixSync.Core.Business.Engine;

/// <summary>
/// Lists both sides of a pair at the same time and applies the filter to both together: a path the filter leaves out on one side
/// (too large, hidden, too old) is left out on the other too, so it can never look "deleted" or "only on one side". Folders that
/// cannot be read are remembered: what is missing below them is unknown, not deleted. On a side that ignores case two names that
/// differ only in case are left out with a warning.
/// </summary>
internal static class Scanner
{
    public static async Task ScanAsync(PairContext pair, PathFilter filter, TransferMeter meter, List<RunEntry> warnings, CancellationToken cancellationToken)
    {
        var source = ListSideAsync(pair, Side.Source, meter, cancellationToken);
        var target = ListSideAsync(pair, Side.Target, meter, cancellationToken);
        var results = await Task.WhenAll(source, target);

        var problems = new List<RunEntry>();
        foreach (var result in results)
        {
            foreach (var (folder, exception) in result.Problems)
            {
                if (folder.Length == 0)
                {
                    throw new StorageException(StorageErrorKind.AccessDenied,
                        $"Der Ordner der {EngineText.SideName(result.Side)} ist nicht lesbar: {exception.Message}", exception);
                }

                result.Listing.Unreadable.Add(folder);
                problems.Add(Warning(pair, folder, $"Ordner {EngineText.InSide(result.Side)} nicht lesbar – wird übersprungen: {exception.Message}"));
            }

            foreach (var collision in result.Collisions)
            {
                pair.Collisions.Add(collision);
                problems.Add(Warning(pair, collision, result.Side == Side.Source
                    ? "In der Quelle gibt es Namen, die sich nur in Groß-/Kleinschreibung unterscheiden – übersprungen"
                    : "Im Ziel gibt es Namen, die sich nur in Groß-/Kleinschreibung unterscheiden – übersprungen"));
            }
        }

        ApplyFilter(pair, filter);
        lock (warnings)
        {
            warnings.AddRange(problems);
        }
    }

    private sealed record SideResult(Side Side, SideListing Listing, List<(string Folder, Exception Exception)> Problems, List<string> Collisions);

    private static async Task<SideResult> ListSideAsync(PairContext pair, Side side, TransferMeter meter, CancellationToken cancellationToken)
    {
        var storage = pair.Storage(side);
        var listing = pair.Listing(side);
        var problems = new ConcurrentQueue<(string, Exception)>();
        var collisions = new List<string>();
        if (storage is IChangeFeed feed)
        {
            try
            {
                listing.Cursor = await feed.GetCursorAsync(cancellationToken); // before the listing: changes during it are seen next time
            }
            catch (Exception exception) when (exception is StorageException or IOException or HttpRequestException)
            {
                listing.Cursor = null;
            }
        }

        await foreach (var raw in storage.ListAsync("", (folder, exception) => problems.Enqueue((StoragePath.Normalize(folder), exception)), cancellationToken))
        {
            var entry = raw.Path.Contains('\\') || raw.Path.StartsWith('/') || raw.Path.EndsWith('/') ? raw with { Path = StoragePath.Normalize(raw.Path) } : raw;
            if (entry.Path.Length == 0)
            {
                continue;
            }

            if (!entry.IsFolder)
            {
                meter.Scanned(side);
            }

            if (listing.Entries.TryAdd(entry.Path, entry))
            {
                continue;
            }

            var existing = listing.Entries[entry.Path];
            if (!string.Equals(existing.Path, entry.Path, StringComparison.Ordinal))
            {
                collisions.Add(existing.Path);
            }
        }

        foreach (var collision in collisions)
        {
            listing.Entries.Remove(collision);
        }

        return new SideResult(side, listing, [.. problems], collisions);
    }

    /// <summary>Removes what the filter leaves out from both sides (by path) and marks the folders that keep such things.</summary>
    private static void ApplyFilter(PairContext pair, PathFilter filter)
    {
        var comparer = pair.Comparer;
        var excludedFolders = new HashSet<string>(comparer);
        var excludedFiles = new HashSet<string>(comparer);
        foreach (var listing in new[] { pair.SourceListing, pair.TargetListing })
        {
            foreach (var entry in listing.Entries.Values)
            {
                if (filter.ExcludesEntry(entry))
                {
                    (entry.IsFolder ? excludedFolders : excludedFiles).Add(entry.Path);
                }
            }
        }

        var folderExcluded = new Dictionary<string, bool>(comparer);
        var folderIncluded = new Dictionary<string, bool>(comparer);

        bool IsFolderExcluded(string folder)
        {
            if (folder.Length == 0)
            {
                return false;
            }

            if (folderExcluded.TryGetValue(folder, out var known))
            {
                return known;
            }

            var excluded = excludedFolders.Contains(folder) || filter.ExcludesFolderPath(folder) || IsFolderExcluded(StoragePath.ParentOf(folder));
            folderExcluded[folder] = excluded;
            return excluded;
        }

        bool IsFolderIncluded(string folder)
        {
            if (folder.Length == 0)
            {
                return false;
            }

            if (folderIncluded.TryGetValue(folder, out var known))
            {
                return known;
            }

            var included = filter.IncludesPath(folder, isFolder: true) || IsFolderIncluded(StoragePath.ParentOf(folder));
            folderIncluded[folder] = included;
            return included;
        }

        bool IsLeftOut(in StorageEntry entry)
        {
            var path = entry.Path;
            if (excludedFiles.Contains(path) || excludedFolders.Contains(path))
            {
                return true;
            }

            if (entry.IsFolder ? IsFolderExcluded(path) : IsFolderExcluded(StoragePath.ParentOf(path)))
            {
                return true;
            }

            return filter.HasIncludes && !filter.IncludesPath(path, entry.IsFolder) && !IsFolderIncluded(StoragePath.ParentOf(path));
        }

        var skipped = new HashSet<string>(comparer);
        foreach (var listing in new[] { pair.SourceListing, pair.TargetListing })
        {
            var leftOut = new List<string>();
            foreach (var entry in listing.Entries.Values)
            {
                if (IsLeftOut(entry))
                {
                    leftOut.Add(entry.Path);
                    if (!entry.IsFolder)
                    {
                        skipped.Add(entry.Path);
                    }
                }
            }

            foreach (var path in leftOut)
            {
                listing.Entries.Remove(path);
                MarkTainted(listing, StoragePath.ParentOf(path));
            }

            foreach (var folder in listing.Unreadable)
            {
                MarkTainted(listing, folder);
            }

            long files = 0;
            long bytes = 0;
            foreach (var entry in listing.Entries.Values)
            {
                if (!entry.IsFolder)
                {
                    files++;
                    bytes += entry.Size;
                }
            }

            listing.Files = files;
            listing.Bytes = bytes;
        }

        pair.Skipped = skipped.Count;
    }

    /// <summary>The folder and every folder above it keep something the job does not look at.</summary>
    private static void MarkTainted(SideListing listing, string folder)
    {
        while (folder.Length > 0 && listing.Tainted.Add(folder))
        {
            folder = StoragePath.ParentOf(folder);
        }
    }

    private static RunEntry Warning(PairContext pair, string path, string message) => new()
    {
        At = Infrastructure.AppClock.UtcNow,
        Level = RunEntryLevel.Warning,
        Path = path,
        Message = pair.Index == 0 ? message : $"Ordnerpaar {pair.Index + 1}: {message}",
    };
}
