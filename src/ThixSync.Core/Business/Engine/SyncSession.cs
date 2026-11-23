using ThixSync.Core.Infrastructure;
using ThixSync.Core.Models;
using ThixSync.Core.Storage;

namespace ThixSync.Core.Business.Engine;

/// <summary>
/// One run of a job from start to end: connect both sides of every pair, scan them, compare with the state, plan, stop for a
/// confirmation when the plan deletes too much, carry it out, prune old versions, and write the run, its protocol and the state.
/// Problems of the storages end the run as Failed (in the returned run and the protocol), never as an exception.
/// </summary>
internal sealed class SyncSession
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan StateFlushInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LogFlushInterval = TimeSpan.FromSeconds(2);
    private const int StateFlushRows = 2000;
    private const int LogFlushLines = 500;

    private readonly RunSlot _slot;
    private readonly SyncJob _job;
    private readonly RunRequest _request;
    private readonly TransferMeter _meter;
    private readonly RunLog _log;
    private readonly AppSettings _settings = SettingsService.Get();
    private readonly List<PairContext> _pairs = [];
    private JobStateStore? _store;
    private StateBook? _state;
    private RunContext? _context;
    private Analysis? _analysis;

    public SyncSession(RunSlot slot)
    {
        _slot = slot;
        _job = slot.Job;
        _request = slot.Request;
        _meter = slot.Meter;
        _log = new RunLog(slot.RunId);
    }

    private CancellationToken Token => _slot.Cancellation.Token;

    public async Task<SyncRun> RunAsync()
    {
        var run = new SyncRun
        {
            Id = _slot.RunId,
            JobId = _job.Id,
            JobName = _job.Name,
            Trigger = _request.Trigger,
            Outcome = RunOutcome.Running,
            StartedAt = AppClock.UtcNow,
            DeviceName = AppInfo.MachineName,
        };
        SyncStore.SaveRun(run);
        ProtocolService.Notify(run);
        JobRunner.OnRunStarted(run);

        using var tickerStop = new CancellationTokenSource();
        var ticker = TickAsync(tickerStop.Token);
        var outcome = RunOutcome.Failed;
        var summary = "";
        try
        {
            (outcome, summary) = await RunStepsAsync(run.StartedAt);
        }
        catch (OperationCanceledException) when (Token.IsCancellationRequested)
        {
            if (_context?.Failure is { } failure)
            {
                (outcome, summary) = Failed(failure.Message);
            }
            else
            {
                outcome = RunOutcome.Cancelled;
                summary = Summary(outcome);
            }
        }
        catch (StorageException exception)
        {
            (outcome, summary) = Failed(exception.Message);
        }
        catch (BusinessException exception)
        {
            (outcome, summary) = Failed(exception.Message);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log.Error($"Run of job {_job.Id:D} failed", exception);
            (outcome, summary) = Failed($"Unerwarteter Fehler: {exception.Message}");
        }
        finally
        {
            await tickerStop.CancelAsync();
            await ticker;
            await CloseAsync();
        }

        var counters = _context?.Counters;
        run = run with
        {
            Outcome = outcome,
            FinishedAt = AppClock.UtcNow,
            FilesCopied = counters?.FilesCopied ?? 0,
            FilesDeleted = counters?.FilesDeleted ?? 0,
            FilesMoved = counters?.FilesMoved ?? 0,
            FoldersCreated = counters?.FoldersCreated ?? 0,
            BytesCopied = counters?.BytesCopied ?? 0,
            Conflicts = _analysis?.Plan.Count(PlanAction.Conflict) ?? 0,
            Errors = counters?.Errors ?? 0,
            FilesScanned = _pairs.Sum(pair => pair.SourceListing.Files + pair.TargetListing.Files),
            Summary = summary,
        };
        _meter.Phase = RunPhase.Finishing;
        PublishProgress();
        SyncStore.SaveRun(run);
        ProtocolService.Notify(run);
        JobRunner.OnRunFinished(run);
        return run;
    }

    private async Task<(RunOutcome Outcome, string Summary)> RunStepsAsync(DateTimeOffset startedAt)
    {
        _meter.Phase = RunPhase.Connecting;
        var source = ConnectionService.Get(_job.SourceConnectionId);
        var target = ConnectionService.Get(_job.TargetConnectionId);
        AccessPolicy.EnsureConnectionKindAllowed(source.Kind);
        AccessPolicy.EnsureConnectionKindAllowed(target.Kind);
        _store = JobStateStore.Open(_job.Id);
        var metas = _store.LoadPairs();
        for (var index = 0; index < _job.Pairs.Count; index++)
        {
            var folders = _job.Pairs[index];
            var fingerprint = JobStateStore.FingerprintOf(source.Id, folders.SourcePath, target.Id, folders.TargetPath);
            var meta = metas.GetValueOrDefault(index);
            _pairs.Add(new PairContext
            {
                Index = index,
                Pair = folders,
                Fingerprint = fingerprint,
                Meta = meta,
                IsReset = meta is null || meta.Fingerprint != fingerprint,
            });
        }

        var warnings = new List<RunEntry>();
        await ConnectAsync(source, target, warnings);
        _state = new StateBook(_pairs, _store);
        _context = CreateContext(startedAt);

        _meter.Phase = RunPhase.Scanning;
        var filter = new PathFilter(_job.Filter, AppClock.UtcNow);
        await Parallel.ForEachAsync(_pairs.Where(pair => pair.IsUsable), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = Token },
            async (pair, token) =>
            {
                try
                {
                    await Scanner.ScanAsync(pair, filter, _meter, warnings, token);
                }
                catch (StorageException exception) when (!exception.StopsRun)
                {
                    PairFailed(pair, exception.Message, warnings);
                }
            });
        ThrowWhenNoPairIsUsable();

        _meter.Phase = RunPhase.Comparing;
        _analysis = await AnalyseAsync(startedAt, warnings);
        foreach (var warning in _analysis.Warnings)
        {
            _log.Add(warning);
        }

        if (_request.UseLastPlan)
        {
            CarryOverResolutions(_analysis);
        }

        var plan = _analysis.Plan;
        var hasWarnings = _analysis.Warnings.Count > 0;
        if (_request.AnalyseOnly)
        {
            JobRunner.KeepPlan(_job, plan);
            _log.Info(null, "", 0, AnalysisText(plan));
            return (RunOutcome.Analysed, AnalysisText(plan));
        }

        if (SafetyStop(plan) is { } stop)
        {
            JobRunner.KeepPlan(_job, plan);
            _log.Warning(null, "", stop);
            return (RunOutcome.NeedsConfirmation, stop);
        }

        PrepareState();
        await new Executor(_context).ExecuteAsync(_analysis);
        _meter.Phase = RunPhase.Finishing;
        _log.Info(null, "", 0, $"Bis zu {_meter.PeakWorkers} Vorgänge gleichzeitig ({_context.Degree} Arbeiter für kleine, {_context.LargeDegree} für große Dateien)");
        await PruneVersionsAsync();
        SaveCursors();

        if (plan.OpenConflicts > 0)
        {
            JobRunner.KeepPlan(_job, plan);
        }
        else
        {
            JobRunner.DropPlan(_job.Id);
        }

        var outcome = _context.Counters.Errors > 0 || plan.OpenConflicts > 0 || hasWarnings ? RunOutcome.Warnings : RunOutcome.Success;
        return (outcome, Summary(outcome));
    }

    // ---- Connect and analyse ---------------------------------------------------------------------------------------

    private async Task ConnectAsync(Connection source, Connection target, List<RunEntry> warnings)
    {
        var connect = new RetryRunner(Math.Max(1, _settings.Retries), _meter);
        var limiter = new AdaptiveLimiter(8);
        await Parallel.ForEachAsync(_pairs, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = Token }, async (pair, token) =>
        {
            try
            {
                var sourceStorage = ConnectionService.Open(source, pair.Pair.SourcePath);
                pair.Source = sourceStorage;
                var targetStorage = ConnectionService.Open(target, pair.Pair.TargetPath);
                pair.Target = targetStorage;

                // a missing root is only created where that can never look like "everything was deleted"
                var hasState = !pair.IsReset;
                var createSource = !hasState && _job.Mode == SyncMode.TwoWay;
                var createTarget = !hasState || _job.Mode != SyncMode.TwoWay;
                await Task.WhenAll(
                    connect.RunAsync(limiter, innerToken => sourceStorage.ConnectAsync(createSource, innerToken), token),
                    connect.RunAsync(limiter, innerToken => targetStorage.ConnectAsync(createTarget, innerToken), token));

                var caseSensitive = sourceStorage.Capabilities.Has(StorageFeatures.CaseSensitive) && targetStorage.Capabilities.Has(StorageFeatures.CaseSensitive);
                var comparer = caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
                pair.Comparer = comparer;
                pair.SourceListing = new SideListing(comparer);
                pair.TargetListing = new SideListing(comparer);
                pair.Collisions = new HashSet<string>(comparer);
                pair.FolderDeletes = new Dictionary<string, FolderDelete>(comparer);
                pair.State = new Dictionary<string, StateRow>(comparer);
                if (!pair.IsReset)
                {
                    foreach (var row in _store!.LoadRows(pair.Index))
                    {
                        pair.State.TryAdd(row.Path, row);
                    }
                }
            }
            catch (StorageException exception) when (!exception.StopsRun)
            {
                PairFailed(pair, exception.Message, warnings);
            }
            catch (BusinessException exception)
            {
                PairFailed(pair, exception.Message, warnings);
            }
        });

        ThrowWhenNoPairIsUsable();
    }

    private async Task<Analysis> AnalyseAsync(DateTimeOffset startedAt, List<RunEntry> warnings)
    {
        var analysis = new Analysis { Plan = new SyncPlan(), Pairs = _pairs };
        analysis.Warnings.AddRange(warnings);
        var items = new List<PlanItem>();
        var settings = _context!.Settings;
        foreach (var pair in _pairs.Where(pair => pair.IsUsable))
        {
            var planner = new Planner(pair, settings, analysis);
            items.AddRange(await planner.PlanAsync(
                (current, first, second, token) => Transfers.HaveSameContentAsync(current.Storage(Side.Source), first, current.Storage(Side.Target), second, token),
                _context.Degree, Token));
        }

        analysis.Plan = new SyncPlan
        {
            JobId = _job.Id,
            CreatedAt = startedAt,
            Items = [.. items.OrderBy(item => item.PairIndex).ThenBy(item => item.Path, StringComparer.Ordinal)],
            SourceFiles = _pairs.Sum(pair => pair.SourceListing.Files),
            TargetFiles = _pairs.Sum(pair => pair.TargetListing.Files),
            SourceBytes = _pairs.Sum(pair => pair.SourceListing.Bytes),
            TargetBytes = _pairs.Sum(pair => pair.TargetListing.Bytes),
            Skipped = _pairs.Sum(pair => pair.Skipped + pair.Collisions.Count),
        };
        return analysis;
    }

    /// <summary>A kept plan's choices for open conflicts are used again when the files are still the same.</summary>
    private void CarryOverResolutions(Analysis analysis)
    {
        var kept = JobRunner.LastPlan(_job.Id);
        if (kept is null)
        {
            return;
        }

        var choices = kept.Items.Where(item => item.Action == PlanAction.Conflict && item.Resolution is not null)
            .ToDictionary(item => (item.PairIndex, item.Path.ToUpperInvariant()), item => item);
        if (choices.Count == 0)
        {
            return;
        }

        var items = analysis.Plan.Items.Select(item =>
        {
            if (item.Action != PlanAction.Conflict || !choices.TryGetValue((item.PairIndex, item.Path.ToUpperInvariant()), out var chosen)
                || chosen.Source != item.Source || chosen.Target != item.Target)
            {
                return item;
            }

            var resolution = chosen.Resolution;
            return item with
            {
                Resolution = resolution,
                Bytes = Planner.BytesOf(resolution, item.Source?.Size ?? 0, item.Target?.Size ?? 0),
            };
        }).ToList();
        analysis.Plan = analysis.Plan with { Items = items };
    }

    /// <summary>The plan would delete more than the limit of a side's files: stop before anything happens (unless confirmed).</summary>
    private string? SafetyStop(SyncPlan plan)
    {
        var percent = _job.Options.SafetyDeletePercent;
        if (percent <= 0 || _request.ConfirmDeletes)
        {
            return null;
        }

        foreach (var side in new[] { Side.Source, Side.Target })
        {
            var action = side == Side.Source ? PlanAction.DeleteOnSource : PlanAction.DeleteOnTarget;
            long deletes = 0;
            foreach (var item in plan.Items.Where(item => item.Action == action))
            {
                deletes += item.IsFolder ? _pairs[item.PairIndex].FolderDeletes.GetValueOrDefault(item.Path)?.Files ?? 0 : 1;
            }

            var files = _pairs.Sum(pair => pair.Listing(side).Files);
            if (deletes > 10 && deletes * 100 > percent * files)
            {
                var share = files == 0 ? 100 : (int)Math.Round(deletes * 100.0 / files);
                return $"Angehalten: {Format.Files(deletes)} würden {EngineText.InSide(side)} gelöscht ({share} % – mehr als die Grenze von {percent} %). " +
                       "Bitte die Analyse prüfen und das Löschen bestätigen.";
            }
        }

        return null;
    }

    // ---- State, versions, cursors ----------------------------------------------------------------------------------

    /// <summary>Pairs that start over lose their old rows; pairs the job does not have any more go.</summary>
    private void PrepareState()
    {
        foreach (var pair in _pairs.Where(pair => pair.IsUsable && pair.IsReset))
        {
            _store!.ResetPair(pair.Index, pair.Fingerprint);
            pair.Meta = new PairState(pair.Index, pair.Fingerprint, null, null, null);
        }

        _store!.RemovePairsFrom(_job.Pairs.Count);
    }

    private async Task PruneVersionsAsync()
    {
        var options = _job.Options;
        if (!_context!.KeepVersions || AccessPolicy.IsDeleteDisabled || (options.KeepVersionsDays <= 0 && options.KeepVersionsCount <= 0))
        {
            return;
        }

        var now = AppClock.UtcNow;
        foreach (var pair in _pairs.Where(pair => pair.IsUsable))
        {
            var due = pair.Meta?.LastPrune is not { } last || now - last > TimeSpan.FromDays(1);
            var sides = new[] { Side.Source, Side.Target }.Where(side => due || _context.VersionsWritten.ContainsKey((pair.Index, side))).ToList();
            if (sides.Count == 0)
            {
                continue;
            }

            foreach (var side in sides)
            {
                try
                {
                    await Versions.PruneAsync(pair.Storage(side), options.KeepVersionsDays, options.KeepVersionsCount, now, Token);
                }
                catch (Exception exception) when (exception is StorageException or IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    _log.Warning(null, Versions.Root, $"Alte Versionen {EngineText.InSide(side)} konnten nicht aufgeräumt werden: {exception.Message}");
                }
            }

            if (due && pair.Meta is { } meta)
            {
                pair.Meta = meta with { LastPrune = now };
                _store!.SavePair(pair.Meta);
            }
        }
    }

    private void SaveCursors()
    {
        foreach (var pair in _pairs.Where(pair => pair.IsUsable && pair.Meta is not null))
        {
            var meta = pair.Meta! with { SourceCursor = pair.SourceListing.Cursor, TargetCursor = pair.TargetListing.Cursor };
            if (meta == pair.Meta)
            {
                continue;
            }

            pair.Meta = meta;
            _store!.SavePair(meta);
        }
    }

    // ---- Plumbing --------------------------------------------------------------------------------------------------

    private RunContext CreateContext(DateTimeOffset startedAt)
    {
        var usable = _pairs.Where(pair => pair.IsUsable).ToList();
        var automatic = usable.Count == 0
            ? 2
            : usable.Min(pair => Math.Min(pair.Source!.Capabilities.MaxParallelism, pair.Target!.Capabilities.MaxParallelism));
        var degree = _job.Options.Parallelism > 0 ? _job.Options.Parallelism
            : _settings.DefaultParallelism > 0 ? _settings.DefaultParallelism
            : Math.Max(2, automatic);
        degree = Math.Clamp(degree, 1, JobService.MaxParallelism);
        var largeDegree = Math.Min(degree, Math.Max(2, degree / 4));
        var settings = new PlanSettings
        {
            Mode = _job.Mode,
            Options = _job.Options,
            PropagateDeletes = _job.Options.PropagateDeletes && _job.Mode != SyncMode.Backup && !AccessPolicy.IsDeleteDisabled,
            KeepVersions = _job.Options.KeepVersions || _job.Mode == SyncMode.Backup,
            DeviceName = AppInfo.MachineName,
            RunStart = startedAt,
        };
        return new RunContext(_slot.Cancellation)
        {
            Settings = settings,
            Meter = _meter,
            Log = _log,
            State = _state!,
            Retry = new RetryRunner(_settings.Retries, _meter),
            Versions = new Versions(startedAt),
            Degree = degree,
            LargeDegree = largeDegree,
            SmallFileBytes = Math.Max(0, (long)_settings.SmallFileThresholdKiB * 1024),
            SmallLane = new AdaptiveLimiter(degree),
            LargeLane = new AdaptiveLimiter(largeDegree),
        };
    }

    /// <summary>Progress a few times a second; the state and the protocol in batches.</summary>
    private async Task TickAsync(CancellationToken cancellationToken)
    {
        var lastStateFlush = _meter.Elapsed;
        var lastLogFlush = _meter.Elapsed;
        using var timer = new PeriodicTimer(TickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                _meter.Tick();
                PublishProgress();
                if (_log.Pending >= LogFlushLines || (_log.Pending > 0 && _meter.Elapsed - lastLogFlush >= LogFlushInterval))
                {
                    _log.Flush();
                    lastLogFlush = _meter.Elapsed;
                }

                if (_state is { } state && (state.PendingCount >= StateFlushRows || _meter.Elapsed - lastStateFlush >= StateFlushInterval))
                {
                    state.Flush();
                    lastStateFlush = _meter.Elapsed;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void PublishProgress()
    {
        var progress = _meter.Snapshot();
        _slot.Progress = progress;
        JobRunner.OnProgress(progress);
    }

    private async Task CloseAsync()
    {
        _state?.Flush();
        _log.Flush();
        foreach (var pair in _pairs)
        {
            foreach (var storage in new[] { pair.Source, pair.Target })
            {
                if (storage is null)
                {
                    continue;
                }

                try
                {
                    await storage.DisposeAsync();
                }
                catch (Exception exception) when (exception is StorageException or IOException or ObjectDisposedException)
                {
                    Log.Warning($"A storage could not be closed ({EngineText.ForLog(exception)})");
                }
            }
        }

        _store?.Dispose();
    }

    private void PairFailed(PairContext pair, string message, List<RunEntry> warnings)
    {
        pair.Problem = message;
        lock (warnings)
        {
            warnings.Add(new RunEntry
            {
                At = AppClock.UtcNow,
                Level = RunEntryLevel.Error,
                Message = _pairs.Count == 1 ? message : $"Ordnerpaar {pair.Index + 1}: {message}",
            });
        }
    }

    private void ThrowWhenNoPairIsUsable()
    {
        if (_pairs.Any(pair => pair.IsUsable))
        {
            return;
        }

        throw new StorageException(StorageErrorKind.Unknown, _pairs.Select(pair => pair.Problem).FirstOrDefault(problem => problem is not null)
            ?? "Kein Ordnerpaar ist erreichbar.");
    }

    private (RunOutcome, string) Failed(string message)
    {
        _log.Error(null, "", message);
        return (RunOutcome.Failed, $"Fehlgeschlagen: {message}");
    }

    private string Summary(RunOutcome outcome)
    {
        var counters = _context?.Counters;
        var parts = new List<string>();
        if (counters is not null)
        {
            if (counters.FilesCopied > 0)
            {
                parts.Add($"{Format.Files(counters.FilesCopied)} kopiert ({Format.Bytes(counters.BytesCopied)})");
            }

            if (counters.FilesDeleted > 0)
            {
                parts.Add($"{Format.Count(counters.FilesDeleted)} gelöscht");
            }

            if (counters.FilesMoved > 0)
            {
                parts.Add($"{Format.Count(counters.FilesMoved)} verschoben");
            }

            if (counters.FoldersCreated > 0)
            {
                parts.Add($"{Format.Count(counters.FoldersCreated)} Ordner angelegt");
            }

            if (counters.Conflicts > 0)
            {
                parts.Add(counters.Conflicts == 1 ? "1 Konflikt" : $"{Format.Count(counters.Conflicts)} Konflikte");
            }

            if (counters.Errors > 0)
            {
                parts.Add($"{Format.Count(counters.Errors)} Fehler");
            }
        }

        var open = _analysis?.Plan.OpenConflicts ?? 0;
        if (open > 0)
        {
            parts.Add(open == 1 ? "1 Konflikt wartet auf Ihre Entscheidung" : $"{Format.Count(open)} Konflikte warten auf Ihre Entscheidung");
        }

        var text = parts.Count == 0 ? "Keine Änderungen – alles auf dem neuesten Stand" : string.Join(", ", parts);
        return outcome switch
        {
            RunOutcome.Cancelled => parts.Count == 0 ? "Abgebrochen" : $"Abgebrochen nach: {text}",
            RunOutcome.Warnings when _analysis?.Warnings.Count > 0 && parts.Count == 0 => $"Mit Warnungen: {Format.Count(_analysis.Warnings.Count)} Hinweise im Protokoll",
            _ => text,
        };
    }

    private static string AnalysisText(SyncPlan plan)
    {
        var copies = plan.Count(PlanAction.CopyToTarget) + plan.Count(PlanAction.CopyToSource);
        var deletes = plan.Count(PlanAction.DeleteOnTarget) + plan.Count(PlanAction.DeleteOnSource);
        var moves = plan.Count(PlanAction.MoveOnTarget) + plan.Count(PlanAction.MoveOnSource);
        var folders = plan.Count(PlanAction.CreateFolderOnTarget) + plan.Count(PlanAction.CreateFolderOnSource);
        var conflicts = plan.Count(PlanAction.Conflict);
        var parts = new List<string>();
        if (copies > 0)
        {
            parts.Add($"{Format.Count(copies)} zu kopieren ({Format.Bytes(plan.BytesToCopy)})");
        }

        if (deletes > 0)
        {
            parts.Add($"{Format.Count(deletes)} zu löschen");
        }

        if (moves > 0)
        {
            parts.Add($"{Format.Count(moves)} zu verschieben");
        }

        if (folders > 0)
        {
            parts.Add($"{Format.Count(folders)} Ordner anzulegen");
        }

        if (conflicts > 0)
        {
            parts.Add(conflicts == 1 ? "1 Konflikt" : $"{Format.Count(conflicts)} Konflikte");
        }

        return parts.Count == 0 ? "Analyse: alles auf dem neuesten Stand" : $"Analyse: {string.Join(", ", parts)}";
    }
}
