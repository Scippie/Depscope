using DepScope.Core.Models;
using System;

namespace DepScope.Desktop.Settings;

public enum SuppressionRuleType
{
    PackageUpdate,
    Advisory
}

public sealed class SuppressionRule
{
    public SuppressionRuleType Type { get; set; }
    public string ProjectPath { get; set; } = string.Empty;
    public Ecosystem Ecosystem { get; set; }
    public string PackageName { get; set; } = string.Empty;
    public string AdvisoryId { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}
