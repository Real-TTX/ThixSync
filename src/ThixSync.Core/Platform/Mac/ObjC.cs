using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ThixSync.Core.Platform.Mac;

/// <summary>
/// The Objective-C runtime for the few Cocoa calls ThixSync needs (NSFileManager: the trash). Every call
/// that creates autoreleased objects runs inside <see cref="Pool"/>. objc_msgSend is declared once per signature: on
/// Apple silicon it must be called with exactly the types of the method.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class ObjC
{
    private const string LibObjC = "/usr/lib/libobjc.A.dylib";

    private static readonly Dictionary<string, IntPtr> Selectors = [];
    private static readonly Dictionary<string, IntPtr> Classes = [];
    private static readonly HashSet<string> LoadedFrameworks = [];

    [DllImport(LibObjC, EntryPoint = "objc_getClass")]
    private static extern IntPtr GetClass(string name);

    [DllImport(LibObjC, EntryPoint = "sel_registerName")]
    private static extern IntPtr RegisterSelector(string name);

    [DllImport(LibObjC, EntryPoint = "objc_autoreleasePoolPush")]
    private static extern IntPtr PoolPush();

    [DllImport(LibObjC, EntryPoint = "objc_autoreleasePoolPop")]
    private static extern void PoolPop(IntPtr pool);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static extern IntPtr Send(IntPtr receiver, IntPtr selector);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr argument);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr first, IntPtr second);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static extern IntPtr SendWithLength(IntPtr receiver, IntPtr selector, IntPtr bytes, nuint length);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static extern IntPtr SendWithInteger(IntPtr receiver, IntPtr selector, nint value);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static extern IntPtr SendWithIntegerAndObject(IntPtr receiver, IntPtr selector, nint value, IntPtr argument);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static extern IntPtr SendWithBool(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool value);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static extern nint SendInteger(IntPtr receiver, IntPtr selector);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static extern nuint SendUnsigned(IntPtr receiver, IntPtr selector);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SendBool(IntPtr receiver, IntPtr selector, IntPtr argument);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SendBool(IntPtr receiver, IntPtr selector, IntPtr first, IntPtr second);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SendBoolWithObjects(IntPtr receiver, IntPtr selector, IntPtr first, IntPtr second, IntPtr third);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static extern void SendVoid(IntPtr receiver, IntPtr selector, IntPtr argument);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static extern void SendVoidWithInteger(IntPtr receiver, IntPtr selector, nint value);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static extern void SendVoidWithBool(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool value);

    public static IntPtr Selector(string name)
    {
        lock (Selectors)
        {
            if (!Selectors.TryGetValue(name, out var selector))
            {
                selector = RegisterSelector(name);
                Selectors[name] = selector;
            }

            return selector;
        }
    }

    public static IntPtr Class(string name)
    {
        lock (Classes)
        {
            if (!Classes.TryGetValue(name, out var cls) || cls == IntPtr.Zero)
            {
                cls = GetClass(name);
                Classes[name] = cls;
            }

            return cls;
        }
    }

    /// <summary>Loads a system framework once, so its classes are known to the runtime (AppKit, Vision ...).</summary>
    public static void LoadFramework(string name)
    {
        lock (LoadedFrameworks)
        {
            if (LoadedFrameworks.Add(name))
            {
                NativeLibrary.TryLoad($"/System/Library/Frameworks/{name}.framework/{name}", out _);
            }
        }
    }

    /// <summary>An autorelease pool for the time of a call: <c>using var pool = ObjC.Pool();</c></summary>
    public static AutoreleasePool Pool() => new(PoolPush());

    /// <summary>An autoreleased NSString.</summary>
    public static IntPtr String(string value)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(value);
        try
        {
            return Send(Class("NSString"), Selector("stringWithUTF8String:"), utf8);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    public static string? ToManagedString(IntPtr nsString) =>
        nsString == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(Send(nsString, Selector("UTF8String")));

    /// <summary>An autoreleased NSData with a copy of the bytes.</summary>
    public static unsafe IntPtr Data(byte[] bytes)
    {
        fixed (byte* pointer = bytes)
        {
            return SendWithLength(Class("NSData"), Selector("dataWithBytes:length:"), (IntPtr)pointer, (nuint)bytes.Length);
        }
    }

    public static byte[]? ToBytes(IntPtr nsData)
    {
        if (nsData == IntPtr.Zero)
        {
            return null;
        }

        var length = (int)SendUnsigned(nsData, Selector("length"));
        var bytes = new byte[length];
        if (length > 0)
        {
            Marshal.Copy(Send(nsData, Selector("bytes")), bytes, 0, length);
        }

        return bytes;
    }

    public static int Count(IntPtr nsArray) => nsArray == IntPtr.Zero ? 0 : (int)SendUnsigned(nsArray, Selector("count"));

    public static IntPtr At(IntPtr nsArray, int index) => SendWithInteger(nsArray, Selector("objectAtIndex:"), index);

    public static bool Contains(IntPtr nsArray, string value) =>
        nsArray != IntPtr.Zero && SendBool(nsArray, Selector("containsObject:"), String(value));

    public readonly struct AutoreleasePool(IntPtr pool) : IDisposable
    {
        public void Dispose() => PoolPop(pool);
    }
}
