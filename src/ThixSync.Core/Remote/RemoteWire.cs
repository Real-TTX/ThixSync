using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ThixSync.Core.Storage;

namespace ThixSync.Core.Remote;

/// <summary>The kinds of frames of ThixSync Remote (see <see cref="RemoteProtocol"/>).</summary>
internal enum RemoteFrameType : byte
{
    Request = 1,
    Response = 2,
    Error = 3,
    Data = 4,
    End = 5,
    Entries = 6,
    Problem = 7,
}

/// <summary>One frame as it was read; <see cref="Payload"/> lives in the buffer of the channel until the next read.</summary>
internal readonly record struct RemoteFrame(RemoteFrameType Type, ReadOnlyMemory<byte> Payload);

/// <summary>
/// The frames over one TLS stream: 1 byte type, 4 bytes length (big endian), payload. Not for two readers or two writers at the same
/// time (one request at a time per connection); one reader and one writer may run in parallel.
/// </summary>
internal sealed class RemoteChannel : IAsyncDisposable
{
    private const int HeaderBytes = 5;

    private readonly Stream _stream;
    private readonly byte[] _header = new byte[HeaderBytes];
    private byte[] _receive = new byte[64 * 1024];
    private readonly byte[] _send = new byte[HeaderBytes + RemoteProtocol.ChunkBytes];

    public RemoteChannel(Stream stream) => _stream = stream;

    /// <summary>Where the next piece of data goes before <see cref="WriteDataAsync"/> sends it (no copy).</summary>
    public Memory<byte> DataBuffer => _send.AsMemory(HeaderBytes, RemoteProtocol.ChunkBytes);

    public async Task<RemoteFrame> ReadAsync(CancellationToken cancellationToken)
    {
        await _stream.ReadExactlyAsync(_header, cancellationToken);
        var type = (RemoteFrameType)_header[0];
        var length = BinaryPrimitives.ReadInt32BigEndian(_header.AsSpan(1));
        if (length < 0 || length > RemoteProtocol.MaxFrameBytes || !Enum.IsDefined(type))
        {
            throw new InvalidDataException("The other side sent a broken frame.");
        }

        if (_receive.Length < length)
        {
            _receive = new byte[Math.Max(length, _receive.Length * 2)];
        }

        await _stream.ReadExactlyAsync(_receive.AsMemory(0, length), cancellationToken);
        return new RemoteFrame(type, _receive.AsMemory(0, length));
    }

    /// <summary>Reads one frame and its JSON; <see cref="RemoteFrameType.Error"/> becomes a <see cref="StorageException"/>.</summary>
    public async Task<T> ReadJsonAsync<T>(RemoteFrameType expected, JsonTypeInfo<T> info, CancellationToken cancellationToken)
    {
        var frame = await ReadAsync(cancellationToken);
        ThrowIfError(frame);
        if (frame.Type != expected)
        {
            throw new InvalidDataException($"Expected a frame {expected}, got {frame.Type}.");
        }

        return Parse(frame, info);
    }

    public Task WriteAsync(RemoteFrameType type, ReadOnlySpan<byte> payload, CancellationToken cancellationToken)
    {
        if (payload.Length > _send.Length - HeaderBytes)
        {
            return WriteLargeAsync(type, payload.ToArray(), cancellationToken);
        }

        payload.CopyTo(_send.AsSpan(HeaderBytes));
        return SendAsync(type, payload.Length, cancellationToken);
    }

    public Task WriteJsonAsync<T>(RemoteFrameType type, T value, JsonTypeInfo<T> info, CancellationToken cancellationToken) =>
        WriteAsync(type, JsonSerializer.SerializeToUtf8Bytes(value, info), cancellationToken);

    /// <summary>Sends the first <paramref name="length"/> bytes of <see cref="DataBuffer"/> as a data frame.</summary>
    public Task WriteDataAsync(int length, CancellationToken cancellationToken) => SendAsync(RemoteFrameType.Data, length, cancellationToken);

    public Task WriteErrorAsync(StorageErrorKind kind, string message, CancellationToken cancellationToken) =>
        WriteJsonAsync(RemoteFrameType.Error, new WireError((int)kind, message), RemoteJson.Default.WireError, cancellationToken);

    public static T Parse<T>(RemoteFrame frame, JsonTypeInfo<T> info) =>
        JsonSerializer.Deserialize(frame.Payload.Span, info) ?? throw new InvalidDataException("Empty JSON in a frame.");

    /// <summary>An error frame of the other side as the exception it stands for.</summary>
    public static void ThrowIfError(RemoteFrame frame)
    {
        if (frame.Type != RemoteFrameType.Error)
        {
            return;
        }

        var error = Parse(frame, RemoteJson.Default.WireError);
        var kind = Enum.IsDefined((StorageErrorKind)error.Kind) ? (StorageErrorKind)error.Kind : StorageErrorKind.Unknown;
        throw new RemoteRefusedException(kind, error.Message);
    }

    public ValueTask DisposeAsync() => _stream.DisposeAsync();

    private async Task SendAsync(RemoteFrameType type, int length, CancellationToken cancellationToken)
    {
        _send[0] = (byte)type;
        BinaryPrimitives.WriteInt32BigEndian(_send.AsSpan(1), length);
        await _stream.WriteAsync(_send.AsMemory(0, HeaderBytes + length), cancellationToken);
    }

    private async Task WriteLargeAsync(RemoteFrameType type, byte[] payload, CancellationToken cancellationToken)
    {
        if (payload.Length > RemoteProtocol.MaxFrameBytes)
        {
            throw new InvalidDataException("A frame would be too large.");
        }

        var header = new byte[HeaderBytes];
        header[0] = (byte)type;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(1), payload.Length);
        await _stream.WriteAsync(header, cancellationToken);
        await _stream.WriteAsync(payload, cancellationToken);
    }
}

/// <summary>The other side answered with an error frame: the connection itself is fine (it can take the next request).</summary>
internal sealed class RemoteRefusedException(StorageErrorKind kind, string message) : StorageException(kind, message);

/// <summary>A request of the client (see <see cref="RemoteProtocol"/>).</summary>
internal sealed record RemoteRequest
{
    public string Op { get; init; } = "";

    public string? Share { get; init; }

    public string? Path { get; init; }

    public string? To { get; init; }

    public long Length { get; init; }

    /// <summary>Unix milliseconds (UTC).</summary>
    public long Modified { get; init; }

    public bool IsFolder { get; init; }

    public bool Recursive { get; init; } = true;
}

/// <summary>The answer to a request (only the fields of that request are set).</summary>
internal sealed record RemoteReply
{
    public WireEntry? Entry { get; init; }

    public long Size { get; init; }

    public int Features { get; init; }

    public bool ReadOnly { get; init; }

    public IReadOnlyList<WireShare>? Shares { get; init; }
}

internal sealed record WireShare(string Name, bool ReadOnly);

/// <summary>An entry on the wire: path, folder, size, modified (Unix ms), hidden, system.</summary>
internal sealed record WireEntry(string P, bool F, long S, long M, bool H, bool Y)
{
    public static WireEntry From(StorageEntry entry) =>
        new(entry.Path, entry.IsFolder, entry.Size, entry.Modified.ToUnixTimeMilliseconds(), entry.IsHidden, entry.IsSystem);

    public StorageEntry ToEntry(string path) =>
        new(path, F, S, DateTimeOffset.FromUnixTimeMilliseconds(M)) { IsHidden = H, IsSystem = Y };
}

internal sealed record WireError(int Kind, string Message);

internal sealed record WireProblem(string Path, int Kind, string Message);

internal sealed record HelloMessage(int Version, string Device, string Platform, string Auth, string? KeyId, string Nonce);

internal sealed record HelloReply(int Version, string Device, string Nonce);

internal sealed record ProofMessage(string Proof);

internal sealed record AuthReply(string? KeyId = null, string? Key = null, string? Proof = null);

internal sealed record DiscoverMessage(string Thixsync, int Version, string? Device = null, int Port = 0, string? Fingerprint = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RemoteRequest))]
[JsonSerializable(typeof(RemoteReply))]
[JsonSerializable(typeof(List<WireEntry>))]
[JsonSerializable(typeof(WireError))]
[JsonSerializable(typeof(WireProblem))]
[JsonSerializable(typeof(HelloMessage))]
[JsonSerializable(typeof(HelloReply))]
[JsonSerializable(typeof(ProofMessage))]
[JsonSerializable(typeof(AuthReply))]
[JsonSerializable(typeof(DiscoverMessage))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class RemoteJson : JsonSerializerContext;
