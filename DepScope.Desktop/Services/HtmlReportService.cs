using DepScope.Core.Models;
using DepScope.Desktop.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DepScope.Desktop.Services;

public static class HtmlReportService
{
    public static async Task WriteReportAsync(
        IEnumerable<ProjectGroup> projectGroups,
        string filePath,
        CancellationToken ct = default)
    {
        var html = GenerateReport(projectGroups, DateTimeOffset.Now);
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        await File.WriteAllTextAsync(filePath, html, Encoding.UTF8, ct);
    }

    public static string GenerateReport(
        IEnumerable<ProjectGroup> projectGroups,
        DateTimeOffset generatedAt)
    {
        var groups = projectGroups.ToList();
        var projects = groups.SelectMany(group => group.Projects).ToList();
        var packages = projects.SelectMany(project => project.Packages).ToList();
        var outdatedPackages = packages.Count(IsOutdated);
        var vulnerablePackages = packages.Count(IsVulnerable);

        var builder = new StringBuilder();
        builder.AppendLine("<!doctype html>");
        builder.AppendLine("<html lang=\"en\">");
        builder.AppendLine("<head>");
        builder.AppendLine("<meta charset=\"utf-8\">");
        builder.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        builder.AppendLine("<title>DepScope Dependency Report</title>");
        builder.AppendLine("<style>");
        builder.AppendLine(":root{color-scheme:light dark;font-family:Segoe UI,Arial,sans-serif;}");
        builder.AppendLine("body{margin:24px;background:#111827;color:#E5E7EB;}");
        builder.AppendLine("h1,h2,h3{margin:0 0 10px;}");
        builder.AppendLine(".meta{color:#9CA3AF;margin-bottom:18px;}");
        builder.AppendLine(".summary{display:flex;flex-wrap:wrap;gap:10px;margin:0 0 24px;}");
        builder.AppendLine(".metric{border:1px solid #374151;border-radius:6px;padding:10px 12px;min-width:130px;background:#1F2937;}");
        builder.AppendLine(".metric strong{display:block;font-size:22px;}");
        builder.AppendLine(".group{margin:28px 0 0;}");
        builder.AppendLine(".project{margin:16px 0 26px;}");
        builder.AppendLine(".project-meta{color:#9CA3AF;font-size:13px;margin-bottom:8px;}");
        builder.AppendLine("table{border-collapse:collapse;width:100%;font-size:13px;}");
        builder.AppendLine("th,td{border:1px solid #374151;padding:7px 8px;text-align:left;vertical-align:top;}");
        builder.AppendLine("th{background:#1F2937;}");
        builder.AppendLine("tr:nth-child(even){background:#172033;}");
        builder.AppendLine(".badge{display:inline-block;border-radius:4px;padding:2px 6px;font-size:12px;border:1px solid #4B5563;}");
        builder.AppendLine(".major,.critical,.high{color:#FCA5A5;border-color:#EF4444;}");
        builder.AppendLine(".minor,.medium{color:#FDBA74;border-color:#F97316;}");
        builder.AppendLine(".patch,.low{color:#FDE68A;border-color:#F59E0B;}");
        builder.AppendLine(".none{color:#86EFAC;border-color:#22C55E;}");
        builder.AppendLine(".unknown{color:#D1D5DB;border-color:#6B7280;}");
        builder.AppendLine(".advisory{margin:4px 0;color:#D1D5DB;}");
        builder.AppendLine("a{color:#93C5FD;}");
        builder.AppendLine("@media print{body{background:white;color:black}.metric,th,tr:nth-child(even){background:white}.meta,.project-meta{color:#555}}");
        builder.AppendLine("</style>");
        builder.AppendLine("</head>");
        builder.AppendLine("<body>");
        builder.AppendLine("<h1>DepScope Dependency Report</h1>");
        builder.AppendLine($"<div class=\"meta\">Generated {Encode(generatedAt.ToString("yyyy-MM-dd HH:mm:ss zzz"))}</div>");
        builder.AppendLine("<section class=\"summary\">");
        AppendMetric(builder, "Root groups", groups.Count);
        AppendMetric(builder, "Projects", projects.Count);
        AppendMetric(builder, "Packages", packages.Count);
        AppendMetric(builder, "Outdated", outdatedPackages);
        AppendMetric(builder, "Vulnerable", vulnerablePackages);
        builder.AppendLine("</section>");

        foreach (var group in groups)
        {
            builder.AppendLine("<section class=\"group\">");
            builder.AppendLine($"<h2>{Encode(group.Name)}</h2>");
            builder.AppendLine($"<div class=\"project-meta\">Root: {Encode(group.RootPath)}</div>");

            foreach (var project in group.Projects)
            {
                builder.AppendLine("<section class=\"project\">");
                builder.AppendLine($"<h3>{Encode(project.Name)}</h3>");
                builder.AppendLine($"<div class=\"project-meta\">{Encode(project.Ecosystem.ToString())} | {Encode(project.Path)}</div>");
                AppendPackageTable(builder, project.Packages);
                builder.AppendLine("</section>");
            }

            builder.AppendLine("</section>");
        }

        builder.AppendLine("</body>");
        builder.AppendLine("</html>");
        return builder.ToString();
    }

    private static void AppendPackageTable(StringBuilder builder, IReadOnlyCollection<PackageRef> packages)
    {
        builder.AppendLine("<table>");
        builder.AppendLine("<thead><tr><th>Package</th><th>Declared</th><th>Installed</th><th>Latest</th><th>Update</th><th>Vulnerabilities</th><th>Advisories</th></tr></thead>");
        builder.AppendLine("<tbody>");

        foreach (var package in packages.OrderBy(package => package.PackageName, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine("<tr>");
            builder.AppendLine($"<td>{Encode(package.PackageName)}</td>");
            builder.AppendLine($"<td>{Encode(package.DeclaredVersion)}</td>");
            builder.AppendLine($"<td>{Encode(package.InstalledVersion)}</td>");
            builder.AppendLine($"<td>{Encode(package.LatestVersion)}</td>");
            builder.AppendLine($"<td>{Badge(package.UpdateType.ToString(), UpdateClass(package.UpdateType))}</td>");
            builder.AppendLine($"<td>{Badge(package.VulnerabilityStatus, SeverityClass(package.VulnerabilitySeverity))}</td>");
            builder.AppendLine($"<td>{FormatAdvisories(package)}</td>");
            builder.AppendLine("</tr>");
        }

        if (packages.Count == 0)
            builder.AppendLine("<tr><td colspan=\"7\">No packages found.</td></tr>");

        builder.AppendLine("</tbody>");
        builder.AppendLine("</table>");
    }

    private static string FormatAdvisories(PackageRef package)
    {
        if (package.VulnerabilityAdvisories.Count > 0)
        {
            return string.Join(
                string.Empty,
                package.VulnerabilityAdvisories.Select(advisory =>
                    $"<div class=\"advisory\">{FormatAdvisory(advisory)}</div>"));
        }

        return string.IsNullOrWhiteSpace(package.VulnerabilityIds)
            ? string.Empty
            : Encode(package.VulnerabilityIds);
    }

    private static string FormatAdvisory(VulnerabilityAdvisory advisory)
    {
        var parts = new List<string>
        {
            $"{Encode(advisory.Id)} [{Encode(advisory.Severity.ToString())}]",
            $"Affected: {Encode(advisory.AffectedPackageName)}@{Encode(advisory.AffectedVersion)}"
        };

        if (!string.IsNullOrWhiteSpace(advisory.Relationship))
            parts.Add($"Via: {Encode(advisory.Relationship)}");

        if (advisory.Aliases.Count > 0)
            parts.Add($"Aliases: {Encode(string.Join(", ", advisory.Aliases))}");

        if (!string.IsNullOrWhiteSpace(advisory.Summary))
            parts.Add($"Summary: {Encode(advisory.Summary)}");

        if (!string.IsNullOrWhiteSpace(advisory.Url))
            parts.Add($"<a href=\"{Encode(advisory.Url)}\">{Encode(advisory.Url)}</a>");

        return string.Join(" | ", parts);
    }

    private static void AppendMetric(StringBuilder builder, string label, int value)
    {
        builder.AppendLine($"<div class=\"metric\"><strong>{value}</strong>{Encode(label)}</div>");
    }

    private static bool IsOutdated(PackageRef package)
    {
        return package.UpdateType != VersionUpdateType.None &&
            package.UpdateType != VersionUpdateType.Unknown &&
            !string.IsNullOrWhiteSpace(package.LatestVersion);
    }

    private static bool IsVulnerable(PackageRef package)
    {
        if (package.VulnerabilityCount <= 0)
            return false;

        return package.VulnerabilitySeverity is VulnerabilitySeverity.Low or
            VulnerabilitySeverity.Medium or
            VulnerabilitySeverity.High or
            VulnerabilitySeverity.Critical or
            VulnerabilitySeverity.Unknown;
    }

    private static string Badge(string text, string cssClass)
    {
        return $"<span class=\"badge {cssClass}\">{Encode(text)}</span>";
    }

    private static string UpdateClass(VersionUpdateType updateType)
    {
        return updateType switch
        {
            VersionUpdateType.Major => "major",
            VersionUpdateType.Minor => "minor",
            VersionUpdateType.Patch => "patch",
            VersionUpdateType.None => "none",
            _ => "unknown"
        };
    }

    private static string SeverityClass(VulnerabilitySeverity severity)
    {
        return severity switch
        {
            VulnerabilitySeverity.Critical => "critical",
            VulnerabilitySeverity.High => "high",
            VulnerabilitySeverity.Medium => "medium",
            VulnerabilitySeverity.Low => "low",
            VulnerabilitySeverity.None => "none",
            _ => "unknown"
        };
    }

    private static string Encode(string? value)
    {
        return WebUtility.HtmlEncode(value ?? string.Empty);
    }
}
