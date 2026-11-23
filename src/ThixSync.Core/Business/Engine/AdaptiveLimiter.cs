namespace ThixSync.Core.Business.Engine;

/// <summary>
/// How many operations of a lane may run at the same time: at most the number of workers, halved when a storage says "too many
/// requests" and raised again by one after a run of successes (additive increase, multiplicative decrease). Waiters are served in
/// order.
/// </summary>
internal sealed class AdaptiveLimiter
{
    private readonly Lock _gate = new();
    private readonly Queue<TaskCompletionSource> _waiting = new();
    private readonly int _maximum;
    private int _limit;
    private int _active;
    private int _successes;

    public AdaptiveLimiter(int maximum)
    {
        _maximum = Math.Max(1, maximum);
        _limit = _maximum;
    }

    /// <summary>How many operations may run at the moment.</summary>
    public int Limit
    {
        get
        {
            lock (_gate)
            {
                return _limit;
            }
        }
    }

    public Task EnterAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource waiter;
        lock (_gate)
        {
            if (_active < _limit)
            {
                _active++;
                return Task.CompletedTask;
            }

            waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiting.Enqueue(waiter);
        }

        if (!cancellationToken.CanBeCanceled)
        {
            return waiter.Task;
        }

        // a cancelled waiter stays in the queue; Exit skips it (TrySetResult fails) and hands the place to the next one
        return WaitAsync(waiter, cancellationToken.Register(() => waiter.TrySetCanceled(cancellationToken)));

        static async Task WaitAsync(TaskCompletionSource waiter, CancellationTokenRegistration registration)
        {
            using (registration)
            {
                await waiter.Task;
            }
        }
    }

    public void Exit()
    {
        lock (_gate)
        {
            if (_active > _limit)
            {
                _active--;
                return;
            }

            while (_waiting.TryDequeue(out var waiter))
            {
                if (waiter.TrySetResult())
                {
                    return; // the place goes straight to the waiter
                }
            }

            _active--;
        }
    }

    /// <summary>An operation went through: after enough of them the limit grows by one.</summary>
    public void OnSuccess()
    {
        lock (_gate)
        {
            if (_limit >= _maximum || ++_successes < Math.Max(8, _limit * 2))
            {
                return;
            }

            _limit++;
            _successes = 0;
            while (_active < _limit && _waiting.TryDequeue(out var waiter))
            {
                if (waiter.TrySetResult())
                {
                    _active++;
                }
            }
        }
    }

    /// <summary>The storage asked to slow down: half as many at once.</summary>
    public void OnThrottled()
    {
        lock (_gate)
        {
            _limit = Math.Max(1, _limit / 2);
            _successes = 0;
        }
    }
}
