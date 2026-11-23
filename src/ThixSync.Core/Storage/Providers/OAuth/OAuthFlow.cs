using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ThixSync.Core.Infrastructure;
using ThixSync.Core.Models;

namespace ThixSync.Core.Storage.Providers.OAuth;

/// <summary>What a token endpoint handed out.</summary>
internal sealed record OAuthTokens(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt);

/// <summary>
/// Signs in in the user's browser: a one-off listener on the loopback address receives the answer (no browser inside the app, the
/// password never passes through ThixSync), the code is exchanged with PKCE (S256), <c>state</c> ties the answer to this request.
/// Refresh tokens are kept by the business layer (the secret of the connection); access tokens live in memory only.
/// </summary>
internal static class OAuthFlow
{
    public static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Opens the browser (through <paramref name="openBrowser"/>) and waits up to 5 minutes for the answer. Throws BusinessException
    /// (refused, timed out, wrong answer) and OperationCanceledException.
    /// </summary>
    public static async Task<OAuthTokens> SignInAsync(OAuthApp app, Action<string> openBrowser, CancellationToken cancellationToken)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));

        LoopbackListener listener;
        try
        {
            listener = new LoopbackListener(app.RedirectPort);
        }
        catch (SocketException)
        {
            throw new BusinessException($"Die Anmeldung braucht den Port {app.RedirectPort} dieses Computers, ein anderes Programm belegt ihn gerade. " +
                                        "Bitte das andere Programm schließen und erneut versuchen.");
        }

        using (listener)
        {
            var redirectUri = app.RedirectUri(listener.Port);
            openBrowser(AuthorizeUrl(app, redirectUri, state, Challenge(verifier)));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(SignInTimeout);
            Dictionary<string, string> answer;
            try
            {
                answer = await listener.WaitForRedirectAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new BusinessException("Die Anmeldung wurde nicht innerhalb von 5 Minuten abgeschlossen.");
            }

            if (answer.TryGetValue("error", out var error))
            {
                throw new BusinessException(RefusalMessage(app, error, answer.GetValueOrDefault("error_description", "")));
            }

            if (answer.GetValueOrDefault("state") != state || !answer.TryGetValue("code", out var code) || code.Length == 0)
            {
                throw new BusinessException("Die Antwort der Anmeldung passt nicht zu dieser Anfrage – bitte erneut anmelden.");
            }

            var form = new Dictionary<string, string>
            {
                ["client_id"] = app.ClientId,
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["code_verifier"] = verifier,
            };
            if (app.ClientSecret is { Length: > 0 } secret)
            {
                form["client_secret"] = secret;
            }

            try
            {
                return await RequestTokensAsync(app, form, cancellationToken);
            }
            catch (StorageException exception)
            {
                throw new BusinessException(exception.Message);
            }
        }
    }

    /// <summary>
    /// A new access token for a refresh token. Some clouds hand out a new refresh token with it (Box always: the old one is then
    /// spent; Microsoft and Dropbox sometimes): the caller keeps it. StorageException: Authentication when the sign-in is gone.
    /// </summary>
    public static Task<OAuthTokens> RefreshAsync(OAuthApp app, string refreshToken, CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = app.ClientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        };
        if (app.ClientSecret is { Length: > 0 } secret)
        {
            form["client_secret"] = secret;
        }

        if (app.ScopeOnRefresh)
        {
            form["scope"] = app.Scope;
        }

        return RequestTokensAsync(app, form, cancellationToken);
    }

    /// <summary>The address the browser opens (also read by tests).</summary>
    internal static string AuthorizeUrl(OAuthApp app, string redirectUri, string state, string challenge)
    {
        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = app.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["scope"] = app.Scope,
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        };
        foreach (var (name, value) in app.ExtraParameters)
        {
            parameters[name] = value;
        }

        return app.AuthorizeUrl + "?" + string.Join("&", parameters.Select(pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value)}"));
    }

    /// <summary>The PKCE challenge of a verifier: BASE64URL(SHA256(ASCII(verifier))).</summary>
    internal static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>
    /// Microsoft's answers when the company does not let its users allow apps themselves: AADSTS65001 (no consent yet),
    /// AADSTS90094 / 90095 (an administrator has to approve).
    /// </summary>
    internal static bool NeedsAdminConsent(string description) =>
        description.Contains("AADSTS65001", StringComparison.Ordinal) || description.Contains("AADSTS90094", StringComparison.Ordinal) ||
        description.Contains("AADSTS90095", StringComparison.Ordinal);

    /// <summary>The address an administrator opens once to allow ThixSync for the whole company.</summary>
    internal static string AdminConsentUrl(string clientId) =>
        $"https://login.microsoftonline.com/organizations/v2.0/adminconsent?client_id={Uri.EscapeDataString(clientId)}" +
        "&scope=https://graph.microsoft.com/.default&redirect_uri=http://localhost";

    private static string RefusalMessage(OAuthApp app, string error, string description)
    {
        if (app.ProviderName == "Microsoft" && NeedsAdminConsent(description))
        {
            return "Deine Firma lässt ThixSync noch nicht zu. Ein Administrator von Microsoft 365 gibt ThixSync einmal für die ganze Firma frei: " +
                   $"{AdminConsentUrl(app.ClientId)} öffnen und zustimmen. Danach klappt die Anmeldung für alle in der Firma.";
        }

        return error == "access_denied"
            ? "Die Anmeldung wurde abgebrochen oder der Zugriff nicht erlaubt."
            : $"{app.ProviderName} hat die Anmeldung abgelehnt ({FirstLine(description.Length > 0 ? description : error)}).";
    }

    private static async Task<OAuthTokens> RequestTokensAsync(OAuthApp app, Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        HttpStatusCode status;
        JsonObject body;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, app.TokenUrl) { Content = new FormUrlEncodedContent(form) };
            using var response = await CloudHttp.Client.SendAsync(request, cancellationToken);
            status = response.StatusCode;
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            body = ParseObject(text);
        }
        catch (HttpRequestException exception)
        {
            throw new StorageException(StorageErrorKind.Unreachable, $"{app.ProviderName} ist nicht erreichbar ({exception.Message}).", exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new StorageException(StorageErrorKind.Unreachable, $"{app.ProviderName} antwortet nicht.", exception);
        }

        var accessToken = Text(body, "access_token");
        if (status == HttpStatusCode.OK && accessToken.Length > 0)
        {
            var expiresIn = body["expires_in"] is JsonValue expires ? ReadSeconds(expires) : 3600;
            var refreshToken = Text(body, "refresh_token");
            return new OAuthTokens(accessToken, refreshToken.Length > 0 ? refreshToken : null, DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn - 60)));
        }

        var error = Text(body, "error");
        if (error.Length == 0)
        {
            error = $"HTTP {(int)status}";
        }

        Log.Warning($"{app.ProviderName} token request failed: {error}");
        if ((int)status >= 500 || status == HttpStatusCode.TooManyRequests)
        {
            throw new StorageException(StorageErrorKind.Transient, $"{app.ProviderName} kann die Anmeldung gerade nicht prüfen ({error}).");
        }

        if (error is "invalid_client" or "unauthorized_client")
        {
            throw new StorageException(StorageErrorKind.Authentication,
                $"{app.ProviderName} kennt die App-Registrierung nicht (Einstellungen › Erweitert › eigene App-Registrierung prüfen).");
        }

        if (error is "invalid_grant" or "interaction_required" or "expired_token")
        {
            throw new StorageException(StorageErrorKind.Authentication,
                $"Die Anmeldung bei {app.ProviderName} ist abgelaufen oder wurde widerrufen – bitte die Verbindung neu anmelden.");
        }

        var description = Text(body, "error_description");
        throw new StorageException(StorageErrorKind.Authentication,
            $"{app.ProviderName} hat die Anmeldung abgelehnt ({error}{(description.Length > 0 ? ": " + FirstLine(description) : "")}).");
    }

    private static JsonObject ParseObject(string text)
    {
        try
        {
            return JsonNode.Parse(text) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string Text(JsonObject json, string name) =>
        json[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    private static int ReadSeconds(JsonValue value)
    {
        if (value.TryGetValue<int>(out var number))
        {
            return number;
        }

        if (value.TryGetValue<long>(out var big))
        {
            return (int)Math.Min(big, int.MaxValue);
        }

        return value.TryGetValue<string>(out var text) && int.TryParse(text, out var parsed) ? parsed : 3600;
    }

    private static string FirstLine(string text) => text.Split('\n', '\r')[0].Trim();

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
