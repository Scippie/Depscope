using DepScope.Core;
using DepScope.Core.Ecosystems;
using DepScope.Core.Models;
using DepScope.Desktop.Converters;
using DepScope.Desktop.ViewModels;
using Xunit;

namespace DepScope.Tests;

public sealed class MainWindowViewModelTests : IDisposable
{
    private readonly string _rootPath = CreateTempDirectory();
    private readonly string _settingsPath;

    public MainWindowViewModelTests()
    {
        _settingsPath = Path.Combine(_rootPath, "settings", "settings.json");
    }

    [Fact]
    public async Task RemoveSelectedProject_KeepsSavedRootWhenGroupStillHasProjects()
    {
        var handler = new FakeHandler(
            new ProjectInfo
            {
                Name = "ProjectA",
                Path = Path.Combine(_rootPath, "ProjectA", "ProjectA.csproj"),
                Ecosystem = Ecosystem.DotNet,
                Packages = { CreatePackage("Package.A") }
            },
            new ProjectInfo
            {
                Name = "ProjectB",
                Path = Path.Combine(_rootPath, "ProjectB", "ProjectB.csproj"),
                Ecosystem = Ecosystem.DotNet,
                Packages = { CreatePackage("Package.B") }
            });

        var viewModel = CreateViewModel(handler);
        await viewModel.ScanFolderAsync(
            _rootPath,
            append: true,
            TestContext.Current.CancellationToken);

        viewModel.SelectedProject = viewModel.Projects.Single(p => p.Name == "ProjectA");
        await viewModel.RemoveSelectedProjectAsync();

        Assert.DoesNotContain(viewModel.Projects, p => p.Name == "ProjectA");
        Assert.Contains(viewModel.Projects, p => p.Name == "ProjectB");
        Assert.Single(viewModel.ProjectGroups);
        Assert.Equal(1, viewModel.RiskSummary.TotalProjects);

        var settingsJson = await File.ReadAllTextAsync(
            _settingsPath,
            TestContext.Current.CancellationToken);
        Assert.Contains(_rootPath.Replace("\\", "\\\\"), settingsJson);
    }

    [Fact]
    public void RiskSummary_CountsProjectsPackagesAndSeverityByPriority()
    {
        var projects = new[]
        {
            new ProjectInfo
            {
                Name = "CleanProject",
                Path = Path.Combine(_rootPath, "CleanProject", "CleanProject.csproj"),
                Ecosystem = Ecosystem.DotNet,
                Packages =
                {
                    new PackageRef
                    {
                        Ecosystem = Ecosystem.DotNet,
                        PackageName = "Package.Clean",
                        DeclaredVersion = "1.0.0",
                        UpdateType = VersionUpdateType.None,
                        VulnerabilitySeverity = VulnerabilitySeverity.None
                    }
                }
            },
            new ProjectInfo
            {
                Name = "OutdatedProject",
                Path = Path.Combine(_rootPath, "OutdatedProject", "OutdatedProject.csproj"),
                Ecosystem = Ecosystem.DotNet,
                Packages =
                {
                    new PackageRef
                    {
                        Ecosystem = Ecosystem.DotNet,
                        PackageName = "Package.Outdated",
                        DeclaredVersion = "1.0.0",
                        LatestVersion = "2.0.0",
                        UpdateType = VersionUpdateType.Major,
                        VulnerabilitySeverity = VulnerabilitySeverity.None
                    }
                }
            },
            new ProjectInfo
            {
                Name = "VulnerableProject",
                Path = Path.Combine(_rootPath, "VulnerableProject", "VulnerableProject.csproj"),
                Ecosystem = Ecosystem.DotNet,
                Packages =
                {
                    new PackageRef
                    {
                        Ecosystem = Ecosystem.DotNet,
                        PackageName = "Package.Vulnerable",
                        DeclaredVersion = "1.0.0",
                        UpdateType = VersionUpdateType.None,
                        VulnerabilitySeverity = VulnerabilitySeverity.Critical,
                        VulnerabilityCount = 1
                    }
                }
            },
            new ProjectInfo
            {
                Name = "UnknownProject",
                Path = Path.Combine(_rootPath, "UnknownProject", "UnknownProject.csproj"),
                Ecosystem = Ecosystem.DotNet,
                Packages =
                {
                    new PackageRef
                    {
                        Ecosystem = Ecosystem.DotNet,
                        PackageName = "Package.Unknown",
                        DeclaredVersion = "1.0.0",
                        UpdateType = VersionUpdateType.Unknown,
                        VulnerabilitySeverity = VulnerabilitySeverity.NotChecked
                    }
                }
            }
        };

        var summary = WorkspaceRiskSummary.FromProjects(projects);

        Assert.Equal(4, summary.TotalProjects);
        Assert.Equal(1, summary.CleanProjects);
        Assert.Equal(1, summary.OutdatedProjects);
        Assert.Equal(1, summary.VulnerableProjects);
        Assert.Equal(1, summary.UnknownProjects);
        Assert.Equal(4, summary.TotalPackages);
        Assert.Equal(1, summary.OutdatedPackages);
        Assert.Equal(1, summary.VulnerablePackages);
        Assert.Equal(1, summary.CriticalSeverityPackages);
        Assert.Equal(0, summary.HighSeverityPackages);
    }

    [Fact]
    public async Task OfflineMode_SkipsLatestVersionEnrichment()
    {
        var handler = new FakeHandler(new ProjectInfo
        {
            Name = "ProjectA",
            Path = Path.Combine(_rootPath, "ProjectA", "ProjectA.csproj"),
            Ecosystem = Ecosystem.DotNet,
            Packages = { CreatePackage("Package.A") }
        });

        var viewModel = CreateViewModel(handler);
        viewModel.OfflineMode = true;

        await viewModel.ScanFolderAsync(
            _rootPath,
            append: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, handler.ScanCalls);
        Assert.Equal(0, handler.EnrichCalls);
        Assert.Null(Assert.Single(viewModel.Projects).Packages.Single().LatestVersion);
    }

    [Fact]
    public async Task RegistrySourceSettings_ArePersisted()
    {
        var viewModel = CreateViewModel(new FakeHandler());

        viewModel.NuGetSourceUrl = "https://nuget.example.test/v3/index.json";
        viewModel.NpmRegistryBaseUrl = "https://npm.example.test/";
        viewModel.GoProxyBaseUrl = "https://go.example.test/";
        viewModel.PythonPackageIndexBaseUrl = "https://python.example.test/pypi/";
        viewModel.PackagistMetadataBaseUrl = "https://packagist.example.test/p2/";
        viewModel.MavenSearchBaseUrl = "https://maven.example.test/solrsearch/select";
        viewModel.CratesApiBaseUrl = "https://crates.example.test/api/v1/";
        viewModel.GitHubApiBaseUrl = "https://github.example.test/api/v3/";

        var settingsJson = await File.ReadAllTextAsync(
            _settingsPath,
            TestContext.Current.CancellationToken);

        Assert.Contains(
            "\"NuGetSourceUrl\": \"https://nuget.example.test/v3/index.json\"",
            settingsJson);
        Assert.Contains(
            "\"NpmRegistryBaseUrl\": \"https://npm.example.test/\"",
            settingsJson);
        Assert.Contains(
            "\"GoProxyBaseUrl\": \"https://go.example.test/\"",
            settingsJson);
        Assert.Contains(
            "\"PythonPackageIndexBaseUrl\": \"https://python.example.test/pypi/\"",
            settingsJson);
        Assert.Contains(
            "\"PackagistMetadataBaseUrl\": \"https://packagist.example.test/p2/\"",
            settingsJson);
        Assert.Contains(
            "\"MavenSearchBaseUrl\": \"https://maven.example.test/solrsearch/select\"",
            settingsJson);
        Assert.Contains(
            "\"CratesApiBaseUrl\": \"https://crates.example.test/api/v1/\"",
            settingsJson);
        Assert.Contains(
            "\"GitHubApiBaseUrl\": \"https://github.example.test/api/v3/\"",
            settingsJson);
    }

    [Fact]
    public async Task NotificationSetting_IsPersisted()
    {
        var viewModel = CreateViewModel(new FakeHandler());

        viewModel.EnableNotifications = false;

        var settingsJson = await File.ReadAllTextAsync(
            _settingsPath,
            TestContext.Current.CancellationToken);

        Assert.Contains("\"EnableNotifications\": false", settingsJson);
        Assert.DoesNotContain("EnableNativeNotifications", settingsJson);
    }

    [Fact]
    public void SelectedPackage_ExposesVulnerabilityAdvisoryIds()
    {
        var viewModel = CreateViewModel(new FakeHandler());

        viewModel.SelectedPackage = new PackageRef
        {
            Ecosystem = Ecosystem.Npm,
            PackageName = "lodash",
            DeclaredVersion = "4.17.20",
            VulnerabilityIds = "GHSA-35jh-r3h4-6jhm, CVE-2021-23337"
        };

        Assert.True(viewModel.HasSelectedVulnerabilityAdvisories);
        Assert.Equal(
            "GHSA-35jh-r3h4-6jhm, CVE-2021-23337",
            viewModel.SelectedVulnerabilityAdvisories);
    }

    [Fact]
    public void SelectedPackage_ExposesRichVulnerabilityAdvisoryDetails()
    {
        var viewModel = CreateViewModel(new FakeHandler());

        viewModel.SelectedPackage = new PackageRef
        {
            Ecosystem = Ecosystem.Npm,
            PackageName = "vite",
            DeclaredVersion = "5.0.0",
            VulnerabilityAdvisories =
            {
                new VulnerabilityAdvisory
                {
                    Id = "GHSA-test",
                    Severity = VulnerabilitySeverity.Medium,
                    AffectedPackageName = "transitive-package",
                    AffectedVersion = "1.2.3",
                    Relationship = "vite > transitive-package",
                    Summary = "Example advisory.",
                    Url = "https://osv.dev/vulnerability/GHSA-test"
                }
            }
        };

        Assert.True(viewModel.HasSelectedVulnerabilityAdvisories);
        Assert.Contains("Affected: transitive-package@1.2.3", viewModel.SelectedVulnerabilityAdvisories);
        Assert.Contains("Via: vite > transitive-package", viewModel.SelectedVulnerabilityAdvisories);
        Assert.Contains("Link: https://osv.dev/vulnerability/GHSA-test", viewModel.SelectedVulnerabilityAdvisories);
    }

    [Fact]
    public void ProjectStatusBrush_PrioritizesVulnerableProjectsOverUpToDateState()
    {
        var converter = new ProjectStatusToBrushConverter();
        var project = new ProjectInfo
        {
            Name = "ProjectA",
            Path = Path.Combine(_rootPath, "ProjectA", "package.json"),
            Ecosystem = Ecosystem.Npm,
            Packages =
            {
                new PackageRef
                {
                    Ecosystem = Ecosystem.Npm,
                    PackageName = "vite",
                    DeclaredVersion = "5.0.0",
                    LatestVersion = "5.0.0",
                    UpdateType = VersionUpdateType.None,
                    VulnerabilitySeverity = VulnerabilitySeverity.Medium,
                    VulnerabilityCount = 1
                }
            }
        };

        var brush = converter.Convert(
            project,
            typeof(object),
            parameter: null,
            culture: System.Globalization.CultureInfo.InvariantCulture);

        Assert.Same(converter.VulnerableBrush, brush);
    }

    [Fact]
    public void ProjectStatusBrush_ShowsUnknownWhenChecksAreUnresolved()
    {
        var converter = new ProjectStatusToBrushConverter();
        var project = new ProjectInfo
        {
            Name = "ProjectA",
            Path = Path.Combine(_rootPath, "ProjectA", "package.json"),
            Ecosystem = Ecosystem.Npm,
            Packages =
            {
                new PackageRef
                {
                    Ecosystem = Ecosystem.Npm,
                    PackageName = "vite",
                    DeclaredVersion = "5.0.0",
                    UpdateType = VersionUpdateType.Unknown,
                    VulnerabilitySeverity = VulnerabilitySeverity.NotChecked
                }
            }
        };

        var brush = converter.Convert(
            project,
            typeof(object),
            parameter: null,
            culture: System.Globalization.CultureInfo.InvariantCulture);

        Assert.Same(converter.UnknownBrush, brush);
    }

    [Fact]
    public void VulnerabilitySeverityIcon_UsesCompactSeverityMarkers()
    {
        var converter = new VulnerabilitySeverityToIconConverter();

        Assert.Equal(
            "OK",
            converter.Convert(
                VulnerabilitySeverity.None,
                typeof(string),
                parameter: null,
                culture: System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(
            "!!!",
            converter.Convert(
                VulnerabilitySeverity.Critical,
                typeof(string),
                parameter: null,
                culture: System.Globalization.CultureInfo.InvariantCulture));
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
            Directory.Delete(_rootPath, recursive: true);
    }

    private MainWindowViewModel CreateViewModel(FakeHandler handler)
    {
        return new MainWindowViewModel(
            _settingsPath,
            new InspectionService(new[] { handler }),
            setAutostart: _ => { });
    }

    private static PackageRef CreatePackage(string name)
    {
        return new PackageRef
        {
            Ecosystem = Ecosystem.DotNet,
            PackageName = name,
            DeclaredVersion = "1.0.0"
        };
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "DepScope.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeHandler : IEcosystemHandler
    {
        private readonly IReadOnlyList<ProjectInfo> _projects;

        public FakeHandler(params ProjectInfo[] projects)
        {
            _projects = projects;
        }

        public Ecosystem Ecosystem => Ecosystem.DotNet;
        public int ScanCalls { get; private set; }
        public int EnrichCalls { get; private set; }

        public bool CanHandleDirectory(string rootPath) => true;

        public Task<IReadOnlyList<ProjectInfo>> ScanProjectsAsync(
            string rootPath,
            CancellationToken ct = default)
        {
            ScanCalls++;
            return Task.FromResult(_projects);
        }

        public Task EnrichWithLatestVersionsAsync(
            IReadOnlyList<ProjectInfo> projects,
            CancellationToken ct = default)
        {
            EnrichCalls++;
            foreach (var project in projects)
            {
                foreach (var package in project.Packages)
                    package.LatestVersion = "2.0.0";
            }

            return Task.CompletedTask;
        }
    }
}
