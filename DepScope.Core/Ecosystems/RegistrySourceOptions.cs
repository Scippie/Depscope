namespace DepScope.Core.Ecosystems;

public sealed class RegistrySourceOptions
{
    public string? NuGetSourceUrl { get; init; }
    public string? NpmRegistryBaseUrl { get; init; }
    public string? GoProxyBaseUrl { get; init; }
    public string? PythonPackageIndexBaseUrl { get; init; }
    public string? PackagistMetadataBaseUrl { get; init; }
    public string? MavenSearchBaseUrl { get; init; }
    public string? CratesApiBaseUrl { get; init; }
    public string? GitHubApiBaseUrl { get; init; }
}
