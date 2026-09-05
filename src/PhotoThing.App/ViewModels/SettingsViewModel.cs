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
    [ObservableProperty] private int _syncIntervalMinutes = 60;
    [ObservableProperty] private string? _statusMessage;

    private AppSettings _base = new(ProjectId: null, BucketName: "", SourceRoots: new List<string>());

    public ObservableCollection<string> SourceRoots { get; } = new();

    public SettingsViewModel() => TryLoad();

    private void TryLoad()
    {
        if (!File.Exists(SettingsPaths.SettingsFile)) return;
        var s = AppSettings.Load(SettingsPaths.SettingsFile);
        _base = s;
        ProjectId = s.ProjectId;
        BucketName = s.BucketName;
        FfmpegPath = s.FfmpegPath;
        GracePeriodDays = s.GracePeriodDays;
        ArchiveAfterDays = s.ArchiveAfterDays;
        SyncIntervalMinutes = s.SyncIntervalMinutes;
        SourceRoots.Clear();
        foreach (var r in s.SourceRoots) SourceRoots.Add(r);
    }

    public AppSettings ToSettings() => _base with {
        ProjectId = ProjectId,
        BucketName = BucketName,
        SourceRoots = SourceRoots.ToList(),
        GracePeriodDays = GracePeriodDays,
        ArchiveAfterDays = ArchiveAfterDays,
        SyncIntervalMinutes = SyncIntervalMinutes,
        FfmpegPath = FfmpegPath
    };

    [RelayCommand]
    private void AddFolder(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && !SourceRoots.Contains(path))
            SourceRoots.Add(path);
    }

    [RelayCommand]
    private void RemoveFolder(string? path)
    {
        if (path is not null)
            SourceRoots.Remove(path);
    }

    [RelayCommand]
    private void Save()
    {
        ToSettings().Save(SettingsPaths.SettingsFile);
        StatusMessage = "Settings saved.";
    }

    [RelayCommand]
    private async Task RunPreflight()
    {
        StatusMessage = "Running checks...";
        var r = await Preflight.CheckAsync(ToSettings());
        StatusMessage = r is { AdcOk: true, FfmpegOk: true, BucketOk: true }
            ? "All checks passed."
            : r.Message;
    }
}
