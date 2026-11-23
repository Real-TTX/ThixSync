using System.Runtime.Versioning;

namespace ThixSync.Core.Platform.Mac;

/// <summary>The trash of macOS: NSFileManager trashItemAtURL (the Finder can put it back).</summary>
[SupportedOSPlatform("macos")]
internal static class MacTrash
{
    /// <summary>True when the item is in the trash; false when the volume has none (a share) or it failed.</summary>
    public static bool MoveToTrash(string path)
    {
        ObjC.LoadFramework("Foundation");
        using var pool = ObjC.Pool();
        var manager = ObjC.Send(ObjC.Class("NSFileManager"), ObjC.Selector("defaultManager"));
        var url = ObjC.Send(ObjC.Class("NSURL"), ObjC.Selector("fileURLWithPath:"), ObjC.String(path));
        return manager != IntPtr.Zero && url != IntPtr.Zero
               && ObjC.SendBoolWithObjects(manager, ObjC.Selector("trashItemAtURL:resultingItemURL:error:"), url, IntPtr.Zero, IntPtr.Zero);
    }
}
