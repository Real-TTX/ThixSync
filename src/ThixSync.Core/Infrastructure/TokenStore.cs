using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace ThixSync.Core.Infrastructure;

/// <summary>
/// Keeps secrets (the device token of the cloud, the passwords of SMB shares) where only this user can read them: Windows DPAPI (the protected value lives in
/// settings.json), macOS Keychain (generic password "info.schmoldt.thixsync.cloud"). No secret ever goes into a log.
/// </summary>
internal static class TokenStore
{
    private const string KeychainService = "info.schmoldt.thixsync.cloud";
    private static readonly byte[] Entropy = "ThixSync.Cloud.DeviceToken"u8.ToArray();

    private static readonly Dictionary<string, string> TestTokens = [];
    private static bool _useTestStore;

    /// <summary>Stores the token; returns the value to keep in the settings (Windows) or an empty string (Keychain).</summary>
    public static string Save(string account, string token)
    {
        if (_useTestStore)
        {
            TestTokens[account] = token;
            return "test";
        }

        if (OperatingSystem.IsWindows())
        {
            return Convert.ToBase64String(Protect(token));
        }

        if (OperatingSystem.IsMacOS())
        {
            RunSecurity("add-generic-password", "-U", "-a", account, "-s", KeychainService, "-w", token);
            return "";
        }

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(token));
    }

    public static string? Read(string account, string protectedValue)
    {
        if (_useTestStore)
        {
            return TestTokens.GetValueOrDefault(account);
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                return protectedValue.Length == 0 ? null : Unprotect(Convert.FromBase64String(protectedValue));
            }

            if (OperatingSystem.IsMacOS())
            {
                var token = RunSecurity("find-generic-password", "-a", account, "-s", KeychainService, "-w").Trim();
                return token.Length == 0 ? null : token;
            }

            return protectedValue.Length == 0 ? null : Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue));
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or InvalidOperationException or IOException)
        {
            Log.Warning($"The device token could not be read: {exception.GetType().Name}");
            return null;
        }
    }

    public static void Delete(string account)
    {
        if (_useTestStore)
        {
            TestTokens.Remove(account);
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            try
            {
                RunSecurity("delete-generic-password", "-a", account, "-s", KeychainService);
            }
            catch (InvalidOperationException)
            {
                // not there
            }
        }
    }

    internal static void UseMemoryForTests(bool enabled)
    {
        _useTestStore = enabled;
        TestTokens.Clear();
    }

    [SupportedOSPlatform("windows")]
    private static byte[] Protect(string token) =>
        ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static string Unprotect(byte[] data) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser));

    private static string RunSecurity(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("/usr/bin/security")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("security could not be started");
        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"security {arguments[0]} failed ({process.ExitCode})");
        }

        return output;
    }
}
