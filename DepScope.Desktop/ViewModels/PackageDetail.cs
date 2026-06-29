using DepScope.Core.Models;
using DepScope.Desktop.Settings;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DepScope.Desktop.ViewModels;

public sealed class PackageDetail
{
    public string PackageName { get; init; } = string.Empty;
    public Ecosystem Ecosystem { get; init; }
    public string DeclaredVersion { get; init; } = "-";
    public string InstalledVersion { get; init; } = "-";
    public string LatestVersion { get; init; } = "-";
    public VersionUpdateType UpdateType { get; init; } = VersionUpdateType.Unknown;
    public string VulnerabilityStatus { get; init; } = string.Empty;
    public int RelatedSecurityPackageCount { get; init; }
    public bool IsUpdateSuppressed { get; init; }
    public bool HasSuppressedAdvisories => Advisories.Any(advisory => advisory.IsSuppressed);
    public bool HasActiveAdvisories => Advisories.Any(advisory => !advisory.IsSuppressed);
    public bool HasAnySuppression => IsUpdateSuppressed || HasSuppressedAdvisories;
    public bool CanSuppressUpdate =>
        (UpdateType is VersionUpdateType.Patch or VersionUpdateType.Minor or VersionUpdateType.Major) &&
        !IsUpdateSuppressed;
    public bool CanClearUpdateSuppression => IsUpdateSuppressed;
    public bool CanSuppressAdvisories => HasActiveAdvisories;
    public bool CanClearAdvisorySuppressions => HasSuppressedAdvisories;
    public string SuppressionStatus
    {
        get
        {
            if (IsUpdateSuppressed && HasSuppressedAdvisories)
                return "Update and advisories accepted";

            if (IsUpdateSuppressed)
                return "Update accepted";

            if (HasSuppressedAdvisories)
                return "Advisories accepted";

            return "None";
        }
    }

    public IReadOnlyList<PackageAdvisoryDetail> Advisories { get; init; } = new List<PackageAdvisoryDetail>();
    public bool HasAdvisories => Advisories.Count > 0;

    public static PackageDetail? FromPackage(
        ProjectInfo? project,
        PackageRef? package,
        IReadOnlyCollection<SuppressionRule>? suppressionRules = null)
    {
        if (package is null)
            return null;

        var rules = suppressionRules ?? Array.Empty<SuppressionRule>();
        return new PackageDetail
        {
            PackageName = package.PackageName,
            Ecosystem = package.Ecosystem,
            DeclaredVersion = Display(package.DeclaredVersion),
            InstalledVersion = Display(package.InstalledVersion),
            LatestVersion = Display(package.LatestVersion),
            UpdateType = package.UpdateType,
            VulnerabilityStatus = package.VulnerabilityStatus,
            RelatedSecurityPackageCount = package.RelatedSecurityPackages.Count,
            IsUpdateSuppressed = project is not null &&
                SuppressionRules.IsPackageUpdateSuppressed(rules, project, package),
            Advisories = CreateAdvisories(project, package, rules)
        };
    }

    private static IReadOnlyList<PackageAdvisoryDetail> CreateAdvisories(
        ProjectInfo? project,
        PackageRef package,
        IReadOnlyCollection<SuppressionRule> suppressionRules)
    {
        if (package.VulnerabilityAdvisories.Count > 0)
        {
            return package.VulnerabilityAdvisories
                .Select(advisory => PackageAdvisoryDetail.FromAdvisory(
                    advisory,
                    IsAdvisorySuppressed(project, package, suppressionRules, advisory.Id)))
                .ToList();
        }

        if (string.IsNullOrWhiteSpace(package.VulnerabilityIds))
            return new List<PackageAdvisoryDetail>();

        return package.VulnerabilityIds
            .Split(',')
            .Select(id => id.Trim())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => new PackageAdvisoryDetail
            {
                Id = id,
                IsSuppressed = IsAdvisorySuppressed(project, package, suppressionRules, id)
            })
            .ToList();
    }

    private static bool IsAdvisorySuppressed(
        ProjectInfo? project,
        PackageRef package,
        IReadOnlyCollection<SuppressionRule> suppressionRules,
        string advisoryId)
    {
        return project is not null &&
            SuppressionRules.IsAdvisorySuppressed(suppressionRules, project, package, advisoryId);
    }

    private static string Display(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value;
    }
}

public sealed class PackageAdvisoryDetail
{
    public string Id { get; init; } = string.Empty;
    public string Source { get; init; } = "-";
    public string Severity { get; init; } = "-";
    public string AffectedPackage { get; init; } = "-";
    public string AffectedVersion { get; init; } = "-";
    public string Relationship { get; init; } = "-";
    public string Aliases { get; init; } = "-";
    public string Summary { get; init; } = "-";
    public string Url { get; init; } = "-";
    public bool IsSuppressed { get; init; }
    public string Status => IsSuppressed ? "Accepted" : "Active";

    public static PackageAdvisoryDetail FromAdvisory(
        VulnerabilityAdvisory advisory,
        bool isSuppressed = false)
    {
        return new PackageAdvisoryDetail
        {
            Id = Display(advisory.Id),
            Source = Display(advisory.Source),
            Severity = advisory.Severity.ToString(),
            AffectedPackage = Display(advisory.AffectedPackageName),
            AffectedVersion = Display(advisory.AffectedVersion),
            Relationship = Display(advisory.Relationship),
            Aliases = advisory.Aliases.Count == 0
                ? "-"
                : string.Join(", ", advisory.Aliases),
            Summary = Display(advisory.Summary),
            Url = Display(advisory.Url),
            IsSuppressed = isSuppressed
        };
    }

    private static string Display(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value;
    }
}
