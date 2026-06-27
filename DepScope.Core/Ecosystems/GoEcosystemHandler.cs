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

public sealed class GoEcosystemHandler : IEcosystemHandler
{
    public Ecosystem Ecosystem => Ecosystem.Go;
    private const string DefaultGoProxyBaseUrl = "https://proxy.golang.org/";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    public bool CanHandleDirectory(string rootPath)
    {
        return DirectoryHelpers.EnumerateFilesSafe(rootPath, "go.mod").Any();
    }

    public async Task<IReadOnlyList<ProjectInfo>> ScanProjectsAsync(
        string rootPath,
        CancellationToken ct = default)
    {
        var projects = new List<ProjectInfo>();

        foreach (var path in DirectoryHelpers.EnumerateFilesSafe(rootPath, "go.mod"))
        {
            ct.ThrowIfCancellationRequested();

            var proj = new ProjectInfo
            {
                Name = Path.GetFileName(Path.GetDirectoryName(path) ?? path),
                Path = path,
                Ecosystem = Ecosystem.Go
            };

            var insideRequireBlock = false;

            foreach (var line in File.ReadLines(path))
            {
                ct.ThrowIfCancellationRequested();

                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("//"))
                    continue;

                if (trimmed.StartsWith("require ("))
                {
                    insideRequireBlock = true;
                    continue;
                }
                if (insideRequireBlock && trimmed.StartsWith(")"))
                {
                    insideRequireBlock = false;
                    continue;
                }

                if (insideRequireBlock || trimmed.StartsWith("require "))
                {
                    if (trimmed.Contains("// indirect", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var requiredParts = insideRequireBlock ? 2 : 3;
                    if (parts.Length >= requiredParts)
                    {
                        var module = parts[insideRequireBlock ? 0 : 1];
                        var version = parts[insideRequireBlock ? 1 : 2];

                        proj.Packages.Add(new PackageRef
                        {
                            Ecosystem = Ecosystem.Go,
                            PackageName = module,
                            DeclaredVersion = version,
                            UpdateType = VersionUpdateType.Unknown
                        });
                    }
                }
            }

            if (proj.Packages.Count > 0)
                projects.Add(proj);
        }

        return projects;
    }

    public async Task EnrichWithLatestVersionsAsync(
        IReadOnlyList<ProjectInfo> projects,
        CancellationToken ct = default)
    {
        await EnrichWithLatestVersionsAsync(projects, goProxyBaseUrl: null, ct);
    }

    public async Task EnrichWithLatestVersionsAsync(
        IReadOnlyList<ProjectInfo> projects,
        string? goProxyBaseUrl,
        CancellationToken ct = default)
    {
        foreach (var project in projects.Where(p => p.Ecosystem == Ecosystem.Go))
        {
            foreach (var pkg in project.Packages)
            {
                ct.ThrowIfCancellationRequested();

                var latest = await GetLatestFromGoProxyAsync(pkg.PackageName, goProxyBaseUrl, ct);
                if (latest is null)
                    continue;

                pkg.LatestVersion = latest;

                if (Version.TryParse(NormalizeVersion(pkg.DeclaredVersion), out var declared)
                    && Version.TryParse(NormalizeVersion(latest), out var latestV))
                {
                    pkg.UpdateType = VersionUpdateClassifier.GetUpdateType(declared, latestV);
                }
            }
        }
    }

    private static async Task<string?> GetLatestFromGoProxyAsync(
        string module,
        string? goProxyBaseUrl,
        CancellationToken ct)
    {
        // https://proxy.golang.org/<module>/@latest
        var baseUrl = NormalizeBaseUrl(goProxyBaseUrl, DefaultGoProxyBaseUrl);
        var url = $"{baseUrl}{EscapeModulePath(module)}/@latest";

        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
                return null;

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            return doc.RootElement.GetProperty("Version").GetString();
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeVersion(string v)
    {
        var s = v.Trim();
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            s = s[1..];
        return s;
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

    private static string EscapeModulePath(string module)
    {
        return string.Join(
            "/",
            module
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString));
    }
}
