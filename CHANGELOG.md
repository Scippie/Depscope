# Changelog

All notable changes to this project will be documented in this file.

## [1.0.0] - Unreleased

### Added
- Added OSV-based vulnerability monitoring with package severity badges in the dependency grid.
- Added an advisory ID detail strip for the selected package when vulnerability IDs are available.
- Added hidden npm lockfile transitive dependency checks that roll vulnerability findings up to the direct dependency row that brings them in.
- Added hidden NuGet, Composer, Cargo, and Poetry lockfile dependency checks that roll transitive vulnerability findings up to the actionable direct dependency row.
- Added richer OSV advisory details with source, severity, affected package/version, dependency path, aliases, summary, and advisory links.
- Added compact project/package status legends and vulnerability icon markers in the package grid.
- Added a manual `Rescan` button that runs the same staged saved-root rescan used by background refreshes.

### Fixed
- Fixed verified update installation when close-to-tray is enabled so the updater can exit DepScope, copy files into the per-user install folder, and restart the app.
- Fixed GitHub Actions monitoring resilience by accepting common GitHub web URLs in the GitHub API setting and showing vulnerability checks as not applicable for workflow dependencies.
- Fixed transient latest-version lookup gaps by retrying unresolved packages and workflow dependencies during background auto-rescan.
- Fixed scan UI consistency by staging timer auto-rescan results before replacing the visible project list.
- Restored progressive foreground saved-root scanning while keeping staged replacement for timer auto-rescans.
- Fixed package grid column sizing so vulnerability and update badges remain visible when package/version text is long.
- Fixed Go scanning so `// indirect` modules no longer appear as direct package rows.
- Fixed project status coloring so an up-to-date project with known vulnerabilities is flagged as vulnerable instead of clean.
- Fixed startup/manual scan resilience by rechecking unresolved latest-version lookups in the background after the first results are shown.
- Fixed GitHub Actions latest-version resilience with a GitHub web release fallback when the API response is unavailable or unusable.
- Aligned the project status legend with the package legend and moved project removal to the project header.

### Changed
- Native OS notifications are not exposed in settings for now because the dependency-free Windows balloon approach was not reliable enough and script-based notification methods are not acceptable for enterprise use.

---

## [0.9.9] - Unreleased

### Added
- Added parser and view-model behavior tests.
- Added configurable NuGet v3 source and npm registry URL settings.
- Added configurable Go proxy URL setting for Go latest-version lookups.
- Added configurable Python package index URL setting for Python latest-version lookups.
- Added configurable Packagist metadata URL setting for PHP latest-version lookups.
- Added configurable Maven search API, crates.io API, and GitHub API URL settings for Java, Rust, and GitHub Actions latest-version lookups.
- Added GitHub Actions workflow dependency detection for external `uses:` references.
- Added GitHub tag lookup for detected GitHub Actions workflow dependencies through the configured GitHub API.
- Added GitHub Actions reference pinning status for tag/branch refs, short SHA refs, full SHA-pinned refs, and dynamic refs.
- Added `composer.lock` installed-version detection for PHP dependencies.
- Added `Cargo.lock` installed-version detection for Rust dependencies.
- Added Gradle `gradle.lockfile` installed-version detection for Java dependencies.
- Added `Pipfile.lock` installed-version detection for Python dependencies.
- Added Yarn v1 `yarn.lock` installed-version detection for npm dependencies.
- Added `pnpm-lock.yaml` installed-version detection for npm dependencies.

### Changed
- CI now runs the automated test suite.
- Release builds now run restore, Release build, and tests before publishing artifacts.

---

## [0.9.8] - 2026-06-18

### Added
- Added offline/private mode to scan local manifests without latest-version registry lookups.
- Added SHA-256 verification for downloaded update archives before extraction.
- Added stable per-platform release asset names with matching `.sha256` sidecars.
- Added verified auto-install and restart flow using a per-user app install folder.

### Changed
- `Open Folder...` now appends scanned roots instead of replacing the current project list.
- Startup update checks are disabled by default for new settings.
- Auto-rescan now skips timer scans while another scan is already running.
- Improved main window layout so large project trees and package grids scroll inside the window.
- Re-enabled compiled bindings for stable main-window bindings while keeping dynamic templates on reflection bindings.
- Update checks now use the configured GitHub owner and repository parameters.
- Auto-update staging now keeps the latest downloaded update and removes older staging folders.

### Fixed
- Removing one project no longer removes the whole saved root unless the group is empty.
- Fixed malformed Go `require` lines so partial `go.mod` entries do not crash scans.
- Isolated malformed `.csproj` parsing so one bad .NET project file does not abort the whole scan.
- Verified the solution workflow references match files present in the repository.

---

## [0.9.7] – 2026-05-17

### Fixed
- Packages update

## [0.9.6] – 2026-04-10

### Fixed
- Packages update

---

## [0.9.5] – 2026-04-10

### Fixed
- Updated Avalonia folder picker code to use the current StorageProvider API.
- Fixed converter nullability warnings after package updates.
- Guarded Windows-only registry autostart code to avoid cross-platform warnings.
- Fixed Avalonia compiled binding / DataGrid type inference issues after upgrading packages.
- Improved npm installed-version detection using `package-lock.json`, including `devDependencies` and nearest lockfile lookup.
- Fixed NuGet latest-version detection to use NuGet registration data correctly and ignore unlisted versions.

---

## [0.9.4] – 2026-01-15

First public release of **DepScope**.

### Added

- **Cross-platform desktop app**
  - Built with .NET 8 and Avalonia UI.
  - Runs on Windows, macOS, and Linux using a single codebase.

- **Multi-root project scanning**
  - Add one or more root folders (repos, monorepos, solutions).
  - Each root appears as a **project group**.
  - Each group contains multiple **subprojects** (per `csproj`, `package.json`, etc.).
  - Subprojects with no dependencies are hidden to keep the list clean.

- **Ecosystem support**
  - **.NET / NuGet**
    - SDK-style `*.csproj` projects.
  - **npm**
    - `package.json` (`dependencies` + `devDependencies`).
  - **Python**
    - `requirements.txt`
    - `requirements/*.txt` (e.g. Django `requirements/common.txt`, `dev.txt`, etc.)
    - `Pipfile`
    - `pyproject.toml` (Poetry / PEP 621)
  - **Java / JVM**
    - Maven: `pom.xml` (includes dependencyManagement/BOM entries, marked as `(managed)` when version is inherited).
    - Gradle: `build.gradle` / `build.gradle.kts` (basic support for common dependency declarations).
  - **PHP**
    - `composer.json` (`require` + `require-dev`, using Packagist to discover latest versions where possible).
  - **Go**
    - `go.mod` (module dependencies via Go module ecosystem).
  - **Rust**
    - `Cargo.toml` (basic detection and listing of dependencies).

- **Dependency view**
  - Per subproject **package grid**:
    - `Package` – id/name (NuGet id, npm package, Composer package, etc.).
    - `Declared` – version or constraint from the project file.
    - `Latest` – latest known version from the public registry (where supported).
    - `UpdateType` – `None`, `Patch`, `Minor`, `Major`, or `Unknown`.
  - Color-coded rows based on `UpdateType` (with dark-mode friendly colors).

- **Filtering**
  - Per-project package filters:
    - **All**
    - **Outdated only**
    - **Major only**
    - **Minor + Major**

- **Project management**
  - Remove a selected project from tracking (including its root if empty).
  - Recent roots are persisted and **rescanned automatically** on startup.

- **Settings & behavior**
  - Settings stored in a simple JSON file per user.
  - Toggles for:
    - Dark mode
    - Start with system (autostart)
    - Minimize to tray instead of exiting
    - Auto-rescan every 10 minutes
    - Check for updates on startup (GitHub Releases)
    - Auto-download latest release archive
    - Show notifications after scan

- **Notifications**
  - Optional summary notification after a scan when outdated packages are found:
    - e.g. `2 project(s) with 4 outdated package(s).`
  - Notifications are in-app toasts (rendered inside the window) using Avalonia’s
    `WindowNotificationManager`.

- **App updates**
  - Ability to check the latest version on GitHub.
  - Manual “Check for updates” button in settings.
  - Optional auto-download of the newest release archive.

- **System tray integration**
  - Optional tray icon.
  - Ability to keep DepScope running in the background while the main window is hidden.

- **Configuration persistence**
  - Tracks scanned roots across sessions.
  - Remembers UI preferences and behavior flags.

---

### Known limitations (0.9.4)

- **Rust**
  - Dependency detection from `Cargo.toml` works.
  - Latest-version resolution via crates.io is basic/incomplete and may not populate the `Latest` column in all environments.

- **Version ranges / managed versions**
  - For BOM-managed, property-based, or range constraints (Maven/Gradle BOMs, Composer `^`/`~`/`*`, Python constraint syntax, etc.):
    - DepScope often shows a `Latest` version from the registry.
    - `UpdateType` is usually `Unknown`, because the exact resolved version is not obvious from the project file alone.
  - Gradle plugin lines (`plugin:…`) and non-registry sources (git, path, etc.) are treated conservatively and may not have `Latest`/`UpdateType`.

- **Notifications**
  - Currently implemented as in-window toasts.
  - No native OS notification center integration yet (Windows/macOS/Linux).

---

[0.9.4]: https://github.com/Scippie/DepScope/releases/tag/v0.9.4
