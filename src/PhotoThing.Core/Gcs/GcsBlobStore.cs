using System.Runtime.CompilerServices;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Storage.v1.Data;
using Google.Cloud.Storage.V1;

namespace PhotoThing.Core.Gcs;

/// <summary>
/// IBlobStore implementation backed by Google Cloud Storage, using
/// Application Default Credentials (ADC).
///
/// Use the static factory <see cref="CreateAsync"/> to obtain an instance.
/// </summary>
public sealed class GcsBlobStore : IBlobStore, IDisposable
{
    private readonly StorageClient _client;
    private readonly string _bucket;

    private GcsBlobStore(StorageClient client, string bucket)
    {
        _client = client;
        _bucket = bucket;
    }

    /// <summary>
    /// Build a GcsBlobStore using Application Default Credentials.
    /// </summary>
    public static async Task<GcsBlobStore> CreateAsync(
        string bucket,
        string? projectId = null,
        CancellationToken ct = default)
    {
        var credential = await GoogleCredential.GetApplicationDefaultAsync(ct);
        var client = await StorageClient.CreateAsync(credential);
        return new GcsBlobStore(client, bucket);
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(string objectName, CancellationToken ct = default)
    {
        try
        {
            await _client.GetObjectAsync(_bucket, objectName, cancellationToken: ct);
            return true;
        }
        catch (Google.GoogleApiException e) when (e.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task PutAsync(
        string objectName,
        Stream content,
        string contentType,
        IReadOnlyDictionary<string, string>? metadata = null,
        string? storageClass = null,
        CancellationToken ct = default)
    {
        var obj = new Google.Apis.Storage.v1.Data.Object
        {
            Bucket = _bucket,
            Name = objectName,
            ContentType = contentType,
            StorageClass = storageClass,
            Metadata = metadata?.ToDictionary(k => k.Key, v => v.Value),
        };
        await _client.UploadObjectAsync(obj, content, cancellationToken: ct);
    }

    /// <inheritdoc />
    public async Task<Stream> GetAsync(string objectName, CancellationToken ct = default)
    {
        var ms = new MemoryStream();
        await _client.DownloadObjectAsync(_bucket, objectName, ms, cancellationToken: ct);
        ms.Position = 0;
        return ms;
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string objectName, CancellationToken ct = default)
    {
        try
        {
            await _client.DeleteObjectAsync(_bucket, objectName, cancellationToken: ct);
        }
        catch (Google.GoogleApiException e) when (e.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Object already gone — treat as success (idempotent delete).
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> ListAsync(
        string prefix,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var o in _client.ListObjectsAsync(_bucket, prefix).WithCancellation(ct))
            yield return o.Name;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Sets a lifecycle rule on the bucket that transitions objects under
    /// <c>blobs/</c> to the ARCHIVE storage class after <paramref name="days"/>.
    /// This is idempotent: calling it again replaces the existing lifecycle config.
    /// </remarks>
    public async Task SetLifecycleArchiveAfterAsync(int days, CancellationToken ct = default)
    {
        // PATCH only the lifecycle field. A full GetBucket + UpdateBucket (PUT) echoes
        // back output-only fields the bucket carries (satisfiesPzs, encryption
        // enforcement, etc.), which GCS rejects with 400 "Invalid argument".
        var patch = new Google.Apis.Storage.v1.Data.Bucket
        {
            Name = _bucket,
            Lifecycle = LifecycleManager.ToGoogleLifecycle(
                LifecycleManager.BuildArchiveAfterRule(days)),
        };
        await _client.PatchBucketAsync(patch, cancellationToken: ct);
    }

    public void Dispose() => _client.Dispose();
}
