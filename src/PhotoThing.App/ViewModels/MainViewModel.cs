using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PhotoThing.App.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    public SettingsViewModel Settings { get; } = new();
    public BackupViewModel Backup { get; } = new();
    public BrowseViewModel Browse { get; } = new();

    // Backup and Settings live behind the top bar as overlay panels over the grid.
    [ObservableProperty] private bool _showBackupPanel;
    [ObservableProperty] private bool _showSettingsPanel;

    public bool IsPanelOpen => ShowBackupPanel || ShowSettingsPanel;

    partial void OnShowBackupPanelChanged(bool value) => OnPropertyChanged(nameof(IsPanelOpen));
    partial void OnShowSettingsPanelChanged(bool value) => OnPropertyChanged(nameof(IsPanelOpen));

    [RelayCommand] private void ShowBackup() { ShowSettingsPanel = false; ShowBackupPanel = true; }
    [RelayCommand] private void ShowSettings() { ShowBackupPanel = false; ShowSettingsPanel = true; }

    [RelayCommand]
    private void ClosePanel()
    {
        // Refresh the grid after Backup so newly archived photos show up.
        var wasBackup = ShowBackupPanel;
        ShowBackupPanel = false;
        ShowSettingsPanel = false;
        if (wasBackup && Browse.LoadCommand.CanExecute(null))
            Browse.LoadCommand.Execute(null);
    }
}
