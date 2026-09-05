using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using PhotoThing.Core;

public sealed class InMemoryBlobStore : IBlobStore
{
    public ConcurrentDictionary<string, byte[]> Objects { get; } = new();
    public ConcurrentDictionary<string, string> StorageClasses { get; } = new();
    public int? LifecycleArchiveDays { get; private set; }

    public Task<bool> ExistsAsync(string objectName, CancellationToken ct = default)
        => Task.FromResult(Objects.ContainsKey(objectName));

    public async Task PutAsync(string objectName, Stream content, string contentType,
        IReadOnlyDictionary<string, string>? metadata = null, string? storageClass = null,
        CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        Objects[objectName] = ms.ToArray();
        StorageClasses[objectName] = storageClass ?? "STANDARD";
    }

    public Task<Stream> GetAsync(string objectName, CancellationToken ct = default)
        => Objects.TryGetValue(objectName, out var bytes)
            ? Task.FromResult<Stream>(new MemoryStream(bytes))
            : throw new FileNotFoundException(objectName);

    public async Task GetToAsync(string objectName, Stream destination, CancellationToken ct = default)
    {
        if (!Objects.TryGetValue(objectName, out var bytes))
            throw new FileNotFoundException(objectName);
        await destination.WriteAsync(bytes, ct);
    }

    public Task DeleteAsync(string objectName, CancellationToken ct = default)
    {
        Objects.TryRemove(objectName, out _);
        StorageClasses.TryRemove(objectName, out _);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<string> ListAsync(string prefix,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var key in Objects.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)))
        {
            ct.ThrowIfCancellationRequested();
            yield return key;
        }
        await Task.CompletedTask;
    }

    public Task SetLifecycleArchiveAfterAsync(int days, CancellationToken ct = default)
    {
        LifecycleArchiveDays = days;
        return Task.CompletedTask;
    }
}
