using ThixSync.Core.Infrastructure;
using ThixSync.Core.Models;

namespace ThixSync.Core.Storage.Providers.OAuth;

/// <summary>
/// How one cloud signs in a desktop app (OAuth 2.0 authorization code with PKCE and a loopback redirect) with the app registration
/// in use: the user's own one (Einstellungen › Erweitert, <see cref="OAuthClientSettings"/>) or else the one built in at build time
/// (<see cref="AppInfo"/>, Directory.Build.props). The registrations the owner has to create are described in docs/APP-REGISTRIERUNG.md.
/// </summary>
internal sealed record OAuthApp(
    string ProviderName,
    string AuthorizeUrl,
    string TokenUrl,
    string ClientId,
    string? ClientSecret,
    string Scope,
    string RedirectHost,
    int RedirectPort,
    IReadOnlyDictionary<string, string> ExtraParameters)
{
    /// <summary>
    /// The fixed loopback port of Dropbox: Dropbox compares the redirect address exactly (with the port), unlike Google, Microsoft and
    /// Box, which accept any port of the loopback address. Registered as http://127.0.0.1:53917/ in the Dropbox App Console.
    /// </summary>
    public const int DropboxRedirectPort = 53917;

    /// <summary>The scopes of Google Drive (the whole Drive: files of other apps and of the web too).</summary>
    public const string GoogleScope = "https://www.googleapis.com/auth/drive";

    /// <summary>The scopes of OneDrive (Graph; offline_access brings the refresh token).</summary>
    public const string OneDriveScope = "offline_access User.Read Files.ReadWrite.All";

    /// <summary>The scopes of SharePoint: also the sites (finding a site by its address needs them).</summary>
    public const string SharePointScope = "offline_access User.Read Files.ReadWrite.All Sites.ReadWrite.All";

    /// <summary>The scopes of a Dropbox app (scoped access; must be enabled on the app's Permissions tab).</summary>
    public const string DropboxScope = "account_info.read files.metadata.read files.metadata.write files.content.read files.content.write";

    /// <summary>Refreshing asks for the scopes again (Microsoft hands out a token for the first resource otherwise).</summary>
    public bool ScopeOnRefresh => ProviderName == "Microsoft";

    /// <summary>The redirect address the token request must repeat exactly.</summary>
    public string RedirectUri(int port) => $"http://{RedirectHost}:{port}/";

    /// <summary>The message when a type has no app registration (the business layer shows the same).</summary>
    public static string NotConfiguredMessage(ConnectionKind kind) =>
        $"Für {ConnectionCatalog.Get(kind).Name} ist noch keine App-Registrierung hinterlegt (Einstellungen › Erweitert).";

    /// <summary>The app registration of a type; StorageException (Authentication) when there is none.</summary>
    public static OAuthApp Require(ConnectionKind kind) =>
        Find(kind) ?? throw new StorageException(StorageErrorKind.Authentication, NotConfiguredMessage(kind));

    /// <summary>The folder a storage of the context is rooted at, relative to the top of the cloud (base folder + folder of the pair).</summary>
    public static string RootOf(StorageContext context) =>
        StoragePath.Combine(ConnectionCatalog.Value(context.Connection, ConnectionFields.BasePath), context.Folder);

    /// <summary>The app registration for a connection type, or null when there is none (neither built in nor own).</summary>
    public static OAuthApp? Find(ConnectionKind kind)
    {
        var own = SettingsStore.Read().OAuthClients;
        switch (kind)
        {
            case ConnectionKind.GoogleDrive:
            {
                var (id, secret) = Pick(own.GoogleClientId, own.GoogleClientSecret, AppInfo.BuiltInGoogleClientId, AppInfo.BuiltInGoogleClientSecret);
                return id.Length == 0 ? null : Google(id, secret);
            }

            case ConnectionKind.OneDrive or ConnectionKind.SharePoint:
            {
                var id = Pick(own.MicrosoftClientId, "", AppInfo.BuiltInMicrosoftClientId, "").Id;
                return id.Length == 0 ? null : Microsoft(id, withSites: kind == ConnectionKind.SharePoint);
            }

            case ConnectionKind.Dropbox:
            {
                var id = Pick(own.DropboxClientId, "", AppInfo.BuiltInDropboxClientId, "").Id;
                return id.Length == 0 ? null : Dropbox(id);
            }

            case ConnectionKind.Box:
            {
                var (id, secret) = Pick(own.BoxClientId, own.BoxClientSecret, AppInfo.BuiltInBoxClientId, AppInfo.BuiltInBoxClientSecret);
                return id.Length == 0 || secret.Length == 0 ? null : Box(id, secret);
            }

            default:
                return null;
        }
    }

    /// <summary>Google ("Desktop app" client; its secret is not confidential). Redirect: http://127.0.0.1 with any port.</summary>
    public static OAuthApp Google(string clientId, string clientSecret) => new(
        "Google",
        "https://accounts.google.com/o/oauth2/v2/auth",
        "https://oauth2.googleapis.com/token",
        clientId,
        clientSecret.Length > 0 ? clientSecret : null,
        GoogleScope,
        "127.0.0.1",
        0,
        new Dictionary<string, string> { ["access_type"] = "offline", ["prompt"] = "consent select_account" });

    /// <summary>Microsoft identity platform ("common": work, school and personal accounts; public client). Redirect: http://localhost, any port.</summary>
    public static OAuthApp Microsoft(string clientId, bool withSites) => new(
        "Microsoft",
        "https://login.microsoftonline.com/common/oauth2/v2.0/authorize",
        "https://login.microsoftonline.com/common/oauth2/v2.0/token",
        clientId,
        null,
        withSites ? SharePointScope : OneDriveScope,
        "localhost",
        0,
        new Dictionary<string, string> { ["prompt"] = "select_account" });

    /// <summary>Dropbox (PKCE without a secret; offline: a refresh token). Redirect: http://127.0.0.1:53917/ exactly.</summary>
    public static OAuthApp Dropbox(string clientId) => new(
        "Dropbox",
        "https://www.dropbox.com/oauth2/authorize",
        "https://api.dropboxapi.com/oauth2/token",
        clientId,
        null,
        DropboxScope,
        "127.0.0.1",
        DropboxRedirectPort,
        new Dictionary<string, string> { ["token_access_type"] = "offline" });

    /// <summary>Box (OAuth 2.0 user authentication; confidential client: id + secret). Redirect: http://127.0.0.1 with any port.</summary>
    public static OAuthApp Box(string clientId, string clientSecret) => new(
        "Box",
        "https://account.box.com/api/oauth2/authorize",
        "https://api.box.com/oauth2/token",
        clientId,
        clientSecret,
        "root_readwrite",
        "127.0.0.1",
        0,
        new Dictionary<string, string>());

    /// <summary>The own registration when its id is set (with its own secret), else the built-in one.</summary>
    private static (string Id, string Secret) Pick(string ownId, string ownSecret, string builtInId, string builtInSecret) =>
        ownId.Trim().Length > 0 ? (ownId.Trim(), ownSecret.Trim()) : (builtInId.Trim(), builtInSecret.Trim());
}
