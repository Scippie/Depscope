using DepScope.Core.Ecosystems;
using DepScope.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DepScope.Core;

public sealed class InspectionService
{
    private static readonly TimeSpan DefaultLatestVersionRetryDelay = TimeSpan.FromSeconds(10);

    private readonly IReadOnlyList<IEcosystemHandler> _handlers;
    private readonly RegistrySourceOptions _registrySourceOptions;
    private readonly VulnerabilityService _vulnerabilityService;
    private readonly TimeSpan _latestVersionRetryDelay;

    public InspectionService(
        IEnumerable<IEcosystemHandler> handlers,
        RegistrySourceOptions? registrySourceOptions = null,
        VulnerabilityService? vulnerabilityService = null,
        TimeSpan? latestVersionRetryDelay = null)
    {
        _handlers = handlers.ToList();
        _registrySourceOptions = registrySourceOptions ?? new RegistrySourceOptions();
        _vulnerabilityService = vulnerabilityService ?? new VulnerabilityService();
        _latestVersionRetryDelay = latestVersionRetryDelay ?? DefaultLatestVersionRetryDelay;
    }

    public async Task<IReadOnlyList<ProjectInfo>> InspectRootAsync(
        string rootPath,
        CancellationToken ct = default,
        bool includeLatestVersions = true,
        RegistrySourceOptions? registrySourceOptions = null,
        bool retryUnresolvedLatestVersions = false)
    {
        var allProjects = new List<ProjectInfo>();
        var scannedProjectsByHandler = new List<(IEcosystemHandler Handler, IReadOnlyList<ProjectInfo> Projects)>();
        var sources = registrySourceOptions ?? _registrySourceOptions;

        using var fileIndex = DirectoryHelpers.UseFileIndex(rootPath);

        foreach (var handler in _handlers)
        {
            if (!handler.CanHandleDirectory(rootPath))
                continue;

            var projects = await handler.ScanProjectsAsync(rootPath, ct);
            if (projects.Count == 0)
                continue;

            if (includeLatestVersions)
            {
                await EnrichWithLatestVersionsAsync(handler, projects, sources, ct);
                scannedProjectsByHandler.Add((handler, projects));
            }

            allProjects.AddRange(projects);
        }

        if (includeLatestVersions && retryUnresolvedLatestVersions)
            await RetryUnresolvedLatestVersionsAsync(scannedProjectsByHandler, sources, ct);

        if (includeLatestVersions)
            await _vulnerabilityService.EnrichWithVulnerabilitiesAsync(allProjects, ct);

        return allProjects;
    }

    public async Task<bool> RetryUnresolvedLatestVersionsAsync(
        IReadOnlyList<ProjectInfo> projects,
        RegistrySourceOptions? registrySourceOptions = null,
        CancellationToken ct = default)
    {
        var sources = registrySourceOptions ?? _registrySourceOptions;
        var projectsByHandler = _handlers
            .Select(handler => (
                Handler: handler,
                Projects: projects
                    .Where(project => project.Ecosystem == handler.Ecosystem)
                    .ToList()))
            .Where(item => item.Projects.Count > 0)
            .Select(item => (item.Handler, Projects: (IReadOnlyList<ProjectInfo>)item.Projects))
            .ToList();

        return await RetryUnresolvedLatestVersionsAsync(projectsByHandler, sources, ct);
    }

    private async Task<bool> RetryUnresolvedLatestVersionsAsync(
        IReadOnlyList<(IEcosystemHandler Handler, IReadOnlyList<ProjectInfo> Projects)> scannedProjectsByHandler,
        RegistrySourceOptions sources,
        CancellationToken ct)
    {
        var retryProjectsByHandler = scannedProjectsByHandler
            .Select(item => (item.Handler, Projects: CreateRetryProjects(item.Projects)))
            .Where(item => item.Projects.Count > 0)
            .ToList();

        if (retryProjectsByHandler.Count == 0)
            return false;

        await Task.Delay(_latestVersionRetryDelay, ct);

        foreach (var (handler, projects) in retryProjectsByHandler)
        {
            ct.ThrowIfCancellationRequested();
            await EnrichWithLatestVersionsAsync(handler, projects, sources, ct);
        }

        return true;
    }

    private static List<ProjectInfo> CreateRetryProjects(IReadOnlyList<ProjectInfo> projects)
    {
        var retryProjects = new List<ProjectInfo>();

        foreach (var project in projects)
        {
            var unresolvedPackages = project.Packages
                .Where(ShouldRetryLatestVersionLookup)
                .ToList();

            if (unresolvedPackages.Count == 0)
                continue;

            var retryProject = new ProjectInfo
            {
                Name = project.Name,
                Path = project.Path,
                Ecosystem = project.Ecosystem
            };

            retryProject.Packages.AddRange(unresolvedPackages);
            retryProjects.Add(retryProject);
        }

        return retryProjects;
    }

    private static bool ShouldRetryLatestVersionLookup(PackageRef package)
    {
        return !string.IsNullOrWhiteSpace(package.PackageName) &&
               string.IsNullOrWhiteSpace(package.LatestVersion) &&
               package.UpdateType == VersionUpdateType.Unknown;
    }

    private static async Task EnrichWithLatestVersionsAsync(
        IEcosystemHandler handler,
        IReadOnlyList<ProjectInfo> projects,
        RegistrySourceOptions sources,
        CancellationToken ct)
    {
        if (handler is DotNetEcosystemHandler dotNetHandler)
            await dotNetHandler.EnrichWithLatestVersionsAsync(
                projects,
                sources.NuGetSourceUrl,
                ct);
        else if (handler is NpmEcosystemHandler npmHandler)
            await npmHandler.EnrichWithLatestVersionsAsync(
                projects,
                sources.NpmRegistryBaseUrl,
                ct);
        else if (handler is GoEcosystemHandler goHandler)
            await goHandler.EnrichWithLatestVersionsAsync(
                projects,
                sources.GoProxyBaseUrl,
                ct);
        else if (handler is PythonEcosystemHandler pythonHandler)
            await pythonHandler.EnrichWithLatestVersionsAsync(
                projects,
                sources.PythonPackageIndexBaseUrl,
                ct);
        else if (handler is PhpEcosystemHandler phpHandler)
            await phpHandler.EnrichWithLatestVersionsAsync(
                projects,
                sources.PackagistMetadataBaseUrl,
                ct);
        else if (handler is JavaEcosystemHandler javaHandler)
            await javaHandler.EnrichWithLatestVersionsAsync(
                projects,
                sources.MavenSearchBaseUrl,
                ct);
        else if (handler is RustEcosystemHandler rustHandler)
            await rustHandler.EnrichWithLatestVersionsAsync(
                projects,
                sources.CratesApiBaseUrl,
                ct);
        else if (handler is GitHubActionsEcosystemHandler gitHubActionsHandler)
            await gitHubActionsHandler.EnrichWithLatestVersionsAsync(
                projects,
                sources.GitHubApiBaseUrl,
                ct);
        else
            await handler.EnrichWithLatestVersionsAsync(projects, ct);
    }
}

