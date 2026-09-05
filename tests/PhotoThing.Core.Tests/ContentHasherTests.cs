using System.Text;
using FluentAssertions;
using PhotoThing.Core;
using Xunit;

public class ContentHasherTests
{
    [Fact]
    public async Task HashStream_matches_known_sha256_of_abc()
    {
        // SHA-256("abc") is a well-known vector.
        using var s = new MemoryStream(Encoding.ASCII.GetBytes("abc"));
        var hash = await ContentHasher.HashStreamAsync(s);
        hash.Should().Be("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    }

    [Fact]
    public async Task HashFile_and_HashStream_agree()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, "hello world");
        try
        {
            var fileHash = await ContentHasher.HashFileAsync(path);
            using var s = File.OpenRead(path);
            var streamHash = await ContentHasher.HashStreamAsync(s);
            fileHash.Should().Be(streamHash);
        }
        finally { File.Delete(path); }
    }
}
