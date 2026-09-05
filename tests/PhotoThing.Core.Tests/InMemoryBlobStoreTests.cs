using System.Text;
using FluentAssertions;
using Xunit;

public class InMemoryBlobStoreTests
{
    [Fact]
    public async Task Put_then_Get_roundtrips_and_records_storage_class()
    {
        var store = new InMemoryBlobStore();
        using var content = new MemoryStream(Encoding.ASCII.GetBytes("data"));
        await store.PutAsync("blobs/aa/bb/aabb", content, "application/octet-stream", storageClass: "STANDARD");

        (await store.ExistsAsync("blobs/aa/bb/aabb")).Should().BeTrue();
        store.StorageClasses["blobs/aa/bb/aabb"].Should().Be("STANDARD");

        using var reader = new StreamReader(await store.GetAsync("blobs/aa/bb/aabb"));
        (await reader.ReadToEndAsync()).Should().Be("data");
    }

    [Fact]
    public async Task GetTo_writes_object_bytes_into_destination()
    {
        var store = new InMemoryBlobStore();
        await store.PutAsync("blobs/x", new MemoryStream(Encoding.ASCII.GetBytes("payload")), "b");
        using var dest = new MemoryStream();
        await store.GetToAsync("blobs/x", dest);
        Encoding.ASCII.GetString(dest.ToArray()).Should().Be("payload");
    }

    [Fact]
    public async Task List_filters_by_prefix()
    {
        var store = new InMemoryBlobStore();
        await store.PutAsync("blobs/x", new MemoryStream([1]), "b");
        await store.PutAsync("thumbs/y", new MemoryStream([2]), "b");
        var listed = new List<string>();
        await foreach (var n in store.ListAsync("blobs/")) listed.Add(n);
        listed.Should().ContainSingle().Which.Should().Be("blobs/x");
    }
}
