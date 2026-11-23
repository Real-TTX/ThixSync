using System.Globalization;

namespace ThixSync.Cli;

/// <summary>
/// A command of thixsync-cli: its name, the arguments it takes, its options (flags without a value, options with a value) and its
/// German help. The handler gets the parsed command line.
/// </summary>
internal sealed record CommandSpec(
    string Name,
    string Usage,
    string Summary,
    string Details,
    int MinArguments,
    int MaxArguments,
    IReadOnlyList<OptionSpec> Options,
    Func<CliContext, Task<int>> Handler);

/// <summary>An option of a command: "--json" (a flag) or "--limit &lt;n&gt;" (with a value); <see cref="Short"/> e.g. "-q".</summary>
internal sealed record OptionSpec(string Name, string Help, string? ValueName = null, string? Short = null)
{
    public bool TakesValue => ValueName is not null;

    public string Display => (Short is null ? "" : $"{Short}, ") + (TakesValue ? $"--{Name} <{ValueName}>" : $"--{Name}");
}

/// <summary>What the command line asks for.</summary>
internal enum RequestKind
{
    Command,
    Help,
    Version,
}

/// <summary>
/// The parsed command line: the command, its arguments and the options given. <see cref="Kind"/> Help with a <see cref="Command"/>
/// is the help of that command, without one the general help.
/// </summary>
internal sealed record ParsedCommand(
    RequestKind Kind,
    CommandSpec? Command,
    IReadOnlyList<string> Arguments,
    IReadOnlySet<string> Flags,
    IReadOnlyDictionary<string, string> Values)
{
    public bool Has(string flag) => Flags.Contains(flag);

    public string? Value(string option) => Values.GetValueOrDefault(option);

    /// <summary>An option with a whole number between <paramref name="min"/> and <paramref name="max"/>; <paramref name="fallback"/> when not given.</summary>
    public int Number(string option, int fallback, int min, int max)
    {
        var text = Value(option);
        if (text is null)
        {
            return fallback;
        }

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) || number < min || number > max)
        {
            throw new UsageException($"--{option} erwartet eine Zahl von {min} bis {max}, nicht „{text}“.", Command?.Name);
        }

        return number;
    }
}

/// <summary>
/// A small parser for the command line (no library: the program stays small and trimmable).
/// <c>thixsync-cli &lt;command&gt; [arguments] [options]</c>; options as <c>--name</c>, <c>--name value</c> or <c>--name=value</c>,
/// anywhere after the command; <c>--</c> ends the options. <c>-h</c>/<c>--help</c> anywhere shows the help of the command.
/// </summary>
internal static class CommandLine
{
    private static readonly OptionSpec HelpOption = new("help", "Diese Hilfe", Short: "-h");

    public static ParsedCommand Parse(IReadOnlyList<string> args, IReadOnlyList<CommandSpec> commands)
    {
        if (args.Count == 0)
        {
            return Request(RequestKind.Help, null);
        }

        var first = args[0];
        if (first is "--version" or "-V" or "version")
        {
            return args.Count == 1 ? Request(RequestKind.Version, null) : throw new UsageException("--version steht allein.");
        }

        if (first is "--help" or "-h" or "help" or "/?")
        {
            if (args.Count == 1)
            {
                return Request(RequestKind.Help, null);
            }

            return args.Count == 2
                ? Request(RequestKind.Help, Find(args[1], commands))
                : throw new UsageException("help erwartet höchstens einen Befehl.");
        }

        if (first.StartsWith('-'))
        {
            throw new UsageException($"Zuerst einen Befehl angeben, dann „{first}“.");
        }

        var command = Find(first, commands);
        return ParseCommand(command, args.Skip(1).ToList());
    }

    /// <summary>A command by its name (case does not matter).</summary>
    public static CommandSpec Find(string name, IReadOnlyList<CommandSpec> commands) =>
        commands.FirstOrDefault(command => string.Equals(command.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new UsageException($"Unbekannter Befehl „{name}“.");

    private static ParsedCommand ParseCommand(CommandSpec command, List<string> args)
    {
        var arguments = new List<string>();
        var flags = new HashSet<string>(StringComparer.Ordinal);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var optionsEnded = false;
        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            if (optionsEnded || !arg.StartsWith('-') || arg == "-")
            {
                arguments.Add(arg);
                continue;
            }

            if (arg == "--")
            {
                optionsEnded = true;
                continue;
            }

            var (name, inlineValue) = SplitOption(arg);
            var option = FindOption(command, name, arg);
            if (option == HelpOption)
            {
                return Request(RequestKind.Help, command);
            }

            if (!option.TakesValue)
            {
                if (inlineValue is not null)
                {
                    throw new UsageException($"--{option.Name} hat keinen Wert.", command.Name);
                }

                flags.Add(option.Name);
                continue;
            }

            var value = inlineValue;
            if (value is null)
            {
                if (index + 1 >= args.Count)
                {
                    throw new UsageException($"--{option.Name} erwartet <{option.ValueName}>.", command.Name);
                }

                value = args[++index];
            }

            if (!values.TryAdd(option.Name, value))
            {
                throw new UsageException($"--{option.Name} ist doppelt angegeben.", command.Name);
            }
        }

        if (arguments.Count < command.MinArguments)
        {
            throw new UsageException($"Es fehlt etwas: {command.Usage}", command.Name);
        }

        if (arguments.Count > command.MaxArguments)
        {
            throw new UsageException($"Zu viele Angaben: „{arguments[command.MaxArguments]}“. Aufruf: {command.Usage}", command.Name);
        }

        return new ParsedCommand(RequestKind.Command, command, arguments, flags, values);
    }

    private static (string Name, string? Value) SplitOption(string arg)
    {
        if (!arg.StartsWith("--", StringComparison.Ordinal))
        {
            return (arg, null);
        }

        var separator = arg.IndexOf('=', StringComparison.Ordinal);
        return separator < 0 ? (arg, null) : (arg[..separator], arg[(separator + 1)..]);
    }

    private static OptionSpec FindOption(CommandSpec command, string name, string arg)
    {
        foreach (var option in command.Options.Append(HelpOption))
        {
            if (name == $"--{option.Name}" || (option.Short is not null && name == option.Short))
            {
                return option;
            }
        }

        throw new UsageException($"Unbekannte Option „{arg}“ für {command.Name}.", command.Name);
    }

    private static ParsedCommand Request(RequestKind kind, CommandSpec? command) =>
        new(kind, command, [], new HashSet<string>(), new Dictionary<string, string>());
}
