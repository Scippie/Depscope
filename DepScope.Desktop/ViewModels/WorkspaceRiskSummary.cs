using DepScope.Core.Models;
using System.Collections.Generic;
using System.Linq;

namespace DepScope.Desktop.ViewModels;

public sealed class WorkspaceRiskSummary
{
    public static WorkspaceRiskSummary Empty { get; } = new();

    public int TotalProjects { get; init; }
    public int CleanProjects { get; init; }
    public int OutdatedProjects { get; init; }
    public int VulnerableProjects { get; init; }
    public int UnknownProjects { get; init; }
    public int TotalPackages { get; init; }
    public int OutdatedPackages { get; init; }
    public int VulnerablePackages { get; init; }
    public int LowSeverityPackages { get; init; }
    public int MediumSeverityPackages { get; init; }
    public int HighSeverityPackages { get; init; }
    public int CriticalSeverityPackages { get; init; }

    public static WorkspaceRiskSummary FromProjects(IEnumerable<ProjectInfo> projects)
    {
        var projectList = projects.ToList();
        var packageList = projectList.SelectMany(project => project.Packages).ToList();

        return new WorkspaceRiskSummary
        {
            TotalProjects = projectList.Count,
            CleanProjects = projectList.Count(IsCleanProject),
            OutdatedProjects = projectList.Count(project => !IsVulnerableProject(project) && IsOutdatedProject(project)),
            VulnerableProjects = projectList.Count(IsVulnerableProject),
            UnknownProjects = projectList.Count(project =>
                !IsVulnerableProject(project) &&
                !IsOutdatedProject(project) &&
                IsUnknownProject(project)),
            TotalPackages = packageList.Count,
            OutdatedPackages = packageList.Count(IsOutdatedPackage),
            VulnerablePackages = packageList.Count(IsVulnerablePackage),
            LowSeverityPackages = packageList.Count(package => IsVulnerablePackage(package) && package.VulnerabilitySeverity == VulnerabilitySeverity.Low),
            MediumSeverityPackages = packageList.Count(package => IsVulnerablePackage(package) && package.VulnerabilitySeverity == VulnerabilitySeverity.Medium),
            HighSeverityPackages = packageList.Count(package => IsVulnerablePackage(package) && package.VulnerabilitySeverity == VulnerabilitySeverity.High),
            CriticalSeverityPackages = packageList.Count(package => IsVulnerablePackage(package) && package.VulnerabilitySeverity == VulnerabilitySeverity.Critical)
        };
    }

    private static bool IsCleanProject(ProjectInfo project)
    {
        return project.Packages.Any() &&
            !IsVulnerableProject(project) &&
            !IsOutdatedProject(project) &&
            !IsUnknownProject(project);
    }

    private static bool IsVulnerableProject(ProjectInfo project)
    {
        return project.Packages.Any(IsVulnerablePackage);
    }

    private static bool IsOutdatedProject(ProjectInfo project)
    {
        return project.Packages.Any(IsOutdatedPackage);
    }

    private static bool IsUnknownProject(ProjectInfo project)
    {
        return !project.Packages.Any() || project.Packages.Any(IsUnknownPackage);
    }

    private static bool IsOutdatedPackage(PackageRef package)
    {
        return package.UpdateType is VersionUpdateType.Patch or
            VersionUpdateType.Minor or
            VersionUpdateType.Major;
    }

    private static bool IsVulnerablePackage(PackageRef package)
    {
        if (package.VulnerabilityCount <= 0)
            return false;

        return package.VulnerabilitySeverity is VulnerabilitySeverity.Low or
            VulnerabilitySeverity.Medium or
            VulnerabilitySeverity.High or
            VulnerabilitySeverity.Critical or
            VulnerabilitySeverity.Unknown;
    }

    private static bool IsUnknownPackage(PackageRef package)
    {
        return package.UpdateType == VersionUpdateType.Unknown ||
            package.VulnerabilitySeverity == VulnerabilitySeverity.NotChecked;
    }
}
