using ThixSync.Core.Remote;

namespace ThixSync.Core.Storage.Providers.Remote;

/// <summary>
/// A file of the other computer as it arrives (data frames up to the end frame). The connection goes back to the pool at the end frame;
/// a stream disposed or cancelled before the end closes its connection (the rest of the file would still be on the way).
/// </summary>
internal sealed class RemoteReadStream : Stream
{
    private readonly RemoteStorage _storage;
    private readonly RemoteClientConnection _connection;
    private readonly long _size;
    private ReadOnlyMemory<byte> _pending;
    private long _received;
    private bool _ended;
    private int _released;

    public RemoteReadStream(RemoteStorage storage, RemoteClientConnection connection, long size)
    {
        _storage = storage;
        _connection = connection;
        _size = size;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    /// <summary>The size the file had when it was opened.</summary>
    public override long Length => _size;

    public override long Position { get => _received - _pending.Length; set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _released) != 0 && !_ended, this);
        while (_pending.IsEmpty)
        {
            if (_ended)
            {
                return 0;
            }

            await NextFrameAsync(cancellationToken);
        }

        var count = Math.Min(buffer.Length, _pending.Length);
        _pending[..count].CopyTo(buffer);
        _pending = _pending[count..];
        return count;
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

    public override async ValueTask DisposeAsync()
    {
        await ReleaseAsync(reusable: false);
        await base.DisposeAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ReleaseAsync(reusable: false).AsTask().GetAwaiter().GetResult();
        }

        base.Dispose(disposing);
    }

    private async Task NextFrameAsync(CancellationToken cancellationToken)
    {
        RemoteFrame frame;
        try
        {
            frame = await _connection.Channel.ReadAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await ReleaseAsync(reusable: false);
            throw;
        }
        catch (Exception exception) when (RemoteStorage.IsNetworkProblem(exception))
        {
            await ReleaseAsync(reusable: false);
            throw _storage.MapNetwork(exception);
        }

        switch (frame.Type)
        {
            case RemoteFrameType.Data:
                _received += frame.Payload.Length;
                _pending = frame.Payload;
                return;
            case RemoteFrameType.End:
                _ended = true;
                await ReleaseAsync(reusable: true);
                if (_received != _size)
                {
                    throw new StorageException(StorageErrorKind.ChangedWhileReading, "Die Datei hat sich auf dem anderen Computer beim Lesen geändert.");
                }

                return;
            case RemoteFrameType.Error:
                _ended = true;
                try
                {
                    RemoteChannel.ThrowIfError(frame);
                }
                catch (RemoteRefusedException exception)
                {
                    await ReleaseAsync(RemoteStorage.IsReusableAfter(exception));
                    throw;
                }

                return;
            default:
                await ReleaseAsync(reusable: false);
                throw _storage.MapNetwork(new InvalidDataException($"Unexpected frame {frame.Type} while reading."));
        }
    }

    /// <summary>Gives the connection back once (to the pool only when the file was read to its end).</summary>
    private ValueTask ReleaseAsync(bool reusable) =>
        Interlocked.Exchange(ref _released, 1) == 0 ? _storage.ReleaseAsync(_connection, reusable && _ended) : ValueTask.CompletedTask;
}
