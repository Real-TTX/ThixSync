using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using FluentFTP;
using FluentFTP.Exceptions;
using ThixSync.Core.Models;
using ThixSync.Core.Platform;
using ThixSync.Core.Storage;
using ThixSync.Core.Storage.Providers.Ftp;
using ThixSync.Core.Storage.Providers.Local;
using ThixSync.Core.Storage.Providers.Nas;
using ThixSync.Core.Storage.Providers.Network;
using ThixSync.Core.Storage.Providers.Nfs;
using ThixSync.Core.Storage.Providers.Sftp;
using ThixSync.Core.Storage.Providers.Smb;
using ThixSync.Core.Storage.Providers.WebDav;

namespace ThixSync.Tests;

/// <summary>
/// The parts of the network and server providers that need no server: rsync's find output and shell quoting, the trust in host
/// keys and certificates, the paths of SMB/NFS shares, which storage Synology/QNAP/Hetzner open by protocol, FTP reply codes, the
/// parallel folder walk and the connection pool.
/// </summary>
public sealed class ServerProviderTests
{
    [Theory]
    [InlineData("plain")]
    [InlineData("mit Leerzeichen")]
    [InlineData("it's")]
    [InlineData("''")]
    [InlineData("$(rm -rf /) `id` $HOME ; | & > < * ? ~ !")]
    [InlineData("-rf")]
    [InlineData("zeile\nzwei\ttab \\ backslash \"doppelt\"")]
    [InlineData("Grüße/日本語")]
    public void Shell_quoting_keeps_every_name_as_one_literal_word(string name)
    {
        var quoted = RemoteShell.Quote(name);

        Assert.StartsWith("'", quoted, StringComparison.Ordinal);
        Assert.Equal(name, UnquotePosix(quoted));
        Assert.Throws<ArgumentException>(() => RemoteShell.Quote("nul\0"));
    }

    [Fact]
    public void The_remote_commands_quote_their_paths()
    {
        Assert.Equal(@"LC_ALL=C find '/backup/Anna'\''s Fotos' -printf '%y\t%s\t%T@\t%P\0'", RemoteShell.FindCommand("/backup/Anna's Fotos"));
        Assert.Equal("rm -rf -- '/backup/-x $(y)'", RemoteShell.RemoveTreeCommand("/backup/-x $(y)"));
    }

    [Fact]
    public void Find_output_becomes_entries()
    {
        var file = RemoteShell.ParseFindRecord("f\t1234\t1700000000.1234567890\tFotos/a\tb.jpg", "Start");
        Assert.Equal(new StorageEntry("Start/Fotos/a\tb.jpg", false, 1234, DateTimeOffset.FromUnixTimeSeconds(1_700_000_000).AddTicks(1_234_567)), file);

        var folder = RemoteShell.ParseFindRecord("d\t4096\t1700000000\t.versteckt", "");
        Assert.Equal(new StorageEntry(".versteckt", true, 0, DateTimeOffset.FromUnixTimeSeconds(1_700_000_000)) { IsHidden = true }, folder);

        Assert.Null(RemoteShell.ParseFindRecord("l\t10\t1700000000.0\tlink", ""));
        Assert.Null(RemoteShell.ParseFindRecord("p\t0\t1700000000.0\tpipe", ""));
        Assert.Null(RemoteShell.ParseFindRecord($"f\t1\t1700000000.0\tOrdner/{LocalStorage.TempPrefix}abc.tmp", ""));
        Assert.Null(RemoteShell.ParseFindRecord("kaputt", ""));

        // the start folder itself comes first: the proof that find ran (it is no entry)
        Assert.True(RemoteShell.IsStartRecord("d\t4096\t1700000000.0\t"));
        Assert.Null(RemoteShell.ParseFindRecord("d\t4096\t1700000000.0\t", ""));
        Assert.False(RemoteShell.IsStartRecord("d\t4096\t1700000000.0\tOrdner"));
        Assert.Equal(DateTimeOffset.UnixEpoch, RemoteShell.ParseTime("unsinn"));
    }

    [Fact]
    public void Find_records_are_cut_at_NUL_even_across_reads_and_inside_umlauts()
    {
        var bytes = Encoding.UTF8.GetBytes("f\t1\t1.0\tä.txt\0d\t0\t2.0\tÜber\0f\t3\t3.0\tre");
        var pending = new List<byte>();
        var records = new List<string>();
        foreach (var chunk in bytes.Chunk(5))
        {
            pending.AddRange(chunk);
            records.AddRange(RemoteShell.TakeRecords(pending));
        }

        Assert.Equal(["f\t1\t1.0\tä.txt", "d\t0\t2.0\tÜber"], records);
        Assert.Equal("f\t3\t3.0\tre", Encoding.UTF8.GetString([.. pending]));
    }

    [Fact]
    public void A_find_without_printf_is_recognised()
    {
        Assert.True(RemoteShell.FindLacksPrintf("find: -printf: unknown primary or operator"));
        Assert.True(RemoteShell.FindLacksPrintf("find: unrecognized: -printf"));
        Assert.False(RemoteShell.FindLacksPrintf("find: '/backup/privat': Permission denied"));
    }

    [Fact]
    public void Host_keys_are_trusted_on_first_use_and_then_must_stay()
    {
        var key = SshSettings.FormatHostKey("ohD8VZEXGWo6Ez8GSEJQ9WpafgLFsOfLOtGGQCQo6Og=");
        Assert.Equal("SHA256:ohD8VZEXGWo6Ez8GSEJQ9WpafgLFsOfLOtGGQCQo6Og", key);
        Assert.Equal(HostKeyDecision.TrustAndRemember, SshSettings.Decide(key, ""));
        Assert.Equal(HostKeyDecision.Trusted, SshSettings.Decide(key, "ohD8VZEXGWo6Ez8GSEJQ9WpafgLFsOfLOtGGQCQo6Og"));
        Assert.Equal(HostKeyDecision.Trusted, SshSettings.Decide(key, " SHA256:ohD8VZEXGWo6Ez8GSEJQ9WpafgLFsOfLOtGGQCQo6Og "));
        Assert.Equal(HostKeyDecision.Mismatch, SshSettings.Decide(key, "SHA256:OHD8VZEXGWo6Ez8GSEJQ9WpafgLFsOfLOtGGQCQo6Og"));
    }

    [Fact]
    public async Task Certificates_pass_when_valid_and_self_signed_ones_are_trusted_on_first_use()
    {
        using var first = SelfSigned("CN=nas");
        using var second = SelfSigned("CN=nas");
        var firstHash = CertificateTrust.HashOf(first);
        Assert.Equal(CertificateDecision.Accept, CertificateTrust.Decide(SslPolicyErrors.None, firstHash, "SHA256:ANDERS"));
        Assert.Equal(CertificateDecision.AcceptAndRemember, CertificateTrust.Decide(SslPolicyErrors.RemoteCertificateChainErrors, firstHash, ""));
        Assert.Equal(CertificateDecision.Accept, CertificateTrust.Decide(SslPolicyErrors.RemoteCertificateChainErrors, firstHash, firstHash.ToLowerInvariant()));
        Assert.Equal(CertificateDecision.Reject, CertificateTrust.Decide(SslPolicyErrors.RemoteCertificateChainErrors, CertificateTrust.HashOf(second), firstHash));

        var saved = new List<(string Key, string Value)>();
        var trust = new TlsTrust("", (key, value, _) =>
        {
            saved.Add((key, value));
            return Task.CompletedTask;
        });
        Assert.True(trust.Validate(first, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.True(trust.Validate(first, SslPolicyErrors.RemoteCertificateChainErrors));
        await trust.SaveLearnedAsync(CancellationToken.None);
        await trust.SaveLearnedAsync(CancellationToken.None);
        Assert.Equal([(ConnectionFields.CertificateHash, firstHash)], saved);
        Assert.False(trust.Validate(second, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.True(trust.Refused);
        Assert.Equal(StorageErrorKind.Authentication, TlsTrust.Mismatch("nas").Kind);
    }

    [Fact]
    public void The_sftp_root_is_absolute_or_below_home()
    {
        Assert.Equal("/backup/Fotos", SftpStorage.ResolveRoot("backup/Fotos", "/home/anna"));
        Assert.Equal("/home/anna/backup", SftpStorage.ResolveRoot("~/backup", "/home/anna"));
        Assert.Equal("/home/anna", SftpStorage.ResolveRoot("~", "/home/anna"));
        Assert.Equal("/", SftpStorage.ResolveRoot("", null));
        Assert.Equal("/backup/Fotos/2024", ServerPaths.Join("/backup/Fotos", "\\2024/"));
        Assert.Equal("/a", ServerPaths.Join("/", "a"));
    }

    [Fact]
    public void Smb_and_nfs_paths_are_built_from_the_fields()
    {
        var (address, sub) = SmbProvider.AddressOf(@"\\nas.local", "daten/Fotos", "2024/Mai");
        Assert.Equal(@"\\nas.local\daten", address.ShareUnc);
        Assert.Equal("Fotos/2024/Mai", sub);
        Assert.Equal(@"\\nas.local\daten\Fotos\2024\Mai", (address with { SubPath = sub }).Unc);
        Assert.Throws<StorageException>(() => SmbProvider.AddressOf("nas", "", ""));
        Assert.Equal(@"\\nas\volume1\daten\Fotos\2024", NetworkShares.NfsUnc("nas", "/volume1/daten/", "Fotos/2024"));
        Assert.Equal(@"\\nas\export", NetworkShares.NfsUnc("nas", "export", ""));

        var smb = new SmbProvider().Open(Context(ConnectionKind.Smb, new()
        {
            [ConnectionFields.Host] = "nas.local",
            [ConnectionFields.Share] = "daten",
            [ConnectionFields.UserName] = "anna",
            [ConnectionFields.Domain] = "FIRMA",
            [ConnectionFields.BasePath] = "/Backup",
        }, "Fotos"));
        Assert.IsAssignableFrom<ILocalStorage>(smb);
        Assert.Equal(ConnectionKind.Smb, smb.Kind);
        var nfs = new NfsProvider().Open(Context(ConnectionKind.Nfs, new() { [ConnectionFields.Host] = "nas", [ConnectionFields.Share] = "/volume1/daten" }, "Fotos"));
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(@"\\nas.local\daten\Backup\Fotos", ((LocalStorage)smb).Root);
            Assert.Equal(@"\\nas\volume1\daten\Fotos", ((LocalStorage)nfs).Root);
        }
    }

    [Theory]
    [InlineData(ConnectionKind.Synology, "", "https://ds.local:5006/")]
    [InlineData(ConnectionKind.Qnap, "", "https://ds.local:8081/")]
    [InlineData(ConnectionKind.Synology, "5443", "https://ds.local:5443/")]
    public void A_nas_with_webdav_opens_the_webdav_storage_on_its_port(ConnectionKind kind, string port, string address)
    {
        var storage = Assert.IsType<WebDavStorage>(new NasProvider().Open(Context(kind, new()
        {
            [ConnectionFields.Host] = "ds.local",
            [ConnectionFields.Protocol] = "webdav",
            [ConnectionFields.Port] = port,
            [ConnectionFields.UserName] = "anna",
            [ConnectionFields.BasePath] = "/home",
        }, "Fotos")));

        Assert.Equal(address, storage.BaseUri.AbsoluteUri);
        Assert.Equal("home/Fotos", storage.Options.Root);
        Assert.Equal(kind, storage.Kind);
        Assert.False(storage.Options.IsOwnCloud);
    }

    [Fact]
    public void A_nas_opens_sftp_or_smb_by_its_protocol()
    {
        var sftp = new NasProvider().Open(Context(ConnectionKind.Qnap, new()
        {
            [ConnectionFields.Host] = "qnap.local",
            [ConnectionFields.Protocol] = "sftp",
            [ConnectionFields.UserName] = "anna",
        }, "Public"));
        Assert.IsType<SftpStorage>(sftp);
        Assert.Equal(ConnectionKind.Qnap, sftp.Kind);
        Assert.Equal("sftp://anna@qnap.local/Public", sftp.DisplayName);

        var smb = new NasProvider().Open(Context(ConnectionKind.Synology, new()
        {
            [ConnectionFields.Host] = "ds.local",
            [ConnectionFields.Share] = "home",
            [ConnectionFields.UserName] = "anna",
        }, "Fotos"));
        Assert.IsAssignableFrom<LocalStorage>(smb);
        Assert.Equal(ConnectionKind.Synology, smb.Kind);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(@"\\ds.local\home\Fotos", ((LocalStorage)smb).Root);
        }
    }

    [Fact]
    public void A_hetzner_storage_box_uses_sftp_on_port_23_or_webdav()
    {
        var sftp = new HetznerProvider().Open(Context(ConnectionKind.HetznerStorageBox, new() { [ConnectionFields.UserName] = "u123456-sub1" }, "/backup"));
        Assert.IsType<SftpStorage>(sftp);
        Assert.Equal("sftp://u123456-sub1@u123456.your-storagebox.de:23/backup", sftp.DisplayName);

        var webDav = Assert.IsType<WebDavStorage>(new HetznerProvider().Open(Context(ConnectionKind.HetznerStorageBox, new()
        {
            [ConnectionFields.UserName] = "u123456",
            [ConnectionFields.Protocol] = "webdav",
        }, "backup")));
        Assert.Equal("https://u123456.your-storagebox.de/", webDav.BaseUri.AbsoluteUri);
    }

    [Fact]
    public void Every_network_type_has_its_provider()
    {
        Assert.IsType<SmbProvider>(StorageProviders.Find(ConnectionKind.Smb));
        Assert.IsType<NfsProvider>(StorageProviders.Find(ConnectionKind.Nfs));
        Assert.IsType<NasProvider>(StorageProviders.Find(ConnectionKind.Synology));
        Assert.IsType<NasProvider>(StorageProviders.Find(ConnectionKind.Qnap));
        Assert.IsType<SftpProvider>(StorageProviders.Find(ConnectionKind.Sftp));
        Assert.IsType<SftpProvider>(StorageProviders.Find(ConnectionKind.RsyncSsh));
        Assert.IsType<HetznerProvider>(StorageProviders.Find(ConnectionKind.HetznerStorageBox));
        Assert.IsType<FtpProvider>(StorageProviders.Find(ConnectionKind.Ftp));
        Assert.IsType<WebDavProvider>(StorageProviders.Find(ConnectionKind.WebDav));
        Assert.IsType<WebDavProvider>(StorageProviders.Find(ConnectionKind.Nextcloud));
        Assert.IsType<Core.Storage.Providers.Seafile.SeafileProvider>(StorageProviders.Find(ConnectionKind.Seafile));
        Assert.IsAssignableFrom<IStorage>(new SftpProvider().Open(Context(ConnectionKind.RsyncSsh, new() { [ConnectionFields.Host] = "h", [ConnectionFields.UserName] = "u" }, "")));
        Assert.Equal("ftps://anna@ftp.example.test/web", new FtpProvider().Open(Context(ConnectionKind.Ftp, new()
        {
            [ConnectionFields.Host] = "ftp.example.test",
            [ConnectionFields.UserName] = "anna",
        }, "web")).DisplayName);
    }

    [Theory]
    [InlineData("530", "Login incorrect.", StorageErrorKind.Authentication)]
    [InlineData("550", "No such file or directory", StorageErrorKind.NotFound)]
    [InlineData("550", "Permission denied", StorageErrorKind.AccessDenied)]
    [InlineData("552", "Quota exceeded", StorageErrorKind.QuotaExceeded)]
    [InlineData("553", "File name not allowed", StorageErrorKind.InvalidName)]
    [InlineData("421", "Too many connections", StorageErrorKind.Transient)]
    [InlineData("502", "Command not implemented", StorageErrorKind.Unknown)]
    public void Ftp_replies_become_error_kinds(string code, string message, StorageErrorKind kind)
    {
        Assert.Equal(kind, FtpStorage.MapReply(new FtpCommandException(code, message), "").Kind);
    }

    [Fact]
    public async Task The_folder_walk_skips_unreadable_folders_and_retries_short_problems()
    {
        var tree = new Dictionary<string, string[]>
        {
            [""] = ["a/", "b/", "c/", "x.txt"],
            ["a"] = ["a1.txt", "tief/"],
            ["a/tief"] = ["t.txt"],
            ["c"] = ["c1.txt"],
        };
        var attempts = 0;
        var problems = new List<string>();
        var entries = new List<string>();

        await foreach (var entry in FolderWalker.WalkAsync("", 4, (folder, _) =>
        {
            if (folder == "b")
            {
                throw new StorageException(StorageErrorKind.AccessDenied, "Kein Zugriff (b).");
            }

            if (folder == "c" && Interlocked.Increment(ref attempts) == 1)
            {
                throw new StorageException(StorageErrorKind.Transient, "kurz weg");
            }

            return Task.FromResult<IReadOnlyList<StorageEntry>>([.. tree[folder].Select(name => name.EndsWith('/')
                ? new StorageEntry(ServerPaths.Child(folder, name.TrimEnd('/')), true, 0, DateTimeOffset.UnixEpoch)
                : new StorageEntry(ServerPaths.Child(folder, name), false, 1, DateTimeOffset.UnixEpoch))]);
        }, (folder, _) => problems.Add(folder), CancellationToken.None))
        {
            entries.Add(entry.Path);
        }

        Assert.Equal(["a", "a/a1.txt", "a/tief", "a/tief/t.txt", "b", "c", "c/c1.txt", "x.txt"], entries.Order(StringComparer.Ordinal));
        Assert.Equal(["b"], problems);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task A_lost_sign_in_ends_the_folder_walk_with_the_error()
    {
        var walk = FolderWalker.WalkAsync("", 4, (folder, _) => folder.Length == 0
            ? Task.FromResult<IReadOnlyList<StorageEntry>>([new StorageEntry("a", true, 0, DateTimeOffset.UnixEpoch), new StorageEntry("b", true, 0, DateTimeOffset.UnixEpoch)])
            : throw new StorageException(StorageErrorKind.Authentication, "abgelehnt"), null, CancellationToken.None);

        var error = await Assert.ThrowsAsync<StorageException>(async () =>
        {
            await foreach (var _ in walk)
            {
            }
        });
        Assert.Equal(StorageErrorKind.Authentication, error.Kind);
    }

    [Fact]
    public async Task The_pool_limits_reuses_and_replaces_broken_connections()
    {
        var created = 0;
        var destroyed = 0;
        await using var pool = new ConnectionPool<object>(2, _ =>
        {
            Interlocked.Increment(ref created);
            return Task.FromResult(new object());
        }, _ => true, _ =>
        {
            Interlocked.Increment(ref destroyed);
            return ValueTask.CompletedTask;
        });

        var first = await pool.RentAsync(CancellationToken.None);
        var second = await pool.RentAsync(CancellationToken.None);
        var third = pool.RentAsync(CancellationToken.None);
        await Task.Delay(50);
        Assert.False(third.IsCompleted);

        var reused = first.Client;
        await first.DisposeAsync();
        Assert.Same(reused, (await third).Client);
        second.Break();
        await second.DisposeAsync();
        await (await third).DisposeAsync();
        var fresh = await pool.RentAsync(CancellationToken.None);
        await fresh.DisposeAsync();

        Assert.Equal(1, destroyed);
        Assert.Equal(2, created);
    }

    private static StorageContext Context(ConnectionKind kind, Dictionary<string, string> settings, string folder) => new()
    {
        Connection = new Connection { Name = kind.ToString(), Kind = kind, Settings = settings },
        Folder = folder,
        Secret = "geheim",
    };

    private static X509Certificate2 SelfSigned(string subject)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    /// <summary>How a POSIX shell reads one word: '…' literally, \x as x, everything else as it is (no spaces outside quotes).</summary>
    private static string UnquotePosix(string word)
    {
        var result = new StringBuilder();
        for (var index = 0; index < word.Length; index++)
        {
            var character = word[index];
            if (character == '\'')
            {
                var end = word.IndexOf('\'', index + 1);
                Assert.True(end > index, "an unclosed quote");
                result.Append(word, index + 1, end - index - 1);
                index = end;
                continue;
            }

            Assert.False(char.IsWhiteSpace(character) || "$`;|&<>*?\"".Contains(character), $"'{character}' outside quotes");
            if (character == '\\')
            {
                index++;
                character = word[index];
            }

            result.Append(character);
        }

        return result.ToString();
    }
}
