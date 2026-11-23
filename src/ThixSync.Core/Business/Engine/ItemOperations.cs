using System.Globalization;
using ThixSync.Core.Infrastructure;
using ThixSync.Core.Models;
using ThixSync.Core.Storage;
using ThixSync.Core.Storage.Providers.Local;

namespace ThixSync.Core.Business.Engine;

/// <summary>
/// Carries out one step of the plan (create a folder, move, copy, delete, resolve a conflict) with retries, keeps old versions,
/// records the state of the path and writes the protocol line. Errors are thrown as <see cref="StorageException"/>; the executor
/// decides whether the file is skipped or the run stops.
/// </summary>
internal sealed class ItemOperations(RunContext run)
{
    private readonly Transfers _transfers = new(run);

    public Task ExecuteAsync(WorkItem work, AdaptiveLimiter lane) => work.Item.Action switch
    {
        PlanAction.CreateFolderOnTarget => CreateFolderAsync(work, Side.Target, lane),
        PlanAction.CreateFolderOnSource => CreateFolderAsync(work, Side.Source, lane),
        PlanAction.MoveOnTarget => MoveAsync(work, Side.Target, lane),
        PlanAction.MoveOnSource => MoveAsync(work, Side.Source, lane),
        PlanAction.CopyToTarget => CopyAsync(work.Pair, Side.Source, work.Item, work.Item.Action, lane, "In das Ziel kopiert"),
        PlanAction.CopyToSource => CopyAsync(work.Pair, Side.Target, work.Item, work.Item.Action, lane, "In die Quelle kopiert"),
        PlanAction.DeleteOnTarget => DeleteAsync(work, Side.Target, lane),
        PlanAction.DeleteOnSource => DeleteAsync(work, Side.Source, lane),
        PlanAction.Conflict => ResolveAsync(work, lane),
        _ => Task.CompletedTask,
    };

    private async Task CreateFolderAsync(WorkItem work, Side side, AdaptiveLimiter lane)
    {
        var storage = work.Pair.Storage(side);
        await run.Retry.RunAsync(lane, token => storage.CreateFolderAsync(work.Item.Path, token), run.Token);
        run.State.Set(RunContext.FolderRow(work.Pair.Index, work.Item.Path));
        run.Counters.FolderCreated();
        run.Log.Info(work.Item.Action, work.Item.Path, 0, $"Ordner {EngineText.InSide(side)} angelegt");
    }

    private async Task MoveAsync(WorkItem work, Side side, AdaptiveLimiter lane)
    {
        var item = work.Item;
        var pair = work.Pair;
        var storage = pair.Storage(side);
        var listing = pair.Listing(side);
        var fromKey = item.FromPath ?? throw new StorageException(StorageErrorKind.Unknown, "Verschieben ohne Ausgangspfad.");
        if (!listing.TryGet(fromKey, out var old) || !pair.Listing(PairContext.Other(side)).TryGet(item.Path, out var moved))
        {
            throw new StorageException(StorageErrorKind.NotFound, "Die Datei ist nicht mehr da.");
        }

        await run.Retry.RunAsync(lane, token => storage.MoveAsync(old.Path, item.Path, token), run.Token);
        var now = await run.Retry.RunAsync(lane, token => storage.GetAsync(item.Path, token), run.Token) ?? old with { Path = item.Path };
        run.State.Remove(pair.Index, fromKey);
        run.State.Set(side == Side.Target
            ? RunContext.FileRow(pair.Index, item.Path, moved, now)
            : RunContext.FileRow(pair.Index, item.Path, now, moved));
        run.Counters.Moved();
        run.Log.Info(item.Action, item.Path, 0, $"{Capital(EngineText.InSide(side))} verschoben (vorher „{old.Path}“)");
    }

    /// <summary>Copies the file at the item's path from <paramref name="from"/> to the other side (old version kept, if wanted).</summary>
    private async Task CopyAsync(PairContext pair, Side from, PlanItem item, PlanAction action, AdaptiveLimiter lane, string message)
    {
        var key = item.Path;
        var to = PairContext.Other(from);
        if (!pair.Listing(from).TryGet(key, out var entry) || entry.IsFolder)
        {
            throw new StorageException(StorageErrorKind.NotFound, "Die Datei ist nicht mehr da.");
        }

        var hasExisting = pair.Listing(to).TryGet(key, out var existing) && !existing.IsFolder;
        var toPath = hasExisting ? existing.Path : entry.Path;
        var toStorage = pair.Storage(to);
        var direction = from == Side.Source ? PlanAction.CopyToTarget : PlanAction.CopyToSource;

        string? versionPath = null;
        StateRow? parked = null;
        try
        {
            if (hasExisting && run.KeepVersions)
            {
                // two-way: the place must never look "deleted" – copy the old version on this computer, or note it in the state first
                var twoWay = run.Settings.Mode == SyncMode.TwoWay;
                var preferCopy = twoWay && toStorage is LocalStorage;
                if (twoWay && !preferCopy && toStorage.Capabilities.Has(StorageFeatures.Move))
                {
                    parked = run.State.Park(pair.Index, key);
                }

                var (path, moved) = await run.Versions.KeepAsync(run, toStorage, existing, forDelete: false, preferCopy, lane);
                versionPath = moved ? path : null;
                run.VersionsWritten[(pair.Index, to)] = true;
            }

            var (source, written) = await _transfers.CopyAsync(pair.Storage(from), entry, toStorage, toPath, direction, lane);
            run.State.Set(from == Side.Source
                ? RunContext.FileRow(pair.Index, key, source, written)
                : RunContext.FileRow(pair.Index, key, written, source));
            run.Counters.Copied(source.Size);
            run.Log.Info(action, key, source.Size, message);
        }
        catch
        {
            if (versionPath is not null)
            {
                await RestoreAsync(toStorage, versionPath, existing.Path);
            }

            if (parked is not null)
            {
                run.State.Set(parked);
            }

            throw;
        }
    }

    private async Task DeleteAsync(WorkItem work, Side side, AdaptiveLimiter lane)
    {
        var item = work.Item;
        var pair = work.Pair;
        if (!pair.Listing(side).TryGet(item.Path, out var entry))
        {
            return;
        }

        if (entry.IsFolder)
        {
            await DeleteFolderAsync(work, side, entry, lane);
            return;
        }

        var storage = pair.Storage(side);
        if (run.KeepVersions)
        {
            await run.Versions.KeepAsync(run, storage, entry, forDelete: true, preferCopy: false, lane);
            run.VersionsWritten[(pair.Index, side)] = true;
        }
        else
        {
            await run.Retry.RunAsync(lane, token => storage.DeleteAsync(entry.Path, false, token), run.Token);
        }

        run.State.Remove(pair.Index, item.Path);
        run.Counters.Deleted(1);
        run.Log.Info(item.Action, item.Path, 0, $"{Capital(EngineText.InSide(side))} gelöscht{(run.KeepVersions ? " (als Version aufbewahrt)" : "")}");
    }

    /// <summary>
    /// Deletes a folder – only after a fresh look inside: every file still there must be one the plan deletes with it and unchanged
    /// since the scan (a folder deleted file by file must be empty by now). Otherwise the folder stays.
    /// </summary>
    private async Task DeleteFolderAsync(WorkItem work, Side side, StorageEntry entry, AdaptiveLimiter lane)
    {
        var item = work.Item;
        var pair = work.Pair;
        var storage = pair.Storage(side);
        var listing = pair.Listing(side);
        var plan = pair.FolderDeletes.GetValueOrDefault(item.Path) ?? new FolderDelete(side, false, [], [item.Path], 0);
        var current = await run.Retry.RunAsync(lane, token => storage.GetAsync(entry.Path, token), run.Token);
        if (current is null)
        {
            Forget(pair, plan);
            return;
        }

        var isSafe = await run.Retry.RunAsync(lane, async token =>
        {
            var problem = false;
            await foreach (var found in storage.ListAsync(entry.Path, (_, _) => problem = true, token))
            {
                if (found.IsFolder)
                {
                    continue;
                }

                var path = StoragePath.Normalize(found.Path);
                if (!plan.Recursive || !plan.Deletable.Contains(path) || !listing.TryGet(path, out var scanned)
                    || scanned.Size != found.Size || scanned.Modified != found.Modified)
                {
                    return false;
                }
            }

            return !problem;
        }, run.Token);

        if (!isSafe)
        {
            run.Log.Warning(item.Action, item.Path, $"Ordner {EngineText.InSide(side)} nicht gelöscht – er enthält Dateien, die nicht gelöscht werden sollen");
            return;
        }

        if (run.KeepVersions && storage.Capabilities.Has(StorageFeatures.Move))
        {
            await run.Versions.KeepAsync(run, storage, entry, forDelete: true, preferCopy: false, lane);
            run.VersionsWritten[(pair.Index, side)] = true;
        }
        else
        {
            await run.Retry.RunAsync(lane, token => storage.DeleteAsync(entry.Path, true, token), run.Token);
        }

        Forget(pair, plan);
        run.Counters.Deleted(Math.Max(1, plan.Files));
        var what = plan.Files > 0 ? $"Ordner mit {Format.Files(plan.Files)}" : "Ordner";
        run.Log.Info(item.Action, item.Path, 0, $"{what} {EngineText.InSide(side)} gelöscht{(run.KeepVersions ? " (als Version aufbewahrt)" : "")}");
    }

    private async Task ResolveAsync(WorkItem work, AdaptiveLimiter lane)
    {
        var item = work.Item;
        switch (item.Resolution)
        {
            case ConflictChoice.KeepSource:
                await CopyAsync(work.Pair, Side.Source, item, PlanAction.Conflict, lane, "Konflikt: die Version der Quelle wurde behalten");
                break;
            case ConflictChoice.KeepTarget:
                await CopyAsync(work.Pair, Side.Target, item, PlanAction.Conflict, lane, "Konflikt: die Version des Ziels wurde behalten");
                break;
            case ConflictChoice.KeepBoth:
                await KeepBothAsync(work.Pair, item, lane);
                break;
        }
    }

    /// <summary>
    /// Both versions stay: the target's file is renamed "name (Konflikt GERÄT yyyy-MM-dd HH-mm).ext" (moved on the target, or copied
    /// there), the source's file takes the name on the target, and the renamed copy goes to the source too.
    /// </summary>
    private async Task KeepBothAsync(PairContext pair, PlanItem item, AdaptiveLimiter lane)
    {
        var key = item.Path;
        if (!pair.SourceListing.TryGet(key, out var source) || !pair.TargetListing.TryGet(key, out var target))
        {
            throw new StorageException(StorageErrorKind.NotFound, "Die Datei ist nicht mehr da.");
        }

        var conflictPath = ConflictPath(pair, source.Path);
        var targetStorage = pair.Storage(Side.Target);
        var sourceStorage = pair.Storage(Side.Source);
        StorageEntry renamed;
        if (targetStorage.Capabilities.Has(StorageFeatures.Move))
        {
            await run.Retry.RunAsync(lane, token => targetStorage.MoveAsync(target.Path, conflictPath, token), run.Token);
            renamed = await run.Retry.RunAsync(lane, token => targetStorage.GetAsync(conflictPath, token), run.Token) ?? target with { Path = conflictPath };
        }
        else
        {
            (_, renamed) = await _transfers.CopyAsync(targetStorage, target, targetStorage, conflictPath, PlanAction.Conflict, lane);
        }

        var (sourceRead, targetWritten) = await _transfers.CopyAsync(sourceStorage, source, targetStorage, target.Path, PlanAction.CopyToTarget, lane);
        run.State.Set(RunContext.FileRow(pair.Index, key, sourceRead, targetWritten));
        var (renamedRead, sourceWritten) = await _transfers.CopyAsync(targetStorage, renamed, sourceStorage, conflictPath, PlanAction.CopyToSource, lane);
        run.State.Set(RunContext.FileRow(pair.Index, conflictPath, sourceWritten, renamedRead));
        run.Counters.Copied(sourceRead.Size);
        run.Counters.Copied(renamedRead.Size);
        run.Log.Info(PlanAction.Conflict, key, sourceRead.Size + renamedRead.Size,
            $"Konflikt: beide Versionen behalten – die Version des Ziels heißt jetzt „{StoragePath.NameOf(conflictPath)}“");
    }

    /// <summary>"name (Konflikt GERÄT 2026-11-23 10-24).ext", with a number when that name is taken.</summary>
    private string ConflictPath(PairContext pair, string path)
    {
        var parent = StoragePath.ParentOf(path);
        var name = StoragePath.NameOf(path);
        var dot = name.LastIndexOf('.');
        var (stem, extension) = dot > 0 ? (name[..dot], name[dot..]) : (name, "");
        var device = string.Concat(run.Settings.DeviceName.Select(character => "/\\:*?\"<>|".Contains(character) ? '-' : character));
        var stamp = run.Settings.RunStart.ToLocalTime().ToString("yyyy-MM-dd HH-mm", CultureInfo.InvariantCulture);
        for (var number = 1; ; number++)
        {
            var label = number == 1 ? $"{stem} (Konflikt {device} {stamp}){extension}" : $"{stem} (Konflikt {device} {stamp} {number}){extension}";
            var candidate = parent.Length == 0 ? label : $"{parent}/{label}";
            if (!pair.SourceListing.Entries.ContainsKey(candidate) && !pair.TargetListing.Entries.ContainsKey(candidate))
            {
                return candidate;
            }
        }
    }

    private void Forget(PairContext pair, FolderDelete plan)
    {
        foreach (var path in plan.Covered)
        {
            run.State.Remove(pair.Index, path);
        }
    }

    /// <summary>Puts a file back from the versions after its replacement failed (best effort, also when the run was cancelled).</summary>
    private static async Task RestoreAsync(IStorage storage, string versionPath, string path)
    {
        try
        {
            await storage.MoveAsync(versionPath, path, CancellationToken.None);
        }
        catch (Exception exception) when (exception is StorageException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Log.Warning($"A file could not be put back from the versions ({EngineText.ForLog(exception)})");
        }
    }

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
