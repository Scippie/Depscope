using DepScope.Core.Models;
using DepScope.Desktop.Settings;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DepScope.Desktop.ViewModels;

public sealed class UpdateRecommendation
{
    public string ProjectName { get; init; } = string.Empty;
    public Ecosystem Ecosystem { get; init; }
    public string PackageName { get; init; } = string.Empty;
    public string DeclaredVersion { get; init; } = string.Empty;
    public string InstalledVersion { get; init; } = string.Empty;
    public string LatestVersion { get; init; } = string.Empty;
    public VersionUpdateType UpdateType { get; init; } = VersionUpdateType.Unknown;
    public VulnerabilitySeverity VulnerabilitySeverity { get; init; } = VulnerabilitySeverity.NotChecked;
    public string VulnerabilityStatus { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public int Priority { get; init; }

    public string PriorityLabel => Priority switch
    {
        <= 20 => "Urgent",
        <= 45 => "Security",
        <= 70 => "Update",
        _ => "Review"
    };

    public string VersionSummary
    {
        get
        {
            var installed = string.IsNullOrWhiteSpace(InstalledVersion)
                ? DeclaredVersion
                : InstalledVersion;

            if (string.IsNullOrWhiteSpace(LatestVersion))
                return string.IsNullOrWhiteSpace(installed) ? string.Empty : installed;

            return string.IsNullOrWhiteSpace(installed)
                ? LatestVersion
                : $"{installed} -> {LatestVersion}";
        }
    }

    public static IReadOnlyList<UpdateRecommendation> FromProjects(
        IEnumerable<ProjectInfo> projects,
        IReadOnlyCollection<SuppressionRule>? suppressionRules = null,
        int maxItems = 5)
    {
        var rules = suppressionRules ?? Array.Empty<SuppressionRule>();
        return projects
            .SelectMany(project => CreateForProject(project, rules))
            .OrderBy(recommendation => recommendation.Priority)
            .ThenBy(recommendation => recommendation.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(recommendation => recommendation.PackageName, StringComparer.OrdinalIgnoreCase)
            .Take(maxItems)
            .ToList();
    }

    private static IEnumerable<UpdateRecommendation> CreateForProject(
        ProjectInfo project,
        IReadOnlyCollection<SuppressionRule> suppressionRules)
    {
        foreach (var package in project.Packages)
        {
            var isUpdateSuppressed = SuppressionRules.IsPackageUpdateSuppressed(
                suppressionRules,
                project,
                package);
            var areAdvisoriesSuppressed = SuppressionRules.AreAllAdvisoriesSuppressed(
                suppressionRules,
                project,
                package);

            var priority = GetPriority(package, isUpdateSuppressed, areAdvisoriesSuppressed);
            if (priority is null)
                continue;

            yield return new UpdateRecommendation
            {
                ProjectName = project.Name,
                Ecosystem = project.Ecosystem,
                PackageName = package.PackageName,
                DeclaredVersion = package.DeclaredVersion,
                InstalledVersion = package.InstalledVersion ?? string.Empty,
                LatestVersion = package.LatestVersion ?? string.Empty,
                UpdateType = package.UpdateType,
                VulnerabilitySeverity = package.VulnerabilitySeverity,
                VulnerabilityStatus = package.VulnerabilityStatus,
                Reason = CreateReason(package, isUpdateSuppressed, areAdvisoriesSuppressed),
                Priority = priority.Value
            };
        }
    }

    private static int? GetPriority(
        PackageRef package,
        bool isUpdateSuppressed,
        bool areAdvisoriesSuppressed)
    {
        if (IsVulnerable(package) && !areAdvisoriesSuppressed)
        {
            return package.VulnerabilitySeverity switch
            {
                VulnerabilitySeverity.Critical => 10,
                VulnerabilitySeverity.High => 20,
                VulnerabilitySeverity.Medium => 30,
                VulnerabilitySeverity.Low => 40,
                _ => 45
            };
        }

        if (isUpdateSuppressed)
            return null;

        return package.UpdateType switch
        {
            VersionUpdateType.Major => 50,
            VersionUpdateType.Minor => 60,
            VersionUpdateType.Patch => 70,
            VersionUpdateType.Unknown when IsUnknown(package) => 80,
            _ => null
        };
    }

    private static string CreateReason(
        PackageRef package,
        bool isUpdateSuppressed,
        bool areAdvisoriesSuppressed)
    {
        if (IsVulnerable(package) && !areAdvisoriesSuppressed)
        {
            var updateSuffix = IsOutdated(package) && !isUpdateSuppressed
                ? " and an update is available"
                : string.Empty;
            return $"{package.VulnerabilitySeverity} vulnerability{updateSuffix}";
        }

        if (package.UpdateType is VersionUpdateType.Major or
            VersionUpdateType.Minor or
            VersionUpdateType.Patch)
        {
            return $"{package.UpdateType} update available";
        }

        return "Latest version or vulnerability check is unresolved";
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

    private static bool IsOutdated(PackageRef package)
    {
        return package.UpdateType is VersionUpdateType.Major or
            VersionUpdateType.Minor or
            VersionUpdateType.Patch;
    }

    private static bool IsUnknown(PackageRef package)
    {
        return package.UpdateType == VersionUpdateType.Unknown ||
            package.VulnerabilitySeverity == VulnerabilitySeverity.NotChecked;
    }
}
