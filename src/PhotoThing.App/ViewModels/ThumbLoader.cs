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
        await using var src = await _store.GetAsync(ObjectNames.Thumb(hash), ct);
        await using var dst = File.Create(local);
        await src.CopyToAsync(dst, ct);
        return local;
    }
}
