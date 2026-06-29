using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Notifications;
using Avalonia.Controls.Platform;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DepScope.Core.Models;
using DepScope.Desktop.Services;
using DepScope.Desktop.ViewModels;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;



namespace DepScope.Desktop.Views;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _vm;
    private readonly DispatcherTimer _autoRescanTimer;
    private WindowNotificationManager? _notificationManager;
    private bool _isInstallingUpdate;

    public MainWindow()
    {
        InitializeComponent();

        _vm = new MainWindowViewModel();
        DataContext = _vm;

        this.Opened += (_, __) =>
        {
            _notificationManager = new WindowNotificationManager(this)
            {
                Position = NotificationPosition.BottomRight,
                MaxItems = 3
            };
        };

        // listen to VM summary event
        _vm.OutdatedSummaryChanged += OnOutdatedSummaryChanged;

        _autoRescanTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(10)
        };
        _autoRescanTimer.Tick += async (_, __) =>
        {
            if (_vm.IsScanning)
                return;

            await _vm.RescanSavedRootsInBackgroundAsync();
        };


#if DEBUG
        this.AttachDevTools();
#endif
        // Handle tray setting changes
        _vm.PropertyChanged += VmOnPropertyChanged;

        // on loaded, rescan and enable tray if needed
        this.Loaded += async (_, __) =>
        {
            await _vm.RescanSavedRootsAsync();
            _ = _vm.RetryUnresolvedLatestVersionsInBackgroundAsync();

            if (_vm.UseSystemTray)
                TrayService.Enable(this);

            if (_vm.AutoRescan)
                _autoRescanTimer.Start();

            if (!_vm.OfflineMode && _vm.CheckUpdatesOnStart)
                await CheckForUpdatesAsync(fromStartup: true);
        };

    }
    private void VmOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.UseSystemTray))
        {
            if (_vm.UseSystemTray)
                TrayService.Enable(this);
            else
                TrayService.Disable();
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.AutoRescan))
        {
            if (_vm.AutoRescan)
                _autoRescanTimer.Start();
            else
                _autoRescanTimer.Stop();
        }
    }


    private async void OnOpenFolderClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select a root folder to scan",
            AllowMultiple = false
        });

        var folder = folders.FirstOrDefault();
        if (folder is null)
            return;

        var path = folder.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            await _vm.ScanFolderAsync(path, append: true, CancellationToken.None);
            _ = _vm.RetryUnresolvedLatestVersionsInBackgroundAsync();
        }
    }

    private async void OnRescanClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_vm.IsScanning)
            return;

        await _vm.RescanSavedRootsInBackgroundAsync(manual: true);
    }

    private async void OnExportHtmlClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!_vm.CanExportReport)
        {
            _vm.StatusMessage = "No scan results to export.";
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export HTML report",
            SuggestedFileName = $"depscope-report-{DateTime.Now:yyyyMMdd-HHmm}.html",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("HTML report")
                {
                    Patterns = new[] { "*.html" },
                    MimeTypes = new[] { "text/html" }
                }
            }
        });

        if (file is null)
            return;

        var path = file.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
        {
            _vm.StatusMessage = "Export failed: select a local file path.";
            return;
        }

        try
        {
            await _vm.ExportHtmlReportAsync(path, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _vm.StatusMessage = $"Export failed: {ex.Message}";
        }
    }

    private void OnSettingsButtonClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button btn)
        {
            FlyoutBase.ShowAttachedFlyout(btn);
        }
    }

    private void PackagesGrid_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Control control)
            return;

        if (control.DataContext is not PackageRef pkg)
            return;

        var url = GetPackageUrl(pkg);
        if (url is null)
            return;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            };
            Process.Start(psi);
        }
        catch
        {
            // ignore browser launch errors
        }
    }

    private static string? GetPackageUrl(PackageRef pkg)
    {
        return pkg.Ecosystem switch
        {
            Ecosystem.DotNet => $"https://www.nuget.org/packages/{pkg.PackageName}",
            Ecosystem.Npm => $"https://www.npmjs.com/package/{Uri.EscapeDataString(pkg.PackageName)}",
            Ecosystem.GitHubActions => GetGitHubActionsUrl(pkg.PackageName),
            _ => null
        };
    }

    private static string? GetGitHubActionsUrl(string packageName)
    {
        var parts = packageName.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return null;

        return $"https://github.com/{parts[0]}/{parts[1]}/releases";
    }

    private async void OnRemoveProjectClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await _vm.RemoveSelectedProjectAsync();
    }

    private async void OnCheckUpdatesClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_vm.OfflineMode)
        {
            _vm.StatusMessage = "Offline/private mode is enabled. Disable it to check for updates.";
            return;
        }

        await CheckForUpdatesAsync(fromStartup: false);
    }


    private async Task CheckForUpdatesAsync(bool fromStartup)
    {
        try
        {
            if (!Version.TryParse(_vm.AppVersion, out var currentVersion))
                currentVersion = new Version(0, 0, 0);

            const string owner = "Scippie";
            const string repo = "DepScope";

            _vm.StatusMessage = "Checking for updates...";

            var info = await UpdateService.CheckForUpdateAsync(owner, repo, currentVersion, CancellationToken.None);

            if (info is null)
            {
                if (!fromStartup)
                    _vm.StatusMessage = $"You are using the latest version ({currentVersion}).";
                else
                    _vm.StatusMessage = $"DepScope is up to date ({currentVersion}).";

                return;
            }

            // New version available
            _vm.StatusMessage = $"New version {info.LatestVersion} is available.";

            if (_vm.AutoDownloadUpdates && info.AssetDownloadUrl is not null)
            {
                var targetDir = UpdateService.GetDefaultUpdateFolder(info.LatestVersion);

                _vm.StatusMessage = $"Downloading update {info.LatestVersion}...";
                var folder = await UpdateService.DownloadAndExtractAsync(info, targetDir, CancellationToken.None);

                if (folder is not null)
                {
                    _vm.StatusMessage = $"Installing update {info.LatestVersion}...";

                    if (UpdateService.InstallAndRestart(folder))
                    {
                        _isInstallingUpdate = true;
                        TrayService.Disable();
                        _vm.StatusMessage = $"Installing update {info.LatestVersion}. DepScope will restart.";
                        await Task.Delay(500);

                        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                            desktop.Shutdown();
                        else
                            Close();
                    }
                    else
                    {
                        _vm.StatusMessage = "Update downloaded, but automatic installation could not start.";
                        UpdateService.OpenFolder(folder);
                    }
                }
                else
                {
                    _vm.StatusMessage = "Download failed; opening release page.";
                    OpenUpdate(info); // fallback to browser
                }
            }
            else
            {
                // Manual path: just open release / asset in browser
                OpenUpdate(info);
            }
        }
        catch (Exception ex)
        {
            _vm.StatusMessage = $"Update check failed: {ex.Message}";
        }
    }

    private static void OpenUpdate(UpdateInfo info)
    {
        var url = info.AssetDownloadUrl ?? info.ReleasePageUrl;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            };
            Process.Start(psi);
        }
        catch
        {
            // ignore failure; user can manually go to GitHub if needed
        }
    }

    private void OnOutdatedSummaryChanged(int projects, int packages)
    {
        var msg = packages > 0
            ? $"{projects} project(s) with {packages} outdated package(s)."
            : "Scan complete. No outdated packages found.";
        var canShowWindowNotification = IsVisible && WindowState != WindowState.Minimized;

        if (_vm.EnableNotifications && canShowWindowNotification && _notificationManager is not null)
        {
            _notificationManager.Show(
                new Notification(
                    "DepScope",
                    msg,
                    NotificationType.Information,
                    expiration: TimeSpan.FromSeconds(6))
            );
        }

    }


    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_isInstallingUpdate)
        {
            base.OnClosing(e);
            return;
        }

        // Close to tray when enabled
        if (_vm.UseSystemTray)
        {
            e.Cancel = true;
            this.Hide();
            return;
        }

        base.OnClosing(e);
    }

}
