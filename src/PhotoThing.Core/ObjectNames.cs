namespace PhotoThing.Core;

public static class ObjectNames
{
    public static string Blob(string hash) => $"blobs/{hash[..2]}/{hash[2..4]}/{hash}";
    public static string Thumb(string hash) => $"thumbs/{hash}.jpg";
    public static string Snapshot(string utcStamp) => $"index/snapshot-{utcStamp}.db";
    public const string LatestIndex = "index/latest.db";
}
