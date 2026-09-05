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
