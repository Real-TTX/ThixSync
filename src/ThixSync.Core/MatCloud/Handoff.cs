// MatCloud.Contract 1.7.0 – a copy from MatCloud, do not edit here
using System.Text.Json;

namespace MatCloud.Contract;

/// <summary>
/// The sign-in ThixLauncher leaves for an app it installs: the cloud, the poll token of a confirmed sign-in (<see cref="CloudContract.DeviceHandoff"/>)
/// and the account, for people.
/// </summary>
public sealed record DeviceHandoff(string ServerUrl, string PollToken, string UserName, DateTimeOffset ExpiresAt);

/// <summary>
/// The hand-over of a sign-in between the Thix apps of one computer and one user: ThixLauncher writes one file per app before it runs the app's setup
/// (<see cref="Write"/>); the app looks for it at its start while it is not connected (<see cref="Find"/>), fetches its token with
/// <see cref="MatCloudClient.RedeemHandoffAsync"/> and removes the file (<see cref="Remove"/>) – also when the hand-over had expired, but not when the
/// cloud could not be reached (then it tries again at the next start). The file holds a secret that works once and for a day: it lives in the
/// local application data of the user (on macOS and Linux readable only by the user).
/// </summary>
public static class CloudHandoff
{
    /// <summary>The folder of the hand-overs; tests set a folder of their own (and must, before anything else).</summary>
    public static string Folder { get; set; } = DefaultFolder();

    /// <summary>%LOCALAPPDATA%\Thix\MatCloud\handoff on Windows, ~/Library/Application Support/Thix/MatCloud/handoff on macOS.</summary>
    public static string DefaultFolder()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
            "Thix", "MatCloud", "handoff");

    /// <summary>Leaves the sign-in for <paramref name="app"/> (an id of <see cref="CloudApps"/>); an older one of the app is replaced.</summary>
    public static void Write(string app, DeviceHandoff handoff)
    {
        var file = PathOf(app);
        var directory = Directory.CreateDirectory(Folder);
        if (!OperatingSystem.IsWindows())
        {
            directory.UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        }

        var temporary = file + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(handoff, CloudContract.Json));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temporary, file, overwrite: true);
    }

    /// <summary>The sign-in left for <paramref name="app"/>, or null; one that expired or cannot be read is removed.</summary>
    public static DeviceHandoff? Find(string app)
    {
        var file = PathOf(app);
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            var handoff = JsonSerializer.Deserialize<DeviceHandoff>(File.ReadAllText(file), CloudContract.Json);
            if (handoff is { ServerUrl.Length: > 0, PollToken.Length: > 0 } && handoff.ExpiresAt > DateTimeOffset.UtcNow)
            {
                return handoff;
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // unreadable: removed below
        }

        Remove(app);
        return null;
    }

    /// <summary>Whether a sign-in waits for <paramref name="app"/> (without reading it).</summary>
    public static bool Exists(string app) => File.Exists(PathOf(app));

    /// <summary>Removes the sign-in left for <paramref name="app"/> (used, expired, or the app was removed).</summary>
    public static void Remove(string app)
    {
        try
        {
            File.Delete(PathOf(app));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // gone at the next start, or it expires
        }
    }

    private static string PathOf(string app)
    {
        if (!CloudApps.IsKnown(app))
        {
            throw new ArgumentException($"Unknown app '{app}'.", nameof(app));
        }

        return Path.Combine(Folder, app + ".json");
    }
}
