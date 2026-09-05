namespace PhotoThing.Core;

public sealed record ReconcileReport(
    IReadOnlyList<string> MissingInGcs,
    IReadOnlyList<string> OrphanedInGcs);

public sealed class ReconcileService
{
    private readonly IndexStore _index;
    private readonly IBlobStore _store;

    public ReconcileService(IndexStore index, IBlobStore store)
    {
        _index = index; _store = store;
    }

    public async Task<ReconcileReport> ReconcileAsync(CancellationToken ct = default)
    {
        // Hashes present as objects in GCS.
        var inGcs = new HashSet<string>();
        await foreach (var name in _store.ListAsync("blobs/", ct))
            inGcs.Add(name[(name.LastIndexOf('/') + 1)..]);

        // Hashes the index believes exist.
        var indexed = new HashSet<string>(
            (await _index.ListActiveFilesAsync()).Select(f => f.Hash));
        // Include blobs referenced by GC-scheduled/zero-ref rows too, via a direct query:
        foreach (var h in await GetAllBlobHashesAsync())
            indexed.Add(h);

        var missing = indexed.Where(h => !inGcs.Contains(h)).OrderBy(h => h).ToList();
        var orphaned = inGcs.Where(h => !indexed.Contains(h)).OrderBy(h => h).ToList();
        return new ReconcileReport(missing, orphaned);
    }

    private async Task<IReadOnlyList<string>> GetAllBlobHashesAsync()
    {
        // Reuse GetGcCandidatesAsync with a far-future cutoff would only return due ones;
        // instead expose all via a dedicated helper on IndexStore.
        return await _index.GetAllBlobHashesAsync();
    }
}
