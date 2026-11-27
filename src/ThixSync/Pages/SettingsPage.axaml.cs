using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MatCloud.Contract;
using MatDesktop;
using ThixSync.Controls;
using ThixSync.Core.Business;
using ThixSync.Core.Infrastructure;
using ThixSync.Core.Models;
using ThixSync.Core.Remote;
using ThixSync.Dialogs;

namespace ThixSync.Pages;

/// <summary>
/// The settings of this computer in tabs. Every change goes through the business layer (SettingsService, AutostartService,
/// CloudSyncService, UpdateService, RemoteServer), which checks values and policies; an error is shown at its field or above the
/// tabs. A configuration locked by the organisation (<see cref="AccessPolicy.IsConfigurationLocked"/>) is shown read-only.
/// </summary>
public partial class SettingsPage : UserControl
{
    public const string GeneralTab = "general";
    public const string TransferTab = "transfer";
    public const string RemoteTab = "remote";
    public const string CloudTab = "cloud";
    public const string UpdatesTab = "updates";
    public const string AdvancedTab = "advanced";

    private bool _isLoading;
    private UpdateManifest? _availableUpdate;
    private CancellationTokenSource? _browserSignIn;
    private string? _browserAddress;

    public SettingsPage()
    {
        InitializeComponent();
        KeepProtocolChoice.ItemsSource = AppSettings.KeepProtocolChoices.Select(days => days switch
        {
            0 => "Für immer",
            365 => "1 Jahr",
            _ => $"{days} Tage",
        }).ToList();

        AutostartSwitch.IsCheckedChanged += (_, _) => Apply(() =>
        {
            if (AutostartSwitch.IsChecked == true)
            {
                AutostartService.Enable(App.ExecutablePath);
            }
            else
            {
                AutostartService.Disable();
            }
        });
        ThemeChoice.SelectionChanged += (_, _) => Apply(() => SettingsService.Update(settings => settings with { Theme = (ThemeChoice)Math.Max(0, ThemeChoice.SelectedIndex) }));
        NotifyChoice.SelectionChanged += (_, _) => Apply(() => SettingsService.Update(settings => settings with { Notify = (NotifyChoice)Math.Max(0, NotifyChoice.SelectedIndex) }));
        KeepProtocolChoice.SelectionChanged += (_, _) => Apply(() =>
        {
            if (KeepProtocolChoice.SelectedIndex >= 0)
            {
                SettingsService.Update(settings => settings with { KeepProtocolDays = AppSettings.KeepProtocolChoices[KeepProtocolChoice.SelectedIndex] });
            }
        });
        ParallelismBox.ValueChanged += (_, _) => Apply(() => SettingsService.Update(settings => settings with { DefaultParallelism = Whole(ParallelismBox) }));
        ConcurrentJobsBox.ValueChanged += (_, _) => Apply(() => SettingsService.Update(settings => settings with { MaxConcurrentJobs = Whole(ConcurrentJobsBox) }));
        BandwidthBox.ValueChanged += (_, _) => Apply(() => SettingsService.Update(settings => settings with { BandwidthLimitKiB = Whole(BandwidthBox) }));
        RetriesBox.ValueChanged += (_, _) => Apply(() => SettingsService.Update(settings => settings with { Retries = Whole(RetriesBox) }));
        SmallFilesBox.ValueChanged += (_, _) => Apply(() => SettingsService.Update(settings => settings with { SmallFileThresholdKiB = Whole(SmallFilesBox) }));
        BatterySwitch.IsCheckedChanged += (_, _) => Apply(() => SettingsService.Update(settings => settings with { PauseOnBattery = BatterySwitch.IsChecked == true }));
        RemoteSwitch.IsCheckedChanged += (_, _) => Apply(() => SettingsService.UpdateRemote(remote => remote with { IsEnabled = RemoteSwitch.IsChecked == true }));
        PortBox.ValueChanged += (_, _) => Apply(() => SettingsService.UpdateRemote(remote => remote with { Port = Whole(PortBox) }));
        CheckUpdatesSwitch.IsCheckedChanged += (_, _) => Apply(() => SettingsService.Update(settings => settings with { CheckForUpdates = CheckUpdatesSwitch.IsChecked == true }));
        AutoInstallSwitch.IsCheckedChanged += (_, _) => Apply(() => SettingsService.Update(settings => settings with { InstallUpdatesAutomatically = AutoInstallSwitch.IsChecked == true }));

        UpdateCoordinator.Checked += (_, result) => ShowUpdateResult(result);
        Ui.WhileShown(this, Subscribe, Unsubscribe, Load);
        Load();
    }

    /// <summary>A sign-in in the browser is waiting for its confirmation (the main window stays open meanwhile).</summary>
    public bool IsSigningInInBrowser => _browserSignIn is not null;

    public void SelectTab(string tab) => Tabs.SelectedItem = tab switch
    {
        TransferTab => TransferTabItem,
        RemoteTab => RemoteTabItem,
        CloudTab => CloudTabItem,
        UpdatesTab => UpdatesTabItem,
        AdvancedTab => AdvancedTabItem,
        _ => GeneralTabItem,
    };

    private void Subscribe()
    {
        SettingsService.Changed += OnSettingsChanged;
        CloudSyncService.StatusChanged += OnCloudStatus;
    }

    private void Unsubscribe()
    {
        SettingsService.Changed -= OnSettingsChanged;
        CloudSyncService.StatusChanged -= OnCloudStatus;
    }

    private void OnSettingsChanged(object? sender, AppSettings settings) => Ui.Post(Load);

    private void OnCloudStatus(object? sender, SyncStatus status) => Ui.Post(LoadCloud);

    /// <summary>Shows the stored settings (again after every change, also from the tray or the sidebar).</summary>
    private void Load()
    {
        _isLoading = true;
        try
        {
            var settings = SettingsService.Get();
            var locked = AccessPolicy.IsConfigurationLocked;
            if (locked)
            {
                PolicyNote.Show("Von deiner Organisation festgelegt: die Einstellungen sind gesperrt (Richtlinie LockConfiguration). Du kannst sie ansehen, aber nicht ändern.", InfoSeverity.Info);
            }
            else if (AccessPolicy.IsCloudDisabled || AccessPolicy.AreUpdatesDisabled || AccessPolicy.IsDeleteDisabled || AccessPolicy.IsRemoteDisabled || AccessPolicy.IsCloudStorageDisabled)
            {
                PolicyNote.Show("Von deiner Organisation festgelegt: was eine Richtlinie bestimmt, ist hier ausgeschaltet.", InfoSeverity.Info);
            }
            else
            {
                PolicyNote.Hide();
            }

            AutostartSwitch.IsChecked = AutostartService.IsEnabled();
            AutostartSwitch.IsEnabled = AutostartService.IsSupported && !locked;
            ThemeChoice.SelectedIndex = (int)settings.Theme;
            NotifyChoice.SelectedIndex = (int)settings.Notify;
            KeepProtocolChoice.SelectedIndex = AppSettings.KeepProtocolChoices.ToList().IndexOf(settings.KeepProtocolDays);
            ParallelismBox.Value = settings.DefaultParallelism;
            ConcurrentJobsBox.Value = settings.MaxConcurrentJobs;
            BandwidthBox.Value = settings.BandwidthLimitKiB;
            RetriesBox.Value = settings.Retries;
            SmallFilesBox.Value = settings.SmallFileThresholdKiB;
            BatterySwitch.IsChecked = settings.PauseOnBattery;
            CheckUpdatesSwitch.IsChecked = settings.CheckForUpdates;
            AutoInstallSwitch.IsChecked = settings.InstallUpdatesAutomatically;
            foreach (var control in new Control[]
                     {
                         ThemeChoice, NotifyChoice, KeepProtocolChoice, ParallelismBox, ConcurrentJobsBox, BandwidthBox, RetriesBox, SmallFilesBox, BatterySwitch,
                         GoogleIdBox, GoogleSecretBox, MicrosoftIdBox, DropboxIdBox, BoxIdBox, BoxSecretBox, SaveOAuthButton, ResetButton,
                     })
            {
                control.IsEnabled = !locked;
            }

            InstalledVersionText.Text = MatVersion.Value(AppInfo.Version, AppInfo.Channel);
            ChannelText.Text = AppInfo.Channel switch
            {
                "release" => "Release (Branch main)",
                "nightly" => "Nightly (Branch dev)",
                _ => "Lokaler Build – keine Updates",
            };
            LastCheckText.Text = UpdateService.LastCheck is { } last ? Formats.When(last) : "–";
            CheckUpdateButton.IsEnabled = UpdateService.CanUpdate;
            CheckUpdatesSwitch.IsEnabled = AutoInstallSwitch.IsEnabled = UpdateService.CanUpdate && !locked;

            var clients = settings.OAuthClients;
            GoogleIdBox.Text = clients.GoogleClientId;
            GoogleSecretBox.Text = clients.GoogleClientSecret;
            MicrosoftIdBox.Text = clients.MicrosoftClientId;
            DropboxIdBox.Text = clients.DropboxClientId;
            BoxIdBox.Text = clients.BoxClientId;
            BoxSecretBox.Text = clients.BoxClientSecret;
            DataFolderRow.Description = AppPaths.DataDirectory;
            LoadRemote(settings.Remote, locked);
            LoadCloud();
        }
        finally
        {
            _isLoading = false;
        }
    }

    /// <summary>The tab Remote: on/off, port, fingerprint, the shares and the paired computers.</summary>
    private void LoadRemote(RemoteSettings remote, bool locked)
    {
        var forbidden = AccessPolicy.IsRemoteDisabled;
        if (forbidden)
        {
            RemoteMessage.Show("Von deiner Organisation festgelegt: ThixSync Remote ist ausgeschaltet (Richtlinie DisableRemote).", InfoSeverity.Warning);
        }
        else if (remote.IsEnabled && !SafeIsListening())
        {
            RemoteMessage.Show($"Remote ist an, aber dieser Computer wartet nicht auf Port {remote.Port} – ist er schon belegt? Wähle einen anderen Port.", InfoSeverity.Warning);
        }

        RemoteSwitch.IsChecked = remote.IsEnabled;
        PortBox.Value = remote.Port;
        var editable = !locked && !forbidden;
        RemoteSwitch.IsEnabled = PortBox.IsEnabled = ShareNameBox.IsEnabled = SharePathBox.IsEnabled = AddShareButton.IsEnabled = editable;
        PairingButton.IsEnabled = editable && remote.IsEnabled;
        var fingerprint = SafeFingerprint();
        FingerprintText.Text = fingerprint.Length > 0 ? fingerprint : "Entsteht, sobald Remote eingeschaltet ist.";
        FingerprintText.Use(TextBlock.FontFamilyProperty, fingerprint.Length > 0 ? "Mat.Mono" : "Mat.Font");

        SharesList.Children.Clear();
        foreach (var share in remote.Shares)
        {
            SharesList.Children.Add(ShareRow(share, editable));
        }

        if (remote.Shares.Count == 0)
        {
            SharesList.Children.Add(new TextBlock { Text = "Noch keine Freigabe – ein anderer Computer sieht nur, was du hier freigibst." }.Themed("MatText.Muted"));
        }

        PeersList.Children.Clear();
        foreach (var peer in remote.Peers)
        {
            PeersList.Children.Add(PeerRow(peer, editable));
        }

        if (remote.Peers.Count == 0)
        {
            PeersList.Children.Add(new TextBlock { Text = "Noch kein Computer gekoppelt." }.Themed("MatText.Muted"));
        }
    }

    private Control ShareRow(RemoteShare share, bool editable)
    {
        var readOnly = new ToggleSwitch { OnContent = "Nur lesen", OffContent = "Lesen und schreiben", IsChecked = share.IsReadOnly, IsEnabled = editable, VerticalAlignment = VerticalAlignment.Center };
        Avalonia.Automation.AutomationProperties.SetName(readOnly, $"{share.Name} nur lesen");
        readOnly.IsCheckedChanged += (_, _) => Apply(() => SettingsService.UpdateRemote(remote => remote with
        {
            Shares = [.. remote.Shares.Select(other => other.Name == share.Name ? other with { IsReadOnly = readOnly.IsChecked == true } : other)],
        }));
        var remove = Ui.Button("trash", "Entfernen", "MatButton.Danger");
        remove.IsEnabled = editable;
        remove.Margin = new Thickness(16, 0, 0, 0);
        remove.Click += (_, _) => Apply(() => SettingsService.UpdateRemote(remote => remote with { Shares = [.. remote.Shares.Where(other => other.Name != share.Name)] }));
        var text = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = share.Name, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = share.LocalPath, TextTrimming = TextTrimming.PathSegmentEllipsis }.Themed("MatText.Muted"),
            },
        };
        var tile = Ui.IconTile("folder", 36, 18);
        tile.Margin = new Thickness(0, 0, 14, 0);
        var row = new DockPanel { Children = { Ui.Docked(tile, Dock.Left), Ui.Docked(remove, Dock.Right), Ui.Docked(readOnly, Dock.Right), text } };
        return new Border { Child = row, Padding = new Thickness(14, 10) }.Themed("MatCard");
    }

    private Control PeerRow(RemotePeer peer, bool editable)
    {
        var remove = Ui.Button("x", "Entfernen", "MatButton.Danger");
        remove.IsEnabled = editable;
        remove.Click += async (_, _) =>
        {
            if (!await ConfirmDialog.AskAsync(Ui.WindowOf(this), "Kopplung entfernen?",
                    $"{peer.DeviceName} kann sich danach nicht mehr mit diesem Computer verbinden, bis er neu gekoppelt wird.", "Entfernen", isDestructive: true))
            {
                return;
            }

            try
            {
                RemoteServer.RemovePeer(peer.KeyId);
                RemoteMessage.Show($"{peer.DeviceName} ist nicht mehr gekoppelt.", InfoSeverity.Success);
            }
            catch (Exception exception) when (exception is BusinessException or NotImplementedException)
            {
                RemoteMessage.Show(exception is NotImplementedException ? "Das Entfernen ist in dieser Version noch nicht verfügbar." : exception.Message, InfoSeverity.Error);
            }

            Load();
        };
        var seen = peer.LastSeen is { } last ? $" · zuletzt {Formats.Ago(last)}" : "";
        var text = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = peer.DeviceName, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = $"gekoppelt {Formats.When(peer.PairedAt)}{seen}" }.Themed("MatText.Muted"),
            },
        };
        var tile = Ui.IconTile("monitor", 36, 18);
        tile.Margin = new Thickness(0, 0, 14, 0);
        var row = new DockPanel { Children = { Ui.Docked(tile, Dock.Left), Ui.Docked(remove, Dock.Right), text } };
        return new Border { Child = row, Padding = new Thickness(14, 10) }.Themed("MatCard");
    }

    /// <summary>Whether the Remote server listens (false while on: the port is taken); the snapshots have no server and count as listening.</summary>
    private static bool SafeIsListening()
    {
        try
        {
            return RemoteServer.IsListening || Snapshot.IsDrawing;
        }
        catch (NotImplementedException)
        {
            return true;
        }
    }

    private static string SafeFingerprint()
    {
        try
        {
            return RemoteServer.Fingerprint;
        }
        catch (NotImplementedException)
        {
            return "";
        }
    }

    private void LoadCloud()
    {
        var cloud = SettingsService.Get().Cloud;
        var status = CloudSyncService.Status;
        CloudLogin.IsVisible = !cloud.IsConnected;
        CloudConnected.IsVisible = cloud.IsConnected;
        CloudLogin.IsEnabled = !AccessPolicy.IsCloudDisabled;
        if (AccessPolicy.IsCloudDisabled)
        {
            CloudMessage.Show("Von deiner Organisation festgelegt: die Cloud-Synchronisierung ist ausgeschaltet (Richtlinie DisableCloud).", InfoSeverity.Warning);
        }
        else if (status.State == CloudState.SignedOut)
        {
            CloudMessage.Show(status.Message, InfoSeverity.Warning);
        }

        if (!cloud.IsConnected)
        {
            if (string.IsNullOrEmpty(ServerUrlBox.Text))
            {
                ServerUrlBox.Text = cloud.ServerUrl;
            }

            if (string.IsNullOrEmpty(UserNameBox.Text))
            {
                UserNameBox.Text = cloud.UserName;
            }

            if (string.IsNullOrEmpty(DeviceNameBox.Text))
            {
                DeviceNameBox.Text = cloud.DeviceName.Length > 0 ? cloud.DeviceName : AppInfo.MachineName;
            }

            return;
        }

        CloudServerText.Text = cloud.ServerUrl;
        CloudAccountText.Text = cloud.DisplayName.Length > 0 && cloud.DisplayName != cloud.UserName ? $"{cloud.DisplayName} ({cloud.UserName})" : cloud.UserName;
        CloudDeviceText.Text = cloud.DeviceName;
        CloudStateText.Text = status.Message;
        CloudLastSyncText.Text = status.LastSync is { } last ? Formats.When(last) : "–";
    }

    private void Apply(Action change)
    {
        if (_isLoading)
        {
            return;
        }

        try
        {
            FormErrors.ShowError(this, null, "");
            PageMessage.Hide();
            change();
        }
        catch (BusinessException exception)
        {
            ShowError(exception.Message, exception.Field);
            Load();
        }
    }

    private void ShowError(string message, string? field)
    {
        if (!FormErrors.ShowError(this, field, message))
        {
            PageMessage.Show(message, InfoSeverity.Error);
        }
    }

    private static int Whole(NumericUpDown box) => (int)Math.Round(box.Value ?? 0);

    // ---------------------------------------------------------------------------------------------------------------- Remote

    private async void OnPickShareFolderClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanPickFolder: true } storage)
        {
            return;
        }

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Ordner freigeben" });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
        {
            SharePathBox.Text = path;
            if (string.IsNullOrWhiteSpace(ShareNameBox.Text))
            {
                ShareNameBox.Text = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
        }
    }

    private void OnAddShareClick(object? sender, RoutedEventArgs e)
    {
        var share = new RemoteShare { Name = (ShareNameBox.Text ?? "").Trim(), LocalPath = (SharePathBox.Text ?? "").Trim() };
        try
        {
            FormErrors.ShowError(this, null, "");
            SettingsService.UpdateRemote(remote => remote with { Shares = [.. remote.Shares, share] });
            ShareNameBox.Text = SharePathBox.Text = "";
            RemoteMessage.Show($"„{share.Name}“ ist freigegeben.", InfoSeverity.Success);
        }
        catch (BusinessException exception)
        {
            if (!FormErrors.ShowError(this, exception.Field, exception.Message))
            {
                RemoteMessage.Show(exception.Message, InfoSeverity.Error);
            }
        }
    }

    private void OnPairingClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            ShowPairingCode(RemoteServer.CreatePairingCode());
        }
        catch (BusinessException exception)
        {
            RemoteMessage.Show(exception.Message, InfoSeverity.Error);
        }
        catch (NotImplementedException)
        {
            RemoteMessage.Show("Das Koppeln ist in dieser Version noch nicht verfügbar.", InfoSeverity.Warning);
        }
    }

    /// <summary>The code another computer enters, big, with the time it ends.</summary>
    internal void ShowPairingCode(PairingCode code)
    {
        PairingCodeText.Text = code.Code;
        PairingExpiryText.Text = $"Gilt bis {code.ExpiresAt.ToLocalTime().ToString("HH:mm", Formats.German)} Uhr. Ein neuer Code macht diesen ungültig.";
        PairingCard.IsVisible = true;
    }

    // ---------------------------------------------------------------------------------------------------------------- Erweitert

    private void OnSaveOAuthClick(object? sender, RoutedEventArgs e) => Apply(() =>
    {
        SettingsService.Update(settings => settings with
        {
            OAuthClients = new OAuthClientSettings
            {
                GoogleClientId = (GoogleIdBox.Text ?? "").Trim(),
                GoogleClientSecret = (GoogleSecretBox.Text ?? "").Trim(),
                MicrosoftClientId = (MicrosoftIdBox.Text ?? "").Trim(),
                DropboxClientId = (DropboxIdBox.Text ?? "").Trim(),
                BoxClientId = (BoxIdBox.Text ?? "").Trim(),
                BoxClientSecret = (BoxSecretBox.Text ?? "").Trim(),
            },
        });
        PageMessage.Show("Die App-Registrierungen sind gespeichert.", InfoSeverity.Success);
    });

    private async void OnResetClick(object? sender, RoutedEventArgs e)
    {
        if (!await ConfirmDialog.AskAsync(Ui.WindowOf(this), "Einstellungen zurücksetzen?",
                "Erscheinungsbild, Mitteilungen, Übertragung, Updates und App-Registrierungen gehen auf den Anfang. Aufträge, Verbindungen, Cloud und Remote bleiben.",
                "Zurücksetzen", isDestructive: true))
        {
            return;
        }

        Apply(() =>
        {
            SettingsService.Update(_ => new AppSettings());
            PageMessage.Show("Die Einstellungen sind zurückgesetzt.", InfoSeverity.Success);
        });
    }

    private void OnOpenDataFolderClick(object? sender, RoutedEventArgs e) => Ui.OpenExternal(AppPaths.DataDirectory);

    private void OnOpenLogFolderClick(object? sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.LogDirectory);
        Ui.OpenExternal(AppPaths.LogDirectory);
    }

    // ---------------------------------------------------------------------------------------------------------------- Cloud

    /// <summary>The first way: MatCloud shows a code, the browser opens, the person confirms it there; ThixSync waits (Abbrechen ends it).</summary>
    private async void OnBrowserConnectClick(object? sender, RoutedEventArgs e)
    {
        if (_browserSignIn is not null)
        {
            return;
        }

        CloudMessage.Hide();
        FormErrors.ShowError(this, null, "");
        _browserSignIn = new CancellationTokenSource();
        ServerUrlBox.IsEnabled = DeviceNameBox.IsEnabled = BrowserConnectButton.IsEnabled = ShowPasswordSignInButton.IsEnabled = false;
        try
        {
            if (await CloudSyncService.ConnectInBrowserAsync(ServerUrlBox.Text ?? "", DeviceNameBox.Text ?? "", start => Ui.Post(() => ShowBrowserCode(start)),
                    Ui.OpenExternal, _browserSignIn.Token))
            {
                CloudMessage.Show("Verbunden. Deine Aufträge und Verbindungen werden jetzt mit der Cloud abgeglichen.", InfoSeverity.Success);
            }
        }
        catch (BusinessException exception)
        {
            if (!FormErrors.ShowError(this, exception.Field, exception.Message))
            {
                CloudMessage.Show(exception.Message, InfoSeverity.Error);
            }
        }
        finally
        {
            _browserSignIn.Dispose();
            _browserSignIn = null;
            ServerUrlBox.IsEnabled = DeviceNameBox.IsEnabled = BrowserConnectButton.IsEnabled = ShowPasswordSignInButton.IsEnabled = true;
            ShowSignInWay(password: false);
            LoadCloud();
        }
    }

    /// <summary>The code of a started sign-in in the browser, big, with the hint to confirm it there.</summary>
    internal void ShowBrowserCode(DeviceLinkStart start)
    {
        _browserAddress = start.VerificationUrl;
        BrowserCodeText.Text = start.Code;
        BrowserSignIn.IsVisible = PasswordSignIn.IsVisible = false;
        BrowserCode.IsVisible = true;
        FocusAfterLayout(CancelLinkButton);
    }

    /// <summary>The way to sign in: in the browser (first) or with user name and password (second).</summary>
    internal void ShowSignInWay(bool password)
    {
        BrowserCode.IsVisible = false;
        BrowserSignIn.IsVisible = !password;
        PasswordSignIn.IsVisible = password;
    }

    private void OnShowPasswordSignInClick(object? sender, RoutedEventArgs e)
    {
        ShowSignInWay(password: true);
        FocusAfterLayout(string.IsNullOrEmpty(UserNameBox.Text) ? UserNameBox : PasswordBox);
    }

    private void OnShowBrowserSignInClick(object? sender, RoutedEventArgs e)
    {
        ShowSignInWay(password: false);
        FocusAfterLayout(BrowserConnectButton);
    }

    /// <summary>A control that has just become visible takes the focus once it is laid out (before that it cannot).</summary>
    private static void FocusAfterLayout(Control control) =>
        Dispatcher.UIThread.Post(() => control.Focus(NavigationMethod.Tab), DispatcherPriority.Loaded);

    private void OnOpenLinkClick(object? sender, RoutedEventArgs e)
    {
        if (_browserAddress is not null)
        {
            Ui.OpenExternal(_browserAddress);
        }
    }

    private void OnCancelLinkClick(object? sender, RoutedEventArgs e) => _browserSignIn?.Cancel();

    /// <summary>The second way: user name and password of a local account of MatCloud.</summary>
    private async void OnConnectClick(object? sender, RoutedEventArgs e)
    {
        ConnectButton.IsEnabled = false;
        CloudMessage.Hide();
        try
        {
            FormErrors.ShowError(this, null, "");
            await CloudSyncService.ConnectAsync(ServerUrlBox.Text ?? "", UserNameBox.Text ?? "", PasswordBox.Text ?? "", DeviceNameBox.Text ?? "");
            PasswordBox.Text = "";
            CloudMessage.Show("Verbunden. Deine Aufträge und Verbindungen werden jetzt mit der Cloud abgeglichen.", InfoSeverity.Success);
        }
        catch (BusinessException exception)
        {
            if (!FormErrors.ShowError(this, exception.Field, exception.Message))
            {
                CloudMessage.Show(exception.Message, InfoSeverity.Error);
            }
        }
        finally
        {
            ConnectButton.IsEnabled = true;
            LoadCloud();
        }
    }

    private void OnSyncNowClick(object? sender, RoutedEventArgs e) => CloudSyncService.Kick();

    private void OnOpenCloudClick(object? sender, RoutedEventArgs e) => Ui.OpenExternal(SettingsService.Get().Cloud.ServerUrl);

    private async void OnDisconnectClick(object? sender, RoutedEventArgs e)
    {
        if (!await ConfirmDialog.AskAsync(Ui.WindowOf(this), "Von der Cloud abmelden?",
                "Dieser Computer wird aus deinem Cloud-Konto entfernt und gleicht nicht mehr ab. Aufträge und Verbindungen auf diesem Computer bleiben erhalten.",
                "Abmelden", isDestructive: true))
        {
            return;
        }

        await CloudSyncService.DisconnectAsync();
        CloudMessage.Show("Abgemeldet.", InfoSeverity.Info);
        LoadCloud();
    }

    // ---------------------------------------------------------------------------------------------------------------- Updates

    private async void OnCheckUpdateClick(object? sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        try
        {
            ShowUpdateResult(await UpdateCoordinator.CheckAsync());
        }
        catch (BusinessException exception)
        {
            UpdateMessage.Show(exception.Message, InfoSeverity.Error);
        }
        finally
        {
            CheckUpdateButton.IsEnabled = UpdateService.CanUpdate;
            LastCheckText.Text = UpdateService.LastCheck is { } last ? Formats.When(last) : "–";
        }
    }

    private void ShowUpdateResult(UpdateCheckResult result)
    {
        _availableUpdate = result.Manifest;
        InstallUpdateButton.IsVisible = result.IsUpdateAvailable;
        UpdateMessage.Show(result.Message, result.IsUpdateAvailable ? InfoSeverity.Success : InfoSeverity.Info);
    }

    private async void OnInstallUpdateClick(object? sender, RoutedEventArgs e)
    {
        if (_availableUpdate is null)
        {
            return;
        }

        InstallUpdateButton.IsEnabled = CheckUpdateButton.IsEnabled = false;
        UpdateProgress.IsVisible = true;
        try
        {
            await UpdateCoordinator.InstallAsync(_availableUpdate, new Progress<double>(value => UpdateProgress.Value = value), showWindow: true);
        }
        catch (Exception exception) when (exception is BusinessException or HttpRequestException or IOException)
        {
            UpdateMessage.Show($"Das Update ist fehlgeschlagen: {exception.Message}", InfoSeverity.Error);
            InstallUpdateButton.IsEnabled = CheckUpdateButton.IsEnabled = true;
            UpdateProgress.IsVisible = false;
        }
    }

    private void OnReleasePageClick(object? sender, RoutedEventArgs e) => Ui.OpenExternal(UpdateService.ReleasePageUrl(AppInfo.IsLocalBuild ? "release" : AppInfo.Channel));
}
