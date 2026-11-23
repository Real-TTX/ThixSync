using System.Net.Sockets;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace ThixSync.Core.Storage.Providers.Sftp;

/// <summary>What the host key seen on connecting means.</summary>
internal enum HostKeyDecision
{
    /// <summary>The key is the one kept with the connection.</summary>
    Trusted = 0,

    /// <summary>No key is kept yet: trust on first use, keep this one.</summary>
    TrustAndRemember = 1,

    /// <summary>Another key than the kept one: maybe another server pretending to be this one – never connect.</summary>
    Mismatch = 2,
}

/// <summary>The sign-in of an SSH server and the trust in its host key (trust on first use, a change is refused).</summary>
internal sealed record SshSettings(string Host, int Port, string UserName, bool UseKeyFile, string KeyFile, string? Secret, string TrustedHostKey)
{
    /// <summary>The host key as ThixSync keeps it: "SHA256:" + the unpadded Base64 of the SHA-256 (as ssh shows it).</summary>
    public static string FormatHostKey(string sha256Base64) => "SHA256:" + sha256Base64.TrimEnd('=');

    public static HostKeyDecision Decide(string seen, string trusted)
    {
        if (string.IsNullOrWhiteSpace(trusted))
        {
            return HostKeyDecision.TrustAndRemember;
        }

        static string Bare(string key) => key.Trim().StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase) ? key.Trim()[7..].TrimEnd('=') : key.Trim().TrimEnd('=');
        return string.Equals(Bare(seen), Bare(trusted), StringComparison.Ordinal) ? HostKeyDecision.Trusted : HostKeyDecision.Mismatch;
    }
}

/// <summary>
/// Opens SSH sessions (SFTP and command channels) to one server with one shared trust: the first host key seen is kept with the
/// connection (<see cref="StorageContext.SaveSettingAsync"/>), every later session must show the same key. The key file is read once.
/// </summary>
internal sealed class SshEndpoint : IDisposable
{
    private readonly SshSettings _settings;
    private readonly Func<string, string, CancellationToken, Task> _saveSetting;
    private readonly Lock _gate = new();
    private string _trustedHostKey;
    private PrivateKeyFile? _keyFile;

    public SshEndpoint(SshSettings settings, Func<string, string, CancellationToken, Task> saveSetting)
    {
        _settings = settings;
        _saveSetting = saveSetting;
        _trustedHostKey = settings.TrustedHostKey;
    }

    public SshSettings Settings => _settings;

    /// <summary>A connected and signed-in SFTP session.</summary>
    public Task<SftpClient> ConnectSftpAsync(CancellationToken cancellationToken) =>
        ConnectAsync(info => new SftpClient(info) { OperationTimeout = TimeSpan.FromSeconds(120), BufferSize = 64 * 1024 }, cancellationToken);

    /// <summary>A connected and signed-in session for commands (rsync over SSH).</summary>
    public Task<SshClient> ConnectShellAsync(CancellationToken cancellationToken) =>
        ConnectAsync(info => new SshClient(info), cancellationToken);

    public void Dispose()
    {
        _keyFile?.Dispose();
    }

    private async Task<T> ConnectAsync<T>(Func<ConnectionInfo, T> create, CancellationToken cancellationToken) where T : BaseClient
    {
        var client = create(CreateConnectionInfo());
        client.KeepAliveInterval = TimeSpan.FromSeconds(30);
        string? seen = null;
        var decision = HostKeyDecision.Trusted;
        client.HostKeyReceived += (_, e) =>
        {
            seen = SshSettings.FormatHostKey(e.FingerPrintSHA256);
            string trusted;
            lock (_gate)
            {
                trusted = _trustedHostKey;
            }

            decision = SshSettings.Decide(seen, trusted);
            e.CanTrust = decision != HostKeyDecision.Mismatch;
        };

        try
        {
            await client.ConnectAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            client.Dispose();
            if (decision == HostKeyDecision.Mismatch)
            {
                throw new StorageException(StorageErrorKind.Authentication,
                    $"Der Server {_settings.Host} meldet sich mit einem anderen Fingerabdruck als bisher. Das kann ein fremder Server sein – ThixSync verbindet "
                    + "deshalb nicht. Wurde der Server neu eingerichtet, den gemerkten Fingerabdruck in der Verbindung zurücksetzen.", exception);
            }

            throw SshErrors.Map(exception, "", cancellationToken, connecting: true, _settings.Host);
        }

        if (decision == HostKeyDecision.TrustAndRemember && seen is not null)
        {
            var remember = false;
            lock (_gate)
            {
                if (_trustedHostKey.Length == 0)
                {
                    _trustedHostKey = seen;
                    remember = true;
                }
            }

            if (remember)
            {
                await _saveSetting(ConnectionFields.HostKey, seen, cancellationToken);
            }
        }

        return client;
    }

    private ConnectionInfo CreateConnectionInfo()
    {
        AuthenticationMethod[] methods;
        if (_settings.UseKeyFile)
        {
            methods = [new PrivateKeyAuthenticationMethod(_settings.UserName, LoadKeyFile())];
        }
        else
        {
            var password = _settings.Secret ?? "";
            var interactive = new KeyboardInteractiveAuthenticationMethod(_settings.UserName);
            interactive.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts)
                {
                    prompt.Response = password;
                }
            };
            methods = [new PasswordAuthenticationMethod(_settings.UserName, password), interactive];
        }

        return new ConnectionInfo(_settings.Host, _settings.Port, _settings.UserName, methods)
        {
            Timeout = TimeSpan.FromSeconds(30),
            RetryAttempts = 1,
        };
    }

    private PrivateKeyFile LoadKeyFile()
    {
        lock (_gate)
        {
            if (_keyFile is not null)
            {
                return _keyFile;
            }

            var path = ExpandHome(_settings.KeyFile);
            if (path.Length == 0 || !File.Exists(path))
            {
                throw new StorageException(StorageErrorKind.Authentication, "Die Schlüsseldatei der Verbindung gibt es auf diesem Computer nicht.");
            }

            try
            {
                _keyFile = string.IsNullOrEmpty(_settings.Secret) ? new PrivateKeyFile(path) : new PrivateKeyFile(path, _settings.Secret);
                return _keyFile;
            }
            catch (SshPassPhraseNullOrEmptyException exception)
            {
                throw new StorageException(StorageErrorKind.Authentication, "Die Schlüsseldatei ist mit einer Passphrase geschützt – bitte die Passphrase als Passwort eintragen.", exception);
            }
            catch (Exception exception) when (exception is SshException or InvalidOperationException or ArgumentException or FormatException or NotSupportedException
                or System.Security.Cryptography.CryptographicException)
            {
                throw new StorageException(StorageErrorKind.Authentication, "Die Schlüsseldatei konnte nicht gelesen werden (Format unbekannt oder Passphrase falsch).", exception);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new StorageException(StorageErrorKind.Authentication, "Die Schlüsseldatei lässt sich nicht öffnen.", exception);
            }
        }
    }

    /// <summary>"~/.ssh/id_ed25519" → the home folder of this user.</summary>
    internal static string ExpandHome(string path)
    {
        var trimmed = path.Trim();
        if (trimmed == "~" || trimmed.StartsWith("~/", StringComparison.Ordinal) || trimmed.StartsWith("~\\", StringComparison.Ordinal))
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), trimmed.Length > 2 ? trimmed[2..] : "");
        }

        return trimmed;
    }
}

/// <summary>Exceptions of SSH.NET as <see cref="StorageException"/> (German, the relative path only, never the password).</summary>
internal static class SshErrors
{
    public static Exception Map(Exception exception, string path, CancellationToken cancellationToken, bool connecting = false, string host = "")
    {
        var where = path.Length == 0 ? "" : $" ({path})";
        var server = host.Length > 0 ? host : "Der Server";
        return exception switch
        {
            StorageException => exception,
            OperationCanceledException when cancellationToken.IsCancellationRequested => exception,
            SshAuthenticationException => new StorageException(StorageErrorKind.Authentication,
                "Der SSH-Server hat die Anmeldung abgelehnt – stimmen Benutzername, Passwort oder Schlüssel?", exception),
            SftpPathNotFoundException => new StorageException(StorageErrorKind.NotFound, $"Nicht gefunden{where}.", exception),
            SftpPermissionDeniedException => new StorageException(StorageErrorKind.AccessDenied, $"Kein Zugriff{where}.", exception),
            SftpException { StatusCode: StatusCode.NoSuchFile } => new StorageException(StorageErrorKind.NotFound, $"Nicht gefunden{where}.", exception),
            SftpException { StatusCode: StatusCode.PermissionDenied } => new StorageException(StorageErrorKind.AccessDenied, $"Kein Zugriff{where}.", exception),
            SftpException { StatusCode: StatusCode.ConnectionLost or StatusCode.NoConnection } =>
                new StorageException(StorageErrorKind.Transient, $"Die Verbindung zum Server wurde unterbrochen{where}.", exception),
            SftpException => new StorageException(StorageErrorKind.Unknown, $"Der Server hat die Aktion abgelehnt: {exception.Message}{where}", exception),
            SshOperationTimeoutException or TimeoutException or OperationCanceledException => new StorageException(connecting ? StorageErrorKind.Unreachable : StorageErrorKind.Transient,
                $"{server} antwortet nicht rechtzeitig{where}.", exception),
            SocketException => new StorageException(StorageErrorKind.Unreachable, $"{server} ist nicht erreichbar.", exception),
            SshConnectionException or ObjectDisposedException => new StorageException(connecting ? StorageErrorKind.Unreachable : StorageErrorKind.Transient,
                connecting ? $"Die Verbindung zu {server} ist fehlgeschlagen." : $"Die Verbindung zum Server wurde unterbrochen{where}.", exception),
            SshException => new StorageException(StorageErrorKind.Unknown, $"SSH-Fehler: {exception.Message}{where}", exception),
            IOException => new StorageException(StorageErrorKind.Transient, $"Die Verbindung zum Server wurde unterbrochen{where}.", exception),
            _ => exception,
        };
    }

    /// <summary>True when the session cannot be used any more (it is not given back to the pool).</summary>
    public static bool BreaksSession(Exception exception) =>
        exception is SshConnectionException or SshOperationTimeoutException or ObjectDisposedException or SocketException or IOException or OperationCanceledException
            || exception is SftpException { StatusCode: StatusCode.ConnectionLost or StatusCode.NoConnection };
}
