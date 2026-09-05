namespace PhotoThing.Core;

public sealed class GarbageCollector
{
    private readonly IndexStore _index;
    private readonly IBlobStore _store;

    public GarbageCollector(IndexStore index, IBlobStore store)
    {
        _index = index; _store = store;
    }

    public async Task<int> CollectAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var candidates = await _index.GetGcCandidatesAsync(now);
        foreach (var blob in candidates)
        {
            ct.ThrowIfCancellationRequested();
            await _store.DeleteAsync(ObjectNames.Blob(blob.Hash), ct);
            await _store.DeleteAsync(ObjectNames.Thumb(blob.Hash), ct);
            await _index.DeleteBlobAsync(blob.Hash);
            await _index.DeleteThumbAsync(blob.Hash);
        }
        return candidates.Count;
    }
}
