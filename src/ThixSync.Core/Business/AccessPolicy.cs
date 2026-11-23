using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;
using ThixSync.Core.Infrastructure;
using ThixSync.Core.Models;

namespace ThixSync.Core.Business;

/// <summary>
/// The permission checks of the business layer. Every service calls them itself before it changes something, whatever
/// the UI shows or hides.
///
/// An administrator can restrict ThixSync with machine policies (value 1 / true):
///   Windows: HKLM\SOFTWARE\Policies\ThixSync (DWORD)
///   macOS:   a configuration profile for the domain info.schmoldt.thixsync (/Library/Managed Preferences)
/// Policies:
///   LockConfiguration   settings, connections and jobs are read-only (the jobs still run)
///   DisableCloud        no connection to MatCloud (profiles are not synced; MatCloud is no storage)
///   DisableUpdates      no update check, no update installation
///   DisableDelete       jobs never delete a file anywhere (deletions are not carried over, Mirror keeps extra files)
///   DisableCloudStorage no third-party cloud storage (Google Drive, OneDrive, S3 …) as source or target
///   DisableRemote       this computer offers no folders to other computers (ThixSync Remote) and connects to none
/// </summary>
public static class AccessPolicy
{
    public const string MacPolicyDomain = "info.schmoldt.thixsync";

    private const string PolicyKeyPath = @"SOFTWARE\Policies\ThixSync";

    private static readonly string MacPolicyFile = $"/Library/Managed Preferences/{MacPolicyDomain}.plist";

    private static Func<string, bool> _readPolicy = ReadMachinePolicy;

    private static (DateTime Written, Dictionary<string, bool> Values) _macPolicies = (DateTime.MinValue, []);

    public static bool IsConfigurationLocked => _readPolicy("LockConfiguration");

    public static bool IsCloudDisabled => _readPolicy("DisableCloud");

    public static bool AreUpdatesDisabled => _readPolicy("DisableUpdates");

    public static bool IsDeleteDisabled => _readPolicy("DisableDelete");

    public static bool IsCloudStorageDisabled => _readPolicy("DisableCloudStorage");

    public static bool IsRemoteDisabled => _readPolicy("DisableRemote");

    public static void EnsureDeleteAllowed()
    {
        if (IsDeleteDisabled)
        {
            throw new BusinessException("Löschen wurde von einem Administrator deaktiviert (Richtlinie DisableDelete).");
        }
    }

    /// <summary>Whether a connection type may be used on this computer (checked when saving and when a job runs).</summary>
    public static bool IsConnectionKindAllowed(ConnectionKind kind) => kind switch
    {
        ConnectionKind.MatCloud => !IsCloudDisabled,
        ConnectionKind.ThixSyncRemote => !IsRemoteDisabled,
        _ => Storage.ConnectionCatalog.Get(kind).Group != ConnectionGroup.Cloud || !IsCloudStorageDisabled,
    };

    public static void EnsureConnectionKindAllowed(ConnectionKind kind)
    {
        if (IsConnectionKindAllowed(kind))
        {
            return;
        }

        var policy = kind switch
        {
            ConnectionKind.MatCloud => "DisableCloud",
            ConnectionKind.ThixSyncRemote => "DisableRemote",
            _ => "DisableCloudStorage",
        };
        throw new BusinessException($"Dieser Verbindungstyp wurde von einem Administrator deaktiviert (Richtlinie {policy}).", "Kind");
    }

    public static void EnsureRemoteAllowed()
    {
        if (IsRemoteDisabled)
        {
            throw new BusinessException("ThixSync Remote wurde von einem Administrator deaktiviert (Richtlinie DisableRemote).");
        }
    }

    public static void EnsureConfigurationEditable()
    {
        if (IsConfigurationLocked)
        {
            throw new BusinessException("Die Einstellungen wurden von einem Administrator gesperrt (Richtlinie LockConfiguration).");
        }
    }

    public static void EnsureCloudAllowed()
    {
        if (IsCloudDisabled)
        {
            throw new BusinessException("Die Cloud-Synchronisierung wurde von einem Administrator deaktiviert (Richtlinie DisableCloud).");
        }
    }

    public static void EnsureUpdatesAllowed()
    {
        if (AreUpdatesDisabled)
        {
            throw new BusinessException("Updates wurden von einem Administrator deaktiviert (Richtlinie DisableUpdates).");
        }
    }

    /// <summary>Tests replace the registry with their own answers.</summary>
    internal static void UsePolicyReaderForTests(Func<string, bool>? reader) => _readPolicy = reader ?? ReadMachinePolicy;

    private static bool ReadMachinePolicy(string name)
    {
        if (OperatingSystem.IsWindows())
        {
            return ReadWindowsPolicy(name);
        }

        return OperatingSystem.IsMacOS() && ReadMacPolicies().GetValueOrDefault(name);
    }

    [SupportedOSPlatform("windows")]
    private static bool ReadWindowsPolicy(string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(PolicyKeyPath);
        return key?.GetValue(name) is int value && value == 1;
    }

    /// <summary>The managed preferences are a (binary) plist; plutil turns it into JSON. Read again only after a change.</summary>
    private static Dictionary<string, bool> ReadMacPolicies()
    {
        var written = File.Exists(MacPolicyFile) ? File.GetLastWriteTimeUtc(MacPolicyFile) : DateTime.MinValue;
        if (written == _macPolicies.Written)
        {
            return _macPolicies.Values;
        }

        var values = new Dictionary<string, bool>();
        if (written != DateTime.MinValue)
        {
            try
            {
                var startInfo = new ProcessStartInfo("/usr/bin/plutil") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var argument in new[] { "-convert", "json", "-o", "-", MacPolicyFile })
                {
                    startInfo.ArgumentList.Add(argument);
                }

                using var process = Process.Start(startInfo)!;
                var json = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                using var document = JsonDocument.Parse(json);
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    values[property.Name] = property.Value.ValueKind == JsonValueKind.True ||
                                            (property.Value.ValueKind == JsonValueKind.Number && property.Value.GetInt32() == 1);
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Log.Warning($"Could not read the policies in {MacPolicyFile}: {exception.Message}");
            }
        }

        _macPolicies = (written, values);
        return values;
    }
}
