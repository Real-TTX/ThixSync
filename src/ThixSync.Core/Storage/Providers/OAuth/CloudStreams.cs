using System.Net;

namespace ThixSync.Core.Storage.Providers.OAuth;

/// <summary>Reading the source of an upload in pieces and handing downloads to the engine (never a whole big file in memory).</summary>
internal static class CloudStreams
{
    /// <summary>Reads exactly <paramref name="count"/> bytes into <paramref name="buffer"/>; ChangedWhileReading when the source ends early.</summary>
    public static async Task ReadExactlyAsync(Stream source, byte[] buffer, int count, string path, CancellationToken cancellationToken)
    {
        var filled = 0;
        while (filled < count)
        {
            var read = await source.ReadAsync(buffer.AsMemory(filled, count - filled), cancellationToken);
            if (read == 0)
            {
                throw new StorageException(StorageErrorKind.ChangedWhileReading, $"Die Datei {path} wurde beim Lesen kürzer.");
            }

            filled += read;
        }
    }

    /// <summary>A small file as a whole (it can then be sent again after a failed attempt).</summary>
    public static async Task<byte[]> ReadSmallAsync(Stream source, long length, string path, CancellationToken cancellationToken)
    {
        var buffer = new byte[length];
        await ReadExactlyAsync(source, buffer, (int)length, path, cancellationToken);
        return buffer;
    }

    /// <summary>The download stream of an answer; disposing it disposes the answer.</summary>
    public static async Task<Stream> OpenAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return new ResponseStream(response, await response.Content.ReadAsStreamAsync(cancellationToken));
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private sealed class ResponseStream(HttpResponseMessage response, Stream inner) : Stream
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

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);

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
}

/// <summary>
/// The body of a streamed upload: exactly <c>length</c> bytes of the source, every sent piece reported (the number of bytes, like LocalStorage). A source that ends early
/// fails with ChangedWhileReading (never a short file under the real name). Can be sent once only.
/// </summary>
internal sealed class ProgressContent(Stream source, long length, IProgress<long>? progress, string path) : HttpContent
{
    private const int BufferSize = 256 * 1024;

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];
        var sent = 0L;
        while (sent < length)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - sent)), cancellationToken);
            if (read == 0)
            {
                throw new StorageException(StorageErrorKind.ChangedWhileReading, $"Die Datei {path} wurde beim Lesen kürzer.");
            }

            await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            sent += read;
            progress?.Report(read);
        }
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override bool TryComputeLength(out long length1)
    {
        length1 = length;
        return true;
    }
}
