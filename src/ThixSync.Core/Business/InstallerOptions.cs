using System.Text.Json;
using ThixSync.Core.Infrastructure;
using ThixSync.Core.Models;

namespace ThixSync.Core.Business;

/// <summary>
/// The install options of ThixSync (SETUP-08 of MatDesign) – one contract for every Thix app:
/// <list type="bullet">
/// <item><see cref="Config"/>: what the setup offers, from <c>installer.json</c> (embedded; CI puts the same file into the update
/// manifest as <c>"installer"</c>, where ThixLauncher and ThixInstall read it).</item>
/// <item><see cref="Parse"/>: the command line of SETUP-05 – <c>--quiet</c> (install), <c>--update</c>, <c>--configure</c>
/// (only apply), <c>--uninstall</c>, <c>--start</c>, and the options <c>--desktop-shortcut=yes|no</c>, <c>--autostart=yes|no</c>,
/// <c>--automatic-updates=yes|no</c>, <c>--cli-path=yes|no</c>, <c>--file-types=.md,.txt|none|default</c>. A wrong value fails at
/// once (exit code 2).</item>
/// <item><see cref="Current"/> and <see cref="Apply"/>: what the installed app has, and making it so (shortcut, autostart,
/// automatic updates, the command line on the PATH).</item>
/// </list>
/// ThixSync opens no files: its <c>fileTypes</c> are empty, so <c>--file-types</c> takes only <c>none</c> or <c>default</c>.
/// An app that offers file types associates them here (ThixFred: Platform/Windows/FileAssociations, Mac/MacFileAssociations).
/// </summary>
public static class InstallerOptions
{
    public const string QuietArgument = "--quiet";
    public const string UpdateArgument = UpdateService.WindowsUpdateArgument;
    public const string ConfigureArgument = "--configure";
    public const string UninstallArgument = "--uninstall";
    public const string StartArgument = "--start";
    public const string DesktopShortcutOption = "--desktop-shortcut";
    public const string AutostartOption = "--autostart";
    public const string AutomaticUpdatesOption = "--automatic-updates";
    public const string CliPathOption = "--cli-path";
    public const string FileTypesOption = "--file-types";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static InstallerConfig? _config;

    /// <summary>What the setup of ThixSync offers (installer.json).</summary>
    public static InstallerConfig Config => _config ??= Load();

    /// <summary>The choices of a first install that is told nothing: the defaults of <see cref="Config"/>.</summary>
    public static InstallChoices Defaults => new(
        Config.DesktopShortcut?.Default ?? false,
        Config.Autostart?.Default ?? false,
        Config.AutomaticUpdates?.Default ?? false,
        Config.FileTypes.Where(type => type.Default).Select(type => type.Extension).ToList(),
        Config.CliPath?.Default ?? false);

    /// <summary>Another configuration (tests); null = installer.json again.</summary>
    public static void UseConfigForTests(InstallerConfig? config) => _config = config;

    /// <summary>
    /// Reads the setup part of a command line. Throws <see cref="BusinessException"/> for a value it does not know – a quiet
    /// install must not guess (SETUP-05). A bare "--autostart" (no "=") is the start at sign-in, not an option.
    /// </summary>
    public static SetupRequest Parse(IReadOnlyList<string> args)
    {
        var action = args.Contains(UninstallArgument) ? SetupAction.Uninstall
            : args.Contains(ConfigureArgument) ? SetupAction.Configure
            : args.Contains(UpdateArgument) ? SetupAction.Update
            : args.Contains(QuietArgument) ? SetupAction.Install
            : SetupAction.None;
        var choices = new InstallChoices();
        foreach (var arg in args)
        {
            var separator = arg.IndexOf('=', StringComparison.Ordinal);
            if (!arg.StartsWith("--", StringComparison.Ordinal) || separator < 0)
            {
                continue;
            }

            var (name, value) = (arg[..separator], arg[(separator + 1)..].Trim());
            choices = name switch
            {
                DesktopShortcutOption => choices with { DesktopShortcut = Switch(name, value, Config.DesktopShortcut) },
                AutostartOption => choices with { Autostart = Switch(name, value, Config.Autostart) },
                AutomaticUpdatesOption => choices with { AutomaticUpdates = Switch(name, value, Config.AutomaticUpdates) },
                CliPathOption => choices with { CliPath = Switch(name, value, Config.CliPath) },
                FileTypesOption => choices with { FileTypes = FileTypes(value) },
                _ => throw new BusinessException($"Unbekannte Option „{name}“. Möglich: {DesktopShortcutOption}, {AutostartOption}, {AutomaticUpdatesOption}, {CliPathOption}, {FileTypesOption}."),
            };
        }

        if (action is SetupAction.None or SetupAction.Uninstall && choices != new InstallChoices())
        {
            throw new BusinessException($"Optionen gelten nur mit {QuietArgument}, {UpdateArgument} oder {ConfigureArgument}.");
        }

        return new SetupRequest(action, choices, args.Contains(QuietArgument), args.Contains(StartArgument));
    }

    /// <summary>The arguments of SETUP-05 for these choices (what ThixLauncher passes; tests and documentation).</summary>
    public static IReadOnlyList<string> ToArguments(InstallChoices choices)
    {
        var arguments = new List<string>();
        static string YesNo(bool value) => value ? "yes" : "no";
        if (choices.DesktopShortcut is { } shortcut)
        {
            arguments.Add($"{DesktopShortcutOption}={YesNo(shortcut)}");
        }

        if (choices.Autostart is { } autostart)
        {
            arguments.Add($"{AutostartOption}={YesNo(autostart)}");
        }

        if (choices.AutomaticUpdates is { } updates)
        {
            arguments.Add($"{AutomaticUpdatesOption}={YesNo(updates)}");
        }

        if (choices.CliPath is { } cliPath)
        {
            arguments.Add($"{CliPathOption}={YesNo(cliPath)}");
        }

        if (choices.FileTypes is { } types)
        {
            arguments.Add($"{FileTypesOption}={(types.Count == 0 ? "none" : string.Join(',', types))}");
        }

        return arguments;
    }

    /// <summary>What the installed ThixSync has now (an update keeps it).</summary>
    public static InstallChoices Current() => new(
        OperatingSystem.IsWindows() && InstallService.HasDesktopShortcut,
        AutostartService.IsEnabled(),
        SettingsService.Get().InstallUpdatesAutomatically,
        [],
        OperatingSystem.IsWindows() && InstallService.IsCliOnPath);

    /// <summary>
    /// Makes the choices so for the program <paramref name="executable"/> (the installed one). Only offered options are
    /// touched; a value of null stays as it is. Each step is reported to <paramref name="log"/>.
    /// </summary>
    public static void Apply(InstallChoices choices, string executable, Action<string> log)
    {
        if (Config.DesktopShortcut is not null && choices.DesktopShortcut is { } shortcut && OperatingSystem.IsWindows())
        {
            InstallService.SetDesktopShortcut(shortcut);
            log(shortcut ? "Desktop-Verknüpfung angelegt" : "Keine Desktop-Verknüpfung");
        }

        if (Config.Autostart is not null && choices.Autostart is { } autostart)
        {
            if (autostart)
            {
                AutostartService.WriteEntry(executable);
                log(OperatingSystem.IsMacOS() ? "Startet bei der Anmeldung" : "Autostart bei der Windows-Anmeldung eingerichtet");
            }
            else
            {
                AutostartService.RemoveEntry();
                log("Kein Autostart");
            }
        }

        if (Config.AutomaticUpdates is not null && choices.AutomaticUpdates is { } updates)
        {
            try
            {
                SettingsService.Update(settings => settings with { InstallUpdatesAutomatically = updates });
                log(updates ? "Updates werden automatisch installiert" : "Updates werden nur angeboten");
            }
            catch (BusinessException exception)
            {
                // a configuration locked by policy keeps its value
                log(exception.Message);
            }
        }

        // macOS: the command line sits in the bundle (Contents/MacOS/thixsync-cli); a link on the PATH would need sudo
        if (Config.CliPath is not null && choices.CliPath is { } cliPath && OperatingSystem.IsWindows())
        {
            log(InstallService.SetCliOnPath(cliPath));
        }
    }

    private static bool Switch(string name, string value, InstallerSwitch? offered)
    {
        if (offered is null)
        {
            throw new BusinessException($"{AppInfo.ProductName} bietet „{name}“ nicht an.");
        }

        return value.ToLowerInvariant() switch
        {
            "yes" or "true" or "1" or "ja" => true,
            "no" or "false" or "0" or "nein" => false,
            _ => throw new BusinessException($"{name} erwartet yes oder no, nicht „{value}“."),
        };
    }

    private static IReadOnlyList<string> FileTypes(string value)
    {
        if (value.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            return Defaults.FileTypes!;
        }

        if (value.Length == 0 || value.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var types = new List<string>();
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var extension = part.StartsWith('.') ? part : "." + part;
            var offered = Config.FileTypes.FirstOrDefault(type => type.Extension.Equals(extension, StringComparison.OrdinalIgnoreCase))
                          ?? throw new BusinessException(Config.FileTypes.Count == 0
                              ? $"{AppInfo.ProductName} verknüpft keine Dateitypen."
                              : $"{AppInfo.ProductName} verknüpft „{extension}“ nicht. Möglich: {string.Join(", ", Config.FileTypes.Select(type => type.Extension))}.");
            if (!types.Contains(offered.Extension))
            {
                types.Add(offered.Extension);
            }
        }

        return types;
    }

    private static InstallerConfig Load()
    {
        using var stream = typeof(InstallerOptions).Assembly.GetManifestResourceStream("installer.json")
                           ?? throw new InvalidOperationException("installer.json is not embedded");
        return JsonSerializer.Deserialize<InstallerConfig>(stream, JsonOptions) ?? new InstallerConfig();
    }
}
