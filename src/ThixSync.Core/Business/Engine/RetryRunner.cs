using ThixSync.Core.Storage;
using ThixSync.Core.Storage.Providers.Local;

namespace ThixSync.Core.Business.Engine;

/// <summary>
/// Runs one storage operation with retries: errors a storage marks as passing (<see cref="StorageException.IsRetryable"/>) are tried
/// again after 0.5 s, 1 s, 2 s … (at most 30 s), "too many requests" waits as long as the storage asks and halves the lane's
/// parallelism. Every other error is mapped to a <see cref="StorageException"/> and thrown to the caller.
/// </summary>
internal sealed class RetryRunner(int retries, TransferMeter meter)
{
    private static readonly TimeSpan MaximumDelay = TimeSpan.FromSeconds(30);

    /// <summary>The first wait (tests shorten it).</summary>
    internal static TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    public async Task<T> RunAsync<T>(AdaptiveLimiter limiter, Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            StorageException failure;
            await limiter.EnterAsync(cancellationToken);
            meter.WorkerStarted();
            try
            {
                var result = await operation(cancellationToken);
                limiter.OnSuccess();
                return result;
            }
            catch (Exception exception) when (!(exception is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                failure = Map(exception);
            }
            finally
            {
                meter.WorkerEnded();
                limiter.Exit();
            }

            if (!failure.IsRetryable || attempt >= retries)
            {
                throw failure;
            }

            var delay = DelayOf(attempt);
            if (failure.Kind == StorageErrorKind.RateLimited)
            {
                limiter.OnThrottled();
                if (failure.RetryAfter is { } retryAfter && retryAfter > delay)
                {
                    delay = retryAfter < TimeSpan.FromMinutes(5) ? retryAfter : TimeSpan.FromMinutes(5);
                }
            }

            await Task.Delay(delay, cancellationToken);
        }
    }

    public Task RunAsync(AdaptiveLimiter limiter, Func<CancellationToken, Task> operation, CancellationToken cancellationToken) =>
        RunAsync(limiter, async token =>
        {
            await operation(token);
            return true;
        }, cancellationToken);

    /// <summary>Any failure of a storage call as a <see cref="StorageException"/> (providers should throw those themselves).</summary>
    public static StorageException Map(Exception exception) => exception switch
    {
        StorageException storage => storage,
        OperationCanceledException => new StorageException(StorageErrorKind.Unreachable, "Zeitüberschreitung – keine Antwort.", exception),
        TimeoutException => new StorageException(StorageErrorKind.Unreachable, "Zeitüberschreitung – keine Antwort.", exception),
        HttpRequestException => new StorageException(StorageErrorKind.Unreachable, $"Keine Verbindung: {exception.Message}", exception),
        IOException or UnauthorizedAccessException or ArgumentException => LocalStorage.Map(exception, ""),
        NotSupportedException => new StorageException(StorageErrorKind.Unknown, "Das kann dieser Speicher nicht.", exception),
        _ => new StorageException(StorageErrorKind.Unknown, exception.Message, exception),
    };

    private static TimeSpan DelayOf(int attempt)
    {
        var delay = BaseDelay * Math.Pow(2, Math.Min(attempt, 16));
        return delay < MaximumDelay ? delay : MaximumDelay;
    }
}
