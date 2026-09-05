namespace PhotoThing.Core;

public enum MediaKind { Image, Video }
public enum FileState { Active, Deleted }

/// A file discovered on disk during a scan.
public sealed record ScannedFile(
    string SourceRoot,
    string RelativePath,
    string AbsolutePath,
    long Size,
    DateTimeOffset ModifiedUtc,
    MediaKind Kind);

/// A unique content blob known to the index.
public sealed record BlobRecord(
    string Hash,
    long Size,
    string StorageClass,
    DateTimeOffset UploadedAt,
    int RefCount,
    DateTimeOffset? GcAfter);

/// A logical file tracked in the index (may share a blob with others).
public sealed record FileRecord(
    long Id,
    string SourceRoot,
    string RelativePath,
    string Hash,
    long Size,
    DateTimeOffset ModifiedUtc,
    DateTimeOffset? CaptureDate,
    FileState State,
    DateTimeOffset? DeletedAt,
    DateTimeOffset LastSeen);

public sealed record ThumbRecord(
    string Hash,
    bool Ready,
    string? GcsObject,
    DateTimeOffset? GeneratedAt);
