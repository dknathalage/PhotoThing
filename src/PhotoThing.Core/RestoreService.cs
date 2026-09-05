namespace PhotoThing.Core;

public sealed class RestoreService
{
    private readonly IndexStore _index;
    private readonly IBlobStore _store;

    public RestoreService(IndexStore index, IBlobStore store)
    {
        _index = index; _store = store;
    }

    public Task<IReadOnlyList<FileRecord>> BrowseAsync() => _index.ListActiveFilesAsync();

    public async Task RestoreAsync(FileRecord file, string destinationDir, CancellationToken ct = default)
    {
        var outPath = Path.Combine(destinationDir, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

        await using var dst = File.Create(outPath);
        await _store.GetToAsync(ObjectNames.Blob(file.Hash), dst, ct);
    }
}
