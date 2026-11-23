using Avalonia.Threading;
using ThixSync.Core.Business;
using ThixSync.Core.Infrastructure;

namespace ThixSync;

/// <summary>
/// When to look for updates and what to do with one: check shortly after start and every 6 hours (when enabled),
/// install on its own with "full auto update", otherwise only point to it (tray, settings).
/// </summary>
internal static class UpdateCoordinator
{
    private static readonly DispatcherTimer Timer = new() { Interval = UpdateService.CheckInterval };

    /// <summary>Raised on the UI thread with the newest result.</summary>
    public static event EventHandler<UpdateCheckResult>? Checked;

    public static void Start()
    {
        if (!UpdateService.CanUpdate)
        {
            return;
        }

        Timer.Tick += async (_, _) => await CheckInBackgroundAsync();
        Timer.Start();

        // The first check waits a bit: the sign-in has more important things to do.
        DispatcherTimer.RunOnce(() => _ = CheckInBackgroundAsync(), TimeSpan.FromSeconds(45));
    }

    public static async Task<UpdateCheckResult> CheckAsync()
    {
        var result = await UpdateService.CheckAsync();
        Checked?.Invoke(null, result);
        return result;
    }

    /// <summary>Downloads and starts the installation, then ends ThixSync; the update starts it again.</summary>
    public static async Task InstallAsync(UpdateManifest manifest, IProgress<double>? progress = null)
    {
        await UpdateService.DownloadAndStartInstallerAsync(manifest, progress);
        App.Instance.ExitApplication();
    }

    private static async Task CheckInBackgroundAsync()
    {
        var settings = SettingsService.Get();
        if (!settings.CheckForUpdates)
        {
            return;
        }

        try
        {
            var result = await CheckAsync();
            if (result is { IsUpdateAvailable: true, Manifest: { } manifest } && settings.InstallUpdatesAutomatically)
            {
                Log.Info($"Full auto update: installing {manifest.Version}");
                await InstallAsync(manifest);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or Core.Models.BusinessException)
        {
            Log.Warning($"Automatic update failed: {exception.Message}");
        }
    }
}
