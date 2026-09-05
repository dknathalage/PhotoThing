using System.Text;
using FluentAssertions;
using PhotoThing.Core;
using Xunit;

public class GarbageCollectorTests
{
    [Fact]
    public async Task Collects_due_zero_refcount_blobs_and_deletes_objects()
    {
        await using var ws = new TempWorkspace();
        await using var idx = await ws.OpenIndexAsync();
        var store = new InMemoryBlobStore();
        var now = new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero);

        // A due blob + its objects.
        var hash = "deadbeefdeadbeef";
        await store.PutAsync(ObjectNames.Blob(hash), new MemoryStream([1]), "b");
        await store.PutAsync(ObjectNames.Thumb(hash), new MemoryStream([2]), "image/jpeg");
        await idx.UpsertBlobAsync(new BlobRecord(hash, 1, "STANDARD", now, 0, now.AddDays(-1)));
        await idx.UpsertThumbAsync(new ThumbRecord(hash, true, ObjectNames.Thumb(hash), now));

        // A blob that must survive (still referenced).
        var keep = "cafecafecafecafe";
        await store.PutAsync(ObjectNames.Blob(keep), new MemoryStream([3]), "b");
        await idx.UpsertBlobAsync(new BlobRecord(keep, 1, "STANDARD", now, 1, null));

        var collected = await new GarbageCollector(idx, store).CollectAsync(now);

        collected.Should().Be(1);
        (await store.ExistsAsync(ObjectNames.Blob(hash))).Should().BeFalse();
        (await store.ExistsAsync(ObjectNames.Thumb(hash))).Should().BeFalse();
        (await idx.GetBlobAsync(hash)).Should().BeNull();
        (await store.ExistsAsync(ObjectNames.Blob(keep))).Should().BeTrue();
    }
}
