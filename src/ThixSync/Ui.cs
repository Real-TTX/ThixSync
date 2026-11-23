using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using ThixSync.Core.Infrastructure;

namespace ThixSync;

/// <summary>
/// Small helpers for code-built UI: the app icon and logo, the UI thread, opening addresses, texts of times and sizes.
/// Colours, sizes and named looks are MatDesktop's (<c>control.Use(property, "Mat.TextMuted")</c>,
/// <c>control.Themed("MatButton.Primary")</c>), icons are <c>MatIcon</c> with the name of the sprite.
/// </summary>
internal static class Ui
{
    private static WindowIcon? _appIcon;
    private static Bitmap? _logo;

    /// <summary>The icon of every window of ThixSync (the task bar, Alt+Tab): MatThemeOptions.Icon gives it to every MatWindow.</summary>
    public static WindowIcon AppIcon => _appIcon ??= new WindowIcon(AssetLoader.Open(new Uri("avares://ThixSync/Assets/thixsync.ico")));

    /// <summary>The logo (the mark of the app), for the title bar and the header of the setup.</summary>
    public static Bitmap Logo => _logo ??= new Bitmap(AssetLoader.Open(new Uri("avares://ThixSync/Assets/logo-256.png")));

    /// <summary>
    /// A named look of MatDesktop (a control theme such as "MatButton.Primary" or "MatChoiceRow") for
    /// an object initializer; anywhere else <c>control.Themed(key)</c>. A control theme is the same in light and dark.
    /// </summary>
    public static ControlTheme Theme(string key) =>
        Application.Current!.TryGetResource(key, null, out var value) && value is ControlTheme theme
            ? theme
            : throw new InvalidOperationException($"The control theme {key} is missing (MatDesktop/Themes/Controls.axaml).");

    /// <summary>Runs the action on the UI thread (events of the business layer come from any thread).</summary>
    public static void Post(Action action) => Dispatcher.UIThread.Post(action);

    /// <summary>The window that hosts a control (null while it is not shown).</summary>
    public static Window? WindowOf(Control control) => TopLevel.GetTopLevel(control) as Window;

    /// <summary>Ctrl on Windows, Cmd on macOS: the modifier of the app's own shortcuts.</summary>
    public static KeyModifiersName CommandKey => OperatingSystem.IsMacOS() ? new("⌘", Avalonia.Input.KeyModifiers.Meta) : new("Strg", Avalonia.Input.KeyModifiers.Control);

    /// <summary>Tests see what would be opened instead of starting the browser.</summary>
    internal static Action<string>? OpenExternalForTests { get; set; }

    /// <summary>Opens a web address or a folder with the program the system uses for it (browser, Explorer, Finder).</summary>
    public static void OpenExternal(string pathOrUrl)
    {
        if (OpenExternalForTests is { } open)
        {
            open(pathOrUrl);
            return;
        }

        try
        {
            var startInfo = OperatingSystem.IsMacOS()
                ? new System.Diagnostics.ProcessStartInfo("open") { ArgumentList = { pathOrUrl }, UseShellExecute = false }
                : OperatingSystem.IsLinux()
                    ? new System.Diagnostics.ProcessStartInfo("xdg-open") { ArgumentList = { pathOrUrl }, UseShellExecute = false }
                    : new System.Diagnostics.ProcessStartInfo(pathOrUrl) { UseShellExecute = true };
            System.Diagnostics.Process.Start(startInfo)?.Dispose();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warning($"Could not open {pathOrUrl}: {exception.Message}");
        }
    }

    /// <summary>"vor 3 Min." / "14:28" / "Gestern, 17:56" / "12.11., 09:14" / "12.11.2025".</summary>
    public static string FormatTime(DateTimeOffset value)
    {
        var local = value.ToLocalTime();
        var today = AppClock.Today;
        if (local.Date == today)
        {
            return local.ToString("HH:mm");
        }

        if (local.Date == today.AddDays(-1))
        {
            return $"Gestern, {local:HH:mm}";
        }

        return local.Year == today.Year ? local.ToString("dd.MM., HH:mm") : local.ToString("dd.MM.yyyy");
    }

    /// <summary>"Heute, 13:47" or "12.11.2026, 13:47".</summary>
    public static string FormatDateTime(DateTimeOffset value)
    {
        var local = value.ToLocalTime();
        if (local.Date == AppClock.Today)
        {
            return $"Heute, {local:HH:mm}";
        }

        return local.Date == AppClock.Today.AddDays(-1) ? $"Gestern, {local:HH:mm}" : local.ToString("dd.MM.yyyy, HH:mm");
    }

    private static readonly System.Globalization.CultureInfo German = System.Globalization.CultureInfo.GetCultureInfo("de-DE");

    /// <summary>"0 B", "512 B", "12,3 KB", "4,56 MB", "123 GB", "1,23 TB" (1 KB = 1024 B, as Explorer and Finder count on their drives).</summary>
    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double value = Math.Abs(bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var sign = bytes < 0 ? "−" : "";
        if (unit == 0)
        {
            return $"{sign}{value:0} B";
        }

        var format = value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00";
        return sign + value.ToString(format, German) + " " + units[unit];
    }

    /// <summary>"+1,20 GB", "−340 MB", "±0 B".</summary>
    public static string FormatDelta(long delta) => delta switch
    {
        > 0 => "+" + FormatSize(delta),
        < 0 => FormatSize(delta),
        _ => "±0 B",
    };

    /// <summary>"1.234.567" (German thousands).</summary>
    public static string FormatCount(long count) => count.ToString("N0", German);

    /// <summary>"12,5 %".</summary>
    public static string FormatShare(long part, long whole) =>
        whole <= 0 ? "–" : (100.0 * part / whole).ToString(part * 1000 < whole ? "0.0" : "0.#", German) + " %";

    /// <summary>"3 Min. 12 s", "45 s".</summary>
    public static string FormatDuration(TimeSpan duration) => duration.TotalSeconds < 60
        ? $"{Math.Max(0, duration.TotalSeconds):0} s"
        : duration.TotalMinutes < 60 ? $"{(int)duration.TotalMinutes} Min. {duration.Seconds} s" : $"{(int)duration.TotalHours} Std. {duration.Minutes} Min.";

    /// <summary>Copies a text to the clipboard of the window of <paramref name="control"/>.</summary>
    public static async Task CopyTextAsync(Control control, string text)
    {
        if (TopLevel.GetTopLevel(control)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }
}

/// <summary>The name and the modifier of the command key of the platform.</summary>
internal sealed record KeyModifiersName(string Name, Avalonia.Input.KeyModifiers Modifier);
