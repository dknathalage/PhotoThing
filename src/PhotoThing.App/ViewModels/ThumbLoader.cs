using PhotoThing.Core;

namespace PhotoThing.App.ViewModels;

public sealed class ThumbLoader
{
    private readonly IBlobStore _store;
    private readonly string _cacheDir;

    public ThumbLoader(IBlobStore store, string cacheDir)
    {
        _store = store; _cacheDir = cacheDir;
        Directory.CreateDirectory(_cacheDir);
    }

    public async Task<string> EnsureLocalThumbAsync(string hash, CancellationToken ct = default)
    {
        var local = Path.Combine(_cacheDir, hash + ".jpg");
        if (File.Exists(local)) return local;

        // Download to a temp file then move into place, so a failed download
        // never leaves a truncated file in the cache.
        var temp = Path.Combine(_cacheDir, hash + ".jpg.tmp");
        await using (var dst = File.Create(temp))
            await _store.GetToAsync(ObjectNames.Thumb(hash), dst, ct);
        File.Move(temp, local, overwrite: true);
        return local;
    }
}
