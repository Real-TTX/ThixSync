using System.Text.Json;
using System.Text.Json.Nodes;

namespace ThixSync.Core.Storage.Providers.OAuth;

/// <summary>
/// The secret of an OAuth connection, kept protected by the business layer (DPAPI / Keychain, never synced, never logged). Format (JSON):
/// <code>
/// {
///   "refresh_token": "1//0g…",          the long-lived token of the sign-in (required)
///   "client_id": "1234….apps.google…",  the app registration it belongs to (another one cannot use it: sign in again)
///   "account": "anna@example.com"       who signed in (informational; the connection shows it as "Angemeldet als")
/// }
/// </code>
/// Access tokens are never stored: they live in memory only (<see cref="OAuthTokenSource"/>). A refresh token the cloud rotates (Box on
/// every refresh, Microsoft and Dropbox sometimes) replaces the stored one at once through <see cref="StorageContext.SaveSecretAsync"/>.
/// A secret that is no JSON is taken as a bare refresh token.
/// </summary>
internal sealed record OAuthSecret(string RefreshToken, string ClientId, string Account)
{
    public static OAuthSecret? Parse(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            return null;
        }

        var text = secret.Trim();
        if (!text.StartsWith('{'))
        {
            return new OAuthSecret(text, "", "");
        }

        try
        {
            if (JsonNode.Parse(text) is not JsonObject json)
            {
                return null;
            }

            var refreshToken = Read(json, "refresh_token");
            return refreshToken.Length == 0 ? null : new OAuthSecret(refreshToken, Read(json, "client_id"), Read(json, "account"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string ToJson() => new JsonObject
    {
        ["refresh_token"] = RefreshToken,
        ["client_id"] = ClientId,
        ["account"] = Account,
    }.ToJsonString();

    private static string Read(JsonObject json, string name) =>
        json[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
}
