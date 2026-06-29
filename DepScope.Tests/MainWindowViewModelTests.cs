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
        Assert.Single(viewModel.UpdateRecommendations);
        Assert.Equal("Package.B", viewModel.UpdateRecommendations.Single().PackageName);

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
    public void UpdateRecommendations_OrderActionableItemsByPriority()
    {
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
                    PackageName = "major-only",
                    DeclaredVersion = "1.0.0",
                    LatestVersion = "2.0.0",
                    UpdateType = VersionUpdateType.Major,
                    VulnerabilitySeverity = VulnerabilitySeverity.None
                },
                new PackageRef
                {
                    Ecosystem = Ecosystem.Npm,
                    PackageName = "critical-vuln",
                    DeclaredVersion = "1.0.0",
                    LatestVersion = "1.0.1",
                    UpdateType = VersionUpdateType.Patch,
                    VulnerabilitySeverity = VulnerabilitySeverity.Critical,
                    VulnerabilityCount = 1
                },
                new PackageRef
                {
                    Ecosystem = Ecosystem.Npm,
                    PackageName = "medium-vuln",
                    DeclaredVersion = "1.0.0",
                    UpdateType = VersionUpdateType.None,
                    VulnerabilitySeverity = VulnerabilitySeverity.Medium,
                    VulnerabilityCount = 1
                },
                new PackageRef
                {
                    Ecosystem = Ecosystem.Npm,
                    PackageName = "unknown-check",
                    DeclaredVersion = "1.0.0",
                    UpdateType = VersionUpdateType.Unknown,
                    VulnerabilitySeverity = VulnerabilitySeverity.NotChecked
                }
            }
        };

        var recommendations = UpdateRecommendation.FromProjects(new[] { project });

        Assert.Equal(
            new[] { "critical-vuln", "medium-vuln", "major-only", "unknown-check" },
            recommendations.Select(recommendation => recommendation.PackageName));
        Assert.Equal("Urgent", recommendations[0].PriorityLabel);
        Assert.Contains("update is available", recommendations[0].Reason);
        Assert.Equal("Review", recommendations[recommendations.Count - 1].PriorityLabel);
    }

    [Fact]
    public void UpdateRecommendations_LimitToTopFive()
    {
        var project = new ProjectInfo
        {
            Name = "ProjectA",
            Path = Path.Combine(_rootPath, "ProjectA", "package.json"),
            Ecosystem = Ecosystem.Npm
        };

        for (var i = 0; i < 8; i++)
        {
            project.Packages.Add(new PackageRef
            {
                Ecosystem = Ecosystem.Npm,
                PackageName = $"package-{i}",
                DeclaredVersion = "1.0.0",
                LatestVersion = "1.0.1",
                UpdateType = VersionUpdateType.Patch,
                VulnerabilitySeverity = VulnerabilitySeverity.None
            });
        }

        var recommendations = UpdateRecommendation.FromProjects(new[] { project });

        Assert.Equal(5, recommendations.Count);
    }

    [Fact]
    public async Task SuppressSelectedPackageUpdate_PersistsRuleAndRemovesAdvisorItem()
    {
        var handler = new FakeHandler(new ProjectInfo
        {
            Name = "ProjectA",
            Path = Path.Combine(_rootPath, "ProjectA", "ProjectA.csproj"),
            Ecosystem = Ecosystem.DotNet,
            Packages =
            {
                new PackageRef
                {
                    Ecosystem = Ecosystem.DotNet,
                    PackageName = "Package.A",
                    DeclaredVersion = "1.0.0",
                    LatestVersion = "2.0.0",
                    UpdateType = VersionUpdateType.Major,
                    VulnerabilitySeverity = VulnerabilitySeverity.None
                }
            }
        });
        var viewModel = CreateViewModel(handler);
        viewModel.OfflineMode = true;
        await viewModel.ScanFolderAsync(
            _rootPath,
            append: true,
            TestContext.Current.CancellationToken);
        viewModel.SelectedPackage = viewModel.SelectedProject!.Packages.Single();

        Assert.Single(viewModel.UpdateRecommendations);

        viewModel.SuppressSelectedPackageUpdate();

        Assert.Empty(viewModel.UpdateRecommendations);
        Assert.NotNull(viewModel.SelectedPackageDetail);
        Assert.True(viewModel.SelectedPackageDetail.IsUpdateSuppressed);
        Assert.Equal("Update accepted", viewModel.SelectedPackageDetail.SuppressionStatus);

        var settingsJson = await File.ReadAllTextAsync(
            _settingsPath,
            TestContext.Current.CancellationToken);
        Assert.Contains("\"PackageName\": \"Package.A\"", settingsJson);
        Assert.Contains("\"Reason\": \"Accepted package update\"", settingsJson);

        viewModel.ClearSelectedPackageUpdateSuppression();

        Assert.Single(viewModel.UpdateRecommendations);
        Assert.NotNull(viewModel.SelectedPackageDetail);
        Assert.False(viewModel.SelectedPackageDetail.IsUpdateSuppressed);
    }

    [Fact]
    public async Task SuppressSelectedPackageAdvisories_PersistsRulesAndRemovesAdvisorItem()
    {
        var handler = new FakeHandler(new ProjectInfo
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
                    UpdateType = VersionUpdateType.None,
                    VulnerabilitySeverity = VulnerabilitySeverity.High,
                    VulnerabilityCount = 1,
                    VulnerabilityIds = "GHSA-test"
                }
            }
        });
        var viewModel = CreateViewModel(handler);
        viewModel.OfflineMode = true;
        await viewModel.ScanFolderAsync(
            _rootPath,
            append: true,
            TestContext.Current.CancellationToken);
        viewModel.SelectedPackage = viewModel.SelectedProject!.Packages.Single();

        Assert.Single(viewModel.UpdateRecommendations);

        viewModel.SuppressSelectedPackageAdvisories();

        Assert.Empty(viewModel.UpdateRecommendations);
        Assert.NotNull(viewModel.SelectedPackageDetail);
        Assert.True(viewModel.SelectedPackageDetail.HasSuppressedAdvisories);
        Assert.Equal("Accepted", viewModel.SelectedPackageDetail.Advisories.Single().Status);

        var settingsJson = await File.ReadAllTextAsync(
            _settingsPath,
            TestContext.Current.CancellationToken);
        Assert.Contains("\"PackageName\": \"vite\"", settingsJson);
        Assert.Contains("\"AdvisoryId\": \"GHSA-test\"", settingsJson);

        viewModel.ClearSelectedPackageAdvisorySuppressions();

        Assert.Single(viewModel.UpdateRecommendations);
        Assert.NotNull(viewModel.SelectedPackageDetail);
        Assert.False(viewModel.SelectedPackageDetail.HasSuppressedAdvisories);
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
        Assert.True(viewModel.HasSelectedPackageDetail);
        Assert.NotNull(viewModel.SelectedPackageDetail);
        Assert.Equal(2, viewModel.SelectedPackageDetail.Advisories.Count);
        Assert.Equal("GHSA-35jh-r3h4-6jhm", viewModel.SelectedPackageDetail.Advisories[0].Id);
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
        Assert.NotNull(viewModel.SelectedPackageDetail);
        var advisory = Assert.Single(viewModel.SelectedPackageDetail.Advisories);
        Assert.Equal("GHSA-test", advisory.Id);
        Assert.Equal("Medium", advisory.Severity);
        Assert.Equal("transitive-package", advisory.AffectedPackage);
        Assert.Equal("1.2.3", advisory.AffectedVersion);
        Assert.Equal("vite > transitive-package", advisory.Relationship);
        Assert.Equal("https://osv.dev/vulnerability/GHSA-test", advisory.Url);
    }

    [Fact]
    public void SelectedPackageDetail_ExposesCorePackageDetailsWithoutAdvisories()
    {
        var viewModel = CreateViewModel(new FakeHandler());

        viewModel.SelectedPackage = new PackageRef
        {
            Ecosystem = Ecosystem.DotNet,
            PackageName = "Newtonsoft.Json",
            DeclaredVersion = "13.0.1",
            InstalledVersion = "13.0.1",
            LatestVersion = "13.0.3",
            UpdateType = VersionUpdateType.Patch,
            VulnerabilitySeverity = VulnerabilitySeverity.None,
            RelatedSecurityPackages =
            {
                new RelatedSecurityPackage
                {
                    Ecosystem = Ecosystem.DotNet,
                    PackageName = "Transitive.Package",
                    Version = "1.2.3",
                    Relationship = "Newtonsoft.Json > Transitive.Package"
                }
            }
        };

        Assert.True(viewModel.HasSelectedPackageDetail);
        Assert.NotNull(viewModel.SelectedPackageDetail);
        Assert.Equal("Newtonsoft.Json", viewModel.SelectedPackageDetail.PackageName);
        Assert.Equal(Ecosystem.DotNet, viewModel.SelectedPackageDetail.Ecosystem);
        Assert.Equal("13.0.1", viewModel.SelectedPackageDetail.DeclaredVersion);
        Assert.Equal("13.0.1", viewModel.SelectedPackageDetail.InstalledVersion);
        Assert.Equal("13.0.3", viewModel.SelectedPackageDetail.LatestVersion);
        Assert.Equal(VersionUpdateType.Patch, viewModel.SelectedPackageDetail.UpdateType);
        Assert.Equal("None", viewModel.SelectedPackageDetail.VulnerabilityStatus);
        Assert.Equal(1, viewModel.SelectedPackageDetail.RelatedSecurityPackageCount);
        Assert.False(viewModel.SelectedPackageDetail.HasAdvisories);
    }

    [Fact]
    public void SelectedPackageDetail_ClearsWhenSelectionClears()
    {
        var viewModel = CreateViewModel(new FakeHandler());

        viewModel.SelectedPackage = new PackageRef
        {
            Ecosystem = Ecosystem.Npm,
            PackageName = "vite",
            DeclaredVersion = "5.0.0"
        };

        Assert.True(viewModel.HasSelectedPackageDetail);

        viewModel.SelectedPackage = null;

        Assert.False(viewModel.HasSelectedPackageDetail);
        Assert.Null(viewModel.SelectedPackageDetail);
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
