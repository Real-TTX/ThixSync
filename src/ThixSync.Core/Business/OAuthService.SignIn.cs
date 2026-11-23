using ThixSync.Core.Models;
using ThixSync.Core.Storage;
using ThixSync.Core.Storage.Providers.Box;
using ThixSync.Core.Storage.Providers.Dropbox;
using ThixSync.Core.Storage.Providers.GoogleDrive;
using ThixSync.Core.Storage.Providers.OAuth;
using ThixSync.Core.Storage.Providers.OneDrive;

namespace ThixSync.Core.Business;

/// <summary>
/// The sign-in itself: the app registration of the type (own one from Einstellungen › Erweitert, else the built-in one), the browser with
/// PKCE and a loopback address (<see cref="OAuthFlow"/>), the refresh token kept as the secret of the connection (<see cref="OAuthSecret"/>,
/// protected, never synced) and the account as "Angemeldet als".
/// </summary>
public static partial class OAuthService
{
    public static partial bool IsConfigured(ConnectionKind kind) => OAuthApp.Find(kind) is not null;

    public static async partial Task<string> SignInAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var connection = ConnectionService.Find(connectionId)
            ?? throw new BusinessException("Bitte die Verbindung zuerst speichern, dann anmelden.");
        var type = ConnectionCatalog.Get(connection.Kind);
        if (type.Auth != AuthKind.OAuth)
        {
            throw new BusinessException($"{type.Name} meldet sich nicht im Browser an.");
        }

        AccessPolicy.EnsureConnectionKindAllowed(connection.Kind);
        var app = OAuthApp.Find(connection.Kind) ?? throw new BusinessException(OAuthApp.NotConfiguredMessage(connection.Kind));

        var tokens = await OAuthFlow.SignInAsync(app, OpenBrowser, cancellationToken);
        if (tokens.RefreshToken is not { Length: > 0 } refreshToken)
        {
            throw new BusinessException($"{app.ProviderName} hat keinen dauerhaften Zugang erteilt – bitte erneut anmelden und den Zugriff erlauben.");
        }

        string account;
        try
        {
            account = await ReadAccountAsync(connection.Kind, tokens.AccessToken, cancellationToken);
        }
        catch (StorageException exception)
        {
            throw new BusinessException($"Angemeldet, aber das Konto ließ sich nicht lesen: {exception.Message}");
        }

        ConnectionService.SaveSecret(connectionId, new OAuthSecret(refreshToken, app.ClientId, account).ToJson());
        OAuthTokenSource.Forget(connectionId);
        ConnectionService.SaveLearnedSetting(connectionId, ConnectionFields.SignedInAs, account);
        return account;
    }

    private static Task<string> ReadAccountAsync(ConnectionKind kind, string accessToken, CancellationToken cancellationToken)
    {
        var api = new CloudApi(ConnectionCatalog.Get(kind).Name, OAuthTokenSource.Fixed(accessToken), 1, (_, _) => default);
        return kind switch
        {
            ConnectionKind.GoogleDrive => GoogleDriveStorage.ReadAccountAsync(api, cancellationToken),
            ConnectionKind.OneDrive or ConnectionKind.SharePoint => GraphStorage.ReadAccountAsync(api, cancellationToken),
            ConnectionKind.Dropbox => DropboxStorage.ReadAccountAsync(api, cancellationToken),
            ConnectionKind.Box => BoxStorage.ReadAccountAsync(api, cancellationToken),
            _ => throw new BusinessException($"{ConnectionCatalog.Get(kind).Name} meldet sich nicht im Browser an."),
        };
    }
}
