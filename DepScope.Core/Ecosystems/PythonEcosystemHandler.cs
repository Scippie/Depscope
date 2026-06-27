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

public sealed class PythonEcosystemHandler : IEcosystemHandler
{
    public Ecosystem Ecosystem => Ecosystem.Python;
    private const string DefaultPythonPackageIndexBaseUrl = "https://pypi.org/pypi/";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    public bool CanHandleDirectory(string rootPath)
    {
        bool HasAny(string pattern)
            => DirectoryHelpers.EnumerateFilesSafe(rootPath, pattern).Any();

        // Classic markers for Python projects
        if (HasAny("requirements.txt") ||
            HasAny("Pipfile") ||
            HasAny("pyproject.toml") ||
            HasAny("setup.py") ||
            HasAny("setup.cfg"))
        {
            return true;
        }

        // NEW: handle Django-style "requirements/*.txt"
        foreach (var txtPath in DirectoryHelpers.EnumerateFilesSafe(rootPath, "*.txt"))
        {
            var dir = Path.GetDirectoryName(txtPath);
            if (string.IsNullOrEmpty(dir))
                continue;

            var dirName = Path.GetFileName(dir);
            if (dirName.Equals("requirements", StringComparison.OrdinalIgnoreCase))
            {
                // any txt file directly under a "requirements" folder is a Python hint
                return true;
            }
        }

        return false;
    }


    public async Task<IReadOnlyList<ProjectInfo>> ScanProjectsAsync(
        string rootPath,
        CancellationToken ct = default)
    {
        // One ProjectInfo per DIRECTORY, not per file
        var projectsByDir = new Dictionary<string, ProjectInfo>(StringComparer.OrdinalIgnoreCase);

        ProjectInfo GetOrCreateProject(string filePath)
        {
            var dir = Path.GetDirectoryName(filePath) ?? filePath;
            if (!projectsByDir.TryGetValue(dir, out var proj))
            {
                proj = new ProjectInfo
                {
                    Name = Path.GetFileName(dir),
                    Path = dir,
                    Ecosystem = Ecosystem.Python
                };
                projectsByDir[dir] = proj;
            }
            return proj;
        }

        // 1) Classic requirements.txt at any level
        var requirementFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reqPath in DirectoryHelpers.EnumerateFilesSafe(rootPath, "requirements.txt"))
        {
            requirementFiles.Add(reqPath);
        }

        // 2) Any *.txt directly under a folder named "requirements"
        foreach (var txtPath in DirectoryHelpers.EnumerateFilesSafe(rootPath, "*.txt"))
        {
            var dir = Path.GetDirectoryName(txtPath);
            if (string.IsNullOrEmpty(dir))
                continue;

            var dirName = Path.GetFileName(dir);
            if (dirName.Equals("requirements", StringComparison.OrdinalIgnoreCase))
            {
                requirementFiles.Add(txtPath);
            }
        }

        // Process all requirement-like files
        foreach (var reqPath in requirementFiles)
        {
            ct.ThrowIfCancellationRequested();
            var proj = GetOrCreateProject(reqPath);

            foreach (var line in File.ReadLines(reqPath))
            {
                ct.ThrowIfCancellationRequested();

                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#"))
                    continue;

                if (trimmed.Contains(" @ ") &&
                    (trimmed.Contains("git+", StringComparison.OrdinalIgnoreCase) ||
                     trimmed.Contains("://", StringComparison.OrdinalIgnoreCase)) ||
                     trimmed.Contains(" # ", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string packageName;
                string declaredVersion;

                // handle "pkg==1.2.3"
                var parts = trimmed.Split(new[] { "==" }, StringSplitOptions.None);
                if (parts.Length == 2)
                {
                    packageName = parts[0].Trim();
                    declaredVersion = parts[1].Trim();
                }
                else
                {
                    // fallback: try to split on first comparator; leave version as full spec
                    var idx = trimmed.IndexOfAny(new[] { '<', '>', '=', '!' });
                    packageName = idx > 0 ? trimmed[..idx].Trim() : trimmed;
                    declaredVersion = trimmed;
                }

                if (string.IsNullOrEmpty(packageName))
                    continue;

                // Optional extra guard: skip ultra-long "versions" that are clearly URLs or hashes
                if (!string.IsNullOrEmpty(declaredVersion) && declaredVersion.Length > 100)
                    continue;

                proj.Packages.Add(new PackageRef
                {
                    Ecosystem = Ecosystem.Python,
                    PackageName = packageName,
                    DeclaredVersion = declaredVersion,
                    UpdateType = VersionUpdateType.Unknown
                });
            }
        }


        // pyproject.toml (Poetry-style)
        foreach (var pyprojectPath in DirectoryHelpers.EnumerateFilesSafe(rootPath, "pyproject.toml"))
        {
            ct.ThrowIfCancellationRequested();
            var proj = GetOrCreateProject(pyprojectPath);

            string? currentSection = null;

            foreach (var line in File.ReadLines(pyprojectPath))
            {
                ct.ThrowIfCancellationRequested();

                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#"))
                    continue;

                if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                {
                    currentSection = trimmed.Trim('[', ']');
                    continue;
                }

                if (currentSection is "tool.poetry.dependencies" or "tool.poetry.dev-dependencies")
                {
                    var eqIndex = trimmed.IndexOf('=');
                    if (eqIndex <= 0)
                        continue;

                    var namePart = trimmed[..eqIndex].Trim().Trim('"', '\'');
                    var verPart = trimmed[(eqIndex + 1)..].Trim().Trim('"', '\'');

                    if (string.IsNullOrEmpty(namePart) ||
                        namePart.Equals("python", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    proj.Packages.Add(new PackageRef
                    {
                        Ecosystem = Ecosystem.Python,
                        PackageName = namePart,
                        DeclaredVersion = verPart,
                        UpdateType = VersionUpdateType.Unknown
                    });
                }
            }
        }

        // Pipfile
        foreach (var pipfilePath in DirectoryHelpers.EnumerateFilesSafe(rootPath, "Pipfile"))
        {
            ct.ThrowIfCancellationRequested();
            var proj = GetOrCreateProject(pipfilePath);
            var projectDir = Path.GetDirectoryName(pipfilePath) ?? rootPath;
            var installedVersions = ReadInstalledFromPipfileLock(
                Path.Combine(projectDir, "Pipfile.lock"));

            string? currentSection = null;

            foreach (var line in File.ReadLines(pipfilePath))
            {
                ct.ThrowIfCancellationRequested();

                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#"))
                    continue;

                if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                {
                    currentSection = trimmed.Trim('[', ']');
                    continue;
                }

                if (currentSection is "packages" or "dev-packages")
                {
                    var eqIndex = trimmed.IndexOf('=');
                    if (eqIndex <= 0)
                        continue;

                    var namePart = trimmed[..eqIndex].Trim().Trim('"', '\'');
                    var verPart = trimmed[(eqIndex + 1)..].Trim().Trim('"', '\'');

                    if (string.IsNullOrEmpty(namePart))
                        continue;

                    // Friendly display for Pipfile specs
                    string displayVersion = verPart;

                    if (displayVersion == "*")
                    {
                        displayVersion = "(unconstrained)";
                    }
                    else if (displayVersion.StartsWith("=="))
                    {
                        displayVersion = displayVersion[2..].Trim();
                    }

                    proj.Packages.Add(new PackageRef
                    {
                        Ecosystem = Ecosystem.Python,
                        PackageName = namePart,
                        DeclaredVersion = displayVersion,
                        InstalledVersion = installedVersions.TryGetValue(namePart, out var installed)
                            ? installed
                            : null,
                        UpdateType = VersionUpdateType.Unknown
                    });
                }
            }
        }

        // poetry.lock
        foreach (var lockPath in DirectoryHelpers.EnumerateFilesSafe(rootPath, "poetry.lock"))
        {
            ct.ThrowIfCancellationRequested();

            var projectDir = Path.GetDirectoryName(lockPath) ?? rootPath;
            if (!projectsByDir.TryGetValue(projectDir, out var proj))
                continue;

            var lockData = ReadFromPoetryLock(lockPath);
            foreach (var package in proj.Packages)
            {
                if (string.IsNullOrWhiteSpace(package.InstalledVersion) &&
                    lockData.InstalledVersions.TryGetValue(package.PackageName, out var installedVersion))
                {
                    package.InstalledVersion = installedVersion;
                }
            }

            AttachRelatedSecurityPackages(proj, lockData);
        }

        // Return only directories that actually have packages
        return projectsByDir.Values
            .Where(p => p.Packages.Count > 0)
            .ToList();
    }

    public async Task EnrichWithLatestVersionsAsync(
        IReadOnlyList<ProjectInfo> projects,
        CancellationToken ct = default)
    {
        await EnrichWithLatestVersionsAsync(projects, pythonPackageIndexBaseUrl: null, ct);
    }

    public async Task EnrichWithLatestVersionsAsync(
        IReadOnlyList<ProjectInfo> projects,
        string? pythonPackageIndexBaseUrl,
        CancellationToken ct = default)
    {
        foreach (var project in projects.Where(p => p.Ecosystem == Ecosystem.Python))
        {
            foreach (var pkg in project.Packages)
            {
                ct.ThrowIfCancellationRequested();

                var latest = await GetLatestVersionFromPyPIAsync(
                    pkg.PackageName,
                    pythonPackageIndexBaseUrl,
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

    private static Dictionary<string, string> ReadInstalledFromPipfileLock(string lockPath)
    {
        var installedVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(lockPath))
            return installedVersions;

        try
        {
            using var stream = File.OpenRead(lockPath);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            AddLockedPipfilePackages(root, "default", installedVersions);
            AddLockedPipfilePackages(root, "develop", installedVersions);
        }
        catch
        {
            // Ignore malformed or unreadable lockfiles; declared constraints remain available.
        }

        return installedVersions;
    }

    private static void AddLockedPipfilePackages(
        JsonElement root,
        string propertyName,
        Dictionary<string, string> installedVersions)
    {
        if (!root.TryGetProperty(propertyName, out var packages) ||
            packages.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var package in packages.EnumerateObject())
        {
            if (package.Value.ValueKind != JsonValueKind.Object ||
                !package.Value.TryGetProperty("version", out var versionEl) ||
                versionEl.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var version = versionEl.GetString();
            if (string.IsNullOrWhiteSpace(version))
                continue;

            installedVersions[package.Name] = version;
        }
    }

    private static PoetryLockData ReadFromPoetryLock(string lockPath)
    {
        var installedVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dependenciesByPackage = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(lockPath))
            return new PoetryLockData(installedVersions, dependenciesByPackage);

        try
        {
            string? currentName = null;
            string? currentVersion = null;
            var currentDependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var inPackageDependencies = false;

            void FlushPackage()
            {
                if (string.IsNullOrWhiteSpace(currentName) ||
                    string.IsNullOrWhiteSpace(currentVersion))
                {
                    return;
                }

                installedVersions[currentName] = currentVersion;
                if (currentDependencies.Count > 0)
                {
                    dependenciesByPackage[currentName] = new HashSet<string>(
                        currentDependencies,
                        StringComparer.OrdinalIgnoreCase);
                }
            }

            foreach (var rawLine in File.ReadLines(lockPath))
            {
                var trimmed = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(trimmed) ||
                    trimmed.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                if (trimmed.Equals("[[package]]", StringComparison.Ordinal))
                {
                    FlushPackage();
                    currentName = null;
                    currentVersion = null;
                    currentDependencies.Clear();
                    inPackageDependencies = false;
                    continue;
                }

                if (trimmed.StartsWith("[", StringComparison.Ordinal) &&
                    trimmed.EndsWith("]", StringComparison.Ordinal))
                {
                    inPackageDependencies = trimmed.Equals("[package.dependencies]", StringComparison.Ordinal);
                    continue;
                }

                if (trimmed.StartsWith("name =", StringComparison.Ordinal))
                {
                    currentName = ReadTomlValue(trimmed);
                    continue;
                }

                if (trimmed.StartsWith("version =", StringComparison.Ordinal))
                {
                    currentVersion = ReadTomlValue(trimmed);
                    continue;
                }

                if (inPackageDependencies)
                    AddPoetryDependencyName(trimmed, currentDependencies);
            }

            FlushPackage();
        }
        catch
        {
            // Ignore malformed or unreadable lockfiles; declared constraints remain available.
        }

        return new PoetryLockData(installedVersions, dependenciesByPackage);
    }

    private static void AttachRelatedSecurityPackages(
        ProjectInfo project,
        PoetryLockData lockData)
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
                        Ecosystem = Ecosystem.Python,
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

    private static string ReadTomlValue(string line)
    {
        var eq = line.IndexOf('=');
        if (eq < 0)
            return string.Empty;

        return line[(eq + 1)..].Trim().Trim('"', '\'');
    }

    private static void AddPoetryDependencyName(
        string line,
        HashSet<string> dependencies)
    {
        var eq = line.IndexOf('=');
        if (eq <= 0)
            return;

        var dependencyName = line[..eq].Trim().Trim('"', '\'');
        if (string.IsNullOrWhiteSpace(dependencyName) ||
            dependencyName.Equals("python", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        dependencies.Add(dependencyName);
    }

    private sealed record PoetryLockData(
        IReadOnlyDictionary<string, string> InstalledVersions,
        IReadOnlyDictionary<string, HashSet<string>> DependenciesByPackage);

    private static async Task<string?> GetLatestVersionFromPyPIAsync(
        string packageName,
        string? pythonPackageIndexBaseUrl,
        CancellationToken ct)
    {
        var baseUrl = NormalizeBaseUrl(pythonPackageIndexBaseUrl, DefaultPythonPackageIndexBaseUrl);
        var url = $"{baseUrl}{Uri.EscapeDataString(packageName)}/json";
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
                return null;

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            return doc.RootElement
                      .GetProperty("info")
                      .GetProperty("version")
                      .GetString();
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

        // Special cases: no pinned version
        if (s == "*" || s.Equals("(unconstrained)", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        // Remove leading comparison operators (==, >=, <=, >, <, !=)
        while (s.Length > 0 &&
               (s[0] == '<' || s[0] == '>' || s[0] == '=' || s[0] == '!'))
        {
            s = s[1..].TrimStart();
        }

        // If something like "3.10, !=3.11" appears, keep only first chunk
        var commaIdx = s.IndexOf(',');
        if (commaIdx > 0)
            s = s[..commaIdx].Trim();

        // Strip pre-release / build metadata (simple heuristic)
        var dashIdx = s.IndexOf('-');
        if (dashIdx > 0)
            s = s[..dashIdx];

        var plusIdx = s.IndexOf('+');
        if (plusIdx > 0)
            s = s[..plusIdx];

        return s;
    }

}



