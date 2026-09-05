using PhotoThing.Core.Thumbnails;

namespace PhotoThing.Core;

public sealed record BackupResult(int Uploaded, int Deduped, int Skipped, int SoftDeleted);

/// Progress for one source root: how many files have been processed of the total.
public sealed record BackupProgress(int Processed, int Total, string CurrentFile);

public sealed class BackupService
{
    private readonly IndexStore _index;
    private readonly IBlobStore _store;
    private readonly ThumbnailService _thumbs;
    private readonly MediaMetadataExtractor _meta;
    private readonly FileScanner _scanner;
    private readonly int _graceDays;

    public BackupService(IndexStore index, IBlobStore store, ThumbnailService thumbs,
        MediaMetadataExtractor meta, FileScanner scanner, int gracePeriodDays)
    {
        _index = index; _store = store; _thumbs = thumbs;
        _meta = meta; _scanner = scanner; _graceDays = gracePeriodDays;
    }

    public async Task<BackupResult> BackupAsync(string sourceRoot, DateTimeOffset now,
        CancellationToken ct = default, IProgress<BackupProgress>? progress = null)
    {
        var root = Path.GetFullPath(sourceRoot);
        int uploaded = 0, deduped = 0, skipped = 0, softDeleted = 0;

        var files = _scanner.Scan(root).ToList();
        var total = files.Count;
        var processed = 0;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new BackupProgress(processed, total, file.RelativePath));

            var existing = await _index.GetActiveFileAsync(root, file.RelativePath);
            if (existing is not null && existing.Size == file.Size && existing.ModifiedUtc == file.ModifiedUtc)
            {
                await _index.MarkSeenAsync(existing.Id, now);
                skipped++;
                continue;
            }

            var hash = await ContentHasher.HashFileAsync(file.AbsolutePath, ct);
            var blob = await _index.GetBlobAsync(hash);

            if (blob is null)
            {
                await UploadNewBlobAsync(file, hash, now, ct);
                uploaded++;
            }
            else
            {
                await _index.AdjustRefCountAsync(hash, +1);
                // A revived blob should no longer be scheduled for GC.
                await _index.SetBlobGcAfterAsync(hash, null);
                deduped++;
            }

            if (existing is not null && existing.Hash != hash)
            {
                var oldCount = await _index.AdjustRefCountAsync(existing.Hash, -1);
                if (oldCount <= 0)
                    await _index.SetBlobGcAfterAsync(existing.Hash, now.AddDays(_graceDays));
            }

            await _index.UpsertFileAsync(new FileRecord(
                0, root, file.RelativePath, hash, file.Size, file.ModifiedUtc,
                _meta.GetCaptureDate(file), FileState.Active, null, now));

            processed++;
        }
        progress?.Report(new BackupProgress(processed, total, ""));

        softDeleted = await SoftDeleteUnseenAsync(root, now, ct);
        return new BackupResult(uploaded, deduped, skipped, softDeleted);
    }

    private async Task UploadNewBlobAsync(ScannedFile file, string hash, DateTimeOffset now, CancellationToken ct)
    {
        var thumbBytes = await _thumbs.GenerateJpegAsync(file, ct);
        using (var thumbStream = new MemoryStream(thumbBytes))
            await _store.PutAsync(ObjectNames.Thumb(hash), thumbStream, "image/jpeg", storageClass: "STANDARD", ct: ct);
        await _index.UpsertThumbAsync(new ThumbRecord(hash, true, ObjectNames.Thumb(hash), now));

        await using (var content = File.OpenRead(file.AbsolutePath))
            await _store.PutAsync(
                ObjectNames.Blob(hash), content, "application/octet-stream",
                metadata: new Dictionary<string, string> { ["filename"] = Path.GetFileName(file.RelativePath) },
                storageClass: "STANDARD", ct: ct);

        await _index.UpsertBlobAsync(new BlobRecord(hash, file.Size, "STANDARD", now, RefCount: 1, GcAfter: null));
    }

    private async Task<int> SoftDeleteUnseenAsync(string root, DateTimeOffset now, CancellationToken ct)
    {
        // Files touched this run were stamped last_seen = now. This query uses a
        // strict `last_seen < now`, so it returns only files NOT seen this run —
        // never one we just backed up. (Relies on GetActiveFilesNotSeenSinceAsync
        // being strict `<`; a change to `<=` would soft-delete same-run files.)
        var stale = await _index.GetActiveFilesNotSeenSinceAsync(root, now);
        foreach (var f in stale)
        {
            ct.ThrowIfCancellationRequested();
            await _index.SoftDeleteAsync(f.Id, now);
            var newCount = await _index.AdjustRefCountAsync(f.Hash, -1);
            if (newCount <= 0)
                await _index.SetBlobGcAfterAsync(f.Hash, now.AddDays(_graceDays));
        }
        return stale.Count;
    }

    public async Task SnapshotIndexAsync(string dbPath, string utcStamp, CancellationToken ct = default)
    {
        // Copy first so we upload a stable file even while the live DB is open (WAL).
        var temp = Path.Combine(Path.GetTempPath(), $"pt-snap-{utcStamp}.db");
        File.Copy(dbPath, temp, overwrite: true);
        try
        {
            await using (var s = File.OpenRead(temp))
                await _store.PutAsync(ObjectNames.Snapshot(utcStamp), s, "application/x-sqlite3", storageClass: "STANDARD", ct: ct);
            await using (var s = File.OpenRead(temp))
                await _store.PutAsync(ObjectNames.LatestIndex, s, "application/x-sqlite3", storageClass: "STANDARD", ct: ct);
        }
        finally { File.Delete(temp); }
    }
}
