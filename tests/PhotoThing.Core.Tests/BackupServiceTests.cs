using FluentAssertions;
using PhotoThing.Core;
using PhotoThing.Core.Thumbnails;
using SixLabors.ImageSharp;
using Xunit;

public class BackupServiceTests
{
    private sealed class FakeExtractor : IVideoFrameExtractor
    {
        public Task<byte[]> ExtractPosterFrameAsync(string p, CancellationToken ct = default)
            => throw new NotSupportedException("no videos in these tests");
    }

    private static async Task<string> WritePngAsync(string dir, string name, int w, int h)
    {
        var path = Path.Combine(dir, name);
        using var img = new Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(w, h);
        await img.SaveAsPngAsync(path);
        return path;
    }

    private static BackupService NewService(IndexStore idx, InMemoryBlobStore store) =>
        new(idx, store, new ThumbnailService(512, new FakeExtractor()),
            new MediaMetadataExtractor(), new FileScanner(), gracePeriodDays: 30);

    private static readonly DateTimeOffset Now = new(2026, 9, 5, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task First_backup_uploads_content_and_thumbnail()
    {
        await using var ws = new TempWorkspace();
        await using var idx = await ws.OpenIndexAsync();
        var store = new InMemoryBlobStore();
        await WritePngAsync(ws.Root, "a.png", 10, 10);

        var result = await NewService(idx, store).BackupAsync(ws.Root, Now);

        result.Uploaded.Should().Be(1);
        store.Objects.Keys.Should().Contain(k => k.StartsWith("blobs/"));
        store.Objects.Keys.Should().Contain(k => k.StartsWith("thumbs/"));
        (await idx.ListActiveFilesAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task Rerun_with_no_changes_skips_everything()
    {
        await using var ws = new TempWorkspace();
        await using var idx = await ws.OpenIndexAsync();
        var store = new InMemoryBlobStore();
        await WritePngAsync(ws.Root, "a.png", 10, 10);

        await NewService(idx, store).BackupAsync(ws.Root, Now);
        var second = await NewService(idx, store).BackupAsync(ws.Root, Now.AddMinutes(1));

        second.Skipped.Should().Be(1);
        second.Uploaded.Should().Be(0);
    }

    [Fact]
    public async Task Identical_content_second_path_is_deduped()
    {
        await using var ws = new TempWorkspace();
        await using var idx = await ws.OpenIndexAsync();
        var store = new InMemoryBlobStore();
        var p1 = await WritePngAsync(ws.Root, "a.png", 10, 10);
        File.Copy(p1, Path.Combine(ws.Root, "copy.png")); // identical bytes

        var result = await NewService(idx, store).BackupAsync(ws.Root, Now);

        result.Uploaded.Should().Be(1);
        result.Deduped.Should().Be(1);
        store.Objects.Keys.Count(k => k.StartsWith("blobs/")).Should().Be(1); // one blob
        var files = await idx.ListActiveFilesAsync();
        var hash = files.First().Hash;
        (await idx.GetBlobAsync(hash))!.RefCount.Should().Be(2);
    }

    [Fact]
    public async Task Removed_file_is_soft_deleted_and_blob_scheduled_for_gc()
    {
        await using var ws = new TempWorkspace();
        await using var idx = await ws.OpenIndexAsync();
        var store = new InMemoryBlobStore();
        var p1 = await WritePngAsync(ws.Root, "a.png", 10, 10);
        await NewService(idx, store).BackupAsync(ws.Root, Now);
        var hash = (await idx.ListActiveFilesAsync()).First().Hash;

        File.Delete(p1);
        var result = await NewService(idx, store).BackupAsync(ws.Root, Now.AddDays(1));

        result.SoftDeleted.Should().Be(1);
        (await idx.ListActiveFilesAsync()).Should().BeEmpty();
        var blob = (await idx.GetBlobAsync(hash))!;
        blob.RefCount.Should().Be(0);
        blob.GcAfter.Should().Be(Now.AddDays(1).AddDays(30));
    }

    [Fact]
    public async Task Modified_file_content_decrements_old_blob_and_schedules_gc()
    {
        await using var ws = new TempWorkspace();
        await using var idx = await ws.OpenIndexAsync();
        var store = new InMemoryBlobStore();
        var svc = NewService(idx, store);

        // First backup: 10x10 PNG
        await WritePngAsync(ws.Root, "a.png", 10, 10);
        await svc.BackupAsync(ws.Root, Now);

        var files1 = await idx.ListActiveFilesAsync();
        var oldHash = files1.Single().Hash;
        (await idx.GetBlobAsync(oldHash))!.RefCount.Should().Be(1);

        // Overwrite with different content (20x20 → different size and hash)
        await WritePngAsync(ws.Root, "a.png", 20, 20);

        // Second backup one day later
        var result = await svc.BackupAsync(ws.Root, Now.AddDays(1));

        var files2 = await idx.ListActiveFilesAsync();
        files2.Should().ContainSingle();
        var newHash = files2.Single().Hash;
        newHash.Should().NotBe(oldHash);

        // New blob has refcount 1
        (await idx.GetBlobAsync(newHash))!.RefCount.Should().Be(1);

        // Old blob refcount dropped to 0 and scheduled for GC
        var oldBlob = (await idx.GetBlobAsync(oldHash))!;
        oldBlob.RefCount.Should().Be(0);
        oldBlob.GcAfter.Should().Be(Now.AddDays(1).AddDays(30));
    }

    [Fact]
    public async Task SnapshotIndex_uploads_snapshot_and_latest()
    {
        await using var ws = new TempWorkspace();
        await using var idx = await ws.OpenIndexAsync();
        var store = new InMemoryBlobStore();
        var svc = NewService(idx, store);

        await svc.SnapshotIndexAsync(ws.DbPath, "20260905T101500Z");

        store.Objects.Keys.Should().Contain("index/snapshot-20260905T101500Z.db");
        store.Objects.Keys.Should().Contain("index/latest.db");
    }
}
