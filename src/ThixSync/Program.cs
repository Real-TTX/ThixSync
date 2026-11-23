using Avalonia;
using Avalonia.Controls;

namespace ThixSync;

internal static class Program
{
    /// <summary>
    /// One program for everything (App decides): the application and - on Windows - its setup.
    /// The application ends only through "Beenden" (tray / menu bar), not when its window closes.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        // First of all: no error ends ThixSync without a report (and on Windows a message box).
        var isSnapshot = args.Contains(Snapshot.Argument);
        ThixSync.Core.Infrastructure.ProblemReports.Install(showMessageBox: !args.Contains(SelfTest.Argument) && !isSnapshot);

        // The self test and the snapshots work in a fresh folder of their own (before anything reads the data folder).
        if (args.Contains(SelfTest.Argument) || isSnapshot)
        {
            Environment.SetEnvironmentVariable(ThixSync.Core.Infrastructure.AppPaths.DataDirectoryVariable,
                Path.Combine(Path.GetTempPath(), isSnapshot ? "thixsync-snapshot" : "thixsync-self-test", Guid.NewGuid().ToString("N")));
        }

        // The snapshots draw the windows headless: no screen, no tray, no shortcut, no single-instance check.
        if (isSnapshot)
        {
            return Snapshot.Run(args);
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    /// <summary>Also used by the XAML previewer.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // A window app with a menu bar icon: in the Dock while it runs (a click on it shows the window again).
            .With(new MacOSPlatformOptions { ShowInDock = true })
            .With(new Win32PlatformOptions { OverlayPopups = true })
            .LogToTrace();
}
