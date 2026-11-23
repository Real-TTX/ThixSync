namespace ThixSync.Core.Storage;

/// <summary>
/// The paths of the storages: relative, '/'-separated, no leading or trailing '/', no "." or ".." parts. Every provider maps them
/// to its own form; the engine only ever sees this form.
/// </summary>
public static class StoragePath
{
    /// <summary>"\Fotos\\2024/" → "Fotos/2024". Throws for ".." (a path must never leave the root).</summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "";
        }

        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                throw new ArgumentException("Ein Pfad darf nicht aus seinem Ordner herausführen (..).", nameof(path));
            }

            kept.Add(part);
        }

        return string.Join('/', kept);
    }

    /// <summary>Joins normalized parts ("a", "b/c" → "a/b/c"); empty parts are left out.</summary>
    public static string Combine(params string[] parts) =>
        string.Join('/', parts.Select(Normalize).Where(part => part.Length > 0));

    /// <summary>The parent ("a/b/c" → "a/b", "a" → "").</summary>
    public static string ParentOf(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? "" : path[..index];
    }

    /// <summary>The last part ("a/b/c.txt" → "c.txt").</summary>
    public static string NameOf(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? path : path[(index + 1)..];
    }

    /// <summary>How deep a path is ("" → 0, "a" → 1, "a/b" → 2).</summary>
    public static int DepthOf(string path) => path.Length == 0 ? 0 : path.Count(c => c == '/') + 1;

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or lies below it.</summary>
    public static bool IsInside(string path, string folder, StringComparison comparison = StringComparison.Ordinal) =>
        folder.Length == 0 || path.Equals(folder, comparison) || (path.Length > folder.Length && path[folder.Length] == '/' && path.StartsWith(folder, comparison));

    /// <summary>The path relative to <paramref name="folder"/> ("a/b/c", "a" → "b/c").</summary>
    public static string RelativeTo(string path, string folder) =>
        folder.Length == 0 ? path : path.Length == folder.Length ? "" : path[(folder.Length + 1)..];
}
