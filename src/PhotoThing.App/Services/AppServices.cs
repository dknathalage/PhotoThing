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
