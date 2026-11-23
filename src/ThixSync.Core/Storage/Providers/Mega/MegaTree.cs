using CG.Web.MegaApiClient;

namespace ThixSync.Core.Storage.Providers.Mega;

/// <summary>
/// The nodes of a MEGA account as a tree of paths. MEGA knows nodes with parent ids, not paths, and allows the same name twice
/// in a folder: a path then means a folder of that name if there is one, else the newest node of that name; the others are
/// reported and left alone. Safe for many threads (the storage updates it after its own changes).
/// </summary>
internal sealed class MegaTree
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, INode> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<INode>> _children = new(StringComparer.Ordinal);

    /// <summary>The node "Cloud Drive" (null before <see cref="Load"/>).</summary>
    public INode? Root { get; private set; }

    /// <summary>Replaces everything with a fresh list of nodes (GetNodes).</summary>
    public void Load(IEnumerable<INode> nodes)
    {
        lock (_gate)
        {
            _byId.Clear();
            _children.Clear();
            Root = null;
            foreach (var node in nodes)
            {
                AddLocked(node);
                if (node.Type == NodeType.Root)
                {
                    Root = node;
                }
            }
        }
    }

    /// <summary>Adds a node (or replaces the one with its id, e.g. after a move or rename).</summary>
    public void Add(INode node)
    {
        lock (_gate)
        {
            RemoveLocked(node.Id, withChildren: false);
            AddLocked(node);
        }
    }

    /// <summary>Removes a node and everything below it.</summary>
    public void Remove(INode node)
    {
        lock (_gate)
        {
            RemoveLocked(node.Id, withChildren: true);
        }
    }

    /// <summary>The node at a relative path below <paramref name="start"/> ("" is <paramref name="start"/> itself); null when there is none.</summary>
    public INode? Find(INode start, string path)
    {
        if (path.Length == 0)
        {
            return start;
        }

        lock (_gate)
        {
            INode? node = start;
            foreach (var part in path.Split('/'))
            {
                node = ChildLocked(node.Id, part);
                if (node is null)
                {
                    return null;
                }
            }

            return node;
        }
    }

    /// <summary>The child of a folder with a name (a folder of that name before files, else the newest node).</summary>
    public INode? Child(INode parent, string name)
    {
        lock (_gate)
        {
            return ChildLocked(parent.Id, name);
        }
    }

    /// <summary>
    /// Everything below <paramref name="start"/> with paths starting at <paramref name="startPath"/>; names that are there twice
    /// go to <paramref name="onDuplicate"/> (only the chosen node is listed).
    /// </summary>
    public List<(string Path, INode Node)> Walk(INode start, string startPath, Action<string>? onDuplicate)
    {
        var result = new List<(string, INode)>();
        lock (_gate)
        {
            var pending = new Stack<(string Path, INode Folder)>();
            pending.Push((startPath, start));
            while (pending.Count > 0)
            {
                var (folderPath, folder) = pending.Pop();
                if (!_children.TryGetValue(folder.Id, out var children))
                {
                    continue;
                }

                foreach (var group in children.Where(child => child.Type is NodeType.File or NodeType.Directory).GroupBy(child => child.Name, StringComparer.Ordinal))
                {
                    var path = folderPath.Length == 0 ? group.Key : $"{folderPath}/{group.Key}";
                    var chosen = Choose(group);
                    if (group.Count() > 1)
                    {
                        onDuplicate?.Invoke(path);
                    }

                    result.Add((path, chosen));
                    if (chosen.Type == NodeType.Directory)
                    {
                        pending.Push((path, chosen));
                    }
                }
            }
        }

        return result;
    }

    /// <summary>The time MEGA keeps for a node, in UTC (the original modification time of an upload, else when it was created).</summary>
    public static DateTimeOffset TimeOf(INode node)
    {
        var time = node.ModificationDate ?? node.CreationDate ?? DateTime.UnixEpoch;
        var utc = time.Kind == DateTimeKind.Utc ? time : time.ToUniversalTime(); // MegaApiClient gives local times
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }

    private INode? ChildLocked(string parentId, string name)
    {
        if (!_children.TryGetValue(parentId, out var children))
        {
            return null;
        }

        var named = children.Where(child => child.Type is NodeType.File or NodeType.Directory && string.Equals(child.Name, name, StringComparison.Ordinal)).ToList();
        return named.Count == 0 ? null : Choose(named);
    }

    private static INode Choose(IEnumerable<INode> nodes) =>
        nodes.OrderByDescending(node => node.Type == NodeType.Directory)
            .ThenByDescending(TimeOf)
            .ThenBy(node => node.Id, StringComparer.Ordinal)
            .First();

    private void AddLocked(INode node)
    {
        _byId[node.Id] = node;
        if (string.IsNullOrEmpty(node.ParentId))
        {
            return;
        }

        if (!_children.TryGetValue(node.ParentId, out var children))
        {
            _children[node.ParentId] = children = [];
        }

        children.Add(node);
    }

    private void RemoveLocked(string id, bool withChildren)
    {
        if (!_byId.Remove(id, out var node))
        {
            return;
        }

        if (!string.IsNullOrEmpty(node.ParentId) && _children.TryGetValue(node.ParentId, out var siblings))
        {
            siblings.RemoveAll(sibling => sibling.Id == id);
        }

        if (!withChildren || !_children.Remove(id, out var children))
        {
            return;
        }

        foreach (var child in children.ToList())
        {
            RemoveLocked(child.Id, withChildren: true);
        }
    }
}
