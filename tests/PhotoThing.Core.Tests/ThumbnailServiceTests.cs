using FluentAssertions;
using PhotoThing.Core;
using PhotoThing.Core.Thumbnails;
using SixLabors.ImageSharp;
using Xunit;

public class ThumbnailServiceTests
{
    private sealed class FakeExtractor : IVideoFrameExtractor
    {
        public Task<byte[]> ExtractPosterFrameAsync(string videoPath, CancellationToken ct = default)
            => Task.FromResult(MakePng(200, 100));
    }

    private static byte[] MakePng(int w, int h)
    {
        using var img = new Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(w, h);
        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task Image_thumbnail_is_jpeg_and_bounded_to_max_edge()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllBytesAsync(path, MakePng(2000, 1000));
        try
        {
            var svc = new ThumbnailService(maxEdge: 512, new FakeExtractor());
            var file = new ScannedFile("/r", "big.png", path, 0, DateTimeOffset.UnixEpoch, MediaKind.Image);
            var bytes = await svc.GenerateJpegAsync(file);

            using var img = Image.Load(bytes);
            Math.Max(img.Width, img.Height).Should().Be(512);
            img.Width.Should().Be(512); // 2:1 aspect preserved
            img.Height.Should().Be(256);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Video_thumbnail_uses_extractor_frame()
    {
        var svc = new ThumbnailService(maxEdge: 512, new FakeExtractor());
        var file = new ScannedFile("/r", "v.mp4", "/does/not/matter.mp4", 0, DateTimeOffset.UnixEpoch, MediaKind.Video);
        var bytes = await svc.GenerateJpegAsync(file);
        using var img = Image.Load(bytes); // 200x100 from fake, already under 512
        img.Width.Should().Be(200);
    }
}
