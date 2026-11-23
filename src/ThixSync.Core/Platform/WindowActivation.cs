using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ThixSync.Core.Platform;

/// <summary>
/// Bringing ThixSync's window to the front when the shortcut is pressed while another app works. Windows only lets the
/// process that received the last input take the foreground; the hook swallowed that input, so ThixSync joins the
/// input queue of the window in front for a moment (the usual way of launchers and tray apps).
/// </summary>
public static partial class WindowActivation
{
    /// <summary>Call on the UI thread right after the window was shown.</summary>
    public static void BringToFront(IntPtr window)
    {
        if (OperatingSystem.IsWindows() && window != IntPtr.Zero)
        {
            ForceForeground(window);
        }
    }

    /// <summary>The mouse position in screen pixels (Windows); null where it is not known.</summary>
    public static (int X, int Y)? CursorPosition()
    {
        if (OperatingSystem.IsWindows() && GetCursorPos(out var point))
        {
            return (point.X, point.Y);
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    private static void ForceForeground(IntPtr window)
    {
        var foreground = GetForegroundWindow();
        if (foreground == window)
        {
            return;
        }

        var currentThread = GetCurrentThreadId();
        var foregroundThread = GetWindowThreadProcessId(foreground, out _);
        var attached = foregroundThread != 0 && foregroundThread != currentThread && AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            BringWindowToTop(window);
            SetForegroundWindow(window);
            SetFocus(window);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(currentThread, foregroundThread, false);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point point);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BringWindowToTop(IntPtr window);

    [LibraryImport("user32.dll")]
    private static partial IntPtr SetFocus(IntPtr window);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachThreadInput(uint attach, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool doAttach);
}
