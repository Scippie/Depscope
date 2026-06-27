using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using DepScope.Desktop.Infrastructure;
using DepScope.Desktop.Views;
using System;

namespace DepScope.Desktop.Services;

public static class TrayService
{
    private static TrayIcons? _trayIcons;
    private static TrayIcon? _trayIcon;

    public static void Enable(MainWindow window)
    {
        if (Application.Current is null)
            return;

        if (_trayIcon != null)
            return; // already created

        // Load icon from assets
        var uri = new Uri("avares://DepScope.Desktop/Assets/DepScope_img.png");
        using var stream = AssetLoader.Open(uri);
        var bitmap = new Bitmap(stream);
        var windowIcon = new WindowIcon(bitmap);

        _trayIcon = new TrayIcon
        {
            Icon = windowIcon,
            ToolTipText = "DepScope"
        };

        // Build native menu
        var menu = new NativeMenu();

        var openItem = new NativeMenuItem("Open");
        openItem.Command = new SimpleCommand(() =>
        {
            window.Show();
            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;

            window.Activate();
        });
        menu.Items.Add(openItem);

        menu.Items.Add(new NativeMenuItemSeparator());

        var exitItem = new NativeMenuItem("Exit");
        exitItem.Command = new SimpleCommand(() =>
        {
            if (Application.Current?.ApplicationLifetime
                is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
        });
        menu.Items.Add(exitItem);

        _trayIcon.Menu = menu;

        _trayIcons = new TrayIcons { _trayIcon };
        TrayIcon.SetIcons(Application.Current, _trayIcons);
    }

    public static void Disable()
    {
        if (Application.Current is null)
            return;

        TrayIcon.SetIcons(Application.Current, null);
        _trayIcon?.Dispose();
        _trayIcon = null;
        _trayIcons = null;
    }
}

