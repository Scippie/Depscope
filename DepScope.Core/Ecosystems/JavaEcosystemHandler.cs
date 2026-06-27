using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using DepScope.Core.Models;

namespace DepScope.Core.Ecosystems;

public sealed class JavaEcosystemHandler : IEcosystemHandler
{
    public Ecosystem Ecosystem => Ecosystem.Java;
    private const string DefaultMavenSearchBaseUrl = "https://search.maven.org/solrsearch/select";

    private static readonly HttpClient _http = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    public bool CanHandleDirectory(string rootPath)
    {
        bool HasAny(string pattern) =>
            DirectoryHelpers.EnumerateFilesSafe(rootPath, pattern).Any();

        // Maven or Gradle markers
        return HasAny("pom.xml")
               || HasAny("build.gradle")
               || HasAny("build.gradle.kts");
    }

    public async Task<IReadOnlyList<ProjectInfo>> ScanProjectsAsync(
        string rootPath,
        CancellationToken ct = default)
    {
        var projectsByDir = new Dictionary<string, ProjectInfo>(StringComparer.OrdinalIgnoreCase);

        ProjectInfo GetOrCreateProjectForFile(string filePath)
        {
            var dir = Path.GetDirectoryName(filePath) ?? rootPath;
            if (!projectsByDir.TryGetValue(dir, out var proj))
            {
                proj = new ProjectInfo
                {
                    Name = Path.GetFileName(dir),
                    Path = dir,
                    Ecosystem = Ecosystem.Java
                };
                projectsByDir[dir] = proj;
            }
            return proj;
        }

        void ApplyInstalledVersionsFromGradleLockfile(string projectDir)
        {
            if (!projectsByDir.TryGetValue(projectDir, out var project))
                return;

            var installedVersions = ReadInstalledFromGradleLockfile(
                Path.Combine(projectDir, "gradle.lockfile"));

            if (installedVersions.Count == 0)
                return;

            foreach (var package in project.Packages)
            {
                if (installedVersions.TryGetValue(package.PackageName, out var installedVersion))
                    package.InstalledVersion = installedVersion;
            }
        }

        // 1) Maven POMs
        foreach (var pomPath in DirectoryHelpers.EnumerateFilesSafe(rootPath, "pom.xml"))
        {
            ct.ThrowIfCancellationRequested();

            ProjectInfo proj;
            try
            {
                proj = GetOrCreateProjectForFile(pomPath);

                var doc = XDocument.Load(pomPath);
                var root = doc.Root;
                if (root == null)
                    continue;

                var ns = root.Name.Namespace;

                // We just collect all <dependency> entries (regular and dependencyManagement)
                var deps = doc.Descendants(ns + "dependency");

                foreach (var dep in deps)
                {
                    var groupId = dep.Element(ns + "groupId")?.Value?.Trim();
                    var artifactId = dep.Element(ns + "artifactId")?.Value?.Trim();
                    var version = dep.Element(ns + "version")?.Value?.Trim();

                    if (string.IsNullOrWhiteSpace(groupId) ||
                        string.IsNullOrWhiteSpace(artifactId))
                        continue;

                    var declaredVersion = string.IsNullOrWhiteSpace(version)
                        ? "(managed)"       // version comes from parent/BOM
                        : version;

                    proj.Packages.Add(new PackageRef
                    {
                        Ecosystem = Ecosystem.Java,
                        PackageName = $"{groupId}:{artifactId}",
                        DeclaredVersion = declaredVersion,
                        UpdateType = VersionUpdateType.Unknown
                    });
                }
            }
            catch
            {
                // Ignore malformed POMs
            }
        }

        // 2) Gradle build files (Groovy + Kotlin DSL)
        foreach (var gradlePath in DirectoryHelpers.EnumerateFilesSafe(rootPath, "build.gradle"))
        {
            ct.ThrowIfCancellationRequested();
            var proj = GetOrCreateProjectForFile(gradlePath);
            ParseGradleFile(gradlePath, proj, ct);
            ApplyInstalledVersionsFromGradleLockfile(Path.GetDirectoryName(gradlePath) ?? rootPath);
        }

        foreach (var gradlePath in DirectoryHelpers.EnumerateFilesSafe(rootPath, "build.gradle.kts"))
        {
            ct.ThrowIfCancellationRequested();
            var proj = GetOrCreateProjectForFile(gradlePath);
            ParseGradleFile(gradlePath, proj, ct);
            ApplyInstalledVersionsFromGradleLockfile(Path.GetDirectoryName(gradlePath) ?? rootPath);
        }

        // Only keep projects with at least one package
        var result = projectsByDir.Values
            .Where(p => p.Packages != null && p.Packages.Count > 0)
            .ToList();

        return result;
    }

    // Very simple Gradle parser: covers most common cases
    private static void ParseGradleFile(string gradlePath, ProjectInfo proj, CancellationToken ct)
    {
        foreach (var line in File.ReadLines(gradlePath))
        {
            ct.ThrowIfCancellationRequested();

            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("//"))
                continue;

            // Plugins: id "org.springframework.boot" version "2.2.4.RELEASE"
            if (trimmed.StartsWith("id ") && trimmed.Contains(" version "))
            {
                var id = ExtractQuotedPart(trimmed);
                var version = ExtractQuotedAfterKeyword(trimmed, "version");

                if (!string.IsNullOrWhiteSpace(id))
                {
                    proj.Packages.Add(new PackageRef
                    {
                        Ecosystem = Ecosystem.Java,
                        PackageName = $"plugin:{id}",
                        DeclaredVersion = string.IsNullOrWhiteSpace(version) ? "(managed)" : version,
                        UpdateType = VersionUpdateType.Unknown
                    });
                }

                continue;
            }

            // Dependencies like: implementation "group:artifact:version"
            if (StartsWithAny(trimmed,
                    "implementation ",
                    "api ",
                    "compileOnly ",
                    "runtimeOnly ",
                    "testImplementation ",
                    "testCompile ",
                    "testCompileOnly ",
                    "testRuntimeOnly "))
            {
                var spec = ExtractQuotedPart(trimmed);
                if (string.IsNullOrEmpty(spec))
                    continue;

                // "group:artifact:version" or "group:artifact" (managed via BOM)
                var segs = spec.Split(':');
                if (segs.Length < 2)
                    continue;

                var groupId = segs[0].Trim();
                var artifactId = segs[1].Trim();
                string? version = segs.Length >= 3 ? segs[2].Trim() : null;

                if (string.IsNullOrWhiteSpace(groupId) ||
                    string.IsNullOrWhiteSpace(artifactId))
                    continue;

                var declared = string.IsNullOrEmpty(version) ? "(managed)" : version;

                proj.Packages.Add(new PackageRef
                {
                    Ecosystem = Ecosystem.Java,
                    PackageName = $"{groupId}:{artifactId}",
                    DeclaredVersion = declared,
                    UpdateType = VersionUpdateType.Unknown
                });
            }
        }
    }

    private static bool StartsWithAny(string value, params string[] prefixes) =>
        prefixes.Any(p => value.StartsWith(p, StringComparison.Ordinal));

    private static string? ExtractQuotedPart(string text)
    {
        var firstQuote = text.IndexOf('"');
        var quoteChar = '"';

        if (firstQuote < 0)
        {
            firstQuote = text.IndexOf('\'');
            quoteChar = '\'';
        }

        if (firstQuote < 0)
            return null;

        var secondQuote = text.IndexOf(quoteChar, firstQuote + 1);
        if (secondQuote < 0)
            return null;

        return text.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
    }

    private static string? ExtractQuotedAfterKeyword(string text, string keyword)
    {
        var idx = text.IndexOf(keyword, StringComparison.Ordinal);
        if (idx < 0)
            return null;

        var tail = text.Substring(idx + keyword.Length);
        return ExtractQuotedPart(tail);
    }

    public async Task EnrichWithLatestVersionsAsync(
        IReadOnlyList<ProjectInfo> projects,
        CancellationToken ct = default)
    {
        await EnrichWithLatestVersionsAsync(projects, mavenSearchBaseUrl: null, ct);
    }

    public async Task EnrichWithLatestVersionsAsync(
        IReadOnlyList<ProjectInfo> projects,
        string? mavenSearchBaseUrl,
        CancellationToken ct = default)
    {
        var allPackages = projects
            .Where(p => p.Ecosystem == Ecosystem.Java)
            .SelectMany(p => p.Packages)
            .Where(p => p.PackageName != null &&
                        p.PackageName.Contains(':') &&
                        !p.PackageName.StartsWith("plugin:", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var latestByGa = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pkg in allPackages)
        {
            ct.ThrowIfCancellationRequested();

            var ga = pkg.PackageName!;
            if (latestByGa.ContainsKey(ga))
                continue;

            var parts = ga.Split(':');
            if (parts.Length < 2)
                continue;

            var groupId = parts[0];
            var artifactId = parts[1];

            var encodedGroup = Uri.EscapeDataString(groupId);
            var encodedArtifact = Uri.EscapeDataString(artifactId);

            var url = BuildMavenSearchUrl(
                mavenSearchBaseUrl,
                $"g:{encodedGroup}+AND+a:{encodedArtifact}");

            try
            {
                using var resp = await _http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode)
                    continue;

                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("response", out var responseEl) &&
                    responseEl.TryGetProperty("docs", out var docsEl) &&
                    docsEl.ValueKind == JsonValueKind.Array &&
                    docsEl.GetArrayLength() > 0)
                {
                    var first = docsEl[0];

                    string? latest = null;
                    if (first.TryGetProperty("latestVersion", out var lv))
                        latest = lv.GetString();
                    else if (first.TryGetProperty("v", out var v))
                        latest = v.GetString();

                    if (!string.IsNullOrWhiteSpace(latest))
                    {
                        latestByGa[ga] = latest;
                    }
                }
            }
            catch
            {
                // ignore network / parse errors
            }
        }

        // Apply latest + UpdateType
        foreach (var pkg in allPackages)
        {
            ct.ThrowIfCancellationRequested();

            var ga = pkg.PackageName!;
            if (!latestByGa.TryGetValue(ga, out var latest))
                continue;

            pkg.LatestVersion = latest;

            var currentRaw = string.IsNullOrWhiteSpace(pkg.InstalledVersion)
                ? pkg.DeclaredVersion
                : pkg.InstalledVersion;

            // General rule: if current isn't a clean numeric version, keep Unknown
            if (string.IsNullOrWhiteSpace(currentRaw) ||
                currentRaw == "(managed)" ||
                currentRaw.Contains("${"))
            {
                pkg.UpdateType = VersionUpdateType.Unknown;
                continue;
            }

            var declaredNorm = NormalizeVersion(currentRaw);
            var latestNorm = NormalizeVersion(latest);

            if (string.IsNullOrEmpty(declaredNorm) ||
                string.IsNullOrEmpty(latestNorm))
            {
                pkg.UpdateType = VersionUpdateType.Unknown;
                continue;
            }

            if (Version.TryParse(declaredNorm, out var declV) &&
                Version.TryParse(latestNorm, out var latestV) &&
                latestV > declV)
            {
                pkg.UpdateType = VersionUpdateClassifier.GetUpdateType(declV, latestV);
            }
            else
            {
                pkg.UpdateType = VersionUpdateType.None;
            }
        }
    }

    private static Dictionary<string, string> ReadInstalledFromGradleLockfile(string lockPath)
    {
        var installedVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(lockPath))
            return installedVersions;

        try
        {
            foreach (var rawLine in File.ReadLines(lockPath))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(line) ||
                    line.StartsWith("#", StringComparison.Ordinal) ||
                    line.StartsWith("empty=", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var equalsIndex = line.IndexOf('=');
                var coordinate = equalsIndex >= 0
                    ? line[..equalsIndex]
                    : line;

                var parts = coordinate.Split(':', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3)
                    continue;

                var groupId = parts[0].Trim();
                var artifactId = parts[1].Trim();
                var version = parts[2].Trim();

                if (string.IsNullOrWhiteSpace(groupId) ||
                    string.IsNullOrWhiteSpace(artifactId) ||
                    string.IsNullOrWhiteSpace(version))
                {
                    continue;
                }

                installedVersions[$"{groupId}:{artifactId}"] = version;
            }
        }
        catch
        {
            // Ignore malformed or unreadable lockfiles; declared versions remain available.
        }

        return installedVersions;
    }

    private static string BuildMavenSearchUrl(string? configuredBaseUrl, string query)
    {
        var baseUrl = string.IsNullOrWhiteSpace(configuredBaseUrl)
            ? DefaultMavenSearchBaseUrl
            : configuredBaseUrl.Trim();

        var separator = baseUrl.Contains('?', StringComparison.Ordinal)
            ? "&"
            : "?";

        return $"{baseUrl}{separator}q={query}&rows=1&wt=json";
    }


    private static string NormalizeVersion(string v)
    {
        if (string.IsNullOrWhiteSpace(v))
            return string.Empty;

        var s = v.Trim();

        // Skip obvious ranges like [1.0,2.0)
        if ((s.StartsWith("[") || s.StartsWith("(")) && s.Contains(","))
            return string.Empty;

        s = s.Trim('[', ']', '(', ')');

        // Strip common suffixes: -SNAPSHOT, -RELEASE, .RELEASE, etc.
        var stopChars = new[] { '-', '+', ' ' };

        var idx = s.IndexOfAny(stopChars);
        if (idx > 0)
            s = s[..idx];

        // Keep only 0-9 and dots at the beginning
        var chars = s
            .TakeWhile(ch => char.IsDigit(ch) || ch == '.')
            .ToArray();

        return new string(chars);
    }
}
