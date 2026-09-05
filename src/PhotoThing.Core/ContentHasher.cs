using System.Security.Cryptography;

namespace PhotoThing.Core;

public static class ContentHasher
{
    public static async Task<string> HashStreamAsync(Stream stream, CancellationToken ct = default)
    {
        using var sha = SHA256.Create();
        var bytes = await sha.ComputeHashAsync(stream, ct);
        return Convert.ToHexStringLower(bytes);
    }

    public static async Task<string> HashFileAsync(string path, CancellationToken ct = default)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1 << 20, useAsync: true);
        return await HashStreamAsync(stream, ct);
    }
}
