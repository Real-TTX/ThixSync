namespace ThixSync.Core.Models;

public enum ThemeChoice
{
    System = 0,
    Light = 1,
    Dark = 2,
}

/// <summary>When ThixSync tells about a finished run in a notification.</summary>
public enum NotifyChoice
{
    Never = 0,

    /// <summary>Only when something went wrong (errors, conflicts, safety stop).</summary>
    Problems = 1,

    /// <summary>After every run that copied or deleted something, and on problems.</summary>
    Changes = 2,
}

/// <summary>The settings of this computer (settings.json). Only <see cref="Business.SettingsService"/> writes them.</summary>
public sealed record AppSettings
{
    /// <summary>The choices of <see cref="KeepProtocolDays"/> (0 = for ever).</summary>
    public static readonly IReadOnlyList<int> KeepProtocolChoices = [7, 30, 90, 365, 0];

    public ThemeChoice Theme { get; init; } = ThemeChoice.System;

    // ---- Transfers ----

    /// <summary>Files copied at the same time by a job set to "automatic" (0: by the storages, see the engine).</summary>
    public int DefaultParallelism { get; init; }

    /// <summary>Jobs that run at the same time (the others wait).</summary>
    public int MaxConcurrentJobs { get; init; } = 2;

    /// <summary>Upload/download limit for all jobs together in KiB/s (0: none).</summary>
    public int BandwidthLimitKiB { get; init; }

    /// <summary>How often a failed file is tried again within a run.</summary>
    public int Retries { get; init; } = 3;

    /// <summary>Files up to this size go through the fast lane for small files (many at once, no progress per file).</summary>
    public int SmallFileThresholdKiB { get; init; } = 1024;

    /// <summary>Do not start runs by schedule while the computer runs on battery.</summary>
    public bool PauseOnBattery { get; init; }

    // ---- Notifications, protocol ----

    public NotifyChoice Notify { get; init; } = NotifyChoice.Problems;

    /// <summary>How long runs are kept in the protocol (0 = for ever).</summary>
    public int KeepProtocolDays { get; init; } = 90;

    // ---- ThixSync Remote ----

    public RemoteSettings Remote { get; init; } = new();

    // ---- Sign-in of the cloud storages (own app registrations; empty: the built-in ones) ----

    public OAuthClientSettings OAuthClients { get; init; } = new();

    // ---- Updates ----

    public bool CheckForUpdates { get; init; } = true;

    /// <summary>Full auto update: install new versions without asking (on by default, the setup offers it).</summary>
    public bool InstallUpdatesAutomatically { get; init; } = true;

    // ---- Remembered by the app ----

    /// <summary>Size of the main window (device-independent pixels).</summary>
    public double WindowWidth { get; init; } = 1280;

    public double WindowHeight { get; init; } = 820;

    /// <summary>The menu at the left of the main window is folded to its icons.</summary>
    public bool IsSidebarCollapsed { get; init; }

    /// <summary>"Dieser Computer" was set up (first start).</summary>
    public bool FirstStartDone { get; init; }

    public CloudSettings Cloud { get; init; } = new();
}

/// <summary>ThixSync Remote on this computer: whether it offers folders to other computers, on which port, which folders.</summary>
public sealed record RemoteSettings
{
    /// <summary>This computer accepts connections of other computers with ThixSync.</summary>
    public bool IsEnabled { get; init; }

    public int Port { get; init; } = Remote.RemoteProtocol.DefaultPort;

    /// <summary>The folders this computer offers (name → local folder).</summary>
    public IReadOnlyList<RemoteShare> Shares { get; init; } = [];

    /// <summary>Computers that paired with this one (they keep their key; removing one ends its access).</summary>
    public IReadOnlyList<RemotePeer> Peers { get; init; } = [];
}

/// <summary>A folder offered to other computers.</summary>
public sealed record RemoteShare
{
    public string Name { get; init; } = "";

    public string LocalPath { get; init; } = "";

    /// <summary>Other computers may only read (a backup source).</summary>
    public bool IsReadOnly { get; init; }
}

/// <summary>A computer that paired with this one.</summary>
public sealed record RemotePeer
{
    public string DeviceName { get; init; } = "";

    /// <summary>The key id (not the key: that is protected in the token store).</summary>
    public string KeyId { get; init; } = "";

    public DateTimeOffset PairedAt { get; init; }

    public DateTimeOffset? LastSeen { get; init; }
}

/// <summary>Own app registrations for the cloud storages (Einstellungen › Erweitert); empty values use the built-in ones.</summary>
public sealed record OAuthClientSettings
{
    public string GoogleClientId { get; init; } = "";

    public string GoogleClientSecret { get; init; } = "";

    public string MicrosoftClientId { get; init; } = "";

    public string DropboxClientId { get; init; } = "";

    public string BoxClientId { get; init; } = "";

    public string BoxClientSecret { get; init; } = "";
}

/// <summary>The connection to MatCloud. The device token is stored protected (DPAPI / Keychain), not in here.</summary>
public sealed record CloudSettings
{
    /// <summary>The address of the cloud, e.g. https://cloud.example.com. Empty = not connected.</summary>
    public string ServerUrl { get; init; } = "";

    public string UserName { get; init; } = "";

    public string DisplayName { get; init; } = "";

    public string DeviceName { get; init; } = "";

    public long DeviceId { get; init; }

    /// <summary>The highest revision of the account this computer has applied.</summary>
    public long LastRevision { get; init; }

    /// <summary>The device token, protected for this user (Windows DPAPI); on macOS it lives in the Keychain.</summary>
    public string ProtectedToken { get; init; } = "";

    public bool IsConnected => ServerUrl.Length > 0 && DeviceId > 0;
}
