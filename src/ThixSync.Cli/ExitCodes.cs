using ThixSync.Core.Models;

namespace ThixSync.Cli;

/// <summary>The exit codes of thixsync-cli (documented in the help: scripts and schedulers rely on them).</summary>
internal static class ExitCodes
{
    /// <summary>Done; a run ended without problems (or the preview was made).</summary>
    public const int Success = 0;

    /// <summary>The run ended with warnings or open conflicts, or it waits for the deletions to be confirmed.</summary>
    public const int Warnings = 1;

    /// <summary>The run failed, or the connection test did.</summary>
    public const int Failed = 2;

    /// <summary>The command line is wrong (unknown command or option, missing argument).</summary>
    public const int Usage = 3;

    /// <summary>The job, connection, run or file does not exist.</summary>
    public const int NotFound = 4;

    /// <summary>The business layer refused (a check, a policy, the job already runs); the message is on stderr.</summary>
    public const int BusinessError = 5;

    /// <summary>Cancelled with Ctrl+C (128 + SIGINT, as shells do).</summary>
    public const int Cancelled = 130;

    /// <summary>The exit code of a finished run.</summary>
    public static int ForOutcome(RunOutcome outcome) => outcome switch
    {
        RunOutcome.Success or RunOutcome.Analysed => Success,
        RunOutcome.Warnings or RunOutcome.NeedsConfirmation => Warnings,
        RunOutcome.Cancelled => Cancelled,
        _ => Failed,
    };
}

/// <summary>The command line is wrong: exit code <see cref="ExitCodes.Usage"/>, the message and a hint to the help.</summary>
internal sealed class UsageException(string message, string? command = null) : Exception(message)
{
    /// <summary>The command whose help fits (null: the general help).</summary>
    public string? Command { get; } = command;
}

/// <summary>Something named on the command line does not exist: exit code <see cref="ExitCodes.NotFound"/>.</summary>
internal sealed class NotFoundException(string message) : Exception(message);
