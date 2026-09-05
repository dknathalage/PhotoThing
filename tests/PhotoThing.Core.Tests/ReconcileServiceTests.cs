using FluentAssertions;
using PhotoThing.Core;
using Xunit;

public class ReconcileServiceTests
{
    [Fact]
    public async Task Reports_missing_and_orphaned_blobs()
    {
        await using var ws = new TempWorkspace();
        await using var idx = await ws.OpenIndexAsync();
        var store = new InMemoryBlobStore();
        var now = DateTimeOffset.UnixEpoch;

        // Indexed + present  -> healthy
        var ok = "aaaaaaaaaaaaaaaa";
        await idx.UpsertBlobAsync(new BlobRecord(ok, 1, "STANDARD", now, 1, null));
        await store.PutAsync(ObjectNames.Blob(ok), new MemoryStream([1]), "b");

        // Indexed but object gone -> missing
        var missing = "bbbbbbbbbbbbbbbb";
        await idx.UpsertBlobAsync(new BlobRecord(missing, 1, "STANDARD", now, 1, null));

        // Object present but not indexed -> orphaned
        var orphan = "cccccccccccccccc";
        await store.PutAsync(ObjectNames.Blob(orphan), new MemoryStream([1]), "b");

        var report = await new ReconcileService(idx, store).ReconcileAsync();

        report.MissingInGcs.Should().BeEquivalentTo(new[] { missing });
        report.OrphanedInGcs.Should().BeEquivalentTo(new[] { orphan });
    }
}
