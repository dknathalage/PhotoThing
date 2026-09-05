namespace PhotoThing.Core;

public interface IBlobStore
{
    Task<bool> ExistsAsync(string objectName, CancellationToken ct = default);

    Task PutAsync(
        string objectName,
        Stream content,
        string contentType,
        IReadOnlyDictionary<string, string>? metadata = null,
        string? storageClass = null,
        CancellationToken ct = default);

    Task<Stream> GetAsync(string objectName, CancellationToken ct = default);

    Task DeleteAsync(string objectName, CancellationToken ct = default);

    IAsyncEnumerable<string> ListAsync(string prefix, CancellationToken ct = default);

    /// Configure a lifecycle rule that transitions objects under `blobs/` to
    /// the Archive storage class after `days`. Idempotent.
    Task SetLifecycleArchiveAfterAsync(int days, CancellationToken ct = default);
}
