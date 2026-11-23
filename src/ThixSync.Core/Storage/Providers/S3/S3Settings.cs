using ThixSync.Core.Models;

namespace ThixSync.Core.Storage.Providers.S3;

/// <summary>
/// Where and how an S3-compatible bucket is reached: the service address (AWS, Wasabi, Backblaze B2, Google Cloud Storage or a
/// custom endpoint), virtual-hosted or path-style addressing, the region of the signature, the keys and the folder inside the
/// bucket every path is relative to.
/// </summary>
internal sealed record S3Settings
{
    public required ConnectionKind Kind { get; init; }

    /// <summary>The address of the service without bucket ("https://s3.eu-central-1.amazonaws.com", "http://localhost:9000").</summary>
    public required Uri ServiceUri { get; init; }

    public required string Bucket { get; init; }

    /// <summary>The region of the signature ("eu-central-1", "auto" for Google Cloud Storage).</summary>
    public required string Region { get; init; }

    /// <summary>The bucket is the first part of the path (MinIO, buckets with dots), not part of the host name.</summary>
    public bool PathStyle { get; init; }

    public required string AccessKeyId { get; init; }

    public required string SecretKey { get; init; }

    /// <summary>The x-amz-storage-class of new objects; null: the bucket's default.</summary>
    public string? StorageClass { get; init; }

    /// <summary>The service deletes many objects in one request (DeleteObjects); Google Cloud Storage does not.</summary>
    public bool BatchDelete { get; init; } = true;

    /// <summary>Objects above 5 GiB are copied in parts (UploadPartCopy); Google Cloud Storage copies any size at once.</summary>
    public bool MultipartCopy { get; init; } = true;

    /// <summary>AWS without an own endpoint: a bucket in another region is followed by changing the region.</summary>
    public bool RegionAdjustable { get; init; }

    /// <summary>The folder inside the bucket ("Backup/Fotos/"), with a trailing '/'; empty: the whole bucket.</summary>
    public string RootPrefix { get; init; } = "";

    public string DisplayName { get; init; } = "";

    public bool IsHttps => ServiceUri.Scheme == Uri.UriSchemeHttps;

    /// <summary>The settings of a connection, rooted at <paramref name="folder"/> below its base folder.</summary>
    public static S3Settings From(StorageContext context, string folder)
    {
        var connection = context.Connection;
        string Value(string key) => ConnectionCatalog.Value(connection, key).Trim();
        var bucket = Value(ConnectionFields.Bucket);
        if (bucket.Length == 0)
        {
            throw new StorageException(StorageErrorKind.Unknown, "Es ist kein Bucket angegeben.");
        }

        var keyId = Value(ConnectionFields.AccessKeyId);
        if (keyId.Length == 0 || string.IsNullOrEmpty(context.Secret))
        {
            throw new StorageException(StorageErrorKind.Authentication, "Schlüssel-ID oder geheimer Schlüssel fehlen.");
        }

        var root = StoragePath.Combine(Value(ConnectionFields.BasePath), folder);
        var settings = connection.Kind switch
        {
            ConnectionKind.Wasabi => Preset(connection.Kind, $"https://s3.{RegionOr(Value(ConnectionFields.Region), "us-east-1")}.wasabisys.com", bucket,
                RegionOr(Value(ConnectionFields.Region), "us-east-1"), keyId, context.Secret, "Wasabi"),
            ConnectionKind.BackblazeB2 => Preset(connection.Kind, $"https://s3.{RegionOr(Value(ConnectionFields.Region), "us-west-004")}.backblazeb2.com", bucket,
                RegionOr(Value(ConnectionFields.Region), "us-west-004"), keyId, context.Secret, "Backblaze B2"),
            ConnectionKind.GoogleCloudStorage => Preset(connection.Kind, "https://storage.googleapis.com", bucket, "auto", keyId, context.Secret, "gs") with
            {
                PathStyle = true,
                BatchDelete = false,
                MultipartCopy = false,
            },
            _ => ForS3(connection, bucket, keyId, context.Secret),
        };
        return settings with
        {
            RootPrefix = root.Length == 0 ? "" : root + "/",
            DisplayName = settings.DisplayName + (root.Length == 0 ? "" : "/" + root),
        };
    }

    /// <summary>The same bucket in another AWS region (the bucket told where it lives).</summary>
    public S3Settings WithRegion(string region) => this with { Region = region, ServiceUri = new Uri(AwsAddress(region)) };

    /// <summary>The URL of an object (key "" and <paramref name="bucketLevel"/>: the bucket) and the encoded path as it is signed.</summary>
    public (Uri Uri, string CanonicalPath) Address(string key, bool bucketLevel, IReadOnlyList<KeyValuePair<string, string>> query)
    {
        var basePath = ServiceUri.AbsolutePath.TrimEnd('/');
        var path = PathStyle
            ? $"{basePath}/{CloudHttp.UriEncode(Bucket, false)}/{(bucketLevel ? "" : CloudHttp.UriEncode(key, true))}"
            : $"{basePath}/{(bucketLevel ? "" : CloudHttp.UriEncode(key, true))}";
        var host = PathStyle ? ServiceUri.Authority : $"{Bucket}.{ServiceUri.Authority}";
        var queryText = query.Count == 0 ? "" : "?" + CloudHttp.Query(query);
        return (new Uri($"{ServiceUri.Scheme}://{host}{path}{queryText}"), path);
    }

    /// <summary>The value of x-amz-copy-source for an object of this bucket ("/bucket/a%20b.txt").</summary>
    public string CopySource(string key) => $"/{CloudHttp.UriEncode(Bucket, false)}/{CloudHttp.UriEncode(key, true)}";

    private static S3Settings ForS3(Connection connection, string bucket, string keyId, string secret)
    {
        var region = RegionOr(ConnectionCatalog.Value(connection, ConnectionFields.Region).Trim(), "us-east-1");
        var endpoint = connection.Get(ConnectionFields.Endpoint).Trim();
        var storageClass = ConnectionCatalog.Value(connection, ConnectionFields.StorageClass).Trim();
        storageClass = storageClass.Length == 0 || storageClass.Equals("STANDARD", StringComparison.OrdinalIgnoreCase) ? "" : storageClass.ToUpperInvariant();
        if (endpoint.Length == 0)
        {
            return new S3Settings
            {
                Kind = ConnectionKind.S3,
                ServiceUri = new Uri(AwsAddress(region)),
                Bucket = bucket,
                Region = region,

                // a bucket with dots does not match the TLS certificate of *.s3.<region>.amazonaws.com
                PathStyle = bucket.Contains('.'),
                AccessKeyId = keyId,
                SecretKey = secret,
                StorageClass = storageClass.Length == 0 ? null : storageClass,
                RegionAdjustable = true,
                DisplayName = $"s3://{bucket}",
            };
        }

        if (!endpoint.Contains("://", StringComparison.Ordinal))
        {
            endpoint = "https://" + endpoint;
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var serviceUri) || (serviceUri.Scheme != Uri.UriSchemeHttps && serviceUri.Scheme != Uri.UriSchemeHttp))
        {
            throw new StorageException(StorageErrorKind.Unknown, "Der Endpunkt ist keine gültige Adresse (https://…).");
        }

        return new S3Settings
        {
            Kind = ConnectionKind.S3,
            ServiceUri = serviceUri,
            Bucket = bucket,
            Region = region,
            PathStyle = bool.TryParse(connection.Get(ConnectionFields.PathStyle), out var pathStyle) && pathStyle,
            AccessKeyId = keyId,
            SecretKey = secret,
            StorageClass = storageClass.Length == 0 ? null : storageClass,
            DisplayName = $"s3://{serviceUri.Authority}/{bucket}",
        };
    }

    private static S3Settings Preset(ConnectionKind kind, string address, string bucket, string region, string keyId, string secret, string name) => new()
    {
        Kind = kind,
        ServiceUri = new Uri(address),
        Bucket = bucket,
        Region = region,
        PathStyle = bucket.Contains('.'),
        AccessKeyId = keyId,
        SecretKey = secret,
        DisplayName = name == "gs" ? $"gs://{bucket}" : $"{name}: {bucket}",
    };

    private static string AwsAddress(string region) =>
        region.StartsWith("cn-", StringComparison.Ordinal) ? $"https://s3.{region}.amazonaws.com.cn" : $"https://s3.{region}.amazonaws.com";

    private static string RegionOr(string region, string fallback) => region.Length == 0 ? fallback : region;
}
