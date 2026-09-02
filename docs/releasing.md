# Release process

`scripts/release-gate.ps1` is the repository's single release preflight. It restores dependencies, builds a portable Release configuration, installs the package-matched Playwright browser, runs all three test projects through `test.ps1`, performs a machine-readable NuGet vulnerability audit, publishes the application, creates SHA-256 checksums, builds the OCI image with the configured WebAssembly AOT compilation, and waits for its `/health` check.

## Local release candidate

Requirements: Git, .NET 10, PowerShell 7, and a running Docker daemon.

Start from a commit with no uncommitted files or changes:

```powershell
pwsh ./scripts/release-gate.ps1 -Version 1.2.3
```

A successful run writes:

- `.artifacts/release-gate/publish/` — framework-dependent application files
- `.artifacts/release-gate/release.json` — version, revision, and build time
- `.artifacts/release-gate/SHA256SUMS` — hashes for the published files and release metadata
- `changedetection:release-gate-1.2.3` — locally validated container image

`-SkipContainer` is available only for a limited preflight on machines without Docker. A skipped container build is not a complete release-gate pass. `-AllowDirty` is similarly intended for development diagnosis and must not be used to certify a release.

## Hosted gate

The `Release gate` GitHub Actions workflow runs on `v*` tags and can also be started manually with a semantic version. Successful runs retain the publish directory, metadata, and checksums as a workflow artifact.

Recommended sequence:

```powershell
pwsh ./scripts/release-gate.ps1 -Version 1.2.3
git tag -a v1.2.3 -m "ChangeDetection 1.2.3"
git push origin v1.2.3
```

Treat the tag as releasable only after the `Release gate` run for that exact tag and commit is green. This workflow validates an image build and smoke test; it does not push an image to a registry or create a GitHub Release.
