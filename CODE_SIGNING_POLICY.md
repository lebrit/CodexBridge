# Code signing policy

## Current status

CodexBridge applied to the SignPath Foundation open-source program on August 8, 2026. The application was not approved because the project does not yet have the external visibility and public-trust signals required by the Foundation program, such as independent adoption, contributors, references, and sustained public engagement. The response did not identify a source-code defect or security issue.

Current releases are unsigned unless both their release notes and their Authenticode signatures explicitly prove otherwise. CodexBridge does not use a self-signed certificate for public releases and has not subscribed to a paid signing plan.

If a future Foundation application is approved, signed releases will include the required notice:

> Free code signing provided by [SignPath.io](https://signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).

## Project and build origin

- Source repository: <https://github.com/lebrit/CodexBridge>
- License: MIT
- Trusted build system: GitHub Actions using `.github/workflows/build-release.yml`
- Release artifacts are built from the referenced Git commit, tested, checked for accidental private data, and published with a SHA-256 checksum.
- Any future SignPath signing request will be limited to artifacts produced by the public GitHub Actions workflow.

## Roles and approval

- Committer and reviewer: [Pavel Cherkashin (`lebrit`)](https://github.com/lebrit)
- Signing approver: [Pavel Cherkashin (`lebrit`)](https://github.com/lebrit)

All maintainers must use multi-factor authentication for GitHub and any future signing service. Every future signing request will require manual approval. Automated unsigned prereleases may continue to be produced for testing, but they must not be described as signed.

## Reapplication criteria

The project will consider reapplying only after it has measurable, organic external trust signals: independent users, stars or forks, issue or pull-request activity, contributors, third-party articles or discussions, and a sustained release history. These signals will not be purchased, fabricated, or represented more strongly than the public evidence supports.

## Privacy policy

CodexBridge does not include telemetry or a hosted backend. Settings, the project catalog, and logs remain in the user's local Windows profile.

This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it. Network access occurs only for user-initiated operations such as installing tools, connecting to a user-configured cloud storage destination, or using external package managers.

Passwords, OAuth sessions, private keys, authentication files, and the active Codex database are excluded from backup by default and are not published with release artifacts.

## Verification

Users can verify a release checksum with `Get-FileHash` and inspect the Authenticode status with `Get-AuthenticodeSignature`. A release is considered signed only when both the release notes and the executable signature confirm it.
