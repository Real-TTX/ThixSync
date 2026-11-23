using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MatDesktop;
using ThixSync.Controls;
using ThixSync.Core.Business;
using ThixSync.Core.Infrastructure;
using ThixSync.Core.Models;
using ThixSync.Core.Storage;
using ThixSync.Dialogs;
using ThixSync.Pages;

[assembly: AvaloniaTestApplication(typeof(ThixSync.UiTests.UiTestApp))]

namespace ThixSync.UiTests;

/// <summary>The real app (App.axaml, themes, pages) rendered headless with Skia: real pixels, no screen needed.</summary>
public static class UiTestApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// The main window driven like a user would (the sections, the forms, the toolbars, the pages of a table, deleting) and screenshots of every
/// page in light and dark (set THIXSYNC_SCREENSHOTS to a folder to keep them; docs/images has the ones of the README, from the snapshot command).
/// </summary>
public sealed class UiTests
{
    private static readonly string? ShotFolder = Environment.GetEnvironmentVariable("THIXSYNC_SCREENSHOTS");

    [AvaloniaFact]
    public void Every_page_renders_in_light_and_dark_and_the_keys_switch_the_sections()
    {
        using var data = new TestData();
        SampleSync.Create();
        var window = Open();
        foreach (var theme in new[] { Avalonia.Styling.ThemeVariant.Light, Avalonia.Styling.ThemeVariant.Dark })
        {
            Application.Current!.RequestedThemeVariant = theme;
            window.KeyPressQwerty(PhysicalKey.Digit1, CommandModifier);
            Settle(window);
            Assert.IsType<JobsPage>(window.CurrentPage);
            Assert.Equal(4, window.JobsPage.Cards.Count);
            Save(window, $"jobs-{theme}");
            window.ShowActivity();
            Settle(window);
            Save(window, $"activity-{theme}");
            window.ShowJobs(JobsPage.OverviewTab);
            Settle(window);
            Assert.Equal("4", window.JobsPage.Overview.JobsValue);
            Save(window, $"overview-{theme}");

            window.KeyPressQwerty(PhysicalKey.Digit2, CommandModifier);
            Settle(window);
            Assert.IsType<ConnectionsPage>(window.CurrentPage);
            Assert.Equal(10, window.ConnectionsPage.List.ItemCount);
            Save(window, $"connections-{theme}");
            window.KeyPressQwerty(PhysicalKey.Digit3, CommandModifier);
            Settle(window);
            Assert.IsType<SchedulesPage>(window.CurrentPage);
            Assert.Equal(4, window.SchedulesPage.Table.ItemCount);
            Save(window, $"schedules-{theme}");
            window.KeyPressQwerty(PhysicalKey.Digit4, CommandModifier);
            Settle(window);
            Assert.IsType<ProtocolPage>(window.CurrentPage);
            Save(window, $"protocol-{theme}");
            window.KeyPressQwerty(PhysicalKey.Comma, CommandModifier);
            Settle(window);
            Assert.IsType<SettingsPage>(window.CurrentPage);
            foreach (var tab in new[] { SettingsPage.TransferTab, SettingsPage.RemoteTab, SettingsPage.CloudTab, SettingsPage.UpdatesTab, SettingsPage.AdvancedTab })
            {
                window.SettingsPage.SelectTab(tab);
                Settle(window);
                Save(window, $"settings-{tab}-{theme}");
            }

            window.ShowJobDetails(SampleSync.ProjectsJobId);
            window.JobDetailsPage.ShowPlan(SampleSync.Plan());
            Settle(window);
            Save(window, $"details-{theme}");
            window.ShowRun(SampleSync.DocumentsRunId);
            Settle(window);
            Save(window, $"run-{theme}");

            // Esc goes back where the page was opened from, and from a section to the start page
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Settle(window);
            Assert.IsType<JobDetailsPage>(window.CurrentPage);
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Settle(window);
            Assert.IsType<SettingsPage>(window.CurrentPage);
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Settle(window);
            Assert.IsType<JobsPage>(window.CurrentPage);
        }

        Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Default;
        window.Close();
    }

    [AvaloniaFact]
    public void The_job_form_creates_a_job_shows_errors_at_their_fields_and_deletes_it_after_a_question()
    {
        using var data = new TestData();
        var source = Directory.CreateDirectory(Path.Combine(data.Folder, "Dokumente")).FullName;
        var target = Directory.CreateDirectory(Path.Combine(data.Folder, "Sicherung")).FullName;
        var window = Open();

        // Ctrl+N: a new job with this computer as its source
        window.KeyPressQwerty(PhysicalKey.N, CommandModifier);
        Settle(window);
        var form = Assert.IsType<JobForm>(window.CurrentPage);
        Assert.False(form.DeleteButton.IsVisible, "a new job cannot be deleted");
        Assert.Equal(ConnectionService.EnsureThisComputer().Id, form.SourcePicker.SelectedId);

        // Enter without a name: the error at the name
        form.NameBox.Focus();
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Settle(window);
        Assert.Equal("Bitte einen Namen eingeben.", form.Fields.Find(nameof(SyncJob.Name))!.Error);
        Assert.Empty(JobService.List());

        // a name, but no target: the error opens the tab "Quelle & Ziel" and shows at the target
        form.NameBox.Text = "Dokumente sichern";
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Settle(window);
        Assert.Equal("Bitte ein Ziel wählen.", form.Fields.Find(nameof(SyncJob.TargetConnectionId))!.Error);
        Assert.Equal(1, form.Tabs.SelectedIndex);
        Save(window, "job-form-error");

        // the target, but no folders: the error at the pairs
        form.TargetPicker.SelectedId = ConnectionService.EnsureThisComputer().Id;
        form.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(window);
        Assert.StartsWith("Ordnerpaar 1:", form.Fields.Find(nameof(SyncJob.Pairs))!.Error);

        // a weekly schedule shows the time and the days, a manual one neither
        form.SelectTab(JobForm.ScheduleTab);
        form.ScheduleKindChoice.SelectedIndex = 3;
        Settle(window);
        Assert.True(form.TimeField.IsVisible && form.DaysField.IsVisible);
        Assert.False(form.IntervalField.IsVisible);
        form.ScheduleKindChoice.SelectedIndex = 0;
        Settle(window);
        Assert.False(form.TimeField.IsVisible || form.DaysField.IsVisible);

        // a backup never deletes: the switch is not there
        form.SelectTab(JobForm.OptionsTab);
        form.ModeChoice.SelectedKey = nameof(SyncMode.Backup);
        Settle(window);
        Assert.False(form.DeletesRow.IsVisible);
        form.ModeChoice.SelectedKey = nameof(SyncMode.Mirror);
        Settle(window);
        Assert.True(form.DeletesRow.IsVisible);

        form.PairsEditor.SourceBox(0).Text = source;
        form.PairsEditor.TargetBox(0).Text = target;
        form.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(window);
        var job = Assert.Single(JobService.List());
        Assert.Equal(("Dokumente sichern", SyncMode.Mirror), (job.Name, job.Mode));
        Assert.IsType<JobsPage>(window.CurrentPage);
        Assert.Contains("gespeichert", window.JobsPage.Message.Text);
        Assert.Single(window.JobsPage.Cards);

        // deleting asks first: "Abbrechen" keeps it, "Löschen" deletes it
        window.ShowJobForm(job.Id);
        Settle(window);
        Assert.True(window.JobForm.DeleteButton.IsVisible);
        window.JobForm.DeleteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(window);
        Assert.NotNull(ConfirmDialog.Current);
        ConfirmDialog.Current!.Answer(false);
        Settle(window);
        Assert.Single(JobService.List());
        window.JobForm.DeleteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(window);
        ConfirmDialog.Current!.Answer(true);
        Settle(window);
        Assert.Empty(JobService.List());
        Assert.IsType<JobsPage>(window.CurrentPage);
        window.Close();
    }

    [AvaloniaFact]
    public void The_connection_form_shows_only_the_fields_of_the_type_that_apply()
    {
        using var data = new TestData();
        var window = Open();
        window.ShowConnections();
        Settle(window);
        window.ShowConnectionForm(null);
        Settle(window);
        var form = window.ConnectionForm;
        Assert.True(form.IsPickingType);
        Assert.Equal(ConnectionCatalog.All.Count, form.TypeButtons.Count);
        Save(window, "connection-types");

        // SFTP: its own fields, the key file only for "Schlüsseldatei", the password as the secret
        form.TypeButtons[ConnectionKind.Sftp].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(window);
        Assert.False(form.IsPickingType);
        Assert.Equal(ConnectionKind.Sftp, form.Type!.Kind);
        Assert.NotNull(form.Input(ConnectionFields.Host));
        Assert.Null(form.Input(ConnectionFields.Bucket));
        Assert.False(form.FormField(ConnectionFields.PrivateKeyFile)!.IsVisible);
        Assert.True(form.SecretFormField!.IsVisible);
        Assert.True(form.SignInTab.IsVisible && form.AdvancedTab.IsVisible);
        var auth = Assert.IsType<ComboBox>(form.Input(ConnectionFields.AuthMethod));
        auth.SelectedIndex = 1;
        Settle(window);
        Assert.True(form.FormField(ConnectionFields.PrivateKeyFile)!.IsVisible);
        Save(window, "connection-sftp");

        // errors at their field: the server first
        form.NameBox.Text = "Server";
        form.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(window);
        Assert.Equal("Bitte „Server“ angeben.", form.Fields.Find(ConnectionFields.Host)!.Error);
        ((TextBox)form.Input(ConnectionFields.Host)!).Text = "server.example.com";
        ((TextBox)form.Input(ConnectionFields.UserName)!).Text = "deploy";
        ((TextBox)((DockPanel)form.Input(ConnectionFields.PrivateKeyFile)!).Children.OfType<TextBox>().Single()).Text = "~/.ssh/id_ed25519";
        form.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(window);
        var saved = Assert.Single(ConnectionService.List());
        Assert.Equal(("Server", "key"), (saved.Name, saved.Get(ConnectionFields.AuthMethod)));
        Assert.IsType<ConnectionsPage>(window.CurrentPage);

        // a NAS: the share only for SMB; this computer: no sign-in at all
        window.ShowConnectionForm(null, kind: ConnectionKind.Synology);
        Settle(window);
        Assert.True(form.FormField(ConnectionFields.Share)!.IsVisible);
        ((ComboBox)form.Input(ConnectionFields.Protocol)!).SelectedIndex = 2;
        Settle(window);
        Assert.False(form.FormField(ConnectionFields.Share)!.IsVisible);
        window.ShowConnectionForm(null, kind: ConnectionKind.Local);
        Settle(window);
        Assert.False(form.SignInTab.IsVisible);
        Assert.False(form.AdvancedTab.IsVisible);
        Assert.Null(form.SecretFormField);
        window.Close();
    }

    [AvaloniaFact]
    public void The_toolbars_search_filter_and_sort_the_lists()
    {
        using var data = new TestData();
        SampleSync.Create();
        var window = Open();
        var jobs = window.JobsPage;
        jobs.FilterChoice.SelectedIndex = 6;
        Settle(window);
        Assert.Equal("Server Spiegel", JobService.Get(Assert.Single(jobs.Cards).JobId).Name);
        Assert.Equal("Pausiert", jobs.Cards[0].StateText);
        jobs.FilterChoice.SelectedIndex = 0;
        jobs.SearchBox.Text = "Fotos";
        Settle(window);
        Assert.Equal(2, jobs.Cards.Count);
        jobs.SearchBox.Text = "";
        jobs.SortChoice.SelectedIndex = 1;
        Settle(window);
        Assert.Equal(SampleSync.ProjectsJobId, jobs.Cards[0].JobId);
        Assert.Equal("Konflikte", jobs.Cards[0].StateText);

        window.ShowConnections();
        Settle(window);
        window.ConnectionsPage.GroupChoice.SelectedIndex = 1 + (int)ConnectionGroup.Cloud;
        Settle(window);
        Assert.Equal(3, window.ConnectionsPage.List.ItemCount);
        window.ConnectionsPage.SearchBox.Text = "macbook";
        window.ConnectionsPage.GroupChoice.SelectedIndex = 0;
        Settle(window);
        Assert.Equal(2, window.ConnectionsPage.List.ItemCount);

        window.ShowSchedules();
        Settle(window);
        window.SchedulesPage.FilterChoice.SelectedIndex = 3;
        Settle(window);
        Assert.Equal("Server Spiegel", Assert.IsType<SyncJob>(window.SchedulesPage.Table.List.Items[0]).Name);

        window.ShowProtocol();
        Settle(window);
        window.ProtocolPage.FilterChoice.SelectedIndex = 3;
        Settle(window);
        Assert.Equal(RunOutcome.Failed, Assert.Single(window.ProtocolPage.Table.List.Items.OfType<SyncRun>()).Outcome);
        window.Close();
    }

    [AvaloniaFact]
    public void The_tables_turn_their_pages()
    {
        using var data = new TestData();
        SampleSync.Create();
        var window = Open();
        window.ShowProtocol();
        Settle(window);
        var protocol = window.ProtocolPage;
        var total = protocol.Pager.TotalCount;
        Assert.True(total > ProtocolService.DefaultPageSize, $"{total} runs");
        Assert.Equal(ProtocolService.DefaultPageSize, protocol.Table.ItemCount);
        Assert.Equal("Seite 1 von 2", protocol.Pager.PositionText);
        protocol.Pager.NextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(window);
        Assert.Equal("Seite 2 von 2", protocol.Pager.PositionText);
        Assert.Equal(total - ProtocolService.DefaultPageSize, protocol.Table.ItemCount);
        Assert.False(protocol.Pager.NextButton.IsEnabled);

        // the lines of a run: 200 a page, "Nur Probleme" leaves the warnings
        window.ShowRun(SampleSync.DocumentsRunId);
        Settle(window);
        var run = window.RunPage;
        Assert.Equal(200, run.Table.ItemCount);
        run.Pager.NextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(window);
        Assert.Equal(run.Pager.TotalCount - 200, run.Table.ItemCount);
        run.OnlyProblems.IsChecked = true;
        Settle(window);
        Assert.All(run.Table.List.Items.OfType<RunEntry>(), entry => Assert.NotEqual(RunEntryLevel.Info, entry.Level));
        Assert.False(run.Pager.NextButton.IsVisible);

        // the plan of an analysis: 100 a page, the conflicts first, a conflict is decided in its row
        window.ShowJobDetails(SampleSync.ProjectsJobId);
        var details = window.JobDetailsPage;
        details.ShowPlan(SampleSync.Plan());
        Settle(window);
        Assert.Equal(PlanAction.Conflict, Assert.IsType<PlanItem>(details.PlanTable.List.Items[0]).Action);
        Assert.Equal(2, details.Plan!.OpenConflicts);
        details.Resolve((PlanItem)details.PlanTable.List.Items[0]!, ConflictChoice.KeepBoth);
        Settle(window);
        Assert.Equal(1, details.Plan!.OpenConflicts);
        details.PlanGroup.SelectedIndex = 5;
        Settle(window);
        Assert.Equal(3, details.PlanTable.ItemCount);
        window.Close();
    }

    [AvaloniaFact]
    public void A_running_job_shows_on_its_card_in_the_activity_and_in_the_title_bar()
    {
        using var data = new TestData();
        SampleSync.Create();
        var window = Open();
        Assert.False(window.RunStatusButton.IsVisible);
        foreach (var progress in SampleSync.RunningProgress())
        {
            RunMonitor.Show(progress);
        }

        Settle(window);
        Assert.True(window.RunStatusButton.IsVisible);
        Assert.Equal("2 Aufträge · 61,0 MB/s", window.RunStatusText);
        Assert.Equal("Läuft …", window.JobsPage.Cards.Single(card => card.JobId == SampleSync.ProjectsJobId).StateText);
        window.RunStatusButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(window);
        Assert.Equal(JobsPage.ActivityTab, window.JobsPage.SelectedTab);
        var panels = window.JobsPage.Activity.Panels;
        Assert.Equal(2, panels.Count);
        Assert.Equal("48,2 MB/s", panels.Single(panel => panel.JobId == SampleSync.ProjectsJobId).SpeedText);
        Assert.Equal(4, panels.Single(panel => panel.JobId == SampleSync.ProjectsJobId).TransferCount);
        Save(window, "activity-running");

        RunMonitor.Clear();
        Settle(window);
        Assert.False(window.RunStatusButton.IsVisible);
        Assert.Empty(window.JobsPage.Activity.Panels);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Starting_a_job_that_cannot_run_tells_why_instead_of_crashing()
    {
        using var data = new TestData();
        SampleSync.Create();
        var window = Open();
        var card = window.JobsPage.Cards.Single(card => card.JobId == SampleSync.ProjectsJobId);
        card.RunButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (RunMonitor.IsRunning(SampleSync.ProjectsJobId) && watch.Elapsed < TimeSpan.FromSeconds(20))
        {
            await Task.Delay(50);
            Settle(window);
        }

        Settle(window);
        Assert.False(string.IsNullOrEmpty(window.JobsPage.Message.Text));
        Assert.DoesNotContain("läuft …", window.JobsPage.Message.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void The_snapshot_draws_every_window_in_light_and_dark_at_its_natural_size()
    {
        using var data = new TestData();
        var folder = Path.Combine(data.Folder, "snapshot");
        var report = Snapshot.Render(folder);

        // Every picture is written – only the setup assistant is left out where it does not exist (macOS).
        Assert.Equal(Snapshot.FileNames.Order(), report.Written.Concat(report.Skipped.Select(skipped => skipped.File)).Order());
        Assert.All(report.Skipped, skipped => Assert.StartsWith("setup-", skipped.File));
        if (OperatingSystem.IsWindows())
        {
            Assert.Empty(report.Skipped);
        }

        foreach (var name in report.Written)
        {
            Assert.True(new FileInfo(Path.Combine(folder, name)).Length > 0, name);
        }

        // 1x, the size the windows have on screen; the pictures the launcher's store shows are there.
        Assert.Equal((1280, 820), PictureSize(folder, "main-jobs-dark.png"));
        Assert.Contains("main-jobs-light.png", report.Written);
        Assert.Contains("main-activity-light.png", report.Written);
        Assert.Contains("main-connections-light.png", report.Written);
        Assert.Equal((64, 64), PictureSize(folder, "tray-icon-windows-running.png"));

        // Clock, version and computer name run and read as before; nothing runs any more.
        Assert.Null(AppClock.Pinned);
        Assert.NotEqual(Snapshot.Version, AppInfo.Version);
        Assert.Empty(RunMonitor.Running);
    }

    [AvaloniaFact]
    public void The_version_reads_the_same_everywhere_and_opens_about()
    {
        using var data = new TestData();
        var expected = MatVersion.Format(AppInfo.Version, AppInfo.Channel);
        Assert.Matches(@"^Version \S+ · (release|nightly|local)$", expected);

        // SETUP-07: at the foot of the menu (it opens About), in About and in the footer of the setup.
        var window = Open();
        window.ShowSettings();
        Settle(window);
        window.VersionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(window);
        Assert.Equal(expected, Assert.IsType<AboutPage>(window.CurrentPage).VersionText.Text);
        window.Close();

        if (OperatingSystem.IsWindows())
        {
            foreach (var (uninstall, update) in new[] { (false, false), (false, true), (true, false) })
            {
                var setup = Setup.ThixSyncSetup.Create(uninstall, update, AppInfo.Version, @"C:\Users\Anna\AppData\Local\Programs\ThixSync", _ => Task.CompletedTask);
                Assert.Equal(expected, setup.VersionText.Text);
            }
        }
    }

    [AvaloniaFact]
    public void The_tray_menu_has_the_order_of_every_tray_and_offers_the_jobs()
    {
        using var data = new TestData();
        SampleSync.Create();
        using var tray = new Tray.TrayIcon(App.Instance);
        var entries = tray.Menu.Build();
        var roles = entries.Select(entry => entry.Role).ToList();

        // TRAY-01: name and version, the state, Open, ThixSync's own items, Settings and the log folder, Quit last; no Uninstall.
        Assert.Equal([MatTrayRole.Title, MatTrayRole.State, MatTrayRole.Separator, MatTrayRole.Open, MatTrayRole.Separator], roles.Take(5));
        Assert.Equal([MatTrayRole.Settings, MatTrayRole.LogFolder, MatTrayRole.Separator, MatTrayRole.Quit], roles.TakeLast(4));
        Assert.Equal($"ThixSync {AppInfo.Version}", entries[0].Text);
        Assert.Equal("ThixSync öffnen", entries[3].Text);
        Assert.Contains(entries, entry => entry.Role == MatTrayRole.Own && entry.Text.Trim() == "Dokumente & Fotos" && entry.IsEnabled);
        Assert.Contains(entries, entry => entry.Role == MatTrayRole.Own && entry.Text == "Alle pausieren");
        Assert.Equal("Beenden", entries[^1].Text);

        // A running job can be stopped from the menu; the state says what runs.
        RunMonitor.Show(SampleSync.RunningProgress()[0]);
        Settle();
        entries = tray.Menu.Build();
        Assert.Contains(entries, entry => entry.Text == "Anhalten: Projekte NAS");
        Assert.Contains("Projekte NAS", Tray.TrayIcon.State.Word);

        // An update waiting adds "Update installieren …" before the separator above Quit.
        tray.SetUpdateAvailable(true);
        Assert.Equal(MatTrayRole.InstallUpdate, tray.Menu.Build()[^3].Role);
    }

    [Fact]
    public void A_finished_run_is_told_as_the_setting_says()
    {
        var problem = new SyncRun { Outcome = RunOutcome.Failed, Trigger = RunTrigger.Schedule };
        var copied = new SyncRun { Outcome = RunOutcome.Success, Trigger = RunTrigger.Schedule, FilesCopied = 3 };
        var nothing = new SyncRun { Outcome = RunOutcome.Success, Trigger = RunTrigger.Schedule };
        Assert.Null(App.ShouldNotify(problem, NotifyChoice.Never, userIsWatching: false));
        Assert.Equal(RunNotice.Problem, App.ShouldNotify(problem, NotifyChoice.Problems, userIsWatching: false));
        Assert.Null(App.ShouldNotify(copied, NotifyChoice.Problems, userIsWatching: false));
        Assert.Equal(RunNotice.Changes, App.ShouldNotify(copied, NotifyChoice.Changes, userIsWatching: false));
        Assert.Null(App.ShouldNotify(nothing, NotifyChoice.Changes, userIsWatching: false));
        Assert.Null(App.ShouldNotify(problem with { Trigger = RunTrigger.Manual }, NotifyChoice.Changes, userIsWatching: true));
        Assert.Null(App.ShouldNotify(new SyncRun { Outcome = RunOutcome.Analysed }, NotifyChoice.Changes, userIsWatching: false));
    }

    [Fact]
    public void The_numbers_read_the_german_way()
    {
        Assert.Equal("3,2 GB", Formats.Bytes((long)(3.2 * 1024 * 1024 * 1024)));
        Assert.Equal("48,2 MB/s", Formats.Speed(48.2 * 1024 * 1024));
        Assert.Equal("1.204 Dateien", Formats.Files(1204));
        Assert.Equal("512 B", Formats.Bytes(512));
        Assert.Equal("Dokumente", JobCard.FolderName(@"C:\Users\Anna\Dokumente\"));
        Assert.Equal("Basisordner", JobCard.FolderName("/"));
    }

    [Fact]
    public void The_setup_assistant_asks_what_installer_json_offers_with_its_defaults()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var options = Setup.ThixSyncSetup.Options().ToDictionary(option => option.Key, option => option.IsOn);
        Assert.Equal(new Dictionary<string, bool> { ["autostart"] = true, ["shortcut"] = true, ["updates"] = true, ["cli"] = true }, options);
        Assert.Equal(new InstallChoices(true, true, true, CliPath: true), Setup.ThixSyncSetup.Choices(options));
    }

    [Fact]
    public void No_window_writes_a_colour_of_its_own()
    {
        // DESK-02: every colour comes from MatDesktop's theme; the one value ThixSync gives is its accent (App.Accent).
        var source = Path.Combine(Repository.Root, "src", "ThixSync");
        var files = Directory.EnumerateFiles(source, "*.*", SearchOption.AllDirectories)
            .Where(file => file.EndsWith(".cs", StringComparison.Ordinal) || file.EndsWith(".axaml", StringComparison.Ordinal))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}MatDesktop{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        var literal = new System.Text.RegularExpressions.Regex(@"#[0-9A-Fa-f]{6}\b|#[0-9A-Fa-f]{8}\b|\bColor\.(Parse|FromRgb|FromArgb)|\bColors\.\w+|\bBrushes\.\w+");
        var findings = files
            .SelectMany(file => File.ReadAllLines(file).Select((line, index) => (file, line, index)))
            .Where(item => literal.IsMatch(item.line) && !item.line.Contains("public const string Accent", StringComparison.Ordinal))
            .Select(item => $"{Path.GetRelativePath(source, item.file)}:{item.index + 1}: {item.line.Trim()}")
            .ToList();
        Assert.Empty(findings);
    }

    [Fact]
    public void The_copy_of_MatDesktop_is_unchanged()
    {
        // MatDesktop is copied from MatDesign (node tools/matdesktop-copy.js src/ThixSync there) and never edited here: every
        // file matches the hash its manifest names.
        var folder = Path.Combine(Repository.Root, "src", "ThixSync", "MatDesktop");
        var lines = File.ReadAllLines(Path.Combine(folder, "matdesktop.version"));
        Assert.StartsWith("MatDesktop ", lines[0]);
        foreach (var line in lines.Skip(1).Where(line => line.Length > 0))
        {
            var (hash, file) = (line[..64], line[66..]);
            var text = File.ReadAllText(Path.Combine(folder, file)).Replace("\r\n", "\n", StringComparison.Ordinal);
            var actual = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
            Assert.True(hash == actual, $"MatDesktop/{file} was changed: copy it again from MatDesign instead of editing it");
        }
    }

    [Fact]
    public void The_copy_of_the_MatCloud_contract_is_unchanged()
    {
        // The contract of MatCloud is copied from there (node ../MatCloud/tools/contract-copy.js src/ThixSync.Core) and never edited here:
        // every file matches the hash its manifest names (SHA-256 of the text with LF line ends, the header line included), and the folder
        // has no file the manifest does not name.
        var folder = Path.Combine(Repository.Root, "src", "ThixSync.Core", "MatCloud");
        var lines = File.ReadAllLines(Path.Combine(folder, "matcloud.version"));
        Assert.StartsWith("MatCloud.Contract ", lines[0]);
        var listed = new List<string>();
        foreach (var line in lines.Skip(1).Where(line => line.Length > 0))
        {
            var (hash, file) = (line[..64], line[66..]);
            listed.Add(file);
            var text = File.ReadAllText(Path.Combine(folder, file)).Replace("\r\n", "\n", StringComparison.Ordinal);
            Assert.StartsWith("// MatCloud.Contract ", text);
            var actual = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
            Assert.True(hash == actual, $"MatCloud/{file} was changed: copy the contract again from MatCloud instead of editing it");
        }

        var present = Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories).Select(file => Path.GetRelativePath(folder, file).Replace('\\', '/'));
        Assert.Equal(listed.Order(), present.Order());
    }

    private static (int Width, int Height) PictureSize(string folder, string name)
    {
        using var codec = SkiaSharp.SKCodec.Create(Path.Combine(folder, name));
        return (codec.Info.Width, codec.Info.Height);
    }

    private static RawInputModifiers CommandModifier => OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

    private static MainWindow Open(double width = 1360, double height = 860)
    {
        SettingsService.Remember(settings => settings with { WindowWidth = width, WindowHeight = height });
        var window = new MainWindow { Width = width, Height = height };
        window.ShowFromTray();
        window.Width = width;
        window.Height = height;
        Settle(window);
        return window;
    }

    private static void Settle(Window? window = null)
    {
        for (var round = 0; round < 8; round++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(15);
        }

        Dispatcher.UIThread.RunJobs();
        window?.UpdateLayout();
    }

    private static void Save(Window window, string name)
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (ShotFolder is null)
        {
            return;
        }

        Directory.CreateDirectory(ShotFolder);
        frame.Save(Path.Combine(ShotFolder, $"{name}.png"));
    }
}
