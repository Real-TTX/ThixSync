using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;
using ThixSync.Core.Business;
using ThixSync.Core.Models;

namespace ThixSync.Tests;

/// <summary>
/// Shipping the command line with the app: the setup puts the thixsync-cli.exe it carries next to ThixSync.exe and the program folder
/// on the user's PATH (install option "cli-path"); uninstalling removes both. Only test folders and a test registry key are touched.
/// </summary>
public sealed class CliSetupTests : IDisposable
{
    private const string EnvironmentKey = @"Software\ThixSyncTests\Environment";

    private readonly TestData _data = new();

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            InstallService.UseLocationsForTests(null);
            InstallService.UseCliSourceForTests(null);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\ThixSyncTests", throwOnMissingSubKey: false);
        }

        AutostartService.UseInMemoryEntry(null);
        InstallerOptions.UseConfigForTests(null);
        _data.Dispose();
    }

    [Fact]
    public void The_install_option_cli_path_is_offered_and_on_by_default()
    {
        Assert.NotNull(InstallerOptions.Config.CliPath);
        Assert.True(InstallerOptions.Defaults.CliPath);

        var request = InstallerOptions.Parse(["--quiet", "--cli-path=no"]);
        Assert.False(request.Choices.CliPath);
        Assert.Equal(["--cli-path=no"], InstallerOptions.ToArguments(request.Choices));
        Assert.True(InstallerOptions.Parse(["--update", "--cli-path=ja"]).Choices.CliPath);
        Assert.Null(InstallerOptions.Parse(["--quiet"]).Choices.CliPath);
        Assert.Equal(new InstallChoices(CliPath: false), new InstallChoices().Over(new InstallChoices(CliPath: false)));

        Assert.Throws<BusinessException>(() => InstallerOptions.Parse(["--quiet", "--cli-path=vielleicht"]));
        Assert.Throws<BusinessException>(() => InstallerOptions.Parse(["--uninstall", "--cli-path=no"]));
        InstallerOptions.UseConfigForTests(InstallerOptions.Config with { CliPath = null });
        Assert.Throws<BusinessException>(() => InstallerOptions.Parse(["--quiet", "--cli-path=yes"]));
    }

    [Fact]
    public void The_path_value_gets_the_folder_once_and_loses_only_it()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string folder = @"C:\Users\anna\AppData\Local\Programs\ThixSync";
        Assert.Equal(folder, InstallService.WithFolder("", folder));
        Assert.Equal($@"C:\Tools;{folder}", InstallService.WithFolder(@"C:\Tools;", folder));
        Assert.Equal($@"C:\Tools;%USERPROFILE%\bin;{folder}", InstallService.WithFolder(@"C:\Tools;%USERPROFILE%\bin", folder));

        // written another way (case, quotes, a trailing backslash, a variable) it is still the same folder
        var written = $@"C:\Tools;""{folder.ToUpperInvariant()}\"";D:\bin";
        Assert.True(InstallService.PathContains(written, folder));
        Assert.Equal(written, InstallService.WithFolder(written, folder));
        Assert.Equal(@"C:\Tools;D:\bin", InstallService.WithoutFolder(written, folder));
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.True(InstallService.PathContains(@"%LOCALAPPDATA%\Programs\ThixSync\", Path.Combine(local, "Programs", "ThixSync")));
        Assert.False(InstallService.PathContains(@"C:\Tools;C:\Users\anna\AppData\Local\Programs\ThixSyncX", folder));
    }

    [Fact]
    public void The_setup_puts_the_cli_next_to_the_program_and_on_the_path_and_the_uninstall_removes_both()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var program = UseTestLocations();
        using (var key = Registry.CurrentUser.CreateSubKey(EnvironmentKey))
        {
            key.SetValue("Path", @"C:\Tools;%USERPROFILE%\bin", RegistryValueKind.ExpandString);
        }

        InstallService.UseCliSourceForTests(() => new MemoryStream(Encoding.ASCII.GetBytes("cli 1")));
        var steps = new List<string>();
        InstallService.Install(Setup(), null, steps.Add);

        Assert.Equal("cli 1", File.ReadAllText(InstallService.InstalledCli));
        Assert.Equal(Path.Combine(program, "thixsync-cli.exe"), InstallService.InstalledCli);
        Assert.Contains("Befehlszeile thixsync-cli eingerichtet", steps);
        Assert.Contains("Befehlszeile thixsync-cli im PATH (in neu geöffneten Terminals)", steps);
        Assert.True(InstallService.IsCliOnPath);
        Assert.Equal($@"C:\Tools;%USERPROFILE%\bin;{program}", UserPath());
        Assert.Equal(RegistryValueKind.ExpandString, PathKind());
        Assert.True(InstallerOptions.Current().CliPath);

        // an update brings the new command line and keeps the choice; the option takes the folder off the PATH again
        InstallService.UseCliSourceForTests(() => new MemoryStream(Encoding.ASCII.GetBytes("cli 2")));
        InstallService.Install(Setup(), new InstallChoices(), _ => { });
        Assert.Equal("cli 2", File.ReadAllText(InstallService.InstalledCli));
        Assert.Equal($@"C:\Tools;%USERPROFILE%\bin;{program}", UserPath());
        InstallService.Install(Setup(), new InstallChoices(CliPath: false), _ => { });
        Assert.False(InstallService.IsCliOnPath);
        Assert.Equal(@"C:\Tools;%USERPROFILE%\bin", UserPath());
        Assert.False(InstallerOptions.Current().CliPath);
        InstallService.Install(Setup(), new InstallChoices(CliPath: true), _ => { });
        Assert.True(InstallService.IsCliOnPath);

        var removed = new List<string>();
        InstallService.Uninstall(deleteUserData: false, removed.Add);
        Assert.Contains("Befehlszeile thixsync-cli aus dem PATH entfernt", removed);
        Assert.Equal(@"C:\Tools;%USERPROFILE%\bin", UserPath());
        Assert.False(File.Exists(InstallService.InstalledCli));
    }

    [Fact]
    public void A_build_without_the_cli_leaves_the_path_alone()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        UseTestLocations();
        InstallService.UseCliSourceForTests(() => null);
        var steps = new List<string>();
        InstallService.Install(Setup(), new InstallChoices(CliPath: true), steps.Add);

        Assert.False(File.Exists(InstallService.InstalledCli));
        Assert.Contains("Dieser Build enthält keine Befehlszeile (thixsync-cli)", steps);
        Assert.False(InstallService.IsCliOnPath);
        Assert.Null(UserPath());
    }

    [Fact]
    public void A_running_cli_is_moved_aside_and_replaced()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var program = UseTestLocations();
        Directory.CreateDirectory(program);
        File.WriteAllText(InstallService.InstalledCli, "cli alt");
        InstallService.UseCliSourceForTests(() => new MemoryStream(Encoding.ASCII.GetBytes("cli neu")));

        // a running program: Windows lets it be renamed (and deleted later), not overwritten
        using (new FileStream(InstallService.InstalledCli, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            Assert.True(InstallService.ExtractCli(_ => { }));
        }

        Assert.Equal("cli neu", File.ReadAllText(InstallService.InstalledCli));
        Assert.Equal(["thixsync-cli.exe"], Directory.GetFiles(program).Select(Path.GetFileName));
    }

    [SupportedOSPlatform("windows")]
    private string UseTestLocations()
    {
        var program = Path.Combine(_data.Folder, "Programs", "ThixSync");
        InstallService.UseLocationsForTests(program, Path.Combine(_data.Folder, "StartMenu"), @"Software\ThixSyncTests\Uninstall", EnvironmentKey);
        AutostartService.UseInMemoryEntry(false);
        return program;
    }

    private string Setup()
    {
        var setup = Path.Combine(_data.Folder, "Downloads", "ThixSyncSetup.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(setup)!);
        File.WriteAllBytes(setup, new byte[1024]);
        return setup;
    }

    [SupportedOSPlatform("windows")]
    private static string? UserPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(EnvironmentKey);
        return key?.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    [SupportedOSPlatform("windows")]
    private static RegistryValueKind PathKind()
    {
        using var key = Registry.CurrentUser.OpenSubKey(EnvironmentKey)!;
        return key.GetValueKind("Path");
    }
}
