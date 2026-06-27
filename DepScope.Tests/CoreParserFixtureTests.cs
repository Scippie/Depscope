using DepScope.Core.Ecosystems;
using DepScope.Core.Models;
using Xunit;

namespace DepScope.Tests;

public sealed class CoreParserFixtureTests : IDisposable
{
    private readonly string _rootPath = CreateTempDirectory();

    [Fact]
    public async Task GoParser_SkipsMalformedSingleLineRequire()
    {
        var projectDir = Directory.CreateDirectory(Path.Combine(_rootPath, "go-app"));
        await File.WriteAllTextAsync(
            Path.Combine(projectDir.FullName, "go.mod"),
            """
            module example.com/app

            require github.com/example/transitive v0.1.0 // indirect
            require github.com/example/missing-version
            require github.com/example/valid v1.2.3
            """,
            TestContext.Current.CancellationToken);

        var projects = await new GoEcosystemHandler().ScanProjectsAsync(
            _rootPath,
            TestContext.Current.CancellationToken);

        var package = Assert.Single(Assert.Single(projects).Packages);
        Assert.Equal("github.com/example/valid", package.PackageName);
        Assert.Equal("v1.2.3", package.DeclaredVersion);
    }

    [Fact]
    public async Task DotNetParser_SkipsMalformedProjectAndKeepsValidProject()
    {
        var validProject = Directory.CreateDirectory(Path.Combine(_rootPath, "valid"));
        await File.WriteAllTextAsync(
            Path.Combine(validProject.FullName, "Valid.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
              </ItemGroup>
            </Project>
            """,
            TestContext.Current.CancellationToken);

        var invalidProject = Directory.CreateDirectory(Path.Combine(_rootPath, "invalid"));
        await File.WriteAllTextAsync(
            Path.Combine(invalidProject.FullName, "Invalid.csproj"),
            """
            <Project>
              <ItemGroup>
            """,
            TestContext.Current.CancellationToken);

        var projects = await new DotNetEcosystemHandler().ScanProjectsAsync(
            _rootPath,
            TestContext.Current.CancellationToken);

        var project = Assert.Single(projects);
        Assert.Equal("Valid", project.Name);

        var package = Assert.Single(project.Packages);
        Assert.Equal(Ecosystem.DotNet, package.Ecosystem);
        Assert.Equal("Newtonsoft.Json", package.PackageName);
        Assert.Equal("13.0.3", package.DeclaredVersion);
    }

    [Fact]
    public async Task DotNetParser_UsesProjectAssetsForInstalledVersionsAndRelatedSecurityPackages()
    {
        var projectDir = Directory.CreateDirectory(Path.Combine(_rootPath, "nuget-app"));
        await File.WriteAllTextAsync(
            Path.Combine(projectDir.FullName, "NuGetApp.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Direct.Package" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """,
            TestContext.Current.CancellationToken);

        var objDir = Directory.CreateDirectory(Path.Combine(projectDir.FullName, "obj"));
        await File.WriteAllTextAsync(
            Path.Combine(objDir.FullName, "project.assets.json"),
            """
            {
              "targets": {
                "net8.0": {
                  "Direct.Package/1.0.1": {
                    "type": "package",
                    "dependencies": {
                      "Transitive.Package": "2.0.0"
                    }
                  },
                  "Transitive.Package/2.0.0": {
                    "type": "package"
                  }
                }
              },
              "libraries": {
                "Direct.Package/1.0.1": {
                  "type": "package"
                },
                "Transitive.Package/2.0.0": {
                  "type": "package"
                }
              }
            }
            """,
            TestContext.Current.CancellationToken);

        var projects = await new DotNetEcosystemHandler().ScanProjectsAsync(
            _rootPath,
            TestContext.Current.CancellationToken);

        var package = Assert.Single(Assert.Single(projects).Packages);

        Assert.Equal("Direct.Package", package.PackageName);
        Assert.Equal("1.0.0", package.DeclaredVersion);
        Assert.Equal("1.0.1", package.InstalledVersion);

        var related = Assert.Single(package.RelatedSecurityPackages);
        Assert.Equal(Ecosystem.DotNet, related.Ecosystem);
        Assert.Equal("Transitive.Package", related.PackageName);
        Assert.Equal("2.0.0", related.Version);
        Assert.Equal("Direct.Package > Transitive.Package", related.Relationship);
    }

    [Fact]
    public async Task GitHubActionsParser_ReadsExternalActionAndReusableWorkflowReferences()
    {
        var workflowsDir = Directory.CreateDirectory(Path.Combine(_rootPath, ".github", "workflows"));
        await File.WriteAllTextAsync(
            Path.Combine(workflowsDir.FullName, "ci.yml"),
            """
            name: CI

            jobs:
              build:
                uses: org/reusable/.github/workflows/build.yml@v1
              test:
                runs-on: ubuntu-latest
                steps:
                  - uses: actions/checkout@v4
                  - uses: "azure/login@v2"
                  - uses: docker/login-action@6f5f8d8d1f5f7c4f0f9f2e0d9c8b7a6f5e4d3c2b
                  - uses: ./.github/actions/local-action
                  - run: dotnet test
            """,
            TestContext.Current.CancellationToken);

        var projects = await new GitHubActionsEcosystemHandler().ScanProjectsAsync(
            _rootPath,
            TestContext.Current.CancellationToken);

        var project = Assert.Single(projects);
        Assert.Equal("CI", project.Name);
        Assert.Equal(Ecosystem.GitHubActions, project.Ecosystem);

        Assert.Collection(
            project.Packages,
            first =>
            {
                Assert.Equal("org/reusable/.github/workflows/build.yml", first.PackageName);
                Assert.Equal("v1", first.DeclaredVersion);
            },
            second =>
            {
                Assert.Equal("actions/checkout", second.PackageName);
                Assert.Equal("v4", second.DeclaredVersion);
                Assert.Equal("Tag/branch ref", second.InstalledVersion);
            },
            third =>
            {
                Assert.Equal("azure/login", third.PackageName);
                Assert.Equal("v2", third.DeclaredVersion);
                Assert.Equal("Tag/branch ref", third.InstalledVersion);
            },
            fourth =>
            {
                Assert.Equal("docker/login-action", fourth.PackageName);
                Assert.Equal("6f5f8d8d1f5f7c4f0f9f2e0d9c8b7a6f5e4d3c2b", fourth.DeclaredVersion);
                Assert.Equal("SHA pinned", fourth.InstalledVersion);
            });
    }

    [Fact]
    public void GitHubActionsPinningStatus_ClassifiesCommonReferenceTypes()
    {
        Assert.Equal(
            "SHA pinned",
            GitHubActionsEcosystemHandler.GetReferencePinningStatus("6f5f8d8d1f5f7c4f0f9f2e0d9c8b7a6f5e4d3c2b"));

        Assert.Equal(
            "Short SHA ref",
            GitHubActionsEcosystemHandler.GetReferencePinningStatus("6f5f8d8"));

        Assert.Equal(
            "Tag/branch ref",
            GitHubActionsEcosystemHandler.GetReferencePinningStatus("v4"));

        Assert.Equal(
            "Dynamic ref",
            GitHubActionsEcosystemHandler.GetReferencePinningStatus("${{ inputs.checkout-ref }}"));
    }

    [Fact]
    public void GitHubActionsVersionClassifier_DoesNotWarnForSameMajorFloatingTag()
    {
        Assert.Equal(
            VersionUpdateType.None,
            GitHubActionsEcosystemHandler.ClassifyVersionTagUpdate("v4", "v4.2.2"));

        Assert.Equal(
            VersionUpdateType.Major,
            GitHubActionsEcosystemHandler.ClassifyVersionTagUpdate("v4", "v5.0.0"));

        Assert.Equal(
            VersionUpdateType.Patch,
            GitHubActionsEcosystemHandler.ClassifyVersionTagUpdate("v4.2.1", "v4.2.2"));
    }

    [Fact]
    public void GitHubActionsApiBaseUrl_NormalizesCommonGitHubWebUrl()
    {
        Assert.Equal(
            "https://api.github.com/",
            GitHubActionsEcosystemHandler.NormalizeGitHubApiBaseUrl("https://github.com"));

        Assert.Equal(
            "https://github.example.test/api/v3/",
            GitHubActionsEcosystemHandler.NormalizeGitHubApiBaseUrl("https://github.example.test/api/v3"));
    }

    [Fact]
    public void GitHubActionsWebFallback_ExtractsLatestReleaseRedirectTag()
    {
        Assert.Equal(
            "v4.2.2",
            GitHubActionsEcosystemHandler.ExtractLatestTagFromReleaseRedirect(
                "https://github.com/actions/checkout/releases/tag/v4.2.2"));
    }

    [Fact]
    public void GitHubActionsWebFallback_SelectsHighestVersionTagFromHtml()
    {
        var html = """
            <a href="/actions/checkout/releases/tag/v3.6.0">v3.6.0</a>
            <a href="/actions/checkout/releases/tag/v4.2.1">v4.2.1</a>
            <a href="/actions/checkout/releases/tag/v4.2.2">v4.2.2</a>
            """;

        Assert.Equal(
            "v4.2.2",
            GitHubActionsEcosystemHandler.ExtractLatestTagFromHtml(html));
    }

    [Fact]
    public async Task NpmParser_UsesYarnLockForInstalledVersions()
    {
        var projectDir = Directory.CreateDirectory(Path.Combine(_rootPath, "npm-app"));
        await File.WriteAllTextAsync(
            Path.Combine(projectDir.FullName, "package.json"),
            """
            {
              "dependencies": {
                "lodash": "^4.17.0"
              },
              "devDependencies": {
                "@scope/tool": "^2.0.0"
              }
            }
            """,
            TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(
            Path.Combine(projectDir.FullName, "yarn.lock"),
            """
            # yarn lockfile v1

            lodash@^4.17.0:
              version "4.17.21"

            "@scope/tool@^2.0.0":
              version "2.3.4"
            """,
            TestContext.Current.CancellationToken);

        var projects = await new NpmEcosystemHandler().ScanProjectsAsync(
            _rootPath,
            TestContext.Current.CancellationToken);

        var packages = Assert.Single(projects).Packages;
        var lodash = Assert.Single(packages, p => p.PackageName == "lodash");
        var scopedTool = Assert.Single(packages, p => p.PackageName == "@scope/tool");

        Assert.Equal("^4.17.0", lodash.DeclaredVersion);
        Assert.Equal("4.17.21", lodash.InstalledVersion);
        Assert.Equal("^2.0.0", scopedTool.DeclaredVersion);
        Assert.Equal("2.3.4", scopedTool.InstalledVersion);
    }

    [Fact]
    public async Task NpmParser_UsesPnpmLockForInstalledVersions()
    {
        var projectDir = Directory.CreateDirectory(Path.Combine(_rootPath, "pnpm-app"));
        await File.WriteAllTextAsync(
            Path.Combine(projectDir.FullName, "package.json"),
            """
            {
              "dependencies": {
                "lodash": "^4.17.0"
              },
              "devDependencies": {
                "@scope/tool": "^2.0.0"
              }
            }
            """,
            TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(
            Path.Combine(projectDir.FullName, "pnpm-lock.yaml"),
            """
            lockfileVersion: '9.0'

            importers:
              .:
                dependencies:
                  lodash:
                    specifier: ^4.17.0
                    version: 4.17.21
                devDependencies:
                  '@scope/tool':
                    specifier: ^2.0.0
                    version: 2.3.4
            """,
            TestContext.Current.CancellationToken);

        var projects = await new NpmEcosystemHandler().ScanProjectsAsync(
            _rootPath,
            TestContext.Current.CancellationToken);

        var packages = Assert.Single(projects).Packages;
        var lodash = Assert.Single(packages, p => p.PackageName == "lodash");
        var scopedTool = Assert.Single(packages, p => p.PackageName == "@scope/tool");

        Assert.Equal("^4.17.0", lodash.DeclaredVersion);
        Assert.Equal("4.17.21", lodash.InstalledVersion);
        Assert.Equal("^2.0.0", scopedTool.DeclaredVersion);
        Assert.Equal("2.3.4", scopedTool.InstalledVersion);
    }

    [Fact]
    public async Task NpmParser_AttachesPackageLockTransitivesToDirectDependenciesForSecurityMonitoring()
    {
        var projectDir = Directory.CreateDirectory(Path.Combine(_rootPath, "npm-transitive-app"));
        await File.WriteAllTextAsync(
            Path.Combine(projectDir.FullName, "package.json"),
            """
            {
              "dependencies": {
                "direct-package": "^1.0.0"
              }
            }
            """,
            TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(
            Path.Combine(projectDir.FullName, "package-lock.json"),
            """
            {
              "lockfileVersion": 3,
              "packages": {
                "": {
                  "dependencies": {
                    "direct-package": "^1.0.0"
                  }
                },
                "node_modules/direct-package": {
                  "version": "1.2.3",
                  "dependencies": {
                    "transitive-package": "^4.0.0"
                  }
                },
                "node_modules/transitive-package": {
                  "version": "4.5.6"
                }
              }
            }
            """,
            TestContext.Current.CancellationToken);

        var projects = await new NpmEcosystemHandler().ScanProjectsAsync(
            _rootPath,
            TestContext.Current.CancellationToken);

        var direct = Assert.Single(Assert.Single(projects).Packages);

        Assert.Equal("direct-package", direct.PackageName);
        Assert.Equal("1.2.3", direct.InstalledVersion);

        var related = Assert.Single(direct.RelatedSecurityPackages);
        Assert.Equal(Ecosystem.Npm, related.Ecosystem);
        Assert.Equal("transitive-package", related.PackageName);
        Assert.Equal("4.5.6", related.Version);
        Assert.Equal("direct-package > transitive-package", related.Relationship);
    }

    [Fact]
    public async Task PhpParser_UsesComposerLockForInstalledVersions()
    {
        var projectDir = Directory.CreateDirectory(Path.Combine(_rootPath, "php-app"));
        await File.WriteAllTextAsync(
            Path.Combine(projectDir.FullName, "composer.json"),
            """
            {
              "require": {
                "monolog/monolog": "^2.0"
              },
              "require-dev": {
                "phpunit/phpunit": "^10.0"
              }
            }
            """,
            TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(
            Path.Combine(projectDir.FullName, "composer.lock"),
            """
            {
              "packages": [
                {
                  "name": "monolog/monolog",
                  "version": "2.9.3",
                  "require": {
                    "php": ">=7.2",
                    "psr/log": "^1.0 || ^2.0 || ^3.0"
                  }
                },
                {
                  "name": "psr/log",
                  "version": "3.0.0"
                }
              ],
              "packages-dev": [
                {
                  "name": "phpunit/phpunit",
                  "version": "10.5.0"
                }
              ]
            }
            """,
            TestContext.Current.CancellationToken);

        var projects = await new PhpEcosystemHandler().ScanProjectsAsync(
            _rootPath,
            TestContext.Current.CancellationToken);

        var packages = Assert.Single(projects).Packages;
        Assert.Equal(2, packages.Count);
        var monolog = Assert.Single(packages, p => p.PackageName == "monolog/monolog");
        var phpunit = Assert.Single(packages, p => p.PackageName == "phpunit/phpunit");

        Assert.Equal("^2.0", monolog.DeclaredVersion);
        Assert.Equal("2.9.3", monolog.InstalledVersion);
        Assert.Equal("^10.0", phpunit.DeclaredVersion);
        Assert.Equal("10.5.0", phpunit.InstalledVersion);

        var related = Assert.Single(monolog.RelatedSecurityPackages);
        Assert.Equal(Ecosystem.Php, related.Ecosystem);
        Assert.Equal("psr/log", related.PackageName);
        Assert.Equal("3.0.0", related.Version);
        Assert.Equal("monolog/monolog > psr/log", related.Relationship);
    }

    [Fact]
    public async Task RustParser_UsesCargoLockForInstalledVersions()
    {
        var projectDir = Directory.CreateDirectory(Path.Combine(_rootPath, "rust-app"));
        await File.WriteAllTextAsync(
            Path.Combine(projectDir.FullName, "Cargo.toml"),
            """
            [package]
            name = "rust-app"
            version = "0.1.0"

            [dependencies]
            serde = "1"
            tokio = { version = "1.35", features = ["rt"] }

            [build-dependencies]
            cc = "1"
            """,
            TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(
            Path.Combine(projectDir.FullName, "Cargo.lock"),
            """
            [[package]]
            name = "serde"
            version = "1.0.203"

            [[package]]
            name = "tokio"
            version = "1.38.0"
            dependencies = [
             "bytes",
            ]

            [[package]]
            name = "bytes"
            version = "1.6.0"

            [[package]]
            name = "cc"
            version = "1.0.99"
            """,
            TestContext.Current.CancellationToken);

        var projects = await new RustEcosystemHandler().ScanProjectsAsync(
            _rootPath,
            TestContext.Current.CancellationToken);

        var packages = Assert.Single(projects).Packages;
        Assert.Equal(3, packages.Count);
        var serde = Assert.Single(packages, p => p.PackageName == "serde");
        var tokio = Assert.Single(packages, p => p.PackageName == "tokio");
        var cc = Assert.Single(packages, p => p.PackageName == "cc");

        Assert.Equal("1", serde.DeclaredVersion);
        Assert.Equal("1.0.203", serde.InstalledVersion);
        Assert.Equal("1.35", tokio.DeclaredVersion);
        Assert.Equal("1.38.0", tokio.InstalledVersion);
        Assert.Equal("1", cc.DeclaredVersion);
        Assert.Equal("1.0.99", cc.InstalledVersion);

        var related = Assert.Single(tokio.RelatedSecurityPackages);
        Assert.Equal(Ecosystem.Rust, related.Ecosystem);
        Assert.Equal("bytes", related.PackageName);
        Assert.Equal("1.6.0", related.Version);
        Assert.Equal("tokio > bytes", related.Relationship);
    }

    [Fact]
    public async Task JavaParser_UsesGradleLockfileForInstalledVersions()
    {
        var projectDir = Directory.CreateDirectory(Path.Combine(_rootPath, "java-app"));
        await File.WriteAllTextAsync(
            Path.Combine(projectDir.FullName, "build.gradle"),
            """
            dependencies {
                implementation "org.slf4j:slf4j-api:2.+"
                testImplementation "org.junit.jupiter:junit-jupiter:5.+"
            }
            """,
            TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(
            Path.Combine(projectDir.FullName, "gradle.lockfile"),
            """
            # This is a Gradle dependency lockfile.
            org.slf4j:slf4j-api:2.0.13=compileClasspath,runtimeClasspath
            org.junit.jupiter:junit-jupiter:5.10.2=testCompileClasspath,testRuntimeClasspath
            empty=annotationProcessor
            """,
            TestContext.Current.CancellationToken);

        var projects = await new JavaEcosystemHandler().ScanProjectsAsync(
            _rootPath,
            TestContext.Current.CancellationToken);

        var packages = Assert.Single(projects).Packages;
        var slf4j = Assert.Single(packages, p => p.PackageName == "org.slf4j:slf4j-api");
        var junit = Assert.Single(packages, p => p.PackageName == "org.junit.jupiter:junit-jupiter");

        Assert.Equal("2.+", slf4j.DeclaredVersion);
        Assert.Equal("2.0.13", slf4j.InstalledVersion);
        Assert.Equal("5.+", junit.DeclaredVersion);
        Assert.Equal("5.10.2", junit.InstalledVersion);
    }

    [Fact]
    public async Task PythonParser_UsesPipfileLockForInstalledVersions()
    {
        var projectDir = Directory.CreateDirectory(Path.Combine(_rootPath, "python-app"));
        await File.WriteAllTextAsync(
            Path.Combine(projectDir.FullName, "Pipfile"),
            """
            [packages]
            requests = ">=2"

            [dev-packages]
            pytest = "*"
            """,
            TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(
            Path.Combine(projectDir.FullName, "Pipfile.lock"),
            """
            {
              "default": {
                "requests": {
                  "version": "==2.32.3"
                }
              },
              "develop": {
                "pytest": {
                  "version": "==8.2.2"
                }
              }
            }
            """,
            TestContext.Current.CancellationToken);

        var projects = await new PythonEcosystemHandler().ScanProjectsAsync(
            _rootPath,
            TestContext.Current.CancellationToken);

        var packages = Assert.Single(projects).Packages;
        var requests = Assert.Single(packages, p => p.PackageName == "requests");
        var pytest = Assert.Single(packages, p => p.PackageName == "pytest");

        Assert.Equal(">=2", requests.DeclaredVersion);
        Assert.Equal("==2.32.3", requests.InstalledVersion);
        Assert.Equal("(unconstrained)", pytest.DeclaredVersion);
        Assert.Equal("==8.2.2", pytest.InstalledVersion);
    }

    [Fact]
    public async Task PythonParser_UsesPoetryLockForInstalledVersionsAndRelatedSecurityPackages()
    {
        var projectDir = Directory.CreateDirectory(Path.Combine(_rootPath, "poetry-app"));
        await File.WriteAllTextAsync(
            Path.Combine(projectDir.FullName, "pyproject.toml"),
            """
            [tool.poetry.dependencies]
            python = "^3.11"
            requests = "^2.0"
            """,
            TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(
            Path.Combine(projectDir.FullName, "poetry.lock"),
            """
            [[package]]
            name = "requests"
            version = "2.32.3"

            [package.dependencies]
            certifi = ">=2017.4.17"

            [[package]]
            name = "certifi"
            version = "2024.6.2"
            """,
            TestContext.Current.CancellationToken);

        var projects = await new PythonEcosystemHandler().ScanProjectsAsync(
            _rootPath,
            TestContext.Current.CancellationToken);

        var package = Assert.Single(Assert.Single(projects).Packages);

        Assert.Equal("requests", package.PackageName);
        Assert.Equal("^2.0", package.DeclaredVersion);
        Assert.Equal("2.32.3", package.InstalledVersion);

        var related = Assert.Single(package.RelatedSecurityPackages);
        Assert.Equal(Ecosystem.Python, related.Ecosystem);
        Assert.Equal("certifi", related.PackageName);
        Assert.Equal("2024.6.2", related.Version);
        Assert.Equal("requests > certifi", related.Relationship);
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
            Directory.Delete(_rootPath, recursive: true);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "DepScope.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
