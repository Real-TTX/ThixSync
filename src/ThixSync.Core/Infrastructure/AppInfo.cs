using System.Reflection;
using System.Runtime.InteropServices;

namespace ThixSync.Core.Infrastructure;

/// <summary>Version, channel and build number baked in at build time (Directory.Build.props).</summary>
public static class AppInfo
{
    private static readonly Assembly ThisAssembly = typeof(AppInfo).Assembly;

    private static readonly string BuiltVersion =
        ThisAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "local";

    private static readonly string BuiltChannel = ReadMetadata("ThixSyncChannel") ?? "local";

    private static readonly int BuiltNumber = int.TryParse(ReadMetadata("ThixSyncBuildNumber"), out var number) ? number : 0;

    private static Pinned? _pinned;

    public const string ProductName = "ThixSync";

    /// <summary>e.g. 0.1.42-20261114, nightly-43-20261114 or local-20261114.</summary>
    public static string Version => _pinned?.Version ?? BuiltVersion;

    /// <summary>release, nightly or local.</summary>
    public static string Channel => _pinned?.Channel ?? BuiltChannel;

    public static int BuildNumber => _pinned?.BuildNumber ?? BuiltNumber;

    /// <summary>owner/name of the GitHub repository that publishes the builds.</summary>
    public static string Repository { get; } = ReadMetadata("ThixSyncRepository") ?? "Real-TTX/ThixSync";

    public static bool IsLocalBuild => Channel == "local";

    /// <summary>"Windows" or "macOS" (sent to the cloud with the device).</summary>
    public static string PlatformName => OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : RuntimeInformation.OSDescription;

    /// <summary>The name of this computer, offered as the device name for the cloud.</summary>
    public static string MachineName
    {
        get
        {
            if (_pinned is not null)
            {
                return _pinned.MachineName;
            }

            try
            {
                return Environment.MachineName;
            }
            catch (InvalidOperationException)
            {
                return "Computer";
            }
        }
    }

    /// <summary>
    /// Shows a fixed version, channel and computer name instead of the built ones (the snapshots of "ThixSync --snapshot"
    /// look the same from every build and machine); null = the built values again.
    /// </summary>
    internal static void Pin(string? version, string channel = "release", int buildNumber = 0, string machineName = "Computer") =>
        _pinned = version is null ? null : new Pinned(version, channel, buildNumber, machineName);

    private static string? ReadMetadata(string key) =>
        ThisAssembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(attribute => attribute.Key == key)?.Value;

    private sealed record Pinned(string Version, string Channel, int BuildNumber, string MachineName);
}
