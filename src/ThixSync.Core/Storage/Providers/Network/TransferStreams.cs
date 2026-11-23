using System.Net;

namespace ThixSync.Core.Storage.Providers.Network;

/// <summary>
/// A read-only stream over the content the engine gives to WriteAsync: reports every block read to the progress (the bytes that
/// left for the server) and never reads more than the announced length.
/// </summary>
internal sealed class ProgressReadStream(Stream inner, long length, IProgress<long>? progress) : Stream
{
    private long _position;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => length;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    /// <summary>The bytes read so far.</summary>
    public long BytesRead => _position;

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var read = inner.Read(buffer[..Limit(buffer.Length)]);
        return Count(read);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer[..Limit(buffer.Length)], cancellationToken);
        return Count(read);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private int Limit(int wanted) => (int)Math.Min(wanted, Math.Max(0, length - _position));

    private int Count(int read)
    {
        if (read > 0)
        {
            _position += read;
            progress?.Report(read);
        }

        return read;
    }
}

/// <summary>
/// A stream handed out by OpenReadAsync that runs a callback when it is disposed (gives the connection back to its pool, reads the
/// final reply of an FTP transfer, disposes the HTTP response). <c>completed</c> says whether the whole content was read.
/// </summary>
internal sealed class OwnedReadStream(Stream inner, Func<bool, ValueTask> onDispose) : Stream
{
    private int _disposed;
    private bool _reachedEnd;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var read = inner.Read(buffer);
        _reachedEnd |= read == 0 && buffer.Length > 0;
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken);
        _reachedEnd |= read == 0 && buffer.Length > 0;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        try
        {
            await inner.DisposeAsync();
        }
        finally
        {
            await onDispose(_reachedEnd);
        }

        GC.SuppressFinalize(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try
            {
                inner.Dispose();
            }
            finally
            {
                onDispose(_reachedEnd).AsTask().GetAwaiter().GetResult();
            }
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// HTTP content of a known length read once from a stream (PUT of WebDAV, the file part of a Seafile upload): the length goes into
/// Content-Length (no chunked encoding), the bytes are streamed and reported to the progress, never buffered as a whole.
/// </summary>
internal sealed class StreamingContent(Stream source, long length, IProgress<long>? progress) : HttpContent
{
    private const int BufferSize = 256 * 1024;

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            var left = length;
            while (left > 0)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(BufferSize, left)), cancellationToken);
                if (read == 0)
                {
                    throw new StorageException(StorageErrorKind.ChangedWhileReading, "Die Datei ist kürzer geworden, während sie gelesen wurde.");
                }

                await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                left -= read;
                progress?.Report(read);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    protected override bool TryComputeLength(out long length1)
    {
        length1 = length;
        return true;
    }
}
