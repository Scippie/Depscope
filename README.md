# DepScope

![Image](DepScope.Desktop/Assets/DepScope_img.png?raw=true)

DepScope is a cross-platform desktop app for Windows, macOS, and Linux that scans local projects for outdated dependencies across multiple ecosystems.
It is built with **.NET 8** and **Avalonia UI**.

DepScope is local-first: project files are read from your machine, source code is not uploaded, and startup update checks are disabled by default. Registry lookups can also be disabled with offline/private mode.

![Version](https://img.shields.io/badge/version-1.0.0-blue)
![.NET](https://img.shields.io/badge/.NET-8.0-5C2D91?logo=dotnet)
![Platform](https://img.shields.io/badge/platform-Windows-blue)
![Platform](https://img.shields.io/badge/Linux-FCC624?style=flat&logo=linux&logoColor=black)
![Platform](https://img.shields.io/badge/macOS-000000?style=flat&logo=apple&logoColor=white)

## Status

[![CI](https://github.com/Scippie/DepScope/actions/workflows/ci.yml/badge.svg)](https://github.com/Scippie/DepScope/actions/workflows/ci.yml)
[![Release](https://github.com/Scippie/DepScope/actions/workflows/release.yml/badge.svg)](https://github.com/Scippie/DepScope/actions/workflows/release.yml)
[![Latest release](https://img.shields.io/github/v/release/Scippie/DepScope)](https://github.com/Scippie/DepScope/releases)

## Features

- **Multi-root scanning**
  - Add multiple folders, repositories, solutions, or monorepos.
  - Each scanned root appears as a project group.
  - Removing one project no longer removes the whole saved root unless that group becomes empty.

- **Supported ecosystems**
  - **.NET**: `*.csproj` using NuGet package references.
  - **npm**: `package.json`, with installed versions from `package-lock.json`, `pnpm-lock.yaml`, or Yarn v1 `yarn.lock` when present.
  - **Python**: `requirements.txt`, `requirements/*.txt`, `Pipfile`, `pyproject.toml`, with installed versions from `Pipfile.lock` and `poetry.lock` when present.
  - **Java**: `pom.xml`, `build.gradle`, `build.gradle.kts`, with installed versions from `gradle.lockfile` when present.
  - **PHP**: `composer.json`, with installed versions from `composer.lock` when present.
  - **Go**: `go.mod` selected module versions.
  - **Rust**: `Cargo.toml`, with installed versions from `Cargo.lock` when present.
  - **GitHub Actions**: `.github/workflows/*.yml` and `.github/workflows/*.yaml` `uses:` references, including ref pinning status.

- **Dependency view**
  - Project tree on the left, package table on the right.
  - Package columns: `Package`, `Declared`, `Installed`, `Latest`, `Vulnerabilities`, `UpdateType`.
  - Update types: `None`, `Patch`, `Minor`, `Major`, `Unknown`.
  - Vulnerability status shows the highest known severity and advisory count when a package/version is matched.
  - Filters: `All`, `Outdated`, `Major only`, `Minor+Major`.
  - Large project lists and package tables scroll inside the window.

- **Local-first privacy controls**
  - Startup update checks are disabled by default for new settings.
  - Offline/private mode scans local manifests without registry latest-version lookups.
  - Offline/private mode also blocks startup and manual GitHub update checks.
  - NuGet v3 source, npm registry, Go proxy, Python package index, Packagist metadata, Maven search API, crates.io API, and GitHub API URLs can be overridden for private or mirrored registries.

- **Background behavior**
  - Remembers scanned roots and rescans them on startup.
  - Optional auto-rescan every 10 minutes.
  - Timer rescans are skipped while another scan is already running.
  - Optional system tray mode.

- **Notifications**
  - Optional in-window summary notification after scans when the main window is visible.
  - Native OS notifications are intentionally not exposed yet.

- **Updates**
  - Manual GitHub release check from Settings.
  - Optional auto-download of release archives.
  - Downloaded update archives must pass SHA-256 verification before extraction.
  - Verified updates are installed into a per-user app folder and DepScope restarts automatically.

## Installation

1. Open the [Releases](https://github.com/Scippie/DepScope/releases) page.
2. Download the archive for your platform:
   - `DepScope-win-x64.zip`
   - `DepScope-linux-x64.zip`
   - `DepScope-osx-x64.zip`
   - `DepScope-osx-arm64.zip`
3. Optionally verify the archive with its matching checksum file:
   - `DepScope-win-x64.zip.sha256`
   - `DepScope-linux-x64.zip.sha256`
   - `DepScope-osx-x64.zip.sha256`
   - `DepScope-osx-arm64.zip.sha256`
4. Extract the archive and run the executable:
   - Windows: `DepScope.Desktop.exe`
   - macOS: `DepScope.Desktop`
   - Linux: `./DepScope.Desktop`

On Linux or macOS you may need to mark the executable as runnable:

```bash
chmod +x DepScope.Desktop
```

On macOS you may also need to allow the app in System Settings because release artifacts are not notarized yet.

## Usage

1. Launch DepScope.
2. Click **Open Folder...** and choose a repository, solution, or monorepo.
3. Add more folders with **Open Folder...**; existing scanned roots are kept.
4. Select a project in the left tree to inspect dependencies.
5. Use the filter dropdown to focus on outdated packages.
6. Use **Remove** to stop tracking a selected project. If it is the last project in a root group, that root is removed from saved roots.

## Settings

The Settings flyout includes:

- Dark mode.
- Offline/private mode.
- NuGet v3 source URL.
- npm registry URL.
- Go proxy URL.
- Python package index URL.
- Packagist metadata URL.
- Maven search API URL.
- crates.io API URL.
- GitHub API URL.
- Start with system.
- Use system tray / close to tray.
- Auto-rescan.
- Check for updates on start.
- Auto-download updates.
- Manual update check.
- Show notifications after scan.

Settings are stored per user in the operating system application-data folder.

Verified updates are installed per user:

- Windows: `%LocalAppData%\Programs\DepScope`
- macOS/Linux: the user-local application data folder under `DepScope/Programs/DepScope`

Temporary downloaded update files are staged under the user-local `DepScope/Updates` folder. DepScope keeps the latest staging folder and removes older staging folders after a verified download.

## Notes And Limitations

- Latest-version checks use public registries unless offline/private mode is enabled.
- Vulnerability checks use OSV.dev when offline/private mode is disabled and a concrete package version is available.
- Source overrides affect latest-version lookups for NuGet, npm, Go, Python, PHP/Packagist, Java/Maven, Rust/crates.io, and GitHub Actions.
- GitHub Actions workflow scanning detects external `uses:` references and checks GitHub repository tags through the configured GitHub API when latest-version lookups are enabled.
- GitHub Actions major-floating tags such as `v4` are treated as current until a newer major tag is available.
- GitHub Actions ref pinning status is shown in the package grid's `Installed` column as `Tag/branch ref`, `Short SHA ref`, `SHA pinned`, or `Dynamic ref`.
- GitHub Actions are not queried through OSV package vulnerability checks and show `N/A` in the vulnerability column.
- Source override URLs are stored in plain per-user settings; do not include credentials or tokens in those URLs.
- Private package names and versions may be sent to public registries or OSV.dev when latest-version or vulnerability checks are enabled.
- Version ranges, managed versions, BOMs, path dependencies, and git dependencies are handled conservatively.
- Python latest-version classification prefers `Pipfile.lock` and `poetry.lock` installed versions when present.
- npm latest-version classification prefers `package-lock.json`, `pnpm-lock.yaml`, and Yarn v1 `yarn.lock` installed versions when present.
- PHP latest-version classification prefers `composer.lock` installed versions when present.
- Rust latest-version classification prefers `Cargo.lock` installed versions when present.
- Java Gradle latest-version classification prefers `gradle.lockfile` installed versions when present.
- Go uses selected module versions from `go.mod`; `go.sum` is checksum metadata and is not treated as a direct dependency lockfile.
- In ambiguous cases, DepScope may show `Latest` as empty or `UpdateType` as `Unknown`.
- Rust latest-version support is still basic.
- Native OS notification center integration is intentionally not exposed yet. The dependency-free Windows balloon approach based on notification-area APIs was not reliable enough across normal app, Visual Studio debug, and tray scenarios, and script-based approaches such as PowerShell are avoided for security and enterprise-device policy reasons. A future implementation should use a reliable cross-platform notification provider or platform-specific integration with clear packaging requirements.
- Update download verification currently uses SHA-256 sidecar files, not code signing.

## Development

Prerequisites:

- .NET 8 SDK.
- Git.
- A supported desktop OS: Windows, macOS, or Linux.

Common commands:

```powershell
dotnet restore
dotnet build
dotnet run --project DepScope.Desktop/DepScope.Desktop.csproj
```

Run tests before committing code changes:

```powershell
dotnet test
```

Project layout:

- `DepScope.Core`: ecosystem handlers, dependency models, scan orchestration.
- `DepScope.Desktop`: Avalonia UI, view models, settings, tray integration, notifications, update checks.
- `.github/workflows/ci.yml`: restore/build validation.
- `.github/workflows/release.yml`: tagged release build for Windows, Linux, and macOS.

## Release Notes For Maintainers

Version numbers are stored in:

- `DepScope.Core/DepScope.Core.csproj`
- `DepScope.Desktop/DepScope.Desktop.csproj`

Release workflow assets use stable names:

- `DepScope-win-x64.zip`
- `DepScope-linux-x64.zip`
- `DepScope-osx-x64.zip`
- `DepScope-osx-arm64.zip`

Each archive must be published with a matching `.sha256` sidecar. The auto-download path requires the checksum to exist and match before extraction.

Auto-install uses an external updater handoff so the running DepScope process can exit before files are replaced.

Tagged release builds run restore, Release build, and tests before publishing platform archives.

## Contributing

Contributions are welcome.

- Open issues for bugs and feature requests.
- Discuss broad changes before starting a large PR.
- Keep PRs focused and reviewable.
- Preserve cross-platform behavior.
- Avoid adding packages unless the dependency is justified.
- Update README or CHANGELOG when behavior changes.

See [CONTRIBUTING.md](./CONTRIBUTING.md) for contribution guidelines.

## License

DepScope is open source. See [License.txt](./License.txt) for details.
