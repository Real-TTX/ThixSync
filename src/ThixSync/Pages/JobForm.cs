using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using MatDesktop;
using ThixSync.Controls;
using ThixSync.Core.Business;
using ThixSync.Core.Infrastructure;
using ThixSync.Core.Models;
using ThixSync.Core.Storage;
using ThixSync.Dialogs;

namespace ThixSync.Pages;

/// <summary>
/// The form of a job (create or change) in tabs: Allgemein (name, description, kind with what it does, the computer that runs it), Quelle
/// &amp; Ziel (the two connections, grouped with icons, "Neue Verbindung"; the folder pairs with "Durchsuchen …"), Filter, Zeitplan (only
/// the fields of the chosen kind) and Optionen (conflicts, deletions – not for a backup –, versions, transfers, checks). Buttons in one
/// row: Speichern, Zurück … Löschen (only for a saved job, with a question). The business layer checks everything; an error opens its
/// tab and shows at its field.
/// </summary>
public sealed class JobForm : UserControl
{
    public const string GeneralTab = "general";
    public const string PairsTab = "pairs";
    public const string FilterTab = "filter";
    public const string ScheduleTab = "schedule";
    public const string OptionsTab = "options";

    private static readonly ScheduleKind[] Kinds = [ScheduleKind.Manual, ScheduleKind.Interval, ScheduleKind.Daily, ScheduleKind.Weekly];
    private static readonly ConflictPolicy[] Policies = [ConflictPolicy.KeepBoth, ConflictPolicy.Ask, ConflictPolicy.NewerWins, ConflictPolicy.SourceWins, ConflictPolicy.TargetWins];

    private readonly FormFields _fields = new();
    private readonly TextBlock _title = new();
    private readonly TextBlock _subtitle = new();
    private readonly InfoBar _message = new();
    private readonly TabControl _tabs = new();
    private readonly TabItem _generalTab = new() { Header = "Allgemein" };
    private readonly TabItem _pairsTab = new() { Header = "Quelle & Ziel" };
    private readonly TabItem _filterTab = new() { Header = "Filter" };
    private readonly TabItem _scheduleTab = new() { Header = "Zeitplan" };
    private readonly TabItem _optionsTab = new() { Header = "Optionen" };
    private readonly Button _save = Ui.Button("save", "Speichern", "MatButton.Primary");
    private readonly Button _back = Ui.Button("back", "Zurück");
    private readonly Button _delete = Ui.Button("trash", "Löschen", "MatButton.Danger");

    // ---- Allgemein
    private readonly TextBox _name = new();
    private readonly TextBox _description = new() { AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Height = 72 };
    private readonly ChoiceCards _mode = new("Art der Synchronisation",
    [
        new CardChoice(nameof(SyncMode.TwoWay), "refresh", "Bidirektional", "Änderungen auf beiden Seiten gehen auf die andere – neue, geänderte, gelöschte und umbenannte Dateien."),
        new CardChoice(nameof(SyncMode.Mirror), "arrow-right", "Spiegeln", "Das Ziel wird eine genaue Kopie der Quelle. Was nur im Ziel liegt, wird dort gelöscht."),
        new CardChoice(nameof(SyncMode.Backup), "archive", "Backup", "Neues und Geändertes geht ins Ziel, dort wird nie gelöscht. Alte Versionen bleiben erhalten."),
    ]);
    private readonly ComboBox _device = new() { MinWidth = 280 };

    // ---- Quelle & Ziel
    private readonly ConnectionPicker _source = new("Quelle");
    private readonly ConnectionPicker _target = new("Ziel");
    private readonly FolderPairsEditor _pairs = new();
    private readonly TextBlock _pairsHint = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };

    // ---- Filter
    private readonly TextBox _include = PatternBox("z. B. *.docx\nFotos/**/*.jpg");
    private readonly TextBox _exclude = PatternBox("z. B. *.tmp\nnode_modules/");
    private readonly ToggleSwitch _skipHidden = new();
    private readonly ToggleSwitch _skipSystem = new();
    private readonly NumericUpDown _maxSize = Number(0, 1_000_000, "MB");
    private readonly NumericUpDown _maxAge = Number(0, 36_500, "Tage");

    // ---- Zeitplan
    private readonly ComboBox _kind = new() { MinWidth = 220, ItemsSource = new[] { "Manuell", "In einem Abstand", "Täglich", "Wöchentlich" } };
    private readonly NumericUpDown _interval = Number(1, 7 * 24 * 60, "Minuten");
    private readonly TextBox _time = new() { Width = 110, Watermark = "20:00", HorizontalAlignment = HorizontalAlignment.Left };
    private readonly WeekdayPicker _days = new();
    private readonly ToggleSwitch _onChange = new();
    private readonly NumericUpDown _delay = Number(1, 3600, "Sekunden");
    private readonly NumericUpDown _poll = Number(1, 24 * 60, "Minuten");
    private readonly ToggleSwitch _atStartup = new();
    private readonly ToggleSwitch _catchUp = new();
    private readonly MatFormField _intervalField;
    private readonly MatFormField _timeField;
    private readonly MatFormField _daysField;
    private readonly Control _changeFields;
    private readonly Control _catchUpRow;

    // ---- Optionen
    private readonly ComboBox _conflicts = new() { MinWidth = 240, ItemsSource = new[] { "Beide behalten (umbenennen)", "Mich fragen", "Neuere Datei gewinnt", "Quelle gewinnt", "Ziel gewinnt" } };
    private readonly ToggleSwitch _propagateDeletes = new();
    private readonly ToggleSwitch _keepVersions = new();
    private readonly NumericUpDown _versionDays = Number(0, 3650, "Tage");
    private readonly NumericUpDown _versionCount = Number(0, 1000, "Versionen");
    private readonly NumericUpDown _parallelism = Number(0, JobService.MaxParallelism, "");
    private readonly ToggleSwitch _detectMoves = new();
    private readonly ToggleSwitch _verify = new();
    private readonly ToggleSwitch _compareContent = new();
    private readonly NumericUpDown _safety = Number(0, 100, "%");
    private readonly NumericUpDown _tolerance = Number(0, 7200, "Sekunden");
    private readonly ToggleSwitch _preserveTime = new();
    private readonly ToggleSwitch _emptyFolders = new();
    private readonly Control _deletesRow;
    private readonly Control _safetyField;
    private readonly Control _versionFields;

    private SyncJob _job = new();
    private bool _isExisting;
    private bool _isLoading;

    public JobForm()
    {
        this.Use(BackgroundProperty, "Mat.Surface");
        _title.Themed("MatText.PageTitle");
        _subtitle.Themed("MatText.Muted");
        _pairsHint.Themed("MatText.Muted");
        AutomationProperties.SetName(_name, "Name");
        AutomationProperties.SetName(_description, "Beschreibung");
        AutomationProperties.SetName(_device, "Läuft automatisch auf");
        AutomationProperties.SetName(_kind, "Zeitplan");
        AutomationProperties.SetName(_time, "Uhrzeit");
        AutomationProperties.SetName(_conflicts, "Konflikte");

        _intervalField = _fields.Add(nameof(JobSchedule.IntervalMinutes), "Abstand", _interval, "Alle so viele Minuten (höchstens 7 Tage).", _scheduleTab);
        _timeField = _fields.Add(nameof(JobSchedule.TimeOfDay), "Uhrzeit", _time, "Ortszeit dieses Computers, z. B. 20:00.", _scheduleTab);
        _daysField = _fields.Add(nameof(JobSchedule.Days), "Wochentage", _days, null, _scheduleTab);
        _changeFields = FormFields.Pair(
            _fields.Add(nameof(JobSchedule.ChangeDelaySeconds), "Wartezeit nach Änderungen", _delay, "Viele kleine Änderungen werden ein Lauf.", _scheduleTab),
            _fields.Add(nameof(JobSchedule.PollMinutes), "Entfernte Seite prüfen alle", _poll, "Lokale Ordner werden beobachtet, entfernte so oft angesehen.", _scheduleTab));
        _changeFields.Margin = new Thickness(0, 12, 0, 0);
        _catchUpRow = FormFields.SwitchRow("Verpasste Läufe nachholen", "War der Computer zur geplanten Zeit aus, läuft der Auftrag beim nächsten Start.", _catchUp, isLast: true);
        _deletesRow = FormFields.SwitchRow("Löschungen übertragen", "Was auf einer Seite gelöscht wurde, wird auch auf der anderen gelöscht (in die Versionen, wenn sie behalten werden).", _propagateDeletes);
        _safetyField = _fields.Add(nameof(JobOptions.SafetyDeletePercent), "Sicherheitsgrenze für Löschungen", _safety,
            "Würde ein Lauf mehr als diesen Anteil der Dateien einer Seite löschen, hält er an und fragt (0: nie fragen).", _optionsTab);
        _versionFields = FormFields.Pair(
            _fields.Add(nameof(JobOptions.KeepVersionsDays), "Versionen aufbewahren", _versionDays, "0: für immer.", _optionsTab),
            _fields.Add(nameof(JobOptions.KeepVersionsCount), "Höchstens je Datei", _versionCount, "0: keine Grenze.", _optionsTab));
        _versionFields.Margin = new Thickness(0, 12, 0, 0);

        _generalTab.Content = Scroll(GeneralContent());
        _pairsTab.Content = Scroll(PairsContent());
        _filterTab.Content = Scroll(FilterContent());
        _scheduleTab.Content = Scroll(ScheduleContent());
        _optionsTab.Content = Scroll(OptionsContent());
        foreach (var tab in new[] { _generalTab, _pairsTab, _filterTab, _scheduleTab, _optionsTab })
        {
            _tabs.Items.Add(tab);
        }

        _mode.SelectionChanged += (_, _) => ApplyVisibility();
        _kind.SelectionChanged += (_, _) => ApplyVisibility();
        _onChange.IsCheckedChanged += (_, _) => ApplyVisibility();
        _keepVersions.IsCheckedChanged += (_, _) => ApplyVisibility();
        _source.SelectionChanged += (_, _) => ApplyConnections();
        _target.SelectionChanged += (_, _) => ApplyConnections();
        _pairs.Browse = BrowseAsync;

        _save.IsDefault = true;
        _save.Click += (_, _) => Save();
        _back.Click += (_, _) => Close(null);
        ActionBar.SetSeparated(_delete, true);
        _delete.Click += async (_, _) =>
        {
            if (await JobCommands.DeleteAsync(this, _job.Id, _message.Show))
            {
                Close(null);
            }
        };

        var head = new StackPanel { Spacing = 4, Children = { _title, _subtitle } };
        var page = new DockPanel { Margin = new Thickness(36, 28, 36, 24) };
        page.Children.Add(Ui.Docked(head, Dock.Top));
        page.Children.Add(Ui.Docked(new ActionBar { Margin = new Thickness(0, 16, 0, 0), Children = { _save, _back, _delete } }, Dock.Bottom));
        page.Children.Add(Ui.Docked(_message, Dock.Top));
        _message.Margin = new Thickness(0, 14, 0, 0);
        _tabs.Margin = new Thickness(0, 14, 0, 0);
        page.Children.Add(_tabs);
        Content = page;
    }

    public TextBox NameBox => _name;

    public ChoiceCards ModeChoice => _mode;

    public ConnectionPicker SourcePicker => _source;

    public ConnectionPicker TargetPicker => _target;

    public FolderPairsEditor PairsEditor => _pairs;

    public ComboBox ScheduleKindChoice => _kind;

    public TabControl Tabs => _tabs;

    public InfoBar Message => _message;

    public Button SaveButton => _save;

    public Button DeleteButton => _delete;

    public FormFields Fields => _fields;

    /// <summary>The fields of the time of day and the weekdays (hidden unless the schedule needs them).</summary>
    public MatFormField TimeField => _timeField;

    public MatFormField DaysField => _daysField;

    public MatFormField IntervalField => _intervalField;

    /// <summary>The switch "Löschungen übertragen" (hidden for a backup).</summary>
    public Control DeletesRow => _deletesRow;

    /// <summary>The page to go back to after saving or "Zurück" (the opener).</summary>
    public Action<string?>? Closed { get; set; }

    /// <summary>Shows a job (null: a new one with the defaults of the business layer) on a tab.</summary>
    public void Load(Guid? jobId, string? tab = null)
    {
        _isLoading = true;
        try
        {
            var existing = jobId is { } id ? JobService.Find(id) : null;
            _isExisting = existing is not null;
            _job = existing ?? JobService.CreateDraft();
            _message.Hide();
            _fields.Clear();
            var locked = AccessPolicy.IsConfigurationLocked;
            _title.Text = _isExisting ? _job.Name : "Neue Synchronisation";
            _subtitle.Text = _isExisting ? JobService.DescribeMode(_job.Mode) : "Zwei Orte gleich halten oder sichern – wähle die Art, Quelle und Ziel, dann die Ordner.";
            _delete.IsVisible = _isExisting;
            _save.IsEnabled = _delete.IsEnabled = !locked;
            if (locked)
            {
                _message.Show("Von deiner Organisation festgelegt: Synchronisationen lassen sich hier nicht ändern (Richtlinie LockConfiguration).", InfoSeverity.Info);
            }

            _name.Text = _job.Name;
            _description.Text = _job.Description;
            _mode.SelectedKey = _job.Mode.ToString();
            LoadDevices();
            LoadConnections();
            _source.SelectedId = _job.SourceConnectionId;
            _target.SelectedId = _job.TargetConnectionId;
            _pairs.Pairs = _job.Pairs;

            _include.Text = string.Join(Environment.NewLine, _job.Filter.Include);
            _exclude.Text = string.Join(Environment.NewLine, _job.Filter.Exclude);
            _skipHidden.IsChecked = _job.Filter.SkipHidden;
            _skipSystem.IsChecked = _job.Filter.SkipSystemFiles;
            _maxSize.Value = _job.Filter.MaxFileSizeBytes / (1024 * 1024);
            _maxAge.Value = _job.Filter.MaxAgeDays;

            var schedule = _job.Schedule;
            _kind.SelectedIndex = Array.IndexOf(Kinds, schedule.Kind);
            _interval.Value = schedule.IntervalMinutes;
            _time.Text = schedule.TimeOfDay.ToString("HH:mm", CultureInfo.InvariantCulture);
            _days.Days = schedule.Days;
            _onChange.IsChecked = schedule.RunOnChange;
            _delay.Value = schedule.ChangeDelaySeconds;
            _poll.Value = schedule.PollMinutes;
            _atStartup.IsChecked = schedule.RunAtStartup;
            _catchUp.IsChecked = schedule.CatchUpMissed;

            var options = _job.Options;
            _conflicts.SelectedIndex = Math.Max(0, Array.IndexOf(Policies, options.Conflicts));
            _propagateDeletes.IsChecked = options.PropagateDeletes;
            _keepVersions.IsChecked = options.KeepVersions;
            _versionDays.Value = options.KeepVersionsDays;
            _versionCount.Value = options.KeepVersionsCount;
            _parallelism.Value = options.Parallelism;
            _detectMoves.IsChecked = options.DetectMoves;
            _verify.IsChecked = options.VerifyAfterCopy;
            _compareContent.IsChecked = options.CompareContent;
            _safety.Value = options.SafetyDeletePercent;
            _tolerance.Value = options.TimeToleranceSeconds;
            _preserveTime.IsChecked = options.PreserveModifiedTime;
            _emptyFolders.IsChecked = options.CopyEmptyFolders;
        }
        finally
        {
            _isLoading = false;
        }

        ApplyVisibility();
        ApplyConnections();
        SelectTab(tab ?? GeneralTab);
        Avalonia.Threading.Dispatcher.UIThread.Post(() => (tab is null or GeneralTab ? _name : (Control)_tabs).Focus(), Avalonia.Threading.DispatcherPriority.Loaded);
    }

    public void SelectTab(string tab) => _tabs.SelectedItem = tab switch
    {
        PairsTab => _pairsTab,
        FilterTab => _filterTab,
        ScheduleTab => _scheduleTab,
        OptionsTab => _optionsTab,
        _ => _generalTab,
    };

    /// <summary>The job as the fields say now (the business layer checks it when saving).</summary>
    public SyncJob Collect()
    {
        var mode = Enum.TryParse<SyncMode>(_mode.SelectedKey, out var chosen) ? chosen : SyncMode.TwoWay;
        return _job with
        {
            Name = _name.Text ?? "",
            Description = _description.Text ?? "",
            Mode = mode,
            RunsOnDevice = _device.SelectedItem as string ?? AppInfo.MachineName,
            SourceConnectionId = _source.SelectedId,
            TargetConnectionId = _target.SelectedId,
            Pairs = _pairs.Pairs,
            Filter = _job.Filter with
            {
                Include = Lines(_include.Text),
                Exclude = Lines(_exclude.Text),
                SkipHidden = _skipHidden.IsChecked == true,
                SkipSystemFiles = _skipSystem.IsChecked == true,
                MaxFileSizeBytes = Whole(_maxSize) * 1024L * 1024,
                MaxAgeDays = Whole(_maxAge),
            },
            Schedule = _job.Schedule with
            {
                Kind = Kinds[Math.Max(0, _kind.SelectedIndex)],
                IntervalMinutes = Whole(_interval),
                TimeOfDay = TimeOnly.TryParse(_time.Text, CultureInfo.GetCultureInfo("de-DE"), out var time) ? time : _job.Schedule.TimeOfDay,
                Days = _days.Days,
                RunOnChange = _onChange.IsChecked == true,
                ChangeDelaySeconds = Whole(_delay),
                PollMinutes = Whole(_poll),
                RunAtStartup = _atStartup.IsChecked == true,
                CatchUpMissed = _catchUp.IsChecked == true,
            },
            Options = _job.Options with
            {
                Conflicts = Policies[Math.Max(0, _conflicts.SelectedIndex)],
                PropagateDeletes = _propagateDeletes.IsChecked == true,
                KeepVersions = _keepVersions.IsChecked == true,
                KeepVersionsDays = Whole(_versionDays),
                KeepVersionsCount = Whole(_versionCount),
                Parallelism = Whole(_parallelism),
                DetectMoves = _detectMoves.IsChecked == true,
                VerifyAfterCopy = _verify.IsChecked == true,
                CompareContent = _compareContent.IsChecked == true,
                SafetyDeletePercent = Whole(_safety),
                TimeToleranceSeconds = Whole(_tolerance),
                PreserveModifiedTime = _preserveTime.IsChecked == true,
                CopyEmptyFolders = _emptyFolders.IsChecked == true,
            },
        };
    }

    private void Save()
    {
        _message.Hide();
        _fields.Clear();
        var needsTime = Kinds[Math.Max(0, _kind.SelectedIndex)] is ScheduleKind.Daily or ScheduleKind.Weekly;
        if (needsTime && !TimeOnly.TryParse(_time.Text, CultureInfo.GetCultureInfo("de-DE"), out _))
        {
            _fields.ShowError(nameof(JobSchedule.TimeOfDay), "Bitte eine Uhrzeit wie 20:00 eingeben.");
            return;
        }

        try
        {
            var saved = JobService.Save(Collect());
            Close($"„{saved.Name}“ wurde gespeichert.");
        }
        catch (BusinessException exception)
        {
            var field = exception.Field == nameof(SyncJob.Schedule) ? nameof(JobSchedule.Kind) : exception.Field;
            if (!_fields.ShowError(field, exception.Message))
            {
                _message.Show(exception.Message, InfoSeverity.Error);
            }
        }
    }

    private void Close(string? savedMessage)
    {
        if (Closed is { } closed)
        {
            closed(savedMessage);
            return;
        }

        Ui.MainOf(this)?.GoBack();
    }

    private Control GeneralContent()
    {
        var nameField = _fields.Add(nameof(SyncJob.Name), "Name", _name, "So erscheint die Synchronisation in der Liste, im Menü und im Protokoll.", _generalTab);
        var descriptionField = _fields.Add(nameof(SyncJob.Description), "Beschreibung", _description, "Optional: wofür sie da ist.", _generalTab);
        var modeField = _fields.Add(nameof(SyncJob.Mode), "Art", _mode, null, _generalTab);
        var deviceField = _fields.Add(nameof(SyncJob.RunsOnDevice), "Läuft automatisch auf", _device,
            "Nach Zeitplan und bei Änderungen läuft sie nur auf diesem Computer. Von Hand starten kannst du sie überall, wo Quelle und Ziel erreichbar sind.", _generalTab);
        return new StackPanel { Children = { nameField, descriptionField, modeField, deviceField } };
    }

    private Control PairsContent()
    {
        var newSource = NewConnectionButton(isSource: true);
        var newTarget = NewConnectionButton(isSource: false);
        var sourceField = _fields.Add(nameof(SyncJob.SourceConnectionId), "Quelle", new DockPanel { Children = { Ui.Docked(newSource, Dock.Right), _source } }, null, _pairsTab);
        var targetField = _fields.Add(nameof(SyncJob.TargetConnectionId), "Ziel", new DockPanel { Children = { Ui.Docked(newTarget, Dock.Right), _target } }, null, _pairsTab);
        var pairsField = _fields.Add(nameof(SyncJob.Pairs), "", _pairs, null, _pairsTab);
        return new StackPanel
        {
            Children =
            {
                sourceField,
                targetField,
                new Border { Child = _pairsHint, Margin = new Thickness(0, 0, 0, 16) },
                pairsField,
            },
        };
    }

    private Button NewConnectionButton(bool isSource)
    {
        var button = new Button { Content = new IconLabel { Icon = "plus", Text = "Neue Verbindung" }, Margin = new Thickness(8, 0, 0, 0) };
        ToolTip.SetTip(button, isSource ? "Eine neue Verbindung anlegen und als Quelle wählen" : "Eine neue Verbindung anlegen und als Ziel wählen");
        button.Click += (_, _) => Ui.MainOf(this)?.ShowConnectionForm(null, saved =>
        {
            LoadConnections();
            (isSource ? _source : _target).SelectedId = saved.Id;
        });
        return button;
    }

    private Control FilterContent()
    {
        var include = _fields.Add(nameof(JobFilter.Include), "Nur diese Dateien", _include, "Ein Muster je Zeile. Leer: alle Dateien.", _filterTab);
        var exclude = _fields.Add(nameof(JobFilter.Exclude), "Diese nie", _exclude, "Ein Muster je Zeile: *.tmp (Dateien), node_modules/ (ein Ordner), Fotos/**/*.jpg (in Unterordnern).", _filterTab);
        var size = _fields.Add(nameof(JobFilter.MaxFileSizeBytes), "Größte Datei", _maxSize, "Größere bleiben weg (0: keine Grenze).", _filterTab);
        var age = _fields.Add(nameof(JobFilter.MaxAgeDays), "Höchstens so alt", _maxAge, "Ältere bleiben weg (0: keine Grenze).", _filterTab);
        return new StackPanel
        {
            Children =
            {
                FormFields.Pair(include, exclude),
                FormFields.Pair(size, age),
                FormFields.Section("Versteckt und System"),
                FormFields.SwitchRow("Versteckte Dateien auslassen", "Dateien und Ordner, die das System als versteckt kennzeichnet.", _skipHidden),
                FormFields.SwitchRow("Systemdateien auslassen", "Thumbs.db, desktop.ini, .DS_Store und Dateien mit dem Systemattribut.", _skipSystem, isLast: true),
            },
        };
    }

    private Control ScheduleContent()
    {
        var kindField = _fields.Add(nameof(JobSchedule.Kind), "Läuft", _kind, null, _scheduleTab);
        return new StackPanel
        {
            Children =
            {
                kindField,
                _intervalField,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24, Children = { _timeField, _daysField } },
                FormFields.Section("Außerdem"),
                FormFields.SwitchRow("Bei Änderungen", "Läuft kurz nach einer Änderung in einem der Ordner.", _onChange),
                _changeFields,
                FormFields.SwitchRow("Beim Start von ThixSync", "Einmal, wenn ThixSync startet (bei der Anmeldung).", _atStartup),
                _catchUpRow,
            },
        };
    }

    private Control OptionsContent()
    {
        var conflicts = _fields.Add(nameof(JobOptions.Conflicts), "Wenn eine Datei auf beiden Seiten geändert wurde", _conflicts,
            "„Beide behalten“ benennt die Datei der anderen Seite um („Name (Konflikt ANNA-PC …)“); „Mich fragen“ wartet in den Details auf dich.", _optionsTab);
        var parallelism = _fields.Add(nameof(JobOptions.Parallelism), "Gleichzeitige Übertragungen", _parallelism, "0: automatisch – so viele, wie Quelle und Ziel gut vertragen.", _optionsTab);
        var tolerance = _fields.Add(nameof(JobOptions.TimeToleranceSeconds), "Zeittoleranz", _tolerance, "Abweichende Zeitstempel bis zu so vielen Sekunden gelten als gleich (FAT, FTP).", _optionsTab);
        return new StackPanel
        {
            Children =
            {
                conflicts,
                FormFields.Section("Löschen und Versionen"),
                _deletesRow,
                FormFields.SwitchRow("Alte Versionen behalten", "Überschriebene und gelöschte Dateien kommen in .thixsync/versions auf ihrer Seite.", _keepVersions, isLast: true),
                _versionFields,
                _safetyField,
                FormFields.Section("Übertragung"),
                FormFields.Pair(parallelism, tolerance),
                FormFields.SwitchRow("Verschiebungen erkennen", "Eine umbenannte oder verschobene Datei wird auch drüben nur verschoben, nicht neu kopiert.", _detectMoves),
                FormFields.SwitchRow("Nach dem Kopieren prüfen", "Vergleicht den Inhalt mit einer Prüfsumme (langsamer, sicherer).", _verify),
                FormFields.SwitchRow("Inhalt vergleichen", "Nicht nur Größe und Zeit: auch den Inhalt, wenn unklar ist, was passiert ist.", _compareContent),
                FormFields.SwitchRow("Zeitstempel übernehmen", "Die Änderungszeit der Quelle bleibt im Ziel erhalten, wo es möglich ist.", _preserveTime),
                FormFields.SwitchRow("Leere Ordner anlegen", "Auch Ordner ohne Dateien werden übertragen.", _emptyFolders, isLast: true),
            },
        };
    }

    /// <summary>Only the fields that apply: the schedule's own, the deletions not for a backup, the version limits when versions are kept.</summary>
    private void ApplyVisibility()
    {
        if (_isLoading)
        {
            return;
        }

        var kind = Kinds[Math.Max(0, _kind.SelectedIndex)];
        _intervalField.IsVisible = kind == ScheduleKind.Interval;
        _timeField.IsVisible = kind is ScheduleKind.Daily or ScheduleKind.Weekly;
        _daysField.IsVisible = kind == ScheduleKind.Weekly;
        _catchUpRow.IsVisible = kind != ScheduleKind.Manual;
        _changeFields.IsVisible = _onChange.IsChecked == true;

        var isBackup = _mode.SelectedKey == nameof(SyncMode.Backup);
        _deletesRow.IsVisible = !isBackup && !AccessPolicy.IsDeleteDisabled;
        _safetyField.IsVisible = !isBackup;
        _keepVersions.IsEnabled = !isBackup;
        if (isBackup)
        {
            _keepVersions.IsChecked = true;
        }

        _versionFields.IsVisible = _keepVersions.IsChecked == true;
        _pairs.SetMode(_mode.SelectedKey == nameof(SyncMode.TwoWay));
    }

    /// <summary>The hint of how folders are written on each side.</summary>
    private void ApplyConnections()
    {
        var source = _source.SelectedConnection;
        var target = _target.SelectedConnection;
        var sourceHint = source is null ? "" : ConnectionCatalog.Get(source.Kind).FolderHint;
        var targetHint = target is null ? "" : ConnectionCatalog.Get(target.Kind).FolderHint;
        _pairs.SetHints(sourceHint, targetHint);
        _pairsHint.Text = source is null || target is null
            ? "Wähle Quelle und Ziel – dann die Ordner, die gleich gehalten werden."
            : $"Ordner in „{source.Name}“ schreibst du wie {sourceHint}, in „{target.Name}“ wie {targetHint}. Leer heißt: der Basisordner der Verbindung.";
    }

    private void LoadConnections()
    {
        var connections = ConnectionService.List();
        _source.Load(connections);
        _target.Load(connections);
    }

    private void LoadDevices()
    {
        var devices = new List<string> { AppInfo.MachineName };
        if (_job.RunsOnDevice.Length > 0)
        {
            devices.Add(_job.RunsOnDevice);
        }

        devices.AddRange(ConnectionService.List().Where(connection => connection.DeviceName.Length > 0).Select(connection => connection.DeviceName));
        devices.AddRange(JobService.List().Select(job => job.RunsOnDevice).Where(device => device.Length > 0));
        var distinct = devices.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _device.ItemsSource = distinct;
        _device.SelectedItem = distinct.FirstOrDefault(device => string.Equals(device, _job.RunsOnDevice.Length > 0 ? _job.RunsOnDevice : AppInfo.MachineName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>"Durchsuchen …": the system's folder picker for this computer, the folder browser of the connection for everything else.</summary>
    private async Task<string?> BrowseAsync(bool isSource, string current)
    {
        var connection = isSource ? _source.SelectedConnection : _target.SelectedConnection;
        if (connection is null)
        {
            _fields.ShowError(isSource ? nameof(SyncJob.SourceConnectionId) : nameof(SyncJob.TargetConnectionId), "Bitte zuerst die Verbindung wählen.");
            return null;
        }

        if (connection.Kind == ConnectionKind.Local && ConnectionService.IsUsableHere(connection, out _))
        {
            if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanPickFolder: true } storage)
            {
                return null;
            }

            var start = Directory.Exists(current) ? await storage.TryGetFolderFromPathAsync(current) : null;
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = isSource ? "Ordner der Quelle wählen" : "Ordner des Ziels wählen", SuggestedStartLocation = start });
            return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        }

        return await FolderBrowserDialog.PickAsync(Ui.WindowOf(this), connection, current);
    }

    private static ScrollViewer Scroll(Control content)
    {
        content.MaxWidth = 900;
        content.HorizontalAlignment = HorizontalAlignment.Left;
        content.Margin = new Thickness(0, 4, 18, 12);
        return new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    private static TextBox PatternBox(string watermark) => new()
    {
        AcceptsReturn = true,
        Height = 140,
        Watermark = watermark,
        TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
        FontFamily = MatTheme.MonoFont(),
    };

    private static NumericUpDown Number(int minimum, int maximum, string unit)
    {
        var box = new NumericUpDown { Minimum = minimum, Maximum = maximum, Increment = 1, FormatString = "0", Width = 190, HorizontalAlignment = HorizontalAlignment.Left };
        if (unit.Length > 0)
        {
            box.InnerRightContent = new TextBlock { Text = unit, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center }.Themed("MatText.Muted");
        }

        return box;
    }

    private static int Whole(NumericUpDown box) => (int)Math.Round(box.Value ?? 0);

    private static IReadOnlyList<string> Lines(string? text) =>
        [.. (text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}
