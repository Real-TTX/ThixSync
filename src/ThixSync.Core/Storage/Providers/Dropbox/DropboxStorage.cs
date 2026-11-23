using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using ThixSync.Core.Models;
using ThixSync.Core.Storage.Providers.OAuth;

namespace ThixSync.Core.Storage.Providers.Dropbox;

/// <summary>
/// A folder of Dropbox (API v2). Dropbox works with paths (no id cache needed) and is case-insensitive. Listing: one recursive
/// list_folder pass with list_folder/continue; the cursor of that listing is also the change feed. Uploads: /files/upload up to 150 MiB
/// (mode overwrite, client_modified = the modification time), upload sessions in 8 MiB chunks above. Missing parent folders are created by
/// Dropbox itself. The arguments of the content endpoints travel in the Dropbox-API-Arg header, with every non-ASCII character escaped as
/// \uXXXX. Deleting moves to Dropbox's deleted files (restorable for 30 days or more). Hash: Dropbox content_hash ("dropbox:").
/// </summary>
internal sealed class DropboxStorage : IStorage, IChangeFeed
{
    private const string Api = "https://api.dropboxapi.com/2";
    private const string ContentApi = "https://content.dropboxapi.com/2";

    private readonly CloudApi _api;
    private readonly string _rootPath;

    public DropboxStorage(StorageContext context, OAuthApp app)
    {
        _rootPath = OAuthApp.RootOf(context);
        _api = new CloudApi("Dropbox", OAuthTokenSource.For(context, app), Capabilities.MaxParallelism, Describe);
        var account = context.Connection.Get(ConnectionFields.SignedInAs);
        var where = account.Length > 0 ? $"Dropbox ({account})" : "Dropbox";
        DisplayName = _rootPath.Length > 0 ? $"{where}/{_rootPath}" : where;
    }

    /// <summary>Files up to this size go in one /files/upload request (Dropbox allows 150 MiB; tests make it small).</summary>
    internal static long SimpleUploadLimit { get; set; } = 150L * 1024 * 1024;

    /// <summary>The chunk of an upload session (a multiple of 4 MiB, as Dropbox recommends).</summary>
    internal static int ChunkSize { get; set; } = 8 * 1024 * 1024;

    /// <summary>Files up to this size are read into memory first, so a failed attempt can be sent again.</summary>
    internal static long BufferedUploadLimit { get; set; } = 8L * 1024 * 1024;

    public ConnectionKind Kind => ConnectionKind.Dropbox;

    public StorageCapabilities Capabilities { get; } = new()
    {
        Features = StorageFeatures.SetModifiedTime | StorageFeatures.Move | StorageFeatures.RealFolders | StorageFeatures.ChangeFeed | StorageFeatures.CoarseTime,
        MaxParallelism = 8,
        HashKind = "dropbox",
        MaxFileSize = 350L * 1024 * 1024 * 1024,
        InvalidNameCharacters = "\\",
    };

    public string DisplayName { get; }

    public async Task ConnectAsync(bool createRoot, CancellationToken cancellationToken)
    {
        if (_rootPath.Length == 0)
        {
            return;
        }

        var root = await GetMetadataAsync(DropboxPath(""), cancellationToken);
        if (root is not null && CloudJson.Text(root, ".tag") == "folder")
        {
            return;
        }

        if (root is not null)
        {
            throw new StorageException(StorageErrorKind.NotFound, $"Dropbox: „{_rootPath}“ ist eine Datei, kein Ordner.");
        }

        if (!createRoot)
        {
            throw new StorageException(StorageErrorKind.NotFound, $"Den Ordner „{_rootPath}“ gibt es bei Dropbox nicht.");
        }

        await CreateFolderAsync("", cancellationToken);
    }

    public async IAsyncEnumerable<StorageEntry> ListAsync(string folder, Action<string, Exception>? onProblem, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var start = StoragePath.Normalize(folder);
        var body = new JsonObject
        {
            ["path"] = DropboxPath(start),
            ["recursive"] = true,
            ["include_deleted"] = false,
            ["include_non_downloadable_files"] = false,
            ["limit"] = 2000,
        };
        var page = await _api.SendJsonAsync(() => CloudApi.Json(HttpMethod.Post, $"{Api}/files/list_folder", body), cancellationToken, path: start);
        while (true)
        {
            foreach (var item in CloudJson.Items(page, "entries"))
            {
                if (ToEntry(item) is { } entry && entry.Path.Length > 0 && !string.Equals(entry.Path, start, StringComparison.OrdinalIgnoreCase))
                {
                    yield return entry;
                }
            }

            if (!CloudJson.Flag(page, "has_more"))
            {
                yield break;
            }

            var next = new JsonObject { ["cursor"] = CloudJson.Text(page, "cursor") };
            page = await _api.SendJsonAsync(() => CloudApi.Json(HttpMethod.Post, $"{Api}/files/list_folder/continue", next), cancellationToken, path: start);
        }
    }

    public async Task<StorageEntry?> GetAsync(string path, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(path);
        if (normalized.Length == 0 && _rootPath.Length == 0)
        {
            return new StorageEntry("", true, 0, DateTimeOffset.MinValue);
        }

        var metadata = await GetMetadataAsync(DropboxPath(normalized), cancellationToken);
        return metadata is null ? null : ToEntry(metadata, normalized);
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(path);
        var argument = Argument(new JsonObject { ["path"] = DropboxPath(normalized) });
        var response = await _api.SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{ContentApi}/files/download");
            request.Headers.TryAddWithoutValidation("Dropbox-API-Arg", argument);
            return request;
        }, cancellationToken, path: normalized);
        return await CloudStreams.OpenAsync(response, cancellationToken);
    }

    public async Task<StorageEntry> WriteAsync(string path, Stream content, long length, DateTimeOffset modified, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(path);
        if (normalized.Length == 0)
        {
            throw new StorageException(StorageErrorKind.InvalidName, "Dropbox: Eine Datei braucht einen Namen.");
        }

        var commit = new JsonObject
        {
            ["path"] = DropboxPath(normalized),
            ["mode"] = "overwrite",
            ["autorename"] = false,
            ["client_modified"] = CloudJson.Rfc3339Seconds(modified),
            ["mute"] = true,
            ["strict_conflict"] = false,
        };

        JsonNode metadata;
        if (length <= SimpleUploadLimit)
        {
            metadata = await UploadAsync(normalized, commit, content, length, progress, cancellationToken);
        }
        else
        {
            metadata = await UploadInSessionAsync(normalized, commit, content, length, progress, cancellationToken);
        }

        return ToEntry(metadata, normalized) ?? throw new StorageException(StorageErrorKind.Transient, $"Dropbox: Der Upload von „{normalized}“ gab keine Antwort.");
    }

    public async Task CreateFolderAsync(string path, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(path);
        if (normalized.Length == 0 && _rootPath.Length == 0)
        {
            return;
        }

        var body = new JsonObject { ["path"] = DropboxPath(normalized), ["autorename"] = false };
        try
        {
            await _api.SendJsonAsync(() => CloudApi.Json(HttpMethod.Post, $"{Api}/files/create_folder_v2", body), cancellationToken, path: normalized);
        }
        catch (StorageException exception) when (exception.Message.Contains("path/conflict/folder", StringComparison.Ordinal))
        {
            // there already
        }
    }

    public async Task DeleteAsync(string path, bool isFolder, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(path);
        if (normalized.Length == 0)
        {
            throw new StorageException(StorageErrorKind.AccessDenied, "Der Wurzelordner eines Auftrags wird nie gelöscht.");
        }

        var body = new JsonObject { ["path"] = DropboxPath(normalized) };
        try
        {
            await _api.SendJsonAsync(() => CloudApi.Json(HttpMethod.Post, $"{Api}/files/delete_v2", body), cancellationToken, path: normalized);
        }
        catch (StorageException exception) when (exception.Kind == StorageErrorKind.NotFound)
        {
        }
    }

    public async Task MoveAsync(string fromPath, string toPath, CancellationToken cancellationToken)
    {
        var from = StoragePath.Normalize(fromPath);
        var to = StoragePath.Normalize(toPath);
        var body = new JsonObject
        {
            ["from_path"] = DropboxPath(from),
            ["to_path"] = DropboxPath(to),
            ["autorename"] = false,
            ["allow_ownership_transfer"] = false,
        };
        try
        {
            await _api.SendJsonAsync(() => CloudApi.Json(HttpMethod.Post, $"{Api}/files/move_v2", body), cancellationToken, path: from);
        }
        catch (StorageException exception) when (exception.Message.Contains("to/conflict", StringComparison.Ordinal) && !string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
        {
            var existing = await GetMetadataAsync(DropboxPath(to), cancellationToken);
            if (existing is not null && CloudJson.Text(existing, ".tag") == "folder")
            {
                throw new StorageException(StorageErrorKind.Unknown, $"Dropbox: Am Ziel „{to}“ gibt es schon einen Ordner.");
            }

            await DeleteAsync(to, isFolder: false, cancellationToken);
            await _api.SendJsonAsync(() => CloudApi.Json(HttpMethod.Post, $"{Api}/files/move_v2", body), cancellationToken, path: from);
        }
    }

    public async Task<string> GetCursorAsync(CancellationToken cancellationToken)
    {
        var body = new JsonObject { ["path"] = DropboxPath(""), ["recursive"] = true, ["include_deleted"] = true };
        var answer = await _api.SendJsonAsync(() => CloudApi.Json(HttpMethod.Post, $"{Api}/files/list_folder/get_latest_cursor", body), cancellationToken);
        return CloudJson.Text(answer, "cursor");
    }

    public async Task<bool> HasChangesAsync(string cursor, CancellationToken cancellationToken)
    {
        var body = new JsonObject { ["cursor"] = cursor };
        try
        {
            var page = await _api.SendJsonAsync(() => CloudApi.Json(HttpMethod.Post, $"{Api}/files/list_folder/continue", body), cancellationToken);
            return CloudJson.Items(page, "entries").Any() || CloudJson.Flag(page, "has_more");
        }
        catch (StorageException exception) when (exception.Kind is StorageErrorKind.Unknown or StorageErrorKind.NotFound)
        {
            return true; // reset: the cursor is no longer valid
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>The folders directly below a folder (relative to the root of this storage), for the folder picker.</summary>
    public async Task<IReadOnlyList<string>> ListFoldersAsync(string folder, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(folder);
        var names = new List<string>();
        var body = new JsonObject { ["path"] = DropboxPath(normalized), ["recursive"] = false, ["limit"] = 2000 };
        var page = await _api.SendJsonAsync(() => CloudApi.Json(HttpMethod.Post, $"{Api}/files/list_folder", body), cancellationToken, path: normalized);
        while (true)
        {
            names.AddRange(CloudJson.Items(page, "entries").Where(item => CloudJson.Text(item, ".tag") == "folder").Select(item => CloudJson.Text(item, "name")));
            if (!CloudJson.Flag(page, "has_more"))
            {
                return [.. names.Order(StringComparer.CurrentCultureIgnoreCase)];
            }

            var next = new JsonObject { ["cursor"] = CloudJson.Text(page, "cursor") };
            page = await _api.SendJsonAsync(() => CloudApi.Json(HttpMethod.Post, $"{Api}/files/list_folder/continue", next), cancellationToken, path: normalized);
        }
    }

    /// <summary>The signed-in account (e-mail, else the name) for a fresh access token.</summary>
    internal static async Task<string> ReadAccountAsync(CloudApi api, CancellationToken cancellationToken)
    {
        var account = await api.SendJsonAsync(() => CloudApi.Json(HttpMethod.Post, $"{Api}/users/get_current_account", null), cancellationToken);
        var email = CloudJson.Text(account, "email");
        return email.Length > 0 ? email : CloudJson.Text(account["name"], "display_name");
    }

    /// <summary>What Dropbox's error answers mean ({"error_summary": "path/not_found/..", "error": {…}}).</summary>
    internal static CloudError Describe(HttpStatusCode status, JsonNode? body)
    {
        var summary = CloudJson.Text(body, "error_summary").TrimEnd('.', '/');
        if (summary.Length == 0 && body is JsonValue text && text.TryGetValue<string>(out var plain))
        {
            summary = plain;
        }

        StorageErrorKind? kind = null;
        if (summary.Contains("not_found", StringComparison.Ordinal))
        {
            kind = StorageErrorKind.NotFound;
        }
        else if (summary.Contains("insufficient_space", StringComparison.Ordinal) || summary.Contains("insufficient_quota", StringComparison.Ordinal))
        {
            kind = StorageErrorKind.QuotaExceeded;
        }
        else if (summary.Contains("too_many_write_operations", StringComparison.Ordinal) || summary.Contains("too_many_requests", StringComparison.Ordinal))
        {
            kind = StorageErrorKind.RateLimited;
        }
        else if (summary.Contains("no_write_permission", StringComparison.Ordinal) || summary.Contains("restricted_content", StringComparison.Ordinal))
        {
            kind = StorageErrorKind.AccessDenied;
        }
        else if (summary.Contains("disallowed_name", StringComparison.Ordinal) || summary.Contains("malformed_path", StringComparison.Ordinal))
        {
            kind = StorageErrorKind.InvalidName;
        }
        else if (summary.Contains("expired_access_token", StringComparison.Ordinal) || summary.Contains("invalid_access_token", StringComparison.Ordinal))
        {
            kind = StorageErrorKind.Authentication;
        }
        else if (summary.Contains("internal_error", StringComparison.Ordinal))
        {
            kind = StorageErrorKind.Transient;
        }

        return new CloudError(kind, summary);
    }

    /// <summary>The Dropbox-API-Arg header of an argument: JSON with every non-ASCII character as \uXXXX.</summary>
    internal static string Argument(JsonNode argument) => CloudJson.HeaderSafe(argument.ToJsonString());

    /// <summary>The Dropbox path of a path below the root ("" for the very top, else "/a/b").</summary>
    private string DropboxPath(string path)
    {
        var full = StoragePath.Combine(_rootPath, path);
        return full.Length == 0 ? "" : "/" + full;
    }

    /// <summary>The entry of a metadata object, its path relative to the root (null for a deleted entry or one outside the root).</summary>
    private StorageEntry? ToEntry(JsonNode metadata, string? knownPath = null)
    {
        var tag = CloudJson.Text(metadata, ".tag");
        if (tag.Length == 0 && knownPath is not null)
        {
            tag = "file"; // the answers of /files/upload and upload_session/finish are file metadata without a tag
        }

        if (tag is not ("file" or "folder"))
        {
            return null;
        }

        var path = knownPath ?? RelativePath(CloudJson.Text(metadata, "path_display"));
        if (path is null)
        {
            return null;
        }

        var name = CloudJson.Text(metadata, "name");
        if (tag == "folder")
        {
            return new StorageEntry(path, true, 0, DateTimeOffset.MinValue) { NativeId = CloudJson.Text(metadata, "id"), IsHidden = name.StartsWith('.') };
        }

        var hash = CloudJson.Text(metadata, "content_hash");
        return new StorageEntry(path, false, CloudJson.Number(metadata, "size"), CloudJson.Time(metadata, "client_modified"))
        {
            Hash = hash.Length > 0 ? $"dropbox:{hash}" : null,
            NativeId = CloudJson.Text(metadata, "id"),
            IsHidden = name.StartsWith('.'),
        };
    }

    private string? RelativePath(string displayPath)
    {
        var full = StoragePath.Normalize(displayPath);
        if (!StoragePath.IsInside(full, _rootPath, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return StoragePath.RelativeTo(full, _rootPath);
    }

    private async Task<JsonNode?> GetMetadataAsync(string dropboxPath, CancellationToken cancellationToken)
    {
        var body = new JsonObject { ["path"] = dropboxPath, ["include_deleted"] = false };
        try
        {
            return await _api.SendJsonAsync(() => CloudApi.Json(HttpMethod.Post, $"{Api}/files/get_metadata", body), cancellationToken);
        }
        catch (StorageException exception) when (exception.Kind == StorageErrorKind.NotFound)
        {
            return null;
        }
    }

    private async Task<JsonNode> UploadAsync(string path, JsonObject commit, Stream content, long length, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var argument = Argument(commit);
        HttpRequestMessage Build(HttpContent body)
        {
            body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            var request = new HttpRequestMessage(HttpMethod.Post, $"{ContentApi}/files/upload") { Content = body };
            request.Headers.TryAddWithoutValidation("Dropbox-API-Arg", argument);
            return request;
        }

        if (length <= BufferedUploadLimit)
        {
            var bytes = await CloudStreams.ReadSmallAsync(content, length, path, cancellationToken);
            var uploaded = await _api.SendJsonAsync(() => Build(new ByteArrayContent(bytes)), cancellationToken, path: path);
            progress?.Report(length);
            return uploaded;
        }

        return await _api.SendJsonAsync(() => Build(new ProgressContent(content, length, progress, path)), cancellationToken, replayable: false, path: path);
    }

    private async Task<JsonNode> UploadInSessionAsync(string path, JsonObject commit, Stream content, long length, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var started = await SendContentAsync("upload_session/start", new JsonObject { ["close"] = false }, [], 0, cancellationToken, path);
        var sessionId = CloudJson.Text(started, "session_id");
        var buffer = new byte[ChunkSize];
        var offset = 0L;
        while (offset < length)
        {
            var count = (int)Math.Min(ChunkSize, length - offset);
            await CloudStreams.ReadExactlyAsync(content, buffer, count, path, cancellationToken);
            var argument = new JsonObject
            {
                ["cursor"] = new JsonObject { ["session_id"] = sessionId, ["offset"] = offset },
                ["close"] = offset + count >= length,
            };
            try
            {
                await SendContentAsync("upload_session/append_v2", argument, buffer, count, cancellationToken, path);
            }
            catch (StorageException exception) when (exception.Message.Contains("incorrect_offset", StringComparison.Ordinal))
            {
                // An earlier attempt of this chunk arrived although its answer got lost: Dropbox has it already.
            }

            progress?.Report(count);
            offset += count;
        }

        var finish = new JsonObject
        {
            ["cursor"] = new JsonObject { ["session_id"] = sessionId, ["offset"] = length },
            ["commit"] = commit,
        };
        return await SendContentAsync("upload_session/finish", finish, [], 0, cancellationToken, path, replayable: false);
    }

    private Task<JsonNode> SendContentAsync(string endpoint, JsonObject argument, byte[] buffer, int count, CancellationToken cancellationToken, string path,
        bool replayable = true)
    {
        var header = Argument(argument);
        return _api.SendJsonAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{ContentApi}/files/{endpoint}")
            {
                Content = new ByteArrayContent(buffer, 0, count) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } },
            };
            request.Headers.TryAddWithoutValidation("Dropbox-API-Arg", header);
            return request;
        }, cancellationToken, replayable, path);
    }
}
