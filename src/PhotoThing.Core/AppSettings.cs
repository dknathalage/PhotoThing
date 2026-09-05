using System.Text.Json;

namespace PhotoThing.Core;

public sealed record AppSettings(
    string? ProjectId,
    string BucketName,
    IReadOnlyList<string> SourceRoots,
    int GracePeriodDays = 30,
    int ArchiveAfterDays = 180,
    int ThumbnailMaxEdge = 512,
    int MaxParallelUploads = 4,
    string FfmpegPath = "ffmpeg",
    int SyncIntervalMinutes = 60)
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static AppSettings Load(string path) =>
        JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Opts)
        ?? throw new InvalidDataException("Settings file was empty or invalid.");

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Opts));
    }
}
