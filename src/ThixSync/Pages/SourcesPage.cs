using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using MatDesktop;
using ThixSync.Controls;
using ThixSync.Core.Business;
using ThixSync.Core.Models;
using ThixSync.Dialogs;

namespace ThixSync.Pages;

/// <summary>
/// "Quellen": the list of sources (toolbar above it: search, kind, sort; actions below it: Neu, Bearbeiten, Analysieren, Auf der Karte …
/// Löschen) and the form of one source (tabs Allgemein | Analysen; Speichern, Zurück … Löschen). The fields of a share (user name,
/// password) are only there for a share; the folder picker only for a local source.
/// </summary>
public sealed class SourcesPage : UserControl
{
    private readonly TextBox _search = new() { Width = 240, Watermark = "Quellen suchen" };
    private readonly ComboBox _kindFilter = new() { MinWidth = 170, ItemsSource = new[] { "Alle Arten", "Laufwerke und Ordner", "Netzwerkfreigaben" }, SelectedIndex = 0 };
    private readonly ComboBox _sort = new() { MinWidth = 180, ItemsSource = new[] { "Sortiert nach Name", "Sortiert nach Größe", "Sortiert nach letzter Analyse" }, SelectedIndex = 0 };
    private readonly ListBox _list = new();
    private readonly TextBlock _count = new();
    private readonly InfoBar _message = new();
    private readonly Button _editButton = new();
    private readonly Button _scanButton = new();
    private readonly Button _mapButton = new();
    private readonly Button _deleteButton = new();
    private readonly WrapPanel _suggestions = new() { Orientation = Orientation.Horizontal };
    private readonly Control _listView;
    private readonly SourceForm _form;

    public SourcesPage()
    {
        this.Use(BackgroundProperty, "Mat.Surface");
        _form = new SourceForm();
        _form.Closed += (_, _) => ShowList();
        _listView = BuildList();
        Content = _listView;
        SourceService.Changed += (_, _) => Ui.Post(Refresh);
        AnalysisService.Changed += (_, _) => Ui.Post(Refresh);
        Refresh();
    }

    public ListBox List => _list;

    public SourceForm Form => _form;

    /// <summary>The form for a new source.</summary>
    public void ShowNew()
    {
        _form.Load(null);
        Content = _form;
    }

    public void ShowEdit(Guid id)
    {
        if (SourceService.Get(id) is { } source)
        {
            _form.Load(source);
            Content = _form;
        }
    }

    public void ShowList()
    {
        Content = _listView;
        Refresh();
    }

    private Control BuildList()
    {
        _search.Theme = Ui.Theme("MatSearchBox");
        AutomationProperties.SetName(_search, "Quellen suchen");
        AutomationProperties.SetName(_kindFilter, "Art");
        AutomationProperties.SetName(_sort, "Sortierung");
        _search.TextChanged += (_, _) => Refresh();
        _kindFilter.SelectionChanged += (_, _) => Refresh();
        _sort.SelectionChanged += (_, _) => Refresh();

        var toolbar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        foreach (var control in new Control[] { _search, _kindFilter, _sort })
        {
            control.Margin = new Thickness(0, 0, 10, 6);
            toolbar.Children.Add(control);
        }

        _list.Theme = Ui.Theme("MatList");
        _list.ItemContainerTheme = Ui.Theme("MatCardRow");
        _list.ItemTemplate = new FuncDataTemplate<ScanSource>((source, _) => source is null ? new Panel() : SourceRow(source), supportsRecycling: false);
        AutomationProperties.SetName(_list, "Quellen");
        _list.SelectionChanged += (_, _) => UpdateActions();
        _list.DoubleTapped += (_, _) =>
        {
            if (_list.SelectedItem is ScanSource source)
            {
                ShowEdit(source.Id);
            }
        };

        var newButton = new Button { Content = new IconLabel { Icon = "plus", Text = "Neue Quelle" } }.Themed("MatButton.Primary");
        newButton.Click += (_, _) => ShowNew();
        _editButton.Content = new IconLabel { Icon = "edit", Text = "Bearbeiten" };
        _editButton.Click += (_, _) =>
        {
            if (_list.SelectedItem is ScanSource source)
            {
                ShowEdit(source.Id);
            }
        };
        _scanButton.Content = new IconLabel { Icon = "refresh", Text = "Analysieren" };
        _scanButton.Click += async (_, _) =>
        {
            if (_list.SelectedItem is ScanSource source && Ui.WindowOf(this) is MainWindow window)
            {
                await window.ScanSourceAsync(source.Id);
            }
        };
        _mapButton.Content = new IconLabel { Icon = "grid", Text = "Auf der Karte zeigen" };
        _mapButton.Click += (_, _) =>
        {
            if (_list.SelectedItem is ScanSource source)
            {
                (Ui.WindowOf(this) as MainWindow)?.ShowMap(source.Id);
            }
        };
        _deleteButton.Content = new IconLabel { Icon = "trash", Text = "Löschen" };
        _deleteButton.Themed("MatButton.Danger");
        ActionBar.SetSeparated(_deleteButton, true);
        _deleteButton.Click += async (_, _) =>
        {
            if (_list.SelectedItem is ScanSource source && await ConfirmDeleteAsync(this, source))
            {
                Delete(source.Id);
            }
        };
        var actions = new ActionBar { Children = { newButton, _editButton, _scanButton, _mapButton, _deleteButton } };
        _count.Themed("MatText.Muted");
        _count.VerticalAlignment = VerticalAlignment.Center;
        _count.HorizontalAlignment = HorizontalAlignment.Right;
        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 12, 0, 0) };
        bottom.Children.Add(actions);
        Grid.SetColumn(_count, 1);
        bottom.Children.Add(_count);

        var suggestionsBox = new StackPanel
        {
            Margin = new Thickness(0, 0, 0, 8),
            Children = { new TextBlock { Text = "Laufwerke dieses Computers, die noch keine Quelle sind:", Margin = new Thickness(0, 0, 0, 6) }.Themed("MatText.Muted"), _suggestions },
        };

        var page = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*,Auto"), Margin = new Thickness(28, 22, 28, 20) };
        page.Children.Add(new TextBlock { Text = "Quellen", Margin = new Thickness(0, 0, 0, 4) }.Themed("MatText.PageTitle"));
        var intro = new TextBlock { Text = "Laufwerke, Ordner und Netzwerkfreigaben (SMB), die ThixSync analysiert. Mit der Cloud sind sie auf all deinen Computern gleich; ein Laufwerk lässt sich nur auf seinem Computer analysieren.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14), MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left }.Themed("MatText.Muted");
        Grid.SetRow(intro, 1);
        page.Children.Add(intro);
        Grid.SetRow(toolbar, 2);
        page.Children.Add(toolbar);
        var messages = new StackPanel { Children = { _message, suggestionsBox } };
        Grid.SetRow(messages, 3);
        page.Children.Add(messages);
        Grid.SetRow(_list, 4);
        page.Children.Add(_list);
        Grid.SetRow(bottom, 5);
        page.Children.Add(bottom);
        return page;
    }

    private Control SourceRow(ScanSource source)
    {
        var latest = AnalysisService.Latest(source.Id);
        var analyses = AnalysisService.List(source.Id);
        var previous = latest is null ? null : AnalysisService.Previous(latest);
        var icon = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(10), Child = new MatIcon { Icon = source.IsShare ? "network" : "hard-drive", Size = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }.Use(MatIcon.ForegroundProperty, "Mat.AccentText") };
        icon.Use(Border.BackgroundProperty, "Mat.AccentSoft");

        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(new TextBlock { Text = source.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        title.Children.Add(new Border { Child = new TextBlock { Text = SourceService.PlaceOf(source) } }.Themed("MatBadge.Soft"));

        var text = new StackPanel
        {
            Margin = new Thickness(14, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 2,
            Children =
            {
                title,
                new TextBlock { Text = source.IsShare && source.UserName.Length > 0 ? $"{source.Path}  ·  als {source.UserName}" : source.Path, TextTrimming = TextTrimming.PathSegmentEllipsis }.Themed("MatText.Muted"),
            },
        };

        var size = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        if (latest is null)
        {
            size.Children.Add(new TextBlock { Text = "Noch nicht analysiert", HorizontalAlignment = HorizontalAlignment.Right }.Themed("MatText.Muted"));
        }
        else
        {
            size.Children.Add(new TextBlock { Text = Ui.FormatSize(latest.TotalBytes), FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Right });
            var detail = $"{Ui.FormatDateTime(latest.StartedAt)} · {analyses.Count} {(analyses.Count == 1 ? "Analyse" : "Analysen")}";
            if (previous is not null)
            {
                detail = $"{Ui.FormatDelta(latest.TotalBytes - previous.TotalBytes)} · " + detail;
            }

            size.Children.Add(new TextBlock { Text = detail, FontSize = MatTokens.Type.FsXs, HorizontalAlignment = HorizontalAlignment.Right }.Themed("MatText.Muted"));
        }

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        row.Children.Add(icon);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        Grid.SetColumn(size, 2);
        row.Children.Add(size);
        return row;
    }

    private void Refresh()
    {
        var search = (_search.Text ?? "").Trim();
        IEnumerable<ScanSource> sources = SourceService.List();
        if (search.Length > 0)
        {
            sources = sources.Where(source => source.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || source.Path.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        sources = _kindFilter.SelectedIndex switch
        {
            1 => sources.Where(source => source.Kind == SourceKind.Local),
            2 => sources.Where(source => source.Kind == SourceKind.Smb),
            _ => sources,
        };
        sources = _sort.SelectedIndex switch
        {
            1 => sources.OrderByDescending(source => AnalysisService.Latest(source.Id)?.TotalBytes ?? -1),
            2 => sources.OrderByDescending(source => AnalysisService.Latest(source.Id)?.StartedAt ?? DateTimeOffset.MinValue),
            _ => sources.OrderBy(source => source.Name, StringComparer.CurrentCultureIgnoreCase),
        };

        var selected = (_list.SelectedItem as ScanSource)?.Id;
        var list = sources.ToList();
        _list.ItemsSource = list;
        _list.SelectedItem = list.FirstOrDefault(source => source.Id == selected) ?? list.FirstOrDefault();
        var all = SourceService.List().Count;
        _count.Text = list.Count == all ? $"{all} {(all == 1 ? "Quelle" : "Quellen")}" : $"{list.Count} von {all} Quellen";

        _suggestions.Children.Clear();
        foreach (var drive in SourceService.SuggestedDrives())
        {
            var add = new Button { Content = new IconLabel { Icon = "plus", Text = drive.Name }, Margin = new Thickness(0, 0, 8, 6) };
            add.Click += (_, _) =>
            {
                try
                {
                    SourceService.Create(drive);
                    _message.Hide();
                }
                catch (BusinessException exception)
                {
                    _message.Show(exception.Message, InfoSeverity.Error);
                }
            };
            _suggestions.Children.Add(add);
        }

        if (_suggestions.Parent is Control box)
        {
            box.IsVisible = _suggestions.Children.Count > 0;
        }

        UpdateActions();
    }

    private void UpdateActions()
    {
        var source = _list.SelectedItem as ScanSource;
        _editButton.IsEnabled = _deleteButton.IsEnabled = source is not null;
        _scanButton.IsEnabled = source is not null && SourceService.CanScanHere(source) && !ScanService.IsScanning;
        _mapButton.IsEnabled = source is not null && AnalysisService.Latest(source.Id) is not null;
    }

    private void Delete(Guid id)
    {
        try
        {
            SourceService.Delete(id);
            _message.Show("Die Quelle und ihre Analysen wurden gelöscht.", InfoSeverity.Success);
        }
        catch (BusinessException exception)
        {
            _message.Show(exception.Message, InfoSeverity.Error);
        }
    }

    internal static Task<bool> ConfirmDeleteAsync(Control owner, ScanSource source)
    {
        var count = AnalysisService.List(source.Id).Count;
        return ConfirmDialog.AskAsync(Ui.WindowOf(owner), "Quelle löschen?",
            $"„{source.Name}“ und {(count == 1 ? "ihre Analyse" : $"ihre {count} Analysen")} werden gelöscht – mit der Cloud auf all deinen Computern. Auf dem Laufwerk selbst wird nichts gelöscht.",
            "Löschen", isDestructive: true);
    }
}

/// <summary>
/// The form of one source (create or change) in tabs: "Allgemein" (name, kind, path; for a share the sign-in) and "Analysen" (the analyses of
/// the source). Buttons in one row: Speichern, Zurück … Löschen. The business layer checks everything; an error shows at its field.
/// </summary>
public sealed class SourceForm : UserControl
{
    private readonly TextBlock _title = new();
    private readonly TextBox _name = new();
    private readonly ComboBox _kind = new() { MinWidth = 260, ItemsSource = new[] { "Laufwerk oder Ordner", "Netzwerkfreigabe (SMB)" } };
    private readonly TextBox _path = new();
    private readonly Button _browse = new() { Content = new IconLabel { Icon = "folder-open", Text = "Auswählen …" } };
    private readonly TextBox _userName = new();
    private readonly TextBox _password = new() { PasswordChar = '●' };
    private readonly MatFormField _pathField;
    private readonly StackPanel _shareFields;
    private readonly InfoBar _message = new();
    private readonly InfoBar _deviceNote = new();
    private readonly Button _deleteButton = new();
    private readonly TabControl _tabs = new();
    private readonly TabItem _analysesTab;
    private readonly ListBox _analyses = new();
    private ScanSource? _source;

    public SourceForm()
    {
        this.Use(BackgroundProperty, "Mat.Surface");
        _title.Themed("MatText.PageTitle");
        AutomationProperties.SetName(_kind, "Art");
        _kind.SelectionChanged += (_, _) => ApplyKind();
        _browse.Click += async (_, _) => await BrowseAsync();

        _pathField = new MatFormField { Label = "Ordner oder Laufwerk", Content = new DockPanel { Children = { Docked(_browse, Dock.Right), _path } } };
        FormErrors.SetField(_pathField, nameof(SourceInput.Path));
        var nameField = new MatFormField { Label = "Name", Hint = "So erscheint die Quelle in ThixSync.", Content = _name };
        FormErrors.SetField(nameField, nameof(SourceInput.Name));
        var kindField = new MatFormField { Label = "Art", Content = _kind };
        FormErrors.SetField(kindField, nameof(SourceInput.Kind));
        var userField = new MatFormField { Label = "Benutzername", Hint = "Leer lassen für die Anmeldung dieses Computers.", Content = _userName };
        FormErrors.SetField(userField, nameof(SourceInput.UserName));
        var passwordField = new MatFormField
        {
            Label = "Passwort",
            Hint = OperatingSystem.IsMacOS() ? "Bleibt im Schlüsselbund dieses Macs, geht nie in die Cloud. Leer: der Finder fragt beim Verbinden." : "Bleibt geschützt auf diesem Computer, geht nie in die Cloud.",
            Content = _password,
        };
        FormErrors.SetField(passwordField, "Password");
        _shareFields = new StackPanel { Children = { userField, passwordField } };

        var general = new StackPanel { MaxWidth = 620, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0), Children = { _deviceNote, nameField, kindField, _pathField, _shareFields } };
        _analyses.Theme = Ui.Theme("MatList");
        _analyses.ItemContainerTheme = Ui.Theme("MatChoiceRow");
        _analyses.ItemTemplate = new FuncDataTemplate<Analysis>((analysis, _) => analysis is null ? new TextBlock() : new TextBlock { Text = $"{Ui.FormatDateTime(analysis.StartedAt)}  ·  {Ui.FormatSize(analysis.TotalBytes)}  ·  {Ui.FormatCount(analysis.FileCount)} Dateien  ·  {analysis.DeviceName}", VerticalAlignment = VerticalAlignment.Center });
        _analyses.DoubleTapped += (_, _) =>
        {
            if (_analyses.SelectedItem is Analysis analysis)
            {
                (Ui.WindowOf(this) as MainWindow)?.ShowMap(analysis.SourceId, analysis.Id);
            }
        };
        var analysesHint = new TextBlock { Text = "Doppelklick zeigt eine Analyse auf der Karte.", Margin = new Thickness(0, 12, 0, 8) }.Themed("MatText.Muted");
        _analysesTab = new TabItem { Header = "Analysen", Content = new DockPanel { Children = { Docked(analysesHint, Dock.Top), _analyses } } };
        _tabs.ItemsSource = new[] { new TabItem { Header = "Allgemein", Content = new ScrollViewer { Content = general } }, _analysesTab };

        var save = new Button { Content = new IconLabel { Icon = "save", Text = "Speichern" }, IsDefault = true }.Themed("MatButton.Primary");
        save.Click += (_, _) => Save();
        var back = new Button { Content = new IconLabel { Icon = "back", Text = "Zurück" }, IsCancel = true };
        back.Click += (_, _) => Closed?.Invoke(this, EventArgs.Empty);
        _deleteButton.Content = new IconLabel { Icon = "trash", Text = "Löschen" };
        _deleteButton.Themed("MatButton.Danger");
        ActionBar.SetSeparated(_deleteButton, true);
        _deleteButton.Click += async (_, _) =>
        {
            if (_source is { } source && await SourcesPage.ConfirmDeleteAsync(this, source))
            {
                try
                {
                    SourceService.Delete(source.Id);
                    Closed?.Invoke(this, EventArgs.Empty);
                }
                catch (BusinessException exception)
                {
                    _message.Show(exception.Message, InfoSeverity.Error);
                }
            }
        };

        var page = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), Margin = new Thickness(28, 22, 28, 20) };
        page.Children.Add(_title);
        Grid.SetRow(_message, 1);
        _message.Margin = new Thickness(0, 12, 0, 0);
        page.Children.Add(_message);
        Grid.SetRow(_tabs, 2);
        _tabs.Margin = new Thickness(0, 8, 0, 0);
        page.Children.Add(_tabs);
        var actions = new ActionBar { Margin = new Thickness(0, 14, 0, 0), Children = { save, back, _deleteButton } };
        Grid.SetRow(actions, 3);
        page.Children.Add(actions);
        Content = page;
    }

    /// <summary>The form was left (saved, back, deleted).</summary>
    public event EventHandler? Closed;

    public TextBox NameBox => _name;

    public TextBox PathBox => _path;

    public ComboBox KindChoice => _kind;

    /// <summary>The fields of a share (hidden for a local source).</summary>
    public Control ShareFields => _shareFields;

    public InfoBar Message => _message;

    public void Load(ScanSource? source)
    {
        _source = source;
        _message.Hide();
        FormErrors.ShowError(this, null, "");
        _title.Text = source is null ? "Neue Quelle" : source.Name;
        _name.Text = source?.Name ?? "";
        _kind.SelectedIndex = (int)(source?.Kind ?? SourceKind.Local);
        _path.Text = source?.Path ?? "";
        _userName.Text = source?.UserName ?? "";
        _password.Text = "";
        _password.Watermark = source?.HasPassword == true ? "gespeichert – leer lassen, um es zu behalten" : "";
        _deleteButton.IsVisible = source is not null;
        _analysesTab.IsVisible = source is not null;
        _analyses.ItemsSource = source is null ? [] : AnalysisService.List(source.Id);
        _tabs.SelectedIndex = 0;
        var foreign = source is { Kind: SourceKind.Local } && !SourceService.CanScanHere(source);
        _path.IsEnabled = _browse.IsEnabled = !foreign;
        if (foreign)
        {
            _deviceNote.Show($"Diese Quelle gehört zu {source!.DeviceName}. Den Pfad kannst du nur dort ändern, den Namen hier.", InfoSeverity.Info);
        }
        else
        {
            _deviceNote.Hide();
        }

        ApplyKind();
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _name.Focus(), Avalonia.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>A share shows its sign-in and an address; a local source the folder picker.</summary>
    private void ApplyKind()
    {
        var isShare = _kind.SelectedIndex == (int)SourceKind.Smb;
        _shareFields.IsVisible = isShare;
        _browse.IsVisible = !isShare;
        _pathField.Label = isShare ? "Adresse der Freigabe" : "Ordner oder Laufwerk";
        _pathField.Hint = isShare
            ? (OperatingSystem.IsWindows() ? @"z. B. \\nas\daten oder \\192.168.1.10\fotos\2026" : "z. B. smb://nas/daten oder smb://192.168.1.10/fotos/2026")
            : (OperatingSystem.IsWindows() ? @"z. B. C:\ oder D:\Projekte" : "z. B. / oder /Users/anna/Projekte");
        _path.Watermark = isShare ? (OperatingSystem.IsWindows() ? @"\\server\freigabe" : "smb://server/freigabe") : "";
    }

    private async Task BrowseAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanPickFolder: true } storage)
        {
            return;
        }

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Ordner oder Laufwerk wählen", AllowMultiple = false });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
        {
            _path.Text = path;
            if (string.IsNullOrWhiteSpace(_name.Text))
            {
                var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                _name.Text = string.IsNullOrEmpty(name) ? path : name;
            }
        }
    }

    private void Save()
    {
        var kind = (SourceKind)Math.Max(0, _kind.SelectedIndex);
        var password = _password.Text ?? "";
        var input = new SourceInput(_name.Text ?? "", kind, _path.Text ?? "", _userName.Text ?? "",
            kind == SourceKind.Smb && password.Length > 0 ? password : null);
        try
        {
            FormErrors.ShowError(this, null, "");
            _message.Hide();
            if (_source is null)
            {
                SourceService.Create(input);
            }
            else
            {
                SourceService.Update(_source.Id, input);
            }

            _password.Text = "";
            Closed?.Invoke(this, EventArgs.Empty);
        }
        catch (BusinessException exception)
        {
            if (!FormErrors.ShowError(this, exception.Field, exception.Message))
            {
                _message.Show(exception.Message, InfoSeverity.Error);
            }
        }
    }

    private static Control Docked(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        if (dock == Dock.Right)
        {
            control.Margin = new Thickness(8, 0, 0, 0);
        }

        return control;
    }
}
