using MetadataExtractor;
using MetadataExtractor.Formats.Exif;

namespace PhotoThing.Core;

public sealed class MediaMetadataExtractor
{
    public DateTimeOffset GetCaptureDate(ScannedFile file)
    {
        if (file.Kind == MediaKind.Image)
        {
            try
            {
                var dirs = ImageMetadataReader.ReadMetadata(file.AbsolutePath);
                var subIfd = dirs.OfType<ExifSubIfdDirectory>().FirstOrDefault();
                if (subIfd is not null &&
                    subIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dt))
                {
                    return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc), TimeSpan.Zero);
                }
            }
            catch
            {
                // Corrupt/unsupported metadata — fall through to mtime.
            }
        }
        return file.ModifiedUtc;
    }
}
