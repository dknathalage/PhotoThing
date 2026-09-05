using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace PhotoThing.App.Views;

public partial class BrowseView : UserControl
{
    public BrowseView() => InitializeComponent();
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
