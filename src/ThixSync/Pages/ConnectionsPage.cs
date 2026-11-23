using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using MatDesktop;
using ThixSync.Controls;
using ThixSync.Core.Business;
using ThixSync.Core.Infrastructure;
using ThixSync.Core.Models;
using ThixSync.Core.Storage;
using ThixSync.Dialogs;

namespace ThixSync.Pages;

/// <summary>
/// "Geräte": every connection (this computer, drives, NAS, servers, clouds, other computers) as a card row with the icon of its type,
/// name, type, address and state ("Bereit", "Gehört zu ANNA-PC", "Anmeldung fehlt", "Nicht verfügbar"). Toolbar: search, group, sort.
/// Below: Neue Verbindung, Bearbeiten, Testen … Löschen; the result of a test shows above the list.
/// </summary>
public sealed class ConnectionsPage : UserControl
{
    private static readonly ConnectionSort[] Sorts = [ConnectionSort.Name, ConnectionSort.Type, ConnectionSort.Changed];

    private readonly ListToolbar _toolbar = new("Geräte und Verbindungen suchen");
    private readonly ComboBox _group;
    private readonly ComboBox _sort;
    private readonly ListBox _list = new();
    private readonly InfoBar _message = new();
    private readonly InfoBar _policy = new();
    private readonly TextBlock _count = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Button _new = Ui.Button("plus", "Neue Verbindung", "MatButton.Primary");
    private readonly Button _edit = Ui.Button("edit", "Bearbeiten");
    private readonly Button _test = Ui.Button("plug", "Testen");
    private readonly Button _delete = Ui.Button("trash", "Löschen", "MatButton.Danger");

    public ConnectionsPage()
    {
        this.Use(BackgroundProperty, "Mat.Surface");
        _group = _toolbar.AddChoice("Gruppe", ["Alle Gruppen", .. Enum.GetValues<ConnectionGroup>().Select(ConnectionCatalog.GroupName)], 190);
        _sort = _toolbar.AddChoice("Sortierung", ["Sortiert nach Name", "Sortiert nach Typ", "Zuletzt geändert"], 190);
        _toolbar.Changed += (_, _) => Refresh();
        _count.Themed("MatText.Muted");

        _list.Themed("MatList");
        _list.ItemContainerTheme = Ui.Theme("MatCardRow");
        _list.ItemTemplate = new FuncDataTemplate<Connection>((connection, _) => connection is null ? new Panel() : Row(connection), supportsRecycling: false);
        AutomationProperties.SetName(_list, "Verbindungen");
        _list.SelectionChanged += (_, _) => ApplyButtons();
        _list.DoubleTapped += (_, _) => Edit();

        _new.Click += (_, _) => Ui.MainOf(this)?.ShowConnectionForm(null);
        _edit.Click += (_, _) => Edit();
        _test.Click += async (_, _) => await TestAsync();
        ActionBar.SetSeparated(_delete, true);
        _delete.Click += async (_, _) =>
        {
            if (_list.SelectedItem is Connection connection)
            {
                await ConnectionForm.DeleteAsync(this, connection, _message.Show);
            }
        };

        var intro = new TextBlock
        {
            Text = "Alles, womit ThixSync abgleicht: dieser Computer und externe Laufwerke, NAS und Server, Cloud-Speicher und andere Computer mit ThixSync. Jede Verbindung kann Quelle und Ziel sein. Passwörter bleiben auf diesem Computer.",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 900,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 18),
        }.Themed("MatText.Muted");
        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 14, 0, 0) };
        bottom.Children.Add(new ActionBar { Children = { _new, _edit, _test, _delete } });
        bottom.Children.Add(Ui.Cell(_count, 0, 1));
        var page = new DockPanel { Margin = new Thickness(36, 28, 36, 24) };
        page.Children.Add(Ui.Docked(new TextBlock { Text = "Geräte" }.Themed("MatText.PageTitle"), Dock.Top));
        page.Children.Add(Ui.Docked(intro, Dock.Top));
        page.Children.Add(Ui.Docked(_toolbar, Dock.Top));
        page.Children.Add(Ui.Docked(new StackPanel { Children = { _policy, _message } }, Dock.Top));
        page.Children.Add(Ui.Docked(bottom, Dock.Bottom));
        page.Children.Add(_list);
        Content = page;

        Ui.WhileShown(this, Subscribe, Unsubscribe, Refresh);
    }

    public ListBox List => _list;

    public ComboBox GroupChoice => _group;

    public TextBox SearchBox => _toolbar.SearchBox;

    public InfoBar Message => _message;

    public Button TestButton => _test;

    /// <summary>A message above the list (after saving, deleting).</summary>
    public void ShowMessage(string text, InfoSeverity severity) => _message.Show(text, severity);

    /// <summary>The state of a connection on this computer, in a word with its icon and tone.</summary>
    public static (string Text, string Icon, Tone Tone) StateOf(Connection connection)
    {
        var type = ConnectionCatalog.Get(connection.Kind);
        if (type.IsBoundToDevice && !string.Equals(connection.DeviceName, AppInfo.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            return ($"Gehört zu {connection.DeviceName}", "monitor", Tone.Muted);
        }

        if (!AccessPolicy.IsConnectionKindAllowed(connection.Kind))
        {
            return ("Gesperrt", "lock", Tone.Danger);
        }

        if (!StorageProviders.IsSupported(connection.Kind) || !type.IsAvailable)
        {
            return ("Nicht verfügbar", "x-circle", Tone.Muted);
        }

        if (connection.Kind == ConnectionKind.MatCloud && !CloudSyncService.IsConnected)
        {
            return ("Cloud nicht verbunden", "cloud", Tone.Warning);
        }

        return ConnectionService.NeedsSecret(connection) ? ("Anmeldung fehlt", "key", Tone.Warning) : ("Bereit", "check", Tone.Success);
    }

    public void Refresh()
    {
        var locked = AccessPolicy.IsConfigurationLocked;
        if (locked)
        {
            _policy.Show("Von deiner Organisation festgelegt: Verbindungen lassen sich hier nicht anlegen oder ändern (Richtlinie LockConfiguration).", InfoSeverity.Info);
        }
        else
        {
            _policy.Hide();
        }

        ConnectionGroup? group = _group.SelectedIndex > 0 ? (ConnectionGroup)(_group.SelectedIndex - 1) : null;
        var connections = ConnectionService.Query(_toolbar.SearchText, group, Sorts[Math.Max(0, _sort.SelectedIndex)]);
        var selected = (_list.SelectedItem as Connection)?.Id;
        _list.ItemsSource = connections;
        _list.SelectedItem = connections.FirstOrDefault(connection => connection.Id == selected) ?? connections.FirstOrDefault();
        var all = ConnectionService.List().Count;
        _count.Text = connections.Count == all ? (all == 1 ? "1 Verbindung" : $"{all} Verbindungen") : $"{connections.Count} von {all} Verbindungen";
        ApplyButtons();
    }

    private void Subscribe()
    {
        ConnectionService.Changed += OnChanged;
        CloudSyncService.StatusChanged += OnCloud;
    }

    private void Unsubscribe()
    {
        ConnectionService.Changed -= OnChanged;
        CloudSyncService.StatusChanged -= OnCloud;
    }

    private void OnChanged(object? sender, EventArgs e) => Ui.Post(Refresh);

    private void OnCloud(object? sender, SyncStatus status) => Ui.Post(Refresh);

    private void ApplyButtons()
    {
        var connection = _list.SelectedItem as Connection;
        var locked = AccessPolicy.IsConfigurationLocked;
        _new.IsEnabled = !locked;
        _edit.IsEnabled = connection is not null;
        _test.IsEnabled = connection is not null && ConnectionService.IsUsableHere(connection, out _);
        _delete.IsEnabled = connection is not null && !locked;
    }

    private void Edit()
    {
        if (_list.SelectedItem is Connection connection)
        {
            Ui.MainOf(this)?.ShowConnectionForm(connection.Id);
        }
    }

    private async Task TestAsync()
    {
        if (_list.SelectedItem is not Connection connection)
        {
            return;
        }

        _test.IsEnabled = false;
        _message.Show($"Teste „{connection.Name}“ …", InfoSeverity.Info);
        var result = await ConnectionForm.TestAsync(connection, null);
        _message.Show(result.Success ? result.Message : $"„{connection.Name}“: {result.Message}", result.Success ? InfoSeverity.Success : InfoSeverity.Error);
        ApplyButtons();
    }

    private static Control Row(Connection connection)
    {
        var type = ConnectionCatalog.Get(connection.Kind);
        var (stateText, stateIcon, tone) = StateOf(connection);
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        title.Children.Add(new TextBlock { Text = connection.Name, FontWeight = FontWeight.SemiBold, FontSize = MatTokens.Type.FsLg, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        title.Children.Add(new Border { Child = new TextBlock { Text = type.Name } }.Themed("MatBadge.Soft"));
        var address = ConnectionService.Address(connection);
        var text = new StackPanel
        {
            Margin = new Thickness(16, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 3,
            Children =
            {
                title,
                new TextBlock { Text = address.Length > 0 ? address : type.Description, TextTrimming = TextTrimming.CharacterEllipsis }.Themed("MatText.Muted"),
            },
        };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), MinHeight = 52 };
        row.Children.Add(Ui.IconTile(type.Icon, 44, 22));
        row.Children.Add(Ui.Cell(text, 0, 1));
        row.Children.Add(Ui.Cell(new StatePill(stateText, stateIcon, tone), 0, 2));
        return row;
    }
}
