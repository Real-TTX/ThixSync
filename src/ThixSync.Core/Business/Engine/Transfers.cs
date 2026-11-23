using System.Security.Cryptography;
using ThixSync.Core.Infrastructure;
using ThixSync.Core.Models;
using ThixSync.Core.Storage;
using ThixSync.Core.Storage.Providers.Local;

namespace ThixSync.Core.Business.Engine;

/// <summary>
/// Copies one file from a storage to a storage: between two folders of this computer a small file goes with the operating system's
/// copy; everything else streams (never the whole file in memory) through <see cref="TransferStream"/> – progress, bandwidth limit
/// and the hash of the copy check on the way. A failed attempt is retried with the file's current size and time.
/// </summary>
internal sealed class Transfers(RunContext run)
{
    /// <summary>Copies <paramref name="entry"/> of <paramref name="from"/> to <paramref name="toPath"/> on <paramref name="to"/>. Returns what was read and what was written.</summary>
    public async Task<(StorageEntry Source, StorageEntry Written)> CopyAsync(IStorage from, StorageEntry entry, IStorage to, string toPath,
        PlanAction action, AdaptiveLimiter lane)
    {
        var current = entry;
        var attempt = 0;
        return await run.Retry.RunAsync(lane, async token =>
        {
            if (attempt++ > 0)
            {
                // the file may have changed (that is what failed): copy what is there now
                var now = await from.GetAsync(current.Path, token);
                if (now is not { IsFolder: false } file)
                {
                    throw new StorageException(StorageErrorKind.NotFound, "Die Datei ist nicht mehr da.");
                }

                current = file;
            }

            var written = await CopyOnceAsync(from, current, to, toPath, action, token);
            return (current, written);
        }, run.Token);
    }

    /// <summary>Content of two files compared by SHA-256 (CompareContent of the analysis; one read of each, at the same time).</summary>
    public static async Task<bool> HaveSameContentAsync(IStorage first, StorageEntry firstEntry, IStorage second, StorageEntry secondEntry,
        CancellationToken cancellationToken)
    {
        if (firstEntry.Size != secondEntry.Size)
        {
            return false;
        }

        if (EngineText.AreComparable(firstEntry.Hash, secondEntry.Hash))
        {
            return EngineText.HashesEqual(firstEntry.Hash!, secondEntry.Hash!);
        }

        async Task<byte[]> HashAsync(IStorage storage, string path)
        {
            await using var stream = await storage.OpenReadAsync(path, cancellationToken);
            return await SHA256.HashDataAsync(stream, cancellationToken);
        }

        var hashes = await Task.WhenAll(HashAsync(first, firstEntry.Path), HashAsync(second, secondEntry.Path));
        return hashes[0].AsSpan().SequenceEqual(hashes[1]);
    }

    private async Task<StorageEntry> CopyOnceAsync(IStorage from, StorageEntry entry, IStorage to, string toPath, PlanAction action,
        CancellationToken cancellationToken)
    {
        var modified = run.Settings.Options.PreserveModifiedTime ? entry.Modified : AppClock.UtcNow;
        var isLarge = entry.Size > run.SmallFileBytes;
        var slot = run.Meter.Begin(entry.Path, entry.Size, action, isLarge);
        var failed = true;
        try
        {
            StorageEntry written;
            if (!run.Verify && !isLarge && from is ILocalStorage local && to is LocalStorage localTarget)
            {
                written = await localTarget.CopyFromLocalAsync(local.GetLocalPath(entry.Path), toPath, modified, cancellationToken);
                slot.Add(written.Size);
            }
            else
            {
                written = await StreamAsync(from, entry, to, toPath, modified, slot, cancellationToken);
            }

            failed = false;
            return written;
        }
        finally
        {
            run.Meter.End(slot, failed);
        }
    }

    private async Task<StorageEntry> StreamAsync(IStorage from, StorageEntry entry, IStorage to, string toPath, DateTimeOffset modified,
        TransferMeter.ActiveSlot slot, CancellationToken cancellationToken)
    {
        using var hash = run.Verify ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
        StorageEntry written;
        long read;
        await using (var input = await from.OpenReadAsync(entry.Path, cancellationToken))
        {
            using var stream = new TransferStream(input, entry.Size, hash, slot.Add);
            written = await to.WriteAsync(toPath, stream, entry.Size, modified, null, cancellationToken);
            read = stream.BytesRead;
        }

        if (read != entry.Size)
        {
            throw new StorageException(StorageErrorKind.ChangedWhileReading, "Die Datei hat sich während des Kopierens geändert.");
        }

        if (run.Verify)
        {
            await VerifyAsync(entry, to, toPath, written, hash!.GetHashAndReset(), cancellationToken);
        }

        return written;
    }

    /// <summary>
    /// The copy check: the size, the hashes of both storages when they offer the same kind, and for a target on this computer the
    /// SHA-256 of the written file against the one computed while reading the source.
    /// </summary>
    private static async Task VerifyAsync(StorageEntry source, IStorage to, string toPath, StorageEntry written, byte[] sourceHash,
        CancellationToken cancellationToken)
    {
        if (written.Size != source.Size)
        {
            throw new StorageException(StorageErrorKind.ChangedWhileReading, "Prüfung nach dem Kopieren: die Größe stimmt nicht.");
        }

        if (EngineText.AreComparable(source.Hash, written.Hash) && !EngineText.HashesEqual(source.Hash!, written.Hash!))
        {
            throw new StorageException(StorageErrorKind.ChangedWhileReading, "Prüfung nach dem Kopieren: die Prüfsumme stimmt nicht.");
        }

        if (to is not ILocalStorage)
        {
            return;
        }

        await using var check = await to.OpenReadAsync(toPath, cancellationToken);
        var targetHash = await SHA256.HashDataAsync(check, cancellationToken);
        if (!targetHash.AsSpan().SequenceEqual(sourceHash))
        {
            throw new StorageException(StorageErrorKind.ChangedWhileReading, "Prüfung nach dem Kopieren: der Inhalt stimmt nicht überein.");
        }
    }
}
