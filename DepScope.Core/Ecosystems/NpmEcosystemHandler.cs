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

public sealed class NpmEcosystemHandler : IEcosystemHandler
{
    public Ecosystem Ecosystem => Ecosystem.Npm;
    private const string DefaultNpmRegistryBaseUrl = "https://registry.npmjs.org/";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    public bool CanHandleDirectory(string rootPath)
    {
        return DirectoryHelpers.EnumerateFilesSafe(rootPath, "package.json").Any();
    }

    public async Task<IReadOnlyList<ProjectInfo>> ScanProjectsAsync(
        string rootPath,
        CancellationToken ct = default)
    {
        var projects = new List<ProjectInfo>();

        foreach (var path in DirectoryHelpers.EnumerateFilesSafe(rootPath, "package.json"))
        {
            ct.ThrowIfCancellationRequested();

            var projectDir = Path.GetDirectoryName(path) ?? rootPath;

            // Read installed/resolved versions and dependency relationships (nearest lock only)
            var lockData = NpmLockData.Empty;
            var lockPath = FindNearestPackageLock(projectDir, rootPath);
            if (lockPath is not null)
            {
                lockData = ReadFromPackageLock(lockPath);
            }
            else
            {
                var pnpmLockPath = FindNearestPnpmLock(projectDir, rootPath);
                if (pnpmLockPath is not null)
                {
                    lockData = NpmLockData.FromInstalledVersions(ReadInstalledFromPnpmLock(pnpmLockPath));
                }
                else if (FindNearestYarnLock(projectDir, rootPath) is { } yarnLockPath)
                {
                    lockData = NpmLockData.FromInstalledVersions(ReadInstalledFromYarnLock(yarnLockPath));
                }
            }


            using var stream = File.OpenRead(path);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            var proj = new ProjectInfo
            {
                Name = Path.GetFileName(projectDir),
                Path = path,
                Ecosystem = Ecosystem.Npm
            };

            // dependencies
            if (root.TryGetProperty("dependencies", out var depsElem) &&
                depsElem.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in depsElem.EnumerateObject())
                {
                    var name = prop.Name;
                    var declared = prop.Value.GetString() ?? string.Empty;

                    var p = new PackageRef
                    {
                        Ecosystem = Ecosystem.Npm,
                        PackageName = name,
                        DeclaredVersion = declared,
                        UpdateType = VersionUpdateType.Unknown
                    };

                    if (lockData.InstalledVersions.TryGetValue(name, out var inst))
                        p.InstalledVersion = inst;

                    proj.Packages.Add(p);

                }
            }

            // devDependencies
            if (root.TryGetProperty("devDependencies", out var devElem) &&
                devElem.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in devElem.EnumerateObject())
                {
                    var name = prop.Name;
                    var declared = prop.Value.GetString() ?? string.Empty;

                    var p = new PackageRef
                    {
                        Ecosystem = Ecosystem.Npm,
                        PackageName = name,
                        DeclaredVersion = declared,
                        UpdateType = VersionUpdateType.Unknown
                    };

                    if (lockData.InstalledVersions.TryGetValue(name, out var inst))
                        p.InstalledVersion = inst;

                    proj.Packages.Add(p);
                }
            }

            AttachRelatedSecurityPackages(proj, lockData);

            // If no packages at all -> skip this package.json (workspace root, config-only, etc.)
            if (proj.Packages.Count == 0)
                continue;

            projects.Add(proj);
        }

        return projects;
    }

    public async Task EnrichWithLatestVersionsAsync(
        IReadOnlyList<ProjectInfo> projects,
        CancellationToken ct = default)
    {
        await EnrichWithLatestVersionsAsync(projects, npmRegistryBaseUrl: null, ct);
    }

    public async Task EnrichWithLatestVersionsAsync(
        IReadOnlyList<ProjectInfo> projects,
        string? npmRegistryBaseUrl,
        CancellationToken ct = default)
    {
        foreach (var project in projects.Where(p => p.Ecosystem == Ecosystem.Npm))
        {
            foreach (var pkg in project.Packages)
            {
                ct.ThrowIfCancellationRequested();

                var latest = await GetLatestFromNpmAsync(pkg.PackageName, npmRegistryBaseUrl, ct);
                if (latest is null)
                    continue;

                pkg.LatestVersion = latest;

                // Prefer installed/resolved version from lockfile (reduces false alarms)
                var currentRaw = pkg.InstalledVersion;
                if (string.IsNullOrWhiteSpace(currentRaw))
                    currentRaw = pkg.DeclaredVersion; // fallback

                if (TryNormalizeSemver(currentRaw, out var currentNorm) &&
                    Version.TryParse(currentNorm, out var currentV) &&
                    TryNormalizeSemver(latest, out var latestNorm) &&
                    Version.TryParse(latestNorm, out var latestV))
                {
                    pkg.UpdateType = VersionUpdateClassifier.GetUpdateType(currentV, latestV);
                }
                else
                {
                    pkg.UpdateType = VersionUpdateType.Unknown;
                }

            }
        }
    }

    private static void AttachRelatedSecurityPackages(
        ProjectInfo project,
        NpmLockData lockData)
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
            {
                queue.Enqueue((dependency, $"{package.PackageName} > {dependency}"));
            }

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
                        Ecosystem = Ecosystem.Npm,
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

    private static async Task<string?> GetLatestFromNpmAsync(
        string packageName,
        string? npmRegistryBaseUrl,
        CancellationToken ct)
    {
        var encoded = packageName.StartsWith("@", StringComparison.Ordinal)
            ? packageName.Replace("/", "%2F")
            : packageName;

        var baseUrl = NormalizeBaseUrl(npmRegistryBaseUrl, DefaultNpmRegistryBaseUrl);
        var url = $"{baseUrl}{encoded}/latest";

        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
                return null;

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            return doc.RootElement.GetProperty("version").GetString();
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

    private static bool TryNormalizeSemver(string raw, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var v = raw.Trim();

        // Strip common prefixes (^, ~, >=, <=, >, <, =)
        while (v.Length > 0 && (v[0] == '^' || v[0] == '~' || v[0] == '>' || v[0] == '<' || v[0] == '='))
            v = v[1..].Trim();

        // Cut range (e.g. "1.2.3 || 2.0.0") to first token
        var spaceIdx = v.IndexOf(' ');
        if (spaceIdx > 0)
            v = v[..spaceIdx];

        // Cut prerelease / build metadata
        var dashIdx = v.IndexOf('-');
        if (dashIdx > 0)
            v = v[..dashIdx];

        var plusIdx = v.IndexOf('+');
        if (plusIdx > 0)
            v = v[..plusIdx];

        normalized = v;
        return !string.IsNullOrEmpty(v);
    }

    private static NpmLockData ReadFromPackageLock(string lockPath)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dependenciesByPackage = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var stream = File.OpenRead(lockPath);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            // lockfileVersion 2/3: "packages" object
            if (root.TryGetProperty("packages", out var packagesEl) &&
                packagesEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in packagesEl.EnumerateObject())
                {
                    var key = entry.Name.Replace('\\', '/');
                    var pkgName = GetPackageNameFromPackageLockPath(key);

                    if (string.IsNullOrWhiteSpace(pkgName))
                        continue;

                    var obj = entry.Value;

                    if (obj.TryGetProperty("version", out var verEl) &&
                        verEl.ValueKind == JsonValueKind.String)
                    {
                        var ver = verEl.GetString();
                        if (string.IsNullOrWhiteSpace(ver))
                            continue;

                        // Prefer top-level node_modules/<pkg> if it exists.
                        // If we already stored it from a deeper path, replace only if this is top-level.
                        if (!map.ContainsKey(pkgName))
                        {
                            map[pkgName] = ver!;
                        }
                        else
                        {
                            // If this entry is top-level (node_modules/<pkg>), override nested values.
                            var isTopLevel = key.Equals($"node_modules/{pkgName}", StringComparison.Ordinal);
                            if (isTopLevel)
                                map[pkgName] = ver!;
                        }
                    }

                    AddDependenciesFromPackageLockObject(obj, pkgName, dependenciesByPackage);
                }

                return new NpmLockData(map, dependenciesByPackage);
            }



            // lockfileVersion 1: "dependencies" tree
            if (root.TryGetProperty("dependencies", out var depsEl) &&
                depsEl.ValueKind == JsonValueKind.Object)
            {
                void Walk(JsonElement deps, string? parentPackageName)
                {
                    if (parentPackageName is not null)
                    {
                        foreach (var dep in deps.EnumerateObject())
                            AddDependency(dependenciesByPackage, parentPackageName, dep.Name);
                    }

                    foreach (var dep in deps.EnumerateObject())
                    {
                        var name = dep.Name;
                        var obj = dep.Value;

                        if (obj.TryGetProperty("version", out var verEl) &&
                            verEl.ValueKind == JsonValueKind.String)
                        {
                            var ver = verEl.GetString();
                            if (!string.IsNullOrWhiteSpace(ver))
                                map[name] = ver!;
                        }

                        if (obj.TryGetProperty("dependencies", out var nested) &&
                            nested.ValueKind == JsonValueKind.Object)
                        {
                            Walk(nested, name);
                        }
                    }
                }

                Walk(depsEl, parentPackageName: null);
            }
        }
        catch
        {
            // ignore parse/IO errors; installed map stays empty
        }

        return new NpmLockData(map, dependenciesByPackage);
    }

    private static string? GetPackageNameFromPackageLockPath(string key)
    {
        const string nodeModulesPrefix = "node_modules/";
        const string nestedNodeModulesMarker = "/node_modules/";

        var idx = key.LastIndexOf(nestedNodeModulesMarker, StringComparison.Ordinal);
        if (idx >= 0)
            return key[(idx + nestedNodeModulesMarker.Length)..];

        return key.StartsWith(nodeModulesPrefix, StringComparison.Ordinal)
            ? key[nodeModulesPrefix.Length..]
            : null;
    }

    private static void AddDependenciesFromPackageLockObject(
        JsonElement packageObject,
        string packageName,
        Dictionary<string, HashSet<string>> dependenciesByPackage)
    {
        AddDependenciesFromProperty(packageObject, packageName, "dependencies", dependenciesByPackage);
        AddDependenciesFromProperty(packageObject, packageName, "optionalDependencies", dependenciesByPackage);
    }

    private static void AddDependenciesFromProperty(
        JsonElement packageObject,
        string packageName,
        string propertyName,
        Dictionary<string, HashSet<string>> dependenciesByPackage)
    {
        if (!packageObject.TryGetProperty(propertyName, out var dependencies) ||
            dependencies.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var dependency in dependencies.EnumerateObject())
            AddDependency(dependenciesByPackage, packageName, dependency.Name);
    }

    private static void AddDependency(
        Dictionary<string, HashSet<string>> dependenciesByPackage,
        string packageName,
        string dependencyName)
    {
        if (string.IsNullOrWhiteSpace(packageName) ||
            string.IsNullOrWhiteSpace(dependencyName))
        {
            return;
        }

        if (!dependenciesByPackage.TryGetValue(packageName, out var dependencies))
        {
            dependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            dependenciesByPackage[packageName] = dependencies;
        }

        dependencies.Add(dependencyName);
    }

    private static Dictionary<string, string> ReadInstalledFromYarnLock(string lockPath)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var currentPackages = new List<string>();

            foreach (var rawLine in File.ReadLines(lockPath))
            {
                var line = rawLine.TrimEnd();
                if (string.IsNullOrWhiteSpace(line) ||
                    line.TrimStart().StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                var isHeader = !char.IsWhiteSpace(rawLine[0]) &&
                               line.EndsWith(":", StringComparison.Ordinal);
                if (isHeader)
                {
                    currentPackages = ParseYarnLockHeader(line);
                    continue;
                }

                if (currentPackages.Count == 0)
                    continue;

                var trimmed = line.TrimStart();
                if (!trimmed.StartsWith("version ", StringComparison.Ordinal))
                    continue;

                var version = NormalizeYarnQuotedValue(trimmed["version ".Length..]);
                if (string.IsNullOrWhiteSpace(version))
                    continue;

                foreach (var packageName in currentPackages)
                {
                    if (!map.ContainsKey(packageName))
                        map[packageName] = version;
                }
            }
        }
        catch
        {
            // ignore parse/IO errors; installed map stays empty
        }

        return map;
    }

    private static Dictionary<string, string> ReadInstalledFromPnpmLock(string lockPath)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var insideImporters = false;
            var insideImporter = false;
            var insideDependencyGroup = false;
            string? currentPackage = null;

            foreach (var rawLine in File.ReadLines(lockPath))
            {
                if (string.IsNullOrWhiteSpace(rawLine))
                    continue;

                var trimmed = rawLine.Trim();
                if (trimmed.StartsWith("#", StringComparison.Ordinal))
                    continue;

                var indent = rawLine.Length - rawLine.TrimStart().Length;

                if (indent == 0)
                {
                    insideImporters = trimmed.Equals("importers:", StringComparison.Ordinal);
                    insideImporter = false;
                    insideDependencyGroup = false;
                    currentPackage = null;
                    continue;
                }

                if (!insideImporters)
                    continue;

                if (indent == 2 && trimmed.EndsWith(":", StringComparison.Ordinal))
                {
                    insideImporter = true;
                    insideDependencyGroup = false;
                    currentPackage = null;
                    continue;
                }

                if (!insideImporter)
                    continue;

                if (indent == 4)
                {
                    insideDependencyGroup = IsPnpmDependencyGroup(trimmed);
                    currentPackage = null;
                    continue;
                }

                if (!insideDependencyGroup)
                    continue;

                if (indent == 6 && trimmed.EndsWith(":", StringComparison.Ordinal))
                {
                    currentPackage = NormalizeYamlKey(trimmed.TrimEnd(':'));
                    continue;
                }

                if (indent >= 8 &&
                    currentPackage is not null &&
                    trimmed.StartsWith("version:", StringComparison.Ordinal))
                {
                    var version = NormalizePnpmVersion(trimmed["version:".Length..]);
                    if (!string.IsNullOrWhiteSpace(version) &&
                        !map.ContainsKey(currentPackage))
                    {
                        map[currentPackage] = version;
                    }
                }
            }
        }
        catch
        {
            // ignore parse/IO errors; installed map stays empty
        }

        return map;
    }

    private static bool IsPnpmDependencyGroup(string trimmed)
    {
        return trimmed.Equals("dependencies:", StringComparison.Ordinal) ||
               trimmed.Equals("devDependencies:", StringComparison.Ordinal) ||
               trimmed.Equals("optionalDependencies:", StringComparison.Ordinal);
    }

    private static string NormalizeYamlKey(string value)
    {
        return value.Trim().Trim('"', '\'');
    }

    private static string NormalizePnpmVersion(string value)
    {
        var version = NormalizeYamlKey(value);
        var peerSuffixIndex = version.IndexOf('(');
        if (peerSuffixIndex > 0)
            version = version[..peerSuffixIndex];

        return version.Trim();
    }

    private static List<string> ParseYarnLockHeader(string line)
    {
        var packages = new List<string>();
        var header = line.TrimEnd(':');

        foreach (var descriptor in header.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var packageName = GetYarnPackageName(descriptor);
            if (!string.IsNullOrWhiteSpace(packageName) &&
                !packages.Contains(packageName, StringComparer.OrdinalIgnoreCase))
            {
                packages.Add(packageName);
            }
        }

        return packages;
    }

    private static string? GetYarnPackageName(string descriptor)
    {
        var value = descriptor.Trim().Trim('"', '\'');
        if (value.Length == 0)
            return null;

        var atIndex = value.StartsWith("@", StringComparison.Ordinal)
            ? value.IndexOf('@', startIndex: 1)
            : value.IndexOf('@');

        if (atIndex <= 0)
            return null;

        return value[..atIndex];
    }

    private static string NormalizeYarnQuotedValue(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length >= 2 && trimmed[0] is '"' or '\'')
        {
            var quote = trimmed[0];
            var closeIndex = trimmed.IndexOf(quote, startIndex: 1);
            if (closeIndex > 0)
                return trimmed[1..closeIndex];
        }

        return trimmed;
    }

    private static string? FindNearestPackageLock(string startDir, string stopAtRoot)
    {
        return FindNearestLockFile(startDir, stopAtRoot, "package-lock.json");
    }

    private static string? FindNearestYarnLock(string startDir, string stopAtRoot)
    {
        return FindNearestLockFile(startDir, stopAtRoot, "yarn.lock");
    }

    private static string? FindNearestPnpmLock(string startDir, string stopAtRoot)
    {
        return FindNearestLockFile(startDir, stopAtRoot, "pnpm-lock.yaml");
    }

    private static string? FindNearestLockFile(
        string startDir,
        string stopAtRoot,
        string fileName)
    {
        var cur = new DirectoryInfo(startDir);
        var stop = new DirectoryInfo(stopAtRoot);

        while (cur != null)
        {
            var candidate = Path.Combine(cur.FullName, fileName);
            if (File.Exists(candidate))
                return candidate;

            // Stop once we reach (or pass) the scan root
            if (string.Equals(cur.FullName.TrimEnd(Path.DirectorySeparatorChar),
                              stop.FullName.TrimEnd(Path.DirectorySeparatorChar),
                              StringComparison.OrdinalIgnoreCase))
                break;

            cur = cur.Parent;
        }

        return null;
    }


    private sealed class NpmLockData
    {
        public static NpmLockData Empty { get; } = new(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase));

        public NpmLockData(
            IReadOnlyDictionary<string, string> installedVersions,
            IReadOnlyDictionary<string, HashSet<string>> dependenciesByPackage)
        {
            InstalledVersions = installedVersions;
            DependenciesByPackage = dependenciesByPackage;
        }

        public IReadOnlyDictionary<string, string> InstalledVersions { get; }
        public IReadOnlyDictionary<string, HashSet<string>> DependenciesByPackage { get; }

        public static NpmLockData FromInstalledVersions(IReadOnlyDictionary<string, string> installedVersions)
        {
            return new NpmLockData(
                installedVersions,
                new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase));
        }
    }
}


