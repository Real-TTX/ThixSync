namespace ThixSync.Core.Remote;

/// <summary>
/// ThixSync Remote: one computer with ThixSync offers folders (shares) to another one, PC to PC, encrypted (TLS) and paired with a
/// code. This class holds the constants of the protocol.
/// </summary>
public static class RemoteProtocol
{
    /// <summary>The TCP port a computer listens on by default (Einstellungen › Remote).</summary>
    public const int DefaultPort = 47600;
}
