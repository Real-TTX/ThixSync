using ThixSync.Core.Models;
using ThixSync.Core.Storage.Providers.Local;

namespace ThixSync.Core.Storage.Providers.Smb;

/// <summary>
/// A share of the network that the operating system makes a folder (SMB, NFS): the share is connected or mounted when the storage
/// connects, then it is a <see cref="LocalStorage"/> (the engine copies with the operating system). The mount stays when the
/// storage is disposed – other jobs may use the same share.
/// </summary>
internal sealed class MountedShareStorage : LocalStorage
{
    private readonly Func<CancellationToken, Task<string>> _mount;

    /// <param name="root">The local path of the root (\\server\share\…, or below the mount point on macOS).</param>
    /// <param name="mount">Connects/mounts the share and returns the local path of the root as it is now.</param>
    public MountedShareStorage(string root, ConnectionKind kind, string displayName, Func<CancellationToken, Task<string>> mount)
        : base(root, kind, displayName)
    {
        _mount = mount;
    }

    public override async Task ConnectAsync(bool createRoot, CancellationToken cancellationToken)
    {
        var mounted = Path.GetFullPath(await _mount(cancellationToken));
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(mounted.TrimEnd('/', '\\'), Root.TrimEnd('/', '\\'), comparison))
        {
            // the share was mounted somewhere else in the meantime (Finder): the next attempt opens it there
            throw new StorageException(StorageErrorKind.Transient, "Die Freigabe wurde inzwischen an anderer Stelle verbunden – bitte erneut versuchen.");
        }

        await base.ConnectAsync(createRoot, cancellationToken);
    }
}
