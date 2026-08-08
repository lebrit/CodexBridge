# Code signing policy

## Current status

CodexBridge submitted an application to the SignPath Foundation open-source program on August 8, 2026 and is awaiting review. Current releases are unsigned unless their release notes explicitly state that Authenticode signing was completed and verified.

After approval, signed releases will include this notice:

> Free code signing provided by [SignPath.io](https://signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).

## Project and build origin

- Source repository: <https://github.com/lebrit/CodexBridge>
- License: MIT
- Trusted build system: GitHub Actions using `.github/workflows/build-release.yml`
- Release artifacts are built from the referenced Git commit, tested, checked for accidental private data, and published with a SHA-256 checksum.
- SignPath signing will be requested only for artifacts produced by the public GitHub Actions workflow.

## Roles and approval

- Committer and reviewer: [Pavel Cherkashin (`lebrit`)](https://github.com/lebrit)
- Signing approver: [Pavel Cherkashin (`lebrit`)](https://github.com/lebrit)

All maintainers must use multi-factor authentication for GitHub and SignPath. Every signing request requires manual approval. Automated unsigned prereleases may continue to be produced for testing, but they must not be described as signed.

## Privacy policy

CodexBridge does not include telemetry or a hosted backend. Settings, the project catalog, and logs remain in the user's local Windows profile.

This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it. Network access occurs only for user-initiated operations such as installing tools, connecting to a user-configured cloud storage destination, or using external package managers.

Passwords, OAuth sessions, private keys, authentication files, and the active Codex database are excluded from backup by default and are not published with release artifacts.

## Verification

Users can verify a release checksum with `Get-FileHash` and, once signing is enabled, inspect the Authenticode publisher with `Get-AuthenticodeSignature`. A release is considered signed only when both the release notes and the executable signature confirm it.
