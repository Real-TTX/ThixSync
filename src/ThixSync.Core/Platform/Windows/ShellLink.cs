using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;

namespace ThixSync.Core.Platform.Windows;

/// <summary>
/// Writes a Windows shortcut (.lnk) through the shell's ShellLink object, with source-generated COM interop: no
/// "dynamic", no built-in COM, so it keeps working in the trimmed single-file build.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class ShellLink
{
    private static readonly Guid ShellLinkClass = new("00021401-0000-0000-C000-000000000046");
    private static readonly StrategyBasedComWrappers Wrappers = new();

    public static void Write(string path, string target, string workingDirectory, string description, string iconPath)
    {
        var interfaceId = typeof(IShellLinkW).GUID;
        Marshal.ThrowExceptionForHR(CoCreateInstance(ShellLinkClass, IntPtr.Zero, 1 /* CLSCTX_INPROC_SERVER */, interfaceId, out var instance));
        try
        {
            var link = (IShellLinkW)Wrappers.GetOrCreateObjectForComInstance(instance, CreateObjectFlags.UniqueInstance);
            link.SetPath(target);
            link.SetWorkingDirectory(workingDirectory);
            link.SetDescription(description);
            link.SetIconLocation(iconPath, 0);
            ((IPersistFile)link).Save(path, true);
        }
        finally
        {
            Marshal.Release(instance);
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid classId, IntPtr outer, uint context, in Guid interfaceId, out IntPtr instance);

    /// <summary>The methods in the order of the vtable; only the setters are used, the rest keep their place.</summary>
    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    internal partial interface IShellLinkW
    {
        void GetPath(IntPtr file, int maxPath, IntPtr findData, uint flags);

        void GetIDList(out IntPtr idList);

        void SetIDList(IntPtr idList);

        void GetDescription(IntPtr name, int maxName);

        void SetDescription(string name);

        void GetWorkingDirectory(IntPtr directory, int maxPath);

        void SetWorkingDirectory(string directory);

        void GetArguments(IntPtr arguments, int maxPath);

        void SetArguments(string arguments);

        void GetHotkey(out short hotkey);

        void SetHotkey(short hotkey);

        void GetShowCmd(out int showCommand);

        void SetShowCmd(int showCommand);

        void GetIconLocation(IntPtr iconPath, int maxPath, out int iconIndex);

        void SetIconLocation(string iconPath, int iconIndex);

        void SetRelativePath(string relativePath, uint reserved);

        void Resolve(IntPtr window, uint flags);

        void SetPath(string file);
    }

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    internal partial interface IPersistFile
    {
        void GetClassID(out Guid classId);

        [PreserveSig]
        int IsDirty();

        void Load(string fileName, uint mode);

        void Save(string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);

        void SaveCompleted(string fileName);

        void GetCurFile(out IntPtr fileName);
    }
}
