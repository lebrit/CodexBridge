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
- restored Windows ACLs are reset only inside CodexBridge's temporary staging directory so data from another Windows SID remains accessible and removable;
- optional WinGet inventory, safe Codex configuration, and VS Code settings when VS Code is installed;
- no telemetry, hosted backend, copied passwords, OAuth sessions, or active Codex database;
- reproducible GitHub Actions prereleases with tests, a public-data safety check, and SHA-256 files.

## Quick start

1. Download and unpack a ZIP from [Releases](https://github.com/lebrit/CodexBridge/releases).
2. Run `CodexBridge.App.exe` and complete the setup wizard.
3. Store the generated recovery key separately. Encrypted snapshots cannot be opened without it.
4. Find and review projects, create a backup, and check the repository.
5. Before recovery, run the verified dry-run. If recovery is interrupted, use the transaction journal in the Recovery page to resume a verified rollback.

Current public binaries are unsigned. The SignPath Foundation application was not approved because this new project does not yet meet the program's external visibility requirements. See [CODE_SIGNING_POLICY.md](CODE_SIGNING_POLICY.md).

## Build

```powershell
winget install --id Microsoft.DotNet.SDK.10 --exact
winget install --id restic.restic --exact
powershell -ExecutionPolicy Bypass -File .\scripts\Build-Release.ps1
```

The local build warns when restic is unavailable; the GitHub Actions release gate always requires the migration lab to pass.

See [CONTRIBUTING.md](CONTRIBUTING.md), [SECURITY.md](SECURITY.md), and the [roadmap](docs/ROADMAP.md). Licensed under the [MIT License](LICENSE).
