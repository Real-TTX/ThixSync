using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;
using ThixSync.Core.Infrastructure;
using ThixSync.Core.Models;

namespace ThixSync.Core.Business;

/// <summary>
/// Windows only (macOS: the app bundle, <see cref="MacAppBundle"/>). ThixSync installs itself - like ThixTunnel, the
/// Matgate agent and the MatMon setup - without a separate installer tool.
/// The published ThixSync.exe is the setup: started from anywhere else it copies itself to
/// %LocalAppData%\Programs\ThixSync (per user, no administrator rights), creates the Start menu entry, the entry in
/// "Installierte Apps" and – as the install options say (InstallerOptions, SETUP-08) – the autostart, a desktop shortcut,
/// automatic updates and the command line on the PATH. "--uninstall" removes every one of them.
/// The command line thixsync-cli.exe travels inside ThixSync.exe (an embedded resource CI adds, see ThixSync.csproj): installing
/// and updating put it next to ThixSync.exe; the option "cli-path" adds the program folder to the user's PATH.
/// User data (%LocalAppData%\ThixSync: history, settings) stays unless the user explicitly deletes it while uninstalling.
/// </summary>
[SupportedOSPlatform("windows")]
public static class InstallService
{
    public const string UpdateArgument = InstallerOptions.UpdateArgument;
    public const string UninstallArgument = InstallerOptions.UninstallArgument;
    public const string QuietArgument = InstallerOptions.QuietArgument;
    public const string ExecutableName = "ThixSync.exe";

    /// <summary>The command line next to ThixSync.exe (not "thixsync.exe": it would clash with ThixSync.exe, names ignore case).</summary>
    public const string CliExecutableName = "thixsync-cli.exe";

    /// <summary>The name of the command line embedded in ThixSync.exe (LogicalName in ThixSync.csproj).</summary>
    public const string CliResourceName = "thixsync-cli.exe";

    /// <summary>A running ThixSync listens for this signal and ends itself cleanly.</summary>
    public const string ExitSignalName = "ThixSync.Exit";

    private const string DefaultUninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ThixSync";
    private const string DefaultEnvironmentKeyPath = "Environment";
    private const string PathValueName = "Path";

    private static TestLocations? _testLocations;
    private static Func<Stream?>? _cliSourceForTests;

    public static string ProgramFolder => _testLocations?.ProgramFolder ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppInfo.ProductName);

    public static string InstalledExecutable => Path.Combine(ProgramFolder, ExecutableName);

    public static string StartMenuShortcut => Path.Combine(
        _testLocations?.ShortcutFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.Programs), $"{AppInfo.ProductName}.lnk");

    public static string DesktopShortcut => Path.Combine(
        _testLocations?.DesktopFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"{AppInfo.ProductName}.lnk");

    /// <summary>The command line in the program folder (there only when the setup carried it).</summary>
    public static string InstalledCli => Path.Combine(ProgramFolder, CliExecutableName);

    /// <summary>The program folder is in the user's PATH, so a terminal finds thixsync-cli.</summary>
    public static bool IsCliOnPath => PathContains(ReadUserPath() ?? "", ProgramFolder);

    private static string UninstallKeyPath => _testLocations?.UninstallKeyPath ?? DefaultUninstallKeyPath;

    private static string EnvironmentKeyPath => _testLocations?.EnvironmentKeyPath ?? DefaultEnvironmentKeyPath;

    /// <summary>Installed means: the entry in "Installierte Apps" and the program file exist.</summary>
    public static bool IsInstalled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath);
            return key is not null && File.Exists(InstalledExecutable);
        }
    }

    public static string? InstalledVersion
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath);
            return key?.GetValue("DisplayVersion") as string;
        }
    }

    public static bool HasDesktopShortcut => File.Exists(DesktopShortcut);

    public static bool IsInstalledExecutable(string path) =>
        string.Equals(Path.GetFullPath(path), Path.GetFullPath(InstalledExecutable), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Installs or updates with the install options (SETUP-08): what <paramref name="choices"/> leaves open (null, or a value of
    /// null) an installed ThixSync keeps as it is and a first install takes from installer.json.
    /// </summary>
    public static void Install(string sourceExecutable, InstallChoices? choices, Action<string> log)
    {
        var isUpdate = IsInstalled;
        var wanted = (choices ?? new InstallChoices()).Over(isUpdate ? InstallerOptions.Current() : InstallerOptions.Defaults);
        log(isUpdate ? $"Aktualisiere ThixSync in {ProgramFolder}" : $"Installiere ThixSync nach {ProgramFolder}");
        StopRunningInstances(log);

        Directory.CreateDirectory(ProgramFolder);
        if (!IsInstalledExecutable(sourceExecutable))
        {
            CopyWithRetry(sourceExecutable, InstalledExecutable);
            log("Programmdatei kopiert");
        }

        ExtractCli(log);
        CreateShortcut(StartMenuShortcut, "Synchronisation und Backup");
        log("Startmenü-Eintrag angelegt");

        // also an update writes them again: the autostart points at the installed file
        InstallerOptions.Apply(wanted, InstalledExecutable, log);

        WriteUninstallEntry();
        log("Eintrag unter „Installierte Apps“ geschrieben");
        log(isUpdate ? "Update abgeschlossen" : "Installation abgeschlossen");
    }

    /// <summary>
    /// Removes program, shortcuts, autostart and the entry in "Installierte Apps"; the user data only on request.
    /// Returns true when the program folder could not be deleted yet because this process runs from it -
    /// then call <see cref="ScheduleProgramFolderRemoval"/> right before the process ends.
    /// </summary>
    public static bool Uninstall(bool deleteUserData, Action<string> log)
    {
        log("Entferne ThixSync");
        StopRunningInstances(log);

        AutostartService.RemoveEntry();
        log("Autostart entfernt");

        DeleteFile(StartMenuShortcut);
        DeleteFile(DesktopShortcut);
        log("Verknüpfungen entfernt");
        if (RemoveFromUserPath(ProgramFolder))
        {
            log("Befehlszeile thixsync-cli aus dem PATH entfernt");
        }

        Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false);
        log("Eintrag unter „Installierte Apps“ entfernt");

        if (deleteUserData)
        {
            DeleteFolder(AppPaths.DataDirectory);
            log("Verbindungen, Aufträge, Einstellungen und Protokolle gelöscht");
        }

        var runsFromProgramFolder = (Environment.ProcessPath ?? "").StartsWith(ProgramFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (runsFromProgramFolder)
        {
            log("Programmordner wird nach dem Schließen gelöscht");
            return true;
        }

        DeleteFolder(ProgramFolder);
        log("Programmordner gelöscht");
        return false;
    }

    /// <summary>The program folder cannot go while this exe runs from it: a short-lived cmd removes it afterwards.</summary>
    public static void ScheduleProgramFolderRemoval() =>
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 4 127.0.0.1 >nul & rmdir /s /q \"{ProgramFolder}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        });

    /// <summary>Starts the installed ThixSync (after setup or update).</summary>
    public static void StartInstalled(string arguments = "") =>
        Process.Start(new ProcessStartInfo(InstalledExecutable, arguments) { UseShellExecute = false, WorkingDirectory = ProgramFolder });

    /// <summary>
    /// Redirects program folder, shortcuts, the uninstall entry and the key holding the user's PATH for tests; null = the real
    /// places. With test locations the change of the PATH is not announced to the other programs.
    /// </summary>
    public static void UseLocationsForTests(string? programFolder, string? shortcutFolder = null, string? uninstallKeyPath = null, string? environmentKeyPath = null) =>
        _testLocations = programFolder is null
            ? null
            : new TestLocations(programFolder, shortcutFolder ?? programFolder, Path.Combine(shortcutFolder ?? programFolder, "Desktop"),
                uninstallKeyPath ?? @"Software\ThixSyncTests\Uninstall", environmentKeyPath ?? @"Software\ThixSyncTests\Environment");

    /// <summary>Another source of the embedded command line (tests); null = the resource of the running program.</summary>
    public static void UseCliSourceForTests(Func<Stream?>? source) => _cliSourceForTests = source;

    /// <summary>
    /// Puts the command line embedded in the running program (the setup) next to ThixSync.exe. A local build carries none: nothing
    /// happens. A thixsync-cli.exe that runs right now (a script) is moved aside first – Windows lets a running program be renamed,
    /// not overwritten. Returns whether the command line is in place.
    /// </summary>
    internal static bool ExtractCli(Action<string> log)
    {
        using var source = (_cliSourceForTests ?? EmbeddedCli)();
        if (source is null)
        {
            return false;
        }

        var fresh = InstalledCli + ".new";
        var aside = InstalledCli + ".old";
        DeleteFile(aside);
        using (var target = File.Create(fresh))
        {
            source.CopyTo(target);
        }

        try
        {
            if (File.Exists(InstalledCli))
            {
                File.Move(InstalledCli, aside, overwrite: true);
            }

            File.Move(fresh, InstalledCli);
            DeleteFile(aside);
            log("Befehlszeile thixsync-cli eingerichtet");
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Could not place the command line: {exception.Message}");
            DeleteFile(fresh);
            log("Die Befehlszeile thixsync-cli konnte nicht ersetzt werden");
            return File.Exists(InstalledCli);
        }
    }

    /// <summary>The command line on the PATH or off it (install option "cli-path"). Returns the line for the setup's log.</summary>
    internal static string SetCliOnPath(bool wanted)
    {
        if (!wanted)
        {
            return RemoveFromUserPath(ProgramFolder) ? "Befehlszeile thixsync-cli aus dem PATH entfernt" : "Befehlszeile thixsync-cli nicht im PATH";
        }

        if (!File.Exists(InstalledCli))
        {
            return "Dieser Build enthält keine Befehlszeile (thixsync-cli)";
        }

        AddToUserPath(ProgramFolder);
        return "Befehlszeile thixsync-cli im PATH (in neu geöffneten Terminals)";
    }

    /// <summary>Adds a folder to the user's PATH (HKCU\Environment) once and tells the running programs. Returns whether it was added.</summary>
    internal static bool AddToUserPath(string folder)
    {
        var current = ReadUserPath() ?? "";
        if (PathContains(current, folder))
        {
            return false;
        }

        WriteUserPath(WithFolder(current, folder));
        return true;
    }

    /// <summary>Takes a folder out of the user's PATH. Returns whether it was there.</summary>
    internal static bool RemoveFromUserPath(string folder)
    {
        var current = ReadUserPath();
        if (current is null || !PathContains(current, folder))
        {
            return false;
        }

        WriteUserPath(WithoutFolder(current, folder));
        return true;
    }

    /// <summary>Whether a PATH value (entries separated by ';', perhaps with %VARIABLES% or quotes) names the folder.</summary>
    internal static bool PathContains(string pathValue, string folder) =>
        PathEntries(pathValue).Any(entry => IsSameFolder(entry, folder));

    /// <summary>The PATH value with the folder at the end (unchanged when it is there already).</summary>
    internal static string WithFolder(string pathValue, string folder)
    {
        if (PathContains(pathValue, folder))
        {
            return pathValue;
        }

        var trimmed = pathValue.TrimEnd(';');
        return trimmed.Length == 0 ? folder : $"{trimmed};{folder}";
    }

    /// <summary>The PATH value without the folder; every other entry stays as it was written.</summary>
    internal static string WithoutFolder(string pathValue, string folder) =>
        string.Join(';', PathEntries(pathValue).Where(entry => !IsSameFolder(entry, folder)));

    private static Stream? EmbeddedCli() =>
        System.Reflection.Assembly.GetEntryAssembly()?.GetManifestResourceStream(CliResourceName);

    private static IEnumerable<string> PathEntries(string pathValue) =>
        pathValue.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool IsSameFolder(string entry, string folder)
    {
        static string Normalize(string path) => Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')).TrimEnd('\\', '/');
        return string.Equals(Normalize(entry), Normalize(folder), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The user's PATH as written (%VARIABLES% not expanded); null when there is none.</summary>
    private static string? ReadUserPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(EnvironmentKeyPath);
        return key?.GetValue(PathValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    /// <summary>Writes the user's PATH (keeps the kind of the value, REG_EXPAND_SZ for a new one) and announces the change.</summary>
    private static void WriteUserPath(string value)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(EnvironmentKeyPath))
        {
            var kind = key.GetValueNames().Contains(PathValueName, StringComparer.OrdinalIgnoreCase)
                ? key.GetValueKind(PathValueName)
                : RegistryValueKind.ExpandString;
            if (value.Length == 0)
            {
                key.DeleteValue(PathValueName, throwOnMissingValue: false);
            }
            else
            {
                key.SetValue(PathValueName, value, kind == RegistryValueKind.String ? RegistryValueKind.String : RegistryValueKind.ExpandString);
            }
        }

        if (_testLocations is null)
        {
            Platform.Windows.EnvironmentChange.Announce();
        }
    }

    /// <summary>Asks a running ThixSync from the program folder to end, and ends it when it does not react in time.</summary>
    private static void StopRunningInstances(Action<string> log)
    {
        var running = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExecutableName))
            .Where(process => process.Id != Environment.ProcessId && RunsFrom(process, InstalledExecutable))
            .ToList();
        if (running.Count == 0)
        {
            return;
        }

        log("Beende das laufende ThixSync");
        if (_testLocations is null && EventWaitHandle.TryOpenExisting(ExitSignalName, out var exitSignal))
        {
            exitSignal.Set();
            exitSignal.Dispose();
        }

        foreach (var process in running)
        {
            try
            {
                if (!process.WaitForExit(TimeSpan.FromSeconds(15)))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(TimeSpan.FromSeconds(10));
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static bool RunsFrom(Process process, string executable)
    {
        try
        {
            return string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>The old program may need a moment to release its file after it ended.</summary>
    private static void CopyWithRetry(string source, string target)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Copy(source, target, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(500);
            }
        }
    }

    /// <summary>The desktop shortcut on or off (install options).</summary>
    internal static void SetDesktopShortcut(bool wanted)
    {
        if (wanted)
        {
            CreateShortcut(DesktopShortcut, "Synchronisation und Backup");
        }
        else
        {
            DeleteFile(DesktopShortcut);
        }
    }

    private static void CreateShortcut(string path, string description)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Platform.Windows.ShellLink.Write(path, InstalledExecutable, ProgramFolder, description, InstalledExecutable);
    }

    private static void WriteUninstallEntry()
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKeyPath);
        key.SetValue("DisplayName", AppInfo.ProductName);
        key.SetValue("DisplayVersion", AppInfo.Version);
        key.SetValue("Publisher", "Matthias Schmoldt");
        key.SetValue("DisplayIcon", InstalledExecutable + ",0");
        key.SetValue("InstallLocation", ProgramFolder);
        key.SetValue("UninstallString", $"\"{InstalledExecutable}\" {UninstallArgument}");
        key.SetValue("QuietUninstallString", $"\"{InstalledExecutable}\" {UninstallArgument} {QuietArgument}");
        key.SetValue("URLInfoAbout", $"https://github.com/{AppInfo.Repository}");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExecutable).Length / 1024), RegistryValueKind.DWord);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
    }

    private static void DeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Could not delete {path}: {exception.Message}");
        }
    }

    private static void DeleteFolder(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Could not delete {path}: {exception.Message}");
        }
    }

    private sealed record TestLocations(string ProgramFolder, string ShortcutFolder, string DesktopFolder, string UninstallKeyPath, string EnvironmentKeyPath);
}
