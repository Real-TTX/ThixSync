using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ThixSync.Core.Platform.Windows;

/// <summary>
/// Tells the running programs (Explorer above all, which starts new terminals) that the user's environment variables changed –
/// what the "Umgebungsvariablen" dialog of Windows does after saving. Without it a new PATH counts only after the next sign-in.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class EnvironmentChange
{
    private static readonly IntPtr Broadcast = new(0xFFFF);
    private const uint SettingChange = 0x001A;
    private const uint AbortIfHung = 0x0002;

    /// <summary>Sends WM_SETTINGCHANGE "Environment" to every top-level window; a hanging one is skipped after 5 seconds.</summary>
    public static void Announce() =>
        SendMessageTimeout(Broadcast, SettingChange, IntPtr.Zero, "Environment", AbortIfHung, 5000, out _);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
}
