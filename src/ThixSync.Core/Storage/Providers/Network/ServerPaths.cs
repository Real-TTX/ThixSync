namespace ThixSync.Core.Storage.Providers.Network;

/// <summary>The paths of the server providers: the root (base folder of the connection + folder of the pair) and paths below it.</summary>
internal static class ServerPaths
{
    /// <summary>The root below the server's top as a relative storage path: base folder + folder ("/backup" + "Fotos" → "backup/Fotos").</summary>
    public static string RootOf(StorageContext context) =>
        StoragePath.Combine(context.Connection.Get(ConnectionFields.BasePath), context.Folder);

    /// <summary>"/root/path" for a server that takes absolute '/' paths (SFTP, FTP); <paramref name="absoluteRoot"/> starts with '/'.</summary>
    public static string Join(string absoluteRoot, string path)
    {
        var normalized = StoragePath.Normalize(path);
        if (normalized.Length == 0)
        {
            return absoluteRoot;
        }

        return absoluteRoot.EndsWith('/') ? absoluteRoot + normalized : absoluteRoot + "/" + normalized;
    }

    /// <summary>The relative path below a folder ("a/b" + "c" → "a/b/c").</summary>
    public static string Child(string folder, string name) => folder.Length == 0 ? name : folder + "/" + name;

    /// <summary>True for names the providers never show (".", "..", the temporary files of a write in progress).</summary>
    public static bool IsHiddenName(string name) =>
        name is "." or ".." || name.StartsWith(Local.LocalStorage.TempPrefix, StringComparison.Ordinal);

    /// <summary>A temporary name next to <paramref name="path"/> (same folder: the rename stays on one volume).</summary>
    public static string TempPathFor(string path) =>
        Child(StoragePath.ParentOf(path), $"{Local.LocalStorage.TempPrefix}{Guid.NewGuid():N}.tmp");

    /// <summary>The port of the connection, or <paramref name="fallback"/> when none (or nonsense) is set.</summary>
    public static int PortOf(Models.Connection connection, int fallback) =>
        int.TryParse(connection.Get(ConnectionFields.Port), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var port) && port is > 0 and < 65536
            ? port
            : fallback;
}
