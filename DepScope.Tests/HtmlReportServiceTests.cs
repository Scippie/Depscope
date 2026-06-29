using DepScope.Core.Models;
using DepScope.Desktop.Services;
using DepScope.Desktop.ViewModels;
using Xunit;

namespace DepScope.Tests;

public sealed class HtmlReportServiceTests
{
    [Fact]
    public void GenerateReport_IncludesProjectPackagesAndAdvisoryDetails()
    {
        var group = new ProjectGroup("Workspace", "C:\\repos\\workspace");
        group.Projects.Add(new ProjectInfo
        {
            Name = "WebApp",
            Path = "C:\\repos\\workspace\\package.json",
            Ecosystem = Ecosystem.Npm,
            Packages =
            {
                new PackageRef
                {
                    Ecosystem = Ecosystem.Npm,
                    PackageName = "vite",
                    DeclaredVersion = "5.0.0",
                    InstalledVersion = "5.0.1",
                    LatestVersion = "5.2.0",
                    UpdateType = VersionUpdateType.Minor,
                    VulnerabilitySeverity = VulnerabilitySeverity.High,
                    VulnerabilityCount = 1,
                    VulnerabilityAdvisories =
                    {
                        new VulnerabilityAdvisory
                        {
                            Id = "GHSA-test",
                            Severity = VulnerabilitySeverity.High,
                            AffectedPackageName = "transitive-package",
                            AffectedVersion = "1.2.3",
                            Relationship = "vite > transitive-package",
                            Summary = "Example advisory.",
                            Url = "https://osv.dev/vulnerability/GHSA-test"
                        }
                    }
                }
            }
        });

        var html = HtmlReportService.GenerateReport(
            new[] { group },
            new DateTimeOffset(2026, 6, 29, 12, 30, 0, TimeSpan.Zero));

        Assert.Contains("DepScope Dependency Report", html);
        Assert.Contains("Workspace", html);
        Assert.Contains("WebApp", html);
        Assert.Contains("vite", html);
        Assert.Contains("Minor", html);
        Assert.Contains("High (1)", html);
        Assert.Contains("Affected: transitive-package@1.2.3", html);
        Assert.Contains("Via: vite &gt; transitive-package", html);
        Assert.Contains("https://osv.dev/vulnerability/GHSA-test", html);
    }

    [Fact]
    public void GenerateReport_EscapesUntrustedText()
    {
        var group = new ProjectGroup("<script>alert(1)</script>", "C:\\repos\\unsafe");
        group.Projects.Add(new ProjectInfo
        {
            Name = "Project <unsafe>",
            Path = "C:\\repos\\unsafe\\project.csproj",
            Ecosystem = Ecosystem.DotNet,
            Packages =
            {
                new PackageRef
                {
                    Ecosystem = Ecosystem.DotNet,
                    PackageName = "Package <unsafe>",
                    DeclaredVersion = "1.0.0",
                    VulnerabilityIds = "GHSA-<unsafe>"
                }
            }
        });

        var html = HtmlReportService.GenerateReport(
            new[] { group },
            DateTimeOffset.UnixEpoch);

        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.Contains("Package &lt;unsafe&gt;", html);
        Assert.Contains("GHSA-&lt;unsafe&gt;", html);
    }
}
