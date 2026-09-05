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
    [ObservableProperty] private bool _isEmpty = true;
    public ObservableCollection<PhotoItem> Items { get; } = new();

    [RelayCommand]
    private async Task Load()
    {
        // Release the previous run's decoded bitmaps before dropping their owners,
        // so reloading a large library doesn't pile up native image memory.
        foreach (var old in Items) old.Thumb?.Dispose();
        Items.Clear();

        if (!File.Exists(SettingsPaths.SettingsFile))
        {
            IsEmpty = true;
            StatusMessage = "Open Settings to add a bucket and folders.";
            return;
        }

        try
        {
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
            StatusMessage = Items.Count == 0 ? "No photos archived yet." : $"{Items.Count} photos";
        }
        catch (Exception e)
        {
            StatusMessage = $"Couldn't load library: {e.Message}";
        }
        IsEmpty = Items.Count == 0;
    }

    [RelayCommand]
    private async Task RestoreSelected()
    {
        if (SelectedItem is null)
        {
            StatusMessage = "Select an item to restore first.";
            return;
        }
        var dest = await FolderPicker.PickAsync();
        if (dest is null) return;

        var settings = AppSettings.Load(SettingsPaths.SettingsFile);
        await using var svc = await AppServices.CreateAsync(settings);
        var fromArchive = (await svc.Index.GetBlobAsync(SelectedItem.Record.Hash))?.StorageClass == "ARCHIVE";
        if (fromArchive)
            StatusMessage = "Retrieving from Archive tier (extra retrieval cost)…";
        await svc.Restore.RestoreAsync(SelectedItem.Record, dest);
        StatusMessage = fromArchive
            ? $"Restored to {dest} — Archive retrieval was billed."
            : $"Restored to {dest}.";
    }
}
