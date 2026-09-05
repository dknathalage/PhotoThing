using FluentAssertions;
using PhotoThing.Core;
using Xunit;

public class MediaMetadataExtractorTests
{
    [Fact]
    public void Falls_back_to_mtime_when_no_exif()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, "not a real image");
        try
        {
            var mtime = new DateTimeOffset(2022, 1, 2, 3, 4, 5, TimeSpan.Zero);
            var file = new ScannedFile("/root", "x.jpg", path, 10, mtime, MediaKind.Image);
            new MediaMetadataExtractor().GetCaptureDate(file).Should().Be(mtime);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Video_always_uses_mtime()
    {
        var mtime = new DateTimeOffset(2021, 6, 7, 8, 9, 10, TimeSpan.Zero);
        var file = new ScannedFile("/root", "v.mp4", "/nonexistent.mp4", 10, mtime, MediaKind.Video);
        new MediaMetadataExtractor().GetCaptureDate(file).Should().Be(mtime);
    }
}
