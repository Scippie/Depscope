# Contributing to DepScope

Thanks for considering contributing to DepScope!  
This document is intentionally short – the goal is to make it easy to get started.

---

## Ways to contribute

- **Bug reports** – something crashes, behaves oddly, or a project type isn’t detected.
- **Feature requests** – new ecosystems, better version handling, UI tweaks, etc.
- **Code contributions** – bug fixes, small features, refactors, docs.

Use the templates in **Issues → New issue** when possible:
- **Bug report**
- **Feature request**

---

## Opening issues

When reporting a bug, please include:

- **Environment**
  - OS (Windows / macOS / Linux + version)
  - DepScope version (e.g. `0.9.4`)
- **What you did**
  - Steps to reproduce (ideally with a public repo or a minimal sample).
- **What happened**
  - Actual behavior, error message, or incorrect output.
- **What you expected**

Screenshots and sample project links are very helpful.

---

## Pull requests

Before starting a large change, open an issue to discuss direction first.

Guidelines:

- Keep PRs **focused** – one fix or feature at a time.
- Make sure the app still builds for:
  - Windows
  - macOS
  - Linux
- Run the automated test suite before committing or opening a PR:
  - `dotnet test`
- For scanner changes, also run DepScope against at least one real local repository.
- Follow existing code style:
  - C# 10 / .NET 8
  - Use `async`/`await` and cancellation tokens where appropriate.
  - Keep ecosystem handlers robust (catch network/parse errors and degrade gracefully).

---

## Project structure (quick overview)

- `DepScope.Core`
  - Ecosystem handlers (`DotNetEcosystemHandler`, `NpmEcosystemHandler`, …)
  - Models (`ProjectInfo`, `PackageRef`, `VersionUpdateType`, …)
  - `InspectionService`
- `DepScope.Desktop`
  - Avalonia UI (`MainWindow`, view models, settings)
  - Tray integration, notifications, update checks

If you add a new ecosystem:

1. Implement `IEcosystemHandler`.
2. Register it in `MainWindowViewModel` handler list.
3. Add a short note to `README.md`.

---

## Code of conduct

Please be respectful and constructive in issues and PRs.  
The goal of DepScope is to help developers keep dependencies healthy; let’s keep the collaboration healthy too 

