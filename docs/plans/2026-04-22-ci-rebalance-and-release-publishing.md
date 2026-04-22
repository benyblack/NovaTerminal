# CI Rebalance And Release Publishing Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Re-enable a balanced set of pull-request CI lanes and add a tag-driven release workflow that publishes Windows, Linux, and macOS Native AOT bundles to GitHub Releases.

**Architecture:** Keep the existing `ci.yml` as the main validation workflow and expand only the PR-relevant matrices/conditions there. Add a separate `release.yml` that triggers on `v*` tags, rebuilds the needed native dependencies, publishes Native AOT bundles for each target RID, archives them, and uploads them to the matching GitHub Release.

**Tech Stack:** GitHub Actions, .NET 10 SDK, Rust toolchain, Native AOT publish, PowerShell/`pwsh`, Markdown docs.

---

### Task 1: Rebalance PR CI In `ci.yml`

**Files:**
- Modify: `.github/workflows/ci.yml`
- Reference: `docs/plans/2026-04-22-ci-rebalance-and-release-publishing-design.md`

**Step 1: Update the PR OS matrix for build-and-unit-tests**

Change the `build_and_unit_tests` matrix so pull requests run on both Windows and Linux instead of Linux only.

Replace the current matrix expression with:

```yaml
      matrix:
        os: ${{ fromJSON(github.event_name == 'pull_request' && '["windows-latest","ubuntu-latest"]' || '["windows-latest","ubuntu-latest"]') }}
```

Then simplify it to the clearer equivalent:

```yaml
      matrix:
        os: [windows-latest, ubuntu-latest]
```

**Step 2: Re-enable PTY smoke tests on PRs**

Update the `pty_smoke_tests` job condition from:

```yaml
    if: github.event_name != 'pull_request'
```

to:

```yaml
    if: github.event_name == 'pull_request' || github.event_name == 'push' || github.event_name == 'schedule' || github.event_name == 'workflow_dispatch'
```

Keep the existing Windows/Linux matrix unchanged.

**Step 3: Re-enable render metrics tests on PRs**

Update the `render_metrics_tests` job condition from:

```yaml
    if: github.event_name != 'pull_request'
```

to:

```yaml
    if: github.event_name == 'pull_request' || github.event_name == 'push' || github.event_name == 'schedule' || github.event_name == 'workflow_dispatch'
```

Keep the existing Windows/Linux matrix unchanged.

**Step 4: Leave heavy lanes unchanged**

Do **not** broaden these jobs in this task:

- `replay_tests`
- `parity_compare`
- `aot_publish`
- macOS inclusion in the main CI workflow

The purpose of this task is a balanced PR CI increase, not full-cost parity.

**Step 5: Run targeted validation**

Run:

```bash
rg -n "build_and_unit_tests|pty_smoke_tests|render_metrics_tests|if: github.event_name" .github/workflows/ci.yml
git diff --check
```

Expected:

- `build_and_unit_tests` shows Windows and Linux on PRs
- `pty_smoke_tests` and `render_metrics_tests` are no longer excluded from PRs
- `git diff --check` prints no whitespace errors

**Step 6: Commit**

```bash
git add .github/workflows/ci.yml
git commit -m "ci: rebalance pull request coverage"
```

### Task 2: Add Tag-Driven Release Publishing Workflow

**Files:**
- Create: `.github/workflows/release.yml`
- Reference: `.github/workflows/ci.yml`
- Reference: `src/NovaTerminal.App/NovaTerminal.App.csproj`

**Step 1: Create the release workflow shell**

Create `.github/workflows/release.yml` with these top-level triggers:

```yaml
name: Release

on:
  push:
    tags:
      - "v*"
  workflow_dispatch:
```

Add shared env:

```yaml
env:
  DOTNET_NOLOGO: true
  DOTNET_CLI_TELEMETRY_OPTOUT: true
  CONFIGURATION: Release
```

**Step 2: Add native build job for all three release OSes**

Mirror the native build logic from `ci.yml`, but include:

```yaml
strategy:
  fail-fast: false
  matrix:
    os: [windows-latest, ubuntu-latest, macos-latest]
```

Preserve the existing per-OS Rust build handling and upload native artifacts under names like:

```yaml
name: native-${{ matrix.os }}
```

**Step 3: Add AOT publish matrix job**

Add a `publish_aot` job with:

```yaml
strategy:
  fail-fast: false
  matrix:
    include:
      - os: windows-latest
        rid: win-x64
      - os: ubuntu-latest
        rid: linux-x64
      - os: macos-latest
        rid: osx-arm64
```

Publish with:

```yaml
run: dotnet publish src/NovaTerminal.App/NovaTerminal.App.csproj -c ${{ env.CONFIGURATION }} -r ${{ matrix.rid }} --self-contained true -p:PublishAot=true -o artifacts/publish/${{ matrix.rid }}
```

**Step 4: Archive each publish folder into a release asset**

Use `pwsh` so the archive step stays consistent across runners:

```yaml
      - name: Archive bundle
        shell: pwsh
        run: |
          $rid = "${{ matrix.rid }}"
          $source = "artifacts/publish/$rid"
          $dest = "artifacts/release/NovaTerminal-$rid-${{ github.ref_name }}.zip"
          New-Item -ItemType Directory -Force -Path "artifacts/release" | Out-Null
          if (Test-Path $dest) { Remove-Item $dest -Force }
          Compress-Archive -Path "$source/*" -DestinationPath $dest
```

**Step 5: Upload archived assets to the GitHub Release**

Use `softprops/action-gh-release@v2` in the publish job after the archive step:

```yaml
      - name: Upload release asset
        uses: softprops/action-gh-release@v2
        with:
          files: artifacts/release/NovaTerminal-${{ matrix.rid }}-${{ github.ref_name }}.zip
```

This workflow assumes the release tag already exists because it is triggered by tag push.

**Step 6: Run targeted validation**

Run:

```bash
rg -n "name: Release|push:|tags:|macos-latest|PublishAot=true|softprops/action-gh-release" .github/workflows/release.yml
git diff --check
```

Expected:

- release workflow triggers on `v*`
- matrices include Windows, Linux, and macOS
- AOT publish command is present
- release asset upload action is present
- `git diff --check` prints no whitespace errors

**Step 7: Commit**

```bash
git add .github/workflows/release.yml
git commit -m "ci: publish release artifacts from tags"
```

### Task 3: Update Public Docs For AOT And Release Assets

**Files:**
- Modify: `README.md`

**Step 1: Keep the new Native AOT section and extend it for releases**

In the existing build/test area, ensure the README states:

- Native AOT is configured in `src/NovaTerminal.App/NovaTerminal.App.csproj`
- the project supports `win-x64`, `linux-x64`, and `osx-arm64` publish targets
- tag-driven GitHub releases attach publish bundles for those targets

Add a short release note such as:

```md
Tagged releases publish Native AOT bundles for `win-x64`, `linux-x64`, and `osx-arm64` to the corresponding GitHub Release.
```

**Step 2: Review wording against the actual workflows**

Run:

```bash
rg -n "Native AOT|GitHub Release|win-x64|linux-x64|osx-arm64" README.md .github/workflows/ci.yml .github/workflows/release.yml
git diff -- README.md
```

Expected:

- README language matches the CI and release workflows
- no stale claim says binaries are unavailable once release assets are being produced

**Step 3: Commit**

```bash
git add README.md
git commit -m "docs: document AOT release publishing"
```

### Task 4: Final Verification And Integration Check

**Files:**
- Modify: `.github/workflows/ci.yml`
- Create: `.github/workflows/release.yml`
- Modify: `README.md`

**Step 1: Review the final diff**

Run:

```bash
git diff -- .github/workflows/ci.yml .github/workflows/release.yml README.md
```

Expected:

- PR CI changes are limited to the balanced lanes
- release workflow is tag-driven and separate from ordinary PR validation
- README reflects both AOT local publish and release asset behavior

**Step 2: Run whitespace and status checks**

Run:

```bash
git diff --check
git status --short
```

Expected:

- no whitespace or patch-format issues
- only the intended workflow/docs files are modified

**Step 3: Commit final cleanup if needed**

If any small wording or workflow cleanup is required after review:

```bash
git add .github/workflows/ci.yml .github/workflows/release.yml README.md
git commit -m "chore: finalize CI and release workflow cleanup"
```

**Step 4: Publish**

```bash
git push origin HEAD
gh pr create --title "[codex] rebalance CI and add release publishing" --body-file <prepared-body-file>
```

Expected:

- PR clearly separates balanced PR CI changes from release publishing additions
- reviewers can validate the new release path without guessing at scope

---
