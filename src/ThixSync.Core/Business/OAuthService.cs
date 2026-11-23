using ThixSync.Core.Models;

namespace ThixSync.Core.Business;

/// <summary>
/// Signs a connection in at its cloud (Google Drive, OneDrive, SharePoint, Dropbox, Box) in the user's browser (OAuth 2 with PKCE and a
/// loopback address). The refresh token becomes the secret of the connection; the account shows as "Angemeldet als".
///
/// CONTRACT – implemented with the OAuth providers; keep the public members.
/// </summary>
public static partial class OAuthService
{
    /// <summary>How the browser is opened (the app sets its own opener; tests replace it).</summary>
    public static Action<string> OpenBrowser { get; set; } = url =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });

    /// <summary>Whether ThixSync has an app registration for the type (built in or in Einstellungen › Erweitert).</summary>
    public static partial bool IsConfigured(ConnectionKind kind);

    /// <summary>
    /// Opens the browser, waits for the sign-in and keeps the token for the connection (which must be saved already). Returns the
    /// signed-in account ("anna@example.com"). Throws BusinessException (not configured, refused, timed out) and OperationCanceledException.
    /// </summary>
    public static partial Task<string> SignInAsync(Guid connectionId, CancellationToken cancellationToken);
}
