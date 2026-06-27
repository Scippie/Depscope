using System.Text.RegularExpressions;
using System.Text.Json;
using DepScope.Core.Models;

namespace DepScope.Core.Ecosystems;

public sealed partial class GitHubActionsEcosystemHandler : IEcosystemHandler
{
    public Ecosystem Ecosystem => Ecosystem.GitHubActions;
    private const string DefaultGitHubApiBaseUrl = "https://api.github.com/";

    private static readonly HttpClient _http = CreateHttpClient();

    public bool CanHandleDirectory(string rootPath)
    {
        return EnumerateWorkflowFiles(rootPath).Any();
    }

    public async Task<IReadOnlyList<ProjectInfo>> ScanProjectsAsync(
        string rootPath,
        CancellationToken ct = default)
    {
        var projects = new List<ProjectInfo>();

        foreach (var workflowPath in EnumerateWorkflowFiles(rootPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();

            string[] lines;
            try
            {
                lines = await File.ReadAllLinesAsync(workflowPath, ct);
            }
            catch (Exception ex) when (
                ex is IOException ||
                ex is UnauthorizedAccessException)
            {
                continue;
            }

            var packages = lines
                .Select(ParseUsesReference)
                .OfType<PackageRef>()
                .ToList();

            if (packages.Count == 0)
                continue;

            var project = new ProjectInfo
            {
                Name = GetWorkflowName(lines) ?? Path.GetFileNameWithoutExtension(workflowPath),
                Path = workflowPath,
                Ecosystem = Ecosystem.GitHubActions
            };

            project.Packages.AddRange(packages);
            projects.Add(project);
        }

        return projects;
    }

    public async Task EnrichWithLatestVersionsAsync(
        IReadOnlyList<ProjectInfo> projects,
        CancellationToken ct = default)
    {
        await EnrichWithLatestVersionsAsync(projects, gitHubApiBaseUrl: null, ct);
    }

    public async Task EnrichWithLatestVersionsAsync(
        IReadOnlyList<ProjectInfo> projects,
        string? gitHubApiBaseUrl,
        CancellationToken ct = default)
    {
        var actionRefs = projects
            .Where(p => p.Ecosystem == Ecosystem.GitHubActions)
            .SelectMany(p => p.Packages)
            .Where(p => !string.IsNullOrWhiteSpace(p.PackageName))
            .ToList();

        if (actionRefs.Count == 0)
            return;

        var cache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var actionRef in actionRefs)
        {
            ct.ThrowIfCancellationRequested();

            var repoKey = GetRepositoryKey(actionRef.PackageName);
            if (repoKey is null)
                continue;

            if (!cache.TryGetValue(repoKey, out var latestTag))
            {
                latestTag = await GetLatestRepositoryTagAsync(
                    repoKey,
                    gitHubApiBaseUrl,
                    ct);
                cache[repoKey] = latestTag;
            }

            if (string.IsNullOrWhiteSpace(latestTag))
                continue;

            actionRef.LatestVersion = latestTag;
            actionRef.UpdateType = ClassifyVersionTagUpdate(actionRef.DeclaredVersion, latestTag);

            if (actionRef.UpdateType == VersionUpdateType.None)
                actionRef.LatestVersion = actionRef.DeclaredVersion;
        }
    }

    private static IEnumerable<string> EnumerateWorkflowFiles(string rootPath)
    {
        return DirectoryHelpers
            .EnumerateFilesSafe(rootPath, "*.yml")
            .Concat(DirectoryHelpers.EnumerateFilesSafe(rootPath, "*.yaml"))
            .Where(IsGitHubWorkflowFile);
    }

    private static bool IsGitHubWorkflowFile(string path)
    {
        var parts = path.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < parts.Length - 2; i++)
        {
            if (string.Equals(parts[i], ".github", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(parts[i + 1], "workflows", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static PackageRef? ParseUsesReference(string line)
    {
        var match = UsesLineRegex().Match(line);
        if (!match.Success)
            return null;

        var value = NormalizeYamlScalar(match.Groups["value"].Value);
        if (string.IsNullOrWhiteSpace(value) ||
            value.StartsWith("./", StringComparison.Ordinal) ||
            value.StartsWith("docker://", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var atIndex = value.LastIndexOf('@');
        if (atIndex <= 0 || atIndex == value.Length - 1)
            return null;

        var actionPath = value[..atIndex].Trim();
        var reference = value[(atIndex + 1)..].Trim();

        if (!LooksLikeExternalGitHubReference(actionPath) ||
            string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        return new PackageRef
        {
            Ecosystem = Ecosystem.GitHubActions,
            PackageName = actionPath,
            DeclaredVersion = reference,
            InstalledVersion = GetReferencePinningStatus(reference),
            UpdateType = VersionUpdateType.Unknown
        };
    }

    internal static string GetReferencePinningStatus(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return "Unknown ref";

        var trimmed = reference.Trim();

        if (trimmed.Contains("${", StringComparison.Ordinal))
            return "Dynamic ref";

        if (FullGitShaRegex().IsMatch(trimmed))
            return "SHA pinned";

        if (ShortGitShaRegex().IsMatch(trimmed))
            return "Short SHA ref";

        return "Tag/branch ref";
    }

    private static bool LooksLikeExternalGitHubReference(string actionPath)
    {
        var parts = actionPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 &&
               !parts[0].StartsWith(".", StringComparison.Ordinal) &&
               !parts[0].Contains("${", StringComparison.Ordinal) &&
               !parts[1].Contains("${", StringComparison.Ordinal);
    }

    private static string? GetRepositoryKey(string actionPath)
    {
        var parts = actionPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return null;

        return $"{parts[0]}/{parts[1]}";
    }

    private static async Task<string?> GetLatestRepositoryTagAsync(
        string repositoryKey,
        string? gitHubApiBaseUrl,
        CancellationToken ct)
    {
        var parts = repositoryKey.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            return null;

        var owner = Uri.EscapeDataString(parts[0]);
        var repo = Uri.EscapeDataString(parts[1]);
        var baseUrl = NormalizeGitHubApiBaseUrl(gitHubApiBaseUrl);
        var url = $"{baseUrl}repos/{owner}/{repo}/tags?per_page=100";

        try
        {
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
                return await GetLatestRepositoryReleaseTagAsync(baseUrl, owner, repo, ct);

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return await GetLatestRepositoryReleaseTagAsync(baseUrl, owner, repo, ct);

            string? bestTag = null;
            Version? bestVersion = null;

            foreach (var tag in doc.RootElement.EnumerateArray())
            {
                if (!tag.TryGetProperty("name", out var nameEl) ||
                    nameEl.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var name = nameEl.GetString();
                if (string.IsNullOrWhiteSpace(name) ||
                    !TryParseVersionTag(name, out var parsed, out _))
                {
                    continue;
                }

                if (bestVersion is null || parsed > bestVersion)
                {
                    bestVersion = parsed;
                    bestTag = name;
                }
            }

            return bestTag ?? await GetLatestRepositoryReleaseTagAsync(baseUrl, owner, repo, ct);
        }
        catch
        {
            return await GetLatestRepositoryReleaseTagFromWebAsync(parts[0], parts[1], ct);
        }
    }

    private static async Task<string?> GetLatestRepositoryReleaseTagAsync(
        string gitHubApiBaseUrl,
        string owner,
        string repo,
        CancellationToken ct)
    {
        var url = $"{gitHubApiBaseUrl}repos/{owner}/{repo}/releases/latest";

        try
        {
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
                return await GetLatestRepositoryReleaseTagFromWebAsync(
                    Uri.UnescapeDataString(owner),
                    Uri.UnescapeDataString(repo),
                    ct);

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            return doc.RootElement.TryGetProperty("tag_name", out var tagName) &&
                   tagName.ValueKind == JsonValueKind.String
                ? tagName.GetString()
                : await GetLatestRepositoryReleaseTagFromWebAsync(
                    Uri.UnescapeDataString(owner),
                    Uri.UnescapeDataString(repo),
                    ct);
        }
        catch
        {
            return await GetLatestRepositoryReleaseTagFromWebAsync(
                Uri.UnescapeDataString(owner),
                Uri.UnescapeDataString(repo),
                ct);
        }
    }

    private static async Task<string?> GetLatestRepositoryReleaseTagFromWebAsync(
        string owner,
        string repo,
        CancellationToken ct)
    {
        var escapedOwner = Uri.EscapeDataString(owner);
        var escapedRepo = Uri.EscapeDataString(repo);
        var url = $"https://github.com/{escapedOwner}/{escapedRepo}/releases/latest";

        try
        {
            using var response = await _http.GetAsync(
                url,
                HttpCompletionOption.ResponseHeadersRead,
                ct);

            var redirectedUrl = response.RequestMessage?.RequestUri?.AbsoluteUri;
            var tag = ExtractLatestTagFromReleaseRedirect(redirectedUrl);
            if (!string.IsNullOrWhiteSpace(tag))
                return tag;

            if (!response.IsSuccessStatusCode)
                return null;

            var html = await response.Content.ReadAsStringAsync(ct);
            return ExtractLatestTagFromHtml(html);
        }
        catch
        {
            return null;
        }
    }

    internal static string? ExtractLatestTagFromReleaseRedirect(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var marker = "/releases/tag/";
        var markerIndex = url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
            return null;

        var tagStart = markerIndex + marker.Length;
        var tagEnd = url.IndexOfAny(new[] { '?', '#', '/' }, tagStart);
        var rawTag = tagEnd >= 0
            ? url[tagStart..tagEnd]
            : url[tagStart..];

        return string.IsNullOrWhiteSpace(rawTag)
            ? null
            : Uri.UnescapeDataString(rawTag);
    }

    internal static string? ExtractLatestTagFromHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;

        var bestTag = default(string);
        var bestVersion = default(Version);

        foreach (Match match in ReleaseTagHrefRegex().Matches(html))
        {
            var tag = Uri.UnescapeDataString(match.Groups["tag"].Value);
            if (!TryParseVersionTag(tag, out var version, out _))
                continue;

            if (bestVersion is null || version > bestVersion)
            {
                bestVersion = version;
                bestTag = tag;
            }
        }

        return bestTag;
    }

    internal static string NormalizeGitHubApiBaseUrl(string? configuredBaseUrl)
    {
        var baseUrl = string.IsNullOrWhiteSpace(configuredBaseUrl)
            ? DefaultGitHubApiBaseUrl
            : configuredBaseUrl.Trim();

        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) &&
            (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
             uri.Host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase)))
        {
            baseUrl = DefaultGitHubApiBaseUrl;
        }

        return baseUrl.EndsWith("/", StringComparison.Ordinal)
            ? baseUrl
            : baseUrl + "/";
    }

    internal static VersionUpdateType ClassifyVersionTagUpdate(string currentRef, string latestTag)
    {
        if (!TryParseVersionTag(currentRef, out var currentVersion, out var currentPrecision) ||
            !TryParseVersionTag(latestTag, out var latestVersion, out _))
        {
            return VersionUpdateType.Unknown;
        }

        if (currentPrecision == 1 && latestVersion.Major == currentVersion.Major)
            return VersionUpdateType.None;

        if (latestVersion <= currentVersion)
            return VersionUpdateType.None;

        return VersionUpdateClassifier.GetUpdateType(currentVersion, latestVersion);
    }

    private static bool TryParseVersionTag(
        string rawTag,
        out Version version,
        out int precision)
    {
        version = new Version(0, 0, 0);
        precision = 0;

        var tag = rawTag.Trim();
        if (tag.StartsWith("refs/tags/", StringComparison.OrdinalIgnoreCase))
            tag = tag["refs/tags/".Length..];

        if (tag.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            tag = tag[1..];

        var match = VersionTagRegex().Match(tag);
        if (!match.Success)
            return false;

        var majorText = match.Groups["major"].Value;
        var minorText = match.Groups["minor"].Value;
        var patchText = match.Groups["patch"].Value;

        if (!int.TryParse(majorText, out var major))
            return false;

        var minor = 0;
        var patch = 0;
        precision = 1;

        if (!string.IsNullOrWhiteSpace(minorText))
        {
            if (!int.TryParse(minorText, out minor))
                return false;

            precision = 2;
        }

        if (!string.IsNullOrWhiteSpace(patchText))
        {
            if (!int.TryParse(patchText, out patch))
                return false;

            precision = 3;
        }

        version = new Version(major, minor, patch);
        return true;
    }

    private static string NormalizeYamlScalar(string value)
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

        var commentIndex = trimmed.IndexOf(" #", StringComparison.Ordinal);
        if (commentIndex >= 0)
            trimmed = trimmed[..commentIndex];

        var spaceIndex = trimmed.IndexOfAny(new[] { ' ', '\t' });
        if (spaceIndex >= 0)
            trimmed = trimmed[..spaceIndex];

        return trimmed.Trim();
    }

    private static string? GetWorkflowName(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var match = NameLineRegex().Match(line);
            if (!match.Success)
                continue;

            var name = NormalizeYamlScalar(match.Groups["value"].Value);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }

        return null;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("DepScope");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    [GeneratedRegex(@"^\s*(?:-\s*)?uses\s*:\s*(?<value>.+?)\s*$")]
    private static partial Regex UsesLineRegex();

    [GeneratedRegex(@"^\s*name\s*:\s*(?<value>.+?)\s*$")]
    private static partial Regex NameLineRegex();

    [GeneratedRegex(@"^(?<major>\d+)(?:\.(?<minor>\d+))?(?:\.(?<patch>\d+))?$")]
    private static partial Regex VersionTagRegex();

    [GeneratedRegex(@"/releases/tag/(?<tag>[^""'<>?#/]+)")]
    private static partial Regex ReleaseTagHrefRegex();

    [GeneratedRegex(@"^[0-9a-fA-F]{40}$")]
    private static partial Regex FullGitShaRegex();

    [GeneratedRegex(@"^[0-9a-fA-F]{7,39}$")]
    private static partial Regex ShortGitShaRegex();
}
