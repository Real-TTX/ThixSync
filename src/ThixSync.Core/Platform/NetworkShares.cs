using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using ThixSync.Core.Infrastructure;
using ThixSync.Core.Storage;

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
/// Reaching SMB and NFS shares. Windows reads \\server\share directly; with a user name it connects first (WNetAddConnection2, the
/// connection stays for the session like a mapped drive without a letter); NFS exports are \\server\export through the "Client für
/// NFS". macOS mounts: a share mounted already (Finder, ThixSync) is used, otherwise it is mounted below the data folder,
/// <c>mounts/&lt;connection&gt;</c>. SMB is mounted with the NetFS framework (NetFSMountURLSync, no dialogs): the password stays
/// inside this process – it never appears on a command line other users could see with <c>ps</c>. NFS has no password and is
/// mounted with <c>mount_nfs -o resvport</c> (NetFS as fallback). Mounts are left in place when a job ends (other jobs may use them).
/// Errors are <see cref="StorageException"/> with German messages.
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

    /// <summary>Where ThixSync mounts the share of a connection on macOS (when the share is not mounted elsewhere already).</summary>
    public static string MountPointFor(Guid connectionId) => Path.Combine(AppPaths.MountDirectory, connectionId.ToString("N"));

    /// <summary>
    /// Makes the SMB share readable and returns the local path of the folder (Windows: the UNC path; macOS: the folder of the mount).
    /// The folder itself may not exist yet (the caller creates it); the share must be reachable.
    /// </summary>
    /// <param name="userName">"DOMÄNE\benutzer" or "benutzer"; empty: the sign-in of this computer (Windows) or the Keychain (macOS).</param>
    public static async Task<string> ConnectAsync(ShareAddress address, string userName, string? password, Guid sourceId, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            return await ConnectWindowsAsync(address, userName, password, cancellationToken);
        }

        if (OperatingSystem.IsMacOS())
        {
            return await ConnectMacAsync(address, userName, password, sourceId, cancellationToken);
        }

        throw new StorageException(StorageErrorKind.Unreachable, "Netzwerkfreigaben werden auf diesem System nicht unterstützt.");
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

    /// <summary>\\server\volume1\daten\sub of the export "/volume1/daten" (Windows, Client für NFS).</summary>
    public static string NfsUnc(string server, string export, string subPath)
    {
        var parts = export.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Concat(subPath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries));
        return @"\\" + server + @"\" + string.Join('\\', parts);
    }

    /// <summary>The local path of an NFS export mounted already (macOS), or null.</summary>
    public static string? NfsLocalPathIfConnected(string server, string export, string subPath) =>
        OperatingSystem.IsMacOS() && FindMacNfsMount(server, export) is { } mount ? Join(mount, subPath) : null;

    /// <summary>
    /// Makes an NFS export readable and returns the local path of <paramref name="subPath"/> in it (Windows: \\server\export\sub;
    /// macOS: the folder of the mount). <paramref name="version"/>: "auto", "3" or "4".
    /// </summary>
    public static async Task<string> ConnectNfsAsync(string server, string export, string subPath, string version, Guid connectionId, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            return await Task.Run(() => ConnectNfsWindows(server, export, subPath), cancellationToken);
        }

        if (OperatingSystem.IsMacOS())
        {
            return await ConnectNfsMacAsync(server, export, subPath, version, connectionId, cancellationToken);
        }

        throw new StorageException(StorageErrorKind.Unreachable, "NFS wird auf diesem System nicht unterstützt.");
    }

    /// <summary>True when the "Client für NFS" of Windows is installed (the NfsClnt service exists).</summary>
    [SupportedOSPlatform("windows")]
    public static bool IsWindowsNfsClientInstalled()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\NfsClnt");
        return key is not null;
    }

    [SupportedOSPlatform("windows")]
    private static Task<string> ConnectWindowsAsync(ShareAddress address, string userName, string? password, CancellationToken cancellationToken) =>
        Task.Run(() => ConnectWindows(address, userName, password), cancellationToken);

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
                throw DescribeError(result, address);
            }
        }

        if (!Directory.Exists(address.ShareUnc + @"\"))
        {
            throw new StorageException(StorageErrorKind.Unreachable, $"{address.ShareUnc} ist nicht erreichbar. Stimmen Server, Freigabe und Anmeldung?");
        }

        return address.Unc;
    }

    private static StorageException DescribeError(int code, ShareAddress address) => code switch
    {
        5 or 86 or 1326 or 1327 or 1330 or 1331 or 1909 => new StorageException(StorageErrorKind.Authentication,
            "Benutzername oder Passwort stimmen nicht (oder der Zugriff auf die Freigabe ist nicht erlaubt)."),
        53 or 67 or 1203 => new StorageException(StorageErrorKind.Unreachable, $"{address.ShareUnc} wurde nicht gefunden. Stimmen Server und Freigabe?"),
        1311 or 1231 or 1222 => new StorageException(StorageErrorKind.Unreachable, $"Der Server {address.Server} ist nicht erreichbar."),
        _ => new StorageException(StorageErrorKind.Unreachable, $"Die Verbindung zu {address.ShareUnc} ist fehlgeschlagen (Fehler {code})."),
    };

    [SupportedOSPlatform("macos")]
    private static async Task<string> ConnectMacAsync(ShareAddress address, string userName, string? password, Guid sourceId, CancellationToken cancellationToken)
    {
        if (FindMacMount(address) is { } mounted)
        {
            return Join(mounted, address.SubPath);
        }

        var mountPoint = MountPointFor(sourceId);
        Directory.CreateDirectory(mountPoint);
        // DOMÄNE;benutzer@server – the form of smb URLs; the password is handed to NetFS directly, never put into the URL
        var (domain, user) = SplitDomain(userName);
        var userPart = user.Length == 0 ? "" : (domain.Length > 0 ? Uri.EscapeDataString(domain) + ";" : "") + Uri.EscapeDataString(user) + "@";
        var url = $"smb://{userPart}{address.Server}/{Uri.EscapeDataString(address.Share)}";
        var result = await Task.Run(() => NetFsMount(url, mountPoint, user.Length == 0 ? null : user, user.Length == 0 ? null : password), cancellationToken);
        if (result == 17 && FindMacMount(address) is { } other)
        {
            return Join(other, address.SubPath); // mounted in the meantime (Finder)
        }

        if (result != 0)
        {
            Log.Warning($"NetFS mount of an SMB share failed ({result})");
            throw DescribeNetFsError(result, address.Url);
        }

        return Join(mountPoint, address.SubPath);
    }

    [SupportedOSPlatform("windows")]
    private static string ConnectNfsWindows(string server, string export, string subPath)
    {
        if (!IsWindowsNfsClientInstalled())
        {
            throw new StorageException(StorageErrorKind.Unreachable,
                "Der „Client für NFS“ ist auf diesem Computer nicht installiert. Windows-Features aktivieren › „Dienste für NFS“ › „Client für NFS“, danach neu anmelden.");
        }

        var share = NfsUnc(server, export, "");
        if (!Directory.Exists(share + @"\"))
        {
            throw new StorageException(StorageErrorKind.Unreachable, $"{share} ist nicht erreichbar. Stimmen Server und Export (und darf dieser Computer ihn einbinden)?");
        }

        return NfsUnc(server, export, subPath);
    }

    [SupportedOSPlatform("macos")]
    private static async Task<string> ConnectNfsMacAsync(string server, string export, string subPath, string version, Guid connectionId, CancellationToken cancellationToken)
    {
        if (FindMacNfsMount(server, export) is { } mounted)
        {
            return Join(mounted, subPath);
        }

        var mountPoint = MountPointFor(connectionId);
        Directory.CreateDirectory(mountPoint);
        var exportPath = "/" + export.Trim('/');
        var options = "resvport" + (version is "3" or "4" ? $",vers={version}" : "");
        var (exitCode, error) = await RunAsync("/sbin/mount_nfs", ["-o", options, $"{server}:{exportPath}", mountPoint], cancellationToken);
        if (exitCode == 0)
        {
            return Join(mountPoint, subPath);
        }

        // mounting as a normal user may be refused: NetFS (as the Finder does it) may still mount it
        Log.Warning($"mount_nfs failed ({exitCode}): {error.Trim()}");
        var url = $"nfs://{server}{string.Join('/', exportPath.Split('/').Select(Uri.EscapeDataString))}";
        var result = await Task.Run(() => NetFsMount(url, mountPoint, null, null), cancellationToken);
        if (result == 0)
        {
            return Join(mountPoint, subPath);
        }

        if (result == 17 && FindMacNfsMount(server, export) is { } other)
        {
            return Join(other, subPath);
        }

        throw error.Contains("denied", StringComparison.OrdinalIgnoreCase)
            ? new StorageException(StorageErrorKind.AccessDenied, $"Der Server {server} erlaubt diesem Computer den Export {exportPath} nicht.")
            : new StorageException(StorageErrorKind.Unreachable, $"Der Export {server}:{exportPath} konnte nicht eingebunden werden.");
    }

    private static StorageException DescribeNetFsError(int code, string url) => code switch
    {
        1 or 13 or 80 or -6003 => new StorageException(StorageErrorKind.Authentication, "Benutzername oder Passwort stimmen nicht (oder der Zugriff auf die Freigabe ist nicht erlaubt)."),
        2 => new StorageException(StorageErrorKind.Unreachable, $"{url} wurde nicht gefunden. Stimmen Server und Freigabe?"),
        60 or 61 or 64 or 65 => new StorageException(StorageErrorKind.Unreachable, $"Der Server von {url} ist nicht erreichbar."),
        _ => new StorageException(StorageErrorKind.Unreachable, $"{url} konnte nicht verbunden werden (Fehler {code})."),
    };

    /// <summary>"DOMÄNE\benutzer" → ("DOMÄNE", "benutzer").</summary>
    private static (string Domain, string User) SplitDomain(string userName)
    {
        var index = userName.IndexOf('\\');
        return index < 0 ? ("", userName) : (userName[..index], userName[(index + 1)..]);
    }

    /// <summary>The mount point of //…@server/share in the output of "mount", or null.</summary>
    private static string? FindMacMount(ShareAddress address)
    {
        foreach (var line in ReadMounts())
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

        return null;
    }

    /// <summary>The mount point of server:/export in the output of "mount", or null.</summary>
    private static string? FindMacNfsMount(string server, string export)
    {
        var wanted = "/" + export.Trim('/');
        foreach (var line in ReadMounts())
        {
            // nas.local:/volume1/daten on /Users/anna/Library/Application Support/ThixSync/mounts/… (nfs, ...)
            var match = NfsMountLineRegex().Match(line);
            if (match.Success
                && string.Equals(match.Groups["server"].Value, server, StringComparison.OrdinalIgnoreCase)
                && string.Equals("/" + match.Groups["export"].Value.Trim('/'), wanted, StringComparison.Ordinal))
            {
                return match.Groups["mount"].Value;
            }
        }

        return null;
    }

    private static string[] ReadMounts()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return [];
        }

        try
        {
            var startInfo = new ProcessStartInfo("/sbin/mount") { UseShellExecute = false, RedirectStandardOutput = true };
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return [];
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return output.Split('\n');
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warning($"mount could not be read: {exception.Message}");
            return [];
        }
    }

    private static async Task<(int ExitCode, string Error)> RunAsync(string program, string[] arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new StorageException(StorageErrorKind.Unknown, $"{program} konnte nicht gestartet werden.");
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, error);
    }

    private static string Join(string folder, string sub) => sub.Length == 0 ? folder : Path.Join(folder, sub);

    /// <summary>
    /// Mounts a URL (smb://, nfs://) exactly at <paramref name="mountPoint"/> with NetFS, without any dialog. The password goes to
    /// the framework as a CFString in memory. Returns 0 or the error (errno, NetAuth codes).
    /// </summary>
    [SupportedOSPlatform("macos")]
    private static int NetFsMount(string url, string mountPoint, string? user, string? password)
    {
        var coreFoundation = NativeLibrary.Load(CoreFoundationPath);
        var keyCallBacks = NativeLibrary.GetExport(coreFoundation, "kCFTypeDictionaryKeyCallBacks");
        var valueCallBacks = NativeLibrary.GetExport(coreFoundation, "kCFTypeDictionaryValueCallBacks");
        var booleanTrue = Marshal.ReadIntPtr(NativeLibrary.GetExport(coreFoundation, "kCFBooleanTrue"));
        var owned = new List<IntPtr>();
        IntPtr Track(IntPtr value)
        {
            if (value != IntPtr.Zero)
            {
                owned.Add(value);
            }

            return value;
        }

        IntPtr Text(string? value) => value is null ? IntPtr.Zero : Track(CFStringCreateWithCharacters(IntPtr.Zero, value, value.Length));

        try
        {
            var urlRef = Track(CFURLCreateWithString(IntPtr.Zero, Text(url), IntPtr.Zero));
            var mountRef = Track(CFURLCreateWithFileSystemPath(IntPtr.Zero, Text(mountPoint), 0, true));
            var openOptions = Track(CFDictionaryCreateMutable(IntPtr.Zero, 0, keyCallBacks, valueCallBacks));
            CFDictionarySetValue(openOptions, Text("UIOption"), Text("NoUI"));
            var mountOptions = Track(CFDictionaryCreateMutable(IntPtr.Zero, 0, keyCallBacks, valueCallBacks));
            CFDictionarySetValue(mountOptions, Text("MountAtMountDir"), booleanTrue);
            if (urlRef == IntPtr.Zero)
            {
                return 22; // EINVAL
            }

            var result = NetFSMountURLSync(urlRef, mountRef, Text(user), Text(password), openOptions, mountOptions, out var mountPoints);
            Track(mountPoints);
            return result;
        }
        finally
        {
            foreach (var value in owned)
            {
                CFRelease(value);
            }
        }
    }

    [GeneratedRegex(@"^(?:smb:)?(?:\\\\|//)(?:[^@/\\]+@)?(?<server>[^/\\]+)[/\\]+(?<share>[^/\\]+)(?<sub>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex ShareRegex();

    [GeneratedRegex(@"^//(?:[^@]*@)?(?<server>[^/]+)/(?<share>\S+) on (?<mount>.+?) \((?:smbfs|cifs)", RegexOptions.IgnoreCase)]
    private static partial Regex MountLineRegex();

    [GeneratedRegex(@"^(?<server>[^:\s/]+):(?<export>/\S*) on (?<mount>.+?) \(nfs", RegexOptions.IgnoreCase)]
    private static partial Regex NfsMountLineRegex();

    private const int ResourceTypeDisk = 1;
    private const int ConnectTemporary = 4;
    private const string CoreFoundationPath = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string NetFsPath = "/System/Library/Frameworks/NetFS.framework/NetFS";

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

    [LibraryImport(CoreFoundationPath, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CFStringCreateWithCharacters(IntPtr allocator, string characters, nint length);

    [LibraryImport(CoreFoundationPath)]
    private static partial IntPtr CFURLCreateWithString(IntPtr allocator, IntPtr urlString, IntPtr baseUrl);

    [LibraryImport(CoreFoundationPath)]
    private static partial IntPtr CFURLCreateWithFileSystemPath(IntPtr allocator, IntPtr filePath, nint pathStyle, [MarshalAs(UnmanagedType.U1)] bool isDirectory);

    [LibraryImport(CoreFoundationPath)]
    private static partial IntPtr CFDictionaryCreateMutable(IntPtr allocator, nint capacity, IntPtr keyCallBacks, IntPtr valueCallBacks);

    [LibraryImport(CoreFoundationPath)]
    private static partial void CFDictionarySetValue(IntPtr dictionary, IntPtr key, IntPtr value);

    [LibraryImport(CoreFoundationPath)]
    private static partial void CFRelease(IntPtr value);

    [LibraryImport(NetFsPath)]
    private static partial int NetFSMountURLSync(IntPtr url, IntPtr mountPath, IntPtr user, IntPtr password, IntPtr openOptions, IntPtr mountOptions, out IntPtr mountPoints);
}
