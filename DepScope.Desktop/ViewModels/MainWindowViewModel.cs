using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DepScope.Core;
using DepScope.Core.Ecosystems;
using DepScope.Core.Models;
using DepScope.Desktop.Settings;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Styling;
using DepScope.Desktop.Services;
using System.Reflection;

namespace DepScope.Desktop.ViewModels;

public enum PackageFilter
{
    All,
    Outdated,
    MajorOnly,
    MinorOrMajor
}

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly InspectionService _inspectionService;
    private readonly Action<bool> _setAutostart;
    private readonly SemaphoreSlim _scanGate = new(1, 1);

    private readonly string _settingsPath;
    private UserSettings _settings = new();

    private ProjectInfo? _selectedProject;
    private PackageRef? _selectedPackage;
    private string _statusMessage = "Select a folder to scan...";

    private bool _isScanning;
    public bool IsScanning
    {
        get => _isScanning;
        set
        {
            if (value != _isScanning)
            {
                _isScanning = value;
                OnPropertyChanged();
            }
        }
    }

    private bool _isDarkMode = true;
    private bool _offlineMode;
    private string _nuGetSourceUrl = string.Empty;
    private string _npmRegistryBaseUrl = string.Empty;
    private string _goProxyBaseUrl = string.Empty;
    private string _pythonPackageIndexBaseUrl = string.Empty;
    private string _packagistMetadataBaseUrl = string.Empty;
    private string _mavenSearchBaseUrl = string.Empty;
    private string _cratesApiBaseUrl = string.Empty;
    private string _gitHubApiBaseUrl = string.Empty;
    private bool _startWithSystem;
    private bool _useSystemTray;
    private bool _autoRescan;
    private bool _checkUpdatesOnStart;
    private bool _autoDownloadUpdates;
    private bool _enableNotifications;

    public bool EnableNotifications
    {
        get => _enableNotifications;
        set
        {
            if (value != _enableNotifications)
            {
                _enableNotifications = value;
                OnPropertyChanged();

                _settings.EnableNotifications = value;
                SaveSettings();
            }
        }
    }

    private int _lastOutdatedProjects;
    private int _lastOutdatedPackages;

    public event Action<int, int>? OutdatedSummaryChanged;

    public string AppVersion { get; } =
        Assembly.GetEntryAssembly()?
            .GetName().Version?.ToString(3) ?? "0.0.0";

    public ObservableCollection<ProjectGroup> ProjectGroups { get; } = new();
    public ObservableCollection<ProjectInfo> Projects { get; } = new();

    public ProjectInfo? SelectedProject
    {
        get => _selectedProject;
        set
        {
            if (!Equals(value, _selectedProject))
            {
                _selectedProject = value;
                SelectedPackage = null;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedPackages));
                OnPropertyChanged(nameof(FilteredPackages));
            }
        }
    }

    public PackageRef? SelectedPackage
    {
        get => _selectedPackage;
        set
        {
            if (!Equals(value, _selectedPackage))
            {
                _selectedPackage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasSelectedVulnerabilityAdvisories));
                OnPropertyChanged(nameof(SelectedVulnerabilityAdvisories));
            }
        }
    }

    public bool HasSelectedVulnerabilityAdvisories =>
        SelectedPackage?.VulnerabilityAdvisories.Count > 0 ||
        !string.IsNullOrWhiteSpace(SelectedPackage?.VulnerabilityIds);

    public string SelectedVulnerabilityAdvisories =>
        SelectedPackage is null
            ? string.Empty
            : SelectedPackage.VulnerabilityAdvisories.Count > 0
                ? string.Join(Environment.NewLine, SelectedPackage.VulnerabilityAdvisories.Select(a => a.DisplayText))
                : SelectedPackage.VulnerabilityIds ?? string.Empty;

    private object? _selectedTreeItem;
    public object? SelectedTreeItem
    {
        get => _selectedTreeItem;
        set
        {
            if (!Equals(value, _selectedTreeItem))
            {
                _selectedTreeItem = value;

                // Only care about ProjectInfo – ignore clicks on group headers
                if (value is ProjectInfo proj)
                {
                    SelectedProject = proj; // this will raise FilteredPackages, etc.
                }
            }
        }
    }


    public IEnumerable<PackageRef> SelectedPackages =>
    SelectedProject?.Packages ?? Enumerable.Empty<PackageRef>();


    public IEnumerable<PackageRef> FilteredPackages
    {
        get
        {
            var baseList = SelectedProject?.Packages ?? Enumerable.Empty<PackageRef>();

            return SelectedFilter switch
            {
                PackageFilter.All =>
                    baseList,

                PackageFilter.Outdated =>
                    baseList.Where(p => p.UpdateType != VersionUpdateType.None && p.LatestVersion != null),

                PackageFilter.MajorOnly =>
                    baseList.Where(p => p.UpdateType == VersionUpdateType.Major),

                PackageFilter.MinorOrMajor =>
                    baseList.Where(p => p.UpdateType == VersionUpdateType.Minor ||
                                        p.UpdateType == VersionUpdateType.Major),

                _ => baseList
            };
        }
    }

    public PackageFilter[] AvailableFilters { get; } =
        (PackageFilter[])Enum.GetValues(typeof(PackageFilter));

    private PackageFilter _selectedFilter = PackageFilter.All;
    public PackageFilter SelectedFilter
    {
        get => _selectedFilter;
        set
        {
            if (value != _selectedFilter)
            {
                _selectedFilter = value;
                SelectedPackage = null;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FilteredPackages));
            }
        }
    }

    public async Task RescanSavedRootsAsync(CancellationToken ct = default)
    {
        await _scanGate.WaitAsync(ct);
        IsScanning = true;
        StatusMessage = "Rescanning saved roots...";
        try
        {
            ProjectGroups.Clear();
            Projects.Clear();
            SelectedProject = null;
            SelectedPackage = null;
            OnPropertyChanged(nameof(FilteredPackages));

            foreach (var root in _settings.RecentRoots.ToList())
            {
                var group = await BuildProjectGroupAsync(
                    root,
                    ct,
                    includeLatestVersions: !OfflineMode,
                    retryUnresolvedLatestVersions: false);
                if (group is not null)
                {
                    ProjectGroups.Add(group);
                    RebuildFlatProjects();

                    if (SelectedProject is null)
                        SelectedProject = group.Projects.FirstOrDefault() ?? Projects.FirstOrDefault();

                    StatusMessage = $"Loaded {ProjectGroups.Count} root(s), {Projects.Count} project(s).";
                    OnPropertyChanged(nameof(FilteredPackages));
                }
            }

            if (!Projects.Any())
            {
                SelectedProject = null;
                SelectedPackage = null;
                StatusMessage = "No saved projects found. Select a folder to scan.";
            }
            else
            {
                StatusMessage = $"Loaded {_settings.RecentRoots.Count} saved root(s), {Projects.Count} project(s).";
            }

            UpdateOutdatedSummaryAndNotify(force: true);

            OnPropertyChanged(nameof(FilteredPackages));
        }
        finally
        {
            IsScanning = false;
            _scanGate.Release();
        }
    }

    public async Task RescanSavedRootsInBackgroundAsync(bool manual = false, CancellationToken ct = default)
    {
        await _scanGate.WaitAsync(ct);
        IsScanning = true;
        StatusMessage = manual ? "Rescanning saved roots..." : "Auto-rescanning saved roots...";
        try
        {
            var selectedProjectPath = SelectedProject?.Path;
            var nextGroups = new List<ProjectGroup>();

            foreach (var root in _settings.RecentRoots.ToList())
            {
                var group = await BuildProjectGroupAsync(
                    root,
                    ct,
                    includeLatestVersions: !OfflineMode,
                    retryUnresolvedLatestVersions: true);
                if (group is not null)
                    nextGroups.Add(group);
            }

            ProjectGroups.Clear();
            foreach (var group in nextGroups)
                ProjectGroups.Add(group);

            RebuildFlatProjects();

            if (Projects.Any())
            {
                SelectedProject = Projects.FirstOrDefault(p =>
                    string.Equals(p.Path, selectedProjectPath, StringComparison.OrdinalIgnoreCase)) ??
                    Projects.First();
                StatusMessage = manual
                    ? $"Rescan complete. Loaded {ProjectGroups.Count} root(s), {Projects.Count} project(s)."
                    : $"Auto-rescan complete. Loaded {ProjectGroups.Count} root(s), {Projects.Count} project(s).";
            }
            else
            {
                SelectedProject = null;
                SelectedPackage = null;
                StatusMessage = manual
                    ? "Rescan complete. No saved projects found."
                    : "Auto-rescan complete. No saved projects found.";
            }

            UpdateOutdatedSummaryAndNotify(force: manual);
            OnPropertyChanged(nameof(FilteredPackages));
        }
        finally
        {
            IsScanning = false;
            _scanGate.Release();
        }
    }


    public string StatusMessage
    {
        get => _statusMessage;
        set
        {
            if (value != _statusMessage)
            {
                _statusMessage = value;
                OnPropertyChanged();
            }
        }
    }

    public MainWindowViewModel()
        : this(settingsPath: null, inspectionService: null, setAutostart: null)
    {
    }

    internal MainWindowViewModel(
        string? settingsPath,
        InspectionService? inspectionService,
        Action<bool>? setAutostart)
    {
        _inspectionService = inspectionService ?? CreateDefaultInspectionService();
        _setAutostart = setAutostart ?? AutostartService.SetAutostart;

        _settingsPath = settingsPath ?? GetSettingsPath();
        LoadSettings();

        _isDarkMode = _settings.DarkMode;
        _offlineMode = _settings.OfflineMode;
        _nuGetSourceUrl = _settings.NuGetSourceUrl ?? string.Empty;
        _npmRegistryBaseUrl = _settings.NpmRegistryBaseUrl ?? string.Empty;
        _goProxyBaseUrl = _settings.GoProxyBaseUrl ?? string.Empty;
        _pythonPackageIndexBaseUrl = _settings.PythonPackageIndexBaseUrl ?? string.Empty;
        _packagistMetadataBaseUrl = _settings.PackagistMetadataBaseUrl ?? string.Empty;
        _mavenSearchBaseUrl = _settings.MavenSearchBaseUrl ?? string.Empty;
        _cratesApiBaseUrl = _settings.CratesApiBaseUrl ?? string.Empty;
        _gitHubApiBaseUrl = _settings.GitHubApiBaseUrl ?? string.Empty;
        _startWithSystem = _settings.StartWithSystem;
        _useSystemTray = _settings.UseSystemTray;
        _autoRescan = _settings.AutoRescan;
        _checkUpdatesOnStart = _settings.CheckUpdatesOnStart;
        _autoDownloadUpdates = _settings.AutoDownloadUpdates;
        _enableNotifications = _settings.EnableNotifications;
        var app = Application.Current;
        if (app is not null)
            app.RequestedThemeVariant = _isDarkMode ? ThemeVariant.Dark : ThemeVariant.Light;

        _setAutostart(_startWithSystem);
    }

    private static InspectionService CreateDefaultInspectionService()
    {
        var handlers = new IEcosystemHandler[]
        {
            new DotNetEcosystemHandler(),
            new NpmEcosystemHandler(),
            new PythonEcosystemHandler(),
            new JavaEcosystemHandler(),
            new PhpEcosystemHandler(),
            new GoEcosystemHandler(),
            new RustEcosystemHandler(),
            new GitHubActionsEcosystemHandler()
        };
        return new InspectionService(handlers);
    }

    private static string GetSettingsPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "DepScope");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "settings.json");
    }

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                _settings = JsonSerializer.Deserialize<UserSettings>(json) ?? new UserSettings();
            }
            else
            {
                _settings = new UserSettings();
            }
        }
        catch
        {
            _settings = new UserSettings();
        }
    }

    private void SaveSettings()
    {
        try
        {
            var settingsDir = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrWhiteSpace(settingsDir))
                Directory.CreateDirectory(settingsDir);

            var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(_settingsPath, json);
        }
        catch
        {
            // ignore write errors
        }
    }

    public bool IsDarkMode
    {
        get => _isDarkMode;
        set
        {
            if (value != _isDarkMode)
            {
                _isDarkMode = value;
                OnPropertyChanged();

                _settings.DarkMode = value;
                SaveSettings();

                var app = Application.Current;
                if (app is not null)
                {
                    app.RequestedThemeVariant = value ? ThemeVariant.Dark : ThemeVariant.Light;
                }
            }
        }
    }

    public bool OfflineMode
    {
        get => _offlineMode;
        set
        {
            if (value != _offlineMode)
            {
                _offlineMode = value;
                OnPropertyChanged();

                _settings.OfflineMode = value;
                SaveSettings();
            }
        }
    }

    public string NuGetSourceUrl
    {
        get => _nuGetSourceUrl;
        set
        {
            var normalizedValue = value ?? string.Empty;
            if (normalizedValue != _nuGetSourceUrl)
            {
                _nuGetSourceUrl = normalizedValue;
                OnPropertyChanged();

                _settings.NuGetSourceUrl = normalizedValue;
                SaveSettings();
            }
        }
    }

    public string NpmRegistryBaseUrl
    {
        get => _npmRegistryBaseUrl;
        set
        {
            var normalizedValue = value ?? string.Empty;
            if (normalizedValue != _npmRegistryBaseUrl)
            {
                _npmRegistryBaseUrl = normalizedValue;
                OnPropertyChanged();

                _settings.NpmRegistryBaseUrl = normalizedValue;
                SaveSettings();
            }
        }
    }

    public string GoProxyBaseUrl
    {
        get => _goProxyBaseUrl;
        set
        {
            var normalizedValue = value ?? string.Empty;
            if (normalizedValue != _goProxyBaseUrl)
            {
                _goProxyBaseUrl = normalizedValue;
                OnPropertyChanged();

                _settings.GoProxyBaseUrl = normalizedValue;
                SaveSettings();
            }
        }
    }

    public string PythonPackageIndexBaseUrl
    {
        get => _pythonPackageIndexBaseUrl;
        set
        {
            var normalizedValue = value ?? string.Empty;
            if (normalizedValue != _pythonPackageIndexBaseUrl)
            {
                _pythonPackageIndexBaseUrl = normalizedValue;
                OnPropertyChanged();

                _settings.PythonPackageIndexBaseUrl = normalizedValue;
                SaveSettings();
            }
        }
    }

    public string PackagistMetadataBaseUrl
    {
        get => _packagistMetadataBaseUrl;
        set
        {
            var normalizedValue = value ?? string.Empty;
            if (normalizedValue != _packagistMetadataBaseUrl)
            {
                _packagistMetadataBaseUrl = normalizedValue;
                OnPropertyChanged();

                _settings.PackagistMetadataBaseUrl = normalizedValue;
                SaveSettings();
            }
        }
    }

    public string MavenSearchBaseUrl
    {
        get => _mavenSearchBaseUrl;
        set
        {
            var normalizedValue = value ?? string.Empty;
            if (normalizedValue != _mavenSearchBaseUrl)
            {
                _mavenSearchBaseUrl = normalizedValue;
                OnPropertyChanged();

                _settings.MavenSearchBaseUrl = normalizedValue;
                SaveSettings();
            }
        }
    }

    public string CratesApiBaseUrl
    {
        get => _cratesApiBaseUrl;
        set
        {
            var normalizedValue = value ?? string.Empty;
            if (normalizedValue != _cratesApiBaseUrl)
            {
                _cratesApiBaseUrl = normalizedValue;
                OnPropertyChanged();

                _settings.CratesApiBaseUrl = normalizedValue;
                SaveSettings();
            }
        }
    }

    public string GitHubApiBaseUrl
    {
        get => _gitHubApiBaseUrl;
        set
        {
            var normalizedValue = value ?? string.Empty;
            if (normalizedValue != _gitHubApiBaseUrl)
            {
                _gitHubApiBaseUrl = normalizedValue;
                OnPropertyChanged();

                _settings.GitHubApiBaseUrl = normalizedValue;
                SaveSettings();
            }
        }
    }

    private RegistrySourceOptions CreateRegistrySourceOptions()
    {
        return new RegistrySourceOptions
        {
            NuGetSourceUrl = NuGetSourceUrl,
            NpmRegistryBaseUrl = NpmRegistryBaseUrl,
            GoProxyBaseUrl = GoProxyBaseUrl,
            PythonPackageIndexBaseUrl = PythonPackageIndexBaseUrl,
            PackagistMetadataBaseUrl = PackagistMetadataBaseUrl,
            MavenSearchBaseUrl = MavenSearchBaseUrl,
            CratesApiBaseUrl = CratesApiBaseUrl,
            GitHubApiBaseUrl = GitHubApiBaseUrl
        };
    }

    public bool StartWithSystem
    {
        get => _startWithSystem;
        set
        {
            if (value != _startWithSystem)
            {
                _startWithSystem = value;
                OnPropertyChanged();

                _settings.StartWithSystem = value;
                SaveSettings();

                _setAutostart(value);
            }
        }
    }

    public bool UseSystemTray
    {
        get => _useSystemTray;
        set
        {
            if (value != _useSystemTray)
            {
                _useSystemTray = value;
                OnPropertyChanged();

                _settings.UseSystemTray = value;
                SaveSettings();
                // TrayIcon will be enabled/disabled from MainWindow code-behind
            }
        }
    }

    public bool AutoRescan
    {
        get => _autoRescan;
        set
        {
            if (value != _autoRescan)
            {
                _autoRescan = value;
                OnPropertyChanged();

                _settings.AutoRescan = value;
                SaveSettings();
                // Timer start/stop is handled in MainWindow
            }
        }
    }

    public bool CheckUpdatesOnStart
    {
        get => _checkUpdatesOnStart;
        set
        {
            if (value != _checkUpdatesOnStart)
            {
                _checkUpdatesOnStart = value;
                OnPropertyChanged();
                _settings.CheckUpdatesOnStart = value;
                SaveSettings();
            }
        }
    }

    public bool AutoDownloadUpdates
    {
        get => _autoDownloadUpdates;
        set
        {
            if (value != _autoDownloadUpdates)
            {
                _autoDownloadUpdates = value;
                OnPropertyChanged();

                _settings.AutoDownloadUpdates = value;
                SaveSettings();
            }
        }
    }



    public async Task ScanFolderAsync(string folderPath, bool append = false, CancellationToken ct = default)
    {
        await _scanGate.WaitAsync(ct);
        IsScanning = true;
        StatusMessage = $"Scanning {folderPath}...";
        try
        {
            var nextGroup = await BuildProjectGroupAsync(
                folderPath,
                ct,
                includeLatestVersions: !OfflineMode,
                retryUnresolvedLatestVersions: false);

            if (!append)
            {
                ProjectGroups.Clear();
                Projects.Clear();
                SelectedProject = null;
                SelectedPackage = null;
                OnPropertyChanged(nameof(FilteredPackages));
                _settings.RecentRoots.Clear();
            }

            var group = ProjectGroups.FirstOrDefault(g =>
                string.Equals(g.RootPath, folderPath, StringComparison.OrdinalIgnoreCase));

            if (group is not null)
            {
                ProjectGroups.Remove(group);
                group = null;
            }

            if (nextGroup is not null)
            {
                ProjectGroups.Add(nextGroup);
                group = nextGroup;
            }

            RebuildFlatProjects();

            if (!string.IsNullOrWhiteSpace(folderPath) &&
                !_settings.RecentRoots.Contains(folderPath, StringComparer.OrdinalIgnoreCase))
            {
                _settings.RecentRoots.Add(folderPath);
                SaveSettings();
            }

            if (Projects.Any())
            {
                if (SelectedProject is null)
                    SelectedProject = group?.Projects.FirstOrDefault() ?? Projects.First();

                var groupName = group?.Name ?? GetRootDisplayName(folderPath);
                var groupProjectCount = group?.Projects.Count ?? 0;
                StatusMessage = $"Found {groupProjectCount} projects under {groupName}. Total projects: {Projects.Count}.";
            }
            else
            {
                StatusMessage = "No supported projects found in this folder.";
            }

            UpdateOutdatedSummaryAndNotify(force: true);

            OnPropertyChanged(nameof(FilteredPackages));
        }
        finally
        {
            IsScanning = false;
            _scanGate.Release();
        }
    }

    public async Task RetryUnresolvedLatestVersionsInBackgroundAsync(CancellationToken ct = default)
    {
        if (OfflineMode || !Projects.Any())
            return;

        await _scanGate.WaitAsync(ct);
        IsScanning = true;
        StatusMessage = "Rechecking unresolved latest versions...";
        try
        {
            var didRetry = await _inspectionService.RetryUnresolvedLatestVersionsAsync(
                Projects.ToList(),
                CreateRegistrySourceOptions(),
                ct);

            if (!didRetry)
            {
                StatusMessage = $"Loaded {ProjectGroups.Count} root(s), {Projects.Count} project(s).";
                return;
            }

            UpdateOutdatedSummaryAndNotify();
            OnPropertyChanged(nameof(ProjectGroups));
            OnPropertyChanged(nameof(Projects));
            OnPropertyChanged(nameof(FilteredPackages));
            StatusMessage = $"Latest-version recheck complete. Loaded {ProjectGroups.Count} root(s), {Projects.Count} project(s).";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            StatusMessage = $"Loaded {ProjectGroups.Count} root(s), {Projects.Count} project(s).";
        }
        finally
        {
            IsScanning = false;
            _scanGate.Release();
        }
    }

    private async Task<ProjectGroup?> BuildProjectGroupAsync(
        string root,
        CancellationToken ct,
        bool includeLatestVersions,
        bool retryUnresolvedLatestVersions)
    {
        if (!Directory.Exists(root))
            return null;

        var group = new ProjectGroup(GetRootDisplayName(root), root);

        var projects = await _inspectionService.InspectRootAsync(
            root,
            ct,
            includeLatestVersions: includeLatestVersions,
            registrySourceOptions: CreateRegistrySourceOptions(),
            retryUnresolvedLatestVersions: retryUnresolvedLatestVersions);

        foreach (var proj in projects)
        {
            if (proj.Packages != null && proj.Packages.Count > 0)
                group.Projects.Add(proj);
        }

        return group.Projects.Any() ? group : null;
    }

    private static string GetRootDisplayName(string root)
    {
        var trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var groupName = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(groupName) ? root : groupName;
    }



    public Task RemoveSelectedProjectAsync()
    {
        if (SelectedProject is null)
            return Task.CompletedTask;

        var toRemove = SelectedProject;

        // Find the group containing this project
        var group = ProjectGroups.FirstOrDefault(g => g.Projects.Contains(toRemove));
        if (group != null)
        {
            // Remove project from that group
            group.Projects.Remove(toRemove);

            // If the group is now empty, remove the group and its root
            if (!group.Projects.Any())
            {
                ProjectGroups.Remove(group);

                // Remove that root from settings
                _settings.RecentRoots.Remove(group.RootPath);
            }
        }

        // Rebuild flat list from groups
        RebuildFlatProjects();

        SaveSettings();

        // Select another project if any
        SelectedProject = Projects.FirstOrDefault();
        StatusMessage =
            $"Removed '{toRemove.Name}'. Now tracking {_settings.RecentRoots.Count} root(s), {Projects.Count} project(s).";

        OnPropertyChanged(nameof(FilteredPackages));

        return Task.CompletedTask;
    }


    private void RebuildFlatProjects()
    {
        Projects.Clear();
        foreach (var p in ProjectGroups.SelectMany(g => g.Projects))
            Projects.Add(p);
    }

    private void UpdateOutdatedSummaryAndNotify(bool force = false)
    {
        if (!EnableNotifications)
            return;

        var allProjects = ProjectGroups
            .SelectMany(g => g.Projects)
            .ToList();

        if (!allProjects.Any())
            return;

        // Count projects that have at least one outdated package
        int projectsWithOutdated = allProjects
            .Count(p => p.Packages.Any(pkg =>
                pkg.UpdateType != VersionUpdateType.None &&
                pkg.UpdateType != VersionUpdateType.Unknown &&
                !string.IsNullOrWhiteSpace(pkg.LatestVersion)));

        // Count total outdated packages
        int totalOutdated = allProjects
            .SelectMany(p => p.Packages)
            .Count(pkg =>
                pkg.UpdateType != VersionUpdateType.None &&
                pkg.UpdateType != VersionUpdateType.Unknown &&
                !string.IsNullOrWhiteSpace(pkg.LatestVersion));

        // Don't spam if nothing changed
        if (!force &&
            projectsWithOutdated == _lastOutdatedProjects &&
            totalOutdated == _lastOutdatedPackages)
        {
            return;
        }

        _lastOutdatedProjects = projectsWithOutdated;
        _lastOutdatedPackages = totalOutdated;

        OutdatedSummaryChanged?.Invoke(projectsWithOutdated, totalOutdated);
    }



    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
