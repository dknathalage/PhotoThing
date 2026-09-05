using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace PhotoThing.Core.Thumbnails;

public sealed class ThumbnailService
{
    private readonly int _maxEdge;
    private readonly IVideoFrameExtractor _videoExtractor;

    public ThumbnailService(int maxEdge, IVideoFrameExtractor videoExtractor)
    {
        _maxEdge = maxEdge;
        _videoExtractor = videoExtractor;
    }

    public async Task<byte[]> GenerateJpegAsync(ScannedFile file, CancellationToken ct = default)
    {
        using var image = file.Kind == MediaKind.Image
            ? await Image.LoadAsync(file.AbsolutePath, ct)
            : Image.Load(await _videoExtractor.ExtractPosterFrameAsync(file.AbsolutePath, ct));

        // Only resize if the longest side exceeds maxEdge
        if (Math.Max(image.Width, image.Height) > _maxEdge)
        {
            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Mode = ResizeMode.Max,
                Size = new Size(_maxEdge, _maxEdge),
            }));
        }

        using var ms = new MemoryStream();
        await image.SaveAsJpegAsync(ms, new JpegEncoder { Quality = 82 }, ct);
        return ms.ToArray();
    }
}
