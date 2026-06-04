# Windows Packaging: Velopack Installer + Auto-Update Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Supplement the portable win-x64 zip with a Velopack installer + notify-on-restart auto-update from GitHub Releases, with a no-op code-signing seam in CI.

**Architecture:** A `Velopack` hook runs first in `Program.Main`. A testable `UpdateService` (logic) sits behind an `IVelopackUpdater` adapter (real Velopack wrapper) so update state is unit-testable without network or AOT. MainWindow checks for updates fire-and-forget on load, shows a title-bar indicator, and registers a conditional command-palette action to restart-and-apply. `release.yml` gains `vpk` steps on the win-x64 leg only, plus an optional signing flag driven by a secret.

**Tech Stack:** .NET 10 / NativeAOT, Avalonia 12, Velopack (`Velopack` NuGet + `vpk` dotnet tool), xUnit v3 + Moq, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-06-04-windows-packaging-velopack-design.md`

**Repo / feed:** `https://github.com/benyblack/NovaTerminal`

---

## File Structure

**Create:**
- `src/NovaTerminal.App/Services/Updates/IVelopackUpdater.cs` — narrow adapter interface over Velopack's `UpdateManager` (the testable seam).
- `src/NovaTerminal.App/Services/Updates/UpdateService.cs` — update orchestration logic (check / state / apply); no Velopack types, depends only on `IVelopackUpdater`.
- `src/NovaTerminal.App/Services/Updates/VelopackUpdater.cs` — real `IVelopackUpdater` wrapping `UpdateManager` + `GithubSource`.
- `tests/NovaTerminal.App.Tests/Updates/UpdateServiceTests.cs` — unit tests for `UpdateService` against a faked `IVelopackUpdater`.
- `docs/packaging/windows-signing.md` — how to enable Authenticode signing later.

**Modify:**
- `src/NovaTerminal.App/Program.cs` — add `VelopackApp.Build().Run()` as first line of `Main`.
- `src/NovaTerminal.App/NovaTerminal.App.csproj` — add `Velopack` package reference.
- `Directory.Packages.props` — add `Velopack` `PackageVersion`.
- `src/NovaTerminal.App/MainWindow.axaml` — add a hidden "update ready" title-bar indicator.
- `src/NovaTerminal.App/MainWindow.axaml.cs` — construct `UpdateService`, kick off check on load, toggle indicator, register conditional palette command.
- `.github/workflows/release.yml` — add `vpk` download/pack/upload + signing seam to the win-x64 leg.

---

## Task 0: Spike — prove Velopack + NativeAOT (GATES ALL OTHER TASKS)

This task is manual validation, not TDD. **Do not start Tasks 1–7 until this passes.** Its outputs (pinned `vpk` version, confirmed signing flag name) feed later tasks.

**Files:** none committed (throwaway). Use a scratch dir under `D:\tmp`.

- [ ] **Step 1: Install the vpk tool and record the version**

Run:
```powershell
dotnet tool install -g vpk
vpk --version
```
Record the exact version printed (e.g. `0.0.xxx`). This exact version is pinned in Task 1 and Task 5.

- [ ] **Step 2: Add a throwaway Velopack hook + package locally**

Temporarily add to `Directory.Packages.props` `<PackageVersion Include="Velopack" Version="<latest>" />` and to `NovaTerminal.App.csproj` `<PackageReference Include="Velopack" />`, and add `Velopack.VelopackApp.Build().Run();` as the first line of `Program.Main`. (These become permanent in Task 1; here you just need them to build.)

- [ ] **Step 3: Publish AOT exactly as CI does**

Run:
```powershell
dotnet publish src/NovaTerminal.App/NovaTerminal.App.csproj -c Release -r win-x64 --self-contained true -p:PublishAot=true -p:SkipCliShim=true -o D:\tmp\nova-velopack-spike\publish
```
Expected: publish succeeds, `D:\tmp\nova-velopack-spike\publish\NovaTerminal.exe` exists.

- [ ] **Step 4: Pack v0.0.1 and install**

Run:
```powershell
vpk pack --packId NovaTerminal --packVersion 0.0.1 --packDir D:\tmp\nova-velopack-spike\publish --mainExe NovaTerminal.exe --outputDir D:\tmp\nova-velopack-spike\releases
```
Run the produced `D:\tmp\nova-velopack-spike\releases\NovaTerminal-win-Setup.exe`. Confirm: the app installs, launches, and runs normally (open a terminal tab).

- [ ] **Step 5: Pack v0.0.2 and verify self-update path**

Re-publish (Step 3), then `vpk pack ... --packVersion 0.0.2 ...` into the same `releases` dir. Point a local `UpdateManager` at that dir (or use `vpk`'s test feed) and confirm an update from 0.0.1 → 0.0.2 is detected, downloaded, and applied on restart.

- [ ] **Step 6: Confirm the signing flag name**

Run `vpk pack --help` and record the exact signing flag (expected `--signTemplate` with a `{{file}}` placeholder; note the real name/shape for the pinned version). This is used by the Task 5 signing seam.

- [ ] **Step 7: Record spike outcome**

**Exit gate:** install → launch → 0.0.1→0.0.2 self-update all work.
- If **pass**: write the pinned `vpk` version and signing flag name into this plan (replace the `<PINNED>` markers in Tasks 1 and 5), revert the throwaway edits from Step 2, and proceed.
- If **fail** (AOT incompatibility): STOP and report. Decide a fallback with the user per the spec (non-AOT self-contained for the installed build, or Velopack's AOT shim) before writing any further code.

---

## Task 1: Add Velopack package + Main hook

**Files:**
- Modify: `Directory.Packages.props`
- Modify: `src/NovaTerminal.App/NovaTerminal.App.csproj`
- Modify: `src/NovaTerminal.App/Program.cs:16`

This task has no unit test — the `VelopackApp` hook is an integration concern validated by the Task 0 spike and a build. TDD resumes in Task 2.

- [ ] **Step 1: Pin the Velopack package version**

In `Directory.Packages.props`, add under a new comment line after the Crypto block:
```xml
    <!-- Packaging / auto-update -->
    <PackageVersion Include="Velopack" Version="<PINNED-from-Task0>" />
```

- [ ] **Step 2: Reference the package**

In `src/NovaTerminal.App/NovaTerminal.App.csproj`, add inside the main `<ItemGroup>` with the other `PackageReference` entries:
```xml
    <PackageReference Include="Velopack" />
```

- [ ] **Step 3: Add the hook as the first line of Main**

In `src/NovaTerminal.App/Program.cs`, the current `Main` body begins with `try {`. Insert the Velopack hook as the very first statement inside `Main`, before the `try`:
```csharp
    [STAThread]
    public static void Main(string[] args)
    {
        // Velopack hook: Setup.exe/Update.exe re-invoke this binary with --veloapp-*
        // args that must be handled and exit before any app/CLI/Avalonia init.
        Velopack.VelopackApp.Build().Run();

        try
        {
```
Leave the rest of `Main` unchanged.

- [ ] **Step 4: Build to verify it compiles and AOT-publishes**

Run:
```powershell
scripts/build.ps1 build src/NovaTerminal.App
```
Expected: build succeeds with no new warnings about Velopack trimming/AOT.

- [ ] **Step 5: Commit**

```powershell
git add Directory.Packages.props src/NovaTerminal.App/NovaTerminal.App.csproj src/NovaTerminal.App/Program.cs
git commit -m "feat(update): add Velopack package + VelopackApp hook in Main (#91)"
```

---

## Task 2: UpdateService + IVelopackUpdater (TDD core)

**Files:**
- Create: `src/NovaTerminal.App/Services/Updates/IVelopackUpdater.cs`
- Create: `src/NovaTerminal.App/Services/Updates/UpdateService.cs`
- Test: `tests/NovaTerminal.App.Tests/Updates/UpdateServiceTests.cs`

`UpdateService` holds all logic and is the only update code with tests. It never references Velopack types — it depends on `IVelopackUpdater`, which Task 3 implements for real and the test fakes.

- [ ] **Step 1: Define the adapter interface**

Create `src/NovaTerminal.App/Services/Updates/IVelopackUpdater.cs`:
```csharp
using System.Threading.Tasks;

namespace NovaTerminal.Services.Updates;

/// <summary>
/// Narrow seam over Velopack's UpdateManager so UpdateService logic is testable
/// without network access or Velopack/AOT coupling.
/// </summary>
public interface IVelopackUpdater
{
    /// <summary>True only when running as a Velopack-installed build.</summary>
    bool IsInstalled { get; }

    /// <summary>
    /// Checks the feed and stages any newer release.
    /// Returns the target version string when an update is staged, or null when up to date.
    /// </summary>
    Task<string?> CheckAndStageAsync();

    /// <summary>Applies the staged update and restarts the app. Does not return on success.</summary>
    Task ApplyAndRestartAsync();
}
```

- [ ] **Step 2: Write the failing tests**

Create `tests/NovaTerminal.App.Tests/Updates/UpdateServiceTests.cs`:
```csharp
using System.Threading.Tasks;
using Moq;
using NovaTerminal.Services.Updates;
using Xunit;

namespace NovaTerminal.Tests.Updates;

public sealed class UpdateServiceTests
{
    private static Mock<IVelopackUpdater> Installed(string? staged)
    {
        var m = new Mock<IVelopackUpdater>();
        m.SetupGet(x => x.IsInstalled).Returns(true);
        m.Setup(x => x.CheckAndStageAsync()).ReturnsAsync(staged);
        return m;
    }

    [Fact]
    public async Task CheckAsync_WhenUpdateStaged_SetsUpdateReadyWithVersion()
    {
        var svc = new UpdateService(Installed("1.2.3").Object);

        await svc.CheckAsync();

        Assert.True(svc.UpdateReady);
        Assert.Equal("1.2.3", svc.AvailableVersion);
    }

    [Fact]
    public async Task CheckAsync_WhenUpToDate_LeavesUpdateNotReady()
    {
        var svc = new UpdateService(Installed(null).Object);

        await svc.CheckAsync();

        Assert.False(svc.UpdateReady);
        Assert.Null(svc.AvailableVersion);
    }

    [Fact]
    public async Task CheckAsync_WhenNotInstalled_SkipsCheckEntirely()
    {
        var m = new Mock<IVelopackUpdater>();
        m.SetupGet(x => x.IsInstalled).Returns(false);
        var svc = new UpdateService(m.Object);

        await svc.CheckAsync();

        Assert.False(svc.UpdateReady);
        m.Verify(x => x.CheckAndStageAsync(), Times.Never);
    }

    [Fact]
    public async Task CheckAsync_WhenUpdaterThrows_SwallowsAndStaysNotReady()
    {
        var m = Installed("1.2.3");
        m.Setup(x => x.CheckAndStageAsync()).ThrowsAsync(new System.Net.Http.HttpRequestException("offline"));
        var svc = new UpdateService(m.Object);

        await svc.CheckAsync(); // must not throw

        Assert.False(svc.UpdateReady);
    }

    [Fact]
    public async Task CheckAsync_WhenUpdateStaged_RaisesUpdateReadyChanged()
    {
        var svc = new UpdateService(Installed("9.9.9").Object);
        bool raised = false;
        svc.UpdateReadyChanged += () => raised = true;

        await svc.CheckAsync();

        Assert.True(raised);
    }

    [Fact]
    public async Task ApplyAsync_WhenReady_CallsUpdaterApply()
    {
        var m = Installed("1.2.3");
        var svc = new UpdateService(m.Object);
        await svc.CheckAsync();

        await svc.ApplyAsync();

        m.Verify(x => x.ApplyAndRestartAsync(), Times.Once);
    }

    [Fact]
    public async Task ApplyAsync_WhenNotReady_DoesNothing()
    {
        var m = Installed(null);
        var svc = new UpdateService(m.Object);
        await svc.CheckAsync();

        await svc.ApplyAsync();

        m.Verify(x => x.ApplyAndRestartAsync(), Times.Never);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run:
```powershell
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~UpdateServiceTests"
```
Expected: FAIL — `UpdateService` does not exist (compile error).

- [ ] **Step 4: Implement UpdateService**

Create `src/NovaTerminal.App/Services/Updates/UpdateService.cs`:
```csharp
using System;
using System.Threading.Tasks;
using NovaTerminal.VT;

namespace NovaTerminal.Services.Updates;

/// <summary>
/// Orchestrates startup update checks and apply-on-restart. Pure logic over
/// <see cref="IVelopackUpdater"/>; never throws out of CheckAsync (failures are logged).
/// </summary>
public sealed class UpdateService
{
    private readonly IVelopackUpdater _updater;

    public UpdateService(IVelopackUpdater updater)
    {
        ArgumentNullException.ThrowIfNull(updater);
        _updater = updater;
    }

    /// <summary>True once a newer release has been staged and is ready to apply on restart.</summary>
    public bool UpdateReady { get; private set; }

    /// <summary>The staged target version, or null when none.</summary>
    public string? AvailableVersion { get; private set; }

    /// <summary>Raised on the calling thread when <see cref="UpdateReady"/> transitions to true.</summary>
    public event Action? UpdateReadyChanged;

    /// <summary>
    /// Fire-and-forget startup check. Never throws. No-op when not running as a
    /// Velopack-installed build (portable zip / dev).
    /// </summary>
    public async Task CheckAsync()
    {
        if (!_updater.IsInstalled)
        {
            return;
        }

        try
        {
            string? version = await _updater.CheckAndStageAsync().ConfigureAwait(false);
            if (!string.IsNullOrEmpty(version))
            {
                AvailableVersion = version;
                UpdateReady = true;
                UpdateReadyChanged?.Invoke();
            }
        }
        catch (Exception ex)
        {
            // Update failure must never block or crash startup.
            TerminalLogger.Log("UpdateService.CheckAsync failed: " + ex);
        }
    }

    /// <summary>Applies the staged update and restarts. No-op when no update is ready.</summary>
    public async Task ApplyAsync()
    {
        if (!UpdateReady)
        {
            return;
        }

        await _updater.ApplyAndRestartAsync().ConfigureAwait(false);
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run:
```powershell
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~UpdateServiceTests"
```
Expected: PASS — all 7 tests green.

- [ ] **Step 6: Commit**

```powershell
git add src/NovaTerminal.App/Services/Updates/IVelopackUpdater.cs src/NovaTerminal.App/Services/Updates/UpdateService.cs tests/NovaTerminal.App.Tests/Updates/UpdateServiceTests.cs
git commit -m "feat(update): testable UpdateService over IVelopackUpdater seam (#91)"
```

---

## Task 3: Real VelopackUpdater adapter

**Files:**
- Create: `src/NovaTerminal.App/Services/Updates/VelopackUpdater.cs`

No unit test — this is the thin Velopack binding, validated by build + the Task 0 spike (real network/AOT).

- [ ] **Step 1: Implement the adapter**

Create `src/NovaTerminal.App/Services/Updates/VelopackUpdater.cs`:
```csharp
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace NovaTerminal.Services.Updates;

/// <summary>
/// Real <see cref="IVelopackUpdater"/> backed by Velopack's UpdateManager against
/// this repo's public GitHub Releases.
/// </summary>
public sealed class VelopackUpdater : IVelopackUpdater
{
    private const string RepoUrl = "https://github.com/benyblack/NovaTerminal";

    private readonly UpdateManager _manager =
        new(new GithubSource(RepoUrl, accessToken: null, prerelease: false));

    private UpdateInfo? _pending;

    public bool IsInstalled => _manager.IsInstalled;

    public async Task<string?> CheckAndStageAsync()
    {
        _pending = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        if (_pending is null)
        {
            return null;
        }

        await _manager.DownloadUpdatesAsync(_pending).ConfigureAwait(false);
        return _pending.TargetFullRelease.Version.ToString();
    }

    public Task ApplyAndRestartAsync()
    {
        if (_pending is not null)
        {
            _manager.ApplyUpdatesAndRestart(_pending);
        }

        return Task.CompletedTask;
    }
}
```
> Note: if the pinned Velopack version's API differs (e.g. `TargetFullRelease.Version` member name), adjust to the exact members confirmed during the Task 0 spike. The `IVelopackUpdater` contract does not change.

- [ ] **Step 2: Build to verify it compiles + AOT-publishes**

Run:
```powershell
scripts/build.ps1 build src/NovaTerminal.App
```
Expected: build succeeds, no AOT/trim warnings for `VelopackUpdater`.

- [ ] **Step 3: Commit**

```powershell
git add src/NovaTerminal.App/Services/Updates/VelopackUpdater.cs
git commit -m "feat(update): real Velopack UpdateManager adapter over GitHub Releases (#91)"
```

---

## Task 4: Wire UpdateService into MainWindow (indicator + palette action)

**Files:**
- Modify: `src/NovaTerminal.App/MainWindow.axaml` (TitleBar StackPanel, ~line 177)
- Modify: `src/NovaTerminal.App/MainWindow.axaml.cs` (field + ctor + Loaded + SetupCommandPalette)

No unit test — `MainWindow` is code-behind UI (no MVVM, not headless-testable here). The logic is already covered by Task 2; this is wiring, verified by build + the spike's manual launch.

- [ ] **Step 1: Add the hidden indicator to the title bar**

In `src/NovaTerminal.App/MainWindow.axaml`, inside the title-bar `StackPanel` (the one beginning near line 115, after the `BtnConnections` button near line 177), add:
```xml
        <Button x:Name="BtnUpdate"
                IsVisible="False"
                Background="Transparent"
                BorderThickness="0"
                Foreground="#FFD25A"
                ToolTip.Tip="An update is ready. Click to restart and apply."
                Content="⭮ Update ready" />
```
(Match the surrounding buttons' style/padding; the exact attributes can mirror `BtnConnections`.)

- [ ] **Step 2: Add the field and construct the service**

In `src/NovaTerminal.App/MainWindow.axaml.cs`, add a field alongside the other service fields (near `_sshConnectionService`):
```csharp
        private readonly NovaTerminal.Services.Updates.UpdateService _updateService =
            new(new NovaTerminal.Services.Updates.VelopackUpdater());
```

- [ ] **Step 3: Wire the indicator + apply action in the constructor**

In the `MainWindow(AppServiceBundle services)` constructor, after `InitializeComponent()` (line 1919) and before the `this.Loaded += ...` block (line 1936), add:
```csharp
            _updateService.UpdateReadyChanged += () => Dispatcher.UIThread.Post(() =>
            {
                var btn = this.FindControl<Button>("BtnUpdate");
                if (btn != null)
                {
                    btn.IsVisible = _updateService.UpdateReady;
                }
                SetupCommandPalette(); // re-register so the restart action appears
            });

            var updateButton = this.FindControl<Button>("BtnUpdate");
            if (updateButton != null)
            {
                updateButton.Click += async (_, __) => await _updateService.ApplyAsync();
            }
```

- [ ] **Step 4: Kick off the check on load**

In the existing `this.Loaded` handler's inner `Dispatcher.UIThread.Post(...)` (the one ending near line 1953), add as the last line inside that lambda, after `InitializeCommandPaletteUI();`:
```csharp
                    _ = _updateService.CheckAsync();
```

- [ ] **Step 5: Register the conditional palette command**

In `SetupCommandPalette()` (starts at line 3891), after `CommandRegistry.Clear();` (line 3893) and the "New Tab" registration, add:
```csharp
            if (_updateService.UpdateReady)
            {
                CommandRegistry.Register(
                    $"Restart to update to {_updateService.AvailableVersion}",
                    "General",
                    () => _ = _updateService.ApplyAsync(),
                    "",
                    "restart_to_update");
            }
```

- [ ] **Step 6: Build to verify everything compiles**

Run:
```powershell
scripts/build.ps1 build src/NovaTerminal.App
```
Expected: build succeeds.

- [ ] **Step 7: Run the full App.Tests project to confirm no regressions**

Run:
```powershell
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~UpdateServiceTests"
```
Expected: PASS (the update tests still green; wiring introduced no compile breakage).

- [ ] **Step 8: Commit**

```powershell
git add src/NovaTerminal.App/MainWindow.axaml src/NovaTerminal.App/MainWindow.axaml.cs
git commit -m "feat(update): title-bar indicator + restart-to-update palette action (#91)"
```

---

## Task 5: CI — vpk pack + upload + signing seam (win-x64 leg)

**Files:**
- Modify: `.github/workflows/release.yml`

No unit test — validated by workflow run on a tag. Add steps to the existing `publish_aot` job, guarded to the win-x64 leg.

- [ ] **Step 1: Add the vpk install + pack + upload steps**

In `.github/workflows/release.yml`, in the `publish_aot` job, after the existing `Upload release asset` step (line 169–173), append these steps (all guarded by `matrix.rid == 'win-x64'`):
```yaml
      - name: Install Velopack CLI (vpk)
        if: matrix.rid == 'win-x64'
        run: dotnet tool install -g vpk --version <PINNED-from-Task0>

      - name: Compute signing args
        id: sign
        if: matrix.rid == 'win-x64'
        shell: pwsh
        run: |
          # Signing seam: no-op unless VPK_SIGN_TEMPLATE secret is set.
          # Set it later to a signtool/Azure Trusted Signing template, e.g.
          #   signtool sign /tr <ts> /td sha256 /fd sha256 /f cert.pfx {{file}}
          $tmpl = "${{ secrets.VPK_SIGN_TEMPLATE }}"
          if ([string]::IsNullOrWhiteSpace($tmpl)) {
            "args=" | Out-File -FilePath $env:GITHUB_OUTPUT -Append
          } else {
            "args=--signTemplate `"$tmpl`"" | Out-File -FilePath $env:GITHUB_OUTPUT -Append
          }

      - name: Download prior Velopack release (for deltas)
        if: matrix.rid == 'win-x64'
        continue-on-error: true
        run: vpk download github --repoUrl https://github.com/benyblack/NovaTerminal

      - name: Velopack pack
        if: matrix.rid == 'win-x64'
        shell: pwsh
        run: |
          $tag = "${{ needs.release_metadata.outputs.release_tag }}"
          $version = $tag.TrimStart('v')
          vpk pack `
            --packId NovaTerminal `
            --packVersion $version `
            --packDir artifacts/publish/win-x64 `
            --mainExe NovaTerminal.exe `
            --icon src/NovaTerminal.App/Assets/nova_icon.ico `
            --outputDir artifacts/velopack `
            ${{ steps.sign.outputs.args }}

      - name: Publish Velopack release to GitHub
        if: matrix.rid == 'win-x64'
        run: vpk upload github --repoUrl https://github.com/benyblack/NovaTerminal --releaseName ${{ needs.release_metadata.outputs.release_tag }} --tag ${{ needs.release_metadata.outputs.release_tag }} --publish --token ${{ secrets.GITHUB_TOKEN }}
```
> Note: `vpk upload github` attaches `Setup.exe`, `RELEASES`, and `.nupkg` to the existing release without removing the portable zips. If the pinned `vpk` version's upload flags differ, adjust per `vpk upload github --help` from the spike.

- [ ] **Step 2: Validate workflow YAML locally**

Run:
```powershell
python -c "import yaml,sys; yaml.safe_load(open('.github/workflows/release.yml')); print('YAML OK')"
```
Expected: `YAML OK` (no parse error). If Python is unavailable, use `gh workflow view` after pushing a branch, or any YAML linter.

- [ ] **Step 3: Commit**

```powershell
git add .github/workflows/release.yml
git commit -m "ci(release): Velopack installer + auto-update for win-x64 with signing seam (#91)"
```

---

## Task 6: Document how to enable signing

**Files:**
- Create: `docs/packaging/windows-signing.md`

- [ ] **Step 1: Write the doc**

Create `docs/packaging/windows-signing.md`:
```markdown
# Enabling Windows code signing

The release pipeline (`.github/workflows/release.yml`) signs the Velopack
installer/updater **only when** the `VPK_SIGN_TEMPLATE` repository secret is set.
Until then, the installer is produced unsigned (users see SmartScreen until
reputation builds / a cert is added).

## To enable

1. Obtain an Authenticode identity (Azure Trusted Signing, or an OV/EV cert).
2. Add a repository secret `VPK_SIGN_TEMPLATE` whose value is the signing command
   template with a `{{file}}` placeholder that `vpk` substitutes per file, e.g.

   - signtool + PFX:
     `signtool sign /tr http://timestamp.digicert.com /td sha256 /fd sha256 /f cert.pfx /p $env:CERT_PW {{file}}`
   - Azure Trusted Signing: use the AzureSignTool/`dotnet-azure-trusted-signing`
     invocation per its docs, ending with `{{file}}`.

3. Re-run the release. `vpk pack` will sign the payload, `Setup.exe`, and `Update.exe`.

No workflow edits are required to enable signing — only the secret.
```

- [ ] **Step 2: Commit**

```powershell
git add docs/packaging/windows-signing.md
git commit -m "docs(packaging): how to enable Windows code signing (#91)"
```

---

## Task 7: Manual acceptance verification (on a tagged pre-release)

This is the spec's acceptance gate; it requires a Windows machine and a real tag.

- [ ] **Step 1: Cut a test tag and let CI run**

Push a pre-release tag (e.g. `v0.3.1-rc1`). Confirm the Release job produces, on the GitHub Release: the existing portable zips **plus** `NovaTerminal-win-Setup.exe`, `RELEASES`, and `.nupkg`.

- [ ] **Step 2: Install N, then publish N+1, verify auto-update**

Install from `Setup.exe` (version N). Cut the next tag (N+1) so CI publishes a new Velopack release. Launch the installed app; confirm the title-bar "Update ready" indicator appears and the command palette shows "Restart to update to <N+1>". Trigger it; confirm the app restarts on N+1.

- [ ] **Step 3: Record the result in the issue**

Comment on #91 confirming the N→N+1 auto-update works (signed: no, seam present).

---

## Self-Review Notes

- **Spec coverage:** spike (Task 0), app hook (Task 1), testable service (Task 2), real adapter (Task 3), notify+restart UX/indicator/palette (Task 4), CI pack/upload + signing seam (Task 5), signing docs (Task 6), manual N→N+1 acceptance (Task 7). All spec acceptance criteria mapped.
- **Supplement-not-replace:** Task 5 adds Velopack steps without touching the existing zip upload; `vpk upload github` attaches assets to the same release.
- **Type consistency:** `IVelopackUpdater` (`IsInstalled`, `CheckAndStageAsync`, `ApplyAndRestartAsync`) is defined in Task 2 and implemented identically in Task 3; `UpdateService` members (`UpdateReady`, `AvailableVersion`, `UpdateReadyChanged`, `CheckAsync`, `ApplyAsync`) are used consistently in Tasks 2 and 4.
- **Pinned markers:** `<PINNED-from-Task0>` in Tasks 1 and 5 are intentionally resolved by the spike before those tasks run.
