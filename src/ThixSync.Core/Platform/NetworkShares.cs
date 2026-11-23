using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using ThixSync.Core.Infrastructure;
using ThixSync.Core.Models;

namespace ThixSync.Core.Platform;

/// <summary>A share in the network: server, share and the folder below it.</summary>
public sealed record ShareAddress(string Server, string Share, string SubPath)
{
    /// <summary>\\server\share\sub (Windows).</summary>
    public string Unc => @"\\" + Server + @"\" + Share + (SubPath.Length > 0 ? @"\" + SubPath.Replace('/', '\\') : "");

    /// <summary>smb://server/share/sub (macOS, Finder).</summary>
    public string Url => $"smb://{Server}/{Share}" + (SubPath.Length > 0 ? "/" + SubPath.Replace('\\', '/') : "");

    /// <summary>\\server\share without the folder.</summary>
    public string ShareUnc => @"\\" + Server + @"\" + Share;
}

/// <summary>
/// Reaching SMB shares. Windows reads \\server\share directly; with a user name it connects first (WNetAddConnection2, the
/// connection stays for the session like a mapped drive without a letter). macOS mounts the share: one Finder or ThixSync
/// mounted already is used, otherwise it is mounted below the data folder with mount_smbfs (with a user name and password)
/// or through the Finder (the system asks for the password and keeps it in the Keychain).
/// </summary>
public static partial class NetworkShares
{
    /// <summary>Reads \\server\share\folder, //server/share/folder or smb://[user@]server/share/folder; null when it is none of them.</summary>
    public static ShareAddress? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = ShareRegex().Match(text.Trim());
        if (!match.Success)
        {
            return null;
        }

        var sub = match.Groups["sub"].Value.Trim('/', '\\').Replace('\\', '/');
        return new ShareAddress(match.Groups["server"].Value, match.Groups["share"].Value, Uri.UnescapeDataString(sub));
    }

    /// <summary>
    /// Makes the share readable and returns the local path of the folder (Windows: the UNC path; macOS: the folder of the mount).
    /// Throws a <see cref="BusinessException"/> with the reason when it is not reachable.
    /// </summary>
    public static async Task<string> ConnectAsync(ShareAddress address, string userName, string? password, Guid sourceId, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            return await Task.Run(() => ConnectWindows(address, userName, password), cancellationToken);
        }

        if (OperatingSystem.IsMacOS())
        {
            return await ConnectMacAsync(address, userName, password, sourceId, cancellationToken);
        }

        throw new BusinessException("Netzwerkfreigaben werden auf diesem System nicht unterstützt.");
    }

    /// <summary>The local path of the folder when the share is reachable without asking (macOS: mounted already); null otherwise.</summary>
    public static string? LocalPathIfConnected(ShareAddress address)
    {
        if (OperatingSystem.IsWindows())
        {
            return address.Unc;
        }

        return OperatingSystem.IsMacOS() && FindMacMount(address) is { } mount ? Join(mount, address.SubPath) : null;
    }

    [SupportedOSPlatform("windows")]
    private static string ConnectWindows(ShareAddress address, string userName, string? password)
    {
        if (userName.Length > 0)
        {
            var resource = new NetResource { Type = ResourceTypeDisk, RemoteName = address.ShareUnc };
            var result = WNetAddConnection2(ref resource, password, userName, ConnectTemporary);
            // 1219: connected already with other credentials - that connection is used; 85: the name is taken
            if (result is not (0 or 1219 or 85))
            {
                throw new BusinessException(DescribeError(result, address), "Path");
            }
        }

        if (!Directory.Exists(address.Unc))
        {
            throw new BusinessException($"{address.Unc} ist nicht erreichbar. Stimmen Server, Freigabe und Anmeldung?", "Path");
        }

        return address.Unc;
    }

    private static string DescribeError(int code, ShareAddress address) => code switch
    {
        5 or 86 or 1326 => "Benutzername oder Passwort stimmen nicht (oder der Zugriff auf die Freigabe ist nicht erlaubt).",
        53 or 67 or 1203 => $"{address.ShareUnc} wurde nicht gefunden. Stimmen Server und Freigabe?",
        1311 or 1231 => $"Der Server {address.Server} ist nicht erreichbar.",
        _ => $"Die Verbindung zu {address.ShareUnc} ist fehlgeschlagen (Fehler {code}).",
    };

    private static async Task<string> ConnectMacAsync(ShareAddress address, string userName, string? password, Guid sourceId, CancellationToken cancellationToken)
    {
        if (FindMacMount(address) is { } mounted)
        {
            return Join(mounted, address.SubPath);
        }

        if (userName.Length > 0 && !string.IsNullOrEmpty(password))
        {
            var mountPoint = Path.Combine(AppPaths.MountDirectory, sourceId.ToString("N"));
            Directory.CreateDirectory(mountPoint);
            var url = $"//{Uri.EscapeDataString(userName)}:{Uri.EscapeDataString(password)}@{address.Server}/{Uri.EscapeDataString(address.Share)}";
            var (exitCode, error) = await RunAsync("/sbin/mount_smbfs", ["-N", url, mountPoint], cancellationToken);
            if (exitCode != 0)
            {
                Log.Warning($"mount_smbfs of {address.Server}/{address.Share} failed ({exitCode})");
                throw new BusinessException(error.Contains("Authentication", StringComparison.OrdinalIgnoreCase)
                    ? "Benutzername oder Passwort stimmen nicht."
                    : $"{address.Url} konnte nicht verbunden werden.", "Path");
            }

            return Join(mountPoint, address.SubPath);
        }

        // The Finder mounts it (and asks for the password, kept in the Keychain); wait until it is there.
        var finderUrl = userName.Length > 0 ? $"smb://{Uri.EscapeDataString(userName)}@{address.Server}/{Uri.EscapeDataString(address.Share)}" : $"smb://{address.Server}/{Uri.EscapeDataString(address.Share)}";
        await RunAsync("/usr/bin/open", [finderUrl], cancellationToken);
        for (var attempt = 0; attempt < 120; attempt++)
        {
            await Task.Delay(500, cancellationToken);
            if (FindMacMount(address) is { } mount)
            {
                return Join(mount, address.SubPath);
            }
        }

        throw new BusinessException($"{address.Url} wurde nicht verbunden (im Finder abgebrochen oder nicht erreichbar).", "Path");
    }

    /// <summary>The mount point of //…@server/share in the output of "mount", or null.</summary>
    private static string? FindMacMount(ShareAddress address)
    {
        try
        {
            var startInfo = new ProcessStartInfo("/sbin/mount") { UseShellExecute = false, RedirectStandardOutput = true };
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            foreach (var line in output.Split('\n'))
            {
                // //user@server/share on /Volumes/share (smbfs, ...)
                var match = MountLineRegex().Match(line);
                if (match.Success
                    && string.Equals(match.Groups["server"].Value, address.Server, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(Uri.UnescapeDataString(match.Groups["share"].Value), address.Share, StringComparison.OrdinalIgnoreCase))
                {
                    return match.Groups["mount"].Value;
                }
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warning($"mount could not be read: {exception.Message}");
        }

        return null;
    }

    private static async Task<(int ExitCode, string Error)> RunAsync(string program, string[] arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new BusinessException($"{program} konnte nicht gestartet werden.");
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, error);
    }

    private static string Join(string folder, string sub) => sub.Length == 0 ? folder : Path.Join(folder, sub);

    [GeneratedRegex(@"^(?:smb:)?(?:\\\\|//)(?:[^@/\\]+@)?(?<server>[^/\\]+)[/\\]+(?<share>[^/\\]+)(?<sub>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex ShareRegex();

    [GeneratedRegex(@"^//(?:[^@]*@)?(?<server>[^/]+)/(?<share>\S+) on (?<mount>.+?) \((?:smbfs|cifs)", RegexOptions.IgnoreCase)]
    private static partial Regex MountLineRegex();

    private const int ResourceTypeDisk = 1;
    private const int ConnectTemporary = 4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource
    {
        public int Scope;
        public int Type;
        public int DisplayType;
        public int Usage;
        public string? LocalName;
        public string? RemoteName;
        public string? Comment;
        public string? Provider;
    }

#pragma warning disable SYSLIB1054 // a struct with strings: the classic marshaller is simpler here
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(ref NetResource resource, string? password, string? userName, int flags);
#pragma warning restore SYSLIB1054
}
