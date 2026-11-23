using System.Text;
using System.Text.RegularExpressions;
using ThixSync.Core.Models;
using ThixSync.Core.Storage;
using ThixSync.Core.Storage.Providers.Local;

namespace ThixSync.Core.Business.Engine;

/// <summary>
/// The filter of a job, compiled once per run. Patterns (case-insensitive):
/// <list type="bullet">
/// <item><c>*.tmp</c>, <c>Thumbs.db</c> – no '/': the name of a file or folder anywhere (a folder takes its content with it);</item>
/// <item><c>node_modules/</c> – a trailing '/': only folders (anywhere, or at that path when the pattern has another '/');</item>
/// <item><c>Fotos/**/*.jpg</c>, <c>/Archiv</c> – with '/': the path from the root of the pair; <c>**</c> spans folders, <c>*</c> and
/// <c>?</c> stay within one name.</item>
/// </list>
/// The versions folder <c>.thixsync/</c> and the temporary files of a write are always left out.
/// </summary>
internal sealed class PathFilter
{
    private const string VersionsRoot = ".thixsync";

    private readonly JobFilter _filter;
    private readonly DateTimeOffset _oldest;
    private readonly List<Pattern> _excludes;
    private readonly List<Pattern> _includes;

    public PathFilter(JobFilter filter, DateTimeOffset now)
    {
        _filter = filter;
        _oldest = filter.MaxAgeDays > 0 ? now.AddDays(-filter.MaxAgeDays) : DateTimeOffset.MinValue;
        _excludes = [.. filter.Exclude.Select(Pattern.Parse).OfType<Pattern>()];
        _includes = [.. filter.Include.Select(Pattern.Parse).OfType<Pattern>()];
    }

    public bool HasIncludes => _includes.Count > 0;

    /// <summary>The entry itself is left out (always-excluded names, attributes, size, age, exclude patterns).</summary>
    public bool ExcludesEntry(in StorageEntry entry)
    {
        var name = entry.Name;
        if (name.StartsWith(LocalStorage.TempPrefix, StringComparison.Ordinal))
        {
            return true;
        }

        if (entry.IsFolder && IsVersionsFolder(entry.Path))
        {
            return true;
        }

        if ((_filter.SkipHidden && entry.IsHidden) || (_filter.SkipSystemFiles && entry.IsSystem))
        {
            return true;
        }

        if (!entry.IsFolder && _filter.MaxFileSizeBytes > 0 && entry.Size > _filter.MaxFileSizeBytes)
        {
            return true;
        }

        if (!entry.IsFolder && _filter.MaxAgeDays > 0 && entry.Modified < _oldest)
        {
            return true;
        }

        return Matches(_excludes, entry.Path, name, entry.IsFolder);
    }

    /// <summary>
    /// A folder (known only by its path, e.g. as the parent of a file) is left out by a pattern, by being the versions folder or by
    /// a hidden name.
    /// </summary>
    public bool ExcludesFolderPath(string folder)
    {
        var name = StoragePath.NameOf(folder);
        if (IsVersionsFolder(folder) || (_filter.SkipHidden && name.StartsWith('.')))
        {
            return true;
        }

        return Matches(_excludes, folder, name, isFolder: true);
    }

    /// <summary>The entry matches an include pattern itself.</summary>
    public bool IncludesPath(string path, bool isFolder) => Matches(_includes, path, StoragePath.NameOf(path), isFolder);

    private static bool IsVersionsFolder(string path) => path.Equals(VersionsRoot, StringComparison.OrdinalIgnoreCase);

    private static bool Matches(List<Pattern> patterns, string path, string name, bool isFolder)
    {
        foreach (var pattern in patterns)
        {
            if (pattern.FoldersOnly && !isFolder)
            {
                continue;
            }

            if (pattern.IsMatch(pattern.OnPath ? path : name))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>One compiled pattern, with fast paths for the common shapes (exact name, "*.ext", "prefix*").</summary>
    private sealed class Pattern
    {
        private enum Shape
        {
            Exact,
            Suffix,
            Prefix,
            Expression,
        }

        private Shape _shape;
        private string _literal = "";
        private Regex? _regex;

        public bool FoldersOnly { get; private init; }

        /// <summary>Matched against the path from the root (otherwise against the name).</summary>
        public bool OnPath { get; private init; }

        public static Pattern? Parse(string text)
        {
            var value = text.Trim().Replace('\\', '/');
            var foldersOnly = value.EndsWith('/');
            value = value.TrimEnd('/');
            var anchored = value.StartsWith('/');
            value = value.TrimStart('/');
            if (value.Length == 0)
            {
                return null;
            }

            var pattern = new Pattern { FoldersOnly = foldersOnly, OnPath = anchored || value.Contains('/') };
            var wildcards = value.IndexOfAny(['*', '?']) >= 0;
            if (!wildcards)
            {
                pattern._shape = Shape.Exact;
                pattern._literal = value;
            }
            else if (!pattern.OnPath && value.StartsWith('*') && value.IndexOfAny(['*', '?'], 1) < 0)
            {
                pattern._shape = Shape.Suffix;
                pattern._literal = value[1..];
            }
            else if (!pattern.OnPath && value.EndsWith('*') && value.IndexOfAny(['*', '?']) == value.Length - 1)
            {
                pattern._shape = Shape.Prefix;
                pattern._literal = value[..^1];
            }
            else
            {
                pattern._shape = Shape.Expression;
                pattern._regex = new Regex(ToRegex(value), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
                    TimeSpan.FromSeconds(1));
            }

            return pattern;
        }

        public bool IsMatch(string text) => _shape switch
        {
            Shape.Exact => text.Equals(_literal, StringComparison.OrdinalIgnoreCase),
            Shape.Suffix => text.EndsWith(_literal, StringComparison.OrdinalIgnoreCase),
            Shape.Prefix => text.StartsWith(_literal, StringComparison.OrdinalIgnoreCase),
            _ => _regex!.IsMatch(text),
        };

        private static string ToRegex(string glob)
        {
            var builder = new StringBuilder("^");
            for (var index = 0; index < glob.Length; index++)
            {
                var character = glob[index];
                if (character == '*' && index + 1 < glob.Length && glob[index + 1] == '*')
                {
                    var followedBySlash = index + 2 < glob.Length && glob[index + 2] == '/';
                    builder.Append(followedBySlash ? "(?:.*/)?" : ".*");
                    index += followedBySlash ? 2 : 1;
                }
                else if (character == '*')
                {
                    builder.Append("[^/]*");
                }
                else if (character == '?')
                {
                    builder.Append("[^/]");
                }
                else
                {
                    builder.Append(Regex.Escape(character.ToString()));
                }
            }

            return builder.Append('$').ToString();
        }
    }
}
