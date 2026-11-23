using System.Collections.Concurrent;

namespace ThixSync.Core.Storage.Providers.OAuth;

/// <summary>
/// The access token of one connection, in memory only. Every storage opened for the connection (source and target of several jobs, the
/// folder picker) shares the one instance: a rotating refresh token (Box spends it on every refresh) is used once, and when many workers
/// hit an expired token at the same time only one of them refreshes, the others take its result. A refresh token the cloud hands out
/// anew is kept at once through <see cref="StorageContext.SaveSecretAsync"/> (format: <see cref="OAuthSecret"/>).
/// </summary>
internal sealed class OAuthTokenSource
{
    private static readonly ConcurrentDictionary<Guid, OAuthTokenSource> ByConnection = new();
    private static readonly Lock CreateGate = new();
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromMinutes(2);

    private readonly OAuthApp? _app;
    private readonly string _account;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly HashSet<string> _knownRefreshTokens = new(StringComparer.Ordinal);
    private string _refreshToken;
    private Func<string, CancellationToken, Task> _saveSecret;
    private volatile AccessToken? _access;
    private SemaphoreSlim? _throttle;

    private OAuthTokenSource(OAuthApp? app, string refreshToken, string account, Func<string, CancellationToken, Task> saveSecret, AccessToken? access)
    {
        _app = app;
        _refreshToken = refreshToken;
        _account = account;
        _saveSecret = saveSecret;
        _access = access;
        _knownRefreshTokens.Add(refreshToken);
    }

    /// <summary>
    /// The token source of the context's connection (shared with every other storage of it). StorageException (Authentication) when the
    /// connection has not been signed in or was signed in with another app registration.
    /// </summary>
    public static OAuthTokenSource For(StorageContext context, OAuthApp app)
    {
        var secret = OAuthSecret.Parse(context.Secret)
            ?? throw new StorageException(StorageErrorKind.Authentication, $"„{context.Connection.Name}“ ist noch nicht bei {app.ProviderName} angemeldet – bitte anmelden.");
        if (secret.ClientId.Length > 0 && !string.Equals(secret.ClientId, app.ClientId, StringComparison.Ordinal))
        {
            throw new StorageException(StorageErrorKind.Authentication,
                $"Die App-Registrierung für {app.ProviderName} wurde geändert – bitte „{context.Connection.Name}“ neu anmelden.");
        }

        lock (CreateGate)
        {
            if (ByConnection.TryGetValue(context.Connection.Id, out var existing) && existing._app?.ClientId == app.ClientId && existing.Knows(secret.RefreshToken))
            {
                existing._saveSecret = context.SaveSecretAsync;
                return existing;
            }

            var created = new OAuthTokenSource(app, secret.RefreshToken, secret.Account, context.SaveSecretAsync, null);
            ByConnection[context.Connection.Id] = created;
            return created;
        }
    }

    /// <summary>A source with a token that is never refreshed (reading the account right after the sign-in).</summary>
    public static OAuthTokenSource Fixed(string accessToken) =>
        new(null, "", "", (_, _) => Task.CompletedTask, new AccessToken(accessToken, DateTimeOffset.MaxValue));

    /// <summary>Drops the kept token of a connection (a new sign-in, a deleted connection).</summary>
    public static void Forget(Guid connectionId) => ByConnection.TryRemove(connectionId, out _);

    /// <summary>A valid access token (refreshed when it ran out).</summary>
    public async ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (_access is { } access && access.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return access.Token;
        }

        return await RefreshAsync(null, cancellationToken);
    }

    /// <summary>
    /// The cloud refused <paramref name="failedToken"/> (401): a new one. When another worker renewed it meanwhile, its token is taken
    /// without asking the cloud again.
    /// </summary>
    public Task<string> RenewAsync(string failedToken, CancellationToken cancellationToken) => RefreshAsync(failedToken, cancellationToken);

    /// <summary>
    /// The gate of the requests of this connection: every storage opened for it (a job's source and target can be the same account)
    /// shares the API's rate limit. The first caller sets the size.
    /// </summary>
    public SemaphoreSlim Throttle(int maxParallelism) =>
        LazyInitializer.EnsureInitialized(ref _throttle, () => new SemaphoreSlim(maxParallelism, maxParallelism));

    private bool Knows(string refreshToken)
    {
        lock (_knownRefreshTokens)
        {
            return _knownRefreshTokens.Contains(refreshToken);
        }
    }

    private async Task<string> RefreshAsync(string? failedToken, CancellationToken cancellationToken)
    {
        if (_app is null)
        {
            throw new StorageException(StorageErrorKind.Authentication, "Die Anmeldung wurde abgelehnt.");
        }

        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            if (_access is { } current && current.Token != failedToken && current.ExpiresAt > DateTimeOffset.UtcNow)
            {
                return current.Token;
            }

            // Not cancelled half-way by one worker: a rotated refresh token must be kept once the cloud handed it out.
            using var timeout = new CancellationTokenSource(RefreshTimeout);
            var tokens = await OAuthFlow.RefreshAsync(_app, _refreshToken, timeout.Token);
            if (tokens.RefreshToken is { } rotated && rotated != _refreshToken)
            {
                _refreshToken = rotated;
                lock (_knownRefreshTokens)
                {
                    _knownRefreshTokens.Add(rotated);
                }

                await _saveSecret(new OAuthSecret(rotated, _app.ClientId, _account).ToJson(), CancellationToken.None);
            }

            _access = new AccessToken(tokens.AccessToken, tokens.ExpiresAt);
            return tokens.AccessToken;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private sealed record AccessToken(string Token, DateTimeOffset ExpiresAt);
}
