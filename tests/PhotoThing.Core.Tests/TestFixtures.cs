using PhotoThing.Core;

/// Disposable temp directory; also opens a throwaway IndexStore.
public sealed class TempWorkspace : IAsyncDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "pt-" + Guid.NewGuid().ToString("N"));
    public string DbPath => Path.Combine(Root, "index.db");

    public TempWorkspace() => Directory.CreateDirectory(Root);

    public Task<IndexStore> OpenIndexAsync() => IndexStore.OpenAsync(DbPath);

    public ValueTask DisposeAsync()
    {
        try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
        return ValueTask.CompletedTask;
    }
}
