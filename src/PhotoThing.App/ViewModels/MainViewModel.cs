using CommunityToolkit.Mvvm.ComponentModel;

namespace PhotoThing.App.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    public SettingsViewModel Settings { get; } = new();
    public BackupViewModel Backup { get; } = new();
    public BrowseViewModel Browse { get; } = new();
}
