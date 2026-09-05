using FluentAssertions;
using PhotoThing.Core;
using Xunit;

public class IndexStoreTests
{
    private static BlobRecord Blob(string h, int rc = 1) =>
        new(h, 100, "STANDARD", DateTimeOffset.UnixEpoch, rc, null);

    [Fact]
    public async Task Upsert_and_get_blob_roundtrips()
    {
        await using var ws = new TempWorkspace();
        await using var idx = await ws.OpenIndexAsync();
        await idx.UpsertBlobAsync(Blob("aaaa"));
        (await idx.GetBlobAsync("aaaa"))!.RefCount.Should().Be(1);
        (await idx.GetBlobAsync("missing")).Should().BeNull();
    }

    [Fact]
    public async Task AdjustRefCount_returns_new_value()
    {
        await using var ws = new TempWorkspace();
        await using var idx = await ws.OpenIndexAsync();
        await idx.UpsertBlobAsync(Blob("aaaa", rc: 1));
        (await idx.AdjustRefCountAsync("aaaa", +1)).Should().Be(2);
        (await idx.AdjustRefCountAsync("aaaa", -2)).Should().Be(0);
    }

    [Fact]
    public async Task GcCandidates_require_zero_refcount_and_elapsed_gcAfter()
    {
        await using var ws = new TempWorkspace();
        await using var idx = await ws.OpenIndexAsync();
        var now = new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero);
        await idx.UpsertBlobAsync(Blob("keep", rc: 1));                    // refcount > 0
        await idx.UpsertBlobAsync(Blob("young", rc: 0));
        await idx.SetBlobGcAfterAsync("young", now.AddDays(1));            // not yet due
        await idx.UpsertBlobAsync(Blob("due", rc: 0));
        await idx.SetBlobGcAfterAsync("due", now.AddDays(-1));             // due

        var candidates = await idx.GetGcCandidatesAsync(now);
        candidates.Select(b => b.Hash).Should().BeEquivalentTo(new[] { "due" });
    }

    [Fact]
    public async Task Files_upsert_seen_and_softdelete_flow()
    {
        await using var ws = new TempWorkspace();
        await using var idx = await ws.OpenIndexAsync();
        var t0 = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var id = await idx.UpsertFileAsync(new FileRecord(
            0, "/root", "a.jpg", "aaaa", 100, t0, t0, FileState.Active, null, t0));

        (await idx.GetActiveFileAsync("/root", "a.jpg"))!.Id.Should().Be(id);

        var t1 = t0.AddDays(1);
        var stale = await idx.GetActiveFilesNotSeenSinceAsync("/root", t1);
        stale.Should().ContainSingle(); // last_seen (t0) < cutoff (t1)

        await idx.SoftDeleteAsync(id, t1);
        (await idx.GetActiveFileAsync("/root", "a.jpg")).Should().BeNull();
        (await idx.ListActiveFilesAsync()).Should().BeEmpty();
    }
}
