# Contributing to CodexBridge

Thank you for helping improve CodexBridge. Bug reports, recovery-test results, documentation fixes, and focused pull requests are welcome.

## Before opening an issue

- Search existing issues and use the appropriate template.
- Remove recovery keys, passwords, tokens, personal paths, project names, and repository contents from screenshots and logs.
- Include the CodexBridge version, Windows version, expected result, actual result, and reproducible steps.
- For backup or recovery problems, attach only the relevant sanitized lines from `CodexBridge-errors.log`.
- Report security vulnerabilities privately as described in [SECURITY.md](SECURITY.md).

## Local checks

```powershell
dotnet test .\CodexBridge.sln -c Release
powershell -ExecutionPolicy Bypass -File .\scripts\Test-PublicSafety.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Build-Release.ps1
```

Keep changes small and test the affected safety path. Do not add telemetry, secrets, generated machine reports, personal configuration, Graphify artifacts, or a new dependency when the .NET or Windows platform already provides the required behavior.

By submitting a contribution, you agree that it may be distributed under the repository's MIT License.
