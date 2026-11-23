using ThixSync.Core.Infrastructure;

namespace ThixSync.Core.Business;

/// <summary>The database of connections, jobs and the protocol as a whole: the app closes it when it ends (also the self test and the snapshots).</summary>
public static class Database
{
    /// <summary>Closes the database (it opens again on the next use).</summary>
    public static void Close() => SyncStore.Close();
}