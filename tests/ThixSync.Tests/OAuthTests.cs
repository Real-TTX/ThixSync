using System.Net;
using System.Text.Json.Nodes;
using ThixSync.Core.Business;
using ThixSync.Core.Infrastructure;
using ThixSync.Core.Models;
using ThixSync.Core.Storage;
using ThixSync.Core.Storage.Providers.Box;
using ThixSync.Core.Storage.Providers.OAuth;

namespace ThixSync.Tests;

/// <summary>
/// The sign-in of the cloud storages: PKCE and state through a real loopback listener (the "browser" is the test calling the redirect
/// address), the secret and the account kept for the connection, refreshing once for many workers, rotated refresh tokens kept.
/// </summary>
public sealed class OAuthTests
{
    [Fact]
    public async Task Sign_in_uses_pkce_and_the_loopback_redirect_and_keeps_the_refresh_token()
    {
        using var test = new CloudTest();
        var connection = ConnectionService.Save(new Connection { Name = "Drive", Kind = ConnectionKind.GoogleDrive }, null);
        Dictionary<string, string> authorize = [];
        Task<string>? browser = null;
        OAuthService.OpenBrowser = url =>
        {
            authorize = Query(new Uri(url));
            var redirect = $"{authorize["redirect_uri"]}?code=the-code&state={Uri.EscapeDataString(authorize["state"])}";
            browser = Task.Run(async () =>
            {
                using var client = new HttpClient();
                return await client.GetStringAsync(redirect);
            });
        };
        test.Http.On(request => request.Url.StartsWith("https://oauth2.googleapis.com/token", StringComparison.Ordinal), request =>
        {
            var form = FormOf(request.Text);
            var ok = form["grant_type"] == "authorization_code" && form["code"] == "the-code" && form["redirect_uri"] == authorize["redirect_uri"]
                     && OAuthFlow.Challenge(form["code_verifier"]) == authorize["code_challenge"] && form["client_secret"] == "google-secret";
            return ok
                ? FakeHttp.Json("""{"access_token":"fresh","refresh_token":"the-refresh-token","expires_in":3599}""")
                : FakeHttp.Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest);
        });
        test.Http.On(HttpMethod.Get, "/drive/v3/about", request =>
            request.Header("Authorization") == "Bearer fresh" ? FakeHttp.Json("""{"user":{"emailAddress":"anna@example.com"}}""") : FakeHttp.Status(HttpStatusCode.Unauthorized));

        var account = await OAuthService.SignInAsync(connection.Id, CancellationToken.None);

        Assert.Equal("anna@example.com", account);
        Assert.Equal("S256", authorize["code_challenge_method"]);
        Assert.Equal(OAuthApp.GoogleScope, authorize["scope"]);
        Assert.Equal("offline", authorize["access_type"]);
        Assert.StartsWith("http://127.0.0.1:", authorize["redirect_uri"], StringComparison.Ordinal);
        Assert.Contains("Anmeldung abgeschlossen", await browser!, StringComparison.Ordinal);
        var secret = OAuthSecret.Parse(ConnectionService.ReadSecret(connection.Id));
        Assert.Equal(new OAuthSecret("the-refresh-token", CloudTest.GoogleClientId, "anna@example.com"), secret);
        Assert.Equal("anna@example.com", ConnectionService.Get(connection.Id).Get(ConnectionFields.SignedInAs));
        Assert.True(ConnectionService.Get(connection.Id).HasSecret);
    }

    [Fact]
    public async Task Sign_in_refuses_an_answer_of_another_request()
    {
        using var test = new CloudTest();
        var connection = ConnectionService.Save(new Connection { Name = "Box", Kind = ConnectionKind.Box }, null);
        Task? browser = null;
        OAuthService.OpenBrowser = url =>
        {
            var redirect = $"{Query(new Uri(url))["redirect_uri"]}?code=the-code&state=forged";
            browser = Task.Run(async () =>
            {
                using var client = new HttpClient();
                await client.GetStringAsync(redirect);
            });
        };

        var error = await Assert.ThrowsAsync<BusinessException>(() => OAuthService.SignInAsync(connection.Id, CancellationToken.None));

        Assert.Contains("passt nicht zu dieser Anfrage", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(test.Http.Requests, request => request.Url.Contains("token", StringComparison.Ordinal));
        await browser!;
        Assert.False(ConnectionService.Get(connection.Id).HasSecret);
    }

    [Fact]
    public async Task Sign_in_tells_when_the_user_refused_and_can_be_cancelled()
    {
        using var test = new CloudTest();
        var connection = ConnectionService.Save(new Connection { Name = "OneDrive", Kind = ConnectionKind.OneDrive }, null);
        OAuthService.OpenBrowser = url =>
        {
            var redirect = $"{Query(new Uri(url))["redirect_uri"]}?error=access_denied&state=x";
            _ = Task.Run(async () =>
            {
                using var client = new HttpClient();
                await client.GetStringAsync(redirect);
            });
        };

        var refused = await Assert.ThrowsAsync<BusinessException>(() => OAuthService.SignInAsync(connection.Id, CancellationToken.None));
        Assert.Contains("abgebrochen", refused.Message, StringComparison.Ordinal);

        OAuthService.OpenBrowser = _ => { };
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OAuthService.SignInAsync(connection.Id, cancel.Token));
    }

    [Fact]
    public async Task Without_an_app_registration_the_sign_in_says_where_to_enter_one()
    {
        using var test = new CloudTest();
        var connection = ConnectionService.Save(new Connection { Name = "Drive", Kind = ConnectionKind.GoogleDrive }, null);
        Assert.True(OAuthService.IsConfigured(ConnectionKind.GoogleDrive));
        SettingsStore.Update(settings => settings with { OAuthClients = new OAuthClientSettings() });

        var error = await Assert.ThrowsAsync<BusinessException>(() => OAuthService.SignInAsync(connection.Id, CancellationToken.None));

        Assert.False(OAuthService.IsConfigured(ConnectionKind.GoogleDrive)); // the test build has no built-in registration
        Assert.Equal("Für Google Drive ist noch keine App-Registrierung hinterlegt (Einstellungen › Erweitert).", error.Message);
        Assert.Throws<StorageException>(() => StorageProviders.Find(ConnectionKind.GoogleDrive)!.Open(test.Context(ConnectionKind.GoogleDrive)));
    }

    [Fact]
    public async Task Box_needs_its_client_secret_to_count_as_configured()
    {
        using var test = new CloudTest();
        SettingsStore.Update(settings => settings with { OAuthClients = settings.OAuthClients with { BoxClientSecret = "" } });

        Assert.False(OAuthService.IsConfigured(ConnectionKind.Box));
        Assert.True(OAuthService.IsConfigured(ConnectionKind.Dropbox));
        Assert.True(OAuthService.IsConfigured(ConnectionKind.SharePoint));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Many_workers_with_an_expired_token_refresh_once_and_a_rotated_refresh_token_is_kept()
    {
        using var test = new CloudTest(answerRefreshes: false);
        var refreshes = 0;
        test.Http.On(CloudTest.IsRefresh, request =>
        {
            var number = Interlocked.Increment(ref refreshes);
            var form = FormOf(request.Text);
            Assert.Equal($"refresh-{number}", form["refresh_token"]); // Box spends every refresh token: the new one must be used next
            Assert.Equal("box-secret", form["client_secret"]);
            return FakeHttp.Json($$"""{"access_token":"access-{{number}}","refresh_token":"refresh-{{number + 1}}","expires_in":3600}""");
        });
        test.Http.On(HttpMethod.Get, "api.box.com/2.0/events", request =>
            request.Header("Authorization") == "Bearer access-1"
                ? FakeHttp.Status(HttpStatusCode.Unauthorized) // revoked early: every worker sees 401 at the same time
                : FakeHttp.Json("""{"next_stream_position":1234}"""));
        var storage = (IChangeFeed)new BoxProvider().Open(test.Context(ConnectionKind.Box));

        var cursors = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => storage.GetCursorAsync(CancellationToken.None)));

        Assert.All(cursors, cursor => Assert.Equal("1234", cursor));
        Assert.Equal(2, refreshes);
        Assert.Equal(["refresh-2", "refresh-3"], test.SavedSecrets.Select(secret => OAuthSecret.Parse(secret)!.RefreshToken));
        Assert.All(test.SavedSecrets, secret => Assert.Equal(CloudTest.BoxClientId, OAuthSecret.Parse(secret)!.ClientId));
    }

    [Fact]
    public async Task A_revoked_sign_in_stops_the_run_with_an_authentication_error()
    {
        using var test = new CloudTest(answerRefreshes: false);
        test.Http.On(CloudTest.IsRefresh, _ => FakeHttp.Json("""{"error":"invalid_grant","error_description":"Token has been expired or revoked."}""", HttpStatusCode.BadRequest));
        var storage = new BoxProvider().Open(test.Context(ConnectionKind.Box));

        var error = await Assert.ThrowsAsync<StorageException>(() => ((IChangeFeed)storage).GetCursorAsync(CancellationToken.None));

        Assert.Equal(StorageErrorKind.Authentication, error.Kind);
        Assert.True(error.StopsRun);
        Assert.Contains("neu anmelden", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh-1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_connection_without_sign_in_or_of_another_registration_must_sign_in()
    {
        using var test = new CloudTest();
        var notSignedIn = test.Context(ConnectionKind.Dropbox) with { Secret = null };
        var otherApp = test.Context(ConnectionKind.Dropbox) with { Secret = new OAuthSecret("token", "another-app", "").ToJson() };

        Assert.Equal(StorageErrorKind.Authentication, Assert.Throws<StorageException>(() => StorageProviders.Find(ConnectionKind.Dropbox)!.Open(notSignedIn)).Kind);
        Assert.Contains("App-Registrierung", Assert.Throws<StorageException>(() => StorageProviders.Find(ConnectionKind.Dropbox)!.Open(otherApp)).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_secret_is_json_with_the_refresh_token_and_a_bare_token_still_works()
    {
        var secret = new OAuthSecret("rt", "client", "anna@example.com");
        var json = JsonNode.Parse(secret.ToJson())!;

        Assert.Equal("rt", json["refresh_token"]!.GetValue<string>());
        Assert.Equal(secret, OAuthSecret.Parse(secret.ToJson()));
        Assert.Equal("legacy-token", OAuthSecret.Parse("legacy-token")!.RefreshToken);
        Assert.Null(OAuthSecret.Parse("{}"));
        Assert.Null(OAuthSecret.Parse(""));
    }

    [Fact]
    public void The_cloud_providers_are_found_without_a_list()
    {
        Assert.True(StorageProviders.IsSupported(ConnectionKind.GoogleDrive));
        Assert.True(StorageProviders.IsSupported(ConnectionKind.OneDrive));
        Assert.True(StorageProviders.IsSupported(ConnectionKind.SharePoint));
        Assert.True(StorageProviders.IsSupported(ConnectionKind.Dropbox));
        Assert.True(StorageProviders.IsSupported(ConnectionKind.Box));
        Assert.IsAssignableFrom<IFolderBrowser>(StorageProviders.Find(ConnectionKind.Box));
    }

    internal static Dictionary<string, string> Query(Uri uri) => FormOf(uri.Query.TrimStart('?'));

    internal static Dictionary<string, string> FormOf(string text) =>
        text.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => pair.Length > 1 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : "");
}
