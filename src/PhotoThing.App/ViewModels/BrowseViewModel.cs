using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoThing.App.Services;
using PhotoThing.App.Views;
using PhotoThing.Core;

namespace PhotoThing.App.ViewModels;

public partial class PhotoItem : ObservableObject
{
    public required FileRecord Record { get; init; }
    [ObservableProperty] private Bitmap? _thumb;
    public string Title => Record.RelativePath;
    public string DateText => Record.CaptureDate?.ToString("yyyy-MM-dd") ?? "";
}

public partial class BrowseViewModel : ObservableObject
{
    [ObservableProperty] private PhotoItem? _selectedItem;
    [ObservableProperty] private string? _statusMessage;
    public ObservableCollection<PhotoItem> Items { get; } = new();

    [RelayCommand]
    private async Task Load()
    {
        Items.Clear();
        var settings = AppSettings.Load(SettingsPaths.SettingsFile);
        await using var svc = await AppServices.CreateAsync(settings);
        var loader = new ThumbLoader(svc.Store, SettingsPaths.ThumbCache);

        foreach (var rec in await svc.Restore.BrowseAsync())
        {
            var item = new PhotoItem { Record = rec };
            Items.Add(item);
            try
            {
                var path = await loader.EnsureLocalThumbAsync(rec.Hash);
                item.Thumb = new Bitmap(path);
            }
            catch { /* leave thumb null if unavailable */ }
        }
        StatusMessage = $"{Items.Count} items.";
    }

    [RelayCommand]
    private async Task RestoreSelected()
    {
        if (SelectedItem is null) return;
        var dest = await FolderPicker.PickAsync();
        if (dest is null) return;

        var settings = AppSettings.Load(SettingsPaths.SettingsFile);
        await using var svc = await AppServices.CreateAsync(settings);
        var blob = await svc.Index.GetBlobAsync(SelectedItem.Record.Hash);
        if (blob?.StorageClass == "ARCHIVE")
            StatusMessage = "Note: this file is in Archive tier — retrieval incurs extra cost.";
        await svc.Restore.RestoreAsync(SelectedItem.Record, dest);
        StatusMessage = $"Restored to {dest}.";
    }
}
