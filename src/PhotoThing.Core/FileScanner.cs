namespace PhotoThing.Core;

public sealed class FileScanner
{
    private static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".gif", ".heic", ".heif", ".tif", ".tiff", ".bmp", ".webp", ".dng", ".raw", ".cr2", ".nef", ".arw" };
    private static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mov", ".m4v", ".avi", ".mkv", ".webm", ".3gp", ".mts", ".m2ts", ".wmv" };

    public IEnumerable<ScannedFile> Scan(string sourceRoot)
    {
        var root = Path.GetFullPath(sourceRoot);
        foreach (var path in EnumerateVisibleFiles(root))
        {
            var ext = Path.GetExtension(path);
            var kind = ImageExt.Contains(ext) ? MediaKind.Image
                     : VideoExt.Contains(ext) ? (MediaKind?)MediaKind.Video : null;
            if (kind is null) continue;

            var info = new FileInfo(path);
            var rel = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            yield return new ScannedFile(
                SourceRoot: root,
                RelativePath: rel,
                AbsolutePath: path,
                Size: info.Length,
                ModifiedUtc: new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                Kind: kind.Value);
        }
    }

    private static IEnumerable<string> EnumerateVisibleFiles(string dir)
    {
        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(dir); }
        catch (UnauthorizedAccessException) { yield break; }

        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            if (name.StartsWith('.')) continue; // hidden files/dirs
            if (Directory.Exists(entry))
                foreach (var f in EnumerateVisibleFiles(entry)) yield return f;
            else
                yield return entry;
        }
    }
}
