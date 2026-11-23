using System.Collections.Concurrent;

namespace ThixSync.Core.Storage.Providers.Network;

/// <summary>
/// A pool of connections to one server (SFTP sessions, FTP control connections): at most <see cref="Size"/> are in use at the same
/// time, idle ones are reused, a broken one is thrown away (<see cref="Lease.Break"/>) and replaced by a new one on demand. Safe for
/// many callers at once.
/// </summary>
internal sealed class ConnectionPool<T> : IAsyncDisposable where T : class
{
    private readonly Func<CancellationToken, Task<T>> _create;
    private readonly Func<T, bool> _isAlive;
    private readonly Func<T, ValueTask> _destroy;
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentBag<T> _idle = [];
    private bool _disposed;

    public ConnectionPool(int size, Func<CancellationToken, Task<T>> create, Func<T, bool> isAlive, Func<T, ValueTask> destroy)
    {
        Size = Math.Max(1, size);
        _create = create;
        _isAlive = isAlive;
        _destroy = destroy;
        _slots = new SemaphoreSlim(Size, Size);
    }

    public int Size { get; }

    /// <summary>A connection for the caller alone until the lease is disposed (waits while all are in use).</summary>
    public async Task<Lease> RentAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _slots.WaitAsync(cancellationToken);
        try
        {
            while (_idle.TryTake(out var idle))
            {
                if (_isAlive(idle))
                {
                    return new Lease(this, idle);
                }

                await _destroy(idle);
            }

            return new Lease(this, await _create(cancellationToken));
        }
        catch
        {
            _slots.Release();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        while (_idle.TryTake(out var idle))
        {
            await _destroy(idle);
        }
    }

    private async ValueTask GiveBackAsync(T connection, bool broken)
    {
        try
        {
            if (broken || _disposed || !_isAlive(connection))
            {
                await _destroy(connection);
                return;
            }

            _idle.Add(connection);
        }
        finally
        {
            _slots.Release();
        }
    }

    /// <summary>One rented connection. Dispose gives it back (or throws it away after <see cref="Break"/>).</summary>
    public sealed class Lease : IAsyncDisposable
    {
        private readonly ConnectionPool<T> _pool;
        private int _returned;
        private bool _broken;

        internal Lease(ConnectionPool<T> pool, T client)
        {
            _pool = pool;
            Client = client;
        }

        public T Client { get; }

        /// <summary>The connection is in an unknown state (connection lost, a transfer was aborted): it is not reused.</summary>
        public void Break() => _broken = true;

        public ValueTask DisposeAsync()
        {
            return Interlocked.Exchange(ref _returned, 1) == 0 ? _pool.GiveBackAsync(Client, _broken) : ValueTask.CompletedTask;
        }
    }
}
