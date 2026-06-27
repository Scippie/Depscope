using DepScope.Core.Models;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using System.Text.Json;
using System.Xml.Linq;


namespace DepScope.Core.Ecosystems;

public sealed class DotNetEcosystemHandler : IEcosystemHandler
{
    public Ecosystem Ecosystem => Ecosystem.DotNet;
    private const string DefaultNuGetSourceUrl = "https://api.nuget.org/v3/index.json";



    public bool CanHandleDirectory(string rootPath)
    {
        return DirectoryHelpers.EnumerateFilesSafe(rootPath, "*.csproj").Any();
    }

    public Task<IReadOnlyList<ProjectInfo>> ScanProjectsAsync(
        string rootPath,
        CancellationToken ct = default)
    {
        var projects = new List<ProjectInfo>();

        foreach (var csprojPath in DirectoryHelpers.EnumerateFilesSafe(rootPath, "*.csproj"))
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var projName = Path.GetFileNameWithoutExtension(csprojPath);
                var project = new ProjectInfo
                {
                    Name = projName,
                    Path = csprojPath,
                    Ecosystem = Ecosystem.DotNet
                };

                var projectDir = Path.GetDirectoryName(csprojPath) ?? rootPath;
                var assetsData = ReadFromProjectAssets(
                    Path.Combine(projectDir, "obj", "project.assets.json"));

                var doc = XDocument.Load(csprojPath);
                var ns = doc.Root?.Name.Namespace ?? XNamespace.None;

                var packageRefs = doc
                    .Descendants(ns + "PackageReference")
                    .Select<XElement, PackageRef?>(x =>
                    {
                        var id = (string?)x.Attribute("Include") ?? (string?)x.Attribute("Update");
                        if (string.IsNullOrWhiteSpace(id))
                            return null;

                        string? version = (string?)x.Attribute("Version");

                        if (string.IsNullOrWhiteSpace(version))
                        {
                            var versionElement = x.Element(ns + "Version");
                            version = versionElement?.Value;
                        }

                        if (string.IsNullOrWhiteSpace(version))
                            return null;

                        return new PackageRef
                        {
                            Ecosystem = Ecosystem.DotNet,
                            PackageName = id,
                            DeclaredVersion = version
                        };
                    })
                    .OfType<PackageRef>()
                    .ToList();

                foreach (var packageRef in packageRefs)
                {
                    if (assetsData.InstalledVersions.TryGetValue(packageRef.PackageName, out var installedVersion))
                        packageRef.InstalledVersion = installedVersion;
                }

                project.Packages.AddRange(packageRefs);
                AttachRelatedSecurityPackages(project, assetsData);
                projects.Add(project);
            }
            catch (Exception ex) when (
                ex is IOException ||
                ex is UnauthorizedAccessException ||
                ex is System.Xml.XmlException)
            {
                continue;
            }
        }

        return Task.FromResult<IReadOnlyList<ProjectInfo>>(projects);
    }

    public async Task EnrichWithLatestVersionsAsync(
        IReadOnlyList<ProjectInfo> projects,
        CancellationToken ct = default)
    {
        await EnrichWithLatestVersionsAsync(projects, nugetSourceUrl: null, ct);
    }

    public async Task EnrichWithLatestVersionsAsync(
        IReadOnlyList<ProjectInfo> projects,
        string? nugetSourceUrl,
        CancellationToken ct = default)
    {
        var nugetPackages = projects
            .Where(p => p.Ecosystem == Ecosystem.DotNet)
            .SelectMany(p => p.Packages)
            .Where(p => !string.IsNullOrWhiteSpace(p.PackageName))
            .ToList();

        if (nugetPackages.Count == 0)
            return;

        var cache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var pkg in nugetPackages)
        {
            ct.ThrowIfCancellationRequested();

            var id = pkg.PackageName!.Trim();

            if (!cache.TryGetValue(id, out var latest))
            {
                latest = await GetNuGetLatestListedStableAsync(id, nugetSourceUrl, ct);
                cache[id] = latest;
            }

            if (string.IsNullOrWhiteSpace(latest))
            {
                pkg.LatestVersion = null;                 // show blank
                pkg.UpdateType = VersionUpdateType.Unknown; // show Unknown
                continue;
            }


            pkg.LatestVersion = latest;

            if (string.IsNullOrWhiteSpace(pkg.DeclaredVersion))
            {
                pkg.UpdateType = VersionUpdateType.Unknown;
                continue;
            }

            var declaredNorm = NormalizeNuGetVersion(pkg.DeclaredVersion);
            var latestNorm = NormalizeNuGetVersion(latest);

            if (!Version.TryParse(declaredNorm, out var declV) ||
                !Version.TryParse(latestNorm, out var latestV))
            {
                pkg.UpdateType = VersionUpdateType.Unknown;
                continue;
            }

            if (latestV <= declV)
            {
                pkg.UpdateType = VersionUpdateType.None;
                pkg.LatestVersion = pkg.DeclaredVersion;
                continue;
            }

            pkg.UpdateType = VersionUpdateClassifier.GetUpdateType(declV, latestV);
        }
    }



    private static void AttachRelatedSecurityPackages(
        ProjectInfo project,
        NuGetAssetsData assetsData)
    {
        if (assetsData.InstalledVersions.Count == 0 ||
            assetsData.DependenciesByPackage.Count == 0)
        {
            return;
        }

        var directPackageNames = project.Packages
            .Select(package => package.PackageName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var package in project.Packages)
        {
            if (!assetsData.DependenciesByPackage.TryGetValue(package.PackageName, out var directDependencies))
                continue;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<(string PackageName, string Relationship)>();
            foreach (var dependency in directDependencies.OrderBy(dep => dep, StringComparer.OrdinalIgnoreCase))
                queue.Enqueue((dependency, $"{package.PackageName} > {dependency}"));

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!seen.Add(current.PackageName))
                    continue;

                if (!directPackageNames.Contains(current.PackageName) &&
                    assetsData.InstalledVersions.TryGetValue(current.PackageName, out var installedVersion))
                {
                    package.RelatedSecurityPackages.Add(new RelatedSecurityPackage
                    {
                        Ecosystem = Ecosystem.DotNet,
                        PackageName = current.PackageName,
                        Version = installedVersion,
                        Relationship = current.Relationship
                    });
                }

                if (!assetsData.DependenciesByPackage.TryGetValue(current.PackageName, out var childDependencies))
                    continue;

                foreach (var child in childDependencies.OrderBy(dep => dep, StringComparer.OrdinalIgnoreCase))
                    queue.Enqueue((child, $"{current.Relationship} > {child}"));
            }
        }
    }

    private static NuGetAssetsData ReadFromProjectAssets(string assetsPath)
    {
        var installedVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dependenciesByPackage = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(assetsPath))
            return new NuGetAssetsData(installedVersions, dependenciesByPackage);

        try
        {
            using var stream = File.OpenRead(assetsPath);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            var packageKeys = new Dictionary<string, (string Name, string Version)>(StringComparer.OrdinalIgnoreCase);
            var versionCounts = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            if (root.TryGetProperty("libraries", out var libraries) &&
                libraries.ValueKind == JsonValueKind.Object)
            {
                foreach (var library in libraries.EnumerateObject())
                {
                    if (library.Value.ValueKind != JsonValueKind.Object ||
                        !library.Value.TryGetProperty("type", out var typeEl) ||
                        typeEl.ValueKind != JsonValueKind.String ||
                        !typeEl.GetString()!.Equals("package", StringComparison.OrdinalIgnoreCase) ||
                        !TryParsePackageAssetKey(library.Name, out var packageName, out var version))
                    {
                        continue;
                    }

                    packageKeys[library.Name] = (packageName, version);
                    if (!versionCounts.TryGetValue(packageName, out var versions))
                    {
                        versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        versionCounts[packageName] = versions;
                    }

                    versions.Add(version);
                }
            }

            if (root.TryGetProperty("targets", out var targets) &&
                targets.ValueKind == JsonValueKind.Object)
            {
                foreach (var target in targets.EnumerateObject())
                {
                    if (target.Value.ValueKind != JsonValueKind.Object)
                        continue;

                    foreach (var targetPackage in target.Value.EnumerateObject())
                    {
                        if (!packageKeys.TryGetValue(targetPackage.Name, out var package) ||
                            targetPackage.Value.ValueKind != JsonValueKind.Object ||
                            !targetPackage.Value.TryGetProperty("dependencies", out var dependencies) ||
                            dependencies.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        foreach (var dependency in dependencies.EnumerateObject())
                        {
                            if (string.IsNullOrWhiteSpace(dependency.Name))
                                continue;

                            if (!dependenciesByPackage.TryGetValue(package.Name, out var packageDependencies))
                            {
                                packageDependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                dependenciesByPackage[package.Name] = packageDependencies;
                            }

                            packageDependencies.Add(dependency.Name);
                        }
                    }
                }
            }

            foreach (var (packageName, versions) in versionCounts)
            {
                if (versions.Count == 1)
                    installedVersions[packageName] = versions.Single();
            }
        }
        catch
        {
            // Ignore malformed or unreadable restore assets; declared references remain available.
        }

        return new NuGetAssetsData(installedVersions, dependenciesByPackage);
    }

    private static bool TryParsePackageAssetKey(
        string key,
        out string packageName,
        out string version)
    {
        packageName = string.Empty;
        version = string.Empty;

        var separator = key.LastIndexOf('/');
        if (separator <= 0 || separator >= key.Length - 1)
            return false;

        packageName = key[..separator];
        version = key[(separator + 1)..];
        return !string.IsNullOrWhiteSpace(packageName) &&
               !string.IsNullOrWhiteSpace(version);
    }

    private sealed record NuGetAssetsData(
        IReadOnlyDictionary<string, string> InstalledVersions,
        IReadOnlyDictionary<string, HashSet<string>> DependenciesByPackage);

    private static string NormalizeNuGetVersion(string v)
    {
        if (string.IsNullOrWhiteSpace(v))
            return string.Empty;

        var s = v.Trim();

        // Strip leading 'v'
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            s = s[1..];

        // Cut at '-' (pre-release tags)
        var dashIdx = s.IndexOf('-');
        if (dashIdx > 0)
            s = s[..dashIdx];

        // Cut at space just in case
        var spaceIdx = s.IndexOf(' ');
        if (spaceIdx > 0)
            s = s[..spaceIdx];

        return s;
    }



    private static async Task<string?> GetNuGetLatestListedStableAsync(
        string packageId,
        string? nugetSourceUrl,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(packageId))
            return null;

        try
        {
            var sourceUrl = string.IsNullOrWhiteSpace(nugetSourceUrl)
                ? DefaultNuGetSourceUrl
                : nugetSourceUrl.Trim();

            var packageSource = new PackageSource(sourceUrl);
            var repository = Repository.Factory.GetCoreV3(packageSource);
            var metadataResource = await repository.GetResourceAsync<PackageMetadataResource>(ct);

            using var cacheContext = new SourceCacheContext();
            var metadata = await metadataResource.GetMetadataAsync(
                packageId,
                includePrerelease: false,
                includeUnlisted: false,
                cacheContext,
                NullLogger.Instance,
                ct);

            var latest = metadata
                .Select(m => m.Identity.Version)
                .Where(v => !v.IsPrerelease)
                .OrderByDescending(v => v)
                .FirstOrDefault();

            return latest?.ToNormalizedString();
        }
        catch
        {
            return null;
        }
    }


}

