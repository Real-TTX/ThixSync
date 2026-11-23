using ThixSync.Core.Infrastructure;
using ThixSync.Core.Models;

namespace ThixSync.Core.Business;

/// <summary>
/// Reading and changing the settings of this computer. Every change is checked here (values, policy) before it is
/// saved; the UI only offers. <see cref="Changed"/> tells the app (theme, scheduler, remote) what is new.
/// </summary>
public static class SettingsService
{
    /// <summary>Raised after every saved change (any thread).</summary>
    public static event EventHandler<AppSettings>? Changed;

    public static AppSettings Get() => SettingsStore.Read();

    /// <summary>Changes general settings (not the cloud connection: <see cref="CloudSyncService"/>; not Remote: <see cref="UpdateRemote"/>).</summary>
    public static AppSettings Update(Func<AppSettings, AppSettings> change)
    {
        AccessPolicy.EnsureConfigurationEditable();
        var current = SettingsStore.Read();
        var next = change(current) with { Cloud = current.Cloud, Remote = current.Remote };
        Validate(next);
        return Save(_ => next);
    }

    /// <summary>Changes the Remote settings (shares, port, on/off) after the checks of the business layer.</summary>
    public static AppSettings UpdateRemote(Func<RemoteSettings, RemoteSettings> change)
    {
        AccessPolicy.EnsureConfigurationEditable();
        var current = SettingsStore.Read();
        var remote = change(current.Remote);
        if (remote.IsEnabled)
        {
            AccessPolicy.EnsureRemoteAllowed();
        }

        if (remote.Port is < 1024 or > 65535)
        {
            throw new BusinessException("Der Port muss zwischen 1024 und 65535 liegen.", nameof(RemoteSettings.Port));
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var share in remote.Shares)
        {
            if (share.Name.Trim().Length == 0)
            {
                throw new BusinessException("Jede Freigabe braucht einen Namen.", nameof(RemoteShare.Name));
            }

            if (share.Name.IndexOfAny(['/', '\\', ':']) >= 0)
            {
                throw new BusinessException("Der Name einer Freigabe darf kein / \\ oder : enthalten.", nameof(RemoteShare.Name));
            }

            if (!names.Add(share.Name.Trim()))
            {
                throw new BusinessException($"Die Freigabe „{share.Name}“ gibt es doppelt.", nameof(RemoteShare.Name));
            }

            if (!Path.IsPathFullyQualified(share.LocalPath) || !Directory.Exists(share.LocalPath))
            {
                throw new BusinessException($"Der Ordner der Freigabe „{share.Name}“ gibt es auf diesem Computer nicht.", nameof(RemoteShare.LocalPath));
            }
        }

        return Save(settings => settings with
        {
            Remote = remote with { Shares = [.. remote.Shares.Select(share => share with { Name = share.Name.Trim() })] },
        });
    }

    /// <summary>Things the app remembers by itself (window size, folded menu, first start): no configuration lock.</summary>
    public static AppSettings Remember(Func<AppSettings, AppSettings> change)
    {
        var current = SettingsStore.Read();
        var next = change(current);
        return Save(_ => current with
        {
            WindowWidth = Math.Clamp(next.WindowWidth, 720, 4000),
            WindowHeight = Math.Clamp(next.WindowHeight, 480, 3000),
            IsSidebarCollapsed = next.IsSidebarCollapsed,
            FirstStartDone = next.FirstStartDone,
        });
    }

    /// <summary>The cloud part (only <see cref="CloudSyncService"/>).</summary>
    internal static AppSettings UpdateCloud(Func<CloudSettings, CloudSettings> change) =>
        Save(settings => settings with { Cloud = change(settings.Cloud) });

    /// <summary>The paired computers of Remote (only the Remote server: pairing, last seen).</summary>
    internal static AppSettings UpdatePeers(Func<IReadOnlyList<RemotePeer>, IReadOnlyList<RemotePeer>> change) =>
        Save(settings => settings with { Remote = settings.Remote with { Peers = change(settings.Remote.Peers) } });

    private static AppSettings Save(Func<AppSettings, AppSettings> change)
    {
        var saved = SettingsStore.Update(change);
        Changed?.Invoke(null, saved);
        return saved;
    }

    private static void Validate(AppSettings settings)
    {
        if (!AppSettings.KeepProtocolChoices.Contains(settings.KeepProtocolDays))
        {
            throw new BusinessException("Diese Aufbewahrungszeit gibt es nicht.", nameof(AppSettings.KeepProtocolDays));
        }

        if (settings.DefaultParallelism is < 0 or > JobService.MaxParallelism)
        {
            throw new BusinessException($"Gleichzeitige Übertragungen: 0 (automatisch) bis {JobService.MaxParallelism}.", nameof(AppSettings.DefaultParallelism));
        }

        if (settings.MaxConcurrentJobs is < 1 or > 16)
        {
            throw new BusinessException("Gleichzeitige Aufträge: 1 bis 16.", nameof(AppSettings.MaxConcurrentJobs));
        }

        if (settings.BandwidthLimitKiB is < 0 or > 10_000_000)
        {
            throw new BusinessException("Die Bandbreite muss 0 (unbegrenzt) oder positiv sein.", nameof(AppSettings.BandwidthLimitKiB));
        }

        if (settings.Retries is < 0 or > 10)
        {
            throw new BusinessException("Wiederholungen: 0 bis 10.", nameof(AppSettings.Retries));
        }

        if (settings.SmallFileThresholdKiB is < 0 or > 1024 * 1024)
        {
            throw new BusinessException("Die Grenze für kleine Dateien ist ungültig.", nameof(AppSettings.SmallFileThresholdKiB));
        }

        if (!Enum.IsDefined(settings.Theme) || !Enum.IsDefined(settings.Notify))
        {
            throw new BusinessException("Diese Auswahl gibt es nicht.");
        }
    }
}
