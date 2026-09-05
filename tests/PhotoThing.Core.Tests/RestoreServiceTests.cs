using System.Text;
using FluentAssertions;
using PhotoThing.Core;
using Xunit;

public class RestoreServiceTests
{
    [Fact]
    public async Task Restore_downloads_blob_to_destination_preserving_relative_path()
    {
        await using var ws = new TempWorkspace();
        await using var idx = await ws.OpenIndexAsync();
        var store = new InMemoryBlobStore();
        var now = DateTimeOffset.UnixEpoch;

        var hash = "0011223344556677";
        var payload = Encoding.ASCII.GetBytes("original-bytes");
        await store.PutAsync(ObjectNames.Blob(hash), new MemoryStream(payload), "application/octet-stream");
        var id = await idx.UpsertFileAsync(new FileRecord(
            0, "/root", "sub/photo.jpg", hash, payload.Length, now, now, FileState.Active, null, now));
        var file = (await idx.ListActiveFilesAsync()).Single(f => f.Id == id);

        var dest = Path.Combine(ws.Root, "restored");
        await new RestoreService(idx, store).RestoreAsync(file, dest);

        var outPath = Path.Combine(dest, "sub", "photo.jpg");
        File.Exists(outPath).Should().BeTrue();
        (await File.ReadAllBytesAsync(outPath)).Should().Equal(payload);
    }
}
