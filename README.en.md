# CodexBridge

[Русский](README.md) · English

CodexBridge is an open-source Windows desktop application for encrypted backup and safe recovery of development projects and selected environment settings.

## Highlights

- bounded discovery of Git and non-Git projects inside user-selected roots;
- encrypted local restic snapshots and an optional second copy through rclone;
- an hourly Windows Task Scheduler agent with automatic catalog refresh;
- full verified snapshot extraction and an exact dry-run before recovery;
- conflict-safe merge: existing different files are never overwritten;
- a durable per-file transaction journal and SHA-256-verified rollback after interruption or restart;
- an isolated A-to-B migration lab with a real restic snapshot, full data verification, a repeated run, and conflict preservation;
- restored Windows ACLs and directory `ReadOnly` flags are normalized only inside CodexBridge's temporary staging directory so data from another Windows SID remains accessible and removable;
- optional WinGet inventory, sanitized Codex configuration, global AGENTS, memories, skills, a portable Git profile, and VS Code settings when VS Code is installed;
- an Obsidian vault registry is retained as recovery metadata without applying old machine paths;
- a fast new-computer diagnostic checks tools, MCP, Graphify, Codebase Memory, Ponytail, and Obsidian paths without a full disk scan or exposing secrets;
- confirmed environment preparation installs missing Graphify, Codebase Memory, and Ponytail through their official WinGet/npm/uv/Codex commands;
- protected projects can rebuild local Graphify and Codebase Memory indexes without an automatic GitHub push;
- Obsidian vault paths are rebound only on an unambiguous folder-name match, with the existing registry backed up first;
- a precise WinGet recovery plan removes already-installed packages from the import and uses `--no-upgrade` as a second-run safeguard;
- Git, Python, Node.js, and Java versions are recorded; only existing allowlisted user paths and path-valued variables can be added, while conflicting values are preserved;
- repeated environment recovery does not reinstall applications or VS Code extensions and does not duplicate PATH or matching Git settings;
- no telemetry, hosted backend, copied passwords, OAuth sessions, or active Codex database;
- a per-user installer that updates in place and leaves `%LOCALAPPDATA%\CodexBridge` intact during uninstall;
- reproducible GitHub Actions prereleases with ZIP and installer packages, tests, a public-data safety check, and SHA-256 files.

## Quick start

1. Download `CodexBridge-...-setup.exe` from [Releases](https://github.com/lebrit/CodexBridge/releases) and install it. The ZIP remains available as a portable alternative.
2. Open CodexBridge from the Start menu and complete the setup wizard.
3. Store the generated recovery key separately. Encrypted snapshots cannot be opened without it.
4. Find and review projects, create a backup, and check the repository.
5. Before recovery, run the verified dry-run. If recovery is interrupted, use the transaction journal in the Recovery page to resume a verified rollback.

Current public binaries are unsigned. The SignPath Foundation application was not approved because this new project does not yet meet the program's external visibility requirements. See [CODE_SIGNING_POLICY.md](CODE_SIGNING_POLICY.md).

Before each snapshot, CodexBridge creates a separate portable `config.toml`. MCP environment/header values and settings whose names indicate tokens, passwords, credentials, or secrets are omitted. Git restore uses a small allowlist and never imports credential helpers, signing keys, URL rewrites, or HTTP headers. User PATH entries and allowlisted path-valued variables are retained only for existing directories under known user or program roots, which are replaced with portable tokens.

## Build

```powershell
winget install --id Microsoft.DotNet.SDK.10 --exact
winget install --id restic.restic --exact
powershell -ExecutionPolicy Bypass -File .\scripts\Build-Release.ps1
```

The local build always creates the ZIP and also creates the installer when Inno Setup 6 is available. The GitHub Actions release gate requires the migration lab and independent Windows Server 2022/2025 acceptance jobs: install the published 0.9.0 release, upgrade, reinstall, then uninstall while preserving user data and tasks owned by other installations. Thirty synthetic GUI render checks cover both themes, two window sizes, all six pages, and all three wizard steps. PNGs and diagnostics remain in Actions artifacts for 14 days. These checks do not replace hands-on clean Windows 11 acceptance.

See [CONTRIBUTING.md](CONTRIBUTING.md), [SECURITY.md](SECURITY.md), and the [roadmap](docs/ROADMAP.md). Licensed under the [MIT License](LICENSE).
