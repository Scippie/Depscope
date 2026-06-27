using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DepScope.Core.Models
{
    public enum Ecosystem
    {
        DotNet,
        Npm,
        Python,
        Java,
        Php,
        Go,
        Rust,
        GitHubActions
    }

    public enum VersionUpdateType
    {
        Major,
        Minor,
        Patch,
        None,
        Unknown
    }

    public enum VulnerabilitySeverity
    {
        NotChecked,
        NotApplicable,
        None,
        Low,
        Medium,
        High,
        Critical,
        Unknown
    }

    public sealed class PackageRef
    {
        public Ecosystem Ecosystem { get; init; }
        public string PackageName { get; init; } = string.Empty;
        public string DeclaredVersion { get; init; } = string.Empty;
        public string? InstalledVersion { get; set; } 
        public string? LatestVersion { get; set; }
        public List<RelatedSecurityPackage> RelatedSecurityPackages { get; init; } = new();
        public VersionUpdateType UpdateType { get; set; } = VersionUpdateType.Unknown;
        public int VulnerabilityCount { get; set; }
        public VulnerabilitySeverity VulnerabilitySeverity { get; set; } = VulnerabilitySeverity.NotChecked;
        public string? VulnerabilityIds { get; set; }
        public List<VulnerabilityAdvisory> VulnerabilityAdvisories { get; init; } = new();

        public string VulnerabilityStatus
        {
            get
            {
                if (VulnerabilitySeverity == VulnerabilitySeverity.NotChecked)
                    return "Not checked";

                if (VulnerabilitySeverity == VulnerabilitySeverity.NotApplicable)
                    return "N/A";

                if (VulnerabilitySeverity == VulnerabilitySeverity.None)
                    return "None";

                if (VulnerabilityCount > 0)
                    return $"{VulnerabilitySeverity} ({VulnerabilityCount})";

                return VulnerabilitySeverity.ToString();
            }
        }
    }

    public sealed class RelatedSecurityPackage
    {
        public Ecosystem Ecosystem { get; init; }
        public string PackageName { get; init; } = string.Empty;
        public string Version { get; init; } = string.Empty;
        public string Relationship { get; init; } = string.Empty;
    }

    public sealed class VulnerabilityAdvisory
    {
        public string Source { get; init; } = "OSV";
        public string Id { get; init; } = string.Empty;
        public List<string> Aliases { get; init; } = new();
        public VulnerabilitySeverity Severity { get; init; } = VulnerabilitySeverity.Unknown;
        public string AffectedPackageName { get; init; } = string.Empty;
        public string AffectedVersion { get; init; } = string.Empty;
        public string? Relationship { get; init; }
        public string? Summary { get; init; }
        public string? Url { get; init; }

        public string DisplayText
        {
            get
            {
                var target = $"{AffectedPackageName}@{AffectedVersion}";
                var aliases = Aliases.Count > 0
                    ? $" | Aliases: {string.Join(", ", Aliases)}"
                    : string.Empty;
                var relationship = string.IsNullOrWhiteSpace(Relationship)
                    ? string.Empty
                    : $" | Via: {Relationship}";
                var summary = string.IsNullOrWhiteSpace(Summary)
                    ? string.Empty
                    : $" | Summary: {Summary}";
                var url = string.IsNullOrWhiteSpace(Url)
                    ? string.Empty
                    : $" | Link: {Url}";

                return $"{Id} [{Severity}] | Source: {Source} | Affected: {target}{relationship}{aliases}{summary}{url}";
            }
        }
    }

    public sealed class ProjectInfo
    {
        public string Name { get; init; } = string.Empty;
        public string Path { get; init; } = string.Empty;
        public Ecosystem Ecosystem { get; init; }
        public List<PackageRef> Packages { get; init; } = new();
    }
}
