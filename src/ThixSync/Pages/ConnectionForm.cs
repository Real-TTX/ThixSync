using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using MatDesktop;
using ThixSync.Controls;
using ThixSync.Core.Business;
using ThixSync.Core.Infrastructure;
using ThixSync.Core.Models;
using ThixSync.Core.Remote;
using ThixSync.Core.Storage;
using ThixSync.Core.Storage.Providers.Local;
using ThixSync.Dialogs;

namespace ThixSync.Pages;

/// <summary>
/// A connection, made or changed. A new one starts with the type picker (every type of <see cref="ConnectionCatalog"/> in its four
/// groups, with icon and description; types this computer may not use are greyed). Then the form of the type in tabs Allgemein |
/// Anmeldung | Erweitert, drawn from its fields: only the fields of the type, and of those only the ones that apply now
/// (<see cref="ConnectionField.IsVisible"/>, <see cref="ConnectionType.IsSecretVisible"/>). Special parts: the drive picker of an external
/// drive, "Im Browser anmelden" for the clouds with OAuth, "Computer im Netzwerk suchen" and the pairing code for ThixSync Remote, the
/// hint to Einstellungen › Cloud for the MatCloud. Buttons: Speichern, Verbindung testen, Zurück … Löschen.
/// </summary>
public sealed class ConnectionForm : UserControl
{
    private const string NameField = nameof(Connection.Name);
    private const string SecretField = "Secret";

    private readonly Control _picker;
    private readonly ListToolbar _pickerToolbar = new("Typ suchen");
    private readonly StackPanel _pickerGroups = new();
    private readonly DockPanel _formView = new() { Margin = new Thickness(36, 28, 36, 24) };
    private readonly ContentControl _typeTile = new() { Margin = new Thickness(0, 2, 16, 0), VerticalAlignment = VerticalAlignment.Top };
    private readonly TextBlock _title = new();
    private readonly TextBlock _subtitle = new();
    private readonly Button _changeType = new() { Content = "Anderen Typ wählen" };
    private readonly InfoBar _message = new();
    private readonly InfoBar _note = new();
    private readonly TabControl _tabs = new();
    private readonly TabItem _generalTab = new() { Header = "Allgemein" };
    private readonly TabItem _signInTab = new() { Header = "Anmeldung" };
    private readonly TabItem _advancedTab = new() { Header = "Erweitert" };
    private readonly TextBox _name = new();
    private readonly TextBox _secret = new() { PasswordChar = '●' };
    private readonly Button _save = Ui.Button("save", "Speichern", "MatButton.Primary");
    private readonly Button _test = Ui.Button("plug", "Verbindung testen");
    private readonly Button _back = Ui.Button("back", "Zurück");
    private readonly Button _delete = Ui.Button("trash", "Löschen", "MatButton.Danger");
    private readonly List<FieldInput> _inputs = [];
    private FormFields _fields = new();
    private MatFormField? _secretField;
    private Control? _signInExtra;
    private ConnectionType? _type;
    private Connection? _existing;
    private Action<Connection>? _onSaved;
    private bool _isLoading;

    public ConnectionForm()
    {
        this.Use(BackgroundProperty, "Mat.Surface");
        _title.Themed("MatText.PageTitle");
        _subtitle.Themed("MatText.Muted");
        _changeType.Themed("MatButton.Link");
        _changeType.Click += (_, _) => ShowPicker();
        AutomationProperties.SetName(_name, "Name");
        _picker = BuildPicker();

        _tabs.Items.Add(_generalTab);
        _tabs.Items.Add(_signInTab);
        _tabs.Items.Add(_advancedTab);
        _save.IsDefault = true;
        _save.Click += (_, _) => Save();
        _test.Click += async (_, _) => await TestFormAsync();
        _back.Click += (_, _) => Leave();
        ActionBar.SetSeparated(_delete, true);
        _delete.Click += async (_, _) =>
        {
            if (_existing is { } connection && await DeleteAsync(this, connection, _message.Show))
            {
                Leave($"„{connection.Name}“ wurde gelöscht.");
            }
        };

        var head = new DockPanel
        {
            Children =
            {
                Ui.Docked(_typeTile, Dock.Left),
                new StackPanel { Spacing = 4, Children = { _title, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { _subtitle, _changeType } } } },
            },
        };
        _message.Margin = new Thickness(0, 14, 0, 0);
        _note.Margin = new Thickness(0, 14, 0, 0);
        _tabs.Margin = new Thickness(0, 14, 0, 0);
        _formView.Children.Add(Ui.Docked(head, Dock.Top));
        _formView.Children.Add(Ui.Docked(new ActionBar { Margin = new Thickness(0, 16, 0, 0), Children = { _save, _test, _back, _delete } }, Dock.Bottom));
        _formView.Children.Add(Ui.Docked(_note, Dock.Top));
        _formView.Children.Add(Ui.Docked(_message, Dock.Top));
        _formView.Children.Add(_tabs);
        Content = _picker;
    }

    /// <summary>The type picker is shown (a new connection, no type chosen yet).</summary>
    public bool IsPickingType => Content == _picker;

    public ConnectionType? Type => _type;

    public TextBox NameBox => _name;

    public TextBox SecretBox => _secret;

    public MatFormField? SecretFormField => _secretField;

    public TabControl Tabs => _tabs;

    public TabItem SignInTab => _signInTab;

    public TabItem AdvancedTab => _advancedTab;

    public InfoBar Message => _message;

    public Button SaveButton => _save;

    public Button TestButton => _test;

    public FormFields Fields => _fields;

    /// <summary>The type tiles of the picker by kind (tests).</summary>
    public IReadOnlyDictionary<ConnectionKind, Button> TypeButtons { get; private set; } = new Dictionary<ConnectionKind, Button>();

    /// <summary>The input of a field of the type (null: the type has no such field).</summary>
    public Control? Input(string key) => _inputs.FirstOrDefault(input => input.Field.Key == key)?.Input;

    /// <summary>The labelled field of a field of the type (its IsVisible says whether it applies).</summary>
    public MatFormField? FormField(string key) => _inputs.FirstOrDefault(input => input.Field.Key == key)?.FormField;

    /// <summary>
    /// Shows a connection (null: a new one – the type picker, or the form of <paramref name="kind"/>). <paramref name="onSaved"/> gets the
    /// saved connection (the job form chooses it); without it the page goes back to where it came from.
    /// </summary>
    public void Load(Guid? connectionId, ConnectionKind? kind = null, Action<Connection>? onSaved = null)
    {
        _onSaved = onSaved;
        _existing = connectionId is { } id ? ConnectionService.Find(id) : null;
        _message.Hide();
        if (_existing is not null)
        {
            ShowForm(ConnectionCatalog.Get(_existing.Kind));
            return;
        }

        if (kind is { } chosen)
        {
            ShowForm(ConnectionCatalog.Get(chosen));
            return;
        }

        ShowPicker();
    }

    public void ShowPicker()
    {
        _pickerToolbar.SearchBox.Text = "";
        FillPicker();
        Content = _picker;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _pickerToolbar.SearchBox.Focus(), Avalonia.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>The form of a type (for a new connection or the one loaded).</summary>
    public void ShowForm(ConnectionType type)
    {
        _isLoading = true;
        try
        {
            _type = type;

            // the name and the secret boxes stay; they leave the fields of the type shown before
            if (_fields.Find(NameField) is { } oldName)
            {
                oldName.Content = null;
            }

            if (_secretField is not null)
            {
                _secretField.Content = null;
            }

            _fields = new FormFields();
            _inputs.Clear();
            _message.Hide();
            _note.Hide();
            var connection = _existing ?? new Connection { Kind = type.Kind };
            var locked = AccessPolicy.IsConfigurationLocked;

            var tile = Ui.IconTile(type.Icon, 52, 26);
            tile.CornerRadius = new CornerRadius(14);
            _typeTile.Content = tile;
            _title.Text = _existing?.Name ?? $"Neue Verbindung: {type.Name}";
            _subtitle.Text = _existing is null ? type.Description : $"{type.Name} · {type.Description}";
            _changeType.IsVisible = _existing is null;
            _delete.IsVisible = _existing is not null;
            _save.IsEnabled = _delete.IsEnabled = !locked;

            _name.Text = _existing?.Name ?? (ConnectionService.List().Any(other => string.Equals(other.Name, type.Name, StringComparison.CurrentCultureIgnoreCase)) ? "" : type.Name);
            _name.Watermark = type.Name;
            var general = new StackPanel();
            general.Children.Add(_fields.Add(NameField, "Name", _name, "So erscheint die Verbindung in den Listen und Aufträgen.", _generalTab));
            var signIn = new StackPanel();
            var advanced = new StackPanel();
            foreach (var field in type.Fields)
            {
                var tab = field.Tab switch { FieldTab.SignIn => _signInTab, FieldTab.Advanced => _advancedTab, _ => _generalTab };
                var input = CreateInput(field, ConnectionCatalog.Value(connection, field.Key));
                var formField = _fields.Add(field.Key, field.Label, input.Input, field.Help.Length > 0 ? field.Help : null, tab);
                input.FormField = formField;
                _inputs.Add(input);
                (field.Tab switch { FieldTab.SignIn => signIn, FieldTab.Advanced => advanced, _ => general }).Children.Add(formField);
            }

            _secretField = null;
            if (type.SecretLabel is { } secretLabel)
            {
                _secret.Text = "";
                _secret.Watermark = connection.HasSecret ? "unverändert" : type.SecretOptional ? "optional" : "";
                AutomationProperties.SetName(_secret, secretLabel);
                var hint = type.Auth == AuthKind.Pairing
                    ? "Auf dem anderen Computer: Einstellungen › Remote › „Kopplungscode anzeigen“. Der Code gilt einige Minuten; danach bleibt ein Schlüssel geschützt auf diesem Computer."
                    : OperatingSystem.IsMacOS() ? "Bleibt im Schlüsselbund dieses Macs und geht nie in die Cloud." : "Bleibt geschützt auf diesem Computer und geht nie in die Cloud.";
                _secretField = _fields.Add(SecretField, secretLabel, _secret, hint, _signInTab);
                signIn.Children.Add(_secretField);
            }

            _signInExtra = type.Auth switch
            {
                AuthKind.OAuth => OAuthPart(type),
                AuthKind.MatCloud => MatCloudPart(),
                _ => null,
            };
            if (_signInExtra is not null)
            {
                signIn.Children.Add(_signInExtra);
            }

            if (type.Kind == ConnectionKind.ThixSyncRemote)
            {
                general.Children.Add(DiscoverPart());
            }

            _generalTab.Content = Scroll(general);
            _signInTab.Content = Scroll(signIn);
            _advancedTab.Content = Scroll(advanced);

            if (type.IsBoundToDevice && _existing is not null && !string.Equals(_existing.DeviceName, AppInfo.MachineName, StringComparison.OrdinalIgnoreCase))
            {
                _note.Show($"Diese Verbindung gehört zu {_existing.DeviceName}: ihre Ordner gibt es nur dort. Hier kannst du den Namen ändern.", InfoSeverity.Info);
            }
            else if (!AccessPolicy.IsConnectionKindAllowed(type.Kind))
            {
                _note.Show("Von deiner Organisation festgelegt: dieser Verbindungstyp ist auf diesem Computer gesperrt.", InfoSeverity.Warning);
            }
            else if (!StorageProviders.IsSupported(type.Kind))
            {
                _note.Show("Dieser Verbindungstyp ist in dieser Version noch nicht verfügbar. Du kannst ihn schon anlegen – er wird nutzbar, sobald ThixSync ihn kann.", InfoSeverity.Info);
            }
            else if (locked)
            {
                _note.Show("Von deiner Organisation festgelegt: Verbindungen lassen sich hier nicht ändern (Richtlinie LockConfiguration).", InfoSeverity.Info);
            }
        }
        finally
        {
            _isLoading = false;
        }

        ApplyVisibility();
        _tabs.SelectedItem = _generalTab;
        Content = _formView;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _name.Focus(), Avalonia.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>The connection as the form says now (with the id of the loaded one).</summary>
    public Connection Collect()
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var input in _inputs)
        {
            var value = input.Read();
            if (value.Length > 0)
            {
                settings[input.Field.Key] = value;
            }
        }

        var connection = _existing ?? new Connection { Kind = _type?.Kind ?? ConnectionKind.Local };
        return connection with { Name = _name.Text ?? "", Settings = settings };
    }

    /// <summary>The secret typed (null: keep the stored one).</summary>
    private string? SecretInput() => _secretField is { IsVisible: true } && (_secret.Text ?? "").Length > 0 ? _secret.Text : null;

    /// <summary>"Verbindung testen" (also the button of the list); a provider that is not there yet answers with a message too.</summary>
    internal static async Task<ConnectionTestResult> TestAsync(Connection connection, string? secret)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            return await Task.Run(() => ConnectionService.TestAsync(connection, secret, timeout.Token));
        }
        catch (OperationCanceledException)
        {
            return new ConnectionTestResult(false, "Keine Antwort nach 30 Sekunden.");
        }
        catch (NotImplementedException)
        {
            return new ConnectionTestResult(false, "Dieser Verbindungstyp ist in dieser Version noch nicht verfügbar.");
        }
    }

    /// <summary>Asks first; true when the connection was deleted.</summary>
    internal static async Task<bool> DeleteAsync(Control owner, Connection connection, Action<string, InfoSeverity> tell)
    {
        if (!await ConfirmDialog.AskAsync(Ui.WindowOf(owner), "Verbindung löschen?",
                $"„{connection.Name}“ wird gelöscht – mit der Cloud auf all deinen Geräten, das Passwort auf diesem Computer auch. Die Dateien dort bleiben, wie sie sind.",
                "Löschen", isDestructive: true))
        {
            return false;
        }

        try
        {
            ConnectionService.Delete(connection.Id);
            tell($"„{connection.Name}“ wurde gelöscht.", InfoSeverity.Success);
            return true;
        }
        catch (BusinessException exception)
        {
            tell(exception.Message, InfoSeverity.Error);
            return false;
        }
    }

    private void Save()
    {
        if (TrySave() is not { } saved)
        {
            return;
        }

        if (_onSaved is { } onSaved)
        {
            Ui.MainOf(this)?.GoBack();
            onSaved(saved);
            return;
        }

        Leave($"„{saved.Name}“ wurde gespeichert.");
    }

    /// <summary>Saves (the checks of the business layer); errors show at their field. Null when it did not save.</summary>
    private Connection? TrySave()
    {
        _message.Hide();
        _fields.Clear();
        try
        {
            var saved = ConnectionService.Save(Collect(), SecretInput());
            _existing = saved;
            _secret.Text = "";
            _secret.Watermark = saved.HasSecret ? "unverändert" : _secret.Watermark;
            return saved;
        }
        catch (BusinessException exception)
        {
            if (!_fields.ShowError(exception.Field, exception.Message))
            {
                _message.Show(exception.Message, InfoSeverity.Error);
            }

            return null;
        }
    }

    private async Task TestFormAsync()
    {
        // ThixSync Remote pairs during the test and uses up the code: the connection is saved with the code as its secret first
        var isRemote = _type?.Kind == ConnectionKind.ThixSyncRemote;
        Connection connection;
        string? secret;
        if (isRemote)
        {
            if (TrySave() is not { } saved)
            {
                return;
            }

            (connection, secret) = (saved, null);
        }
        else
        {
            (connection, secret) = (Collect(), SecretInput());
        }

        _test.IsEnabled = false;
        _message.Show("Teste die Verbindung …", InfoSeverity.Info);
        var result = await TestAsync(connection, secret);
        _test.IsEnabled = true;
        if (isRemote && !result.Success && result.Message.Contains("Fingerabdruck", StringComparison.OrdinalIgnoreCase))
        {
            _inputs.FirstOrDefault(input => input.Field.Key == ConnectionFields.Fingerprint)?.Write("");
            _message.Show($"{result.Message} Bitte neu koppeln: auf dem anderen Computer einen neuen Kopplungscode anzeigen, hier eingeben und speichern.", InfoSeverity.Error);
            return;
        }

        _message.Show(result.Message, result.Success ? InfoSeverity.Success : InfoSeverity.Error);
    }

    /// <summary>Back to where the form was opened; <paramref name="done"/> is told on the list of connections.</summary>
    private void Leave(string? done = null)
    {
        var window = Ui.MainOf(this);
        window?.GoBack();
        if (done is not null)
        {
            window?.ConnectionsPage.ShowMessage(done, InfoSeverity.Success);
        }
    }

    /// <summary>Only the fields that apply with the values chosen now, the secret only where it is asked, tabs without fields hidden.</summary>
    private void ApplyVisibility()
    {
        if (_isLoading || _type is null)
        {
            return;
        }

        var settings = Collect().Settings;
        foreach (var input in _inputs)
        {
            input.FormField!.IsVisible = input.Field.IsVisible(settings, _type.Fields);
        }

        if (_secretField is not null)
        {
            _secretField.IsVisible = _type.IsSecretVisible(settings);
        }

        _signInTab.IsVisible = _inputs.Any(input => input.Field.Tab == FieldTab.SignIn && input.FormField!.IsVisible) || _secretField?.IsVisible == true || _signInExtra is not null;
        _advancedTab.IsVisible = _inputs.Any(input => input.Field.Tab == FieldTab.Advanced && input.FormField!.IsVisible);
        if (_tabs.SelectedItem is TabItem { IsVisible: false })
        {
            _tabs.SelectedItem = _generalTab;
        }
    }

    // ---------------------------------------------------------------------------------------------------------------- inputs

    private FieldInput CreateInput(ConnectionField field, string value)
    {
        if (field.Key == ConnectionFields.VolumeLabel && _type?.Kind == ConnectionKind.ExternalDrive)
        {
            return DriveInput(field, value);
        }

        switch (field.Type)
        {
            case FieldType.Number:
            {
                var box = new NumericUpDown { Minimum = 1, Maximum = 65535, Increment = 1, FormatString = "0", Width = 180, HorizontalAlignment = HorizontalAlignment.Left, Watermark = field.Default.Length > 0 ? field.Default : field.Placeholder };
                box.Value = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;
                AutomationProperties.SetName(box, field.Label);
                return new FieldInput(field, box, () => box.Value is { } chosen ? ((int)chosen).ToString(CultureInfo.InvariantCulture) : "", text => box.Value = int.TryParse(text, out var parsed) ? parsed : null);
            }

            case FieldType.Choice:
            {
                var combo = new ComboBox { MinWidth = 280, ItemsSource = field.Choices.Select(choice => choice.Label).ToList() };
                var chosen = value.Length > 0 ? value : field.Default;
                combo.SelectedIndex = field.Choices.ToList().FindIndex(choice => choice.Value == chosen);
                AutomationProperties.SetName(combo, field.Label);
                combo.SelectionChanged += (_, _) => ApplyVisibility();
                return new FieldInput(field, combo, () => combo.SelectedIndex >= 0 ? field.Choices[combo.SelectedIndex].Value : "", text => combo.SelectedIndex = field.Choices.ToList().FindIndex(choice => choice.Value == text));
            }

            case FieldType.Toggle:
            {
                var toggle = new ToggleSwitch { OnContent = "", OffContent = "", IsChecked = (value.Length > 0 ? value : field.Default) == "true" };
                AutomationProperties.SetName(toggle, field.Label);
                toggle.IsCheckedChanged += (_, _) => ApplyVisibility();
                return new FieldInput(field, toggle, () => toggle.IsChecked == true ? "true" : "false", text => toggle.IsChecked = text == "true");
            }

            case FieldType.ReadOnly:
            {
                var text = new SelectableTextBlock { Text = value.Length > 0 ? value : "–", TextWrapping = TextWrapping.Wrap, FontFamily = MatTheme.MonoFont(), FontSize = MatTokens.Type.FsSm };
                var current = value;
                return new FieldInput(field, text, () => current, update =>
                {
                    current = update;
                    text.Text = update.Length > 0 ? update : "–";
                });
            }

            case FieldType.LocalFolder:
            case FieldType.LocalFile:
            {
                var box = new TextBox { Text = value, Watermark = field.Placeholder };
                AutomationProperties.SetName(box, field.Label);
                var browse = new Button { Content = new IconLabel { Icon = "folder-open", Text = "Durchsuchen …" }, Margin = new Thickness(8, 0, 0, 0) };
                browse.Click += async (_, _) =>
                {
                    if (await PickLocalAsync(field.Type == FieldType.LocalFolder, field.Label) is { } path)
                    {
                        box.Text = path;
                    }
                };
                return new FieldInput(field, new DockPanel { Children = { Ui.Docked(browse, Dock.Right), box } }, () => box.Text ?? "", text => box.Text = text);
            }

            default:
            {
                var box = new TextBox { Text = value, Watermark = field.Placeholder };
                AutomationProperties.SetName(box, field.Label);
                box.TextChanged += (_, _) => ApplyVisibility();
                return new FieldInput(field, box, () => box.Text ?? "", text => box.Text = text);
            }
        }
    }

    /// <summary>The drive of an external drive: the drives connected now (label, letter or mount point, size); the choice sets label and id.</summary>
    private FieldInput DriveInput(ConnectionField field, string value)
    {
        var volumes = Volumes.List().ToList();
        var labels = volumes.Select(volume => $"{volume.Label} ({volume.RootPath.TrimEnd('\\', '/')}) · {Formats.Bytes(volume.TotalBytes)}{(volume.IsRemovable ? " · wechselbar" : "")}").ToList();
        var currentId = _existing?.Get(ConnectionFields.VolumeId) ?? "";
        var index = volumes.FindIndex(volume => currentId.Length > 0 ? string.Equals(volume.Id, currentId, StringComparison.OrdinalIgnoreCase) : volume.Label == value);
        if (index < 0 && value.Length > 0)
        {
            volumes.Insert(0, new VolumeInfo(currentId, value, "", "", 0, 0, true));
            labels.Insert(0, $"{value} (nicht angeschlossen)");
            index = 0;
        }

        var combo = new ComboBox { MinWidth = 380, ItemsSource = labels, SelectedIndex = index, PlaceholderText = volumes.Count == 0 ? "Kein Laufwerk angeschlossen" : "Laufwerk wählen …" };
        AutomationProperties.SetName(combo, field.Label);
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex < 0)
            {
                return;
            }

            var volume = volumes[combo.SelectedIndex];
            _inputs.FirstOrDefault(input => input.Field.Key == ConnectionFields.VolumeId)?.Write(volume.Id);
            if (string.IsNullOrWhiteSpace(_name.Text) || _name.Text == _type?.Name)
            {
                _name.Text = volume.Label;
            }
        };
        return new FieldInput(field, combo, () => combo.SelectedIndex >= 0 ? volumes[combo.SelectedIndex].Label : "", _ => { });
    }

    private async Task<string?> PickLocalAsync(bool folder, string title)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return null;
        }

        if (folder)
        {
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title });
            return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    // ---------------------------------------------------------------------------------------------------------------- special parts

    /// <summary>"Im Browser anmelden": saves the connection first, then the sign-in of its cloud in the browser; shows who signed in.</summary>
    private Control OAuthPart(ConnectionType type)
    {
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap }.Themed("MatText.Muted");
        status.Text = _existing?.Get(ConnectionFields.SignedInAs) is { Length: > 0 } account
            ? $"Angemeldet als {account}."
            : "Noch nicht angemeldet: ThixSync öffnet die Anmeldung im Browser, du meldest dich dort an und erlaubst den Zugriff.";
        var signIn = Ui.Button("login", "Im Browser anmelden", "MatButton.Primary");
        signIn.Click += async (_, _) =>
        {
            if (TrySave() is not { } saved)
            {
                return;
            }

            signIn.IsEnabled = false;
            _message.Show("Die Anmeldung ist im Browser geöffnet – melde dich dort an und erlaube den Zugriff.", InfoSeverity.Info);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                var account = await OAuthService.SignInAsync(saved.Id, timeout.Token);
                _existing = ConnectionService.Find(saved.Id) ?? saved;
                _inputs.FirstOrDefault(input => input.Field.Key == ConnectionFields.SignedInAs)?.Write(account);
                status.Text = $"Angemeldet als {account}.";
                _message.Show($"Angemeldet als {account}.", InfoSeverity.Success);
            }
            catch (BusinessException exception)
            {
                _message.Show(exception.Message, InfoSeverity.Error);
            }
            catch (OperationCanceledException)
            {
                _message.Show("Die Anmeldung wurde nicht abgeschlossen.", InfoSeverity.Warning);
            }
            catch (NotImplementedException)
            {
                _message.Show("Die Anmeldung bei Cloud-Speichern ist in dieser Version noch nicht verfügbar.", InfoSeverity.Warning);
            }
            finally
            {
                signIn.IsEnabled = true;
            }
        };
        var part = new StackPanel { Spacing = 12, Children = { status, new ActionBar { Children = { signIn } } } };
        if (!IsOAuthConfigured(type.Kind))
        {
            var note = new InfoBar();
            note.Show("Für diesen Dienst ist in dieser Version noch keine App-Registrierung eingebaut. Du kannst eine eigene unter Einstellungen › Erweitert eintragen.", InfoSeverity.Warning);
            part.Children.Insert(0, note);
        }

        return part;
    }

    private static bool IsOAuthConfigured(ConnectionKind kind)
    {
        try
        {
            return OAuthService.IsConfigured(kind);
        }
        catch (NotImplementedException)
        {
            return false;
        }
    }

    /// <summary>The MatCloud signs in with the app's own cloud connection (Einstellungen › Cloud).</summary>
    private Control MatCloudPart()
    {
        var connected = CloudSyncService.IsConnected;
        var cloud = SettingsService.Get().Cloud;
        var note = new InfoBar();
        note.Show(connected
                ? $"Diese Verbindung nutzt die Anmeldung dieses Computers bei der MatCloud ({cloud.UserName} auf {cloud.ServerUrl}) – kein eigenes Passwort nötig."
                : "Diese Verbindung nutzt die Anmeldung dieses Computers bei der MatCloud. Verbinde ThixSync zuerst unter Einstellungen › Cloud.",
            connected ? InfoSeverity.Success : InfoSeverity.Warning);
        var go = Ui.Button("cloud", "Einstellungen › Cloud");
        go.Click += (_, _) => Ui.MainOf(this)?.ShowSettings(SettingsPage.CloudTab);
        return new StackPanel { Spacing = 4, Children = { note, new ActionBar { Children = { go } } } };
    }

    /// <summary>ThixSync Remote: the computers with ThixSync in this network; a click takes its address.</summary>
    private Control DiscoverPart()
    {
        var found = new WrapPanel { Orientation = Orientation.Horizontal };
        var status = new TextBlock { Text = "ThixSync sucht andere Computer, auf denen „Andere Computer dürfen sich verbinden“ an ist (Einstellungen › Remote).", TextWrapping = TextWrapping.Wrap }.Themed("MatText.Muted");
        var search = Ui.Button("search", "Computer im Netzwerk suchen");
        search.Click += async (_, _) =>
        {
            search.IsEnabled = false;
            found.Children.Clear();
            status.Text = "Suche …";
            try
            {
                var computers = await RemoteServer.DiscoverAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
                status.Text = computers.Count == 0 ? "Kein Computer gefunden. Gib die Adresse von Hand ein." : "Gefunden – ein Klick übernimmt die Adresse:";
                foreach (var computer in computers)
                {
                    var pick = Ui.Button("monitor", $"{computer.DeviceName} ({computer.Host})");
                    pick.Margin = new Thickness(0, 0, 8, 8);
                    pick.Click += (_, _) =>
                    {
                        _inputs.FirstOrDefault(input => input.Field.Key == ConnectionFields.Host)?.Write(computer.Host);
                        _inputs.FirstOrDefault(input => input.Field.Key == ConnectionFields.Port)?.Write(computer.Port.ToString(CultureInfo.InvariantCulture));
                        if (string.IsNullOrWhiteSpace(_name.Text) || _name.Text == _type?.Name)
                        {
                            _name.Text = computer.DeviceName;
                        }
                    };
                    found.Children.Add(pick);
                }
            }
            catch (Exception exception) when (exception is BusinessException or NotImplementedException or IOException or System.Net.Sockets.SocketException)
            {
                status.Text = exception is NotImplementedException ? "Die Suche ist in dieser Version noch nicht verfügbar." : exception.Message;
            }
            finally
            {
                search.IsEnabled = true;
            }
        };
        return new StackPanel
        {
            Spacing = 10,
            Margin = new Thickness(0, 6, 0, 0),
            Children = { FormFields.Section("Im Netzwerk", isFirst: true), status, new ActionBar { Children = { search } }, found },
        };
    }

    // ---------------------------------------------------------------------------------------------------------------- type picker

    private Control BuildPicker()
    {
        _pickerToolbar.Changed += (_, _) => FillPicker();
        var back = Ui.Button("back", "Zurück");
        back.Click += (_, _) => Ui.MainOf(this)?.GoBack();
        var intro = new TextBlock
        {
            Text = "Wohin soll ThixSync verbinden? Jede Verbindung kann Quelle und Ziel einer Synchronisation sein – in jeder Kombination.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 18),
        }.Themed("MatText.Muted");
        var page = new DockPanel { Margin = new Thickness(36, 28, 36, 24) };
        page.Children.Add(Ui.Docked(new TextBlock { Text = "Neue Verbindung" }.Themed("MatText.PageTitle"), Dock.Top));
        page.Children.Add(Ui.Docked(intro, Dock.Top));
        page.Children.Add(Ui.Docked(_pickerToolbar, Dock.Top));
        page.Children.Add(Ui.Docked(new ActionBar { Margin = new Thickness(0, 16, 0, 0), Children = { back } }, Dock.Bottom));
        page.Children.Add(new ScrollViewer { Content = _pickerGroups, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 14, 0) });
        return page;
    }

    private void FillPicker()
    {
        var search = _pickerToolbar.SearchText;
        var buttons = new Dictionary<ConnectionKind, Button>();
        _pickerGroups.Children.Clear();
        foreach (var group in ConnectionCatalog.All.GroupBy(type => type.Group).OrderBy(group => group.Key))
        {
            var types = group.Where(type => search.Length == 0 || type.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) || type.Description.Contains(search, StringComparison.CurrentCultureIgnoreCase)).ToList();
            if (types.Count == 0)
            {
                continue;
            }

            _pickerGroups.Children.Add(new TextBlock { Text = ConnectionCatalog.GroupName(group.Key), FontWeight = FontWeight.SemiBold, FontSize = MatTokens.Type.FsLg, Margin = new Thickness(0, _pickerGroups.Children.Count == 0 ? 0 : 14, 0, 10) });
            var tiles = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var type in types)
            {
                var button = TypeTile(type);
                buttons[type.Kind] = button;
                tiles.Children.Add(button);
            }

            _pickerGroups.Children.Add(tiles);
        }

        TypeButtons = buttons;
    }

    private Button TypeTile(ConnectionType type)
    {
        var allowed = type.IsAvailable && AccessPolicy.IsConnectionKindAllowed(type.Kind);
        var text = new StackPanel
        {
            Spacing = 3,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = type.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis }.Use(TextBlock.ForegroundProperty, "Mat.Text"),
                new TextBlock { Text = type.Description, FontWeight = FontWeight.Normal, FontSize = MatTokens.Type.FsSm, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis }
                    .Use(TextBlock.ForegroundProperty, "Mat.TextMuted"),
            },
        };
        var tile = Ui.IconTile(type.Icon, 42, 21);
        tile.Margin = new Thickness(0, 0, 14, 0);
        var button = new Button
        {
            Width = 296,
            Height = 82,
            Margin = new Thickness(0, 0, 12, 12),
            Padding = new Thickness(14, 10),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new DockPanel { Children = { Ui.Docked(tile, Dock.Left), text } },
            IsEnabled = allowed,
        };
        button.CornerRadius = new CornerRadius(MatTokens.Size.RadiusLg);
        AutomationProperties.SetName(button, type.Name);
        ToolTip.SetTip(button, allowed ? type.Description : "Auf diesem Computer nicht verfügbar");
        button.Click += (_, _) => ShowForm(type);
        return button;
    }

    private static ScrollViewer Scroll(Control content)
    {
        content.Width = 640;
        content.HorizontalAlignment = HorizontalAlignment.Left;
        content.Margin = new Thickness(0, 4, 18, 12);
        return new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    /// <summary>The input of one field of the type: how to read and write its value as text.</summary>
    private sealed class FieldInput(ConnectionField field, Control input, Func<string> read, Action<string> write)
    {
        public ConnectionField Field { get; } = field;

        public Control Input { get; } = input;

        public MatFormField? FormField { get; set; }

        public string Read() => read();

        public void Write(string value) => write(value);
    }
}
