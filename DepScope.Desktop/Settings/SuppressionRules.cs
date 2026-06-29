using DepScope.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DepScope.Desktop.Settings;

public static class SuppressionRules
{
    public static bool IsPackageUpdateSuppressed(
        IEnumerable<SuppressionRule> rules,
        ProjectInfo project,
        PackageRef package)
    {
        return rules.Any(rule =>
            rule.Type == SuppressionRuleType.PackageUpdate &&
            IsPackageMatch(rule, project, package));
    }

    public static bool IsAdvisorySuppressed(
        IEnumerable<SuppressionRule> rules,
        ProjectInfo project,
        PackageRef package,
        string advisoryId)
    {
        return rules.Any(rule =>
            rule.Type == SuppressionRuleType.Advisory &&
            IsPackageMatch(rule, project, package) &&
            string.Equals(rule.AdvisoryId, advisoryId, StringComparison.OrdinalIgnoreCase));
    }

    public static bool AreAllAdvisoriesSuppressed(
        IEnumerable<SuppressionRule> rules,
        ProjectInfo project,
        PackageRef package)
    {
        var advisoryIds = GetAdvisoryIds(package).ToList();
        return advisoryIds.Count > 0 &&
            advisoryIds.All(id => IsAdvisorySuppressed(rules, project, package, id));
    }

    public static IReadOnlyList<string> GetAdvisoryIds(PackageRef package)
    {
        if (package.VulnerabilityAdvisories.Count > 0)
        {
            return package.VulnerabilityAdvisories
                .Select(advisory => advisory.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (string.IsNullOrWhiteSpace(package.VulnerabilityIds))
            return Array.Empty<string>();

        return package.VulnerabilityIds
            .Split(',')
            .Select(id => id.Trim())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsPackageMatch(
        SuppressionRule rule,
        ProjectInfo project,
        PackageRef package)
    {
        return string.Equals(rule.ProjectPath, project.Path, StringComparison.OrdinalIgnoreCase) &&
            rule.Ecosystem == package.Ecosystem &&
            string.Equals(rule.PackageName, package.PackageName, StringComparison.OrdinalIgnoreCase);
    }
}
