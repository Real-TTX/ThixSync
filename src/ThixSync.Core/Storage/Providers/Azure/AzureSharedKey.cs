using System.Security.Cryptography;
using System.Text;

namespace ThixSync.Core.Storage.Providers.Azure;

/// <summary>
/// The Shared Key signature of Azure Storage (version 2009-09-19 and later): the string to sign from the verb, the standard
/// headers, the x-ms-* headers and the canonicalized resource ("/account/path" + the query parameters), HMAC-SHA256 with the
/// account key.
/// </summary>
internal static class AzureSharedKey
{
    private static readonly string[] StandardHeaders =
    [
        "Content-Encoding", "Content-Language", "Content-Length", "Content-MD5", "Content-Type", "Date",
        "If-Modified-Since", "If-Match", "If-None-Match", "If-Unmodified-Since", "Range",
    ];

    /// <summary>The value of the Authorization header ("SharedKey account:signature").</summary>
    public static string Authorization(string account, byte[] key, string stringToSign) =>
        $"SharedKey {account}:{Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(stringToSign)))}";

    /// <summary>
    /// The string to sign of a request. <paramref name="headers"/> are every header the request carries (names in any case);
    /// a Content-Length of 0 is signed as empty (since version 2015-02-21).
    /// </summary>
    public static string StringToSign(string method, string account, Uri uri, IReadOnlyList<KeyValuePair<string, string>> headers)
    {
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in headers)
        {
            byName[name] = value;
        }

        var builder = new StringBuilder();
        builder.Append(method.ToUpperInvariant()).Append('\n');
        foreach (var name in StandardHeaders)
        {
            var value = byName.GetValueOrDefault(name) ?? "";
            if (name == "Content-Length" && value == "0")
            {
                value = "";
            }

            builder.Append(value).Append('\n');
        }

        foreach (var (name, value) in byName
            .Where(header => header.Key.StartsWith("x-ms-", StringComparison.OrdinalIgnoreCase))
            .Select(header => (Name: header.Key.ToLowerInvariant(), Value: header.Value.Trim()))
            .OrderBy(header => header.Name, StringComparer.Ordinal))
        {
            builder.Append(name).Append(':').Append(value).Append('\n');
        }

        builder.Append(CanonicalizedResource(account, uri));
        return builder.ToString();
    }

    /// <summary>"/account/container/blob" (the path as sent) and "\nname:value" per query parameter, sorted, values decoded.</summary>
    public static string CanonicalizedResource(string account, Uri uri)
    {
        var builder = new StringBuilder();
        builder.Append('/').Append(account).Append(uri.AbsolutePath.Length == 0 ? "/" : uri.AbsolutePath);
        var parameters = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = part.IndexOf('=');
            var name = Uri.UnescapeDataString(index < 0 ? part : part[..index]).ToLowerInvariant();
            var value = index < 0 ? "" : Uri.UnescapeDataString(part[(index + 1)..]);
            if (!parameters.TryGetValue(name, out var values))
            {
                parameters[name] = values = [];
            }

            values.Add(value);
        }

        foreach (var (name, values) in parameters)
        {
            values.Sort(StringComparer.Ordinal);
            builder.Append('\n').Append(name).Append(':').Append(string.Join(',', values));
        }

        return builder.ToString();
    }
}
