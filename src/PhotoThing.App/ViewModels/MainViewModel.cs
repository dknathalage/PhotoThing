using System;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoThing.App.Services;
using PhotoThing.Core;

namespace PhotoThing.App.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    public SettingsViewModel Settings { get; } = new();
    public BackupViewModel Backup { get; } = new();
    public BrowseViewModel Browse { get; } = new();

    // Backup and Settings live behind the tray as overlay panels over the grid.
    [ObservableProperty] private bool _showBackupPanel;
    [ObservableProperty] private bool _showSettingsPanel;

    public bool IsPanelOpen => ShowBackupPanel || ShowSettingsPanel;

    private readonly DispatcherTimer _syncTimer = new();

    public MainViewModel()
    {
        _syncTimer.Tick += async (_, _) => await RunSyncAsync();
        ReconfigureSync();
    }

    partial void OnShowBackupPanelChanged(bool value) => OnPropertyChanged(nameof(IsPanelOpen));
    partial void OnShowSettingsPanelChanged(bool value) => OnPropertyChanged(nameof(IsPanelOpen));

    [RelayCommand] private void ShowBackup() { ShowSettingsPanel = false; ShowBackupPanel = true; }
    [RelayCommand] private void ShowSettings() { ShowBackupPanel = false; ShowSettingsPanel = true; }

    [RelayCommand]
    private void ClosePanel()
    {
        var wasBackup = ShowBackupPanel;
        var wasSettings = ShowSettingsPanel;
        ShowBackupPanel = false;
        ShowSettingsPanel = false;
        if (wasSettings) ReconfigureSync();                              // interval may have changed
        if (wasBackup && Browse.LoadCommand.CanExecute(null))
            Browse.LoadCommand.Execute(null);                           // show newly archived photos
    }

    /// Run a backup now (skipping if one is already running), then refresh the grid.
    public async Task RunSyncAsync()
    {
        if (!File.Exists(SettingsPaths.SettingsFile)) return;
        if (!Backup.RunBackupCommand.CanExecute(null)) return;
        await Backup.RunBackupCommand.ExecuteAsync(null);
        if (Browse.LoadCommand.CanExecute(null))
            Browse.LoadCommand.Execute(null);
    }

    /// (Re)start the periodic sync timer from the saved interval. 0 disables it.
    public void ReconfigureSync()
    {
        _syncTimer.Stop();
        var minutes = ReadSyncInterval();
        if (minutes > 0)
        {
            _syncTimer.Interval = TimeSpan.FromMinutes(minutes);
            _syncTimer.Start();
        }
    }

    private static int ReadSyncInterval()
    {
        try
        {
            if (File.Exists(SettingsPaths.SettingsFile))
                return AppSettings.Load(SettingsPaths.SettingsFile).SyncIntervalMinutes;
        }
        catch { /* fall through to disabled */ }
        return 0;
    }
}
