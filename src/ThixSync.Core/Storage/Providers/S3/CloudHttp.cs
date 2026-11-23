using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace ThixSync.Core.Storage.Providers.S3;

/// <summary>
/// What the HTTP providers of object storages share (S3 family, Azure, pCloud): one pooled client per storage, the mapping of
/// transport errors and HTTP codes to <see cref="StorageException"/>, URI encoding, streams that report progress and streams of
/// responses that turn network errors into storage errors.
/// </summary>
internal static class CloudHttp
{
    /// <summary>How long a request without a large body may take until its answer starts.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(2);

    /// <summary>A client for one storage: enough connections for the engine's workers, no redirects (they would break signatures).</summary>
    public static HttpClient CreateClient(HttpMessageHandler? handler, int connections)
    {
        handler ??= new SocketsHttpHandler
        {
            MaxConnectionsPerServer = connections,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            ConnectTimeout = TimeSpan.FromSeconds(30),
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
        };
        var client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ThixSync", "1.0"));
        return client;
    }

    /// <summary>
    /// Sends a request: with <paramref name="limitTime"/> the answer must start within <see cref="RequestTimeout"/> (not for
    /// uploads of large bodies). Transport errors become <see cref="StorageException"/>; cancellation stays cancellation.
    /// </summary>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request, HttpCompletionOption completion, bool limitTime,
        string path, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (limitTime)
        {
            timeout.CancelAfter(RequestTimeout);
        }

        try
        {
            return await client.SendAsync(request, completion, timeout.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new StorageException(StorageErrorKind.Transient, $"Der Server antwortet nicht (Zeitüberschreitung){Where(path)}.", exception);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw MapTransport(exception, path);
        }
    }

    /// <summary>A network error without an answer of the server.</summary>
    public static StorageException MapTransport(Exception exception, string path)
    {
        if (exception is StorageException storage)
        {
            return storage;
        }

        if (exception is HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError } request)
        {
            var reason = request.HttpRequestError switch
            {
                HttpRequestError.NameResolutionError => "Der Server wurde nicht gefunden",
                HttpRequestError.SecureConnectionError => "Die sichere Verbindung zum Server kam nicht zustande",
                _ => "Der Server ist nicht erreichbar",
            };
            return new StorageException(StorageErrorKind.Unreachable, $"{reason}{Where(path)}.", exception);
        }

        return new StorageException(StorageErrorKind.Transient, $"Die Verbindung zum Server brach ab{Where(path)}.", exception);
    }

    /// <summary>The kind of an HTTP error when the service gave no better code.</summary>
    public static StorageErrorKind KindOf(HttpStatusCode status) => (int)status switch
    {
        401 => StorageErrorKind.Authentication,
        403 => StorageErrorKind.AccessDenied,
        404 or 410 => StorageErrorKind.NotFound,
        429 or 503 => StorageErrorKind.RateLimited,
        507 => StorageErrorKind.QuotaExceeded,
        408 or 409 or 412 or >= 500 => StorageErrorKind.Transient,
        _ => StorageErrorKind.Unknown,
    };

    /// <summary>A German sentence for a kind of error (the caller adds the path).</summary>
    public static string Describe(StorageErrorKind kind) => kind switch
    {
        StorageErrorKind.Authentication => "Die Anmeldung wurde abgelehnt",
        StorageErrorKind.AccessDenied => "Kein Zugriff",
        StorageErrorKind.NotFound => "Nicht gefunden",
        StorageErrorKind.RateLimited => "Der Dienst bittet, langsamer zu machen",
        StorageErrorKind.QuotaExceeded => "Der Speicherplatz ist voll",
        StorageErrorKind.Transient => "Vorübergehender Fehler des Servers",
        StorageErrorKind.InvalidName => "Der Name ist dort nicht erlaubt",
        StorageErrorKind.Unreachable => "Der Server ist nicht erreichbar",
        _ => "Der Server meldet einen Fehler",
    };

    /// <summary>How long the server asked to wait (Retry-After as seconds or date); null when it did not say.</summary>
    public static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var retry = response.Headers.RetryAfter;
        if (retry?.Delta is { } delta)
        {
            return delta;
        }

        if (retry?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }

    /// <summary>" (path)" for messages, empty for the root.</summary>
    public static string Where(string path) => path.Length == 0 ? "" : $" ({path})";

    /// <summary>
    /// RFC 3986 encoding as AWS and Azure sign it: unreserved characters (A–Z a–z 0–9 - . _ ~) stay, every other UTF-8 byte
    /// becomes %XX (upper case). With <paramref name="keepSlash"/> '/' stays (object keys in paths).
    /// </summary>
    public static string UriEncode(string value, bool keepSlash)
    {
        var builder = new StringBuilder(value.Length + 16);
        Span<byte> bytes = stackalloc byte[4];
        foreach (var rune in value.EnumerateRunes())
        {
            if (rune.IsAscii && (IsUnreserved((char)rune.Value) || (keepSlash && rune.Value == '/')))
            {
                builder.Append((char)rune.Value);
                continue;
            }

            var count = rune.EncodeToUtf8(bytes);
            for (var index = 0; index < count; index++)
            {
                builder.Append('%').Append(bytes[index].ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    /// <summary>The query part of a URL ("a=1&amp;b=x%20y") from parameters in the given order.</summary>
    public static string Query(IEnumerable<KeyValuePair<string, string>> parameters) =>
        string.Join('&', parameters.Select(parameter => $"{UriEncode(parameter.Key, false)}={UriEncode(parameter.Value, false)}"));

    /// <summary>
    /// Waits for all tasks; when some failed, throws the first real error (not the cancellations it caused in its siblings).
    /// </summary>
    public static async Task WhenAllAsync(IReadOnlyCollection<Task> tasks)
    {
        try
        {
            await Task.WhenAll(tasks);
        }
        catch
        {
            var real = tasks.Where(task => task.IsFaulted).Select(task => task.Exception!.InnerException!).FirstOrDefault(exception => exception is not OperationCanceledException);
            if (real is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(real);
            }

            throw;
        }
    }

    /// <summary>Retries a step with a buffered body (a part, a block) a few times when the error is worth it.</summary>
    public static async Task<T> RetryAsync<T>(Func<Task<T>> step, CancellationToken cancellationToken)
    {
        const int Attempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await step();
            }
            catch (StorageException exception) when (exception.IsRetryable && attempt < Attempts)
            {
                var wait = exception.RetryAfter ?? TimeSpan.FromSeconds(attempt);
                await Task.Delay(wait > TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : wait, cancellationToken);
            }
        }
    }

    /// <summary>Fills <paramref name="buffer"/> completely; a source that ends earlier did not give what it promised.</summary>
    public static async Task ReadExactlyAsync(Stream source, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        try
        {
            await source.ReadExactlyAsync(buffer, cancellationToken);
        }
        catch (EndOfStreamException exception)
        {
            throw new StorageException(StorageErrorKind.ChangedWhileReading, "Die Datei wurde beim Lesen kürzer.", exception);
        }
    }

    private static bool IsUnreserved(char character) =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.' or '_' or '~';
}

/// <summary>
/// A read-only stream over another one that reports how far it was read. <see cref="Length"/> is the promised length (so an
/// HTTP body can carry Content-Length over a stream that cannot seek). With a shared <see cref="ProgressMark"/>, a body sent a
/// second time (a retried part) reports only the bytes beyond what was already reported.
/// </summary>
internal sealed class ProgressStream(Stream inner, long length, IProgress<long>? progress, ProgressMark? mark = null, bool leaveOpen = true) : Stream
{
    private readonly ProgressMark _mark = mark ?? new ProgressMark();
    private long _position;

    /// <summary>Called with every block read (e.g. to hash the content on the way).</summary>
    public Action<ReadOnlySpan<byte>>? OnRead { get; init; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => length;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Advance(buffer.AsSpan(offset), inner.Read(buffer, offset, count));

    public override int Read(Span<byte> buffer) => Advance(buffer, inner.Read(buffer));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken);
        return Advance(buffer.Span, read);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        // a library may "rewind" a stream that was not read yet; anything else cannot be done over a forward-only source
        if (offset == 0 && origin == SeekOrigin.Begin && _position == 0)
        {
            return 0;
        }

        throw new NotSupportedException();
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !leaveOpen)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private int Advance(Span<byte> buffer, int read)
    {
        if (read <= 0)
        {
            return read;
        }

        OnRead?.Invoke(buffer[..read]);
        _position += read;
        if (_position > _mark.Reported)
        {
            progress?.Report(_position - _mark.Reported);
            _mark.Reported = _position;
        }

        return read;
    }
}

/// <summary>How many bytes of one body were reported already (shared by the attempts of a retried part).</summary>
internal sealed class ProgressMark
{
    public long Reported { get; set; }
}

/// <summary>
/// The body of a download: disposes the response with the stream and turns network errors while reading into
/// <see cref="StorageException"/> (Transient: the engine retries the file).
/// </summary>
internal sealed class ResponseStream(HttpResponseMessage response, Stream inner, string path) : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => response.Content.Headers.ContentLength ?? throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public static async Task<Stream> OpenAsync(HttpResponseMessage response, string path, CancellationToken cancellationToken)
    {
        try
        {
            return new ResponseStream(response, await response.Content.ReadAsStreamAsync(cancellationToken), path);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            response.Dispose();
            throw CloudHttp.MapTransport(exception, path);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        try
        {
            return inner.Read(buffer, offset, count);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw CloudHttp.MapTransport(exception, path);
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            return await inner.ReadAsync(buffer, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw CloudHttp.MapTransport(exception, path);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
            response.Dispose();
        }

        base.Dispose(disposing);
    }
}
