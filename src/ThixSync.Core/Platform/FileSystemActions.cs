using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ThixSync.Core.Infrastructure;
using ThixSync.Core.Platform.Mac;

namespace ThixSync.Core.Platform;

/// <summary>
/// What the system does with a file or folder: open it, show it in Explorer / Finder, move it to the trash (or delete it for
/// good where there is no trash: a share in the network). Only <see cref="Business.FileActionService"/> calls these; it checks
/// first whether it may.
/// </summary>
internal static class FileSystemActions
{
    /// <summary>Tests see what would happen instead of the system doing it.</summary>
    internal static Action<string, string>? ForTests { get; set; }

    public static void Open(string path)
    {
        if (ForTests is { } test)
        {
            test("open", path);
            return;
        }

        var startInfo = OperatingSystem.IsMacOS()
            ? new ProcessStartInfo("/usr/bin/open") { ArgumentList = { path }, UseShellExecute = false }
            : OperatingSystem.IsLinux()
                ? new ProcessStartInfo("xdg-open") { ArgumentList = { path }, UseShellExecute = false }
                : new ProcessStartInfo(path) { UseShellExecute = true };
        Process.Start(startInfo)?.Dispose();
    }

    /// <summary>Opens the folder around the path with the path chosen.</summary>
    public static void Reveal(string path)
    {
        if (ForTests is { } test)
        {
            test("reveal", path);
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{path}\"", UseShellExecute = false })?.Dispose();
        }
        else if (OperatingSystem.IsMacOS())
        {
            Process.Start(new ProcessStartInfo("/usr/bin/open") { ArgumentList = { "-R", path }, UseShellExecute = false })?.Dispose();
        }
        else
        {
            Open(Path.GetDirectoryName(path) ?? path);
        }
    }

    /// <summary>Moves the path to the trash; true when it is there. False: the system has no trash for it (deleted for good).</summary>
    public static bool Delete(string path, bool useTrash)
    {
        if (ForTests is { } test)
        {
            test(useTrash ? "trash" : "delete", path);
            DeleteForGood(path);
            return useTrash;
        }

        if (useTrash && OperatingSystem.IsWindows())
        {
            DeleteWindows(path, toRecycleBin: true);
            return !File.Exists(path) && !Directory.Exists(path);
        }

        if (useTrash && OperatingSystem.IsMacOS() && MacTrash.MoveToTrash(path))
        {
            return true;
        }

        if (OperatingSystem.IsWindows())
        {
            DeleteWindows(path, toRecycleBin: false);
            return false;
        }

        DeleteForGood(path);
        return false;
    }

    private static void DeleteForGood(string path)
    {
        if (Directory.Exists(path))
        {
            var info = new DirectoryInfo(path);
            if (info.LinkTarget is not null)
            {
                info.Delete(); // the link, never what it points to
            }
            else
            {
                info.Delete(recursive: true);
            }
        }
        else if (File.Exists(path))
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
    }

    /// <summary>SHFileOperation: the recycle bin with FOF_ALLOWUNDO (network paths have none: there it deletes for good).</summary>
    [SupportedOSPlatform("windows")]
    private static void DeleteWindows(string path, bool toRecycleBin)
    {
        var operation = new ShFileOperation
        {
            Function = FoDelete,
            From = path + "\0\0",
            Flags = (ushort)(FofNoConfirmation | FofNoErrorUi | FofSilent | (toRecycleBin ? FofAllowUndo : 0)),
        };
        var result = SHFileOperation(ref operation);
        if (result != 0 || operation.AnyOperationsAborted)
        {
            throw new IOException($"Löschen ist fehlgeschlagen (Fehler {result}). Ist die Datei geöffnet oder schreibgeschützt?");
        }
    }

    private const uint FoDelete = 3;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOperation
    {
        public IntPtr Window;
        public uint Function;
        public string From;
        public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool AnyOperationsAborted;
        public IntPtr NameMappings;
        public string? ProgressTitle;
    }

#pragma warning disable SYSLIB1054 // a struct with strings: the classic marshaller is simpler here
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOperation operation);
#pragma warning restore SYSLIB1054
}
