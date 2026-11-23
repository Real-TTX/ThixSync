using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace ThixSync.Core.Storage.Providers.WebDav;

/// <summary>One &lt;response&gt; of a PROPFIND: the unescaped path of the href and the properties ThixSync reads.</summary>
internal sealed record WebDavItem(string Path, bool IsFolder, long Size, DateTimeOffset Modified)
{
    public string? ETag { get; init; }

    /// <summary>"sha1:…" from oc:checksums (Nextcloud/ownCloud), when the server keeps one.</summary>
    public string? Hash { get; init; }

    /// <summary>oc:fileid (Nextcloud/ownCloud).</summary>
    public string? FileId { get; init; }

    /// <summary>The last part of the path (the name).</summary>
    public string Name => Path[(Path.LastIndexOf('/') + 1)..];
}

/// <summary>
/// Reads the 207 Multi-Status answer of a PROPFIND as a stream (a folder with 100 000 files is never held as one document):
/// the href of each response (absolute URL or absolute path, percent-encoded – or not, as some servers do), resourcetype,
/// getcontentlength, getlastmodified, getetag and, from Nextcloud/ownCloud, oc:checksums and oc:fileid. Only the properties of
/// the propstat with status 200 count.
/// </summary>
internal static class WebDavMultistatus
{
    public const string DavNamespace = "DAV:";
    public const string OwnCloudNamespace = "http://owncloud.org/ns";

    private static readonly XNamespace Dav = DavNamespace;
    private static readonly XNamespace OwnCloud = OwnCloudNamespace;

    /// <summary>The body of a PROPFIND that asks for exactly what ThixSync needs.</summary>
    public static string PropfindBody(bool ownCloud)
    {
        var extra = ownCloud ? "<oc:checksums/><oc:fileid/>" : "";
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
            + $"<d:propfind xmlns:d=\"DAV:\" xmlns:oc=\"{OwnCloudNamespace}\"><d:prop>"
            + "<d:resourcetype/><d:getcontentlength/><d:getlastmodified/><d:getetag/>" + extra
            + "</d:prop></d:propfind>";
    }

    /// <summary>Every response of the answer, with the path of its href unescaped ("/remote.php/dav/files/anna/Fotos/a b.jpg").</summary>
    public static async Task<List<WebDavItem>> ParseAsync(Stream body, Uri requestUri, CancellationToken cancellationToken)
    {
        var items = new List<WebDavItem>();
        var settings = new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, IgnoreWhitespace = true };
        using var reader = XmlReader.Create(body, settings);
        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "response" || reader.NamespaceURI != DavNamespace)
            {
                continue;
            }

            using var subtree = reader.ReadSubtree();
            var response = await XElement.LoadAsync(subtree, LoadOptions.None, cancellationToken);
            if (ToItem(response, requestUri) is { } item)
            {
                items.Add(item);
            }
        }

        return items;
    }

    /// <summary>The unescaped path of an href ("/dav/a%20b/" → "/dav/a b"), without a trailing '/'; relative to <paramref name="requestUri"/>.</summary>
    public static string PathOfHref(string href, Uri requestUri)
    {
        // some servers send '#' or '?' unescaped inside names: they belong to the path here, never fragment or query
        var safe = href.Trim().Replace("#", "%23", StringComparison.Ordinal).Replace("?", "%3F", StringComparison.Ordinal);
        var absolute = Uri.TryCreate(requestUri, safe, out var resolved) ? resolved.AbsolutePath : safe;
        return Unescape(absolute).TrimEnd('/');
    }

    /// <summary>Percent-decoding as UTF-8 (a lone '%' stays as it is). The name stays as the server has it (NFC or NFD).</summary>
    public static string Unescape(string text) => Uri.UnescapeDataString(text);

    /// <summary>Whether two unescaped paths are the same (servers may answer in another Unicode normalization than asked).</summary>
    public static bool SamePath(string first, string second) =>
        string.Equals(first, second, StringComparison.Ordinal)
        || string.Equals(first.Normalize(NormalizationForm.FormC), second.Normalize(NormalizationForm.FormC), StringComparison.Ordinal);

    private static WebDavItem? ToItem(XElement response, Uri requestUri)
    {
        var href = response.Element(Dav + "href")?.Value;
        if (string.IsNullOrWhiteSpace(href))
        {
            return null;
        }

        var prop = response.Elements(Dav + "propstat")
            .Where(propstat => IsOk(propstat.Element(Dav + "status")?.Value))
            .Select(propstat => propstat.Element(Dav + "prop"))
            .FirstOrDefault(found => found is not null);

        // a response without a 200 propstat (some servers answer with a status only): just the href
        var isFolder = prop?.Element(Dav + "resourcetype")?.Element(Dav + "collection") is not null
            || (prop is null && href.TrimEnd().EndsWith('/'));
        long.TryParse(prop?.Element(Dav + "getcontentlength")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);
        var modified = ParseDate(prop?.Element(Dav + "getlastmodified")?.Value);
        var etag = prop?.Element(Dav + "getetag")?.Value is { Length: > 0 } tag ? tag.Trim('"') : null;
        var fileId = prop?.Element(OwnCloud + "fileid")?.Value is { Length: > 0 } id ? id : null;
        return new WebDavItem(PathOfHref(href, requestUri), isFolder, isFolder ? 0 : size, modified)
        {
            ETag = etag,
            FileId = fileId,
            Hash = Sha1Of(prop?.Element(OwnCloud + "checksums")),
        };
    }

    private static bool IsOk(string? status) =>
        status is null || status.Contains(" 200 ", StringComparison.Ordinal) || status.EndsWith(" 200", StringComparison.Ordinal);

    /// <summary>getlastmodified is RFC 1123 ("Tue, 14 Nov 2026 09:12:00 GMT"); some servers send ISO 8601.</summary>
    public static DateTimeOffset ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return DateTimeOffset.UnixEpoch;
        }

        if (DateTimeOffset.TryParseExact(text.Trim(), "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var rfc))
        {
            return rfc.ToUniversalTime();
        }

        return DateTimeOffset.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var other)
            ? other.ToUniversalTime()
            : DateTimeOffset.UnixEpoch;
    }

    /// <summary>"SHA1:abc MD5:def" in oc:checksum → "sha1:abc".</summary>
    private static string? Sha1Of(XElement? checksums)
    {
        if (checksums is null)
        {
            return null;
        }

        foreach (var part in checksums.Value.Split([' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith("SHA1:", StringComparison.OrdinalIgnoreCase) && part.Length > 5)
            {
                return "sha1:" + part[5..].ToLowerInvariant();
            }
        }

        return null;
    }
}
