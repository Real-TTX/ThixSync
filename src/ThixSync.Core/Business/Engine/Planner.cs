using ThixSync.Core.Infrastructure;
using ThixSync.Core.Models;
using ThixSync.Core.Storage;

namespace ThixSync.Core.Business.Engine;

/// <summary>The choices of a job that decide the plan.</summary>
internal sealed record PlanSettings
{
    public required SyncMode Mode { get; init; }

    public required JobOptions Options { get; init; }

    /// <summary>Deletions are carried over (option, mode and the policy DisableDelete together).</summary>
    public required bool PropagateDeletes { get; init; }

    public required bool KeepVersions { get; init; }

    /// <summary>The name of this computer (the name of a conflict copy).</summary>
    public required string DeviceName { get; init; }

    /// <summary>The local time of the run start (the name of a conflict copy).</summary>
    public required DateTimeOffset RunStart { get; init; }
}

/// <summary>
/// Compares both sides of one pair with each other and with the state of the last run and decides what to do with every path
/// (<see cref="PlanItem"/>). Each side is compared with its OWN values in the state (a storage that cannot keep modification times
/// is never compared with the other side's time). Nothing is deleted that the planner is not sure about: unknown parts (unreadable
/// folders, case collisions, file vs folder) are left alone, and a folder goes as a whole only when everything in it goes too.
/// </summary>
internal sealed class Planner
{
    private readonly PairContext _pair;
    private readonly PlanSettings _settings;
    private readonly Analysis _analysis;
    private readonly Dictionary<string, PlanItem> _items;
    private readonly List<(string Key, StorageEntry Source, StorageEntry Target, bool HasState)> _contentChecks = [];
    private readonly TimeSpan _sourceTolerance;
    private readonly TimeSpan _targetTolerance;
    private readonly TimeSpan _crossTolerance;
    private readonly bool _sourceKeepsTime;
    private readonly bool _targetKeepsTime;

    public Planner(PairContext pair, PlanSettings settings, Analysis analysis)
    {
        _pair = pair;
        _settings = settings;
        _analysis = analysis;
        _items = new Dictionary<string, PlanItem>(pair.Comparer);
        _sourceTolerance = pair.Tolerance(Side.Source, settings.Options.TimeToleranceSeconds);
        _targetTolerance = pair.Tolerance(Side.Target, settings.Options.TimeToleranceSeconds);
        _crossTolerance = _sourceTolerance > _targetTolerance ? _sourceTolerance : _targetTolerance;
        _sourceKeepsTime = pair.Storage(Side.Source).Capabilities.Has(StorageFeatures.SetModifiedTime);
        _targetKeepsTime = pair.Storage(Side.Target).Capabilities.Has(StorageFeatures.SetModifiedTime);
    }

    /// <summary>
    /// The plan of the pair. <paramref name="sameContent"/> compares two files by content (CompareContent) and is asked only for
    /// files of equal size that size and time could not decide.
    /// </summary>
    public async Task<List<PlanItem>> PlanAsync(Func<PairContext, StorageEntry, StorageEntry, CancellationToken, Task<bool>> sameContent,
        int parallelism, CancellationToken cancellationToken)
    {
        var source = _pair.SourceListing.Entries;
        var target = _pair.TargetListing.Entries;
        foreach (var (key, entry) in source)
        {
            Decide(key, entry, target.TryGetValue(key, out var other) ? other : null);
        }

        foreach (var (key, entry) in target)
        {
            if (!source.ContainsKey(key))
            {
                Decide(key, null, entry);
            }
        }

        foreach (var (key, row) in _pair.State)
        {
            if (!source.ContainsKey(key) && !target.ContainsKey(key) && !IsBlocked(key))
            {
                _analysis.DroppedRows.Add((_pair.Index, row.Path)); // gone on both sides, or left out by the filter now
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (_contentChecks.Count > 0)
        {
            await CheckContentAsync(sameContent, parallelism, cancellationToken);
        }

        if (_settings.Options.DetectMoves && _settings.PropagateDeletes && _settings.Mode != SyncMode.Backup)
        {
            DetectMoves(Side.Source);
            if (_settings.Mode == SyncMode.TwoWay)
            {
                DetectMoves(Side.Target);
            }
        }

        PlanFolderDeletes();
        return [.. _items.Values];
    }

    // ---- Decisions per path ----------------------------------------------------------------------------------------

    private void Decide(string key, StorageEntry? source, StorageEntry? target)
    {
        if (IsBlocked(key))
        {
            return;
        }

        if (source is { } s && target is { } t && s.IsFolder != t.IsFolder)
        {
            _analysis.Warnings.Add(new RunEntry
            {
                At = AppClock.UtcNow,
                Level = RunEntryLevel.Warning,
                Path = s.Path,
                Message = s.IsFolder
                    ? "In der Quelle ein Ordner, im Ziel eine Datei – übersprungen"
                    : "In der Quelle eine Datei, im Ziel ein Ordner – übersprungen",
            });
            return;
        }

        var isFolder = (source ?? target)!.Value.IsFolder;
        var row = _pair.State.TryGetValue(key, out var found) && found.IsFolder == isFolder ? found : null;
        if (isFolder)
        {
            DecideFolder(key, source, target, row);
            return;
        }

        if (_settings.Mode == SyncMode.TwoWay)
        {
            DecideTwoWay(key, source, target, row);
        }
        else
        {
            DecideOneWay(key, source, target, row);
        }
    }

    private void DecideTwoWay(string key, StorageEntry? source, StorageEntry? target, StateRow? row)
    {
        if (source is { } s && target is { } t)
        {
            if (row is null)
            {
                if (AreSame(s, t))
                {
                    InStep(key, s, t, "Auf beiden Seiten gleich");
                }
                else if (_settings.Options.CompareContent && s.Size == t.Size)
                {
                    _contentChecks.Add((key, s, t, false));
                }
                else
                {
                    Conflict(key, s, t, "Auf beiden Seiten vorhanden, aber verschieden");
                }

                return;
            }

            var sourceChanged = HasChanged(Side.Source, s, row);
            var targetChanged = HasChanged(Side.Target, t, row);
            if (!sourceChanged && !targetChanged)
            {
                return;
            }

            if (sourceChanged && !targetChanged)
            {
                Copy(key, Side.Source, s, t, "In der Quelle geändert");
            }
            else if (targetChanged && !sourceChanged)
            {
                Copy(key, Side.Target, t, s, "Im Ziel geändert");
            }
            else if (AreSame(s, t))
            {
                InStep(key, s, t, "Auf beiden Seiten gleich geändert");
            }
            else
            {
                Conflict(key, s, t, "Auf beiden Seiten geändert");
            }

            return;
        }

        var side = source is not null ? Side.Source : Side.Target;
        var entry = (source ?? target)!.Value;
        var other = PairContext.Other(side);
        if (row is null)
        {
            Copy(key, side, entry, null, side == Side.Source ? "Neu in der Quelle" : "Neu im Ziel");
            return;
        }

        // both had it after the last run: the other side deleted it
        if (HasChanged(side, entry, row))
        {
            Copy(key, side, entry, null, $"{Capital(EngineText.InSide(other))} gelöscht, {EngineText.InSide(side)} geändert");
        }
        else if (_settings.PropagateDeletes)
        {
            Delete(key, side, entry, $"{Capital(EngineText.InSide(other))} gelöscht");
        }
        else
        {
            Copy(key, side, entry, null, $"{Capital(EngineText.InSide(other))} gelöscht – wird wiederhergestellt");
        }
    }

    /// <summary>Mirror and Backup: the target follows the source (Backup never deletes).</summary>
    private void DecideOneWay(string key, StorageEntry? source, StorageEntry? target, StateRow? row)
    {
        if (source is { } s && target is { } t)
        {
            var sourceChanged = row is null || HasChanged(Side.Source, s, row);
            var targetChanged = row is null || HasChanged(Side.Target, t, row);
            if (!sourceChanged && !targetChanged)
            {
                return;
            }

            if (row is not null && !targetChanged)
            {
                Copy(key, Side.Source, s, t, "In der Quelle geändert");
                return;
            }

            // no state, or the target was changed: compare both sides directly
            if (AreSame(s, t))
            {
                InStep(key, s, t, "Auf beiden Seiten gleich");
            }
            else if (_settings.Options.CompareContent && s.Size == t.Size)
            {
                _contentChecks.Add((key, s, t, row is not null));
            }
            else
            {
                Copy(key, Side.Source, s, t, row is null ? "Im Ziel anders als in der Quelle" : "Im Ziel geändert – wird ersetzt");
            }

            return;
        }

        if (source is { } onlySource)
        {
            Copy(key, Side.Source, onlySource, null, row is null ? "Neu in der Quelle" : "Im Ziel gelöscht – wird wiederhergestellt");
            return;
        }

        if (_settings.Mode == SyncMode.Mirror && _settings.PropagateDeletes)
        {
            Delete(key, Side.Target, target!.Value, row is null ? "Nicht in der Quelle" : "In der Quelle gelöscht");
        }
    }

    private void DecideFolder(string key, StorageEntry? source, StorageEntry? target, StateRow? row)
    {
        if (source is { } s && target is { } t)
        {
            if (row is null)
            {
                _analysis.QuietRows.Add(FolderRow(s.Path));
            }

            return;
        }

        var side = source is not null ? Side.Source : Side.Target;
        var entry = (source ?? target)!.Value;
        var other = PairContext.Other(side);
        if (_settings.Mode != SyncMode.TwoWay && side == Side.Target)
        {
            if (_settings.Mode == SyncMode.Mirror && _settings.PropagateDeletes)
            {
                Delete(key, Side.Target, entry, row is null ? "Nicht in der Quelle" : "In der Quelle gelöscht");
            }

            return;
        }

        if (_settings.Mode == SyncMode.TwoWay && row is not null)
        {
            if (_settings.PropagateDeletes)
            {
                Delete(key, side, entry, $"{Capital(EngineText.InSide(other))} gelöscht");
            }
            else if (_settings.Options.CopyEmptyFolders)
            {
                CreateFolder(key, other, entry, $"{Capital(EngineText.InSide(other))} gelöscht – wird wiederhergestellt");
            }

            return;
        }

        if (_settings.Options.CopyEmptyFolders)
        {
            CreateFolder(key, other, entry, side == Side.Source ? "Neu in der Quelle" : "Neu im Ziel");
        }
    }

    // ---- Comparisons -----------------------------------------------------------------------------------------------

    /// <summary>A side changed since the last run: compared with ITS OWN values in the state.</summary>
    private bool HasChanged(Side side, in StorageEntry entry, StateRow row)
    {
        var (size, modified, hash) = side == Side.Source
            ? (row.SourceSize, row.SourceModified, row.SourceHash)
            : (row.TargetSize, row.TargetModified, row.TargetHash);
        if (entry.Size != size)
        {
            return true;
        }

        if (EngineText.AreComparable(entry.Hash, hash))
        {
            return !EngineText.HashesEqual(entry.Hash!, hash!); // the content decides; a new time alone is no change
        }

        var tolerance = side == Side.Source ? _sourceTolerance : _targetTolerance;
        return (entry.Modified - modified).Duration() > tolerance;
    }

    /// <summary>
    /// Two files of both sides are the same: equal hashes of the same kind, otherwise equal size and time. A side that cannot keep
    /// times has its upload time: its copy counts as the same when it is not older than the other side's file.
    /// </summary>
    private bool AreSame(in StorageEntry source, in StorageEntry target)
    {
        if (source.Size != target.Size)
        {
            return false;
        }

        if (EngineText.AreComparable(source.Hash, target.Hash))
        {
            return EngineText.HashesEqual(source.Hash!, target.Hash!);
        }

        return (_sourceKeepsTime, _targetKeepsTime) switch
        {
            (true, true) => (source.Modified - target.Modified).Duration() <= _crossTolerance,
            (true, false) => target.Modified >= source.Modified - _crossTolerance,
            (false, true) => source.Modified >= target.Modified - _crossTolerance,
            _ => true,
        };
    }

    private async Task CheckContentAsync(Func<PairContext, StorageEntry, StorageEntry, CancellationToken, Task<bool>> sameContent,
        int parallelism, CancellationToken cancellationToken)
    {
        var results = new bool[_contentChecks.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, _contentChecks.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism), CancellationToken = cancellationToken },
            async (index, token) =>
            {
                var (_, source, target, _) = _contentChecks[index];
                results[index] = await sameContent(_pair, source, target, token);
            });

        for (var index = 0; index < _contentChecks.Count; index++)
        {
            var (key, source, target, hasState) = _contentChecks[index];
            if (results[index])
            {
                InStep(key, source, target, "Gleicher Inhalt auf beiden Seiten");
            }
            else if (_settings.Mode == SyncMode.TwoWay)
            {
                Conflict(key, source, target, "Auf beiden Seiten vorhanden, aber verschieden");
            }
            else
            {
                Copy(key, Side.Source, source, target, hasState ? "Im Ziel geändert – wird ersetzt" : "Im Ziel anders als in der Quelle");
            }
        }
    }

    // ---- Moves -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// A file that disappeared at A on <paramref name="side"/> and appeared at B on the same side with the same size and time (and
    /// hash, when known), while the other side still has A unchanged, is moved there instead of copied again and deleted.
    /// </summary>
    private void DetectMoves(Side side)
    {
        var other = PairContext.Other(side);
        if (!_pair.Storage(other).Capabilities.Has(StorageFeatures.Move))
        {
            return;
        }

        var deleteAction = other == Side.Target ? PlanAction.DeleteOnTarget : PlanAction.DeleteOnSource;
        var copyAction = other == Side.Target ? PlanAction.CopyToTarget : PlanAction.CopyToSource;
        var listing = _pair.Listing(side);
        var otherListing = _pair.Listing(other);

        // a move keeps the time: exactly, or within 2 s where the storage rounds (no tolerance of the job: a wrong match would put
        // the wrong content under the new name)
        var coarse = _pair.Storage(side).Capabilities.Has(StorageFeatures.CoarseTime);
        var tolerance = coarse ? TimeSpan.FromSeconds(2) : TimeSpan.Zero;
        long BucketOf(DateTimeOffset time) => coarse ? time.UtcTicks / tolerance.Ticks : time.UtcTicks;

        // gone on this side, still unchanged on the other: candidates by size and time
        var gone = new Dictionary<(long Size, long Bucket), List<(string Key, StateRow Row)>>();
        foreach (var item in _items.Values)
        {
            if (item.Action != deleteAction || item.IsFolder || !_pair.State.TryGetValue(item.Path, out var row) || row.IsFolder
                || listing.Entries.ContainsKey(item.Path) || !otherListing.TryGet(item.Path, out var otherEntry) || HasChanged(other, otherEntry, row))
            {
                continue;
            }

            var (size, modified) = side == Side.Source ? (row.SourceSize, row.SourceModified) : (row.TargetSize, row.TargetModified);
            var key = (size, BucketOf(modified));
            if (!gone.TryGetValue(key, out var list))
            {
                gone[key] = list = [];
            }

            list.Add((item.Path, row));
        }

        if (gone.Count == 0)
        {
            return;
        }

        var matches = new List<(string From, string To)>();
        var usedFrom = new HashSet<string>(_pair.Comparer);
        var ambiguousFrom = new HashSet<string>(_pair.Comparer);
        var found = new List<(string Key, StateRow Row)>();
        foreach (var item in _items.Values)
        {
            var otherHasIt = other == Side.Target ? item.Target is not null : item.Source is not null;
            if (item.Action != copyAction || item.IsFolder || otherHasIt || _pair.State.ContainsKey(item.Path) || !listing.TryGet(item.Path, out var entry))
            {
                continue;
            }

            found.Clear();
            var bucket = BucketOf(entry.Modified);
            for (var near = coarse ? bucket - 1 : bucket; near <= (coarse ? bucket + 1 : bucket) && found.Count < 2; near++)
            {
                if (!gone.TryGetValue((entry.Size, near), out var candidates))
                {
                    continue;
                }

                foreach (var candidate in candidates)
                {
                    if (IsSameFile(side, entry, candidate.Row, tolerance))
                    {
                        found.Add(candidate);
                        if (found.Count > 1)
                        {
                            break; // ambiguous: copied and deleted as usual
                        }
                    }
                }
            }

            if (found.Count != 1)
            {
                continue;
            }

            var from = found[0].Key;
            if (!usedFrom.Add(from))
            {
                ambiguousFrom.Add(from);
            }

            matches.Add((from, item.Path));
        }

        foreach (var (from, to) in matches)
        {
            if (ambiguousFrom.Contains(from))
            {
                continue;
            }

            var copy = _items[to];
            _items.Remove(from);
            _items[to] = copy with
            {
                Action = other == Side.Target ? PlanAction.MoveOnTarget : PlanAction.MoveOnSource,
                FromPath = otherListing.PathOf(from),
                Bytes = 0,
                Reason = side == Side.Source ? "In der Quelle umbenannt oder verschoben" : "Im Ziel umbenannt oder verschoben",
            };
        }
    }

    private static bool IsSameFile(Side side, in StorageEntry entry, StateRow row, TimeSpan tolerance)
    {
        var (size, modified, hash) = side == Side.Source
            ? (row.SourceSize, row.SourceModified, row.SourceHash)
            : (row.TargetSize, row.TargetModified, row.TargetHash);
        if (entry.Size != size || (entry.Modified - modified).Duration() > tolerance)
        {
            return false;
        }

        return !EngineText.AreComparable(entry.Hash, hash) || EngineText.HashesEqual(entry.Hash!, hash!);
    }

    // ---- Folder deletions ------------------------------------------------------------------------------------------

    /// <summary>
    /// A folder planned for deletion goes as a whole only when everything in it on that side is planned for deletion (or moves
    /// away) and nothing unknown is inside (filtered, unreadable). Then its content items are dropped from the plan (one step).
    /// Otherwise the folder stays (its content items stay as they are). Without a move on the storage, kept versions need file by
    /// file: the files go first, the then empty folder after them.
    /// </summary>
    private void PlanFolderDeletes()
    {
        var folders = _items.Values.Where(item => item.IsFolder && item.Action is PlanAction.DeleteOnSource or PlanAction.DeleteOnTarget)
            .OrderBy(item => StoragePath.DepthOf(item.Path))
            .ToList();
        if (folders.Count == 0)
        {
            return;
        }

        var stats = new Dictionary<string, FolderStats>(_pair.Comparer);
        foreach (var folder in folders)
        {
            stats[folder.Path] = new FolderStats(folder.Action == PlanAction.DeleteOnSource ? Side.Source : Side.Target, _pair.Comparer);
        }

        var lookup = stats.GetAlternateLookup<ReadOnlySpan<char>>();
        foreach (var side in new[] { Side.Source, Side.Target })
        {
            var deleteAction = side == Side.Source ? PlanAction.DeleteOnSource : PlanAction.DeleteOnTarget;
            var moveAction = side == Side.Source ? PlanAction.MoveOnSource : PlanAction.MoveOnTarget;
            var movedAway = new HashSet<string>(_items.Values.Where(item => item.Action == moveAction && item.FromPath is not null).Select(item => item.FromPath!),
                _pair.Comparer);
            foreach (var entry in _pair.Listing(side).Entries.Values)
            {
                var path = entry.Path;
                for (var slash = path.LastIndexOf('/'); slash > 0; slash = path.LastIndexOf('/', slash - 1))
                {
                    if (!lookup.TryGetValue(path.AsSpan(0, slash), out var folder) || folder.Side != side)
                    {
                        continue;
                    }

                    folder.Total++;
                    if (_items.TryGetValue(path, out var item) && item.Action == deleteAction)
                    {
                        folder.Covered++;
                        folder.Deletable.Add(path);
                        folder.Files += entry.IsFolder ? 0 : 1;
                    }
                    else if (!entry.IsFolder && movedAway.Contains(path))
                    {
                        folder.Covered++;
                    }
                }
            }
        }

        var wholeFolders = new HashSet<string>(_pair.Comparer);
        var wholeLookup = wholeFolders.GetAlternateLookup<ReadOnlySpan<char>>();
        bool GoesWithParent(string path)
        {
            for (var slash = path.LastIndexOf('/'); slash > 0; slash = path.LastIndexOf('/', slash - 1))
            {
                if (wholeLookup.Contains(path.AsSpan(0, slash)))
                {
                    return true;
                }
            }

            return false;
        }

        foreach (var folder in folders)
        {
            if (GoesWithParent(folder.Path))
            {
                continue; // goes with a folder above it
            }

            var stat = stats[folder.Path];
            var side = stat.Side;
            var listing = _pair.Listing(side);
            var complete = stat.Covered == stat.Total && !listing.Tainted.Contains(folder.Path) && !listing.IsInsideUnreadable(folder.Path);
            if (!complete)
            {
                _items.Remove(folder.Path);
                continue;
            }

            var storage = _pair.Storage(side);
            var recursive = !_settings.KeepVersions || storage.Capabilities.Has(StorageFeatures.Move);
            var covered = new List<string>(stat.Deletable) { folder.Path };
            if (recursive)
            {
                foreach (var path in stat.Deletable)
                {
                    _items.Remove(path);
                }

                wholeFolders.Add(folder.Path);
            }

            _pair.FolderDeletes[folder.Path] = new FolderDelete(side, recursive, recursive ? stat.Deletable : new HashSet<string>(_pair.Comparer), covered, stat.Files);
            _items[folder.Path] = folder with { Reason = stat.Files > 0 ? $"{folder.Reason} (mit {Format.Files(stat.Files)})" : folder.Reason };
        }
    }

    private sealed class FolderStats(Side side, StringComparer comparer)
    {
        public Side Side { get; } = side;

        public int Total { get; set; }

        public int Covered { get; set; }

        public int Files { get; set; }

        public HashSet<string> Deletable { get; } = new(comparer);
    }

    // ---- Items -----------------------------------------------------------------------------------------------------

    private bool IsBlocked(string key) =>
        _pair.Collisions.Contains(key) || _pair.SourceListing.IsInsideUnreadable(key) || _pair.TargetListing.IsInsideUnreadable(key);

    private void Copy(string key, Side from, in StorageEntry entry, StorageEntry? existing, string reason)
    {
        var toTarget = from == Side.Source;
        Add(key, new PlanItem
        {
            PairIndex = _pair.Index,
            Action = toTarget ? PlanAction.CopyToTarget : PlanAction.CopyToSource,
            Path = entry.Path,
            Bytes = entry.Size,
            Source = toTarget ? EngineText.Facts(entry) : existing is { } source ? EngineText.Facts(source) : null,
            Target = toTarget ? existing is { } target ? EngineText.Facts(target) : null : EngineText.Facts(entry),
            Reason = reason,
        });
    }

    private void Delete(string key, Side side, in StorageEntry entry, string reason) => Add(key, new PlanItem
    {
        PairIndex = _pair.Index,
        Action = side == Side.Target ? PlanAction.DeleteOnTarget : PlanAction.DeleteOnSource,
        Path = entry.Path,
        IsFolder = entry.IsFolder,
        Source = side == Side.Source ? EngineText.Facts(entry) : null,
        Target = side == Side.Target ? EngineText.Facts(entry) : null,
        Reason = reason,
    });

    private void CreateFolder(string key, Side on, in StorageEntry entry, string reason) => Add(key, new PlanItem
    {
        PairIndex = _pair.Index,
        Action = on == Side.Target ? PlanAction.CreateFolderOnTarget : PlanAction.CreateFolderOnSource,
        Path = entry.Path,
        IsFolder = true,
        Source = on == Side.Target ? EngineText.Facts(entry) : null,
        Target = on == Side.Source ? EngineText.Facts(entry) : null,
        Reason = reason,
    });

    private void InStep(string key, in StorageEntry source, in StorageEntry target, string reason) => Add(key, new PlanItem
    {
        PairIndex = _pair.Index,
        Action = PlanAction.InStep,
        Path = source.Path,
        Source = EngineText.Facts(source),
        Target = EngineText.Facts(target),
        Reason = reason,
    });

    private void Conflict(string key, in StorageEntry source, in StorageEntry target, string reason)
    {
        ConflictChoice? resolution = _settings.Options.Conflicts switch
        {
            ConflictPolicy.SourceWins => ConflictChoice.KeepSource,
            ConflictPolicy.TargetWins => ConflictChoice.KeepTarget,
            ConflictPolicy.KeepBoth => ConflictChoice.KeepBoth,
            ConflictPolicy.NewerWins when source.Modified > target.Modified + _crossTolerance => ConflictChoice.KeepSource,
            ConflictPolicy.NewerWins when target.Modified > source.Modified + _crossTolerance => ConflictChoice.KeepTarget,
            ConflictPolicy.NewerWins => ConflictChoice.KeepBoth,
            _ => null,
        };

        Add(key, new PlanItem
        {
            PairIndex = _pair.Index,
            Action = PlanAction.Conflict,
            Path = source.Path,
            Bytes = BytesOf(resolution, source.Size, target.Size),
            Source = EngineText.Facts(source),
            Target = EngineText.Facts(target),
            Reason = reason,
            Resolution = resolution,
        });
    }

    /// <summary>The bytes a resolved conflict moves.</summary>
    public static long BytesOf(ConflictChoice? resolution, long sourceSize, long targetSize) => resolution switch
    {
        ConflictChoice.KeepSource => sourceSize,
        ConflictChoice.KeepTarget => targetSize,
        ConflictChoice.KeepBoth => sourceSize + targetSize,
        _ => 0,
    };

    private void Add(string key, PlanItem item) => _items[key] = item;

    private StateRow FolderRow(string path) => new(_pair.Index, path, true, 0, default, null, 0, default, null);

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
