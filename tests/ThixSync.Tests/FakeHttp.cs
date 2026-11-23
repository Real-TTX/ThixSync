using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using ThixSync.Core.Infrastructure;
using ThixSync.Core.Models;
using ThixSync.Core.Storage;
using ThixSync.Core.Storage.Providers.Box;
using ThixSync.Core.Storage.Providers.Dropbox;
using ThixSync.Core.Storage.Providers.GoogleDrive;
using ThixSync.Core.Storage.Providers.OAuth;
using ThixSync.Core.Storage.Providers.OneDrive;

namespace ThixSync.Tests;

/// <summary>A request the fake cloud received (body read completely, headers flattened).</summary>
internal sealed record FakeRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, byte[] Body)
{
    public string Text => Encoding.UTF8.GetString(Body);

    /// <summary>The address with the query decoded ("…/files?q=name = 'x'").</summary>
    public string Url => Uri.UnescapeDataString(Uri.AbsoluteUri);

    public JsonNode? Json => JsonNode.Parse(Text);

    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>
/// A cloud API in memory for the provider tests: routes (first match wins) answer the requests, every request is recorded. No network,
/// no account.
/// </summary>
internal sealed class FakeHttp : HttpMessageHandler
{
    private readonly List<(Func<FakeRequest, bool> Matches, Func<FakeRequest, HttpResponseMessage> Respond)> _routes = [];
    private readonly List<FakeRequest> _requests = [];

    public IReadOnlyList<FakeRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    public FakeHttp On(Func<FakeRequest, bool> matches, Func<FakeRequest, HttpResponseMessage> respond)
    {
        lock (_routes)
        {
            _routes.Add((matches, respond));
        }

        return this;
    }

    public FakeHttp On(HttpMethod method, string urlPart, Func<FakeRequest, HttpResponseMessage> respond) =>
        On(request => request.Method == method && request.Url.Contains(urlPart, StringComparison.Ordinal), respond);

    public FakeHttp On(HttpMethod method, string urlPart, string json) => On(method, urlPart, _ => Json(json));

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Json(JsonNode json, HttpStatusCode status = HttpStatusCode.OK) => Json(json.ToJsonString(), status);

    public static HttpResponseMessage Status(HttpStatusCode status) => new(status) { Content = new ByteArrayContent([]) };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers)
        {
            headers[header.Key] = string.Join(", ", header.Value);
        }

        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers)
            {
                headers[header.Key] = string.Join(", ", header.Value);
            }
        }

        var recorded = new FakeRequest(request.Method, request.RequestUri!, headers, body);
        lock (_requests)
        {
            _requests.Add(recorded);
        }

        Func<FakeRequest, HttpResponseMessage>? respond;
        lock (_routes)
        {
            respond = _routes.FirstOrDefault(route => route.Matches(recorded)).Respond;
        }

        var response = respond?.Invoke(recorded) ?? Json("""{"error":"no route in the test"}""", HttpStatusCode.NotFound);
        response.RequestMessage = request;
        return response;
    }
}

/// <summary>
/// The frame of every cloud provider test: own data folder (<see cref="TestData"/>), app registrations in the settings, the fake cloud as
/// the network, short waits and the size limits of the uploads back to normal afterwards.
/// </summary>
internal sealed class CloudTest : IDisposable
{
    public const string GoogleClientId = "google-client";
    public const string MicrosoftClientId = "microsoft-client";
    public const string DropboxClientId = "dropbox-client";
    public const string BoxClientId = "box-client";

    private readonly TestData _data = new();
    private readonly Action<string> _originalBrowser = Core.Business.OAuthService.OpenBrowser;

    public CloudTest(bool answerRefreshes = true)
    {
        SettingsStore.Update(settings => settings with
        {
            OAuthClients = new OAuthClientSettings
            {
                GoogleClientId = GoogleClientId,
                GoogleClientSecret = "google-secret",
                MicrosoftClientId = MicrosoftClientId,
                DropboxClientId = DropboxClientId,
                BoxClientId = BoxClientId,
                BoxClientSecret = "box-secret",
            },
        });
        CloudHttp.HandlerForTests = Http;
        CloudApi.FirstBackoff = TimeSpan.FromMilliseconds(1);
        CloudApi.MaxInternalWait = TimeSpan.FromSeconds(5);
        BoxStorage.CommitWait = TimeSpan.FromMilliseconds(1);
        if (!answerRefreshes)
        {
            return;
        }

        var tokens = 0;
        Http.On(IsRefresh, _ => FakeHttp.Json($$"""{"access_token":"access-{{Interlocked.Increment(ref tokens)}}","expires_in":3600}"""));
    }

    public static bool IsRefresh(FakeRequest request) =>
        request.Method == HttpMethod.Post && request.Url.Contains("token", StringComparison.Ordinal) && request.Text.Contains("grant_type=refresh_token", StringComparison.Ordinal);

    public FakeHttp Http { get; } = new();

    public List<string> SavedSecrets { get; } = [];

    public StorageContext Context(ConnectionKind kind, string folder = "", Dictionary<string, string>? settings = null, string refreshToken = "refresh-1")
    {
        var clientId = kind switch
        {
            ConnectionKind.GoogleDrive => GoogleClientId,
            ConnectionKind.OneDrive or ConnectionKind.SharePoint => MicrosoftClientId,
            ConnectionKind.Dropbox => DropboxClientId,
            _ => BoxClientId,
        };
        return new StorageContext
        {
            Connection = new Connection { Id = Guid.NewGuid(), Name = "Test", Kind = kind, Settings = settings ?? new Dictionary<string, string>() },
            Folder = folder,
            Secret = new OAuthSecret(refreshToken, clientId, "anna@example.com").ToJson(),
            SaveSecretAsync = (secret, _) =>
            {
                lock (SavedSecrets)
                {
                    SavedSecrets.Add(secret);
                }

                return Task.CompletedTask;
            },
        };
    }

    /// <summary>Everything a listing yields, by path.</summary>
    public static async Task<Dictionary<string, StorageEntry>> ListAllAsync(IStorage storage, List<string>? problems = null)
    {
        var entries = new Dictionary<string, StorageEntry>(StringComparer.Ordinal);
        await foreach (var entry in storage.ListAsync("", (path, exception) => problems?.Add($"{path}: {exception.Message}"), CancellationToken.None))
        {
            entries.Add(entry.Path, entry);
        }

        return entries;
    }

    /// <summary>A file of <paramref name="length"/> bytes with a recognisable pattern.</summary>
    public static byte[] Bytes(int length) => [.. Enumerable.Range(0, length).Select(index => (byte)(index * 7 % 251))];

    public void Dispose()
    {
        CloudHttp.HandlerForTests = null;
        CloudApi.FirstBackoff = TimeSpan.FromSeconds(1);
        CloudApi.MaxInternalWait = TimeSpan.FromSeconds(30);
        BoxStorage.CommitWait = TimeSpan.FromSeconds(5);
        BoxStorage.SessionUploadLimit = 50L * 1024 * 1024;
        BoxStorage.BufferedUploadLimit = 8L * 1024 * 1024;
        GoogleDriveStorage.SimpleUploadLimit = 5L * 1024 * 1024;
        GoogleDriveStorage.ChunkSize = 8 * 1024 * 1024;
        GraphStorage.SimpleUploadLimit = 4L * 1024 * 1024;
        GraphStorage.ChunkSize = 32 * 320 * 1024;
        DropboxStorage.SimpleUploadLimit = 150L * 1024 * 1024;
        DropboxStorage.ChunkSize = 8 * 1024 * 1024;
        DropboxStorage.BufferedUploadLimit = 8L * 1024 * 1024;
        Core.Business.OAuthService.OpenBrowser = _originalBrowser;
        _data.Dispose();
    }
}

/// <summary>Collects reported progress (the engine gets byte counts as they go).</summary>
internal sealed class CountingProgress : IProgress<long>
{
    private long _total;

    public long Total => Interlocked.Read(ref _total);

    public void Report(long value) => Interlocked.Add(ref _total, value);
}

/// <summary>A header value of a response for the fake routes.</summary>
internal static class FakeHeaders
{
    public static HttpResponseMessage WithRetryAfter(this HttpResponseMessage response, int seconds)
    {
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
        return response;
    }
}
