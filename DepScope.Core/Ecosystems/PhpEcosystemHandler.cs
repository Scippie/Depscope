using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DepScope.Core.Models;

namespace DepScope.Core.Ecosystems;

public sealed class PhpEcosystemHandler : IEcosystemHandler
{
    public Ecosystem Ecosystem => Ecosystem.Php;
    private const string DefaultPackagistMetadataBaseUrl = "https://repo.packagist.org/p2/";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    public bool CanHandleDirectory(string rootPath)
    {
        return DirectoryHelpers.EnumerateFilesSafe(rootPath, "composer.json").Any();
    }

    public async Task<IReadOnlyList<ProjectInfo>> ScanProjectsAsync(
        string rootPath,
        CancellationToken ct = default)
    {
        var projects = new List<ProjectInfo>();

        foreach (var path in DirectoryHelpers.EnumerateFilesSafe(rootPath, "composer.json"))
        {
            ct.ThrowIfCancellationRequested();

            var projectDir = Path.GetDirectoryName(path) ?? rootPath;
            var lockData = ReadFromComposerLock(
                Path.Combine(projectDir, "composer.lock"));

            var proj = new ProjectInfo
            {
                Name = Path.GetFileName(projectDir),
                Path = path,
                Ecosystem = Ecosystem.Php
            };

            using var stream = File.OpenRead(path);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            if (root.TryGetProperty("require", out var require))
            {
                AddComposerDeps(require, proj, lockData.InstalledVersions);
            }

            if (root.TryGetProperty("require-dev", out var requireDev))
            {
                AddComposerDeps(requireDev, proj, lockData.InstalledVersions);
            }

            AttachRelatedSecurityPackages(proj, lockData);

            if (proj.Packages.Count > 0)
                projects.Add(proj);
        }

        return projects;
    }

    private static void AddComposerDeps(
        JsonElement obj,
        ProjectInfo proj,
        IReadOnlyDictionary<string, string> installedVersions)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            var name = prop.Name;
            var version = prop.Value.GetString() ?? "";

            // ignore PHP itself
            if (name.Equals("php", StringComparison.OrdinalIgnoreCase))
                continue;

            proj.Packages.Add(new PackageRef
            {
                Ecosystem = Ecosystem.Php,
                PackageName = name,
                DeclaredVersion = version,
                InstalledVersion = installedVersions.TryGetValue(name, out var installed)
                    ? installed
                    : null,
                UpdateType = VersionUpdateType.Unknown
            });
        }
    }

    public async Task EnrichWithLatestVersionsAsync(
        IReadOnlyList<ProjectInfo> projects,
        CancellationToken ct = default)
    {
        await EnrichWithLatestVersionsAsync(projects, packagistMetadataBaseUrl: null, ct);
    }

    public async Task EnrichWithLatestVersionsAsync(
        IReadOnlyList<ProjectInfo> projects,
        string? packagistMetadataBaseUrl,
        CancellationToken ct = default)
    {
        foreach (var project in projects.Where(p => p.Ecosystem == Ecosystem.Php))
        {
            foreach (var pkg in project.Packages)
            {
                ct.ThrowIfCancellationRequested();

                var latest = await GetLatestFromPackagistAsync(
                    pkg.PackageName,
                    packagistMetadataBaseUrl,
                    ct);
                if (latest is null)
                    continue;

                pkg.LatestVersion = latest;

                var currentRaw = string.IsNullOrWhiteSpace(pkg.InstalledVersion)
                    ? pkg.DeclaredVersion
                    : pkg.InstalledVersion;

                if (Version.TryParse(NormalizeVersion(currentRaw), out var declared)
                    && Version.TryParse(NormalizeVersion(latest), out var latestV))
                {
                    pkg.UpdateType = VersionUpdateClassifier.GetUpdateType(declared, latestV);
                }
            }
        }
    }

    private static void AttachRelatedSecurityPackages(
        ProjectInfo project,
        ComposerLockData lockData)
    {
        if (lockData.InstalledVersions.Count == 0 ||
            lockData.DependenciesByPackage.Count == 0)
        {
            return;
        }

        var directPackageNames = project.Packages
            .Select(package => package.PackageName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var package in project.Packages)
        {
            if (!lockData.DependenciesByPackage.TryGetValue(package.PackageName, out var directDependencies))
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
                    lockData.InstalledVersions.TryGetValue(current.PackageName, out var installedVersion))
                {
                    package.RelatedSecurityPackages.Add(new RelatedSecurityPackage
                    {
                        Ecosystem = Ecosystem.Php,
                        PackageName = current.PackageName,
                        Version = installedVersion,
                        Relationship = current.Relationship
                    });
                }

                if (!lockData.DependenciesByPackage.TryGetValue(current.PackageName, out var childDependencies))
                    continue;

                foreach (var child in childDependencies.OrderBy(dep => dep, StringComparer.OrdinalIgnoreCase))
                    queue.Enqueue((child, $"{current.Relationship} > {child}"));
            }
        }
    }

    private static ComposerLockData ReadFromComposerLock(string lockPath)
    {
        var installedVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dependenciesByPackage = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(lockPath))
            return new ComposerLockData(installedVersions, dependenciesByPackage);

        try
        {
            using var stream = File.OpenRead(lockPath);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            AddLockedPackages(root, "packages", installedVersions, dependenciesByPackage);
            AddLockedPackages(root, "packages-dev", installedVersions, dependenciesByPackage);
        }
        catch
        {
            // Ignore malformed or unreadable lockfiles; declared constraints remain available.
        }

        return new ComposerLockData(installedVersions, dependenciesByPackage);
    }

    private static void AddLockedPackages(
        JsonElement root,
        string propertyName,
        Dictionary<string, string> installedVersions,
        Dictionary<string, HashSet<string>> dependenciesByPackage)
    {
        if (!root.TryGetProperty(propertyName, out var packages) ||
            packages.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var package in packages.EnumerateArray())
        {
            if (!package.TryGetProperty("name", out var nameEl) ||
                nameEl.ValueKind != JsonValueKind.String ||
                !package.TryGetProperty("version", out var versionEl) ||
                versionEl.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var name = nameEl.GetString();
            var version = versionEl.GetString();
            if (string.IsNullOrWhiteSpace(name) ||
                string.IsNullOrWhiteSpace(version))
            {
                continue;
            }

            installedVersions[name] = version;

            AddComposerDependencies(package, name, "require", dependenciesByPackage);
            AddComposerDependencies(package, name, "require-dev", dependenciesByPackage);
        }
    }

    private static void AddComposerDependencies(
        JsonElement package,
        string packageName,
        string propertyName,
        Dictionary<string, HashSet<string>> dependenciesByPackage)
    {
        if (!package.TryGetProperty(propertyName, out var dependencies) ||
            dependencies.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var dependency in dependencies.EnumerateObject())
        {
            if (IsComposerPlatformPackage(dependency.Name))
                continue;

            if (!dependenciesByPackage.TryGetValue(packageName, out var packageDependencies))
            {
                packageDependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                dependenciesByPackage[packageName] = packageDependencies;
            }

            packageDependencies.Add(dependency.Name);
        }
    }

    private static bool IsComposerPlatformPackage(string packageName)
    {
        return packageName.Equals("php", StringComparison.OrdinalIgnoreCase) ||
               packageName.StartsWith("ext-", StringComparison.OrdinalIgnoreCase) ||
               packageName.StartsWith("lib-", StringComparison.OrdinalIgnoreCase) ||
               packageName.Equals("composer-plugin-api", StringComparison.OrdinalIgnoreCase) ||
               packageName.Equals("composer-runtime-api", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ComposerLockData(
        IReadOnlyDictionary<string, string> InstalledVersions,
        IReadOnlyDictionary<string, HashSet<string>> DependenciesByPackage);

    private static async Task<string?> GetLatestFromPackagistAsync(
        string packageName,
        string? packagistMetadataBaseUrl,
        CancellationToken ct)
    {
        // packageName like "vendor/package"
        var baseUrl = NormalizeBaseUrl(packagistMetadataBaseUrl, DefaultPackagistMetadataBaseUrl);
        var url = $"{baseUrl}{packageName}.json";

        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
                return null;

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            var root = doc.RootElement;
            if (!root.TryGetProperty("packages", out var packagesObj))
                return null;

            if (!packagesObj.TryGetProperty(packageName, out var arr))
                return null;

            Version? best = null;
            string? bestRaw = null;

            foreach (var ver in arr.EnumerateArray())
            {
                var versionStr = ver.GetProperty("version").GetString();
                if (string.IsNullOrEmpty(versionStr))
                    continue;

                // skip dev/pre versions
                if (versionStr.Contains("-dev", StringComparison.OrdinalIgnoreCase) ||
                    versionStr.Contains("alpha", StringComparison.OrdinalIgnoreCase) ||
                    versionStr.Contains("beta", StringComparison.OrdinalIgnoreCase) ||
                    versionStr.Contains("RC", StringComparison.OrdinalIgnoreCase))
                    continue;

                var normalized = NormalizeVersion(versionStr);
                if (!Version.TryParse(normalized, out var v))
                    continue;

                if (best is null || v > best)
                {
                    best = v;
                    bestRaw = versionStr;
                }
            }

            return bestRaw;
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeBaseUrl(string? configuredBaseUrl, string defaultBaseUrl)
    {
        var baseUrl = string.IsNullOrWhiteSpace(configuredBaseUrl)
            ? defaultBaseUrl
            : configuredBaseUrl.Trim();

        return baseUrl.EndsWith("/", StringComparison.Ordinal)
            ? baseUrl
            : baseUrl + "/";
    }

    private static string NormalizeVersion(string v)
    {
        if (string.IsNullOrWhiteSpace(v))
            return string.Empty;

        var s = v.Trim();

        // Remove Composer operators (^ ~ >= <= > < =)
        while (s.Length > 0 && (s[0] == '^' || s[0] == '~' || s[0] == '>' || s[0] == '<' || s[0] == '='))
            s = s[1..].Trim();

        // Handle simple OR constraints: keep only the first part
        var pipeIdx = s.IndexOf('|');
        if (pipeIdx > 0)
            s = s[..pipeIdx].Trim();

        // Remove leading "v"
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            s = s[1..];

        // Strip pre-release suffixes and build metadata
        var dashIdx = s.IndexOf('-');
        if (dashIdx > 0)
            s = s[..dashIdx];

        var plusIdx = s.IndexOf('+');
        if (plusIdx > 0)
            s = s[..plusIdx];

        return s;
    }

}
