# release

- [release](#release)
  - [1 versioning policy](#1-versioning-policy)
  - [2 branches and release flow](#2-branches-and-release-flow)
    - [2.1 day-to-day work](#21-day-to-day-work)
    - [2.2 cutting a release](#22-cutting-a-release)
    - [2.3 what ci does at each step](#23-what-ci-does-at-each-step)
    - [2.4 when a step fails](#24-when-a-step-fails)
  - [3 what a github release contains](#3-what-a-github-release-contains)
  - [4 why the dll is built locally](#4-why-the-dll-is-built-locally)
  - [5 github setup](#5-github-setup)

This document covers how `glados-pdm-addin` versions and releases are cut. It follows the same
branch and release flow as `glados-sw-addin/docs/RELEASE.md`, with one difference: CI cannot
build this add-in, so the DLL is built and attached on a developer machine (section 4).

## 1 versioning policy

The add-in versions independently of `glados`, with its own semver: **major** for a breaking
change to its behaviour or its supported backend range, **minor** for new functionality,
**patch** for fixes that change neither. Every CHANGELOG entry states the backend minors it
supports (for example "Supports backend 0.1.x"), since a backend minor may change or remove
`pat` endpoints. The first release is `0.1.0`.

The supported range lives in code as `GladosClient.SupportedPatContract` (the `pat` contract minor, `0.1` for backend 0.1.x). The GLADOS tab warns when the server reports a different `pat_contract` on `GET api/v1/pat/version`, without blocking anything. When a release is checked against a new backend minor, raise the constant and the CHANGELOG's "Supports backend" line together.

Version sources:

- `Properties/AssemblyInfo.cs`: `AssemblyVersion` and `AssemblyFileVersion`, both `X.Y.Z.0`,
  and `AssemblyInformationalVersion`, `X.Y.Z`, which the tag `vX.Y.Z` must equal. The DLL's file
  version and product version (its Details tab in File Explorer) come from them.
- `AddIn.cs`: `mlAddInVersion`, the number the PDM Administration tool shows next to the
  uploaded add-in. It rises by one with every release; `scripts/Start-Release.ps1` raises it and
  `scripts/Test-ReleaseVersion.ps1` refuses a release that does not.

## 2 branches and release flow

### 2.1 day-to-day work

1. Branch from an up-to-date `main`.
2. Build and test locally (`CLAUDE.md`, Commands: Debug Add-ins against `bin\Debug`).
3. Open a pull request into `main`. CI runs on it (section 2.3).
4. Squash-merge it. Nothing is released yet.

Unfinished work stays on its branch, since everything on `main` ships with the next release.

### 2.2 cutting a release

1. Run `scripts\Start-Release.ps1 X.Y.Z` (Windows PowerShell or PowerShell 7, repository root).
   It refuses a dirty tree or an existing branch or tag, branches `release/X.Y.Z` from an
   up-to-date `main`, sets the version (section 1), turns the `[Unreleased]` section of
   `CHANGELOG.md` into `[X.Y.Z] - YYYY-MM-DD`, commits `release: X.Y.Z` and pushes the branch.
2. Build the branch in Release, register it as a debug add-in and run the manual test plan in
   the test vault (the `pdm-addin` skill). Complete the CHANGELOG section, including the
   supported backend range. Push small release fixes to the branch as needed.
3. Open a pull request into `main`. If `main` moved in the meantime, update the branch from
   `main` first; `release-check` fails until it contains the current `main`.
4. Squash-merge it. `release.yml` tags the merge commit `vX.Y.Z` and creates a draft GitHub
   Release with the CHANGELOG section as its body.
5. Run `scripts\Publish-Release.ps1 X.Y.Z` on a machine with the SOLIDWORKS PDM client, Visual
   Studio and the GitHub CLI (`gh auth login` once). It checks out the tag into a temporary
   worktree, rebuilds Release there, checks the DLL's file version, uploads
   `GladosPdmAddin.dll` and `SHA256SUMS.txt` to the draft and publishes it. `-KeepDraft` uploads
   without publishing.
6. Upload that DLL in the PDM Administration tool (Add-ins, New Add-in). Clients get it on their
   next vault login.

The same sequence covers hotfixes: a `fix/*` pull request into `main`, then
`scripts\Start-Release.ps1` for the next patch.

### 2.3 what ci does at each step

- **On every pull request into `main` and on pushes to `release/**`:** `ci.yml` runs
  - `gitleaks`: a secret scan over the full history;
  - `sources`: every tracked `.cs` file has a `<Compile>` item in `GladosPdmAddin.csproj` and
    every item has a tracked file (the csproj lists its sources explicitly);
  - `release-check`, on release branches only: `Test-ReleaseVersion.ps1` (both version
    attributes, the CHANGELOG section, the tag still free, `mlAddInVersion` above the previous
    release's) and that the branch contains the current `main`. Skipped elsewhere, which counts
    as passing.

  Documentation-only pull requests run too: the jobs are required status checks, and a check a
  path filter never starts stays "pending" and blocks the merge.
- **On merging a release pull request:** `release.yml` runs
  - `guard`: only a merged pull request from a `release/X.Y.Z` branch of this repository,
    `Test-ReleaseVersion.ps1` again on the merge commit, and the release branch must have
    contained `main` as it was right before the merge. A failing guard tags nothing;
  - `release`: tags the merge commit `vX.Y.Z` and creates the draft release.

  Tagging runs in the workflow, not by hand, because a tag pushed by hand creates no release.

### 2.4 when a step fails

- `guard` failed: nothing was tagged, but `main` now carries the new version. When `main` is
  still the state to ship, run *Actions, Release, Run workflow* on `main` with the version as
  input: it checks the current `main` head the same way and tags it. Otherwise fix `main`
  through normal pull requests and cut the next patch.
- `release` failed: re-run the failed job from the run page. It accepts a tag that already
  points at the merge commit and a draft that already exists.
- `Publish-Release.ps1` failed: fix the cause (it names it) and run it again. It refuses a
  release that is already published, so a published DLL is never replaced; ship a fix as the
  next patch.

## 3 what a github release contains

- `GladosPdmAddin.dll`: the add-in, built in Release from the tag. This is the file to upload
  in the Administration tool. It does not include `EPDM.Interop.epdm.dll`; every PDM client
  has its own.
- `SHA256SUMS.txt`: its checksum, so a download can be checked before the upload
  (`Get-FileHash -Algorithm SHA256 GladosPdmAddin.dll`).

The release body is the matching `## [X.Y.Z] - ...` section of `CHANGELOG.md`. The DLL is not
Authenticode-signed.

Because the DLL is built on a developer machine, a GitHub Release does not prove which
machine built it, the way CI-built artifacts with build provenance would. The tag and
`Publish-Release.ps1`'s clean worktree build make it reproducible from the tag on any machine
with the PDM client.

## 4 why the dll is built locally

The add-in implements PDM's COM interfaces (`IEdmAddIn5`, `EdmCmd`, `IEdmVault5` and others),
so the compiler needs `EPDM.Interop.epdm.dll` from `C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS
PDM\`. The PDM client loads its own copy at runtime, so the DLL is never shipped. A
GitHub-hosted runner has no PDM client, and the interop assembly belongs to SOLIDWORKS, so it
is not committed or downloaded from an unofficial package. Building in CI would need a
self-hosted Windows runner with the PDM client installed.

## 5 github setup

Configured by hand in the repository settings (rulesets need a public repository): squash
merging only, "Always suggest updating pull request branches" and "Automatically delete head
branches" on; a `main` branch ruleset with *Restrict deletions*, *Block force pushes*, *Require
linear history*, *Require a pull request before merging* (0 approvals, squash only) and
*Require status checks to pass* with the checks `gitleaks`, `sources` and `release-check`; a
`v*` tag ruleset with *Restrict updates*, *Restrict deletions* and *Block force pushes*
(creation stays open for the release workflow).
