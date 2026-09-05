using FluentAssertions;
using PhotoThing.Core;
using Xunit;

public class FileScannerTests
{
    private static string MakeTree()
    {
        var root = Path.Combine(Path.GetTempPath(), "pt-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        File.WriteAllText(Path.Combine(root, "a.jpg"), "x");
        File.WriteAllText(Path.Combine(root, "b.MP4"), "x");   // case-insensitive
        File.WriteAllText(Path.Combine(root, "notes.txt"), "x"); // excluded
        File.WriteAllText(Path.Combine(root, "sub", "c.png"), "x");
        File.WriteAllText(Path.Combine(root, ".hidden.jpg"), "x"); // excluded
        return root;
    }

    [Fact]
    public void Scan_returns_only_media_and_skips_hidden_and_nonmedia()
    {
        var root = MakeTree();
        try
        {
            var files = new FileScanner().Scan(root).ToList();
            files.Select(f => f.RelativePath).Should()
                .BeEquivalentTo(new[] { "a.jpg", "b.MP4", "sub/c.png" });
            files.Single(f => f.RelativePath == "b.MP4").Kind.Should().Be(MediaKind.Video);
            files.Single(f => f.RelativePath == "a.jpg").Kind.Should().Be(MediaKind.Image);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
