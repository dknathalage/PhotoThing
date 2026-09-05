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

    public async Task<IReadOnlyList<string>> GetAllBlobHashesAsync()
    {
        var list = new List<string>();
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT hash FROM blobs";
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(r.GetString(0));
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

    public async Task DeleteThumbAsync(string hash) =>
        await ExecAsync("DELETE FROM thumbs WHERE hash=$h", ("$h", hash));

    // ---- stats ----
    /// Snapshot counts for the dashboard. `archiveBefore` is the age cutoff: blobs
    /// uploaded before it are (or will soon be) in the Archive tier per the
    /// lifecycle rule. ISO-8601 "O" strings sort chronologically, so a text
    /// comparison is a valid age filter.
    public async Task<LibraryStats> GetLibraryStatsAsync(DateTimeOffset archiveBefore)
    {
        var files = Convert.ToInt32(await ScalarAsync("SELECT COUNT(*) FROM files WHERE state='Active'"));
        var blobs = Convert.ToInt32(await ScalarAsync("SELECT COUNT(*) FROM blobs"));
        var bytes = Convert.ToInt64(await ScalarAsync("SELECT COALESCE(SUM(size),0) FROM blobs"));
        var archived = Convert.ToInt32(await ScalarAsync(
            "SELECT COUNT(*) FROM blobs WHERE uploaded_at < $t", ("$t", Iso(archiveBefore))));
        return new LibraryStats(files, blobs, archived, bytes);
    }

    public void Dispose() => _conn.Dispose();
    public async ValueTask DisposeAsync() => await _conn.DisposeAsync();
}
