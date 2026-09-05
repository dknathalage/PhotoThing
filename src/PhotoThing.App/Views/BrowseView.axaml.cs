using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PhotoThing.App.ViewModels;

namespace PhotoThing.App.Views;

public partial class BrowseView : UserControl
{
    private bool _autoLoaded;

    public BrowseView() => InitializeComponent();

    // Load the library once when the grid first appears, so the main view fills
    // with the archive automatically.
    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_autoLoaded) return;
        _autoLoaded = true;
        if (DataContext is BrowseViewModel vm && vm.LoadCommand.CanExecute(null))
            vm.LoadCommand.Execute(null);
    }
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
