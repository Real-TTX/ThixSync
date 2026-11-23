using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ThixSync.Core.Models;

namespace ThixSync.Core.Storage.Providers.Local;

/// <summary>
/// "Dieser Computer" (the folder of a pair is an absolute path) and "Externes Laufwerk" (the drive is found by its id, wherever it
/// is mounted now; the folder of a pair lies below its root).
/// </summary>
public sealed class LocalProvider : IStorageProvider, IFolderBrowser
{
    public IReadOnlyList<ConnectionKind> Kinds { get; } = [ConnectionKind.Local, ConnectionKind.ExternalDrive];

    public IStorage Open(StorageContext context)
    {
        if (context.Connection.Kind == ConnectionKind.Local)
        {
            if (!Path.IsPathFullyQualified(context.Folder))
            {
                throw new StorageException(StorageErrorKind.InvalidName, "Der Ordner muss ein vollständiger Pfad sein.");
            }

            return new LocalStorage(context.Folder, ConnectionKind.Local);
        }

        var volume = Volumes.Find(context.Connection.Get(ConnectionFields.VolumeId), context.Connection.Get(ConnectionFields.VolumeLabel))
            ?? throw new StorageException(StorageErrorKind.Unreachable,
                $"Das Laufwerk „{context.Connection.Get(ConnectionFields.VolumeLabel)}“ ist nicht angeschlossen.");
        var relative = StoragePath.Normalize(context.Folder);
        var root = relative.Length == 0 ? volume.RootPath : Path.Combine(volume.RootPath, relative.Replace('/', Path.DirectorySeparatorChar));
        return new LocalStorage(root, ConnectionKind.ExternalDrive, $"{volume.Label}/{relative}");
    }

    public Task<IReadOnlyList<string>> ListFoldersAsync(StorageContext context, string folder, CancellationToken cancellationToken)
    {
        string root;
        if (context.Connection.Kind == ConnectionKind.Local)
        {
            if (string.IsNullOrEmpty(folder))
            {
                IReadOnlyList<string> starts = OperatingSystem.IsWindows()
                    ? [.. DriveInfo.GetDrives().Where(drive => drive.IsReady).Select(drive => drive.RootDirectory.FullName)]
                    : ["/", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)];
                return Task.FromResult(starts);
            }

            root = folder;
        }
        else
        {
            var volume = Volumes.Find(context.Connection.Get(ConnectionFields.VolumeId), context.Connection.Get(ConnectionFields.VolumeLabel))
                ?? throw new StorageException(StorageErrorKind.Unreachable, "Das Laufwerk ist nicht angeschlossen.");
            root = Path.Combine(volume.RootPath, StoragePath.Normalize(folder).Replace('/', Path.DirectorySeparatorChar));
        }

        try
        {
            IReadOnlyList<string> folders = [.. new DirectoryInfo(root)
                .EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint })
                .Select(directory => directory.Name)
                .Order(StringComparer.CurrentCultureIgnoreCase)];
            return Task.FromResult(folders);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw LocalStorage.Map(exception, folder);
        }
    }
}

/// <summary>A drive of this computer (an external drive is recognised by <see cref="Id"/>, not by its letter or mount point).</summary>
public sealed record VolumeInfo(string Id, string Label, string RootPath, string Format, long TotalBytes, long FreeBytes, bool IsRemovable);

/// <summary>The drives of this computer and their ids (Windows: the volume serial; macOS: the volume UUID).</summary>
public static class Volumes
{
    /// <summary>Tests: a fixed list instead of the real drives.</summary>
    internal static Func<IReadOnlyList<VolumeInfo>>? ListForTests { get; set; }

    /// <summary>The drives that can be the home of an "Externes Laufwerk" (removable and fixed data drives, not the system drive).</summary>
    public static IReadOnlyList<VolumeInfo> List()
    {
        if (ListForTests is { } forTests)
        {
            return forTests();
        }

        var volumes = new List<VolumeInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType is DriveType.CDRom or DriveType.Network or DriveType.Ram or DriveType.NoRootDirectory)
                {
                    continue;
                }

                var root = drive.RootDirectory.FullName;
                if (OperatingSystem.IsMacOS() && !root.StartsWith("/Volumes/", StringComparison.Ordinal))
                {
                    continue;
                }

                if (OperatingSystem.IsWindows() && string.Equals(root, Path.GetPathRoot(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var id = IdOf(root);
                if (id.Length == 0)
                {
                    continue;
                }

                var label = drive.VolumeLabel.Length > 0 ? drive.VolumeLabel : Path.GetFileName(root.TrimEnd('/', '\\'));
                volumes.Add(new VolumeInfo(id, label.Length > 0 ? label : root, root, drive.DriveFormat, drive.TotalSize, drive.AvailableFreeSpace,
                    drive.DriveType == DriveType.Removable));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        return volumes;
    }

    /// <summary>The drive with this id (or, when no id is known yet, with this label); null when it is not connected.</summary>
    public static VolumeInfo? Find(string id, string label)
    {
        var volumes = List();
        if (id.Length > 0)
        {
            return volumes.FirstOrDefault(volume => string.Equals(volume.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        return volumes.FirstOrDefault(volume => string.Equals(volume.Label, label, StringComparison.CurrentCultureIgnoreCase));
    }

    private static string IdOf(string root)
    {
        if (OperatingSystem.IsWindows())
        {
            var name = new StringBuilder(261);
            var fileSystem = new StringBuilder(261);
            return GetVolumeInformationW(root, name, name.Capacity, out var serial, out _, out _, fileSystem, fileSystem.Capacity)
                ? serial.ToString("X8", System.Globalization.CultureInfo.InvariantCulture)
                : "";
        }

        if (OperatingSystem.IsMacOS())
        {
            try
            {
                var start = new ProcessStartInfo("/usr/sbin/diskutil") { RedirectStandardOutput = true, UseShellExecute = false };
                start.ArgumentList.Add("info");
                start.ArgumentList.Add(root);
                using var process = Process.Start(start)!;
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(5000);
                foreach (var line in output.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("Volume UUID:", StringComparison.Ordinal))
                    {
                        return trimmed["Volume UUID:".Length..].Trim();
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }

        return "";
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(string rootPathName, StringBuilder volumeNameBuffer, int volumeNameSize, out uint volumeSerialNumber,
        out uint maximumComponentLength, out uint fileSystemFlags, StringBuilder fileSystemNameBuffer, int fileSystemNameSize);
}
