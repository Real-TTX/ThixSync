namespace ThixSync.Core.Storage.Providers.OAuth;

/// <summary>One item of a flat listing (Drive files.list, Graph delta): its id, name and parents, and the entry without its path.</summary>
internal sealed record CloudItem(string Id, string Name, IReadOnlyList<string> ParentIds, bool IsFolder, long Size, DateTimeOffset Modified)
{
    public string? Hash { get; init; }

    /// <summary>Not a file that can be copied (a Google document, a OneNote notebook): left out and reported once.</summary>
    public bool IsSkipped { get; init; }
}

/// <summary>
/// Builds the tree below one folder from a flat listing of a whole drive (one query for everything instead of one per folder). Two items
/// with the same name in one folder (possible on Google Drive) yield the newest one, the others are reported. Items that are not files
/// (<see cref="CloudItem.IsSkipped"/>) are counted and reported once. Cycles and items outside the folder are ignored.
/// </summary>
internal static class CloudTree
{
    /// <summary>
    /// The entries below <paramref name="rootId"/>, their paths starting with <paramref name="prefix"/> (the listed folder relative to the
    /// storage's root), with <see cref="StorageEntry.NativeId"/> set.
    /// </summary>
    public static List<StorageEntry> Build(IEnumerable<CloudItem> items, string rootId, string prefix, StringComparer nameComparer, string serviceName,
        Action<string, Exception>? onProblem)
    {
        var children = new Dictionary<string, List<CloudItem>>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            foreach (var parent in item.ParentIds)
            {
                if (!children.TryGetValue(parent, out var list))
                {
                    list = [];
                    children[parent] = list;
                }

                list.Add(item);
            }
        }

        var entries = new List<StorageEntry>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { rootId };
        var pending = new Queue<(string Id, string Path)>();
        pending.Enqueue((rootId, prefix));
        var skipped = 0;
        string? firstSkipped = null;
        while (pending.Count > 0)
        {
            var (folderId, folderPath) = pending.Dequeue();
            if (!children.TryGetValue(folderId, out var list))
            {
                continue;
            }

            foreach (var group in list.GroupBy(item => item.Name, nameComparer))
            {
                var path = folderPath.Length == 0 ? group.Key : $"{folderPath}/{group.Key}";
                var candidates = group.Where(item => !item.IsSkipped).ToList();
                if (candidates.Count < group.Count())
                {
                    skipped += group.Count() - candidates.Count;
                    firstSkipped ??= path;
                }

                if (candidates.Count == 0)
                {
                    continue;
                }

                var chosen = candidates.OrderByDescending(item => item.Modified).ThenBy(item => item.Id, StringComparer.Ordinal).First();
                if (candidates.Count > 1)
                {
                    onProblem?.Invoke(path, new StorageException(StorageErrorKind.Unknown,
                        $"{serviceName}: „{path}“ gibt es {candidates.Count}-mal im selben Ordner – nur das neueste wird synchronisiert."));
                }

                if (chosen.IsFolder && !visited.Add(chosen.Id))
                {
                    continue;
                }

                entries.Add(new StorageEntry(path, chosen.IsFolder, chosen.IsFolder ? 0 : chosen.Size, chosen.Modified)
                {
                    Hash = chosen.Hash,
                    NativeId = chosen.Id,
                    IsHidden = chosen.Name.StartsWith('.'),
                });
                if (chosen.IsFolder)
                {
                    pending.Enqueue((chosen.Id, path));
                }
            }
        }

        if (skipped > 0)
        {
            onProblem?.Invoke(firstSkipped!, new StorageException(StorageErrorKind.Unknown,
                $"{serviceName}: {skipped} Online-Dokument(e) übersprungen (z. B. „{firstSkipped}“) – sie sind keine Dateien und lassen sich nicht synchronisieren."));
        }

        return entries;
    }
}
