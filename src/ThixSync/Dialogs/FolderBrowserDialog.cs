using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using MatDesktop;
using ThixSync.Controls;
using ThixSync.Core.Business;
using ThixSync.Core.Models;
using ThixSync.Core.Storage;

namespace ThixSync.Dialogs;

/// <summary>
/// Picks a folder of a connection that is not this computer's file system (a NAS, a server, a cloud, an external drive): the folders of
/// the current folder (<see cref="ConnectionService.ListFoldersAsync"/>), a double click or "Öffnen" goes in, "Nach oben" goes up, the
/// path can be typed too. Buttons from positive to negative: "Diesen Ordner wählen", Abbrechen.
/// </summary>
public sealed class FolderBrowserDialog : MatWindow
{
    private readonly Connection _connection;
    private readonly bool _isLocal;
    private readonly TextBox _path = new();
    private readonly ListBox _folders = new();
    private readonly InfoBar _message = new();
    private readonly ProgressBar _loading = new() { IsIndeterminate = true, IsVisible = false, Margin = new Thickness(0, 0, 0, 10) };
    private CancellationTokenSource? _listing;
    private string? _chosen;

    private FolderBrowserDialog(Connection connection, string start)
    {
        _connection = connection;
        _isLocal = connection.Kind == ConnectionKind.Local;
        Title = $"Ordner wählen – {connection.Name}";
        Width = 560;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        this.Use(BackgroundProperty, "Mat.Surface");
        _loading.Themed("MatProgress");
        AutomationProperties.SetName(_path, "Ordner");
        _path.Text = start;
        _path.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                _ = ListAsync(_path.Text ?? "");
            }
        };

        _folders.Themed("MatList");
        _folders.ItemContainerTheme = Ui.Theme("MatChoiceRow");
        _folders.ItemTemplate = new FuncDataTemplate<string>((name, _) => new IconLabel { Icon = "folder", Text = DisplayName(name ?? "") }, supportsRecycling: false);
        AutomationProperties.SetName(_folders, "Ordner");
        _folders.DoubleTapped += (_, _) => Open();

        var up = Ui.Button("arrow-up", "Nach oben");
        up.Click += (_, _) => _ = ListAsync(ParentOf(_path.Text ?? ""));
        var open = Ui.Button("folder-open", "Öffnen");
        open.Click += (_, _) => Open();
        var choose = Ui.Button("check", "Diesen Ordner wählen", "MatButton.Primary");
        choose.IsDefault = true;
        choose.Click += (_, _) =>
        {
            _chosen = _folders.SelectedItem is string name ? Combine(_path.Text ?? "", name) : _path.Text ?? "";
            Close();
        };
        var cancel = Ui.Button("x", "Abbrechen");
        cancel.IsCancel = true;
        cancel.Click += (_, _) => Close();

        var type = ConnectionCatalog.Get(connection.Kind);
        var heading = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Margin = new Thickness(0, 0, 0, 14),
            Children =
            {
                Ui.IconTile(type.Icon, 36, 18),
                new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new TextBlock { Text = connection.Name, FontSize = MatTokens.Type.FsLg, FontWeight = FontWeight.SemiBold },
                        new TextBlock { Text = ConnectionService.Address(connection) }.Themed("MatText.Muted"),
                    },
                },
            },
        };
        var pathRow = new DockPanel { Margin = new Thickness(0, 0, 0, 10), Children = { Ui.Docked(up, Dock.Right), _path } };
        up.Margin = new Thickness(8, 0, 0, 0);
        var list = new Border { Child = _folders, Padding = new Thickness(6) }.Themed("MatCard");
        var buttons = new ActionBar { Margin = new Thickness(0, 14, 0, 0), Children = { choose, open, cancel } };
        var page = new DockPanel { Margin = new Thickness(22, 18) };
        page.Children.Add(Ui.Docked(heading, Dock.Top));
        page.Children.Add(Ui.Docked(pathRow, Dock.Top));
        page.Children.Add(Ui.Docked(_message, Dock.Top));
        page.Children.Add(Ui.Docked(_loading, Dock.Top));
        page.Children.Add(Ui.Docked(buttons, Dock.Bottom));
        page.Children.Add(list);
        Content = page;
        Opened += (_, _) => _ = ListAsync(start);
        Closed += (_, _) => _listing?.Cancel();
    }

    /// <summary>The folder chosen, or null when the dialog was cancelled.</summary>
    public static async Task<string?> PickAsync(Window? owner, Connection connection, string start)
    {
        var dialog = new FolderBrowserDialog(connection, start);
        if (owner is null)
        {
            var closed = new TaskCompletionSource();
            dialog.Closed += (_, _) => closed.TrySetResult();
            dialog.Show();
            await closed.Task;
            return dialog._chosen;
        }

        await dialog.ShowDialog(owner);
        return dialog._chosen;
    }

    private void Open()
    {
        if (_folders.SelectedItem is string name)
        {
            _ = ListAsync(Combine(_path.Text ?? "", name));
        }
    }

    private async Task ListAsync(string folder)
    {
        _listing?.Cancel();
        _listing = new CancellationTokenSource();
        var token = _listing.Token;
        _message.Hide();
        _loading.IsVisible = true;
        try
        {
            var folders = await ConnectionService.ListFoldersAsync(_connection, folder, token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            _path.Text = folder;
            _folders.ItemsSource = folders;
            if (folders.Count == 0)
            {
                _message.Show("Hier gibt es keine Unterordner.", InfoSeverity.Info);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is BusinessException or StorageException or IOException or UnauthorizedAccessException or NotSupportedException or HttpRequestException or TimeoutException)
        {
            _message.Show(exception.Message, InfoSeverity.Error);
        }
        finally
        {
            _loading.IsVisible = false;
        }
    }

    private string Combine(string folder, string name)
    {
        if (_isLocal)
        {
            return Path.IsPathFullyQualified(name) ? name : Path.Combine(folder, name);
        }

        return folder.TrimEnd('/').Length == 0 ? $"/{name}" : $"{folder.TrimEnd('/')}/{name}";
    }

    private string ParentOf(string folder)
    {
        if (_isLocal)
        {
            return Path.GetDirectoryName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? "";
        }

        var trimmed = folder.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash <= 0 ? "" : trimmed[..slash];
    }

    private static string DisplayName(string name) => Path.IsPathFullyQualified(name) ? name : name.TrimStart('/');
}
