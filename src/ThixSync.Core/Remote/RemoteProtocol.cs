namespace ThixSync.Core.Remote;

/// <summary>
/// ThixSync Remote: one computer with ThixSync offers folders (shares) to another one, PC to PC, encrypted (TLS) and paired with a
/// code. This class holds the constants of the protocol.
///
/// <para><b>Transport.</b> TCP (port <see cref="DefaultPort"/> unless the settings say otherwise), then TLS 1.2/1.3. The server shows a
/// self-signed certificate (ECDSA P-256, CN = its computer name); the client pins its SHA-256 fingerprint (the first connection learns
/// it, every later one must show the same).</para>
///
/// <para><b>Frames.</b> Everything after the TLS handshake is a frame: 1 byte <see cref="RemoteFrameType"/>, 4 bytes payload length
/// (big endian, at most <see cref="MaxFrameBytes"/>), the payload. Requests, answers, errors and entries are UTF-8 JSON; data is raw
/// bytes in pieces of up to <see cref="ChunkBytes"/>. One request at a time per connection: the client sends it and reads the answer
/// to the end before it sends the next one.</para>
///
/// <para><b>Authentication</b> (right after TLS; anything else first closes the connection):</para>
/// <list type="number">
/// <item>Client → <c>Request</c> Hello {version, device, platform, auth: "pair" | "key", keyId, nonce (32 random bytes)}.</item>
/// <item>Server → <c>Response</c> HelloReply {version, device, nonce (32 random bytes)} – or <c>Error</c> (Remote is off, too many
/// connections, another protocol version).</item>
/// <item>Client → <c>Request</c> Proof {proof}:
///   <list type="bullet">
///   <item>"key": HMAC-SHA256(device key, T("key", client nonce, server nonce, server fingerprint, keyId)).</item>
///   <item>"pair": HMAC-SHA256(PBKDF2-SHA256(the 8 digits of the pairing code, salt = client nonce ‖ server nonce,
///   <see cref="PairingIterations"/>), T("pair-client", client nonce, server nonce, server fingerprint, client device)). The fingerprint
///   is the one the client SAW in TLS: a man in the middle with another certificate makes the proof useless for the real server.</item>
///   </list>
/// T(…) is the concatenation of the parts, each with a 4-byte big-endian length in front.</item>
/// <item>Server checks in constant time → <c>Response</c> AuthReply {} for "key"; for "pair" {keyId, key (32 random bytes),
/// proof = HMAC-SHA256(the PBKDF2 key, T("pair-server", client nonce, server nonce, server fingerprint, keyId, key))} – the client
/// checks it and keeps "key:&lt;keyId&gt;:&lt;base64 key&gt;" as the secret of the connection. A wrong proof → <c>Error</c>
/// (Authentication) and the connection closes. A pairing code allows <see cref="PairingAttempts"/> tries, then it is void; it pairs one
/// computer and is valid for <see cref="PairingMinutes"/> minutes.</item>
/// </list>
///
/// <para><b>Requests</b> (<c>Request</c> frame, JSON <see cref="RemoteRequest"/>; paths are relative to the root of the share,
/// '/'-separated, checked by the server: no "..", no drive letters, nothing outside the share, no links on the way):</para>
/// <list type="bullet">
/// <item><c>share</c> {share} → Response {features, readOnly}: the share exists (features: <see cref="Storage.StorageFeatures"/> of
/// its folder on the server).</item>
/// <item><c>shares</c> → Response {shares: [{name, readOnly}]}.</item>
/// <item><c>list</c> {share, path, recursive} → <c>Entries</c> frames (JSON arrays of up to <see cref="ListBatch"/> entries),
/// <c>Problem</c> frames for unreadable folders, then <c>End</c> – or <c>Error</c>.</item>
/// <item><c>get</c> {share, path} → Response {entry | null}.</item>
/// <item><c>read</c> {share, path} → Response {size}, <c>Data</c> frames, <c>End</c> (or <c>Error</c> in between). A client that stops
/// early closes the connection.</item>
/// <item><c>write</c> {share, path, length, modified} → Response {} (ready) or <c>Error</c>; then the client sends <c>Data</c> frames
/// with exactly length bytes and <c>End</c>; the server writes to a temporary name and renames (LocalStorage) → Response {entry} or
/// <c>Error</c> (after it read the data up to <c>End</c>, so the connection stays usable).</item>
/// <item><c>mkdir</c> {share, path}, <c>delete</c> {share, path, isFolder}, <c>move</c> {share, path, to} → Response {} or
/// <c>Error</c>.</item>
/// </list>
/// Errors are JSON {kind (<see cref="Storage.StorageErrorKind"/>), message (German)}. A read-only share refuses write, mkdir, delete and
/// move. When Remote is switched off, the policy forbids it or the peer was removed, the server answers <c>Error</c> and closes.
///
/// <para><b>Discovery.</b> UDP on <see cref="DefaultPort"/>: a client broadcasts {"thixsync":"discover","version":1}; every server
/// with Remote on answers {"thixsync":"here","device","port","fingerprint"} to the sender.</para>
/// </summary>
public static class RemoteProtocol
{
    /// <summary>The TCP port a computer listens on by default (Einstellungen › Remote); also the UDP port of the discovery.</summary>
    public const int DefaultPort = 47600;

    /// <summary>The version of the protocol (Hello); a server refuses another one.</summary>
    public const int Version = 1;

    /// <summary>The largest payload of a frame.</summary>
    public const int MaxFrameBytes = 4 * 1024 * 1024;

    /// <summary>The size of one piece of file data.</summary>
    public const int ChunkBytes = 256 * 1024;

    /// <summary>Entries per <c>Entries</c> frame of a listing.</summary>
    public const int ListBatch = 1000;

    /// <summary>The PBKDF2 rounds that turn a pairing code into a key (slows guessing a code from a recorded proof down).</summary>
    public const int PairingIterations = 600_000;

    /// <summary>Wrong tries a pairing code allows before it is void.</summary>
    public const int PairingAttempts = 5;

    /// <summary>How long a pairing code is valid.</summary>
    public const int PairingMinutes = 10;

    /// <summary>Connections of one paired computer at the same time.</summary>
    public const int MaxConnectionsPerPeer = 16;

    /// <summary>Connections at the same time (all computers together, including those that did not sign in yet).</summary>
    public const int MaxConnections = 64;
}
