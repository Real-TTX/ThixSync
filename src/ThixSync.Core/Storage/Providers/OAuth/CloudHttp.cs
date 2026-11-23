using System.Net;
using System.Net.Http.Headers;
using ThixSync.Core.Infrastructure;

namespace ThixSync.Core.Storage.Providers.OAuth;

/// <summary>
/// The one HttpClient of the cloud providers (one connection pool for every storage and worker; no timeout of its own – uploads of big
/// files take long, cancellation ends them). Redirects are followed by <see cref="CloudApi"/> itself, so that the Authorization header
/// never goes to a download server (OneDrive's and Box's downloads redirect to a CDN).
/// </summary>
internal static class CloudHttp
{
    private static readonly Lazy<HttpClient> Shared = new(Create);
    private static HttpClient? _forTests;

    public static HttpClient Client => _forTests ?? Shared.Value;

    /// <summary>Tests: every request goes to this handler (null: the real network again).</summary>
    internal static HttpMessageHandler? HandlerForTests
    {
        set => _forTests = value is null ? null : new HttpClient(value, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>How long the server asked to wait (Retry-After as seconds or a date); null when it did not say.</summary>
    public static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta;
        }

        if (header?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }

    private static HttpClient Create()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 32,
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ThixSync", Token(AppInfo.Version)));
        return client;
    }

    private static string Token(string value)
    {
        var clean = new string([.. value.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')]);
        return clean.Length > 0 ? clean : "0";
    }
}
