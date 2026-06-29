using DepScope.Core.Models;
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
    public IReadOnlyList<PackageAdvisoryDetail> Advisories { get; init; } = new List<PackageAdvisoryDetail>();
    public bool HasAdvisories => Advisories.Count > 0;

    public static PackageDetail? FromPackage(PackageRef? package)
    {
        if (package is null)
            return null;

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
            Advisories = CreateAdvisories(package)
        };
    }

    private static IReadOnlyList<PackageAdvisoryDetail> CreateAdvisories(PackageRef package)
    {
        if (package.VulnerabilityAdvisories.Count > 0)
        {
            return package.VulnerabilityAdvisories
                .Select(PackageAdvisoryDetail.FromAdvisory)
                .ToList();
        }

        if (string.IsNullOrWhiteSpace(package.VulnerabilityIds))
            return new List<PackageAdvisoryDetail>();

        return package.VulnerabilityIds
            .Split(',')
            .Select(id => id.Trim())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => new PackageAdvisoryDetail { Id = id })
            .ToList();
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

    public static PackageAdvisoryDetail FromAdvisory(VulnerabilityAdvisory advisory)
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
            Url = Display(advisory.Url)
        };
    }

    private static string Display(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value;
    }
}
