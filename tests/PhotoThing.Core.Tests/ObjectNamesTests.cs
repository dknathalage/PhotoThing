using FluentAssertions;
using PhotoThing.Core;
using Xunit;

public class ObjectNamesTests
{
    [Fact]
    public void Blob_shards_by_first_two_byte_pairs()
    {
        var hash = "abcdef1234567890";
        ObjectNames.Blob(hash).Should().Be("blobs/ab/cd/abcdef1234567890");
    }

    [Fact]
    public void Thumb_uses_hash_and_jpg_extension()
    {
        ObjectNames.Thumb("deadbeef").Should().Be("thumbs/deadbeef.jpg");
    }

    [Fact]
    public void Snapshot_embeds_stamp_and_latest_is_constant()
    {
        ObjectNames.Snapshot("20260905T101500Z").Should().Be("index/snapshot-20260905T101500Z.db");
        ObjectNames.LatestIndex.Should().Be("index/latest.db");
    }
}
