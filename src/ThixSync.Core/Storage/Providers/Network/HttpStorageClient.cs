using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using ThixSync.Core.Infrastructure;

namespace ThixSync.Core.Storage.Providers.Network;

/// <summary>
/// The HTTP side of the server providers (WebDAV, Nextcloud, Seafile): one HttpClient per opened storage over one
/// SocketsHttpHandler (keep-alive, as many connections per server as the storage works in parallel, HTTP/2 over TLS when the
/// server offers it), and the mapping of HTTP answers to <see cref="StorageException"/>.
/// </summary>
internal static class HttpStorageClient
{
    /// <summary>Tests: a handler of their own instead of the network (a fake WebDAV or Seafile server).</summary>
    internal static Func<HttpMessageHandler>? HandlerForTests { get; set; }

    /// <summary>How long a metadata request (listing, folder, delete, move) may take; transfers have no limit.</summary>
    public static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(120);

    /// <param name="trust">The certificate check (a self-signed certificate of a NAS is trusted on first use); null: the usual check only.</param>
    public static HttpClient Create(int parallelism, TlsTrust? trust)
    {
        HttpMessageHandler handler = HandlerForTests?.Invoke() ?? new SocketsHttpHandler
        {
            MaxConnectionsPerServer = Math.Max(1, parallelism),
            EnableMultipleHttp2Connections = true,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(60),
            ConnectTimeout = TimeSpan.FromSeconds(30),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            AllowAutoRedirect = false,
            UseCookies = true,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, errors) => trust?.Validate(certificate, errors) ?? errors == SslPolicyErrors.None,
            },
        };
        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        client.DefaultRequestHeaders.UserAgent.TryParseAdd($"ThixSync/{AppInfo.Version}");
        return client;
    }

    /// <summary>"Basic …" for a user and password (UTF-8, as every current server expects).</summary>
    public static AuthenticationHeaderValue Basic(string user, string password) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));

    /// <summary>A failed HTTP answer as a <see cref="StorageException"/> (German, with the relative path only).</summary>
    public static StorageException Map(HttpResponseMessage response, string path, string? what = null)
    {
        var where = path.Length == 0 ? "" : $" ({path})";
        var code = (int)response.StatusCode;
        return code switch
        {
            401 => new StorageException(StorageErrorKind.Authentication, "Die Anmeldung wurde abgelehnt – stimmen Benutzername und Passwort?"),
            403 => new StorageException(StorageErrorKind.AccessDenied, $"Kein Zugriff{where}."),
            404 or 410 => new StorageException(StorageErrorKind.NotFound, $"Nicht gefunden{where}."),
            409 => new StorageException(StorageErrorKind.NotFound, $"Der übergeordnete Ordner fehlt{where}."),
            413 => new StorageException(StorageErrorKind.QuotaExceeded, $"Die Datei ist für den Server zu groß{where}."),
            414 or 400 when what == "name" => new StorageException(StorageErrorKind.InvalidName, $"Der Server nimmt diesen Namen nicht an{where}."),
            423 => new StorageException(StorageErrorKind.Locked, $"Die Datei ist auf dem Server gesperrt{where}."),
            429 => new StorageException(StorageErrorKind.RateLimited, "Der Server bittet um eine Pause (zu viele Anfragen).") { RetryAfter = RetryAfterOf(response) },
            507 => new StorageException(StorageErrorKind.QuotaExceeded, "Auf dem Server ist kein Platz mehr (Speicher voll oder Kontingent erreicht)."),
            503 when RetryAfterOf(response) is { } wait => new StorageException(StorageErrorKind.RateLimited, "Der Server ist gerade ausgelastet.") { RetryAfter = wait },
            >= 500 => new StorageException(StorageErrorKind.Transient, $"Fehler auf dem Server (HTTP {code}){where}."),
            >= 300 and < 400 => new StorageException(StorageErrorKind.Unknown, $"Der Server leitet auf eine andere Adresse um (HTTP {code}) – stimmt die Adresse der Verbindung?"),
            _ => new StorageException(StorageErrorKind.Unknown, $"Der Server hat die Anfrage abgelehnt (HTTP {code}){where}."),
        };
    }

    /// <summary>
    /// An exception of the transport (DNS, refused, TLS, reset, timeout) as a <see cref="StorageException"/>. Cancellation by the
    /// caller stays an <see cref="OperationCanceledException"/>.
    /// </summary>
    public static Exception Map(Exception exception, string host, CancellationToken cancellationToken, TlsTrust? trust = null)
    {
        if (exception is StorageException)
        {
            return exception;
        }

        if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            return exception;
        }

        if (trust is { Refused: true } && exception is HttpRequestException { InnerException: AuthenticationException })
        {
            return TlsTrust.Mismatch(host, exception);
        }

        if (exception is OperationCanceledException or TimeoutException)
        {
            return new StorageException(StorageErrorKind.Transient, $"{host} antwortet nicht rechtzeitig.", exception);
        }

        if (exception is HttpRequestException { InnerException: AuthenticationException })
        {
            return new StorageException(StorageErrorKind.Authentication, $"Die sichere Verbindung zu {host} ist fehlgeschlagen (Zertifikat ungültig oder nicht vertrauenswürdig).", exception);
        }

        if (exception is HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError } or SocketException)
        {
            return new StorageException(StorageErrorKind.Unreachable, $"{host} ist nicht erreichbar.", exception);
        }

        return exception is HttpRequestException or IOException
            ? new StorageException(StorageErrorKind.Transient, $"Die Verbindung zu {host} wurde unterbrochen.", exception)
            : exception;
    }

    private static TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        var retry = response.Headers.RetryAfter;
        if (retry?.Delta is { } delta)
        {
            return delta;
        }

        if (retry?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.FromSeconds(1);
        }

        return null;
    }

    /// <summary>Unix seconds as text (X-OC-Mtime).</summary>
    public static string UnixSeconds(DateTimeOffset time) => time.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
}
