using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using MatDesktop;
using ThixSync.Controls;
using ThixSync.Core.Business;
using ThixSync.Core.Infrastructure;

namespace ThixSync.Dialogs;

/// <summary>
/// Shown at start when ThixSync crashed or hung last time (<see cref="ProblemReports"/>): what happened, the whole report
/// to read and copy, the log folder. Several reports: the newest one, the others are named.
/// </summary>
public sealed class ProblemReportDialog : MatWindow
{
    private ProblemReportDialog(IReadOnlyList<ProblemReport> reports)
    {
        var report = reports[^1];
        Title = report.IsHang ? "ThixSync hat nicht reagiert" : "ThixSync wurde unerwartet beendet";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        this.Use(BackgroundProperty, "Mat.Surface");

        var heading = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(0, 0, 0, 8),
            Children =
            {
                new MatIcon { Icon = "alert", Size = 22 }.Use(MatIcon.ForegroundProperty, report.IsHang ? "Mat.WarningText" : "Mat.DangerText"),
                new TextBlock { Text = Title, FontSize = MatTokens.Type.FsLg, FontWeight = FontWeight.SemiBold, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
            },
        };

        var others = reports.Count - 1;
        var intro = new TextBlock
        {
            Text = $"Am {report.Time:dd.MM.yyyy} um {report.Time:HH:mm} Uhr. Der Bericht unten hilft, die Ursache zu finden – kopiere ihn oder öffne den Protokollordner." +
                   (others > 0 ? $" Dazu {(others == 1 ? "ein weiterer Bericht" : $"{others} weitere Berichte")} seit dem letzten Start." : ""),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        }.Use(TextBlock.ForegroundProperty, "Mat.TextMuted");

        var details = new TextBox
        {
            Text = report.Text,
            IsReadOnly = true,
            TextWrapping = TextWrapping.NoWrap,
            AcceptsReturn = true,
            FontSize = MatTokens.Type.FsXs,
            Height = 280,
            Margin = new Thickness(0, 0, 0, 18),
        };
        details.Use(TextBox.FontFamilyProperty, "Mat.Mono");
        ScrollViewer.SetHorizontalScrollBarVisibility(details, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        AutomationProperties.SetName(details, "Bericht");

        var copy = new Button { Content = new IconLabel { Icon = "copy", Text = "Bericht kopieren" }, Theme = Ui.Theme("MatButton.Primary") };
        copy.Click += (_, _) =>
        {
            _ = Ui.CopyTextAsync(this, report.Text);
            ((IconLabel)copy.Content!).Text = "Kopiert";
        };

        var folder = new Button { Content = new IconLabel { Icon = "external", Text = "Protokollordner öffnen" } };
        folder.Click += (_, _) => Ui.OpenExternal(AppPaths.LogDirectory);

        var close = new Button { Content = new IconLabel { Icon = "x", Text = "Schließen" }, IsCancel = true, IsDefault = true };
        close.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(24, 22),
            Children = { heading, intro, details, new ActionBar { Children = { copy, folder, close } } },
        };
    }

    /// <summary>Shows the reports of the last run, if there are any (start-up); returns the dialog (null: nothing to say).</summary>
    public static ProblemReportDialog? ShowUnseen()
    {
        var reports = ProblemReports.TakeUnseen();
        if (reports.Count == 0)
        {
            return null;
        }

        var dialog = new ProblemReportDialog(reports);
        dialog.Show();
        return dialog;
    }
}
