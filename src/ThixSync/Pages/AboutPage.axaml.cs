using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MatDesktop;
using ThixSync.Core.Business;
using ThixSync.Core.Infrastructure;

namespace ThixSync.Pages;

/// <summary>Version and channel ("Version 1.0.0 · release", SETUP-07), build and where the data lives.</summary>
public partial class AboutPage : UserControl
{
    public AboutPage()
    {
        InitializeComponent();
        VersionText.Text = MatVersion.Format(AppInfo.Version, AppInfo.Channel);
        BuildText.Text = AppInfo.BuildNumber > 0 ? $"#{AppInfo.BuildNumber}" : "–";
        SystemText.Text = $"{RuntimeInformation.OSDescription} ({UpdateService.PlatformKey}), .NET {Environment.Version}";
        DataText.Text = AppPaths.DataDirectory;
        AttachedToVisualTree += (_, _) => RefreshUpdate();
        UpdateCoordinator.Checked += (_, _) => Ui.Post(RefreshUpdate);
    }

    /// <summary>What the last update check found; the button leads to Einstellungen › Updates (install or check there).</summary>
    private void RefreshUpdate()
    {
        var result = UpdateService.LastResult;
        var available = result is { IsUpdateAvailable: true, Manifest: not null };
        UpdateTitle.Text = available ? $"Version {result!.Manifest!.Version} ist verfügbar" : AppInfo.IsLocalBuild ? "Lokaler Build" : "Updates";
        UpdateText.Text = available
            ? "Installieren dauert nur einen Moment, deine Quellen und Analysen bleiben erhalten."
            : AppInfo.IsLocalBuild
                ? MatVersion.LocalNote(AppInfo.Channel)
                : result is null
                    ? "Noch nicht geprüft."
                    : $"{result.Message} Zuletzt geprüft: {(UpdateService.LastCheck is { } last ? Ui.FormatDateTime(last) : "–")}.";
        UpdateLabel.Text = available ? "Update installieren …" : "Nach Updates suchen …";
        if (available)
        {
            UpdateButton.Themed("MatButton.Primary");
        }
        else
        {
            UpdateButton.ClearValue(ThemeProperty);
        }

        UpdateIcon.Use(MatIcon.ForegroundProperty, available ? "Mat.AccentText" : "Mat.TextMuted");
    }

    private void OnUpdateClick(object? sender, RoutedEventArgs e)
    {
        if (Ui.WindowOf(this) is MainWindow window)
        {
            window.ShowSettings(SettingsPage.UpdatesTab);
        }
    }

    private void OnGitHubClick(object? sender, RoutedEventArgs e) => Ui.OpenExternal($"https://github.com/{AppInfo.Repository}");

    private void OnBackClick(object? sender, RoutedEventArgs e)
    {
        if (Ui.WindowOf(this) is MainWindow window)
        {
            window.ShowMap();
        }
    }
}
