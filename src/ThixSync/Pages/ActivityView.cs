using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using MatDesktop;
using ThixSync.Controls;
using ThixSync.Core.Business;

namespace ThixSync.Pages;

/// <summary>
/// The tab "Aktivität": a live panel per running job (<see cref="ActivityPanel"/>), updated in place from <see cref="RunMonitor"/>; when
/// nothing runs it says so and offers to start a job.
/// </summary>
public sealed class ActivityView : UserControl
{
    private readonly StackPanel _panels = new();
    private readonly InfoBar _message = new();
    private readonly EmptyState _empty;

    public ActivityView()
    {
        var jobs = Ui.Button("refresh", "Zu den Synchronisationen");
        jobs.Click += (_, _) => JobsRequested?.Invoke(this, EventArgs.Empty);
        _empty = new EmptyState("activity", "Gerade läuft nichts",
            "Sobald eine Synchronisation läuft – von Hand, nach Zeitplan oder weil sich etwas geändert hat –, siehst du hier live, was übertragen wird und wie schnell.",
            jobs);
        var content = new StackPanel { Children = { _message, _panels, _empty } };
        Content = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 14, 0) };
    }

    /// <summary>"Zu den Synchronisationen" was chosen.</summary>
    public event EventHandler? JobsRequested;

    public IReadOnlyList<ActivityPanel> Panels => [.. _panels.Children.OfType<ActivityPanel>()];

    /// <summary>Adds, updates and removes the panels of the running jobs.</summary>
    public void Refresh()
    {
        var running = RunMonitor.Running;
        var panels = _panels.Children.OfType<ActivityPanel>().ToDictionary(panel => panel.JobId);
        foreach (var gone in panels.Values.Where(panel => running.All(progress => progress.JobId != panel.JobId)))
        {
            _panels.Children.Remove(gone);
        }

        foreach (var progress in running)
        {
            if (!panels.TryGetValue(progress.JobId, out var panel))
            {
                if (JobService.Find(progress.JobId) is not { } job)
                {
                    continue;
                }

                panel = new ActivityPanel(job);
                var jobId = job.Id;
                panel.StopRequested += (_, _) =>
                {
                    RunMonitor.Cancel(jobId);
                    _message.Show($"„{job.Name}“ wird angehalten. Was schon kopiert ist, bleibt.", InfoSeverity.Info);
                };
                _panels.Children.Add(panel);
            }

            panel.Show(progress);
        }

        _empty.IsVisible = _panels.Children.Count == 0;
    }
}
