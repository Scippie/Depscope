using DepScope.Core;
using DepScope.Core.Ecosystems;
using DepScope.Core.Models;
using Xunit;

namespace DepScope.Tests;

public sealed class InspectionServiceTests
{
    [Fact]
    public async Task InspectRootAsync_RetriesOnlyPackagesWithoutLatestVersion()
    {
        var resolved = CreatePackage("resolved/action");
        var flaky = CreatePackage("flaky/action");
        var handler = new RetryFakeHandler(
            new ProjectInfo
            {
                Name = "CI",
                Path = ".github/workflows/ci.yml",
                Ecosystem = Ecosystem.GitHubActions,
                Packages = { resolved, flaky }
            });

        var service = new InspectionService(
            new[] { handler },
            latestVersionRetryDelay: TimeSpan.Zero);

        await service.InspectRootAsync(
            "unused",
            TestContext.Current.CancellationToken,
            retryUnresolvedLatestVersions: true);

        Assert.Equal(2, handler.EnrichCalls);
        Assert.Equal(new[] { "resolved/action", "flaky/action" }, handler.PackageNamesByCall[0]);
        Assert.Equal(new[] { "flaky/action" }, handler.PackageNamesByCall[1]);
        Assert.Equal("1.0.0", resolved.LatestVersion);
        Assert.Equal("2.0.0", flaky.LatestVersion);
    }

    [Fact]
    public async Task InspectRootAsync_DoesNotRetryWhenLatestVersionsResolved()
    {
        var handler = new RetryFakeHandler(
            new ProjectInfo
            {
                Name = "CI",
                Path = ".github/workflows/ci.yml",
                Ecosystem = Ecosystem.GitHubActions,
                Packages =
                {
                    CreatePackage("resolved/action"),
                    CreatePackage("also-resolved/action")
                }
            })
        {
            ResolveAllOnFirstCall = true
        };

        var service = new InspectionService(
            new[] { handler },
            latestVersionRetryDelay: TimeSpan.Zero);

        await service.InspectRootAsync(
            "unused",
            TestContext.Current.CancellationToken,
            retryUnresolvedLatestVersions: true);

        Assert.Equal(1, handler.EnrichCalls);
    }

    [Fact]
    public async Task RetryUnresolvedLatestVersionsAsync_UpdatesExistingUnresolvedPackagesOnly()
    {
        var resolved = CreatePackage("resolved/action");
        resolved.LatestVersion = "1.0.0";
        resolved.UpdateType = VersionUpdateType.None;

        var flaky = CreatePackage("flaky/action");
        var handler = new RetryFakeHandler()
        {
            ResolveAllOnFirstCall = true
        };
        var service = new InspectionService(
            new[] { handler },
            latestVersionRetryDelay: TimeSpan.Zero);

        var didRetry = await service.RetryUnresolvedLatestVersionsAsync(
            new[]
            {
                new ProjectInfo
                {
                    Name = "CI",
                    Path = ".github/workflows/ci.yml",
                    Ecosystem = Ecosystem.GitHubActions,
                    Packages = { resolved, flaky }
                }
            },
            ct: TestContext.Current.CancellationToken);

        Assert.True(didRetry);
        Assert.Equal(1, handler.EnrichCalls);
        Assert.Equal(new[] { "flaky/action" }, handler.PackageNamesByCall[0]);
        Assert.Equal("1.0.0", resolved.LatestVersion);
        Assert.Equal("1.0.0", flaky.LatestVersion);
    }

    private static PackageRef CreatePackage(string packageName)
    {
        return new PackageRef
        {
            Ecosystem = Ecosystem.GitHubActions,
            PackageName = packageName,
            DeclaredVersion = "v1",
            InstalledVersion = "Tag/branch ref",
            UpdateType = VersionUpdateType.Unknown
        };
    }

    private sealed class RetryFakeHandler : IEcosystemHandler
    {
        private readonly IReadOnlyList<ProjectInfo> _projects;

        public RetryFakeHandler(params ProjectInfo[] projects)
        {
            _projects = projects;
        }

        public Ecosystem Ecosystem => Ecosystem.GitHubActions;
        public int EnrichCalls { get; private set; }
        public List<string[]> PackageNamesByCall { get; } = new();
        public bool ResolveAllOnFirstCall { get; init; }

        public bool CanHandleDirectory(string rootPath) => true;

        public Task<IReadOnlyList<ProjectInfo>> ScanProjectsAsync(
            string rootPath,
            CancellationToken ct = default)
        {
            return Task.FromResult(_projects);
        }

        public Task EnrichWithLatestVersionsAsync(
            IReadOnlyList<ProjectInfo> projects,
            CancellationToken ct = default)
        {
            EnrichCalls++;

            var packages = projects
                .SelectMany(project => project.Packages)
                .ToList();

            PackageNamesByCall.Add(packages
                .Select(package => package.PackageName)
                .ToArray());

            foreach (var package in packages)
            {
                if (ResolveAllOnFirstCall ||
                    EnrichCalls > 1 ||
                    package.PackageName.Equals("resolved/action", StringComparison.Ordinal))
                {
                    package.LatestVersion = EnrichCalls > 1 ? "2.0.0" : "1.0.0";
                    package.UpdateType = VersionUpdateType.None;
                }
            }

            return Task.CompletedTask;
        }
    }
}
