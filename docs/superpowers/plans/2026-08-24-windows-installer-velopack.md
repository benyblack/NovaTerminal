# Windows Installer + Auto-Update (Velopack) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Publish a Windows installer (`Setup.exe`) plus a delta update feed as GitHub release assets, and have installed builds check for updates in the background and apply them on the user's next restart.

**Architecture:** Velopack's `vpk` CLI wraps the release workflow's existing NativeAOT `dotnet publish` output into an installer and full/delta `.nupkg` packages, attached to the same GitHub release that already carries the portable zips. In the app, `VelopackApp.Build().Run()` runs first in `Main` to service install/update hooks, and a UI-free `UpdateCoordinator` (over an `IUpdateService` seam) decides when to check, swallows failures, and raises a "restart to update" toast. The portable zip and `packaging/winget/` are untouched.

**Tech Stack:** .NET 10 / C#, Avalonia 12, Velopack 1.2.0 (NuGet + `vpk` global tool), xunit v3, GitHub Actions, PowerShell.

**Design doc:** [docs/superpowers/specs/2026-08-24-windows-installer-velopack-design.md](../specs/2026-08-24-windows-installer-velopack-design.md) — read it before Task 1.

**Tracking issue:** [#91](https://github.com/benyblack/NovaTerminal/issues/91). This plan covers the installer + auto-update half only; **code signing stays open on #91.**

## Global Constraints

- **Never run raw `dotnet build` / `dotnet test`.** Use `scripts/build.ps1 <args…>` (PowerShell) or `scripts/build.sh <args…>`. Raw invocations leave MSBuild daemons holding the pipe and hang the harness. See `CLAUDE.md`.
- **Run test projects individually, never the whole solution** — a full-solution test run takes 20–30 minutes because of headless Avalonia.
- **Velopack version is pinned to `1.2.0`**, and the `vpk` tool version must equal the NuGet package version. Pin the NuGet version in `Directory.Packages.props` (the repo uses central package management: `ManagePackageVersionsCentrally=true`), never in a csproj.
- **`tests/NovaTerminal.App.Tests` also runs on ubuntu in CI.** No test added by this plan may require Windows, a real Velopack install, or network access.
- **`tests/NovaTerminal.Architecture.Tests` builds with `TreatWarningsAsErrors`.** Constant array arguments trip CA1861 — hoist any array literal used in an assertion into a `private static readonly string[]` field, matching that file's existing style.
- **Repo URL for the update feed:** `https://github.com/benyblack/NovaTerminal`
- **Never rename Velopack's `*.nupkg` or `releases.win.json`.** `GithubSource` resolves them by name; renaming breaks every client's update check. Only `Setup.exe` may be renamed.
- **Existing release assets are frozen.** `NovaTerminal-win-x64-<tag>.zip` keeps its exact name and contents so `packaging/winget/` needs no manifest change.
- `tests/NovaTerminal.App.Tests` has `<Using Include="Xunit" />` globally — test files need no `using Xunit;`.

---

### Task 1: Velopack reference + startup hook + NativeAOT verification (spike)

This is the spike the design calls for. Velopack's docs never mention NativeAOT and this app publishes with `PublishAot=true`, so nothing else in this plan is safe to build until an AOT publish with a real Velopack call path is proven. A bare `PackageReference` proves nothing — the trim/AOT analyzers only report on *reachable* code — so this task lands the reference **and** its first real call site together.

**Files:**
- Modify: `Directory.Packages.props` (add `PackageVersion`)
- Modify: `src/NovaTerminal.App/NovaTerminal.App.csproj` (add `PackageReference`)
- Modify: `src/NovaTerminal.App/Program.cs:19-25` (hook as first statement in `Main`)
- Test: `tests/NovaTerminal.Architecture.Tests/ProjectFileLayeringTests.cs` (append one fact)

**Interfaces:**
- Consumes: nothing.
- Produces: the `Velopack` package is available to `NovaTerminal.App` only; `VelopackApp.Build().Run()` has already run by the time any other code in `Main` executes.

- [ ] **Step 1: Write the failing architecture test**

Append to `tests/NovaTerminal.Architecture.Tests/ProjectFileLayeringTests.cs`. Note the hoisted field — this project treats warnings as errors and CA1861 fires on inline array arguments.

```csharp
    // Same CA1861 reasoning as VtOnly above.
    private static readonly string[] ProjectsAllowedToReferenceVelopack =
        ["src/NovaTerminal.App/NovaTerminal.App.csproj"];

    /// <summary>
    /// Velopack is the Windows install/update host. It is referenced for exactly one reason -
    /// <c>VelopackApp.Build().Run()</c> and the update seam in <c>NovaTerminal.App/Update</c> - and
    /// must not spread. A second project taking the reference would put install-location and
    /// restart-the-process concerns behind a library boundary where nothing can see them, and would
    /// drag an unsigned-updater dependency into layers that are meant to be host-agnostic.
    /// </summary>
    [Fact]
    public void Velopack_is_referenced_only_by_the_App()
    {
        var offenders = Directory
            .EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.csproj", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(RepoRoot(), p).Replace('\\', '/'))
            .Where(rel => PackageReferences(rel).Any(
                p => p.Equals("Velopack", StringComparison.OrdinalIgnoreCase)))
            .Where(rel => !ProjectsAllowedToReferenceVelopack.Contains(rel))
            .ToArray();

        Assert.Empty(offenders);
    }
```

- [ ] **Step 2: Run it and watch it pass vacuously, then confirm it can fail**

```bash
scripts/build.ps1 test tests/NovaTerminal.Architecture.Tests --filter "FullyQualifiedName~Velopack_is_referenced_only_by_the_App"
```

Expected: PASS (no project references Velopack yet). This test is a guard, not a red-green driver — so prove it *can* fail: temporarily add `<PackageReference Include="Velopack" />` to `src/NovaTerminal.VT/NovaTerminal.VT.csproj`, re-run, confirm FAIL with `NovaTerminal.VT/NovaTerminal.VT.csproj` listed, then revert that edit.

- [ ] **Step 3: Pin the package version**

In `Directory.Packages.props`, add a new group after the MCP block:

```xml
    <!-- Windows install + auto-update (#91). The vpk CLI version used in
         .github/workflows/release.yml MUST equal this version. -->
    <PackageVersion Include="Velopack" Version="1.2.0" />
```

- [ ] **Step 4: Reference it from the app**

In `src/NovaTerminal.App/NovaTerminal.App.csproj`, add to the first `ItemGroup` that holds `PackageReference` items (after the `SkiaSharp.NativeAssets.Linux` line):

```xml
    <!-- Windows installer + auto-update (#91). Version is pinned centrally in
         Directory.Packages.props and must match the vpk tool version in release.yml. -->
    <PackageReference Include="Velopack" />
```

- [ ] **Step 5: Add the startup hook as the first statement in `Main`**

In `src/NovaTerminal.App/Program.cs`, add the `using` and make the hook the first thing inside the `try`. Ordering is load-bearing: Velopack re-invokes this executable with its own hook arguments during install, update and uninstall, and the `IsSupportedCliMode` checks below must never see or swallow them.

```csharp
using Velopack;
```

```csharp
        try
        {
            // Velopack install/update/uninstall hooks re-invoke this exe with their own
            // arguments and expect to be serviced before anything else happens - including
            // before our own CLI-mode dispatch below, which would otherwise treat a hook
            // argument as an unrecognised command line. Run() returns immediately for a
            // normal launch, and exits the process for a hook invocation. Harmless when the
            // app was not installed by Velopack (portable zip, winget, dev runs).
            VelopackApp.Build().Run();

            if (VtReportCommand.IsSupportedCliMode(args))
```

- [ ] **Step 6: Build and run the architecture test**

```bash
scripts/build.ps1 build src/NovaTerminal.App
```

```bash
scripts/build.ps1 test tests/NovaTerminal.Architecture.Tests --filter "FullyQualifiedName~Velopack_is_referenced_only_by_the_App"
```

Expected: build succeeds, test PASSES.

- [ ] **Step 7: The actual spike — publish NativeAOT and read every warning**

```bash
scripts/build.ps1 publish src/NovaTerminal.App/NovaTerminal.App.csproj -c Release -r win-x64 --self-contained true -p:PublishAot=true -p:SkipCliShim=true -o artifacts/publish/aot-spike
```

Expected: publish **succeeds**. Then scan the output for trim/AOT diagnostics:

```bash
scripts/build.ps1 publish src/NovaTerminal.App/NovaTerminal.App.csproj -c Release -r win-x64 --self-contained true -p:PublishAot=true -p:SkipCliShim=true -o artifacts/publish/aot-spike 2>&1 | grep -E "IL2026|IL2104|IL3050|IL3053|warning IL" | sort -u
```

Any `IL2026` (reflection in a trimmed app) or `IL3050` (dynamic code in AOT) attributed to a `Velopack.*` call is the finding this spike exists to surface.

- [ ] **Step 8: Smoke-test the published exe**

Run `artifacts/publish/aot-spike/NovaTerminal.exe`. Expected: the app starts normally and behaves exactly as before — `VelopackApp.Run()` is a no-op outside an install. Then confirm the CLI paths still work, because Step 5 inserted a statement ahead of them:

```bash
artifacts/publish/aot-spike/NovaTerminal.exe --help
```

- [ ] **Step 9: Decide, and record the decision**

Apply the design's fallback ladder:

1. No Velopack-attributed IL warnings and the exe runs → **proceed with the plan as written.**
2. Warnings confined to APIs this plan does not call → proceed, and note in the commit message exactly which APIs are off-limits.
3. Warnings on `UpdateManager` / `GithubSource` / `CheckForUpdatesAsync` — the APIs Task 4 needs → **stop and report before writing Task 4.** The fallback is Setup.exe from `vpk pack` alone (Task 2 needs no in-app SDK at all) with update checks driven through the bundled `Update.exe` CLI. That changes Task 4's implementation and must be re-planned, not improvised.

While the AOT publish is open, also confirm one API fact the plan depends on: whether `UpdateManager.CurrentVersion` reads Velopack's own install metadata or the assembly's informational version. Task 2's version wiring is worth doing either way, but the answer decides whether it is *correctness* or *hygiene*, and Task 4's logging should name the right source.

- [ ] **Step 10: Commit**

```bash
git add Directory.Packages.props src/NovaTerminal.App/NovaTerminal.App.csproj src/NovaTerminal.App/Program.cs tests/NovaTerminal.Architecture.Tests/ProjectFileLayeringTests.cs
git commit -m "feat(update): reference Velopack and service its startup hooks

Velopack's install/update/uninstall hooks re-invoke the exe with their own
arguments, so VelopackApp.Build().Run() has to precede our CLI-mode dispatch
in Main or a hook argument reaches VtReportCommand instead.

Verified against a win-x64 NativeAOT publish, which the design flagged as the
one real unknown (Velopack documents no AOT support). An architecture test
pins the package reference to NovaTerminal.App.

Refs #91"
```

---

### Task 2: Publish the installer and update feed from `release.yml`

Packaging comes before the in-app update code because the in-app path cannot be verified end-to-end until a release actually carries a feed. This task needs no Velopack code in the app at all — which is also why it survives Task 1's worst-case fallback.

**Files:**
- Modify: `.github/workflows/release.yml` (`release_metadata` outputs; `publish_aot` steps)
- Modify: `packaging/winget/README.md` (note the new installer next to the portable manifest)
- Modify: `README.md` (installer in the install instructions)

**Interfaces:**
- Consumes: nothing from Task 1.
- Produces: release assets `NovaTerminal-Setup-win-x64-<tag>.exe`, `NovaTerminal-<version>-full.nupkg`, `NovaTerminal-<version>-delta.nupkg` (second and later releases only), `releases.win.json`. A new workflow output `needs.release_metadata.outputs.release_version` — the tag with its leading `v` stripped.

- [ ] **Step 1: Add a tag-without-`v` output to `release_metadata`**

In `.github/workflows/release.yml`, replace the `release_metadata` job's `outputs` block and its `Resolve tag and checkout ref` step body:

```yaml
    outputs:
      release_tag: ${{ steps.meta.outputs.release_tag }}
      release_version: ${{ steps.meta.outputs.release_version }}
      checkout_ref: ${{ steps.meta.outputs.checkout_ref }}
    steps:
      - name: Resolve tag and checkout ref
        id: meta
        shell: bash
        run: |
          if [[ "${{ github.event_name }}" == "push" ]]; then
            tag="${{ github.ref_name }}"
            echo "release_tag=$tag" >> "$GITHUB_OUTPUT"
            echo "checkout_ref=${{ github.ref }}" >> "$GITHUB_OUTPUT"
          else
            tag="${{ inputs.tag_name }}"
            echo "release_tag=$tag" >> "$GITHUB_OUTPUT"
            echo "checkout_ref=${{ inputs.target_commitish }}" >> "$GITHUB_OUTPUT"
          fi
          # Velopack versions are SemVer without a leading 'v'; the tag has one.
          echo "release_version=${tag#v}" >> "$GITHUB_OUTPUT"
```

- [ ] **Step 2: Stamp the published build with the release version**

In the `publish_aot` job, replace the `Publish AOT` step's `run:` so the executable reports the version being released rather than the static `0.4.0` in `Directory.Build.props`. `AssemblyVersion`/`FileVersion` are left alone deliberately — they must be four-part numerics and a prerelease tag like `v0.5.0-beta.1` would not parse.

```yaml
      - name: Publish AOT
        env:
          SKIP_RUST_NATIVE_BUILD: "1"
        run: dotnet publish src/NovaTerminal.App/NovaTerminal.App.csproj -c ${{ env.CONFIGURATION }} -r ${{ matrix.rid }} --self-contained true -p:PublishAot=true -p:SkipCliShim=true -p:Version=${{ needs.release_metadata.outputs.release_version }} -p:InformationalVersion=${{ needs.release_metadata.outputs.release_version }} -o artifacts/publish/${{ matrix.rid }}
```

- [ ] **Step 3: Install the pinned `vpk` tool (win-x64 only)**

Add after the existing `Archive bundle` step in `publish_aot`:

```yaml
      # ---- Windows installer + update feed (#91). win-x64 only; the other two RIDs
      # ---- keep shipping portable zips exactly as before.
      - name: Install vpk
        if: matrix.rid == 'win-x64'
        run: dotnet tool install -g vpk --version 1.2.0
```

- [ ] **Step 4: Fetch the previous release so a delta can be built**

Velopack builds a delta only when the previous full `.nupkg` is present in the output directory. A first run — or any fetch failure — must degrade to full-only output rather than failing the release, hence `continue-on-error`.

```yaml
      - name: Download previous Velopack release (for delta generation)
        if: matrix.rid == 'win-x64'
        continue-on-error: true
        shell: pwsh
        env:
          GH_TOKEN: ${{ secrets.GITHUB_TOKEN }}
        run: |
          New-Item -ItemType Directory -Force -Path artifacts/velopack | Out-Null
          # First release ever, or no prior Velopack assets: nothing to download, and a
          # full-only release is the correct outcome rather than a failed one.
          vpk download github --repoUrl https://github.com/benyblack/NovaTerminal --token $env:GH_TOKEN --outputDir artifacts/velopack
```

- [ ] **Step 5: Pack the installer**

```yaml
      - name: Pack Windows installer (Velopack)
        if: matrix.rid == 'win-x64'
        shell: pwsh
        run: |
          $ver = "${{ needs.release_metadata.outputs.release_version }}"
          $tag = "${{ needs.release_metadata.outputs.release_tag }}"
          vpk pack `
            --packId NovaTerminal `
            --packVersion $ver `
            --packDir artifacts/publish/win-x64 `
            --mainExe NovaTerminal.exe `
            --packTitle NovaTerminal `
            --packAuthors benyblack `
            --icon src/NovaTerminal.App/Assets/nova_icon.ico `
            --outputDir artifacts/velopack `
            --noPortable
          # --noPortable: the release already ships NovaTerminal-win-x64-<tag>.zip, and two
          # near-identical zips on the releases page is a support question waiting to happen.

          # The installer's file name is ours to choose - nothing resolves it by name - so
          # give it one that reads clearly next to the zips. Matched by glob because vpk
          # includes the channel in the name it produces.
          $setup = Get-ChildItem artifacts/velopack -Filter "*Setup.exe" | Select-Object -First 1
          if (-not $setup) { throw "vpk pack produced no Setup.exe" }
          Move-Item $setup.FullName "artifacts/velopack/NovaTerminal-Setup-win-x64-$tag.exe"

          Write-Host "Velopack output:"
          Get-ChildItem artifacts/velopack | Select-Object Name, Length | Format-Table
```

- [ ] **Step 6: Upload only this release's feed files**

Step 4 deliberately drops the *previous* release's `.nupkg` into the same directory, so a `*.nupkg` glob here would re-upload old packages — and Velopack requires each GitHub release to carry exactly one full and one delta package. Scope the paths to this version. `releases.win.json` is uploaded as produced: `GithubSource` resolves it by that exact name. The delta is absent on a first release, which `softprops/action-gh-release` tolerates (`fail_on_unmatched_files` defaults to false).

```yaml
      - name: Upload Windows installer and update feed
        if: matrix.rid == 'win-x64'
        uses: softprops/action-gh-release@v2
        with:
          tag_name: ${{ needs.release_metadata.outputs.release_tag }}
          files: |
            artifacts/velopack/NovaTerminal-Setup-win-x64-${{ needs.release_metadata.outputs.release_tag }}.exe
            artifacts/velopack/NovaTerminal-${{ needs.release_metadata.outputs.release_version }}-full.nupkg
            artifacts/velopack/NovaTerminal-${{ needs.release_metadata.outputs.release_version }}-delta.nupkg
            artifacts/velopack/releases.win.json
```

- [ ] **Step 7: Verify the workflow parses and the win-only guards are right**

There is no test harness for workflow YAML, so verify by reading and by tooling:

```bash
rtk gh workflow view release.yml
```

Then re-read the diff and confirm all four of these hold: every new step carries `if: matrix.rid == 'win-x64'`; the `linux-x64` and `osx-arm64` legs gained nothing but the version properties in Step 2; `Archive bundle` and its upload step are untouched; and no step renames a `.nupkg` or `releases.win.json`.

```bash
rtk git diff .github/workflows/release.yml
```

- [ ] **Step 8: Update the docs that describe how NovaTerminal is installed**

In `packaging/winget/README.md`, the opening paragraph currently states "there is no signed installer" as the reason for the portable manifest. That reasoning survives — the installer is unsigned — but the flat claim no longer does. Replace the first paragraph of the intro section's second sentence with:

```markdown
The Windows release ships a self-contained, AOT-compiled **zip** *and*, since #91's packaging
half landed, an unsigned Velopack installer (`NovaTerminal-Setup-win-x64-<tag>.exe`). Neither is
code-signed. The winget manifest deliberately packages the **zip** as a **portable** app
(`InstallerType: zip`, `NestedInstallerType: portable`) rather than pointing at the installer:
that keeps winget's install out of Velopack's updater's way, so the two never both believe they
own the install. Once accepted into the community repo it installs with:
```

In `README.md`, add the installer to the install instructions alongside the existing zip/winget options, worded so nobody expects a signed binary:

```markdown
**Windows**

- **Installer** — download `NovaTerminal-Setup-win-x64-<tag>.exe` from the
  [latest release](https://github.com/benyblack/NovaTerminal/releases/latest). Installs per-user
  (no admin prompt), adds a Start Menu entry, and updates itself in the background.
- **Portable** — download `NovaTerminal-win-x64-<tag>.zip` and extract it anywhere. No updater.
- **winget** — `winget install benyblack.NovaTerminal` (portable package).

The installer and the executables are **not code-signed yet** ([#91](https://github.com/benyblack/NovaTerminal/issues/91)),
so SmartScreen will warn on first run. Choose *More info → Run anyway*.
```

- [ ] **Step 9: Commit**

```bash
git add .github/workflows/release.yml packaging/winget/README.md README.md
git commit -m "feat(release): publish a Windows installer and Velopack update feed

Wraps the existing win-x64 AOT publish output with vpk pack, adding
NovaTerminal-Setup-win-x64-<tag>.exe plus the full/delta nupkgs and
releases.win.json to the release. The portable zip keeps its exact name and
contents, so packaging/winget needs no manifest change.

Two details that are easy to get wrong: the previous release's nupkg is
downloaded into the same directory to make a delta possible, so the upload
globs are version-scoped rather than *.nupkg (a GitHub release must carry
exactly one full and one delta); and the publish is now stamped with the
tag-derived version instead of the static 0.4.0 in Directory.Build.props.

Refs #91"
```

---

### Task 3: `UpdateCoordinator` — the decision logic, unit-tested

The coordinator holds every rule the design states (honour the settings toggle, never surface failures, no-op when not installed) and knows nothing about Velopack or Avalonia. That is what makes the rules testable on ubuntu with no network and no install.

**Files:**
- Create: `src/NovaTerminal.App/Update/IUpdateService.cs`
- Create: `src/NovaTerminal.App/Update/UpdateCoordinator.cs`
- Test: `tests/NovaTerminal.App.Tests/Update/UpdateCoordinatorTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces:
  - `NovaTerminal.Update.IUpdateService` — `bool IsSupported { get; }`, `Task<UpdateAvailability> CheckAndDownloadAsync(CancellationToken)`, `void ApplyAndRestart()`
  - `readonly record struct UpdateAvailability(bool HasUpdate, string? Version)`
  - `enum UpdateCheckOutcome { Unsupported, Disabled, UpToDate, Failed, UpdateReady }`
  - `sealed class UpdateCoordinator(IUpdateService service, Func<bool> automaticChecksEnabled, Action<string> onUpdateReady, Action<string> log)` with `Task<UpdateCheckOutcome> RunAutomaticCheckAsync(CancellationToken ct = default)`, `Task<UpdateCheckOutcome> RunManualCheckAsync(CancellationToken ct = default)`, `bool IsUpdateStaged { get; }`, `string? StagedVersion { get; }`, `void ApplyStagedUpdate()`

- [ ] **Step 1: Write the failing tests**

Create `tests/NovaTerminal.App.Tests/Update/UpdateCoordinatorTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NovaTerminal.Update;

namespace NovaTerminal.AppTests.Update;

public class UpdateCoordinatorTests
{
    private sealed class FakeUpdateService : IUpdateService
    {
        public bool IsSupported { get; set; } = true;
        public UpdateAvailability Result { get; set; } = new(false, null);
        public Exception? Throw { get; set; }
        public int CheckCount { get; private set; }
        public int ApplyCount { get; private set; }

        public Task<UpdateAvailability> CheckAndDownloadAsync(CancellationToken ct)
        {
            CheckCount++;
            if (Throw != null) throw Throw;
            return Task.FromResult(Result);
        }

        public void ApplyAndRestart() => ApplyCount++;
    }

    private sealed class Harness
    {
        public FakeUpdateService Service { get; } = new();
        public bool AutomaticChecksEnabled { get; set; } = true;
        public List<string> Ready { get; } = [];
        public List<string> Log { get; } = [];

        public UpdateCoordinator Build() => new(
            Service,
            () => AutomaticChecksEnabled,
            version => Ready.Add(version),
            message => Log.Add(message));
    }

    [Fact]
    public async Task Not_a_velopack_install_reports_unsupported_and_never_checks()
    {
        var harness = new Harness();
        harness.Service.IsSupported = false;

        var outcome = await harness.Build().RunAutomaticCheckAsync();

        Assert.Equal(UpdateCheckOutcome.Unsupported, outcome);
        Assert.Equal(0, harness.Service.CheckCount);
        Assert.Empty(harness.Ready);
    }

    [Fact]
    public async Task Automatic_check_is_skipped_when_the_setting_is_off()
    {
        var harness = new Harness { AutomaticChecksEnabled = false };

        var outcome = await harness.Build().RunAutomaticCheckAsync();

        Assert.Equal(UpdateCheckOutcome.Disabled, outcome);
        Assert.Equal(0, harness.Service.CheckCount);
    }

    [Fact]
    public async Task Manual_check_runs_even_when_the_setting_is_off()
    {
        var harness = new Harness { AutomaticChecksEnabled = false };

        var outcome = await harness.Build().RunManualCheckAsync();

        Assert.Equal(UpdateCheckOutcome.UpToDate, outcome);
        Assert.Equal(1, harness.Service.CheckCount);
    }

    [Fact]
    public async Task No_update_available_raises_nothing()
    {
        var harness = new Harness();
        harness.Service.Result = new UpdateAvailability(false, null);

        var coordinator = harness.Build();
        var outcome = await coordinator.RunAutomaticCheckAsync();

        Assert.Equal(UpdateCheckOutcome.UpToDate, outcome);
        Assert.Empty(harness.Ready);
        Assert.False(coordinator.IsUpdateStaged);
    }

    [Fact]
    public async Task A_throwing_service_is_logged_and_swallowed()
    {
        var harness = new Harness();
        harness.Service.Throw = new InvalidOperationException("github is down");

        var coordinator = harness.Build();
        var outcome = await coordinator.RunAutomaticCheckAsync();

        Assert.Equal(UpdateCheckOutcome.Failed, outcome);
        Assert.Empty(harness.Ready);
        Assert.False(coordinator.IsUpdateStaged);
        Assert.Contains(harness.Log, m => m.Contains("github is down", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_available_update_is_staged_and_announced_once()
    {
        var harness = new Harness();
        harness.Service.Result = new UpdateAvailability(true, "0.5.0");

        var coordinator = harness.Build();
        var outcome = await coordinator.RunAutomaticCheckAsync();

        Assert.Equal(UpdateCheckOutcome.UpdateReady, outcome);
        Assert.True(coordinator.IsUpdateStaged);
        Assert.Equal("0.5.0", coordinator.StagedVersion);
        Assert.Equal(["0.5.0"], harness.Ready);
    }

    [Fact]
    public async Task A_second_check_does_not_re_announce_an_already_staged_update()
    {
        var harness = new Harness();
        harness.Service.Result = new UpdateAvailability(true, "0.5.0");
        var coordinator = harness.Build();

        await coordinator.RunAutomaticCheckAsync();
        var second = await coordinator.RunAutomaticCheckAsync();

        Assert.Equal(UpdateCheckOutcome.UpdateReady, second);
        Assert.Equal(["0.5.0"], harness.Ready);
    }

    [Fact]
    public async Task Applying_a_staged_update_delegates_to_the_service()
    {
        var harness = new Harness();
        harness.Service.Result = new UpdateAvailability(true, "0.5.0");
        var coordinator = harness.Build();
        await coordinator.RunAutomaticCheckAsync();

        coordinator.ApplyStagedUpdate();

        Assert.Equal(1, harness.Service.ApplyCount);
    }

    [Fact]
    public void Applying_with_nothing_staged_is_a_no_op()
    {
        var harness = new Harness();

        harness.Build().ApplyStagedUpdate();

        Assert.Equal(0, harness.Service.ApplyCount);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~UpdateCoordinatorTests"
```

Expected: FAIL at compile time — `NovaTerminal.Update` does not exist.

- [ ] **Step 3: Write the seam**

Create `src/NovaTerminal.App/Update/IUpdateService.cs`:

```csharp
using System.Threading;
using System.Threading.Tasks;

namespace NovaTerminal.Update
{
    /// <summary>
    /// What an update host can do, expressed without reference to Velopack, Avalonia or the
    /// network. <see cref="UpdateCoordinator"/> holds the policy; this holds the mechanism.
    /// </summary>
    /// <remarks>
    /// The seam exists for two reasons. It keeps every rule in <see cref="UpdateCoordinator"/>
    /// testable on a machine with no Velopack install and no network - which includes the ubuntu
    /// leg of CI, where <c>App.Tests</c> also runs. And it confines the Velopack API surface to a
    /// single implementation, which is what makes the design's AOT fallback (drive updates through
    /// the bundled Update.exe instead of the in-process SDK) a one-file change.
    /// </remarks>
    public interface IUpdateService
    {
        /// <summary>
        /// False when this process was not installed by Velopack - a portable zip, a winget
        /// portable install, or a dev run. Those must never see update UI or errors.
        /// </summary>
        bool IsSupported { get; }

        /// <summary>
        /// Checks for a newer release and, if there is one, downloads it. May throw; the caller
        /// is responsible for deciding that a failed update check is not the user's problem.
        /// </summary>
        Task<UpdateAvailability> CheckAndDownloadAsync(CancellationToken ct);

        /// <summary>Applies the downloaded update and restarts the app.</summary>
        void ApplyAndRestart();
    }

    /// <param name="HasUpdate">True when a newer release was found and downloaded.</param>
    /// <param name="Version">The new version, for display. Null when <paramref name="HasUpdate"/> is false.</param>
    public readonly record struct UpdateAvailability(bool HasUpdate, string? Version);
}
```

- [ ] **Step 4: Write the coordinator**

Create `src/NovaTerminal.App/Update/UpdateCoordinator.cs`:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NovaTerminal.Update
{
    /// <summary>Why a check ended the way it did. Returned for tests and logging, not for display.</summary>
    public enum UpdateCheckOutcome
    {
        /// <summary>Not a Velopack install - portable zip, winget, or a dev run.</summary>
        Unsupported,

        /// <summary>Automatic checks are switched off in settings.</summary>
        Disabled,

        /// <summary>Checked successfully; already on the newest release.</summary>
        UpToDate,

        /// <summary>The check or download threw. Logged, never surfaced by an automatic check.</summary>
        Failed,

        /// <summary>A newer release is downloaded and waiting for a restart.</summary>
        UpdateReady,
    }

    /// <summary>
    /// Decides when to check for updates and what the rest of the app is told about the result.
    /// UI-free and Velopack-free by design - see <see cref="IUpdateService"/>.
    /// </summary>
    public sealed class UpdateCoordinator
    {
        private readonly IUpdateService _service;
        private readonly Func<bool> _automaticChecksEnabled;
        private readonly Action<string> _onUpdateReady;
        private readonly Action<string> _log;

        public UpdateCoordinator(
            IUpdateService service,
            Func<bool> automaticChecksEnabled,
            Action<string> onUpdateReady,
            Action<string> log)
        {
            _service = service;
            _automaticChecksEnabled = automaticChecksEnabled;
            _onUpdateReady = onUpdateReady;
            _log = log;
        }

        /// <summary>True once a downloaded update is waiting for a restart.</summary>
        public bool IsUpdateStaged => StagedVersion != null;

        /// <summary>The staged version, or null when nothing is staged.</summary>
        public string? StagedVersion { get; private set; }

        /// <summary>The startup check. Honours the settings toggle and never surfaces a failure.</summary>
        public Task<UpdateCheckOutcome> RunAutomaticCheckAsync(CancellationToken ct = default)
        {
            if (!_automaticChecksEnabled())
            {
                return Task.FromResult(UpdateCheckOutcome.Disabled);
            }

            return RunCheckAsync(ct);
        }

        /// <summary>
        /// The user asked. Deliberately ignores the automatic-checks toggle: that setting governs
        /// background traffic, not whether the user may ask a direct question.
        /// </summary>
        public Task<UpdateCheckOutcome> RunManualCheckAsync(CancellationToken ct = default)
            => RunCheckAsync(ct);

        private async Task<UpdateCheckOutcome> RunCheckAsync(CancellationToken ct)
        {
            if (!_service.IsSupported)
            {
                return UpdateCheckOutcome.Unsupported;
            }

            UpdateAvailability availability;
            try
            {
                availability = await _service.CheckAndDownloadAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutdown during a check is not a failure worth logging.
                return UpdateCheckOutcome.Failed;
            }
            catch (Exception ex)
            {
                // An unreachable GitHub, a rate limit or a malformed feed must cost the user
                // nothing. The caller decides whether to tell them (a manual check does; the
                // startup check does not).
                _log("Update check failed: " + ex);
                return UpdateCheckOutcome.Failed;
            }

            if (!availability.HasUpdate)
            {
                return UpdateCheckOutcome.UpToDate;
            }

            var version = availability.Version ?? string.Empty;

            // Announce a given staged version once. Without this, a manual check after the
            // startup check re-raises the toast the user just dismissed.
            if (StagedVersion != version)
            {
                StagedVersion = version;
                _onUpdateReady(version);
            }

            return UpdateCheckOutcome.UpdateReady;
        }

        /// <summary>Restarts into the staged update. No-op when nothing is staged.</summary>
        public void ApplyStagedUpdate()
        {
            if (!IsUpdateStaged)
            {
                return;
            }

            _service.ApplyAndRestart();
        }
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~UpdateCoordinatorTests"
```

Expected: all 9 tests PASS.

- [ ] **Step 6: Commit**

```bash
git add src/NovaTerminal.App/Update/IUpdateService.cs src/NovaTerminal.App/Update/UpdateCoordinator.cs tests/NovaTerminal.App.Tests/Update/UpdateCoordinatorTests.cs
git commit -m "feat(update): add the update coordinator behind a Velopack-free seam

Every rule the design states - honour the settings toggle, never surface a
failed background check, do nothing at all when this is not a Velopack
install, announce a staged version once - lives here, with no reference to
Velopack or Avalonia. That is what lets the rules be tested on the ubuntu leg
of CI, where App.Tests also runs and neither a Velopack install nor network
access exists.

Refs #91"
```

---

### Task 4: `VelopackUpdateService` — the real implementation

**Files:**
- Create: `src/NovaTerminal.App/Update/VelopackUpdateService.cs`
- Test: `tests/NovaTerminal.App.Tests/Update/VelopackUpdateServiceTests.cs`

**Interfaces:**
- Consumes: `IUpdateService`, `UpdateAvailability` from Task 3.
- Produces: `sealed class VelopackUpdateService : IUpdateService` with `VelopackUpdateService(string repoUrl, Action<string> log)` and `const string DefaultRepoUrl = "https://github.com/benyblack/NovaTerminal"`.

> **If Task 1 Step 9 landed on outcome 3** (AOT warnings on `UpdateManager` / `GithubSource`), stop: this task's implementation is the one the fallback replaces, and it needs re-planning against `Update.exe` rather than the in-process SDK.

- [ ] **Step 1: Write the failing test**

Only one behaviour here is testable without a real install — and it happens to be the one that protects every non-installed user. Create `tests/NovaTerminal.App.Tests/Update/VelopackUpdateServiceTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NovaTerminal.Update;

namespace NovaTerminal.AppTests.Update;

public class VelopackUpdateServiceTests
{
    /// <summary>
    /// The test host is never a Velopack install, on any OS. This is the guard that keeps
    /// portable-zip, winget and dev runs from ever reaching the network or showing update UI,
    /// so it is worth pinning even though it looks tautological here.
    /// </summary>
    [Fact]
    public void Is_not_supported_when_the_process_is_not_a_velopack_install()
    {
        var service = new VelopackUpdateService(VelopackUpdateService.DefaultRepoUrl, _ => { });

        Assert.False(service.IsSupported);
    }

    [Fact]
    public async Task Check_reports_no_update_when_unsupported_instead_of_throwing()
    {
        var log = new List<string>();
        var service = new VelopackUpdateService(VelopackUpdateService.DefaultRepoUrl, log.Add);

        var availability = await service.CheckAndDownloadAsync(CancellationToken.None);

        Assert.False(availability.HasUpdate);
        Assert.Null(availability.Version);
    }

    [Fact]
    public void Apply_is_a_no_op_when_unsupported()
    {
        var service = new VelopackUpdateService(VelopackUpdateService.DefaultRepoUrl, _ => { });

        // Must not throw, and must certainly not restart the test host.
        service.ApplyAndRestart();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~VelopackUpdateServiceTests"
```

Expected: FAIL at compile time — `VelopackUpdateService` does not exist.

- [ ] **Step 3: Write the implementation**

Create `src/NovaTerminal.App/Update/VelopackUpdateService.cs`:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace NovaTerminal.Update
{
    /// <summary>
    /// <see cref="IUpdateService"/> over Velopack, reading releases straight off this repo's
    /// GitHub releases.
    /// </summary>
    /// <remarks>
    /// This is the only file in the app that names a Velopack type besides the
    /// <c>VelopackApp.Build().Run()</c> hook in <c>Program.Main</c>.
    /// </remarks>
    public sealed class VelopackUpdateService : IUpdateService
    {
        public const string DefaultRepoUrl = "https://github.com/benyblack/NovaTerminal";

        private readonly Action<string> _log;
        private readonly UpdateManager _manager;
        private UpdateInfo? _downloaded;

        public VelopackUpdateService(string repoUrl, Action<string> log)
        {
            _log = log;

            // prerelease: false - a prerelease tag must never pull stable users onto an
            // unfinished build. accessToken: null - the repo is public, and an unauthenticated
            // check is subject to GitHub's anonymous rate limit, which a once-per-launch check
            // is nowhere near.
            _manager = new UpdateManager(new GithubSource(repoUrl, null, false));
        }

        /// <summary>
        /// True only when Velopack installed this process. False for the portable zip, the winget
        /// portable package, and every dev run.
        /// </summary>
        public bool IsSupported => _manager.IsInstalled;

        public async Task<UpdateAvailability> CheckAndDownloadAsync(CancellationToken ct)
        {
            if (!IsSupported)
            {
                return new UpdateAvailability(false, null);
            }

            var update = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (update == null)
            {
                return new UpdateAvailability(false, null);
            }

            var version = update.TargetFullRelease.Version.ToString();
            _log($"Update available: {version}; downloading.");

            await _manager.DownloadUpdatesAsync(update, null, ct).ConfigureAwait(false);
            _downloaded = update;

            _log($"Update {version} downloaded; waiting for a restart.");
            return new UpdateAvailability(true, version);
        }

        public void ApplyAndRestart()
        {
            if (_downloaded == null)
            {
                return;
            }

            _log("Applying update and restarting.");
            _manager.ApplyUpdatesAndRestart(_downloaded);
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~VelopackUpdateServiceTests"
```

Expected: 3 tests PASS. If compilation fails on `update.TargetFullRelease.Version`, fix it against the real Velopack 1.2.0 API rather than casting around it — that property path is the one member name in this plan taken from documentation rather than read off this repo.

- [ ] **Step 5: Re-run the AOT publish, now that Velopack's real call paths are reachable**

Task 1's spike only had the startup hook. This is the first build where `UpdateManager`, `GithubSource` and the JSON feed parsing are reachable from `Main`, which is where AOT problems would actually appear.

```bash
scripts/build.ps1 publish src/NovaTerminal.App/NovaTerminal.App.csproj -c Release -r win-x64 --self-contained true -p:PublishAot=true -p:SkipCliShim=true -o artifacts/publish/aot-spike2 2>&1 | grep -E "IL2026|IL2104|IL3050|IL3053|warning IL" | sort -u
```

Expected: publish succeeds. Any Velopack-attributed `IL2026`/`IL3050` here is the design's fallback trigger — stop and report rather than shipping an updater that fails only in the AOT build users actually get.

- [ ] **Step 6: Commit**

```bash
git add src/NovaTerminal.App/Update/VelopackUpdateService.cs tests/NovaTerminal.App.Tests/Update/VelopackUpdateServiceTests.cs
git commit -m "feat(update): implement the update service over Velopack + GitHub releases

Reads releases straight off this repo with prerelease:false, so a prerelease
tag never pulls stable users onto an unfinished build. Every path is inert
when IsInstalled is false, which is the case for the portable zip, the winget
portable package and all dev runs - none of those may reach the network or
show update UI.

Re-verified against a win-x64 NativeAOT publish; unlike the Task 1 spike this
build actually reaches UpdateManager, GithubSource and the feed parsing.

Refs #91"
```

---

### Task 5: The `AutomaticUpdateChecks` setting

**Files:**
- Modify: `src/NovaTerminal.App/Shell/TerminalSettings.cs` (new property near the notification settings)
- Modify: `src/NovaTerminal.App/SettingsWindow.axaml:596-601` (new row after "Long command notifications")
- Modify: `src/NovaTerminal.App/SettingsWindow.axaml.cs:2023-2024` (load) and `:2278-2279` (save)
- Test: `tests/NovaTerminal.App.Tests/Update/UpdateSettingsTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `TerminalSettings.AutomaticUpdateChecks` (`bool`, defaults `true`); XAML control name `AutomaticUpdateChecksToggle`.

- [ ] **Step 1: Write the failing test**

Create `tests/NovaTerminal.App.Tests/Update/UpdateSettingsTests.cs`. The round-trip matters because settings are serialized through a source-generated `JsonSerializerContext` (`Shell/AppJsonContext.cs`) for AOT — a property that is not carried by that context silently loses the user's choice.

```csharp
using System.Text.Json;
using NovaTerminal.Shell;

namespace NovaTerminal.AppTests.Update;

public class UpdateSettingsTests
{
    [Fact]
    public void Automatic_update_checks_default_to_on()
    {
        Assert.True(new TerminalSettings().AutomaticUpdateChecks);
    }

    [Fact]
    public void Automatic_update_checks_survive_a_json_round_trip()
    {
        var settings = new TerminalSettings { AutomaticUpdateChecks = false };

        var json = JsonSerializer.Serialize(settings, AppJsonContext.Default.TerminalSettings);
        var restored = JsonSerializer.Deserialize(json, AppJsonContext.Default.TerminalSettings);

        Assert.NotNull(restored);
        Assert.False(restored!.AutomaticUpdateChecks);
    }

    [Fact]
    public void A_settings_file_written_before_this_setting_existed_opts_in()
    {
        // Users upgrading from a build without the property must land on the default rather
        // than on `false`, which is what a bare `default(bool)` would give them.
        var restored = JsonSerializer.Deserialize("{}", AppJsonContext.Default.TerminalSettings);

        Assert.NotNull(restored);
        Assert.True(restored!.AutomaticUpdateChecks);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~UpdateSettingsTests"
```

Expected: FAIL at compile time — `AutomaticUpdateChecks` does not exist.

- [ ] **Step 3: Add the property**

In `src/NovaTerminal.App/Shell/TerminalSettings.cs`, add next to the other notification-shaped booleans:

```csharp
        // Governs the once-per-launch background update check (#91). Default on: an installed
        // build that never learns about a fix is worse than a single anonymous request to
        // GitHub's releases API 10 seconds after launch. Off stops all background traffic; the
        // command palette's "Check for updates" still works, because that is the user asking
        // rather than the app polling. Ignored entirely when the app was not installed by
        // Velopack (portable zip, winget, dev runs) - there is nothing to update.
        public bool AutomaticUpdateChecks { get; set; } = true;
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~UpdateSettingsTests"
```

Expected: 3 tests PASS. `AppJsonContext` already declares `[JsonSerializable(typeof(TerminalSettings))]`, so the source generator picks up the new property with no change there.

- [ ] **Step 5: Add the settings row**

In `src/NovaTerminal.App/SettingsWindow.axaml`, after the "Long command notifications" `Grid` and its trailing `Border` separator:

```xml
                            <Grid ColumnDefinitions="*,360">
                                <StackPanel Grid.Column="0" Spacing="2">
                                    <TextBlock Classes="RowLabel" Text="Automatic update checks"/>
                                    <TextBlock Classes="RowDesc" Text="Check GitHub for a newer version shortly after launch and download it in the background. Nothing restarts until you say so. Only applies to installed builds, not the portable zip."/>
                                </StackPanel>
                                <CheckBox Name="AutomaticUpdateChecksToggle" Grid.Column="1" Classes="Toggle" Content="" HorizontalAlignment="Right" VerticalAlignment="Center"/>
                            </Grid>
                            <Border BorderBrush="{StaticResource NtHairline}" BorderThickness="0,0,0,1" Margin="0,14,0,14"/>
```

- [ ] **Step 6: Wire load and save**

In `src/NovaTerminal.App/SettingsWindow.axaml.cs`, after the `longCommandNotificationsToggle` **load** line (~2023):

```csharp
            var automaticUpdateChecksToggle = this.FindControl<CheckBox>("AutomaticUpdateChecksToggle");
            if (automaticUpdateChecksToggle != null) automaticUpdateChecksToggle.IsChecked = _settings.AutomaticUpdateChecks;
```

and after the `longCommandNotificationsToggle` **save** line (~2278):

```csharp
            var automaticUpdateChecksToggle = this.FindControl<CheckBox>("AutomaticUpdateChecksToggle");
            if (automaticUpdateChecksToggle != null) _settings.AutomaticUpdateChecks = automaticUpdateChecksToggle.IsChecked == true;
```

- [ ] **Step 7: Build and re-run the tests**

```bash
scripts/build.ps1 build src/NovaTerminal.App
```

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~UpdateSettingsTests"
```

Expected: build succeeds, 3 tests PASS.

- [ ] **Step 8: Commit**

```bash
git add src/NovaTerminal.App/Shell/TerminalSettings.cs src/NovaTerminal.App/SettingsWindow.axaml src/NovaTerminal.App/SettingsWindow.axaml.cs tests/NovaTerminal.App.Tests/Update/UpdateSettingsTests.cs
git commit -m "feat(update): add the automatic-update-checks setting

Defaults to on, and a settings file written before the property existed
deserializes to on rather than to default(bool) - which is the case that
would silently opt every existing user out. Turning it off stops background
traffic only; the palette's manual check is the user asking, not the app
polling.

Refs #91"
```

---

### Task 6: Wire it into the window — toast, startup check, palette

**Files:**
- Modify: `src/NovaTerminal.App/MainWindow.axaml` (new `UpdateToast` border after the `RecordingToast` border, ~line 226)
- Modify: `src/NovaTerminal.App/MainWindow.axaml.cs` (fields; `OnOpened` ~line 132; `SetupCommandPalette` ~line 4440; new methods near `ShowRecordingToast` ~line 6005)

**Interfaces:**
- Consumes: `UpdateCoordinator`, `UpdateCheckOutcome` (Task 3); `VelopackUpdateService` (Task 4); `TerminalSettings.AutomaticUpdateChecks` (Task 5).
- Produces: nothing consumed by later tasks.

- [ ] **Step 1: Add the toast markup**

In `src/NovaTerminal.App/MainWindow.axaml`, immediately after the `RecordingToast` `Border`'s closing tag. It sits **bottom**-right while the recording toast sits top-right: the two are independent notices and can be live at once, so they must not share a surface or a position.

```xml
        <!-- Update-ready notice (#91). Deliberately not the RecordingToast: a recording toast and
             an update notice can be live simultaneously, and this one has no auto-hide timer -
             dismissing it leaves the update staged for the next restart. -->
        <Border Name="UpdateToast"
                IsVisible="False"
                HorizontalAlignment="Right"
                VerticalAlignment="Bottom"
                Margin="0,0,16,16"
                MinWidth="320"
                MaxWidth="440"
                Padding="12"
                Background="#20242C"
                BorderBrush="#3A404A"
                BorderThickness="1"
                CornerRadius="8"
                ZIndex="180">
            <Grid RowDefinitions="Auto,Auto,Auto" ColumnDefinitions="*,Auto">
                <TextBlock Name="UpdateToastTitle"
                           Text="Update ready"
                           Foreground="White"
                           FontWeight="SemiBold" />
                <Button Name="UpdateToastClose"
                        Grid.Column="1"
                        Content="✕"
                        Width="28"
                        Height="28"
                        Margin="8,0,0,0"
                        Background="Transparent"
                        Foreground="#C9CFD9"
                        BorderThickness="0"
                        Focusable="False"/>
                <TextBlock Name="UpdateToastMessage"
                           Grid.Row="1"
                           Grid.ColumnSpan="2"
                           Margin="0,8,0,0"
                           Foreground="#C9CFD9"
                           TextWrapping="Wrap" />
                <StackPanel Grid.Row="2"
                            Grid.ColumnSpan="2"
                            Orientation="Horizontal"
                            HorizontalAlignment="Right"
                            Margin="0,10,0,0"
                            Spacing="8">
                    <Button Name="UpdateToastRestart"
                            Content="Restart now"
                            Focusable="False"/>
                </StackPanel>
            </Grid>
        </Border>
```

- [ ] **Step 2: Add the fields**

In `src/NovaTerminal.App/MainWindow.axaml.cs`, next to the `_recordingToast*` fields (~line 89):

```csharp
        private NovaTerminal.Update.UpdateCoordinator? _updateCoordinator;
        private readonly DispatcherTimer _updateCheckTimer = new() { Interval = TimeSpan.FromSeconds(10) };
```

- [ ] **Step 3: Add the show/hide/apply methods**

Next to `ShowRecordingToast` / `HideRecordingToast` (~line 6005):

```csharp
        /// <summary>
        /// Raises the update-ready notice. No auto-hide: unlike a recording toast this is an
        /// offer the user may take minutes later, and closing it only dismisses the notice - the
        /// update stays staged.
        /// </summary>
        private void ShowUpdateToast(string version)
        {
            var toast = this.FindControl<Border>("UpdateToast");
            var messageBlock = this.FindControl<TextBlock>("UpdateToastMessage");
            if (toast == null || messageBlock == null)
            {
                return;
            }

            messageBlock.Text = $"NovaTerminal {version} is downloaded and will be applied when you restart.";
            toast.IsVisible = true;
        }

        private void HideUpdateToast()
        {
            var toast = this.FindControl<Border>("UpdateToast");
            if (toast != null)
            {
                toast.IsVisible = false;
            }
        }

        /// <summary>
        /// Builds the update coordinator and schedules the one background check this process
        /// makes. Deliberately not on the cold-start path: <see cref="StartupPerformanceTracker"/>
        /// exists because that path is measured, and an update check has no business in it.
        /// </summary>
        private void StartUpdateChecks()
        {
            _updateCoordinator = new NovaTerminal.Update.UpdateCoordinator(
                new NovaTerminal.Update.VelopackUpdateService(
                    NovaTerminal.Update.VelopackUpdateService.DefaultRepoUrl,
                    message => TerminalLogger.Log(message)),
                () => _settings.AutomaticUpdateChecks,
                version => Dispatcher.UIThread.Post(() =>
                {
                    ShowUpdateToast(version);
                    SetupCommandPalette();
                }),
                message => TerminalLogger.Log(message));

            _updateCheckTimer.Tick += (_, __) =>
            {
                // Once per launch, not every 10 seconds.
                _updateCheckTimer.Stop();
                _ = _updateCoordinator.RunAutomaticCheckAsync();
            };
            _updateCheckTimer.Start();
        }

        private void ApplyStagedUpdate()
        {
            _updateCoordinator?.ApplyStagedUpdate();
        }
```

- [ ] **Step 4: Hook the buttons and the startup check**

In `OnOpened` (~line 132), after the existing `ClipboardImage.CleanUpOldTempImages` task:

```csharp
            var updateToastClose = this.FindControl<Button>("UpdateToastClose");
            if (updateToastClose != null)
            {
                updateToastClose.Click += (_, __) => HideUpdateToast();
            }

            var updateToastRestart = this.FindControl<Button>("UpdateToastRestart");
            if (updateToastRestart != null)
            {
                updateToastRestart.Click += (_, __) => ApplyStagedUpdate();
            }

            StartUpdateChecks();
```

- [ ] **Step 5: Register the palette commands**

In `SetupCommandPalette` (~line 4440), after the `"New Tab"` registration:

```csharp
            // Update commands are registered only when this build can actually update itself -
            // a portable-zip or dev run has nothing to check. SetupCommandPalette() is lazy
            // (it runs on palette-open and settings-save), so this reflects the state at the
            // moment the palette is built rather than a value latched at startup.
            if (_updateCoordinator != null)
            {
                if (_updateCoordinator.IsUpdateStaged)
                {
                    CommandRegistry.Register(
                        $"Update: Restart to apply {_updateCoordinator.StagedVersion}",
                        "Application",
                        () => ApplyStagedUpdate(),
                        "");
                }

                CommandRegistry.Register("Update: Check for updates", "Application", () =>
                {
                    _ = CheckForUpdatesInteractiveAsync();
                }, "");
            }
```

- [ ] **Step 6: Add the manual-check handler**

A manual check is the one case that reports failure — the user asked, so silence would read as a hang. Add next to `StartUpdateChecks`:

```csharp
        /// <summary>
        /// The palette's "Check for updates". Unlike the background check this one always says
        /// what happened: the user asked a direct question and silence would read as a hang.
        /// </summary>
        private async System.Threading.Tasks.Task CheckForUpdatesInteractiveAsync()
        {
            if (_updateCoordinator == null)
            {
                return;
            }

            var outcome = await _updateCoordinator.RunManualCheckAsync();
            switch (outcome)
            {
                case NovaTerminal.Update.UpdateCheckOutcome.UpdateReady:
                    // The coordinator's onUpdateReady callback already raised the toast.
                    break;
                case NovaTerminal.Update.UpdateCheckOutcome.UpToDate:
                    ShowRecordingToast("Up to date", "You are running the newest version.", null, null, autoHide: true);
                    break;
                case NovaTerminal.Update.UpdateCheckOutcome.Unsupported:
                    ShowRecordingToast(
                        "Updates unavailable",
                        "This build was not installed by the NovaTerminal installer, so it cannot update itself. Download the installer from the releases page to get automatic updates.",
                        null,
                        null,
                        autoHide: true);
                    break;
                case NovaTerminal.Update.UpdateCheckOutcome.Failed:
                    ShowRecordingToast("Update check failed", "Could not reach GitHub. See the debug log for details.", null, null, autoHide: true);
                    break;
                case NovaTerminal.Update.UpdateCheckOutcome.Disabled:
                    // Unreachable: a manual check ignores the automatic-checks setting.
                    break;
            }
        }
```

- [ ] **Step 7: Build**

```bash
scripts/build.ps1 build src/NovaTerminal.App
```

Expected: succeeds. If `Dispatcher` or `DispatcherTimer` is unresolved, add `using Avalonia.Threading;` — check the existing usings first, since `_recordingToastTimer` is a `DispatcherTimer` in the same file and the namespace is almost certainly already imported.

- [ ] **Step 8: Run the App.Tests suite for regressions**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "Category!=ShellIntegration"
```

Expected: no new failures versus the pre-change baseline. `ShellIntegration` is excluded because those tests are the known trigger for the headless dispatcher deadlock (#81).

- [ ] **Step 9: Manual GUI verification**

Automated GUI checks are unreliable here, so verify by hand:

1. `scripts/build.ps1 run --project src/NovaTerminal.App` (or launch the built exe).
2. The app starts normally; no update toast appears (a dev run is not a Velopack install).
3. Open the command palette. **"Update: Check for updates"** is present.
4. Run it. Expect the *"Updates unavailable — this build was not installed by the NovaTerminal installer"* toast, **not** a failure toast and not silence.
5. Open Settings → find **Automatic update checks**, confirm it is on, toggle it off, save, reopen Settings, confirm it stayed off. Toggle it back on.
6. Confirm the debug log (`AppLogger.GetLogFilePath()`) contains no update-related exception.

- [ ] **Step 10: Commit**

```bash
git add src/NovaTerminal.App/MainWindow.axaml src/NovaTerminal.App/MainWindow.axaml.cs
git commit -m "feat(update): surface staged updates in the window and palette

The check fires once, 10 seconds after the window opens, so it stays off the
cold-start path that StartupPerformanceTracker measures. A ready update gets
its own bottom-right toast rather than reusing RecordingToast - the two
notices are independent and can be live at the same time - and it has no
auto-hide, because dismissing it should not discard the staged update.

The palette entries are registered only when the build can actually update
itself, which SetupCommandPalette()'s laziness makes correct: it runs on
palette-open, so it sees the real staged state rather than a startup latch.
A manual check reports its outcome, including 'this build cannot update
itself' for portable-zip users; the background check stays silent on failure.

Refs #91"
```

---

### Task 7: End-to-end verification on a real release

The manual end-to-end run is the only thing that proves the whole chain — and the design says so explicitly, because `App.Tests` is not in the gating unit loop (`VT`, `Rendering`, `Architecture`, `Platform`, `McpServer`) and no CI job exercises an install.

**Files:** none (verification only; may produce a follow-up note or issue).

**Interfaces:**
- Consumes: everything from Tasks 1–6.
- Produces: a verified release, or a defect list.

- [ ] **Step 1: Confirm the whole test surface is green before releasing anything**

```bash
scripts/build.ps1 test tests/NovaTerminal.Architecture.Tests
```

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "Category!=ShellIntegration"
```

- [ ] **Step 2: Cut the first release carrying the installer**

Push a tag (or use the workflow's `workflow_dispatch` with `tag_name`). Then watch the run:

```bash
rtk gh run list --workflow release.yml --limit 3
```

- [ ] **Step 3: Verify the release assets**

```bash
rtk gh release view <tag>
```

Expected on the release: the three existing zips **unchanged in name**, plus `NovaTerminal-Setup-win-x64-<tag>.exe`, `NovaTerminal-<version>-full.nupkg`, and `releases.win.json`. No `*-delta.nupkg` on this first one — there was no prior Velopack release to diff against, which is expected, and the `Download previous Velopack release` step is expected to have logged a miss without failing.

- [ ] **Step 4: Verify winget was not disturbed**

```bash
rtk gh release view <tag> --json assets --jq '.assets[].name'
```

Confirm `NovaTerminal-win-x64-<tag>.zip` is present with exactly that name. The winget manifest's `InstallerUrl` pattern depends on it.

- [ ] **Step 5: Install and inspect**

Download and run the installer on Windows. Verify: it installs without a UAC prompt (per-user), a Start Menu entry appears, an Add/Remove Programs entry appears, and the app launches and behaves normally. SmartScreen **will** warn — that is expected and unfixed until signing lands on #91.

- [ ] **Step 6: Verify the update path — the actual point of all this**

Cut a second release (bump `Version` in `Directory.Build.props` and push the next tag). Then, in the *installed* vN app:

1. Launch it and wait ~15 seconds.
2. The update toast appears: "NovaTerminal <vN+1> is downloaded and will be applied when you restart."
3. The palette shows **"Update: Restart to apply <vN+1>"**.
4. Click **Restart now**. The app restarts on vN+1 — confirm via the debug log's `Build:` line.
5. Confirm the vN+1 release carries a `*-delta.nupkg`, and that the debug log shows the delta being used rather than the full package.
6. Turn **Automatic update checks** off, relaunch, and confirm no check fires (nothing update-related in the log) while the palette's manual check still works.

- [ ] **Step 7: Record what happened**

If everything passes, comment the verified behaviour on [#91](https://github.com/benyblack/NovaTerminal/issues/91) and note that only code signing remains open there. If anything failed, open a defect issue per failure with the log excerpt rather than patching blind — an updater that half-works is worse than one that is honestly absent.

---

## Notes for the implementer

- **`vpk` and the `Velopack` NuGet package must stay on the same version.** If you bump one, bump the other in the same commit: `Directory.Packages.props` and the `Install vpk` step in `.github/workflows/release.yml`.
- **The one member path taken from docs rather than from this repo** is `UpdateInfo.TargetFullRelease.Version` in Task 4. Everything else was read off the actual source files.
- **Do not point the winget manifest at the installer.** It is deliberately left on the portable zip so winget and Velopack's updater never both believe they own the install.
- **Signing is not in this plan.** If SmartScreen comes up in review, the answer is #91, not a scope expansion here.
