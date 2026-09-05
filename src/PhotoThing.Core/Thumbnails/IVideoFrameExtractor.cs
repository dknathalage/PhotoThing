namespace PhotoThing.Core.Thumbnails;

public interface IVideoFrameExtractor
{
    /// Returns encoded image bytes (JPEG or PNG) for a representative frame.
    Task<byte[]> ExtractPosterFrameAsync(string videoPath, CancellationToken ct = default);
}
