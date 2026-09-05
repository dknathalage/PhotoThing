# PhotoThing — Design Spec

**Date:** 2026-09-05
**Status:** Approved (design), pending implementation plan

## 1. Summary

PhotoThing is a macOS desktop application that backs up and archives photos
and videos from local folders to a **Google Cloud Storage (GCS)** bucket,
authenticating with the machine's **local Application Default Credentials
(ADC)**. It uses **content-addressed storage** (restic/git-style) for
automatic deduplication, a local **SQLite index** for folder *and* timeline
browsing, and generates **thumbnails** so browsing never touches full-res
originals. It supports incremental re-runs, soft-delete with a grace period,
age-based Standard→Archive tiering, and restore-by-download.

### Goals
- One-copy-per-unique-content backup of local media to GCS via ADC.
- Fast, cheap browsing (folder tree + timeline) using thumbnails and a local index.
- Incremental, resumable, idempotent backups.
- Self-describing archive: restorable on a fresh machine from the bucket alone.
- Cost-aware tiering: recent data in Standard, old data in Archive.

### Non-goals (v1)
- Full two-way / bidirectional sync (pulling remote changes down to local).
- Client-side encryption beyond Google-managed encryption at rest.
- Multi-user / shared buckets, sharing, or web access.
- Mobile clients.

## 2. Platform & Prerequisites

- **.NET 10** (`dotnet 10.0.400` confirmed on the target machine).
- **.NET MAUI** GUI targeting **Mac Catalyst**.
- **Xcode** installed (required by Mac Catalyst).
- `dotnet workload install maui`.
- **ffmpeg** installed and on `PATH` (hard prerequisite — required for video
  poster-frame thumbnails). Checked at startup with an actionable message if missing.
- **ADC** configured: `gcloud auth application-default login`.
- A GCS bucket the credentials can read/write (created by the user or by the app on first run).

## 3. Architecture

Three projects for clean isolation and testability:

- **`PhotoThing.Core`** — class library, no UI. All logic lives here; fully unit-testable.
- **`PhotoThing.App`** — MAUI GUI (MVVM via CommunityToolkit.Mvvm) wrapping Core services. Thin.
- **`PhotoThing.Core.Tests`** — xUnit, using an in-memory `IBlobStore` and temp
  SQLite so tests never touch the network.

### Core services (each has one job)

| Service | Responsibility | Depends on |
|---|---|---|
| `FileScanner` | Enumerate source roots, filter media extensions, skip hidden/system files | filesystem |
| `ContentHasher` | Streaming SHA-256 of file content | filesystem |
| `MediaMetadataExtractor` | EXIF capture date (MetadataExtractor lib), fallback to file mtime | file bytes |
| `ThumbnailService` | Generate thumbnails: images via ImageSharp, videos via ffmpeg | ImageSharp, ffmpeg |
| `IndexStore` | SQLite read/write: files, blobs, thumbs, snapshots, settings | Microsoft.Data.Sqlite |
| `IBlobStore` → `GcsBlobStore` | put/get/exists/delete objects; set bucket lifecycle; uses ADC | Google.Cloud.Storage.V1 |
| `BackupService` | Orchestrate scan → hash → thumbnail → dedup → upload → index | the above |
| `RestoreService` | Query index, download blob, write file to disk | IndexStore, IBlobStore |
| `GarbageCollector` | Soft-delete grace + refcount → delete blobs | IndexStore, IBlobStore |
| `ReconcileService` | Verify index against actual GCS listing; catch drift | IndexStore, IBlobStore |
| `LifecycleManager` | Configure bucket lifecycle rule (Standard→Archive, scoped to `blobs/`) | IBlobStore |

## 4. Data Model & GCS Layout

### SQLite index (local source of truth; snapshotted to the bucket)

- `blobs(hash PK, size, storage_class, uploaded_at, refcount, gc_after)`
- `files(id, source_root, relative_path, hash → blobs, size, mtime, capture_date, state[active|deleted], deleted_at, last_seen)`
- `thumbs(hash PK → blobs, thumb_ready, thumb_gcs_object, generated_at)`
- `snapshots(id, created_at, gcs_object, file_count)`
- `settings(key, value)`

### GCS bucket layout (Scheme A — content-addressed + index)

- `blobs/<h0h1>/<h2h3>/<full-hash>` — the single copy of each unique content.
  Original filename and content-type stored as object metadata. Subject to
  Standard→Archive lifecycle.
- `thumbs/<hash>.jpg` — thumbnail per unique content. **Pinned to Standard**
  (excluded from Archive lifecycle) so browsing is always instant and cheap.
- `index/snapshot-<utc>.db` + `index/latest.db` — the index DB backed up every
  run. Makes the archive self-describing and restorable on a fresh machine.

## 5. Key Flows

### Backup (incremental)
1. Scan source roots → candidate media files (filter by extension; skip hidden/system).
2. For each file: if `(relative_path, size, mtime)` matches an active index row,
   mark `last_seen` and skip.
3. Otherwise stream-hash (SHA-256).
4. If the hash already exists as a blob → dedup: add/link the `files` row,
   adjust refcount for the new logical reference. Reuse existing thumbnail.
5. Else: generate thumbnail; upload thumbnail to `thumbs/` (Standard); upload
   content to `blobs/…` (Standard) with metadata; insert `blobs`/`thumbs` rows.
6. Files present in the index but **not seen** this run → **soft-delete**:
   set `state=deleted`, `deleted_at=now`, `gc_after = now + grace`, decrement refcount.
7. Snapshot the index DB → upload to `index/snapshot-<utc>.db` and update `index/latest.db`.

Per-file index writes occur only after a successful upload, so an interrupted
run is safely resumable and idempotent.

### Garbage collection
Delete blobs (and their thumbnails) where `refcount == 0 AND gc_after < now`.
Grace period default **30 days** (configurable). Recoverable within the window.

### Tiering
A **bucket lifecycle rule** (configured by `LifecycleManager`) transitions
objects under the `blobs/` prefix Standard→Archive after **180 days**
(configurable). `thumbs/` and `index/` are excluded and stay Standard.

Accurate note: GCS Archive class is **instantly readable** (no thaw/restore
step, unlike AWS Glacier) — it only carries higher retrieval cost and a minimum
storage duration. Restore therefore remains a plain download.

### Restore
Browse entirely from the local index + thumbnails (folder tree *or* timeline by
`capture_date`). On explicit restore: resolve blob hash → download → write to
the chosen location with the original filename. Show a cost hint when the source
object is in Archive tier.

## 6. Auth, Error Handling, Testing

### Auth
`GoogleCredential.GetApplicationDefaultAsync()` → `StorageClient.Create(...)`.
If ADC, permissions, or the bucket are missing, surface an actionable message
including the exact `gcloud auth application-default login` command.

### Error handling
- Bounded-parallel uploads (~4 concurrent).
- Retry-with-backoff on transient failures.
- Resumable uploads for large video files.
- Post-upload integrity validation (CRC32C/MD5, client-verified).
- `ReconcileService` detects and reports index/GCS drift.
- Startup checks: ffmpeg present, ADC present, bucket reachable.

### Testing (TDD on Core)
Unit tests with an in-memory `IBlobStore`, temp files, and temp SQLite — fast,
offline. Cover: dedup, incremental skip, soft-delete + grace, refcount GC,
hash correctness, EXIF-with-mtime-fallback, thumbnail generation, reconcile.

## 7. Defaults

- Grace period: **30 days**
- Archive after: **180 days**
- Thumbnail: **512px** longest edge, JPEG
- Parallel uploads: **4**

## 8. GUI (MAUI, MVVM) — pages

- **Setup/Settings** — GCP project, bucket, source folders, defaults; ADC/ffmpeg status.
- **Dashboard/Backup** — trigger backup, progress, activity log.
- **Browse** — gallery grid + timeline (thumbnails), folder tree.
- **Restore** — select items, choose destination, download (with Archive cost hint).
