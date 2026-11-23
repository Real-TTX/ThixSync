using System.Threading.Channels;
using ThixSync.Core.Infrastructure;
using ThixSync.Core.Models;
using ThixSync.Core.Storage;

namespace ThixSync.Core.Business.Engine;

/// <summary>
/// Carries out a plan with a pool of workers, phase by phase: folders to create (parents first, each level in parallel) → moves →
/// copies in two lanes at the same time (small files with all workers, so the count moves quickly; large files with a quarter of
/// them; each lane in path order) → deletions (files, then folders deepest first). A failed file is skipped with an error line; a
/// failure that concerns the whole run (sign-in, full, unreachable) stops it.
/// </summary>
internal sealed class Executor(RunContext run)
{
    private readonly ItemOperations _operations = new(run);

    public async Task ExecuteAsync(Analysis analysis)
    {
        var work = analysis.Plan.Items.Select(item => new WorkItem(analysis.PairOf(item), item)).ToList();
        ApplyQuietChanges(analysis, work);

        var todo = work.Where(IsWork).ToList();
        run.Meter.SetTotals(todo.Count, todo.Sum(item => item.Item.Bytes));
        run.Meter.Phase = RunPhase.Transferring;

        foreach (var level in todo.Where(item => item.Item.Action is PlanAction.CreateFolderOnTarget or PlanAction.CreateFolderOnSource)
                     .GroupBy(item => item.Depth).OrderBy(level => level.Key))
        {
            await ForEachAsync(level, run.SmallLane);
        }

        await ForEachAsync(todo.Where(item => item.Item.Action is PlanAction.MoveOnTarget or PlanAction.MoveOnSource), run.SmallLane);
        await CopyAsync(todo.Where(item => item.Item.Action is PlanAction.CopyToTarget or PlanAction.CopyToSource or PlanAction.Conflict));

        var deletes = todo.Where(item => item.Item.Action is PlanAction.DeleteOnTarget or PlanAction.DeleteOnSource).ToList();
        await ForEachAsync(deletes.Where(item => !item.Item.IsFolder), run.SmallLane);
        foreach (var level in deletes.Where(item => item.Item.IsFolder).GroupBy(item => item.Depth).OrderByDescending(level => level.Key))
        {
            await ForEachAsync(level, run.SmallLane);
        }
    }

    /// <summary>Steps that need a storage (not InStep, not open or skipped conflicts).</summary>
    private static bool IsWork(WorkItem work) => work.Item.Action switch
    {
        PlanAction.InStep => false,
        PlanAction.Conflict => work.Item.Resolution is ConflictChoice.KeepSource or ConflictChoice.KeepTarget or ConflictChoice.KeepBoth,
        _ => true,
    };

    /// <summary>The state of what needs no transfer (equal on both sides, folders, gone paths) and the lines of open conflicts.</summary>
    private void ApplyQuietChanges(Analysis analysis, List<WorkItem> work)
    {
        foreach (var row in analysis.QuietRows)
        {
            run.State.Set(row);
        }

        foreach (var (pairIndex, path) in analysis.DroppedRows)
        {
            run.State.Remove(pairIndex, path);
        }

        foreach (var item in work)
        {
            var step = item.Item;
            if (step.Action == PlanAction.InStep
                && item.Pair.SourceListing.TryGet(step.Path, out var source) && item.Pair.TargetListing.TryGet(step.Path, out var target))
            {
                run.State.Set(RunContext.FileRow(item.Pair.Index, step.Path, source, target));
                continue;
            }

            if (step.Action != PlanAction.Conflict)
            {
                continue;
            }

            run.Counters.Conflict();
            if (step.Resolution is null)
            {
                run.Log.Warning(PlanAction.Conflict, step.Path, $"Konflikt ({step.Reason}) – wartet auf Ihre Entscheidung");
            }
            else if (step.Resolution == ConflictChoice.Skip)
            {
                run.Log.Info(PlanAction.Conflict, step.Path, 0, "Konflikt übersprungen");
            }
        }
    }

    /// <summary>The two lanes, both at once; each worker takes the next file of its lane.</summary>
    private async Task CopyAsync(IEnumerable<WorkItem> copies)
    {
        var small = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions { SingleWriter = true });
        var large = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions { SingleWriter = true });
        int smallCount = 0, largeCount = 0;
        foreach (var item in Ordered(copies))
        {
            if (item.Item.Bytes > run.SmallFileBytes)
            {
                large.Writer.TryWrite(item);
                largeCount++;
            }
            else
            {
                small.Writer.TryWrite(item);
                smallCount++;
            }
        }

        small.Writer.Complete();
        large.Writer.Complete();
        var workers = new List<Task>();
        for (var index = 0; index < Math.Min(run.Degree, smallCount); index++)
        {
            workers.Add(Task.Run(() => DrainAsync(small.Reader, run.SmallLane)));
        }

        for (var index = 0; index < Math.Min(run.LargeDegree, largeCount); index++)
        {
            workers.Add(Task.Run(() => DrainAsync(large.Reader, run.LargeLane)));
        }

        await Task.WhenAll(workers);
    }

    private async Task DrainAsync(ChannelReader<WorkItem> reader, AdaptiveLimiter lane)
    {
        while (await reader.WaitToReadAsync(run.Token))
        {
            while (reader.TryRead(out var item))
            {
                run.Token.ThrowIfCancellationRequested();
                await RunItemAsync(item, lane);
            }
        }
    }

    private async Task ForEachAsync(IEnumerable<WorkItem> items, AdaptiveLimiter lane)
    {
        var ordered = Ordered(items);
        if (ordered.Count == 0)
        {
            return;
        }

        await Parallel.ForEachAsync(ordered, new ParallelOptions { MaxDegreeOfParallelism = run.Degree, CancellationToken = run.Token },
            async (item, _) => await RunItemAsync(item, lane));
    }

    /// <summary>
    /// The files of each folder in path order, the folders taken in turns: workers that add names to the same folder at the same time
    /// wait for each other (the file system locks the folder), so the next worker takes the next folder. With many small files this
    /// is about twice as fast as plain path order (measured on NTFS), and every folder is still read in order.
    /// </summary>
    private static List<WorkItem> Ordered(IEnumerable<WorkItem> items)
    {
        var sorted = items.OrderBy(item => item.Item.PairIndex).ThenBy(item => item.Item.Path, StringComparer.Ordinal).ToList();
        var turns = new Dictionary<(int PairIndex, string Folder), int>();
        var ranked = new (int Turn, int Position)[sorted.Count];
        for (var position = 0; position < sorted.Count; position++)
        {
            var folder = (sorted[position].Item.PairIndex, StoragePath.ParentOf(sorted[position].Item.Path));
            var turn = turns.GetValueOrDefault(folder);
            turns[folder] = turn + 1;
            ranked[position] = (turn, position);
        }

        return [.. ranked.OrderBy(rank => rank.Turn).ThenBy(rank => rank.Position).Select(rank => sorted[rank.Position])];
    }

    private async Task RunItemAsync(WorkItem work, AdaptiveLimiter lane)
    {
        try
        {
            await _operations.ExecuteAsync(work, lane);
        }
        catch (OperationCanceledException) when (run.Token.IsCancellationRequested)
        {
            throw;
        }
        catch (StorageException exception) when (exception.StopsRun || exception.Kind == StorageErrorKind.Unreachable)
        {
            run.Fail(exception);
            throw new OperationCanceledException(exception.Message, exception, run.Token);
        }
        catch (StorageException exception)
        {
            Failed(work, exception.Message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Log.Error("A step of a run failed unexpectedly", exception);
            Failed(work, exception.Message);
        }

        run.Meter.FileDone();
    }

    private void Failed(WorkItem work, string message)
    {
        run.Counters.Error();
        run.Meter.Error();
        run.Log.Error(work.Item.Action, work.Item.Path, $"Fehler: {message}");
    }
}
