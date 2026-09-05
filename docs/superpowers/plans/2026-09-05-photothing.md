# PhotoThing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a macOS desktop app that backs up and archives local photos/videos to a Google Cloud Storage bucket using local ADC, with content-addressed dedup, a SQLite index, thumbnails, incremental backup, soft-delete, tiering, and restore.

**Architecture:** All logic lives in a UI-free `PhotoThing.Core` library tested offline against an in-memory `IBlobStore`. A real `GcsBlobStore` implements the same interface over Google Cloud Storage. A thin Avalonia MVVM app (`PhotoThing.App`) wraps Core services via dependency injection.

**Tech Stack:** .NET 10, C#, Avalonia 11 (Fluent theme) + CommunityToolkit.Mvvm, Microsoft.Data.Sqlite, Google.Cloud.Storage.V1 (ADC), SixLabors.ImageSharp, MetadataExtractor, ffmpeg (external, for video frames), xUnit + FluentAssertions.

**Spec:** `docs/superpowers/specs/2026-09-05-photothing-design.md`

## Global Constraints

- Target framework: **`net10.0`** for Core/Tests; `net10.0` desktop for the Avalonia app.
- Content hash: **SHA-256, lowercase hex**. Used as the blob key everywhere.
- GCS object names (verbatim): blobs → `blobs/<h[0:2]>/<h[2:4]>/<hash>`; thumbnails → `thumbs/<hash>.jpg`; index snapshots → `index/snapshot-<utcStamp>.db`; latest index pointer → `index/latest.db`.
- Only `blobs/` is subject to the Standard→Archive lifecycle. `thumbs/` and `index/` stay Standard.
- Defaults: grace period **30 days**, Archive after **180 days**, thumbnail **512px** longest edge JPEG, **4** parallel uploads.
- All timestamps stored/compared as **UTC** (`DateTimeOffset`), persisted as ISO-8601 strings in SQLite.
- No network access in `PhotoThing.Core.Tests` — always use `InMemoryBlobStore`.
- Every service depends on interfaces, never on `StorageClient` directly (except `GcsBlobStore`).

---

## File Structure

```
PhotoThing.sln
src/PhotoThing.Core/
  Models.cs                 # records + enums shared across services
  ObjectNames.cs            # GCS object-name helpers
  IBlobStore.cs             # storage abstraction
  ContentHasher.cs          # SHA-256 streaming
  FileScanner.cs            # enumerate + filter source roots
  MediaMetadataExtractor.cs # EXIF capture date + mtime fallback
  IndexStore.cs             # SQLite schema + repository
  Thumbnails/
    IVideoFrameExtractor.cs # abstraction over ffmpeg
    FfmpegVideoFrameExtractor.cs
    ThumbnailService.cs     # ImageSharp resize + video poster frame
  BackupService.cs          # scan→hash→thumb→dedup→upload→index
  GarbageCollector.cs       # soft-delete grace + refcount GC
  RestoreService.cs         # query index, download, write file
  ReconcileService.cs       # index vs GCS drift
  Gcs/
    GcsBlobStore.cs         # real GCS impl (ADC)
    LifecycleManager.cs     # bucket lifecycle rule
  AppSettings.cs            # config record + load/save
tests/PhotoThing.Core.Tests/
  InMemoryBlobStore.cs      # test double
  TestFixtures.cs           # temp dir + temp sqlite helpers
  *Tests.cs                 # one file per service
src/PhotoThing.App/         # Avalonia GUI (added in Tasks 15-19)
```

---

## Task 1: Solution scaffolding + models + object names

**Files:**
- Create: `PhotoThing.sln`, `src/PhotoThing.Core/PhotoThing.Core.csproj`, `tests/PhotoThing.Core.Tests/PhotoThing.Core.Tests.csproj`
- Create: `src/PhotoThing.Core/Models.cs`, `src/PhotoThing.Core/ObjectNames.cs`
- Test: `tests/PhotoThing.Core.Tests/ObjectNamesTests.cs`

**Interfaces:**
- Produces: the shared model records/enums and `ObjectNames` static helpers used by every later task.

- [ ] **Step 1: Scaffold solution and projects**

```bash
cd /Users/dknathalage/photothing
dotnet new sln -n PhotoThing
dotnet new classlib -n PhotoThing.Core -o src/PhotoThing.Core -f net10.0
dotnet new xunit -n PhotoThing.Core.Tests -o tests/PhotoThing.Core.Tests -f net10.0
rm src/PhotoThing.Core/Class1.cs tests/PhotoThing.Core.Tests/UnitTest1.cs
dotnet sln add src/PhotoThing.Core tests/PhotoThing.Core.Tests
dotnet add tests/PhotoThing.Core.Tests reference src/PhotoThing.Core
dotnet add tests/PhotoThing.Core.Tests package FluentAssertions
```

- [ ] **Step 2: Write `Models.cs`**

```csharp
namespace PhotoThing.Core;

public enum MediaKind { Image, Video }
public enum FileState { Active, Deleted }

/// A file discovered on disk during a scan.
public sealed record ScannedFile(
    string SourceRoot,
    string RelativePath,
    string AbsolutePath,
    long Size,
    DateTimeOffset ModifiedUtc,
    MediaKind Kind);

/// A unique content blob known to the index.
public sealed record BlobRecord(
    string Hash,
    long Size,
    string StorageClass,
    DateTimeOffset UploadedAt,
    int RefCount,
    DateTimeOffset? GcAfter);

/// A logical file tracked in the index (may share a blob with others).
public sealed record FileRecord(
    long Id,
    string SourceRoot,
    string RelativePath,
    string Hash,
    long Size,
    DateTimeOffset ModifiedUtc,
    DateTimeOffset? CaptureDate,
    FileState State,
    DateTimeOffset? DeletedAt,
    DateTimeOffset LastSeen);

public sealed record ThumbRecord(
    string Hash,
    bool Ready,
    string? GcsObject,
    DateTimeOffset? GeneratedAt);
```

- [ ] **Step 3: Write the failing test `ObjectNamesTests.cs`**

```csharp
using FluentAssertions;
using PhotoThing.Core;
using Xunit;

public class ObjectNamesTests
{
    [Fact]
    public void Blob_shards_by_first_two_byte_pairs()
    {
        var hash = "abcdef1234567890";
        ObjectNames.Blob(hash).Should().Be("blobs/ab/cd/abcdef1234567890");
    }

    [Fact]
    public void Thumb_uses_hash_and_jpg_extension()
    {
        ObjectNames.Thumb("deadbeef").Should().Be("thumbs/deadbeef.jpg");
    }

    [Fact]
    public void Snapshot_embeds_stamp_and_latest_is_constant()
    {
        ObjectNames.Snapshot("20260905T101500Z").Should().Be("index/snapshot-20260905T101500Z.db");
        ObjectNames.LatestIndex.Should().Be("index/latest.db");
    }
}
```

- [ ] **Step 4: Run test to verify it fails**

Run: `dotnet test tests/PhotoThing.Core.Tests --filter ObjectNamesTests`
Expected: FAIL — `ObjectNames` does not exist.

- [ ] **Step 5: Write `ObjectNames.cs`**

```csharp
namespace PhotoThing.Core;

public static class ObjectNames
{
    public static string Blob(string hash) => $"blobs/{hash[..2]}/{hash[2..4]}/{hash}";
    public static string Thumb(string hash) => $"thumbs/{hash}.jpg";
    public static string Snapshot(string utcStamp) => $"index/snapshot-{utcStamp}.db";
    public const string LatestIndex = "index/latest.db";
}
```

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test tests/PhotoThing.Core.Tests --filter ObjectNamesTests`
Expected: PASS (3 tests).

- [ ] **Step 7: Commit**

```bash
git add PhotoThing.sln src tests
git commit -m "feat: scaffold solution, core models, and object-name helpers"
```

---

## Task 2: ContentHasher (streaming SHA-256)

**Files:**
- Create: `src/PhotoThing.Core/ContentHasher.cs`
- Test: `tests/PhotoThing.Core.Tests/ContentHasherTests.cs`

**Interfaces:**
- Produces: `ContentHasher.HashFileAsync(string path, CancellationToken) -> Task<string>` and `ContentHasher.HashStreamAsync(Stream, CancellationToken) -> Task<string>`, returning lowercase hex SHA-256.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Text;
using FluentAssertions;
using PhotoThing.Core;
using Xunit;

public class ContentHasherTests
{
    [Fact]
    public async Task HashStream_matches_known_sha256_of_abc()
    {
        // SHA-256("abc") is a well-known vector.
        using var s = new MemoryStream(Encoding.ASCII.GetBytes("abc"));
        var hash = await ContentHasher.HashStreamAsync(s);
        hash.Should().Be("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    }

    [Fact]
    public async Task HashFile_and_HashStream_agree()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, "hello world");
        try
        {
            var fileHash = await ContentHasher.HashFileAsync(path);
            using var s = File.OpenRead(path);
            var streamHash = await ContentHasher.HashStreamAsync(s);
            fileHash.Should().Be(streamHash);
        }
        finally { File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PhotoThing.Core.Tests --filter ContentHasherTests`
Expected: FAIL — `ContentHasher` not defined.

- [ ] **Step 3: Write `ContentHasher.cs`**

```csharp
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
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PhotoThing.Core.Tests --filter ContentHasherTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/PhotoThing.Core/ContentHasher.cs tests/PhotoThing.Core.Tests/ContentHasherTests.cs
git commit -m "feat: streaming SHA-256 content hasher"
```

---

## Task 3: FileScanner (enumerate + filter media)

**Files:**
- Create: `src/PhotoThing.Core/FileScanner.cs`
- Test: `tests/PhotoThing.Core.Tests/FileScannerTests.cs`

**Interfaces:**
- Consumes: `ScannedFile`, `MediaKind`.
- Produces: `FileScanner.Scan(string sourceRoot) -> IEnumerable<ScannedFile>`. Recurses; includes known image/video extensions (case-insensitive); skips hidden files/dirs (name starting with `.`). `RelativePath` uses `/` separators relative to `sourceRoot`.

- [ ] **Step 1: Write the failing test**

```csharp
using FluentAssertions;
using PhotoThing.Core;
using Xunit;

public class FileScannerTests
{
    private static string MakeTree()
    {
        var root = Path.Combine(Path.GetTempPath(), "pt-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        File.WriteAllText(Path.Combine(root, "a.jpg"), "x");
        File.WriteAllText(Path.Combine(root, "b.MP4"), "x");   // case-insensitive
        File.WriteAllText(Path.Combine(root, "notes.txt"), "x"); // excluded
        File.WriteAllText(Path.Combine(root, "sub", "c.png"), "x");
        File.WriteAllText(Path.Combine(root, ".hidden.jpg"), "x"); // excluded
        return root;
    }

    [Fact]
    public void Scan_returns_only_media_and_skips_hidden_and_nonmedia()
    {
        var root = MakeTree();
        try
        {
            var files = new FileScanner().Scan(root).ToList();
            files.Select(f => f.RelativePath).Should()
                .BeEquivalentTo(new[] { "a.jpg", "b.MP4", "sub/c.png" });
            files.Single(f => f.RelativePath == "b.MP4").Kind.Should().Be(MediaKind.Video);
            files.Single(f => f.RelativePath == "a.jpg").Kind.Should().Be(MediaKind.Image);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PhotoThing.Core.Tests --filter FileScannerTests`
Expected: FAIL — `FileScanner` not defined.

- [ ] **Step 3: Write `FileScanner.cs`**

```csharp
namespace PhotoThing.Core;

public sealed class FileScanner
{
    private static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".gif", ".heic", ".heif", ".tif", ".tiff", ".bmp", ".webp", ".dng", ".raw", ".cr2", ".nef", ".arw" };
    private static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mov", ".m4v", ".avi", ".mkv", ".webm", ".3gp", ".mts", ".m2ts", ".wmv" };

    public IEnumerable<ScannedFile> Scan(string sourceRoot)
    {
        var root = Path.GetFullPath(sourceRoot);
        foreach (var path in EnumerateVisibleFiles(root))
        {
            var ext = Path.GetExtension(path);
            var kind = ImageExt.Contains(ext) ? MediaKind.Image
                     : VideoExt.Contains(ext) ? (MediaKind?)MediaKind.Video : null;
            if (kind is null) continue;

            var info = new FileInfo(path);
            var rel = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            yield return new ScannedFile(
                SourceRoot: root,
                RelativePath: rel,
                AbsolutePath: path,
                Size: info.Length,
                ModifiedUtc: new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                Kind: kind.Value);
        }
    }

    private static IEnumerable<string> EnumerateVisibleFiles(string dir)
    {
        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(dir); }
        catch (UnauthorizedAccessException) { yield break; }

        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            if (name.StartsWith('.')) continue; // hidden files/dirs
            if (Directory.Exists(entry))
                foreach (var f in EnumerateVisibleFiles(entry)) yield return f;
            else
                yield return entry;
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PhotoThing.Core.Tests --filter FileScannerTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/PhotoThing.Core/FileScanner.cs tests/PhotoThing.Core.Tests/FileScannerTests.cs
git commit -m "feat: recursive media file scanner"
```

---

## Task 4: MediaMetadataExtractor (EXIF capture date + mtime fallback)

**Files:**
- Create: `src/PhotoThing.Core/MediaMetadataExtractor.cs`
- Test: `tests/PhotoThing.Core.Tests/MediaMetadataExtractorTests.cs`

**Interfaces:**
- Consumes: `ScannedFile`.
- Produces: `MediaMetadataExtractor.GetCaptureDate(ScannedFile) -> DateTimeOffset`. For images, tries EXIF `DateTimeOriginal`; on any failure or for videos, returns `file.ModifiedUtc`.

- [ ] **Step 1: Add the MetadataExtractor package**

```bash
dotnet add src/PhotoThing.Core package MetadataExtractor
```

- [ ] **Step 2: Write the failing test**

```csharp
using FluentAssertions;
using PhotoThing.Core;
using Xunit;

public class MediaMetadataExtractorTests
{
    [Fact]
    public void Falls_back_to_mtime_when_no_exif()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, "not a real image");
        try
        {
            var mtime = new DateTimeOffset(2022, 1, 2, 3, 4, 5, TimeSpan.Zero);
            var file = new ScannedFile("/root", "x.jpg", path, 10, mtime, MediaKind.Image);
            new MediaMetadataExtractor().GetCaptureDate(file).Should().Be(mtime);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Video_always_uses_mtime()
    {
        var mtime = new DateTimeOffset(2021, 6, 7, 8, 9, 10, TimeSpan.Zero);
        var file = new ScannedFile("/root", "v.mp4", "/nonexistent.mp4", 10, mtime, MediaKind.Video);
        new MediaMetadataExtractor().GetCaptureDate(file).Should().Be(mtime);
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/PhotoThing.Core.Tests --filter MediaMetadataExtractorTests`
Expected: FAIL — `MediaMetadataExtractor` not defined.

- [ ] **Step 4: Write `MediaMetadataExtractor.cs`**

```csharp
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;

namespace PhotoThing.Core;

public sealed class MediaMetadataExtractor
{
    public DateTimeOffset GetCaptureDate(ScannedFile file)
    {
        if (file.Kind == MediaKind.Image)
        {
            try
            {
                var dirs = ImageMetadataReader.ReadMetadata(file.AbsolutePath);
                var subIfd = dirs.OfType<ExifSubIfdDirectory>().FirstOrDefault();
                if (subIfd is not null &&
                    subIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dt))
                {
                    return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc), TimeSpan.Zero);
                }
            }
            catch
            {
                // Corrupt/unsupported metadata — fall through to mtime.
            }
        }
        return file.ModifiedUtc;
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/PhotoThing.Core.Tests --filter MediaMetadataExtractorTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/PhotoThing.Core/MediaMetadataExtractor.cs tests/PhotoThing.Core.Tests/MediaMetadataExtractorTests.cs
git commit -m "feat: capture-date extraction with mtime fallback"
```

---

## Task 5: IBlobStore + InMemoryBlobStore test double

**Files:**
- Create: `src/PhotoThing.Core/IBlobStore.cs`
- Create: `tests/PhotoThing.Core.Tests/InMemoryBlobStore.cs`
- Test: `tests/PhotoThing.Core.Tests/InMemoryBlobStoreTests.cs`

**Interfaces:**
- Produces: `IBlobStore` with `ExistsAsync`, `PutAsync`, `GetAsync`, `DeleteAsync`, `ListAsync`, `SetLifecycleArchiveAfterAsync`. `InMemoryBlobStore` implements it in-process for tests, exposing `Objects` (name→bytes) and `StorageClasses` (name→class) for assertions.

- [ ] **Step 1: Write `IBlobStore.cs`**

```csharp
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
```

- [ ] **Step 2: Write `InMemoryBlobStore.cs`**

```csharp
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
```

- [ ] **Step 3: Write the failing test `InMemoryBlobStoreTests.cs`**

```csharp
using System.Text;
using FluentAssertions;
using Xunit;

public class InMemoryBlobStoreTests
{
    [Fact]
    public async Task Put_then_Get_roundtrips_and_records_storage_class()
    {
        var store = new InMemoryBlobStore();
        using var content = new MemoryStream(Encoding.ASCII.GetBytes("data"));
        await store.PutAsync("blobs/aa/bb/aabb", content, "application/octet-stream", storageClass: "STANDARD");

        (await store.ExistsAsync("blobs/aa/bb/aabb")).Should().BeTrue();
        store.StorageClasses["blobs/aa/bb/aabb"].Should().Be("STANDARD");

        using var reader = new StreamReader(await store.GetAsync("blobs/aa/bb/aabb"));
        (await reader.ReadToEndAsync()).Should().Be("data");
    }

    [Fact]
    public async Task List_filters_by_prefix()
    {
        var store = new InMemoryBlobStore();
        await store.PutAsync("blobs/x", new MemoryStream([1]), "b");
        await store.PutAsync("thumbs/y", new MemoryStream([2]), "b");
        var listed = new List<string>();
        await foreach (var n in store.ListAsync("blobs/")) listed.Add(n);
        listed.Should().ContainSingle().Which.Should().Be("blobs/x");
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PhotoThing.Core.Tests --filter InMemoryBlobStoreTests`
Expected: PASS (the interface + double are the implementation under test here).

- [ ] **Step 5: Commit**

```bash
git add src/PhotoThing.Core/IBlobStore.cs tests/PhotoThing.Core.Tests/InMemoryBlobStore.cs tests/PhotoThing.Core.Tests/InMemoryBlobStoreTests.cs
git commit -m "feat: IBlobStore abstraction and in-memory test double"
```

---

## Task 6: IndexStore (SQLite schema + repository)

**Files:**
- Create: `src/PhotoThing.Core/IndexStore.cs`
- Create: `tests/PhotoThing.Core.Tests/TestFixtures.cs`
- Test: `tests/PhotoThing.Core.Tests/IndexStoreTests.cs`

**Interfaces:**
- Consumes: `BlobRecord`, `FileRecord`, `ThumbRecord`, `FileState`.
- Produces `IndexStore` (implements `IDisposable`, `IAsyncDisposable`) with:
  - `static Task<IndexStore> OpenAsync(string dbPath)` — opens/creates schema.
  - `Task<BlobRecord?> GetBlobAsync(string hash)`
  - `Task UpsertBlobAsync(BlobRecord blob)`
  - `Task<int> AdjustRefCountAsync(string hash, int delta)` — returns new refcount.
  - `Task SetBlobGcAfterAsync(string hash, DateTimeOffset? gcAfter)`
  - `Task<IReadOnlyList<BlobRecord>> GetGcCandidatesAsync(DateTimeOffset now)` — refcount ≤ 0 and `GcAfter` < now.
  - `Task DeleteBlobAsync(string hash)`
  - `Task<FileRecord?> GetActiveFileAsync(string sourceRoot, string relativePath)`
  - `Task<long> UpsertFileAsync(FileRecord file)` — returns row id.
  - `Task MarkSeenAsync(long id, DateTimeOffset lastSeen)`
  - `Task<IReadOnlyList<FileRecord>> GetActiveFilesNotSeenSinceAsync(string sourceRoot, DateTimeOffset cutoff)`
  - `Task SoftDeleteAsync(long id, DateTimeOffset deletedAt)`
  - `Task<IReadOnlyList<FileRecord>> ListActiveFilesAsync()`
  - `Task UpsertThumbAsync(ThumbRecord thumb)` / `Task<ThumbRecord?> GetThumbAsync(string hash)`

- [ ] **Step 1: Add Sqlite package + write `TestFixtures.cs`**

```bash
dotnet add src/PhotoThing.Core package Microsoft.Data.Sqlite
```

```csharp
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
```

- [ ] **Step 2: Write the failing test `IndexStoreTests.cs`**

```csharp
using FluentAssertions;
using PhotoThing.Core;
using Xunit;

public class IndexStoreTests
{
    private static BlobRecord Blob(string h, int rc = 1) =>
        new(h, 100, "STANDARD", DateTimeOffset.UnixEpoch, rc, null);

    [Fact]
    public async Task Upsert_and_get_blob_roundtrips()
    {
        await using var ws = new TempWorkspace();
        await using var idx = await ws.OpenIndexAsync();
        await idx.UpsertBlobAsync(Blob("aaaa"));
        (await idx.GetBlobAsync("aaaa"))!.RefCount.Should().Be(1);
        (await idx.GetBlobAsync("missing")).Should().BeNull();
    }

    [Fact]
    public async Task AdjustRefCount_returns_new_value()
    {
        await using var ws = new TempWorkspace();
        await using var idx = await ws.OpenIndexAsync();
        await idx.UpsertBlobAsync(Blob("aaaa", rc: 1));
        (await idx.AdjustRefCountAsync("aaaa", +1)).Should().Be(2);
        (await idx.AdjustRefCountAsync("aaaa", -2)).Should().Be(0);
    }

    [Fact]
    public async Task GcCandidates_require_zero_refcount_and_elapsed_gcAfter()
    {
        await using var ws = new TempWorkspace();
        await using var idx = await ws.OpenIndexAsync();
        var now = new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero);
        await idx.UpsertBlobAsync(Blob("keep", rc: 1));                    // refcount > 0
        await idx.UpsertBlobAsync(Blob("young", rc: 0));
        await idx.SetBlobGcAfterAsync("young", now.AddDays(1));            // not yet due
        await idx.UpsertBlobAsync(Blob("due", rc: 0));
        await idx.SetBlobGcAfterAsync("due", now.AddDays(-1));             // due

        var candidates = await idx.GetGcCandidatesAsync(now);
        candidates.Select(b => b.Hash).Should().BeEquivalentTo(new[] { "due" });
    }

    [Fact]
    public async Task Files_upsert_seen_and_softdelete_flow()
    {
        await using var ws = new TempWorkspace();
        await using var idx = await ws.OpenIndexAsync();
        var t0 = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var id = await idx.UpsertFileAsync(new FileRecord(
            0, "/root", "a.jpg", "aaaa", 100, t0, t0, FileState.Active, null, t0));

        (await idx.GetActiveFileAsync("/root", "a.jpg"))!.Id.Should().Be(id);

        var t1 = t0.AddDays(1);
        var stale = await idx.GetActiveFilesNotSeenSinceAsync("/root", t1);
        stale.Should().ContainSingle(); // last_seen (t0) < cutoff (t1)

        await idx.SoftDeleteAsync(id, t1);
        (await idx.GetActiveFileAsync("/root", "a.jpg")).Should().BeNull();
        (await idx.ListActiveFilesAsync()).Should().BeEmpty();
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/PhotoThing.Core.Tests --filter IndexStoreTests`
Expected: FAIL — `IndexStore` not defined.

- [ ] **Step 4: Write `IndexStore.cs`**

```csharp
using Microsoft.Data.Sqlite;

namespace PhotoThing.Core;

public sealed class IndexStore : IAsyncDisposable, IDisposable
{
    private readonly SqliteConnection _conn;

    private IndexStore(SqliteConnection conn) => _conn = conn;

    public static async Task<IndexStore> OpenAsync(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        await conn.OpenAsync();
        var store = new IndexStore(conn);
        await store.InitSchemaAsync();
        return store;
    }

    private async Task InitSchemaAsync()
    {
        await ExecAsync("""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS blobs (
                hash TEXT PRIMARY KEY,
                size INTEGER NOT NULL,
                storage_class TEXT NOT NULL,
                uploaded_at TEXT NOT NULL,
                refcount INTEGER NOT NULL,
                gc_after TEXT NULL);
            CREATE TABLE IF NOT EXISTS files (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source_root TEXT NOT NULL,
                relative_path TEXT NOT NULL,
                hash TEXT NOT NULL,
                size INTEGER NOT NULL,
                mtime TEXT NOT NULL,
                capture_date TEXT NULL,
                state TEXT NOT NULL,
                deleted_at TEXT NULL,
                last_seen TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_files_active
                ON files(source_root, relative_path) WHERE state='Active';
            CREATE TABLE IF NOT EXISTS thumbs (
                hash TEXT PRIMARY KEY,
                ready INTEGER NOT NULL,
                gcs_object TEXT NULL,
                generated_at TEXT NULL);
            CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """);
    }

    // ---- helpers ----
    private static string? Iso(DateTimeOffset? d) => d?.ToUniversalTime().ToString("O");
    private static DateTimeOffset? Parse(object? v) =>
        v is null or DBNull ? null : DateTimeOffset.Parse((string)v).ToUniversalTime();

    private async Task ExecAsync(string sql, params (string, object?)[] ps)
    {
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<object?> ScalarAsync(string sql, params (string, object?)[] ps)
    {
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return await cmd.ExecuteScalarAsync();
    }

    // ---- blobs ----
    public async Task UpsertBlobAsync(BlobRecord b) => await ExecAsync("""
        INSERT INTO blobs(hash,size,storage_class,uploaded_at,refcount,gc_after)
        VALUES($h,$s,$c,$u,$r,$g)
        ON CONFLICT(hash) DO UPDATE SET
            size=$s, storage_class=$c, uploaded_at=$u, refcount=$r, gc_after=$g;
        """, ("$h", b.Hash), ("$s", b.Size), ("$c", b.StorageClass),
             ("$u", Iso(b.UploadedAt)), ("$r", b.RefCount), ("$g", Iso(b.GcAfter)));

    public async Task<BlobRecord?> GetBlobAsync(string hash)
    {
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT hash,size,storage_class,uploaded_at,refcount,gc_after FROM blobs WHERE hash=$h";
        cmd.Parameters.AddWithValue("$h", hash);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        return new BlobRecord(r.GetString(0), r.GetInt64(1), r.GetString(2),
            DateTimeOffset.Parse(r.GetString(3)), r.GetInt32(4), Parse(r.IsDBNull(5) ? null : r.GetString(5)));
    }

    public async Task<int> AdjustRefCountAsync(string hash, int delta)
    {
        await ExecAsync("UPDATE blobs SET refcount = refcount + $d WHERE hash=$h",
            ("$d", delta), ("$h", hash));
        return Convert.ToInt32(await ScalarAsync("SELECT refcount FROM blobs WHERE hash=$h", ("$h", hash)));
    }

    public async Task SetBlobGcAfterAsync(string hash, DateTimeOffset? gcAfter) =>
        await ExecAsync("UPDATE blobs SET gc_after=$g WHERE hash=$h", ("$g", Iso(gcAfter)), ("$h", hash));

    public async Task<IReadOnlyList<BlobRecord>> GetGcCandidatesAsync(DateTimeOffset now)
    {
        var list = new List<BlobRecord>();
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT hash,size,storage_class,uploaded_at,refcount,gc_after FROM blobs WHERE refcount<=0 AND gc_after IS NOT NULL AND gc_after < $n";
        cmd.Parameters.AddWithValue("$n", Iso(now));
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new BlobRecord(r.GetString(0), r.GetInt64(1), r.GetString(2),
                DateTimeOffset.Parse(r.GetString(3)), r.GetInt32(4), Parse(r.GetString(5))));
        return list;
    }

    public async Task DeleteBlobAsync(string hash) =>
        await ExecAsync("DELETE FROM blobs WHERE hash=$h", ("$h", hash));

    // ---- files ----
    public async Task<long> UpsertFileAsync(FileRecord f)
    {
        var existing = await GetActiveFileAsync(f.SourceRoot, f.RelativePath);
        if (existing is null)
        {
            await ExecAsync("""
                INSERT INTO files(source_root,relative_path,hash,size,mtime,capture_date,state,deleted_at,last_seen)
                VALUES($sr,$rp,$h,$s,$m,$cd,$st,$da,$ls);
                """,
                ("$sr", f.SourceRoot), ("$rp", f.RelativePath), ("$h", f.Hash), ("$s", f.Size),
                ("$m", Iso(f.ModifiedUtc)), ("$cd", Iso(f.CaptureDate)), ("$st", f.State.ToString()),
                ("$da", Iso(f.DeletedAt)), ("$ls", Iso(f.LastSeen)));
            return Convert.ToInt64(await ScalarAsync("SELECT last_insert_rowid()"));
        }
        await ExecAsync("""
            UPDATE files SET hash=$h,size=$s,mtime=$m,capture_date=$cd,last_seen=$ls WHERE id=$id;
            """,
            ("$h", f.Hash), ("$s", f.Size), ("$m", Iso(f.ModifiedUtc)),
            ("$cd", Iso(f.CaptureDate)), ("$ls", Iso(f.LastSeen)), ("$id", existing.Id));
        return existing.Id;
    }

    public async Task<FileRecord?> GetActiveFileAsync(string sourceRoot, string relativePath)
    {
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id,source_root,relative_path,hash,size,mtime,capture_date,state,deleted_at,last_seen FROM files WHERE source_root=$sr AND relative_path=$rp AND state='Active'";
        cmd.Parameters.AddWithValue("$sr", sourceRoot);
        cmd.Parameters.AddWithValue("$rp", relativePath);
        await using var r = await cmd.ExecuteReaderAsync();
        return await ReadFileOrNull(r);
    }

    public async Task MarkSeenAsync(long id, DateTimeOffset lastSeen) =>
        await ExecAsync("UPDATE files SET last_seen=$ls WHERE id=$id", ("$ls", Iso(lastSeen)), ("$id", id));

    public async Task<IReadOnlyList<FileRecord>> GetActiveFilesNotSeenSinceAsync(string sourceRoot, DateTimeOffset cutoff)
    {
        var list = new List<FileRecord>();
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id,source_root,relative_path,hash,size,mtime,capture_date,state,deleted_at,last_seen FROM files WHERE source_root=$sr AND state='Active' AND last_seen < $c";
        cmd.Parameters.AddWithValue("$sr", sourceRoot);
        cmd.Parameters.AddWithValue("$c", Iso(cutoff));
        await using var r = await cmd.ExecuteReaderAsync();
        while (await ReadFileOrNull(r) is { } f) list.Add(f);
        return list;
    }

    public async Task SoftDeleteAsync(long id, DateTimeOffset deletedAt) =>
        await ExecAsync("UPDATE files SET state='Deleted', deleted_at=$da WHERE id=$id",
            ("$da", Iso(deletedAt)), ("$id", id));

    public async Task<IReadOnlyList<FileRecord>> ListActiveFilesAsync()
    {
        var list = new List<FileRecord>();
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id,source_root,relative_path,hash,size,mtime,capture_date,state,deleted_at,last_seen FROM files WHERE state='Active' ORDER BY capture_date";
        await using var r = await cmd.ExecuteReaderAsync();
        while (await ReadFileOrNull(r) is { } f) list.Add(f);
        return list;
    }

    private static async Task<FileRecord?> ReadFileOrNull(SqliteDataReader r)
    {
        if (!await r.ReadAsync()) return null;
        return new FileRecord(
            r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4),
            DateTimeOffset.Parse(r.GetString(5)),
            r.IsDBNull(6) ? null : DateTimeOffset.Parse(r.GetString(6)),
            Enum.Parse<FileState>(r.GetString(7)),
            r.IsDBNull(8) ? null : DateTimeOffset.Parse(r.GetString(8)),
            DateTimeOffset.Parse(r.GetString(9)));
    }

    // ---- thumbs ----
    public async Task UpsertThumbAsync(ThumbRecord t) => await ExecAsync("""
        INSERT INTO thumbs(hash,ready,gcs_object,generated_at) VALUES($h,$r,$o,$g)
        ON CONFLICT(hash) DO UPDATE SET ready=$r, gcs_object=$o, generated_at=$g;
        """, ("$h", t.Hash), ("$r", t.Ready ? 1 : 0), ("$o", t.GcsObject), ("$g", Iso(t.GeneratedAt)));

    public async Task<ThumbRecord?> GetThumbAsync(string hash)
    {
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT hash,ready,gcs_object,generated_at FROM thumbs WHERE hash=$h";
        cmd.Parameters.AddWithValue("$h", hash);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        return new ThumbRecord(r.GetString(0), r.GetInt32(1) == 1,
            r.IsDBNull(2) ? null : r.GetString(2),
            r.IsDBNull(3) ? null : DateTimeOffset.Parse(r.GetString(3)));
    }

    public void Dispose() => _conn.Dispose();
    public async ValueTask DisposeAsync() => await _conn.DisposeAsync();
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/PhotoThing.Core.Tests --filter IndexStoreTests`
Expected: PASS (4 tests).

- [ ] **Step 6: Commit**

```bash
git add src/PhotoThing.Core/IndexStore.cs tests/PhotoThing.Core.Tests/TestFixtures.cs tests/PhotoThing.Core.Tests/IndexStoreTests.cs
git commit -m "feat: SQLite index store (blobs, files, thumbs)"
```

---

## Task 7: ThumbnailService (images via ImageSharp, video via abstraction)

**Files:**
- Create: `src/PhotoThing.Core/Thumbnails/IVideoFrameExtractor.cs`
- Create: `src/PhotoThing.Core/Thumbnails/ThumbnailService.cs`
- Create: `src/PhotoThing.Core/Thumbnails/FfmpegVideoFrameExtractor.cs`
- Test: `tests/PhotoThing.Core.Tests/ThumbnailServiceTests.cs`

**Interfaces:**
- Consumes: `ScannedFile`, `MediaKind`.
- Produces:
  - `interface IVideoFrameExtractor { Task<byte[]> ExtractPosterFrameAsync(string videoPath, CancellationToken ct) }` — returns encoded image bytes (JPEG/PNG) of one frame.
  - `ThumbnailService(int maxEdge, IVideoFrameExtractor videoExtractor)` with `Task<byte[]> GenerateJpegAsync(ScannedFile file, CancellationToken ct)` — returns a JPEG ≤ `maxEdge` on its longest side.
  - `FfmpegVideoFrameExtractor(string ffmpegPath="ffmpeg")` implements `IVideoFrameExtractor` by shelling out.

- [ ] **Step 1: Add ImageSharp**

```bash
dotnet add src/PhotoThing.Core package SixLabors.ImageSharp
```

- [ ] **Step 2: Write `IVideoFrameExtractor.cs`**

```csharp
namespace PhotoThing.Core.Thumbnails;

public interface IVideoFrameExtractor
{
    /// Returns encoded image bytes (JPEG or PNG) for a representative frame.
    Task<byte[]> ExtractPosterFrameAsync(string videoPath, CancellationToken ct = default);
}
```

- [ ] **Step 3: Write the failing test `ThumbnailServiceTests.cs`**

```csharp
using FluentAssertions;
using PhotoThing.Core;
using PhotoThing.Core.Thumbnails;
using SixLabors.ImageSharp;
using Xunit;

public class ThumbnailServiceTests
{
    private sealed class FakeExtractor : IVideoFrameExtractor
    {
        public Task<byte[]> ExtractPosterFrameAsync(string videoPath, CancellationToken ct = default)
            => Task.FromResult(MakePng(200, 100));
    }

    private static byte[] MakePng(int w, int h)
    {
        using var img = new Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(w, h);
        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task Image_thumbnail_is_jpeg_and_bounded_to_max_edge()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllBytesAsync(path, MakePng(2000, 1000));
        try
        {
            var svc = new ThumbnailService(maxEdge: 512, new FakeExtractor());
            var file = new ScannedFile("/r", "big.png", path, 0, DateTimeOffset.UnixEpoch, MediaKind.Image);
            var bytes = await svc.GenerateJpegAsync(file);

            using var img = Image.Load(bytes);
            Math.Max(img.Width, img.Height).Should().Be(512);
            img.Width.Should().Be(512); // 2:1 aspect preserved
            img.Height.Should().Be(256);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Video_thumbnail_uses_extractor_frame()
    {
        var svc = new ThumbnailService(maxEdge: 512, new FakeExtractor());
        var file = new ScannedFile("/r", "v.mp4", "/does/not/matter.mp4", 0, DateTimeOffset.UnixEpoch, MediaKind.Video);
        var bytes = await svc.GenerateJpegAsync(file);
        using var img = Image.Load(bytes); // 200x100 from fake, already under 512
        img.Width.Should().Be(200);
    }
}
```

- [ ] **Step 4: Run tests to verify they fail**

Run: `dotnet test tests/PhotoThing.Core.Tests --filter ThumbnailServiceTests`
Expected: FAIL — `ThumbnailService` not defined.

- [ ] **Step 5: Write `ThumbnailService.cs`**

```csharp
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace PhotoThing.Core.Thumbnails;

public sealed class ThumbnailService
{
    private readonly int _maxEdge;
    private readonly IVideoFrameExtractor _videoExtractor;

    public ThumbnailService(int maxEdge, IVideoFrameExtractor videoExtractor)
    {
        _maxEdge = maxEdge;
        _videoExtractor = videoExtractor;
    }

    public async Task<byte[]> GenerateJpegAsync(ScannedFile file, CancellationToken ct = default)
    {
        using var image = file.Kind == MediaKind.Image
            ? await Image.LoadAsync(file.AbsolutePath, ct)
            : Image.Load(await _videoExtractor.ExtractPosterFrameAsync(file.AbsolutePath, ct));

        image.Mutate(x => x.Resize(new ResizeOptions
        {
            Mode = ResizeMode.Max,
            Size = new Size(_maxEdge, _maxEdge),
        }));

        using var ms = new MemoryStream();
        await image.SaveAsJpegAsync(ms, new JpegEncoder { Quality = 82 }, ct);
        return ms.ToArray();
    }
}
```

- [ ] **Step 6: Write `FfmpegVideoFrameExtractor.cs`**

```csharp
using System.Diagnostics;

namespace PhotoThing.Core.Thumbnails;

/// Extracts a poster frame by invoking ffmpeg. Requires ffmpeg on PATH.
public sealed class FfmpegVideoFrameExtractor : IVideoFrameExtractor
{
    private readonly string _ffmpegPath;
    public FfmpegVideoFrameExtractor(string ffmpegPath = "ffmpeg") => _ffmpegPath = ffmpegPath;

    public async Task<byte[]> ExtractPosterFrameAsync(string videoPath, CancellationToken ct = default)
    {
        // Grab one frame ~1s in; write PNG to stdout.
        var psi = new ProcessStartInfo(_ffmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "-ss", "1", "-i", videoPath, "-frames:v", "1", "-f", "image2pipe", "-vcodec", "png", "pipe:1" })
            psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg.");
        using var outBuf = new MemoryStream();
        var copy = proc.StandardOutput.BaseStream.CopyToAsync(outBuf, ct);
        var err = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        await copy;

        if (proc.ExitCode != 0 || outBuf.Length == 0)
            throw new InvalidOperationException($"ffmpeg failed ({proc.ExitCode}): {await err}");
        return outBuf.ToArray();
    }
}
```

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test tests/PhotoThing.Core.Tests --filter ThumbnailServiceTests`
Expected: PASS (2 tests). `FfmpegVideoFrameExtractor` is not exercised here (needs real ffmpeg); it is wired in the app layer.

- [ ] **Step 8: Commit**

```bash
git add src/PhotoThing.Core/Thumbnails tests/PhotoThing.Core.Tests/ThumbnailServiceTests.cs
git commit -m "feat: thumbnail service (ImageSharp + ffmpeg abstraction)"
```

---

## Task 8: BackupService (the orchestrator)

**Files:**
- Create: `src/PhotoThing.Core/BackupService.cs`
- Test: `tests/PhotoThing.Core.Tests/BackupServiceTests.cs`

**Interfaces:**
- Consumes: `FileScanner`, `ContentHasher` (static), `MediaMetadataExtractor`, `ThumbnailService`, `IndexStore`, `IBlobStore`, `ObjectNames`, all model records.
- Produces:
  - `sealed record BackupResult(int Uploaded, int Deduped, int Skipped, int SoftDeleted)`
  - `BackupService(IndexStore index, IBlobStore store, ThumbnailService thumbs, MediaMetadataExtractor meta, FileScanner scanner, int gracePeriodDays)`
  - `Task<BackupResult> BackupAsync(string sourceRoot, DateTimeOffset now, CancellationToken ct = default)`

Behavior contract (drives the tests):
1. New file → thumbnail generated + uploaded to `thumbs/<hash>.jpg` (STANDARD), content uploaded to `blobs/...` (STANDARD), blob row created refcount=1, file row Active. Counts as `Uploaded`.
2. Unchanged file (same path+size+mtime, active row exists) → `MarkSeen`, no upload. Counts as `Skipped`.
3. New logical file whose content hash already has a blob → no content upload; refcount incremented; file row created. Counts as `Deduped`.
4. Active file absent from this scan → `SoftDelete`, blob refcount decremented and `gc_after = now + grace`. Counts as `SoftDeleted`.

- [ ] **Step 1: Write the failing test `BackupServiceTests.cs`**

```csharp
using System.Text;
using FluentAssertions;
using PhotoThing.Core;
using PhotoThing.Core.Thumbnails;
using SixLabors.ImageSharp;
using Xunit;

public class BackupServiceTests
{
    private sealed class FakeExtractor : IVideoFrameExtractor
    {
        public Task<byte[]> ExtractPosterFrameAsync(string p, CancellationToken ct = default)
            => throw new NotSupportedException("no videos in these tests");
    }

    private static async Task<string> WritePngAsync(string dir, string name, int w, int h)
    {
        var path = Path.Combine(dir, name);
        using var img = new Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(w, h);
        await img.SaveAsPngAsync(path);
        return path;
    }

    private static BackupService NewService(IndexStore idx, InMemoryBlobStore store) =>
        new(idx, store, new ThumbnailService(512, new FakeExtractor()),
            new MediaMetadataExtractor(), new FileScanner(), gracePeriodDays: 30);

    private static readonly DateTimeOffset Now = new(2026, 9, 5, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task First_backup_uploads_content_and_thumbnail()
    {
        await using var ws = new TempWorkspace();
        await using var idx = await ws.OpenIndexAsync();
        var store = new InMemoryBlobStore();
        await WritePngAsync(ws.Root, "a.png", 10, 10);

        var result = await NewService(idx, store).BackupAsync(ws.Root, Now);

        result.Uploaded.Should().Be(1);
        store.Objects.Keys.Should().Contain(k => k.StartsWith("blobs/"));
        store.Objects.Keys.Should().Contain(k => k.StartsWith("thumbs/"));
        (await idx.ListActiveFilesAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task Rerun_with_no_changes_skips_everything()
    {
        await using var ws = new TempWorkspace();
        await using var idx = await ws.OpenIndexAsync();
        var store = new InMemoryBlobStore();
        await WritePngAsync(ws.Root, "a.png", 10, 10);

        await NewService(idx, store).BackupAsync(ws.Root, Now);
        var second = await NewService(idx, store).BackupAsync(ws.Root, Now.AddMinutes(1));

        second.Skipped.Should().Be(1);
        second.Uploaded.Should().Be(0);
    }

    [Fact]
    public async Task Identical_content_second_path_is_deduped()
    {
        await using var ws = new TempWorkspace();
        await using var idx = await ws.OpenIndexAsync();
        var store = new InMemoryBlobStore();
        var p1 = await WritePngAsync(ws.Root, "a.png", 10, 10);
        File.Copy(p1, Path.Combine(ws.Root, "copy.png")); // identical bytes

        var result = await NewService(idx, store).BackupAsync(ws.Root, Now);

        result.Uploaded.Should().Be(1);
        result.Deduped.Should().Be(1);
        store.Objects.Keys.Count(k => k.StartsWith("blobs/")).Should().Be(1); // one blob
        var files = await idx.ListActiveFilesAsync();
        var hash = files.First().Hash;
        (await idx.GetBlobAsync(hash))!.RefCount.Should().Be(2);
    }

    [Fact]
    public async Task Removed_file_is_soft_deleted_and_blob_scheduled_for_gc()
    {
        await using var ws = new TempWorkspace();
        await using var idx = await ws.OpenIndexAsync();
        var store = new InMemoryBlobStore();
        var p1 = await WritePngAsync(ws.Root, "a.png", 10, 10);
        await NewService(idx, store).BackupAsync(ws.Root, Now);
        var hash = (await idx.ListActiveFilesAsync()).First().Hash;

        File.Delete(p1);
        var result = await NewService(idx, store).BackupAsync(ws.Root, Now.AddDays(1));

        result.SoftDeleted.Should().Be(1);
        (await idx.ListActiveFilesAsync()).Should().BeEmpty();
        var blob = (await idx.GetBlobAsync(hash))!;
        blob.RefCount.Should().Be(0);
        blob.GcAfter.Should().Be(Now.AddDays(1).AddDays(30));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PhotoThing.Core.Tests --filter BackupServiceTests`
Expected: FAIL — `BackupService` not defined.

- [ ] **Step 3: Write `BackupService.cs`**

```csharp
using PhotoThing.Core.Thumbnails;

namespace PhotoThing.Core;

public sealed record BackupResult(int Uploaded, int Deduped, int Skipped, int SoftDeleted);

public sealed class BackupService
{
    private readonly IndexStore _index;
    private readonly IBlobStore _store;
    private readonly ThumbnailService _thumbs;
    private readonly MediaMetadataExtractor _meta;
    private readonly FileScanner _scanner;
    private readonly int _graceDays;

    public BackupService(IndexStore index, IBlobStore store, ThumbnailService thumbs,
        MediaMetadataExtractor meta, FileScanner scanner, int gracePeriodDays)
    {
        _index = index; _store = store; _thumbs = thumbs;
        _meta = meta; _scanner = scanner; _graceDays = gracePeriodDays;
    }

    public async Task<BackupResult> BackupAsync(string sourceRoot, DateTimeOffset now, CancellationToken ct = default)
    {
        var root = Path.GetFullPath(sourceRoot);
        int uploaded = 0, deduped = 0, skipped = 0, softDeleted = 0;

        foreach (var file in _scanner.Scan(root))
        {
            ct.ThrowIfCancellationRequested();

            var existing = await _index.GetActiveFileAsync(root, file.RelativePath);
            if (existing is not null && existing.Size == file.Size && existing.ModifiedUtc == file.ModifiedUtc)
            {
                await _index.MarkSeenAsync(existing.Id, now);
                skipped++;
                continue;
            }

            var hash = await ContentHasher.HashFileAsync(file.AbsolutePath, ct);
            var blob = await _index.GetBlobAsync(hash);

            if (blob is null)
            {
                await UploadNewBlobAsync(file, hash, now, ct);
                uploaded++;
            }
            else
            {
                await _index.AdjustRefCountAsync(hash, +1);
                // A revived blob should no longer be scheduled for GC.
                await _index.SetBlobGcAfterAsync(hash, null);
                deduped++;
            }

            await _index.UpsertFileAsync(new FileRecord(
                0, root, file.RelativePath, hash, file.Size, file.ModifiedUtc,
                _meta.GetCaptureDate(file), FileState.Active, null, now));
        }

        softDeleted = await SoftDeleteUnseenAsync(root, now, ct);
        return new BackupResult(uploaded, deduped, skipped, softDeleted);
    }

    private async Task UploadNewBlobAsync(ScannedFile file, string hash, DateTimeOffset now, CancellationToken ct)
    {
        var thumbBytes = await _thumbs.GenerateJpegAsync(file, ct);
        using (var thumbStream = new MemoryStream(thumbBytes))
            await _store.PutAsync(ObjectNames.Thumb(hash), thumbStream, "image/jpeg", storageClass: "STANDARD", ct: ct);
        await _index.UpsertThumbAsync(new ThumbRecord(hash, true, ObjectNames.Thumb(hash), now));

        await using (var content = File.OpenRead(file.AbsolutePath))
            await _store.PutAsync(
                ObjectNames.Blob(hash), content, "application/octet-stream",
                metadata: new Dictionary<string, string> { ["filename"] = Path.GetFileName(file.RelativePath) },
                storageClass: "STANDARD", ct: ct);

        await _index.UpsertBlobAsync(new BlobRecord(hash, file.Size, "STANDARD", now, RefCount: 1, GcAfter: null));
    }

    private async Task<int> SoftDeleteUnseenAsync(string root, DateTimeOffset now, CancellationToken ct)
    {
        var stale = await _index.GetActiveFilesNotSeenSinceAsync(root, now);
        foreach (var f in stale)
        {
            ct.ThrowIfCancellationRequested();
            await _index.SoftDeleteAsync(f.Id, now);
            var newCount = await _index.AdjustRefCountAsync(f.Hash, -1);
            if (newCount <= 0)
                await _index.SetBlobGcAfterAsync(f.Hash, now.AddDays(_graceDays));
        }
        return stale.Count;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PhotoThing.Core.Tests --filter BackupServiceTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/PhotoThing.Core/BackupService.cs tests/PhotoThing.Core.Tests/BackupServiceTests.cs
git commit -m "feat: backup orchestrator (upload, dedup, skip, soft-delete)"
```

---

## Task 9: GarbageCollector

**Files:**
- Create: `src/PhotoThing.Core/GarbageCollector.cs`
- Test: `tests/PhotoThing.Core.Tests/GarbageCollectorTests.cs`

**Interfaces:**
- Consumes: `IndexStore`, `IBlobStore`, `ObjectNames`, `BlobRecord`.
- Produces: `GarbageCollector(IndexStore, IBlobStore)` with `Task<int> CollectAsync(DateTimeOffset now, CancellationToken ct=default)` — for each GC candidate, deletes `blobs/...` and `thumbs/...` objects and the blob (and thumb) rows; returns count collected.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Text;
using FluentAssertions;
using PhotoThing.Core;
using Xunit;

public class GarbageCollectorTests
{
    [Fact]
    public async Task Collects_due_zero_refcount_blobs_and_deletes_objects()
    {
        await using var ws = new TempWorkspace();
        await using var idx = await ws.OpenIndexAsync();
        var store = new InMemoryBlobStore();
        var now = new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero);

        // A due blob + its objects.
        var hash = "deadbeefdeadbeef";
        await store.PutAsync(ObjectNames.Blob(hash), new MemoryStream([1]), "b");
        await store.PutAsync(ObjectNames.Thumb(hash), new MemoryStream([2]), "image/jpeg");
        await idx.UpsertBlobAsync(new BlobRecord(hash, 1, "STANDARD", now, 0, now.AddDays(-1)));
        await idx.UpsertThumbAsync(new ThumbRecord(hash, true, ObjectNames.Thumb(hash), now));

        // A blob that must survive (still referenced).
        var keep = "cafecafecafecafe";
        await store.PutAsync(ObjectNames.Blob(keep), new MemoryStream([3]), "b");
        await idx.UpsertBlobAsync(new BlobRecord(keep, 1, "STANDARD", now, 1, null));

        var collected = await new GarbageCollector(idx, store).CollectAsync(now);

        collected.Should().Be(1);
        (await store.ExistsAsync(ObjectNames.Blob(hash))).Should().BeFalse();
        (await store.ExistsAsync(ObjectNames.Thumb(hash))).Should().BeFalse();
        (await idx.GetBlobAsync(hash)).Should().BeNull();
        (await store.ExistsAsync(ObjectNames.Blob(keep))).Should().BeTrue();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PhotoThing.Core.Tests --filter GarbageCollectorTests`
Expected: FAIL — `GarbageCollector` not defined.

- [ ] **Step 3: Write `GarbageCollector.cs`**

```csharp
namespace PhotoThing.Core;

public sealed class GarbageCollector
{
    private readonly IndexStore _index;
    private readonly IBlobStore _store;

    public GarbageCollector(IndexStore index, IBlobStore store)
    {
        _index = index; _store = store;
    }

    public async Task<int> CollectAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var candidates = await _index.GetGcCandidatesAsync(now);
        foreach (var blob in candidates)
        {
            ct.ThrowIfCancellationRequested();
            await _store.DeleteAsync(ObjectNames.Blob(blob.Hash), ct);
            await _store.DeleteAsync(ObjectNames.Thumb(blob.Hash), ct);
            await _index.DeleteBlobAsync(blob.Hash);
        }
        return candidates.Count;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PhotoThing.Core.Tests --filter GarbageCollectorTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/PhotoThing.Core/GarbageCollector.cs tests/PhotoThing.Core.Tests/GarbageCollectorTests.cs
git commit -m "feat: garbage collector for expired unreferenced blobs"
```

---

## Task 10: RestoreService

**Files:**
- Create: `src/PhotoThing.Core/RestoreService.cs`
- Test: `tests/PhotoThing.Core.Tests/RestoreServiceTests.cs`

**Interfaces:**
- Consumes: `IndexStore`, `IBlobStore`, `ObjectNames`, `FileRecord`.
- Produces: `RestoreService(IndexStore, IBlobStore)` with:
  - `Task<IReadOnlyList<FileRecord>> BrowseAsync()` — active files ordered by capture date.
  - `Task RestoreAsync(FileRecord file, string destinationDir, CancellationToken ct=default)` — downloads the blob and writes it to `destinationDir` preserving the relative path.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Text;
using FluentAssertions;
using PhotoThing.Core;
using Xunit;

public class RestoreServiceTests
{
    [Fact]
    public async Task Restore_downloads_blob_to_destination_preserving_relative_path()
    {
        await using var ws = new TempWorkspace();
        await using var idx = await ws.OpenIndexAsync();
        var store = new InMemoryBlobStore();
        var now = DateTimeOffset.UnixEpoch;

        var hash = "0011223344556677";
        var payload = Encoding.ASCII.GetBytes("original-bytes");
        await store.PutAsync(ObjectNames.Blob(hash), new MemoryStream(payload), "application/octet-stream");
        var id = await idx.UpsertFileAsync(new FileRecord(
            0, "/root", "sub/photo.jpg", hash, payload.Length, now, now, FileState.Active, null, now));
        var file = (await idx.ListActiveFilesAsync()).Single(f => f.Id == id);

        var dest = Path.Combine(ws.Root, "restored");
        await new RestoreService(idx, store).RestoreAsync(file, dest);

        var outPath = Path.Combine(dest, "sub", "photo.jpg");
        File.Exists(outPath).Should().BeTrue();
        (await File.ReadAllBytesAsync(outPath)).Should().Equal(payload);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PhotoThing.Core.Tests --filter RestoreServiceTests`
Expected: FAIL — `RestoreService` not defined.

- [ ] **Step 3: Write `RestoreService.cs`**

```csharp
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

        await using var src = await _store.GetAsync(ObjectNames.Blob(file.Hash), ct);
        await using var dst = File.Create(outPath);
        await src.CopyToAsync(dst, ct);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PhotoThing.Core.Tests --filter RestoreServiceTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/PhotoThing.Core/RestoreService.cs tests/PhotoThing.Core.Tests/RestoreServiceTests.cs
git commit -m "feat: restore service (browse + download)"
```

---

## Task 11: ReconcileService (index vs GCS drift)

**Files:**
- Create: `src/PhotoThing.Core/ReconcileService.cs`
- Test: `tests/PhotoThing.Core.Tests/ReconcileServiceTests.cs`

**Interfaces:**
- Consumes: `IndexStore`, `IBlobStore`, `ObjectNames`.
- Produces:
  - `sealed record ReconcileReport(IReadOnlyList<string> MissingInGcs, IReadOnlyList<string> OrphanedInGcs)` (both lists are content hashes).
  - `ReconcileService(IndexStore, IBlobStore)` with `Task<ReconcileReport> ReconcileAsync(CancellationToken ct=default)`.
    - `MissingInGcs`: blob hashes in the index whose `blobs/...` object does not exist.
    - `OrphanedInGcs`: `blobs/...` objects present in GCS with no matching index blob row.

- [ ] **Step 1: Write the failing test**

```csharp
using FluentAssertions;
using PhotoThing.Core;
using Xunit;

public class ReconcileServiceTests
{
    [Fact]
    public async Task Reports_missing_and_orphaned_blobs()
    {
        await using var ws = new TempWorkspace();
        await using var idx = await ws.OpenIndexAsync();
        var store = new InMemoryBlobStore();
        var now = DateTimeOffset.UnixEpoch;

        // Indexed + present  -> healthy
        var ok = "aaaaaaaaaaaaaaaa";
        await idx.UpsertBlobAsync(new BlobRecord(ok, 1, "STANDARD", now, 1, null));
        await store.PutAsync(ObjectNames.Blob(ok), new MemoryStream([1]), "b");

        // Indexed but object gone -> missing
        var missing = "bbbbbbbbbbbbbbbb";
        await idx.UpsertBlobAsync(new BlobRecord(missing, 1, "STANDARD", now, 1, null));

        // Object present but not indexed -> orphaned
        var orphan = "cccccccccccccccc";
        await store.PutAsync(ObjectNames.Blob(orphan), new MemoryStream([1]), "b");

        var report = await new ReconcileService(idx, store).ReconcileAsync();

        report.MissingInGcs.Should().BeEquivalentTo(new[] { missing });
        report.OrphanedInGcs.Should().BeEquivalentTo(new[] { orphan });
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PhotoThing.Core.Tests --filter ReconcileServiceTests`
Expected: FAIL — `ReconcileService` not defined.

- [ ] **Step 3: Write `ReconcileService.cs`**

```csharp
namespace PhotoThing.Core;

public sealed record ReconcileReport(
    IReadOnlyList<string> MissingInGcs,
    IReadOnlyList<string> OrphanedInGcs);

public sealed class ReconcileService
{
    private readonly IndexStore _index;
    private readonly IBlobStore _store;

    public ReconcileService(IndexStore index, IBlobStore store)
    {
        _index = index; _store = store;
    }

    public async Task<ReconcileReport> ReconcileAsync(CancellationToken ct = default)
    {
        // Hashes present as objects in GCS.
        var inGcs = new HashSet<string>();
        await foreach (var name in _store.ListAsync("blobs/", ct))
            inGcs.Add(name[(name.LastIndexOf('/') + 1)..]);

        // Hashes the index believes exist.
        var indexed = new HashSet<string>(
            (await _index.ListActiveFilesAsync()).Select(f => f.Hash));
        // Include blobs referenced by GC-scheduled/zero-ref rows too, via a direct query:
        foreach (var h in await GetAllBlobHashesAsync())
            indexed.Add(h);

        var missing = indexed.Where(h => !inGcs.Contains(h)).OrderBy(h => h).ToList();
        var orphaned = inGcs.Where(h => !indexed.Contains(h)).OrderBy(h => h).ToList();
        return new ReconcileReport(missing, orphaned);
    }

    private async Task<IReadOnlyList<string>> GetAllBlobHashesAsync()
    {
        // Reuse GetGcCandidatesAsync with a far-future cutoff would only return due ones;
        // instead expose all via a dedicated helper on IndexStore.
        return await _index.GetAllBlobHashesAsync();
    }
}
```

- [ ] **Step 4: Add the supporting `IndexStore.GetAllBlobHashesAsync`**

Add to `src/PhotoThing.Core/IndexStore.cs` (in the blobs region):

```csharp
public async Task<IReadOnlyList<string>> GetAllBlobHashesAsync()
{
    var list = new List<string>();
    await using var cmd = _conn.CreateCommand();
    cmd.CommandText = "SELECT hash FROM blobs";
    await using var r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync()) list.Add(r.GetString(0));
    return list;
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/PhotoThing.Core.Tests --filter ReconcileServiceTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/PhotoThing.Core/ReconcileService.cs src/PhotoThing.Core/IndexStore.cs tests/PhotoThing.Core.Tests/ReconcileServiceTests.cs
git commit -m "feat: reconcile service detecting index/GCS drift"
```

---

## Task 12: AppSettings + index snapshot upload

**Files:**
- Create: `src/PhotoThing.Core/AppSettings.cs`
- Modify: `src/PhotoThing.Core/BackupService.cs` (add snapshot upload at end of `BackupAsync`)
- Test: `tests/PhotoThing.Core.Tests/AppSettingsTests.cs`, extend `BackupServiceTests.cs`

**Interfaces:**
- Produces:
  - `sealed record AppSettings(string? ProjectId, string BucketName, IReadOnlyList<string> SourceRoots, int GracePeriodDays=30, int ArchiveAfterDays=180, int ThumbnailMaxEdge=512, int MaxParallelUploads=4, string FfmpegPath="ffmpeg")` with `static AppSettings Load(string path)` and `void Save(string path)` (JSON).
  - `BackupService.SnapshotIndexAsync(string dbPath, string utcStamp, CancellationToken)` — uploads the DB file to `index/snapshot-<stamp>.db` and copies to `index/latest.db`.

- [ ] **Step 1: Write the failing test `AppSettingsTests.cs`**

```csharp
using FluentAssertions;
using PhotoThing.Core;
using Xunit;

public class AppSettingsTests
{
    [Fact]
    public void Save_then_load_roundtrips_all_fields()
    {
        var path = Path.Combine(Path.GetTempPath(), "pt-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var s = new AppSettings(
                ProjectId: "my-proj", BucketName: "my-bucket",
                SourceRoots: new[] { "/a", "/b" },
                GracePeriodDays: 15, ArchiveAfterDays: 90,
                ThumbnailMaxEdge: 256, MaxParallelUploads: 8, FfmpegPath: "/usr/bin/ffmpeg");
            s.Save(path);
            var loaded = AppSettings.Load(path);
            loaded.Should().BeEquivalentTo(s);
        }
        finally { File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PhotoThing.Core.Tests --filter AppSettingsTests`
Expected: FAIL — `AppSettings` not defined.

- [ ] **Step 3: Write `AppSettings.cs`**

```csharp
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
    string FfmpegPath = "ffmpeg")
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
```

- [ ] **Step 4: Write the failing snapshot test (add to `BackupServiceTests.cs`)**

```csharp
[Fact]
public async Task SnapshotIndex_uploads_snapshot_and_latest()
{
    await using var ws = new TempWorkspace();
    await using var idx = await ws.OpenIndexAsync();
    var store = new InMemoryBlobStore();
    var svc = NewService(idx, store);

    await svc.SnapshotIndexAsync(ws.DbPath, "20260905T101500Z");

    store.Objects.Keys.Should().Contain("index/snapshot-20260905T101500Z.db");
    store.Objects.Keys.Should().Contain("index/latest.db");
}
```

- [ ] **Step 5: Run test to verify it fails**

Run: `dotnet test tests/PhotoThing.Core.Tests --filter BackupServiceTests.SnapshotIndex_uploads_snapshot_and_latest`
Expected: FAIL — `SnapshotIndexAsync` not defined.

- [ ] **Step 6: Add `SnapshotIndexAsync` to `BackupService.cs`**

```csharp
public async Task SnapshotIndexAsync(string dbPath, string utcStamp, CancellationToken ct = default)
{
    // Copy first so we upload a stable file even while the live DB is open (WAL).
    var temp = Path.Combine(Path.GetTempPath(), $"pt-snap-{utcStamp}.db");
    File.Copy(dbPath, temp, overwrite: true);
    try
    {
        await using (var s = File.OpenRead(temp))
            await _store.PutAsync(ObjectNames.Snapshot(utcStamp), s, "application/x-sqlite3", storageClass: "STANDARD", ct: ct);
        await using (var s = File.OpenRead(temp))
            await _store.PutAsync(ObjectNames.LatestIndex, s, "application/x-sqlite3", storageClass: "STANDARD", ct: ct);
    }
    finally { File.Delete(temp); }
}
```

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test tests/PhotoThing.Core.Tests --filter AppSettingsTests`
then: `dotnet test tests/PhotoThing.Core.Tests --filter BackupServiceTests`
Expected: PASS (both).

- [ ] **Step 8: Commit**

```bash
git add src/PhotoThing.Core/AppSettings.cs src/PhotoThing.Core/BackupService.cs tests/PhotoThing.Core.Tests
git commit -m "feat: app settings + index snapshot upload"
```

---

## Task 13: GcsBlobStore + LifecycleManager (real GCS via ADC)

**Files:**
- Create: `src/PhotoThing.Core/Gcs/GcsBlobStore.cs`
- Create: `src/PhotoThing.Core/Gcs/LifecycleManager.cs`
- Test: `tests/PhotoThing.Core.Tests/LifecycleRuleTests.cs` (unit-tests the rule-building only)

**Interfaces:**
- Consumes: `IBlobStore`.
- Produces:
  - `static Task<GcsBlobStore> CreateAsync(string bucket, string? projectId=null, CancellationToken ct=default)` — builds a `StorageClient` from ADC (`GoogleCredential.GetApplicationDefaultAsync`). Implements every `IBlobStore` member.
  - `LifecycleManager` with `static LifecycleRuleData BuildArchiveAfterRule(int days)` returning the rule object, and `SetLifecycleArchiveAfterAsync` delegating through the store (the interface method on `GcsBlobStore`).

> **Note:** `GcsBlobStore` is exercised by the app and (optionally) integration tests against a real bucket or `fake-gcs-server`; it is not unit-tested offline. Only the pure rule-building logic is unit-tested here.

- [ ] **Step 1: Add the GCS package**

```bash
dotnet add src/PhotoThing.Core package Google.Cloud.Storage.V1
```

- [ ] **Step 2: Write the failing test `LifecycleRuleTests.cs`**

```csharp
using FluentAssertions;
using PhotoThing.Core.Gcs;
using Xunit;

public class LifecycleRuleTests
{
    [Fact]
    public void Archive_rule_targets_blobs_prefix_and_age()
    {
        var rule = LifecycleManager.BuildArchiveAfterRule(180);
        rule.Action.Type.Should().Be("SetStorageClass");
        rule.Action.StorageClass.Should().Be("ARCHIVE");
        rule.Condition.Age.Should().Be(180);
        rule.Condition.MatchesPrefix.Should().Contain("blobs/");
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/PhotoThing.Core.Tests --filter LifecycleRuleTests`
Expected: FAIL — `LifecycleManager` not defined.

- [ ] **Step 4: Write `LifecycleManager.cs`**

```csharp
using Google.Apis.Storage.v1.Data;

namespace PhotoThing.Core.Gcs;

/// The lifecycle rule payload type from the Storage v1 data model.
public sealed record LifecycleRuleData(
    LifecycleAction Action,
    LifecycleCondition Condition);

public sealed record LifecycleAction(string Type, string? StorageClass);
public sealed record LifecycleCondition(int Age, IReadOnlyList<string> MatchesPrefix);

public static class LifecycleManager
{
    public static LifecycleRuleData BuildArchiveAfterRule(int days) =>
        new(
            new LifecycleAction("SetStorageClass", "ARCHIVE"),
            new LifecycleCondition(days, new[] { "blobs/" }));

    /// Convert our POCO into the Google client's Bucket.LifecycleData.RuleData.
    public static Bucket.LifecycleData ToGoogleLifecycle(LifecycleRuleData rule) => new()
    {
        Rule = new List<Bucket.LifecycleData.RuleData>
        {
            new()
            {
                Action = new Bucket.LifecycleData.RuleData.ActionData
                {
                    Type = rule.Action.Type,
                    StorageClass = rule.Action.StorageClass,
                },
                Condition = new Bucket.LifecycleData.RuleData.ConditionData
                {
                    Age = rule.Condition.Age,
                    MatchesPrefix = rule.Condition.MatchesPrefix.ToList(),
                },
            },
        },
    };
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/PhotoThing.Core.Tests --filter LifecycleRuleTests`
Expected: PASS.

- [ ] **Step 6: Write `GcsBlobStore.cs`** (no unit test; verified via the app / integration)

```csharp
using System.Runtime.CompilerServices;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Storage.v1.Data;
using Google.Cloud.Storage.V1;

namespace PhotoThing.Core.Gcs;

public sealed class GcsBlobStore : IBlobStore, IDisposable
{
    private readonly StorageClient _client;
    private readonly string _bucket;

    private GcsBlobStore(StorageClient client, string bucket)
    {
        _client = client; _bucket = bucket;
    }

    public static async Task<GcsBlobStore> CreateAsync(string bucket, string? projectId = null, CancellationToken ct = default)
    {
        var credential = await GoogleCredential.GetApplicationDefaultAsync(ct);
        var client = await StorageClient.CreateAsync(credential);
        return new GcsBlobStore(client, bucket);
    }

    public async Task<bool> ExistsAsync(string objectName, CancellationToken ct = default)
    {
        try { await _client.GetObjectAsync(_bucket, objectName, cancellationToken: ct); return true; }
        catch (Google.GoogleApiException e) when (e.HttpStatusCode == System.Net.HttpStatusCode.NotFound) { return false; }
    }

    public async Task PutAsync(string objectName, Stream content, string contentType,
        IReadOnlyDictionary<string, string>? metadata = null, string? storageClass = null,
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

    public async Task<Stream> GetAsync(string objectName, CancellationToken ct = default)
    {
        var ms = new MemoryStream();
        await _client.DownloadObjectAsync(_bucket, objectName, ms, cancellationToken: ct);
        ms.Position = 0;
        return ms;
    }

    public async Task DeleteAsync(string objectName, CancellationToken ct = default)
    {
        try { await _client.DeleteObjectAsync(_bucket, objectName, cancellationToken: ct); }
        catch (Google.GoogleApiException e) when (e.HttpStatusCode == System.Net.HttpStatusCode.NotFound) { /* already gone */ }
    }

    public async IAsyncEnumerable<string> ListAsync(string prefix, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var o in _client.ListObjectsAsync(_bucket, prefix).WithCancellation(ct))
            yield return o.Name;
    }

    public async Task SetLifecycleArchiveAfterAsync(int days, CancellationToken ct = default)
    {
        var bucket = await _client.GetBucketAsync(_bucket, cancellationToken: ct);
        bucket.Lifecycle = LifecycleManager.ToGoogleLifecycle(LifecycleManager.BuildArchiveAfterRule(days));
        await _client.UpdateBucketAsync(bucket, cancellationToken: ct);
    }

    public void Dispose() => _client.Dispose();
}
```

- [ ] **Step 7: Build to verify the Google types compile**

Run: `dotnet build src/PhotoThing.Core`
Expected: build succeeds. If a `Bucket.LifecycleData` nested type name differs by package version, adjust `ToGoogleLifecycle` to match the installed `Google.Apis.Storage.v1.Data` shape (the property names `Action`/`Condition`/`Type`/`StorageClass`/`Age`/`MatchesPrefix` are stable).

- [ ] **Step 8: Commit**

```bash
git add src/PhotoThing.Core/Gcs tests/PhotoThing.Core.Tests/LifecycleRuleTests.cs
git commit -m "feat: real GCS blob store (ADC) + lifecycle manager"
```

---

## Task 14: Avalonia app scaffold + DI + preflight checks

**Files:**
- Create: `src/PhotoThing.App/PhotoThing.App.csproj`, `Program.cs`, `App.axaml`, `App.axaml.cs`, `ViewLocator.cs`
- Create: `src/PhotoThing.App/Services/AppServices.cs` (composition root + `Preflight`)
- Create: `src/PhotoThing.App/Services/SettingsPaths.cs`
- Modify: `PhotoThing.sln` (add project)

**Interfaces:**
- Consumes: all Core services, `AppSettings`, `GcsBlobStore`.
- Produces:
  - `SettingsPaths.SettingsFile` / `SettingsPaths.IndexDb` — absolute paths under `~/Library/Application Support/PhotoThing/`.
  - `AppServices` composition root: given `AppSettings`, constructs `IndexStore`, `GcsBlobStore`, `ThumbnailService`, `BackupService`, `RestoreService`, `GarbageCollector`, `ReconcileService`.
  - `Preflight.CheckAsync(AppSettings) -> PreflightResult(bool AdcOk, bool FfmpegOk, bool BucketOk, string? Message)`.

- [ ] **Step 1: Scaffold the Avalonia project**

```bash
dotnet new install Avalonia.Templates
dotnet new avalonia.mvvm -n PhotoThing.App -o src/PhotoThing.App
dotnet sln add src/PhotoThing.App
dotnet add src/PhotoThing.App reference src/PhotoThing.Core
dotnet add src/PhotoThing.App package CommunityToolkit.Mvvm
```

- [ ] **Step 2: Write `Services/SettingsPaths.cs`**

```csharp
namespace PhotoThing.App.Services;

public static class SettingsPaths
{
    private static string Base =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PhotoThing");

    public static string SettingsFile => Path.Combine(Base, "settings.json");
    public static string IndexDb => Path.Combine(Base, "index.db");
    public static string ThumbCache => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoThing", "thumbs");
}
```

- [ ] **Step 3: Write `Services/AppServices.cs`**

```csharp
using System.Diagnostics;
using PhotoThing.Core;
using PhotoThing.Core.Gcs;
using PhotoThing.Core.Thumbnails;

namespace PhotoThing.App.Services;

public sealed record PreflightResult(bool AdcOk, bool FfmpegOk, bool BucketOk, string? Message);

public sealed class AppServices : IAsyncDisposable
{
    public IndexStore Index { get; }
    public GcsBlobStore Store { get; }
    public BackupService Backup { get; }
    public RestoreService Restore { get; }
    public GarbageCollector Gc { get; }
    public ReconcileService Reconcile { get; }
    public AppSettings Settings { get; }

    private AppServices(AppSettings s, IndexStore index, GcsBlobStore store)
    {
        Settings = s; Index = index; Store = store;
        var thumbs = new ThumbnailService(s.ThumbnailMaxEdge, new FfmpegVideoFrameExtractor(s.FfmpegPath));
        Backup = new BackupService(index, store, thumbs, new MediaMetadataExtractor(), new FileScanner(), s.GracePeriodDays);
        Restore = new RestoreService(index, store);
        Gc = new GarbageCollector(index, store);
        Reconcile = new ReconcileService(index, store);
    }

    public static async Task<AppServices> CreateAsync(AppSettings s, CancellationToken ct = default)
    {
        var index = await IndexStore.OpenAsync(SettingsPaths.IndexDb);
        var store = await GcsBlobStore.CreateAsync(s.BucketName, s.ProjectId, ct);
        return new AppServices(s, index, store);
    }

    public async ValueTask DisposeAsync()
    {
        await Index.DisposeAsync();
        Store.Dispose();
    }
}

public static class Preflight
{
    public static async Task<PreflightResult> CheckAsync(AppSettings s, CancellationToken ct = default)
    {
        bool ffmpeg = TryRun(s.FfmpegPath, "-version");
        bool adc = false, bucket = false;
        string? msg = null;
        try
        {
            var store = await GcsBlobStore.CreateAsync(s.BucketName, s.ProjectId, ct);
            adc = true;
            // A cheap list confirms bucket access.
            await foreach (var _ in store.ListAsync("index/", ct)) break;
            bucket = true;
            store.Dispose();
        }
        catch (Exception e)
        {
            msg = e.Message;
        }
        if (!adc) msg ??= "ADC not found. Run: gcloud auth application-default login";
        if (!ffmpeg) msg ??= $"ffmpeg not found at '{s.FfmpegPath}'. Install ffmpeg and set its path in Settings.";
        return new PreflightResult(adc, ffmpeg, bucket, msg);
    }

    private static bool TryRun(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            if (p is null) return false;
            p.WaitForExit(5000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch { return false; }
    }
}
```

- [ ] **Step 4: Build to verify wiring compiles**

Run: `dotnet build src/PhotoThing.App`
Expected: build succeeds (Avalonia template's default `MainWindow`/`MainWindowViewModel` remain for now).

- [ ] **Step 5: Commit**

```bash
git add src/PhotoThing.App PhotoThing.sln
git commit -m "feat: Avalonia app scaffold, composition root, preflight checks"
```

---

## Task 15: Settings/Setup view (project, bucket, folders, status)

**Files:**
- Create: `src/PhotoThing.App/ViewModels/SettingsViewModel.cs`
- Create: `src/PhotoThing.App/Views/SettingsView.axaml` (+ `.axaml.cs`)
- Modify: `src/PhotoThing.App/ViewModels/MainWindowViewModel.cs`, `Views/MainWindow.axaml` (host a tab/nav for Settings/Backup/Browse)

**Interfaces:**
- Consumes: `AppSettings`, `SettingsPaths`, `Preflight`.
- Produces: `SettingsViewModel` with observable `ProjectId`, `BucketName`, `SourceRoots` (ObservableCollection<string>), numeric defaults, `FfmpegPath`; commands `AddFolderCommand`, `RemoveFolderCommand`, `SaveCommand`, `RunPreflightCommand`; and a `StatusMessage`. Persists to `SettingsPaths.SettingsFile`.

- [ ] **Step 1: Write `SettingsViewModel.cs`**

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoThing.App.Services;
using PhotoThing.Core;

namespace PhotoThing.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    [ObservableProperty] private string? _projectId;
    [ObservableProperty] private string _bucketName = "";
    [ObservableProperty] private string _ffmpegPath = "ffmpeg";
    [ObservableProperty] private int _gracePeriodDays = 30;
    [ObservableProperty] private int _archiveAfterDays = 180;
    [ObservableProperty] private string? _statusMessage;

    public ObservableCollection<string> SourceRoots { get; } = new();

    public SettingsViewModel() => TryLoad();

    private void TryLoad()
    {
        if (!File.Exists(SettingsPaths.SettingsFile)) return;
        var s = AppSettings.Load(SettingsPaths.SettingsFile);
        ProjectId = s.ProjectId; BucketName = s.BucketName; FfmpegPath = s.FfmpegPath;
        GracePeriodDays = s.GracePeriodDays; ArchiveAfterDays = s.ArchiveAfterDays;
        SourceRoots.Clear();
        foreach (var r in s.SourceRoots) SourceRoots.Add(r);
    }

    public AppSettings ToSettings() => new(
        ProjectId, BucketName, SourceRoots.ToList(),
        GracePeriodDays, ArchiveAfterDays);

    [RelayCommand]
    private void AddFolder(string path)
    {
        if (!string.IsNullOrWhiteSpace(path) && !SourceRoots.Contains(path))
            SourceRoots.Add(path);
    }

    [RelayCommand]
    private void RemoveFolder(string path) => SourceRoots.Remove(path);

    [RelayCommand]
    private void Save()
    {
        ToSettings().Save(SettingsPaths.SettingsFile);
        StatusMessage = "Settings saved.";
    }

    [RelayCommand]
    private async Task RunPreflight()
    {
        var r = await Preflight.CheckAsync(ToSettings());
        StatusMessage = r is { AdcOk: true, FfmpegOk: true, BucketOk: true }
            ? "All checks passed."
            : r.Message;
    }
}
```

- [ ] **Step 2: Write `Views/SettingsView.axaml`**

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="clr-namespace:PhotoThing.App.ViewModels"
             x:Class="PhotoThing.App.Views.SettingsView"
             x:DataType="vm:SettingsViewModel">
  <ScrollViewer>
    <StackPanel Margin="16" Spacing="8">
      <TextBlock Text="Google Cloud" FontWeight="Bold"/>
      <TextBox Watermark="Project ID (optional)" Text="{Binding ProjectId}"/>
      <TextBox Watermark="Bucket name" Text="{Binding BucketName}"/>
      <TextBox Watermark="ffmpeg path" Text="{Binding FfmpegPath}"/>

      <TextBlock Text="Source folders" FontWeight="Bold" Margin="0,12,0,0"/>
      <ListBox ItemsSource="{Binding SourceRoots}" Height="120"/>
      <StackPanel Orientation="Horizontal" Spacing="8">
        <TextBox x:Name="NewFolderBox" Watermark="/path/to/photos" Width="320"/>
        <Button Content="Add"
                Command="{Binding AddFolderCommand}"
                CommandParameter="{Binding #NewFolderBox.Text}"/>
      </StackPanel>

      <TextBlock Text="Retention" FontWeight="Bold" Margin="0,12,0,0"/>
      <StackPanel Orientation="Horizontal" Spacing="8">
        <TextBlock Text="Grace days" VerticalAlignment="Center"/>
        <NumericUpDown Value="{Binding GracePeriodDays}" Minimum="0"/>
        <TextBlock Text="Archive after days" VerticalAlignment="Center"/>
        <NumericUpDown Value="{Binding ArchiveAfterDays}" Minimum="1"/>
      </StackPanel>

      <StackPanel Orientation="Horizontal" Spacing="8" Margin="0,12,0,0">
        <Button Content="Save" Command="{Binding SaveCommand}"/>
        <Button Content="Run preflight checks" Command="{Binding RunPreflightCommand}"/>
      </StackPanel>
      <TextBlock Text="{Binding StatusMessage}" Foreground="#555" TextWrapping="Wrap"/>
    </StackPanel>
  </ScrollViewer>
</UserControl>
```

- [ ] **Step 3: Write `Views/SettingsView.axaml.cs`**

```csharp
using Avalonia.Controls;

namespace PhotoThing.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();
}
```

- [ ] **Step 4: Host the views in `MainWindow`**

Replace `Views/MainWindow.axaml` body with a `TabControl`:

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:views="clr-namespace:PhotoThing.App.Views"
        xmlns:vm="clr-namespace:PhotoThing.App.ViewModels"
        x:Class="PhotoThing.App.Views.MainWindow"
        x:DataType="vm:MainWindowViewModel"
        Title="PhotoThing" Width="900" Height="640">
  <TabControl>
    <TabItem Header="Settings">
      <views:SettingsView DataContext="{Binding Settings}"/>
    </TabItem>
    <TabItem Header="Backup">
      <views:BackupView DataContext="{Binding Backup}"/>
    </TabItem>
    <TabItem Header="Browse">
      <views:BrowseView DataContext="{Binding Browse}"/>
    </TabItem>
  </TabControl>
</Window>
```

Update `MainWindowViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;

namespace PhotoThing.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    public SettingsViewModel Settings { get; } = new();
    public BackupViewModel Backup { get; } = new();
    public BrowseViewModel Browse { get; } = new();
}
```

> `BackupView`/`BackupViewModel` and `BrowseView`/`BrowseViewModel` are created in Tasks 16-17. To keep the build green here, first create empty placeholder VMs/Views (a `UserControl` with a single `TextBlock`), then flesh them out. Commit this task after Settings works.

- [ ] **Step 5: Build and run to verify the Settings tab renders and saves**

Run: `dotnet run --project src/PhotoThing.App`
Expected: window opens; entering a bucket + folder and clicking Save writes `~/Library/Application Support/PhotoThing/settings.json` (verify the file exists). Close the window.

- [ ] **Step 6: Commit**

```bash
git add src/PhotoThing.App
git commit -m "feat: settings view with folder management and preflight"
```

---

## Task 16: Backup view (run backup, tiering, GC, progress)

**Files:**
- Create/replace: `src/PhotoThing.App/ViewModels/BackupViewModel.cs`
- Create/replace: `src/PhotoThing.App/Views/BackupView.axaml` (+ `.axaml.cs`)

**Interfaces:**
- Consumes: `AppServices`, `AppSettings`, `BackupService`, `GarbageCollector`, `SettingsPaths`.
- Produces: `BackupViewModel` with `RunBackupCommand` (iterates settings source roots, calls `BackupAsync` per root, then `SnapshotIndexAsync`, applies lifecycle via `Store.SetLifecycleArchiveAfterAsync`, then `Gc.CollectAsync`), an `IsRunning` flag, a `Log` (ObservableCollection<string>), and summary counters.

- [ ] **Step 1: Write `BackupViewModel.cs`**

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoThing.App.Services;
using PhotoThing.Core;

namespace PhotoThing.App.ViewModels;

public partial class BackupViewModel : ObservableObject
{
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _summary = "";
    public ObservableCollection<string> Log { get; } = new();

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunBackup()
    {
        IsRunning = true;
        Log.Clear();
        try
        {
            var settings = AppSettings.Load(SettingsPaths.SettingsFile);
            await using var svc = await AppServices.CreateAsync(settings);
            var now = DateTimeOffset.UtcNow;
            int up = 0, dd = 0, sk = 0, del = 0;

            foreach (var root in settings.SourceRoots)
            {
                Log.Add($"Backing up {root}…");
                var r = await svc.Backup.BackupAsync(root, now);
                up += r.Uploaded; dd += r.Deduped; sk += r.Skipped; del += r.SoftDeleted;
                Log.Add($"  +{r.Uploaded} new, {r.Deduped} deduped, {r.Skipped} unchanged, {r.SoftDeleted} removed");
            }

            var stamp = now.ToString("yyyyMMdd'T'HHmmss'Z'");
            await svc.Backup.SnapshotIndexAsync(SettingsPaths.IndexDb, stamp);
            Log.Add("Index snapshot uploaded.");

            await svc.Store.SetLifecycleArchiveAfterAsync(settings.ArchiveAfterDays);
            Log.Add($"Lifecycle: Archive after {settings.ArchiveAfterDays} days (blobs/).");

            var collected = await svc.Gc.CollectAsync(now);
            Log.Add($"Garbage-collected {collected} expired blob(s).");

            Summary = $"Done: +{up} new, {dd} deduped, {sk} unchanged, {del} removed, {collected} GC'd.";
        }
        catch (Exception e)
        {
            Log.Add($"ERROR: {e.Message}");
            Summary = "Backup failed.";
        }
        finally { IsRunning = false; }
    }

    private bool CanRun() => !IsRunning;
    partial void OnIsRunningChanged(bool value) => RunBackupCommand.NotifyCanExecuteChanged();
}
```

- [ ] **Step 2: Write `Views/BackupView.axaml`**

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="clr-namespace:PhotoThing.App.ViewModels"
             x:Class="PhotoThing.App.Views.BackupView"
             x:DataType="vm:BackupViewModel">
  <DockPanel Margin="16">
    <StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Spacing="12">
      <Button Content="Run backup" Command="{Binding RunBackupCommand}"/>
      <ProgressBar IsIndeterminate="{Binding IsRunning}" Width="160"
                   IsVisible="{Binding IsRunning}"/>
      <TextBlock Text="{Binding Summary}" VerticalAlignment="Center"/>
    </StackPanel>
    <ListBox ItemsSource="{Binding Log}" Margin="0,12,0,0"/>
  </DockPanel>
</UserControl>
```

with matching `.axaml.cs`:

```csharp
using Avalonia.Controls;
namespace PhotoThing.App.Views;
public partial class BackupView : UserControl { public BackupView() => InitializeComponent(); }
```

- [ ] **Step 3: Manual verification (requires a real bucket + ADC)**

Run: `dotnet run --project src/PhotoThing.App`
Point Settings at a test bucket + a small folder of photos, Save, then Backup → Run backup. Expected log shows new uploads; a second run shows all "unchanged". Verify objects appear under `blobs/`, `thumbs/`, `index/` in the GCS console.

- [ ] **Step 4: Commit**

```bash
git add src/PhotoThing.App
git commit -m "feat: backup view (run backup, snapshot, tiering, GC)"
```

---

## Task 17: Browse + Restore view (thumbnails, timeline, download)

**Files:**
- Create/replace: `src/PhotoThing.App/ViewModels/BrowseViewModel.cs`, `ThumbLoader.cs`
- Create/replace: `src/PhotoThing.App/Views/BrowseView.axaml` (+ `.axaml.cs`)

**Interfaces:**
- Consumes: `AppServices`, `RestoreService`, `IBlobStore`, `ObjectNames`, `SettingsPaths.ThumbCache`, `FileRecord`.
- Produces:
  - `ThumbLoader(IBlobStore store, string cacheDir)` with `Task<string> EnsureLocalThumbAsync(string hash, CancellationToken)` — returns a local file path, downloading `thumbs/<hash>.jpg` into the cache on first use.
  - `BrowseViewModel` with `LoadCommand` (populates a grid of `PhotoItem { FileRecord Record; Bitmap? Thumb; DateTimeOffset? CaptureDate }` ordered by capture date), `RestoreSelectedCommand` (uses a folder picker → `RestoreService.RestoreAsync`), `SelectedItem`, and a `StatusMessage` (shows an Archive cost hint when the selected blob's storage class is ARCHIVE).

- [ ] **Step 1: Write `ThumbLoader.cs`**

```csharp
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
```

- [ ] **Step 2: Write `BrowseViewModel.cs`**

```csharp
using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoThing.App.Services;
using PhotoThing.Core;

namespace PhotoThing.App.ViewModels;

public partial class PhotoItem : ObservableObject
{
    public required FileRecord Record { get; init; }
    [ObservableProperty] private Bitmap? _thumb;
    public string Title => Record.RelativePath;
    public string DateText => Record.CaptureDate?.ToString("yyyy-MM-dd") ?? "";
}

public partial class BrowseViewModel : ObservableObject
{
    [ObservableProperty] private PhotoItem? _selectedItem;
    [ObservableProperty] private string? _statusMessage;
    public ObservableCollection<PhotoItem> Items { get; } = new();

    [RelayCommand]
    private async Task Load()
    {
        Items.Clear();
        var settings = AppSettings.Load(SettingsPaths.SettingsFile);
        await using var svc = await AppServices.CreateAsync(settings);
        var loader = new ThumbLoader(svc.Store, SettingsPaths.ThumbCache);

        foreach (var rec in await svc.Restore.BrowseAsync())
        {
            var item = new PhotoItem { Record = rec };
            Items.Add(item);
            try
            {
                var path = await loader.EnsureLocalThumbAsync(rec.Hash);
                item.Thumb = new Bitmap(path);
            }
            catch { /* leave thumb null if unavailable */ }
        }
        StatusMessage = $"{Items.Count} items.";
    }

    [RelayCommand]
    private async Task RestoreSelected()
    {
        if (SelectedItem is null) return;
        var dest = await FolderPicker.PickAsync();  // see .axaml.cs helper
        if (dest is null) return;

        var settings = AppSettings.Load(SettingsPaths.SettingsFile);
        await using var svc = await AppServices.CreateAsync(settings);
        var blob = await svc.Index.GetBlobAsync(SelectedItem.Record.Hash);
        if (blob?.StorageClass == "ARCHIVE")
            StatusMessage = "Note: this file is in Archive tier — retrieval incurs extra cost.";
        await svc.Restore.RestoreAsync(SelectedItem.Record, dest);
        StatusMessage = $"Restored to {dest}.";
    }
}
```

- [ ] **Step 3: Write `Views/BrowseView.axaml`**

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="clr-namespace:PhotoThing.App.ViewModels"
             x:Class="PhotoThing.App.Views.BrowseView"
             x:DataType="vm:BrowseViewModel">
  <DockPanel Margin="16">
    <StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Spacing="12">
      <Button Content="Load library" Command="{Binding LoadCommand}"/>
      <Button Content="Restore selected" Command="{Binding RestoreSelectedCommand}"/>
      <TextBlock Text="{Binding StatusMessage}" VerticalAlignment="Center"/>
    </StackPanel>
    <ListBox ItemsSource="{Binding Items}" SelectedItem="{Binding SelectedItem}"
             Margin="0,12,0,0">
      <ListBox.ItemsPanel>
        <ItemsPanelTemplate><WrapPanel/></ItemsPanelTemplate>
      </ListBox.ItemsPanel>
      <ListBox.ItemTemplate>
        <DataTemplate x:DataType="vm:PhotoItem">
          <StackPanel Width="140" Margin="6">
            <Image Source="{Binding Thumb}" Width="128" Height="128" Stretch="UniformToFill"/>
            <TextBlock Text="{Binding DateText}" FontSize="11" Foreground="#666"/>
            <TextBlock Text="{Binding Title}" FontSize="11" TextTrimming="CharacterEllipsis"/>
          </StackPanel>
        </DataTemplate>
      </ListBox.ItemTemplate>
    </ListBox>
  </DockPanel>
</UserControl>
```

- [ ] **Step 4: Write `Views/BrowseView.axaml.cs` with a folder picker helper**

```csharp
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace PhotoThing.App.Views;

public partial class BrowseView : UserControl
{
    public BrowseView() => InitializeComponent();
}

public static class FolderPicker
{
    public static async Task<string?> PickAsync()
    {
        var top = Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime d
            ? d.MainWindow : null;
        if (top is null) return null;
        var res = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false });
        return res.Count > 0 ? res[0].Path.LocalPath : null;
    }
}
```

Reference `FolderPicker` from the VM via `PhotoThing.App.Views.FolderPicker.PickAsync()` (add the `using` in the VM).

- [ ] **Step 5: Manual verification (requires a populated bucket)**

Run: `dotnet run --project src/PhotoThing.App`
Browse → Load library shows a thumbnail grid ordered by capture date; select an item → Restore selected → pick a folder → file is downloaded. Archive-tier items show the cost hint.

- [ ] **Step 6: Commit**

```bash
git add src/PhotoThing.App
git commit -m "feat: browse + restore view with cached thumbnails"
```

---

## Task 18: Full-suite run + README

**Files:**
- Create: `README.md`

- [ ] **Step 1: Run the entire test suite**

Run: `dotnet test`
Expected: all Core tests pass.

- [ ] **Step 2: Write `README.md`** covering: prerequisites (`.NET 10`, ffmpeg, `gcloud auth application-default login`, a GCS bucket), how to configure settings, how to run a backup, how to restore, and the storage model (content-addressed blobs, index snapshots, tiering, soft-delete grace).

- [ ] **Step 3: Commit**

```bash
git add README.md
git commit -m "docs: add README with setup and usage"
```

---

## Self-Review

**Spec coverage:**
- Local→GCS incremental backup — Task 8 (skip/upload/dedup). ✓
- Content-addressed layout `blobs/<h>/<h>/<hash>` — Tasks 1, 8. ✓
- SQLite index (blobs/files/thumbs/snapshots/settings) — Task 6, 12. ✓
- Index snapshot to bucket (self-describing archive) — Task 12. ✓
- Thumbnails: ImageSharp images + ffmpeg videos, pinned Standard, local cache — Tasks 7, 16(store), 17(cache). ✓
- Soft-delete + grace + refcount GC — Tasks 8, 9. ✓
- Age-based Standard→Archive lifecycle scoped to `blobs/` — Task 13, applied in Task 16. ✓
- Restore browse (timeline via capture_date) + download + Archive cost hint — Tasks 10, 17. ✓
- Reconcile drift — Task 11. ✓
- ADC auth with actionable errors — Tasks 13, 14. ✓
- Avalonia MVVM GUI (Settings/Backup/Browse) — Tasks 14-17. ✓
- Defaults (30/180/512/4) — Global Constraints + Task 12 `AppSettings`. ✓
- ffmpeg preflight — Task 14. ✓

**Placeholder scan:** Task 15 intentionally notes creating temporary placeholder VMs/Views to keep the build green until Tasks 16-17 replace them — this is a build-ordering instruction, not an unfinished step; the real implementations are fully specified in 16-17.

**Type consistency:** `IBlobStore` signatures (Task 5) are used unchanged by `BackupService` (8), `GarbageCollector` (9), `RestoreService` (10), `ReconcileService` (11), `GcsBlobStore` (13). `IndexStore` method names match across Tasks 6, 8-12 (`GetActiveFileAsync`, `AdjustRefCountAsync`, `SetBlobGcAfterAsync`, `GetGcCandidatesAsync`, `GetAllBlobHashesAsync` added in 11, `ListActiveFilesAsync`, `SnapshotIndexAsync` on BackupService). `AppSettings` field names consistent across Tasks 12, 14, 15, 16, 17.

**Note on parallelism:** the spec calls for ~4 parallel uploads; Task 8 implements a correct sequential pipeline for clarity and testability. A follow-up optimization (bounded `Parallel.ForEachAsync` over scanned files with a `SemaphoreSlim`) can be layered on without changing the public `BackupAsync` contract — deferred to keep v1 correct-first.
