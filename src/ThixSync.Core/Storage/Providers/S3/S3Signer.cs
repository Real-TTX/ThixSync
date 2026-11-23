using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ThixSync.Core.Storage.Providers.S3;

/// <summary>
/// AWS Signature Version 4 with the Authorization header, as S3 wants it (the path is encoded once, not twice like other AWS
/// services). Also signs for S3-compatible services (MinIO, Wasabi, Backblaze B2, Google Cloud Storage with HMAC keys).
/// </summary>
internal static class S3Signer
{
    public const string Algorithm = "AWS4-HMAC-SHA256";

    /// <summary>The payload hash of a body that is streamed over HTTPS without hashing it first.</summary>
    public const string UnsignedPayload = "UNSIGNED-PAYLOAD";

    /// <summary>The SHA-256 of nothing (requests without a body).</summary>
    public const string EmptyPayloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    /// <summary>
    /// The value of the Authorization header. <paramref name="headers"/> are every header that is signed (host, x-amz-date,
    /// x-amz-content-sha256 and the others the request carries), names in any case; <paramref name="canonicalPath"/> is the
    /// path exactly as it is sent (already encoded).
    /// </summary>
    public static string Authorization(string accessKeyId, string secretKey, string region, string service, string method, string canonicalPath,
        IReadOnlyList<KeyValuePair<string, string>> query, IReadOnlyList<KeyValuePair<string, string>> headers, string payloadHash, DateTime time)
    {
        var (canonicalRequest, signedHeaders) = CanonicalRequest(method, canonicalPath, query, headers, payloadHash);
        var scope = Scope(time, region, service);
        var stringToSign = StringToSign(time, scope, canonicalRequest);
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(SigningKey(secretKey, time, region, service), Encoding.UTF8.GetBytes(stringToSign)));
        return $"{Algorithm} Credential={accessKeyId}/{scope},SignedHeaders={signedHeaders},Signature={signature}";
    }

    /// <summary>The canonical request and the list of signed header names (lower case, ';'-separated).</summary>
    public static (string Request, string SignedHeaders) CanonicalRequest(string method, string canonicalPath, IReadOnlyList<KeyValuePair<string, string>> query,
        IReadOnlyList<KeyValuePair<string, string>> headers, string payloadHash)
    {
        var sortedHeaders = headers
            .Select(header => (Name: header.Key.ToLowerInvariant(), Value: TrimAll(header.Value)))
            .OrderBy(header => header.Name, StringComparer.Ordinal)
            .ToList();
        var signedHeaders = string.Join(';', sortedHeaders.Select(header => header.Name));
        var builder = new StringBuilder();
        builder.Append(method).Append('\n');
        builder.Append(canonicalPath.Length == 0 ? "/" : canonicalPath).Append('\n');
        builder.Append(CanonicalQuery(query)).Append('\n');
        foreach (var (name, value) in sortedHeaders)
        {
            builder.Append(name).Append(':').Append(value).Append('\n');
        }

        builder.Append('\n');
        builder.Append(signedHeaders).Append('\n');
        builder.Append(payloadHash);
        return (builder.ToString(), signedHeaders);
    }

    /// <summary>The query sorted by encoded name, then encoded value ("a=1&amp;lifecycle=").</summary>
    public static string CanonicalQuery(IReadOnlyList<KeyValuePair<string, string>> query) =>
        string.Join('&', query
            .Select(parameter => (Name: CloudHttp.UriEncode(parameter.Key, false), Value: CloudHttp.UriEncode(parameter.Value, false)))
            .OrderBy(parameter => parameter.Name, StringComparer.Ordinal)
            .ThenBy(parameter => parameter.Value, StringComparer.Ordinal)
            .Select(parameter => $"{parameter.Name}={parameter.Value}"));

    public static string StringToSign(DateTime time, string scope, string canonicalRequest) =>
        $"{Algorithm}\n{AmzDate(time)}\n{scope}\n{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)))}";

    public static string Scope(DateTime time, string region, string service) =>
        $"{time.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}/{region}/{service}/aws4_request";

    /// <summary>The time as x-amz-date wants it ("20130524T000000Z").</summary>
    public static string AmzDate(DateTime time) => time.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static byte[] SigningKey(string secretKey, DateTime time, string region, string service)
    {
        var dateKey = HMACSHA256.HashData(Encoding.UTF8.GetBytes("AWS4" + secretKey), Encoding.UTF8.GetBytes(time.ToString("yyyyMMdd", CultureInfo.InvariantCulture)));
        var regionKey = HMACSHA256.HashData(dateKey, Encoding.UTF8.GetBytes(region));
        var serviceKey = HMACSHA256.HashData(regionKey, Encoding.UTF8.GetBytes(service));
        return HMACSHA256.HashData(serviceKey, "aws4_request"u8);
    }

    /// <summary>Header values without leading/trailing blanks and with inner runs of blanks folded to one.</summary>
    private static string TrimAll(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.Contains("  ", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var builder = new StringBuilder(trimmed.Length);
        var lastWasSpace = false;
        foreach (var character in trimmed)
        {
            var isSpace = character == ' ';
            if (isSpace && lastWasSpace)
            {
                continue;
            }

            builder.Append(character);
            lastWasSpace = isSpace;
        }

        return builder.ToString();
    }
}
