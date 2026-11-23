using ThixSync.Core.Business;
using ThixSync.Core.Models;

namespace ThixSync.Cli;

/// <summary>
/// What the commands need from the outside world: the console and the running of jobs. <see cref="ForConsole"/> is the real one
/// (Console, <see cref="JobRunner"/>, Ctrl+C); tests give writers and a runner of their own.
/// </summary>
internal sealed record CliHost
{
    public required TextWriter Out { get; init; }

    public required TextWriter Error { get; init; }

    /// <summary>Standard input (import from "-").</summary>
    public TextReader In { get; init; } = TextReader.Null;

    /// <summary>Standard output is a terminal: progress on one line that updates itself. Otherwise plain lines (log files, pipes).</summary>
    public bool IsTerminal { get; init; }

    /// <summary>The width of the terminal in characters.</summary>
    public Func<int> Width { get; init; } = () => 100;

    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.Now;

    public Func<Guid, RunRequest, CancellationToken, Task<SyncRun>> RunAsync { get; init; } = JobRunner.RunAsync;

    public Action<Guid> CancelRun { get; init; } = JobRunner.Cancel;

    public Func<Guid, SyncPlan?> LastPlan { get; init; } = JobRunner.LastPlan;

    public Func<IReadOnlyList<RunProgress>> Running { get; init; } = JobRunner.Running;

    /// <summary>Calls the action with every progress of every job until the returned object is disposed.</summary>
    public Func<Action<RunProgress>, IDisposable> SubscribeProgress { get; init; } = SubscribeToRunner;

    /// <summary>Calls the action when the user presses Ctrl+C (the first time; a second Ctrl+C ends the program) until disposed.</summary>
    public Func<Action, IDisposable> OnCancelKey { get; init; } = _ => new Subscription(() => { });

    /// <summary>The real console, the real runner and Ctrl+C.</summary>
    public static CliHost ForConsole() => new()
    {
        Out = Console.Out,
        Error = Console.Error,
        In = Console.In,
        IsTerminal = !Console.IsOutputRedirected,
        Width = ConsoleWidth,
        OnCancelKey = SubscribeToCancelKey,
    };

    private static IDisposable SubscribeToRunner(Action<RunProgress> action)
    {
        void Handler(object? sender, RunProgress progress) => action(progress);
        JobRunner.ProgressChanged += Handler;
        return new Subscription(() => JobRunner.ProgressChanged -= Handler);
    }

    private static IDisposable SubscribeToCancelKey(Action action)
    {
        var pressed = 0;
        void Handler(object? sender, ConsoleCancelEventArgs args)
        {
            // the first Ctrl+C stops the run cleanly; a second one ends the program at once
            if (Interlocked.Increment(ref pressed) == 1)
            {
                args.Cancel = true;
                action();
            }
        }

        Console.CancelKeyPress += Handler;
        return new Subscription(() => Console.CancelKeyPress -= Handler);
    }

    private static int ConsoleWidth()
    {
        try
        {
            return Console.WindowWidth > 0 ? Console.WindowWidth : 100;
        }
        catch (Exception exception) when (exception is IOException or PlatformNotSupportedException)
        {
            return 100;
        }
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

/// <summary>A command at work: the host and the parsed command line.</summary>
internal sealed record CliContext(CliHost Host, ParsedCommand Command)
{
    public TextWriter Out => Host.Out;

    public TextWriter Error => Host.Error;

    public IReadOnlyList<string> Arguments => Command.Arguments;

    public bool Json => Command.Has("json");
}
