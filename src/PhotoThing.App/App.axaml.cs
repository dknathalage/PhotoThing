using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using PhotoThing.App.ViewModels;
using PhotoThing.App.Views;

namespace PhotoThing.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm };
            desktop.MainWindow = window;

            // Live in the menu bar: closing the window hides it to the tray;
            // the app keeps running (and syncing) until Quit.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            window.Closing += (_, e) =>
            {
                if (e.CloseReason == WindowCloseReason.WindowClosing)
                {
                    e.Cancel = true;
                    window.Hide();
                }
            };

            SetupTray(desktop, window, vm);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void SetupTray(IClassicDesktopStyleApplicationLifetime desktop, Window window, MainViewModel vm)
    {
        void ShowWindow()
        {
            window.Show();
            window.WindowState = WindowState.Normal;
            window.Activate();
        }

        var open = new NativeMenuItem { Header = "Open PhotoThing" };
        open.Click += (_, _) => ShowWindow();

        var backupNow = new NativeMenuItem { Header = "Back up now" };
        backupNow.Click += (_, _) => { ShowWindow(); vm.ShowBackupCommand.Execute(null); _ = vm.RunSyncAsync(); };

        var settings = new NativeMenuItem { Header = "Settings…" };
        settings.Click += (_, _) => { ShowWindow(); vm.ShowSettingsCommand.Execute(null); };

        var quit = new NativeMenuItem { Header = "Quit PhotoThing" };
        quit.Click += (_, _) => desktop.Shutdown();

        var menu = new NativeMenu();
        menu.Items.Add(open);
        menu.Items.Add(backupNow);
        menu.Items.Add(settings);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(quit);

        var tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://PhotoThing.App/Assets/avalonia-logo.ico"))),
            ToolTipText = "PhotoThing",
            IsVisible = true,
            Menu = menu,
        };
        tray.Clicked += (_, _) => ShowWindow();

        TrayIcon.SetIcons(this, new TrayIcons { tray });
    }
}
