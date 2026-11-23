namespace ThixSync.Core.Storage.Providers.MatCloud;

/// <summary>
/// The content of an upload as the client of the contract wants it: a stream whose length is known (it would copy anything else into a
/// temporary file first). Reads exactly <see cref="Length"/> bytes of the source, reports them to the progress as they go, and can only
/// go back when the source can (a piece sent again after a broken connection); otherwise "seeking" is only allowed to where it is.
/// </summary>
internal sealed class UploadSource : Stream
{
    private readonly Stream _source;
    private readonly long _length;
    private readonly long _sourceStart;
    private readonly IProgress<long>? _progress;
    private long _position;

    public UploadSource(Stream source, long length, IProgress<long>? progress)
    {
        _source = source;
        _length = length;
        _sourceStart = source.CanSeek ? source.Position : 0;
        _progress = progress;
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var wanted = (int)Math.Min(buffer.Length, _length - _position);
        if (wanted <= 0)
        {
            return 0;
        }

        var read = await _source.ReadAsync(buffer[..wanted], cancellationToken);
        if (read == 0)
        {
            throw new StorageException(StorageErrorKind.ChangedWhileReading, "Die Datei ist beim Lesen kleiner geworden.");
        }

        _position += read;
        _progress?.Report(read);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => _length + offset,
        };
        if (target == _position)
        {
            return _position;
        }

        if (!_source.CanSeek || target < 0 || target > _length)
        {
            throw new NotSupportedException("The source of the upload cannot go back.");
        }

        _source.Position = _sourceStart + target;
        _position = target;
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        // the caller's stream stays open
    }

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// The content of a file of the Drive as it arrives (the body of GET content). Network errors become <see cref="StorageException"/>
/// (Transient); a read that waits longer than <see cref="ReadWaitLimit"/> fails instead of hanging. Disposing it ends the request.
/// </summary>
internal sealed class DownloadStream : Stream
{
    private static readonly TimeSpan ReadWaitLimit = TimeSpan.FromMinutes(2);

    private readonly HttpResponseMessage _response;
    private readonly Stream _body;
    private readonly long _length;
    private long _position;

    public DownloadStream(HttpResponseMessage response, Stream body, long length)
    {
        _response = response;
        _body = body;
        _length = length;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => _length;

    public override long Position { get => _position; set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReadWaitLimit);
        try
        {
            var read = await _body.ReadAsync(buffer, timeout.Token);
            _position += read;
            return read;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new StorageException(StorageErrorKind.Transient, "Die MatCloud schickt die Datei nicht weiter (Zeitüberschreitung).");
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException)
        {
            throw new StorageException(StorageErrorKind.Transient, "Die Verbindung zur MatCloud wurde beim Lesen unterbrochen.", exception);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

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
            _body.Dispose();
            _response.Dispose();
        }

        base.Dispose(disposing);
    }
}
