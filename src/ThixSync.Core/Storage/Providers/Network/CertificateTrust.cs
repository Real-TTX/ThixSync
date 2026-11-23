using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ThixSync.Core.Storage.Providers.Network;

/// <summary>What the TLS certificate of an FTPS server means.</summary>
internal enum CertificateDecision
{
    /// <summary>Valid for this server (a public certificate) or the one kept with the connection.</summary>
    Accept = 0,

    /// <summary>Not publicly valid (self-signed) and none kept yet: trust on first use, keep its fingerprint.</summary>
    AcceptAndRemember = 1,

    /// <summary>Not publicly valid and not the kept one: maybe another server – never connect.</summary>
    Reject = 2,
}

/// <summary>
/// The trust in the certificate of an FTPS server: a publicly valid certificate passes as always (it may be renewed any time); a
/// self-signed one is trusted on the first connection and its SHA-256 kept (<see cref="ConnectionFields.CertificateHash"/>); a
/// different self-signed one later is refused.
/// </summary>
internal static class CertificateTrust
{
    public static CertificateDecision Decide(SslPolicyErrors errors, string seenHash, string trustedHash)
    {
        if (errors == SslPolicyErrors.None)
        {
            return CertificateDecision.Accept;
        }

        if (string.IsNullOrWhiteSpace(trustedHash))
        {
            return CertificateDecision.AcceptAndRemember;
        }

        return string.Equals(Bare(seenHash), Bare(trustedHash), StringComparison.OrdinalIgnoreCase) ? CertificateDecision.Accept : CertificateDecision.Reject;
    }

    /// <summary>"SHA256:" + the hex SHA-256 of the certificate (DER).</summary>
    public static string HashOf(X509Certificate certificate) => "SHA256:" + Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));

    private static string Bare(string hash)
    {
        var text = hash.Trim();
        if (text.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase))
        {
            text = text[7..];
        }

        return text.Replace(":", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
    }
}

/// <summary>
/// The trust of one opened storage in the TLS certificate of its server (HTTPS, FTPS), shared by all its connections: decides with
/// <see cref="CertificateTrust.Decide"/>, keeps a certificate trusted on first use until <see cref="SaveLearnedAsync"/> stores it
/// with the connection, and remembers a refusal for the message.
/// </summary>
internal sealed class TlsTrust(string trustedHash, Func<string, string, CancellationToken, Task> saveSetting)
{
    private readonly Lock _gate = new();
    private string _trusted = trustedHash;
    private string? _learned;
    private int _refused;

    /// <summary>A certificate was refused because it is not the kept one.</summary>
    public bool Refused => Volatile.Read(ref _refused) == 1;

    /// <summary>The certificate check of a connection (any thread).</summary>
    public bool Validate(X509Certificate? certificate, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None)
        {
            return true;
        }

        if (certificate is null)
        {
            return false;
        }

        var seen = CertificateTrust.HashOf(certificate);
        lock (_gate)
        {
            switch (CertificateTrust.Decide(errors, seen, _trusted))
            {
                case CertificateDecision.Accept:
                    return true;
                case CertificateDecision.AcceptAndRemember:
                    _trusted = seen;
                    _learned = seen;
                    return true;
                default:
                    Interlocked.Exchange(ref _refused, 1);
                    return false;
            }
        }
    }

    /// <summary>Stores a certificate trusted on first use with the connection (once).</summary>
    public async Task SaveLearnedAsync(CancellationToken cancellationToken)
    {
        string? learned;
        lock (_gate)
        {
            learned = _learned;
            _learned = null;
        }

        if (learned is not null)
        {
            await saveSetting(ConnectionFields.CertificateHash, learned, cancellationToken);
        }
    }

    /// <summary>The message when the server shows another certificate than the kept one.</summary>
    public static StorageException Mismatch(string host, Exception? inner = null) => new(StorageErrorKind.Authentication,
        $"Der Server {host} zeigt ein anderes Zertifikat als bisher. Das kann ein fremder Server sein – ThixSync verbindet deshalb nicht. "
        + "Wurde das Zertifikat absichtlich getauscht, das gemerkte Zertifikat in der Verbindung zurücksetzen.", inner);
}
