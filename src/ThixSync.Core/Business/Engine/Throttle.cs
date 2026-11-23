using System.Diagnostics;
using System.Security.Cryptography;

namespace ThixSync.Core.Business.Engine;

/// <summary>
/// The bandwidth limit of all jobs together (<see cref="Models.AppSettings.BandwidthLimitKiB"/>): a token bucket every streamed
/// transfer draws from (copies by the operating system are not limited). Bytes beyond the bucket become a debt the next reader
/// waits for, so the long-run rate is the limit whatever the number of workers.
/// </summary>
internal static class BandwidthThrottle
{
    private static readonly Lock Gate = new();
    private static double _tokens;
    private static long _lastRefill = Stopwatch.GetTimestamp();

    /// <summary>Tests: the limit in bytes per second instead of the setting (0: none).</summary>
    internal static Func<long>? LimitForTests { get; set; }

    /// <summary>The limit in bytes per second (0: none).</summary>
    public static long BytesPerSecond => LimitForTests?.Invoke() ?? SettingsService.Get().BandwidthLimitKiB * 1024L;

    /// <summary>Takes <paramref name="bytes"/> from the bucket and waits as long as the limit asks.</summary>
    public static ValueTask ConsumeAsync(long bytes, long limit, CancellationToken cancellationToken)
    {
        var wait = Reserve(bytes, limit);
        return wait <= TimeSpan.Zero ? ValueTask.CompletedTask : new ValueTask(Task.Delay(wait, cancellationToken));
    }

    public static void Consume(long bytes, long limit)
    {
        var wait = Reserve(bytes, limit);
        if (wait > TimeSpan.Zero)
        {
            Thread.Sleep(wait);
        }
    }

    private static TimeSpan Reserve(long bytes, long limit)
    {
        if (limit <= 0)
        {
            return TimeSpan.Zero;
        }

        lock (Gate)
        {
            var now = Stopwatch.GetTimestamp();
            var burst = Math.Max(64 * 1024, limit / 4.0);
            _tokens = Math.Min(burst, _tokens + (Stopwatch.GetElapsedTime(_lastRefill, now).TotalSeconds * limit));
            _lastRefill = now;
            _tokens -= bytes;
            return _tokens >= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(-_tokens / limit);
        }
    }
}

/// <summary>
/// The stream a transfer reads the source through: it counts the bytes (progress of the transfer and of the run), feeds the hash
/// of the copy check (computed while copying, so the source is read only once) and keeps to the bandwidth limit.
/// </summary>
internal sealed class TransferStream(Stream inner, long length, IncrementalHash? hash, Action<long> onRead) : Stream
{
    private const int ThrottledChunk = 64 * 1024;

    private readonly long _limit = BandwidthThrottle.BytesPerSecond;
    private long _bytesRead;

    public long BytesRead => _bytesRead;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    /// <summary>The length the transfer expects (some writers ask for it; the stream cannot seek).</summary>
    public override long Length => length;

    public override long Position
    {
        get => _bytesRead;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_limit > 0 && buffer.Length > ThrottledChunk)
        {
            buffer = buffer[..ThrottledChunk];
        }

        var read = inner.Read(buffer);
        if (read > 0)
        {
            hash?.AppendData(buffer[..read]);
            BandwidthThrottle.Consume(read, _limit);
            Count(read);
        }

        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_limit > 0 && buffer.Length > ThrottledChunk)
        {
            buffer = buffer[..ThrottledChunk];
        }

        var read = await inner.ReadAsync(buffer, cancellationToken);
        if (read > 0)
        {
            hash?.AppendData(buffer.Span[..read]);
            await BandwidthThrottle.ConsumeAsync(read, _limit, cancellationToken);
            Count(read);
        }

        return read;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private void Count(int read)
    {
        _bytesRead += read;
        onRead(read);
    }
}
