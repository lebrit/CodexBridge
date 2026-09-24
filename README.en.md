# CodexBridge

[Русский](README.md) · English

[![Build and release](https://github.com/lebrit/CodexBridge/actions/workflows/build-release.yml/badge.svg?branch=develop)](https://github.com/lebrit/CodexBridge/actions/workflows/build-release.yml)

CodexBridge is an open-source Windows desktop application for encrypted backup and safe recovery of development projects and selected environment settings.

## Highlights

- bounded discovery of Git and non-Git projects inside user-selected roots;
- encrypted local restic snapshots and an optional second copy through rclone;
- an hourly Windows Task Scheduler agent with automatic catalog refresh;
- full verified snapshot extraction and an exact dry-run before recovery;
- conflict-safe merge: existing different files are never overwritten;
- a durable per-file transaction journal and SHA-256-verified rollback after interruption or restart;
- an isolated A-to-B migration lab with a real restic snapshot, full data verification, a repeated run, and conflict preservation;
- an independent cross-runner gate where Windows Server 2022 creates an encrypted synthetic repository and Windows Server 2025 verifies, restores, retries, and checks every file by SHA-256;
- restored Windows ACLs and directory `ReadOnly` flags are normalized only inside CodexBridge's temporary staging directory so data from another Windows SID remains accessible and removable;
- optional WinGet inventory, sanitized Codex configuration, global AGENTS, memories, skills, a portable Git profile, and VS Code settings when VS Code is installed;
- an Obsidian vault registry is retained as recovery metadata without applying old machine paths;
- a fast new-computer diagnostic checks tools, MCP, Graphify, Codebase Memory, Ponytail, and Obsidian paths without a full disk scan or exposing secrets;
- a dedicated new-computer wizard mode that requires an existing repository and saved recovery key, never creates a replacement key or an empty repository, and opens the verified recovery flow;
- confirmed environment preparation installs missing Graphify, Codebase Memory, and Ponytail through their official WinGet/npm/uv/Codex commands;
- a selectable new-computer app catalog works before backup recovery: ChatGPT, Git, Python, Node.js with npm, and optional GitHub CLI, .NET SDK, and VS Code; it previews and confirms installs from fixed WinGet/Store sources;
- protected projects can rebuild local Graphify and Codebase Memory indexes without an automatic GitHub push;
- Obsidian vault paths are rebound only on an unambiguous folder-name match, with the existing registry backed up first;
- a precise WinGet recovery plan removes already-installed packages from the import and uses `--no-upgrade` as a second-run safeguard;
- Git, Python, Node.js, and Java versions are recorded; only existing allowlisted user paths and path-valued variables can be added, while conflicting values are preserved;
- repeated environment recovery does not reinstall applications or VS Code extensions and does not duplicate PATH or matching Git settings;
- no telemetry, hosted backend, copied passwords, OAuth sessions, or active Codex database;
- a user-created diagnostic ZIP with aggregate state, environment checks, and redacted error tails but no key, settings, project catalog, Codex database, tokens, private keys, e-mail, IP addresses, or remote URLs;
- an optional daily GitHub release check; downloading, SHA-256 verification, and launching the installer remain explicitly confirmed steps;
- a per-user installer that updates in place and leaves `%LOCALAPPDATA%\CodexBridge` intact during uninstall;
- reproducible GitHub Actions prereleases with ZIP and installer packages, tests, a public-data safety check, and SHA-256 files.

## Quick start

1. Download `CodexBridge-...-setup.exe` from [Releases](https://github.com/lebrit/CodexBridge/releases) and install it. The ZIP remains available as a portable alternative.
2. Open CodexBridge from the Start menu and choose normal setup or the dedicated new-computer recovery mode.
3. For a new backup, store the generated recovery key separately. For migration, provide the existing repository and the key saved on the old computer; no replacement key is generated.
4. Find and review projects, create a backup, and check the repository.
5. Before recovery, run the verified dry-run. If recovery is interrupted, use the transaction journal in the Recovery page to resume a verified rollback.
6. On a fresh Windows PC, open Programs, select the apps you need, review the plan, and confirm installation. Python Install Manager resolves the current stable Python release. Account sign-in and any optional Codex CLI/index preparation remain separate steps.

Current public binaries are unsigned. The SignPath Foundation application was not approved because this new project does not yet meet the program's external visibility requirements. See [CODE_SIGNING_POLICY.md](CODE_SIGNING_POLICY.md).

Before each snapshot, CodexBridge creates a separate portable `config.toml`. MCP environment/header values and settings whose names indicate tokens, passwords, credentials, or secrets are omitted. Git restore uses a small allowlist and never imports credential helpers, signing keys, URL rewrites, or HTTP headers. User PATH entries and allowlisted path-valued variables are retained only for existing directories under known user or program roots, which are replaced with portable tokens.

## Build

```powershell
winget install --id Microsoft.DotNet.SDK.10 --exact
winget install --id restic.restic --exact
powershell -ExecutionPolicy Bypass -File .\scripts\Build-Release.ps1
```

The local build always creates the ZIP and also creates the installer when Inno Setup 6 is available. The GitHub Actions release gate requires both the local migration lab and a repository transfer between independent Windows Server 2022/2025 runners, plus installer acceptance jobs that install the published 0.9.0 release, upgrade, reinstall, and uninstall while preserving user data and tasks owned by other installations. Thirty-six synthetic GUI render checks cover both themes, two window sizes, all six pages, and both three-step wizard modes. PNGs and diagnostics remain in Actions artifacts for 14 days. These checks do not replace hands-on clean Windows 11 acceptance.

See [CONTRIBUTING.md](CONTRIBUTING.md), [SECURITY.md](SECURITY.md), the [roadmap](docs/ROADMAP.md), and the safe [manual acceptance checklist](docs/MANUAL_ACCEPTANCE.md). Questions and ideas are welcome in [GitHub Discussions](https://github.com/lebrit/CodexBridge/discussions); reproducible defects belong in [GitHub Issues](https://github.com/lebrit/CodexBridge/issues). Licensed under the [MIT License](LICENSE).
