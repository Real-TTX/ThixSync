using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using ThixSync.Core.Infrastructure;

namespace ThixSync.Core.Remote;

/// <summary>
/// The identity of this computer for ThixSync Remote: a self-signed certificate (ECDSA P-256, CN = the computer name, 20 years), kept as
/// a PFX in <see cref="AppPaths.RemoteDirectory"/>; its password is protected by the <see cref="TokenStore"/> (DPAPI / Keychain). Made on
/// first use; made again (a new fingerprint) only when the file or its password is gone.
/// </summary>
internal static class RemoteIdentity
{
    private const string PasswordAccount = "remote-identity";
    private const string CertificateFile = "identity.pfx";
    private const string PasswordFile = "identity.key";

    private static readonly Lock Gate = new();
    private static Loaded? _loaded;

    public static X509Certificate2 Certificate => Load().Certificate;

    /// <summary>The SHA-256 fingerprint of the certificate ("AB:CD:…").</summary>
    public static string Fingerprint => Load().Fingerprint;

    /// <summary>The fingerprint when the identity was loaded already (null: not yet; nothing is made).</summary>
    public static string? LoadedFingerprint
    {
        get
        {
            lock (Gate)
            {
                return _loaded is { } loaded && loaded.Directory == AppPaths.RemoteDirectory ? loaded.Fingerprint : null;
            }
        }
    }

    private static Loaded Load()
    {
        lock (Gate)
        {
            var directory = AppPaths.RemoteDirectory;
            if (_loaded is { } loaded && loaded.Directory == directory)
            {
                return loaded;
            }

            var certificate = TryRead(directory) ?? Create(directory);
            _loaded = new Loaded(directory, certificate, RemoteCrypto.FingerprintOf(certificate));
            return _loaded;
        }
    }

    private static X509Certificate2? TryRead(string directory)
    {
        var certificateFile = Path.Combine(directory, CertificateFile);
        var passwordFile = Path.Combine(directory, PasswordFile);
        if (!File.Exists(certificateFile) || !File.Exists(passwordFile))
        {
            return null;
        }

        try
        {
            var password = TokenStore.Read(PasswordAccount, File.ReadAllText(passwordFile).Trim());
            if (password is null)
            {
                Log.Warning("Remote: the password of the certificate could not be read; making a new identity");
                return null;
            }

            var certificate = X509CertificateLoader.LoadPkcs12FromFile(certificateFile, password, KeyStorageFlags);
            if (certificate.HasPrivateKey)
            {
                return certificate;
            }

            certificate.Dispose();
            return null;
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Remote: the certificate could not be read ({exception.GetType().Name}); making a new identity");
            return null;
        }
    }

    private static X509Certificate2 Create(string directory)
    {
        Directory.CreateDirectory(directory);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var name = new X500DistinguishedNameBuilder();
        name.AddCommonName(AppInfo.MachineName);
        var request = new CertificateRequest(name.Build(), key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var now = DateTimeOffset.UtcNow;
        using var created = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(20));

        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        WriteAtomically(Path.Combine(directory, CertificateFile), created.Export(X509ContentType.Pkcs12, password));
        WriteAtomically(Path.Combine(directory, PasswordFile), Encoding.UTF8.GetBytes(TokenStore.Save(PasswordAccount, password)));
        Log.Info("Remote: made a new certificate for this computer");
        return X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(directory, CertificateFile), password, KeyStorageFlags);
    }

    /// <summary>Windows (SChannel) needs a key it can find in a key store for a server certificate; an ephemeral key fails there.</summary>
    private static X509KeyStorageFlags KeyStorageFlags => OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.DefaultKeySet;

    internal static void WriteAtomically(string path, byte[] content)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, content);
        File.Move(temp, path, overwrite: true);
    }

    private sealed record Loaded(string Directory, X509Certificate2 Certificate, string Fingerprint);
}

/// <summary>
/// The keys of the paired computers (32 random bytes each). The key itself lives in the <see cref="TokenStore"/> under
/// "remote-peer:&lt;keyId&gt;"; the protected value (Windows) is kept in <c>peers.json</c> of <see cref="AppPaths.RemoteDirectory"/>.
/// The peers themselves (name, paired at, last seen) are in the settings (<see cref="Models.RemoteSettings.Peers"/>).
/// </summary>
internal static class RemotePeerKeys
{
    private const string KeysFile = "peers.json";

    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, byte[]> Cache = new(StringComparer.Ordinal);
    private static string _cacheDirectory = "";

    public static void Add(string keyId, byte[] key)
    {
        lock (Gate)
        {
            var stored = ReadFile();
            stored[keyId] = TokenStore.Save(Account(keyId), Convert.ToBase64String(key));
            WriteFile(stored);
            CacheFor()[keyId] = key;
        }
    }

    /// <summary>The key of a peer, or null when it is not known (any more).</summary>
    public static byte[]? Find(string keyId)
    {
        lock (Gate)
        {
            var cache = CacheFor();
            if (cache.TryGetValue(keyId, out var cached))
            {
                return cached;
            }

            if (!ReadFile().TryGetValue(keyId, out var protectedValue) || TokenStore.Read(Account(keyId), protectedValue) is not { } text)
            {
                return null;
            }

            try
            {
                var key = Convert.FromBase64String(text);
                cache[keyId] = key;
                return key;
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }

    public static void Remove(string keyId)
    {
        lock (Gate)
        {
            CacheFor().Remove(keyId);
            TokenStore.Delete(Account(keyId));
            var stored = ReadFile();
            if (stored.Remove(keyId))
            {
                WriteFile(stored);
            }
        }
    }

    private static string Account(string keyId) => $"remote-peer:{keyId}";

    private static Dictionary<string, byte[]> CacheFor()
    {
        if (_cacheDirectory != AppPaths.RemoteDirectory)
        {
            Cache.Clear();
            _cacheDirectory = AppPaths.RemoteDirectory;
        }

        return Cache;
    }

    private static Dictionary<string, string> ReadFile()
    {
        var path = Path.Combine(AppPaths.RemoteDirectory, KeysFile);
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize(File.ReadAllBytes(path), RemoteJson.Default.DictionaryStringString) ?? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Remote: {KeysFile} could not be read ({exception.GetType().Name})");
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static void WriteFile(Dictionary<string, string> stored)
    {
        Directory.CreateDirectory(AppPaths.RemoteDirectory);
        RemoteIdentity.WriteAtomically(Path.Combine(AppPaths.RemoteDirectory, KeysFile), JsonSerializer.SerializeToUtf8Bytes(stored, RemoteJson.Default.DictionaryStringString));
    }
}

/// <summary>The cryptography of the Remote sign-in (see <see cref="RemoteProtocol"/>).</summary>
internal static class RemoteCrypto
{
    public const int NonceBytes = 32;
    public const int KeyBytes = 32;

    /// <summary>"AB:CD:…" – the SHA-256 of the DER bytes of a certificate.</summary>
    public static string FingerprintOf(X509Certificate certificate)
    {
        var hex = Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
        return string.Join(':', Enumerable.Range(0, hex.Length / 2).Select(index => hex.Substring(index * 2, 2)));
    }

    /// <summary>The 8 digits of a pairing code ("1234-5678", "1234 5678", "12345678"), or null when the text is no pairing code.</summary>
    public static string? PairingDigits(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var digits = new string([.. text.Where(c => c is not ('-' or ' '))]);
        return digits.Length == 8 && digits.All(char.IsAsciiDigit) ? digits : null;
    }

    /// <summary>The key a pairing code stands for in one handshake (PBKDF2 over the code, salted with both nonces).</summary>
    public static byte[] PairingKey(string digits, byte[] clientNonce, byte[] serverNonce) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(digits), (byte[])[.. clientNonce, .. serverNonce], RemoteProtocol.PairingIterations,
            HashAlgorithmName.SHA256, KeyBytes);

    /// <summary>HMAC-SHA256 over T(parts): every part with a 4-byte big-endian length in front.</summary>
    public static byte[] Proof(byte[] key, params byte[][] parts)
    {
        using var transcript = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        foreach (var part in parts)
        {
            BinaryPrimitives.WriteInt32BigEndian(length, part.Length);
            transcript.Write(length);
            transcript.Write(part);
        }

        return HMACSHA256.HashData(key, transcript.ToArray());
    }

    public static bool Matches(byte[] expected, string? proof)
    {
        if (proof is null)
        {
            return false;
        }

        try
        {
            return CryptographicOperations.FixedTimeEquals(expected, Convert.FromBase64String(proof));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);
}
