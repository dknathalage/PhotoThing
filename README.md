# PhotoThing

PhotoThing is a macOS desktop application that backs up and archives photos and videos from local folders to a Google Cloud Storage (GCS) bucket. It uses content-addressed storage for automatic deduplication, a local SQLite index for fast browsing, and generates thumbnails for efficient preview without accessing full-resolution originals. PhotoThing supports incremental backups, soft-delete with grace periods, age-based tiering (Standard → Archive), and restore-by-download, all authenticated via your machine's local Application Default Credentials (ADC).

## Prerequisites

- **.NET 10**: Ensure `dotnet 10.0.400` or later is installed.
- **ffmpeg**: Required for video poster-frame thumbnails. Install via Homebrew (`brew install ffmpeg`) and verify it's on your `PATH`.
- **Google Cloud credentials**: Run `gcloud auth application-default login` to set up ADC.
- **GCS bucket**: Create a writable bucket in your GCP project (or PhotoThing can initialize one on first run).

**Note on ImageSharp license warning:** The build produces a SixLabors.ImageSharp license notice (Six Labors Split License). This is harmless for personal and open-source use; no action required.

## Build & Run

### Build the application:
```bash
dotnet build
```

### Run the desktop app:
```bash
dotnet run --project src/PhotoThing.App
```

### Run the full test suite:
```bash
dotnet test
```
(27 unit tests covering dedup, incremental skip, soft-delete + grace, refcount GC, and index reconciliation.)

## Configuration

PhotoThing stores settings in `~/Library/Application Support/PhotoThing/settings.json`. Configure the following via the **Settings tab** in the GUI:

- **GCP Project**: (Optional) Your Google Cloud project ID.
- **Bucket name**: The GCS bucket to back up to.
- **Source folders**: Local directories to scan for media (photos and videos).
- **Grace period**: Days before soft-deleted blobs become eligible for garbage collection (default: 30).
- **Archive after**: Days before blobs transition from Standard to Archive tier (default: 180).

## Usage

### Backup
1. Open the **Backup tab**.
2. Click "Start Backup" to scan source folders, hash new files, upload unique content, and update the local index.
3. Subsequent runs are incremental: unchanged files are skipped, new files are hashed and deduplicated if matching existing content, and files no longer on disk are soft-deleted.

### Browse & Restore
1. Open the **Browse tab** to view your library as a folder tree or timeline (by capture date).
2. Select photos/videos to restore.
3. Choose a destination on disk and click "Restore" to download.
4. If a blob is in Archive tier, a cost hint is displayed.

## Storage Model

### GCS Layout

- **Blobs** (`blobs/<h0h1>/<h2h3>/<full-hash>`): Content-addressed storage with one copy per unique file. Original filename and MIME type stored as object metadata. Subject to Standard → Archive lifecycle rules.
- **Thumbnails** (`thumbs/<hash>.jpg`): Pinned to Standard tier (excluded from Archive lifecycle) so browsing remains fast and cost-effective.
- **Index snapshots** (`index/snapshot-<utc>.db`, `index/latest.db`): Self-describing archives of the local SQLite index. Backed up after each run, enabling restore on a fresh machine.

### Local Index (SQLite)

- **blobs**: Hash, size, storage class, upload timestamp, reference count, and GC grace deadline.
- **files**: Source root, relative path, hash, size, mtime, capture date (from EXIF or mtime), state (active or deleted), and deletion timestamp.
- **thumbs**: Thumbnail status and GCS object path per unique hash.
- **snapshots**: Historical snapshots of the index for point-in-time restore.
- **settings**: Persisted configuration (bucket, source folders, defaults).

### Lifecycle & Garbage Collection

- **Soft-delete + grace**: Files no longer on disk are marked deleted but retained for a configurable grace period (default 30 days). During grace, blobs are refcounted; once grace expires and refcount reaches zero, blobs are eligible for deletion.
- **Tiering**: A bucket lifecycle rule (configured automatically) transitions blobs under `blobs/` from Standard to Archive after a configurable age (default 180 days). Thumbnails and index snapshots remain Standard.
- **Incremental**: Unchanged files are detected via `(relative_path, size, mtime)` and skipped; only new or modified files are hashed and uploaded.

## Project Layout

```
├── src/
│   ├── PhotoThing.Core/          # Class library: core backup, restore, GCS, index logic
│   └── PhotoThing.App/           # Avalonia MVVM GUI (Settings, Backup, Browse tabs)
├── tests/
│   └── PhotoThing.Core.Tests/    # xUnit tests (in-memory IBlobStore, temp SQLite)
├── docs/
│   └── superpowers/specs/        # Design specification
└── README.md                      # This file
```

## Troubleshooting

- **ffmpeg not found**: Ensure ffmpeg is installed and on your PATH. Run `which ffmpeg` to verify.
- **ADC not configured**: Run `gcloud auth application-default login` and log in with a GCP account that has write access to your bucket.
- **Bucket not found**: Verify the bucket exists and that your GCP credentials have `storage.objects.get` and `storage.objects.create` permissions.
- **Tests fail**: Run `dotnet build` first to ensure all packages are restored.
