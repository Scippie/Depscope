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

public sealed class RustEcosystemHandler : IEcosystemHandler
{
    public Ecosystem Ecosystem => Ecosystem.Rust;
    private const string DefaultCratesApiBaseUrl = "https://crates.io/api/v1/";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    public bool CanHandleDirectory(string rootPath)
    {
        return DirectoryHelpers.EnumerateFilesSafe(rootPath, "Cargo.toml").Any();
    }

    public async Task<IReadOnlyList<ProjectInfo>> ScanProjectsAsync(
        string rootPath,
        CancellationToken ct = default)
    {
        var projects = new List<ProjectInfo>();

        foreach (var path in DirectoryHelpers.EnumerateFilesSafe(rootPath, "Cargo.toml"))
        {
            ct.ThrowIfCancellationRequested();

            var projectDir = Path.GetDirectoryName(path) ?? rootPath;
            var lockData = ReadFromCargoLock(
                FindNearestCargoLock(projectDir, rootPath));

            var proj = new ProjectInfo
            {
                Name = Path.GetFileName(projectDir),
                Path = path,
                Ecosystem = Ecosystem.Rust
            };

            string? currentSection = null;

            foreach (var line in File.ReadLines(path))
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

                if (currentSection is "dependencies" or "dev-dependencies" or "build-dependencies")
                {
                    // simple: name = "version"
                    var eq = trimmed.IndexOf('=');
                    if (eq <= 0)
                        continue;

                    var name = trimmed[..eq].Trim();
                    var rest = trimmed[(eq + 1)..].Trim();

                    if (rest.StartsWith("{"))
                    {
                        // inline table: try to find version = "..."
                        var idx = rest.IndexOf("version", StringComparison.OrdinalIgnoreCase);
                        if (idx < 0)
                            continue;

                        var after = rest[idx..];
                        var verEq = after.IndexOf('=');
                        if (verEq < 0)
                            continue;

                        var verPart = ReadTomlInlineValue(after[(verEq + 1)..]);
                        if (string.IsNullOrEmpty(verPart))
                            continue;

                        proj.Packages.Add(new PackageRef
                        {
                            Ecosystem = Ecosystem.Rust,
                            PackageName = name,
                            DeclaredVersion = verPart,
                            InstalledVersion = lockData.InstalledVersions.TryGetValue(name, out var installed)
                                ? installed
                                : null,
                            UpdateType = VersionUpdateType.Unknown
                        });
                    }
                    else
                    {
                        var verPart = rest.Trim().Trim('"', '\'');
                        if (string.IsNullOrEmpty(verPart))
                            continue;

                        proj.Packages.Add(new PackageRef
                        {
                            Ecosystem = Ecosystem.Rust,
                            PackageName = name,
                            DeclaredVersion = verPart,
                            InstalledVersion = lockData.InstalledVersions.TryGetValue(name, out var installed)
                                ? installed
                                : null,
                            UpdateType = VersionUpdateType.Unknown
                        });
                    }
                }
            }

            AttachRelatedSecurityPackages(proj, lockData);

            if (proj.Packages.Count > 0)
                projects.Add(proj);
        }

        return projects;
    }

    public async Task EnrichWithLatestVersionsAsync(
        IReadOnlyList<ProjectInfo> projects,
        CancellationToken ct = default)
    {
        await EnrichWithLatestVersionsAsync(projects, cratesApiBaseUrl: null, ct);
    }

    public async Task EnrichWithLatestVersionsAsync(
        IReadOnlyList<ProjectInfo> projects,
        string? cratesApiBaseUrl,
        CancellationToken ct = default)
    {
        // 1) Collect all Rust packages
        var rustPackages = projects
            .Where(p => p.Ecosystem == Ecosystem.Rust)
            .SelectMany(p => p.Packages)
            .Where(p => !string.IsNullOrWhiteSpace(p.PackageName))
            .ToList();

        if (rustPackages.Count == 0)
            return;

        // crate_name -> latest version
        var latestByCrate = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 2) Query crates.io for each unique crate name
        foreach (var pkg in rustPackages)
        {
            ct.ThrowIfCancellationRequested();

            var crateName = pkg.PackageName!.Trim();

            // Already resolved?
            if (latestByCrate.ContainsKey(crateName))
                continue;

            // Skip clearly invalid crate names
            if (crateName.Contains(' ') || crateName.Contains('/'))
                continue;

            var baseUrl = NormalizeBaseUrl(cratesApiBaseUrl, DefaultCratesApiBaseUrl);
            var url = $"{baseUrl}crates/{Uri.EscapeDataString(crateName)}";

            try
            {
                using var resp = await _http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    // You can log resp.StatusCode here if you want
                    continue;
                }

                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("crate", out var crateEl))
                    continue;

                string? latest = null;

                // Prefer max_stable_version if present
                if (crateEl.TryGetProperty("max_stable_version", out var msv)
                    && msv.ValueKind == JsonValueKind.String)
                {
                    latest = msv.GetString();
                }

                // Fallback: max_version
                if (string.IsNullOrWhiteSpace(latest)
                    && crateEl.TryGetProperty("max_version", out var mv)
                    && mv.ValueKind == JsonValueKind.String)
                {
                    latest = mv.GetString();
                }

                if (!string.IsNullOrWhiteSpace(latest))
                {
                    latestByCrate[crateName] = latest!;
                }
            }
            catch
            {
                // ignore network / parse errors; we just won't fill Latest
            }
        }

        // 3) Apply Latest + UpdateType
        foreach (var pkg in rustPackages)
        {
            ct.ThrowIfCancellationRequested();

            var crateName = pkg.PackageName!.Trim();

            if (!latestByCrate.TryGetValue(crateName, out var latest))
                continue;

            pkg.LatestVersion = latest;

            var currentRaw = string.IsNullOrWhiteSpace(pkg.InstalledVersion)
                ? pkg.DeclaredVersion
                : pkg.InstalledVersion;

            // If current is missing or not a simple version, we keep Unknown
            if (string.IsNullOrWhiteSpace(currentRaw))
            {
                pkg.UpdateType = VersionUpdateType.Unknown;
                continue;
            }

            var currentNorm = NormalizeRustVersion(currentRaw);
            var latestNorm = NormalizeRustVersion(latest);

            if (string.IsNullOrEmpty(currentNorm) || string.IsNullOrEmpty(latestNorm))
            {
                pkg.UpdateType = VersionUpdateType.Unknown;
                continue;
            }

            if (Version.TryParse(currentNorm, out var currentV) &&
                Version.TryParse(latestNorm, out var latestV) &&
                latestV > currentV)
            {
                pkg.UpdateType = VersionUpdateClassifier.GetUpdateType(currentV, latestV);
            }
            else
            {
                pkg.UpdateType = VersionUpdateType.None;
            }
        }
    }

    private static string? FindNearestCargoLock(string startDir, string stopAtRoot)
    {
        var current = new DirectoryInfo(startDir);
        var stop = new DirectoryInfo(stopAtRoot);

        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "Cargo.lock");
            if (File.Exists(candidate))
                return candidate;

            if (string.Equals(
                    current.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    stop.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = current.Parent;
        }

        return null;
    }

    private static void AttachRelatedSecurityPackages(
        ProjectInfo project,
        CargoLockData lockData)
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
                        Ecosystem = Ecosystem.Rust,
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

    private static CargoLockData ReadFromCargoLock(string? lockPath)
    {
        var installedVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dependenciesByPackage = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(lockPath) || !File.Exists(lockPath))
            return new CargoLockData(installedVersions, dependenciesByPackage);

        try
        {
            string? currentName = null;
            string? currentVersion = null;
            var currentDependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var inDependenciesArray = false;
            var versionCounts = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            void FlushPackage()
            {
                if (string.IsNullOrWhiteSpace(currentName) ||
                    string.IsNullOrWhiteSpace(currentVersion))
                {
                    return;
                }

                if (!versionCounts.TryGetValue(currentName, out var versions))
                {
                    versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    versionCounts[currentName] = versions;
                }

                versions.Add(currentVersion);

                if (currentDependencies.Count > 0)
                {
                    dependenciesByPackage[currentName] = new HashSet<string>(
                        currentDependencies,
                        StringComparer.OrdinalIgnoreCase);
                }
            }

            foreach (var rawLine in File.ReadLines(lockPath))
            {
                var line = rawLine.Trim();

                if (line.Equals("[[package]]", StringComparison.Ordinal))
                {
                    FlushPackage();
                    currentName = null;
                    currentVersion = null;
                    currentDependencies.Clear();
                    inDependenciesArray = false;
                    continue;
                }

                if (inDependenciesArray)
                {
                    AddCargoDependencyNamesFromLine(line, currentDependencies);
                    if (line.Contains(']'))
                        inDependenciesArray = false;

                    continue;
                }

                if (line.StartsWith("name =", StringComparison.Ordinal))
                {
                    currentName = ReadTomlStringValue(line);
                }
                else if (line.StartsWith("version =", StringComparison.Ordinal))
                {
                    currentVersion = ReadTomlStringValue(line);
                }
                else if (line.StartsWith("dependencies =", StringComparison.Ordinal))
                {
                    AddCargoDependencyNamesFromLine(line, currentDependencies);
                    inDependenciesArray = !line.Contains(']');
                }
            }

            FlushPackage();

            foreach (var (name, versions) in versionCounts)
            {
                if (versions.Count == 1)
                    installedVersions[name] = versions.Single();
            }
        }
        catch
        {
            // Ignore malformed or unreadable lockfiles; declared constraints remain available.
        }

        return new CargoLockData(installedVersions, dependenciesByPackage);
    }

    private static void AddCargoDependencyNamesFromLine(
        string line,
        HashSet<string> dependencies)
    {
        var start = 0;
        while (start < line.Length)
        {
            var open = line.IndexOf('"', start);
            if (open < 0)
                break;

            var close = line.IndexOf('"', open + 1);
            if (close < 0)
                break;

            var dependency = ParseCargoLockDependencyName(line[(open + 1)..close]);
            if (!string.IsNullOrWhiteSpace(dependency))
                dependencies.Add(dependency);

            start = close + 1;
        }
    }

    private static string ParseCargoLockDependencyName(string dependency)
    {
        var trimmed = dependency.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return string.Empty;

        var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0
            ? string.Empty
            : parts[0].Trim();
    }

    private sealed record CargoLockData(
        IReadOnlyDictionary<string, string> InstalledVersions,
        IReadOnlyDictionary<string, HashSet<string>> DependenciesByPackage);

    private static string NormalizeBaseUrl(string? configuredBaseUrl, string defaultBaseUrl)
    {
        var baseUrl = string.IsNullOrWhiteSpace(configuredBaseUrl)
            ? defaultBaseUrl
            : configuredBaseUrl.Trim();

        return baseUrl.EndsWith("/", StringComparison.Ordinal)
            ? baseUrl
            : baseUrl + "/";
    }

    private static string? ReadTomlStringValue(string line)
    {
        var equalsIndex = line.IndexOf('=');
        if (equalsIndex < 0)
            return null;

        return ReadTomlInlineValue(line[(equalsIndex + 1)..]);
    }

    private static string ReadTomlInlineValue(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
            return string.Empty;

        if (trimmed[0] is '"' or '\'')
        {
            var quote = trimmed[0];
            var closeIndex = trimmed.IndexOf(quote, startIndex: 1);
            return closeIndex > 0
                ? trimmed[1..closeIndex].Trim()
                : trimmed.Trim(quote).Trim();
        }

        var commaIndex = trimmed.IndexOf(',');
        if (commaIndex > 0)
            trimmed = trimmed[..commaIndex];

        var braceIndex = trimmed.IndexOf('}');
        if (braceIndex > 0)
            trimmed = trimmed[..braceIndex];

        return trimmed.Trim();
    }


    private static string NormalizeRustVersion(string v)
    {
        if (string.IsNullOrWhiteSpace(v))
            return string.Empty;

        var s = v.Trim();

        // Strip leading constraint characters
        while (s.Length > 0 && (s[0] == '^' || s[0] == '~' || s[0] == '=' || s[0] == 'v'))
        {
            s = s[1..];
        }

        // Cut at first space or comma
        var stopIdx = s.IndexOfAny(new[] { ' ', ',' });
        if (stopIdx > 0)
            s = s[..stopIdx];

        // Cut at pre-release suffix
        var dashIdx = s.IndexOf('-');
        if (dashIdx > 0)
            s = s[..dashIdx];

        // Keep only digits and dots from the start
        var chars = s.TakeWhile(ch => char.IsDigit(ch) || ch == '.').ToArray();
        var cleaned = new string(chars);

        // Normalize to x.y if just "x"
        if (!cleaned.Contains('.') && cleaned.Length > 0)
            cleaned += ".0";

        return cleaned;
    }


}
