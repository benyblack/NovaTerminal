# Marketing Screenshot & Clip Harness — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a standalone tool that boots the real `MainWindow` headless-with-Skia, drives scripted scenarios against live PTY sessions in an isolated demo world, and produces the PNG stills and clips used by README, `site/`, and social posts.

**Architecture:** A console project `tools/NovaTerminal.Shots` starts a `HeadlessUnitTestSession` over the app's own `App` class with `UseHeadlessDrawing = false`, so drawing goes through real Skia. Each scenario opens tabs and panes through `MainWindow`'s own code paths, sends commands to a real shell running inside a seeded temp workspace, waits for output to settle, and rasterizes the whole window via `CaptureRenderedFrame()`. Post-processing derives social and README variants; ffmpeg turns frame sequences into clips.

**Tech Stack:** .NET 10, Avalonia 12.0.4 (`Avalonia.Headless`), SkiaSharp 3.119.4, xunit.v3, ffmpeg 8.0 (already on PATH), PowerShell for the hero-capture script.

**Spec:** [`docs/superpowers/specs/2026-08-28-marketing-screenshot-harness-design.md`](../specs/2026-08-28-marketing-screenshot-harness-design.md)

> **Status (2026-09-04):** all 19 tasks implemented. 16 of 20 scenarios are registered and
> publishing. The four unticked steps below belong to scenarios that are implemented but
> deliberately left out of `ScenarioCatalog` - `sixel-graphics` and `iterm2-inline-image` (they
> decode correctly now, but the cursor is not returned to column 0 after an image, so the prompt
> after it lands mid-row), and `connection-manager` and `remote-files` (the SSH profile store
> bypasses `AppPaths`' sandbox and writes a real per-machine file; remote-files needs a live SSH
> session). `ScenarioCatalog.cs` carries the current evidence for each - read it rather than this
> doc for the up-to-date blocker.
## Global Constraints

- **Build only through the wrappers.** `scripts/build.ps1 <args>` (Windows) or `scripts/build.sh` (bash). Raw `dotnet build` spawns MSBuild daemons that inherit stdout and hang the caller. This applies to every build step in every task.
- **Prefix shell commands with `rtk`** per `CLAUDE.md`, including inside `&&` chains: `rtk git add …&& rtk git commit …`.
- **Work in a worktree, not the shared checkout.** Other sessions commit into the top-level `nova2` checkout and switch its branch mid-task. Create an isolated worktree via `superpowers:using-git-worktrees` before Task 1, and verify `HEAD` before every commit.
- **Never commit to `main`.** Branch first: `feat/shots-harness`.
- **Target framework:** `net10.0`. New projects set `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`, matching `NovaTerminal.Cli`.
- **Package versions are centrally managed.** Add versions to `Directory.Packages.props`; reference without a `Version` attribute in csproj files.
- **App assembly name is `NovaTerminal`**, not `NovaTerminal.App`. `InternalsVisibleTo` entries and any assembly-name matching must use `NovaTerminal`.
- **Config isolation is mandatory.** `NOVATERM_APPDATA_ROOT` must be set before anything touches `AppPaths`, because `MainWindow`'s constructor calls `TerminalSettings.Load()`. Without it a capture run reads and can rewrite the developer's real `settings.json`.
- **Test-run protocol for `App.Tests`:** always `--blame-hang-timeout 5m`, never two concurrent runs, log to a file. `NovaTerminal.Shots.Tests` is small and fast and needs no special handling except the `ShotsSmoke` category exclusion described in Task 1.
- **Published assets live only under `docs/assets/shots/`.** Masters go to `artifacts/shots/`, which is already gitignored.

---

### Task 1: Project skeleton, headless host, and CI wiring

Creates both projects and proves the app boots headless in a plain console process. Everything else depends on this.

**Files:**
- Create: `tools/NovaTerminal.Shots/NovaTerminal.Shots.csproj`
- Create: `tools/NovaTerminal.Shots/ShotsAppBuilder.cs`
- Create: `tools/NovaTerminal.Shots/ShotHost.cs`
- Create: `tools/NovaTerminal.Shots/Program.cs`
- Create: `tests/NovaTerminal.Shots.Tests/NovaTerminal.Shots.Tests.csproj`
- Create: `tests/NovaTerminal.Shots.Tests/ShotHostSmokeTests.cs`
- Modify: `Directory.Packages.props` (add `Avalonia.Headless`)
- Modify: `NovaTerminal.sln`
- Modify: `.github/workflows/ci.yml:180-185` (artifact path list), `:294` and `:545` (unit loops), `:293` (gating filter)

**Interfaces:**
- Consumes: nothing.
- Produces: `NovaTerminal.Shots.ShotsAppBuilder.BuildAvaloniaApp() -> AppBuilder`; `NovaTerminal.Shots.ShotHost` with `static ShotHost Start()`, `Task RunAsync(Func<Task> body)`, `Task<T> RunAsync<T>(Func<Task<T>> body)`, `void Dispose()`.

- [x] **Step 1: Add the Avalonia.Headless package version**

In `Directory.Packages.props`, next to the existing `Avalonia.Headless.XUnit` line, add:

```xml
    <PackageVersion Include="Avalonia.Headless" Version="12.0.4" />
```

- [x] **Step 2: Create the tool project**

`tools/NovaTerminal.Shots/NovaTerminal.Shots.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <AssemblyName>NovaTerminal.Shots</AssemblyName>
    <RootNamespace>NovaTerminal.Shots</RootNamespace>
    <OutputType>Exe</OutputType>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Avalonia.Headless" />
    <PackageReference Include="SkiaSharp" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\NovaTerminal.App\NovaTerminal.App.csproj" />
  </ItemGroup>
</Project>
```

- [x] **Step 3: Create the test project**

`tests/NovaTerminal.Shots.Tests/NovaTerminal.Shots.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <AssemblyName>NovaTerminal.Shots.Tests</AssemblyName>
    <RootNamespace>NovaTerminal.ShotsTests</RootNamespace>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.runner.visualstudio" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\tools\NovaTerminal.Shots\NovaTerminal.Shots.csproj" />
  </ItemGroup>
</Project>
```

- [x] **Step 4: Write the failing smoke test**

`tests/NovaTerminal.Shots.Tests/ShotHostSmokeTests.cs`:

```csharp
using Avalonia.Controls;
using NovaTerminal.Shots;

namespace NovaTerminal.ShotsTests;

public sealed class ShotHostSmokeTests
{
    [Fact]
    [Trait("Category", "ShotsSmoke")]
    public async Task ShotHost_RunsAWindowOnTheDispatcherThread()
    {
        using ShotHost host = ShotHost.Start();

        bool shown = await host.RunAsync(() =>
        {
            var window = new Window { Width = 320, Height = 200 };
            window.Show();
            bool visible = window.IsVisible;
            window.Close();
            return Task.FromResult(visible);
        });

        Assert.True(shown, "ShotHost could not show a window; the headless session never started.");
    }
}
```

- [x] **Step 5: Run the test and verify it fails**

```bash
scripts/build.ps1 test tests/NovaTerminal.Shots.Tests --filter "Category=ShotsSmoke"
```

Expected: FAIL — `ShotHost` does not exist, so the project does not compile.

- [x] **Step 6: Implement the app builder**

`tools/NovaTerminal.Shots/ShotsAppBuilder.cs`:

```csharp
using Avalonia;
using Avalonia.Headless;

namespace NovaTerminal.Shots;

/// <summary>
/// Entry point handed to <see cref="HeadlessUnitTestSession.StartNew"/>.
///
/// <c>UseHeadlessDrawing = false</c> is the whole point: the headless stub backend accepts
/// every draw call and produces an empty raster, which would make this tool emit blank PNGs
/// that still look like successful captures. This mirrors
/// <c>tests/NovaTerminal.App.Tests/TestAppBuilder.cs</c>.
/// </summary>
public static class ShotsAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions
        {
            UseHeadlessDrawing = false
        });
}
```

- [x] **Step 7: Implement the host**

`tools/NovaTerminal.Shots/ShotHost.cs`:

```csharp
using Avalonia.Headless;

namespace NovaTerminal.Shots;

/// <summary>
/// Owns the headless Avalonia session and the dispatcher thread every scenario runs on.
///
/// The ThreadPool minimums are raised deliberately. Issue #81 traced headless dispatcher
/// deadlocks to PTY loops occupying pool threads while a synchronous wait starved the
/// dispatcher at the default minimum of two. This tool spawns real shells, so it starts
/// from a floor that cannot reproduce that shape.
/// </summary>
public sealed class ShotHost : IDisposable
{
    private readonly HeadlessUnitTestSession _session;

    private ShotHost(HeadlessUnitTestSession session) => _session = session;

    public static ShotHost Start()
    {
        ThreadPool.GetMinThreads(out int workers, out int completionPorts);
        ThreadPool.SetMinThreads(Math.Max(workers, 16), Math.Max(completionPorts, 16));

        return new ShotHost(HeadlessUnitTestSession.StartNew(typeof(ShotsAppBuilder)));
    }

    public Task<T> RunAsync<T>(Func<Task<T>> body) => _session.Dispatch(body, CancellationToken.None);

    public Task RunAsync(Func<Task> body) => _session.Dispatch(async () =>
    {
        await body().ConfigureAwait(true);
        return true;
    }, CancellationToken.None);

    public void Dispose() => _session.Dispose();
}
```

- [x] **Step 8: Implement a placeholder entry point**

`tools/NovaTerminal.Shots/Program.cs`:

```csharp
namespace NovaTerminal.Shots;

public static class Program
{
    public static int Main(string[] args)
    {
        Console.Error.WriteLine("No scenarios registered yet. See Task 6.");
        return 1;
    }
}
```

- [x] **Step 9: Grant the tool access to internals**

Task 8 drives the agent-host acting path through `AgentHostService.HandleRequestLineAsync`, which is `internal`. That is the escape hatch the spec documents. Add to `src/NovaTerminal.App/AssemblyInfo.cs`:

```csharp
[assembly: InternalsVisibleTo("NovaTerminal.Shots")]
```

and to `src/NovaTerminal.App/NovaTerminal.App.csproj` beside the existing entries at line 507:

```xml
    <InternalsVisibleTo Include="NovaTerminal.Shots" />
```

This is the only production file the harness touches.

- [x] **Step 10: Add both projects to the solution**

```bash
rtk dotnet sln NovaTerminal.sln add tools/NovaTerminal.Shots/NovaTerminal.Shots.csproj tests/NovaTerminal.Shots.Tests/NovaTerminal.Shots.Tests.csproj
```

- [x] **Step 11: Run the test and verify it passes**

```bash
scripts/build.ps1 test tests/NovaTerminal.Shots.Tests --filter "Category=ShotsSmoke"
```

Expected: PASS, 1 test.

- [x] **Step 12: Wire CI so solution-wide test jobs keep working**

Several `ci.yml` jobs run `dotnet test` with **no project argument** and `--no-build --no-restore` (lines 630, 690, 761, 856, 914, 972). A test project present in the solution but absent from the downloaded build artifact makes those jobs fail. Three edits:

1. In the artifact path list after `.github/workflows/ci.yml:185`, add:

```yaml
            tests/NovaTerminal.Shots.Tests/bin/${{ env.CONFIGURATION }}
```

2. In the gating unit-test loop (`ci.yml:293-294`), add `Shots` to the project list and exclude the smoke category from the filter:

```bash
          filter="Category!=Replay&Category!=RenderMetrics&Category!=PtySmoke&Category!=Stress&Category!=GoldenSharedPng&Category!=ShotsSmoke"
          for proj in VT Rendering Architecture Platform McpServer Shots; do
```

3. In the coverage loop (`ci.yml:545`), add `Shots` to the project list the same way.

The smoke test is excluded from CI because it boots a real Avalonia session; it is a local guard, run on demand.

- [x] **Step 13: Verify the whole solution still builds**

```bash
scripts/build.ps1 build -c Release
```

Expected: build succeeds with no warnings from the two new projects.

- [x] **Step 14: Commit**

```bash
rtk git add tools/NovaTerminal.Shots tests/NovaTerminal.Shots.Tests Directory.Packages.props NovaTerminal.sln .github/workflows/ci.yml && rtk git commit -m "feat(shots): add headless capture host skeleton"
```

---

### Task 2: DemoWorld — isolated profile root and seeded settings

**Files:**
- Create: `tools/NovaTerminal.Shots/DemoWorld.cs`
- Create: `tests/NovaTerminal.Shots.Tests/DemoWorldTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `DemoWorld` with `static DemoWorld Create(string baseDirectory)`, properties `string ProfileRoot`, `string WorkspaceRoot`, `TerminalProfile DemoProfile`, method `void SeedSettings(Action<TerminalSettings>? customize = null)`, and `void Dispose()`. `DemoProfile.Name` is `"Demo"`.

- [x] **Step 1: Write the failing test**

`tests/NovaTerminal.Shots.Tests/DemoWorldTests.cs`:

```csharp
using NovaTerminal.Shots;

namespace NovaTerminal.ShotsTests;

public sealed class DemoWorldTests
{
    private static string NewBaseDir() =>
        Path.Combine(Path.GetTempPath(), "nova-shots-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void SeedSettings_WritesSettingsUnderTheIsolatedRoot_NotTheRealProfile()
    {
        string baseDir = NewBaseDir();
        using var world = DemoWorld.Create(baseDir);

        world.SeedSettings();

        string settingsPath = Path.Combine(world.ProfileRoot, "settings.json");
        Assert.True(File.Exists(settingsPath), $"Expected seeded settings at {settingsPath}.");
        Assert.StartsWith(baseDir, world.ProfileRoot, StringComparison.Ordinal);
        Assert.Equal(world.ProfileRoot, Environment.GetEnvironmentVariable("NOVATERM_APPDATA_ROOT"));
    }

    [Fact]
    public void SeedSettings_PinsTheDemoProfileAsDefault()
    {
        using var world = DemoWorld.Create(NewBaseDir());

        world.SeedSettings();

        string json = File.ReadAllText(Path.Combine(world.ProfileRoot, "settings.json"));
        Assert.Contains("\"Demo\"", json, StringComparison.Ordinal);
        Assert.Equal("Demo", world.DemoProfile.Name);
    }

    [Fact]
    public void Dispose_RemovesEverythingItCreated()
    {
        string baseDir = NewBaseDir();
        var world = DemoWorld.Create(baseDir);
        world.SeedSettings();

        world.Dispose();

        Assert.False(Directory.Exists(baseDir), "DemoWorld left files behind after disposal.");
    }
}
```

- [x] **Step 2: Run the test and verify it fails**

```bash
scripts/build.ps1 test tests/NovaTerminal.Shots.Tests --filter "FullyQualifiedName~DemoWorldTests"
```

Expected: FAIL — `DemoWorld` is not defined.

- [x] **Step 3: Implement DemoWorld**

`tools/NovaTerminal.Shots/DemoWorld.cs`:

```csharp
using System.Runtime.InteropServices;
using NovaTerminal.Shell;

namespace NovaTerminal.Shots;

/// <summary>
/// The fictional machine every screenshot is taken on: an isolated NovaTerminal profile
/// root, a scratch workspace, and a shell profile that ignores the developer's dotfiles.
///
/// Setting NOVATERM_APPDATA_ROOT is load-bearing rather than tidy. MainWindow's constructor
/// calls TerminalSettings.Load(), and several ordinary actions call Save(), so without the
/// override a capture run would both read the developer's live settings into public images
/// and be able to rewrite them.
/// </summary>
public sealed class DemoWorld : IDisposable
{
    private const string RootOverrideEnvVar = "NOVATERM_APPDATA_ROOT";

    private readonly string _baseDirectory;
    private readonly string? _previousRootOverride;

    private DemoWorld(string baseDirectory, string? previousRootOverride)
    {
        _baseDirectory = baseDirectory;
        _previousRootOverride = previousRootOverride;
        ProfileRoot = Path.Combine(baseDirectory, "profile");
        WorkspaceRoot = Path.Combine(baseDirectory, "workspace", "nova-demo");
        DemoProfile = BuildDemoProfile(WorkspaceRoot);
    }

    public string ProfileRoot { get; }

    public string WorkspaceRoot { get; }

    public TerminalProfile DemoProfile { get; }

    public static DemoWorld Create(string baseDirectory)
    {
        string? previous = Environment.GetEnvironmentVariable(RootOverrideEnvVar);
        var world = new DemoWorld(baseDirectory, previous);

        Directory.CreateDirectory(world.ProfileRoot);
        Directory.CreateDirectory(world.WorkspaceRoot);
        Environment.SetEnvironmentVariable(RootOverrideEnvVar, world.ProfileRoot);

        return world;
    }

    private static TerminalProfile BuildDemoProfile(string workspaceRoot)
    {
        bool windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        return new TerminalProfile
        {
            Name = "Demo",
            Command = windows ? "pwsh.exe" : "/bin/bash",
            Arguments = windows ? "-NoProfile -NoLogo" : "--noprofile --norc",
            StartingDirectory = workspaceRoot,
            Type = ConnectionType.Local
        };
    }

    /// <summary>
    /// Writes a settings.json into the isolated root with everything a screenshot depends on
    /// pinned. <paramref name="customize"/> lets a scenario change theme or tab orientation
    /// without touching anything else.
    /// </summary>
    public void SeedSettings(Action<TerminalSettings>? customize = null)
    {
        var settings = new TerminalSettings
        {
            ThemeName = "Dracula",
            FontFamily = "Cascadia Code PL, CaskaydiaCove Nerd Font, Cascadia Code, Consolas, Monospace",
            FontSize = 14
        };

        settings.Profiles.Clear();
        settings.Profiles.Add(DemoProfile);
        settings.DefaultProfileId = DemoProfile.Id;

        customize?.Invoke(settings);
        settings.Save();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(RootOverrideEnvVar, _previousRootOverride);

        try
        {
            if (Directory.Exists(_baseDirectory))
            {
                Directory.Delete(_baseDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A shell that has not fully exited can hold a handle in the workspace. Leaving a
            // temp directory behind is a worse outcome than a failed run only in theory; make
            // it visible rather than throwing out of Dispose.
            Console.Error.WriteLine($"[shots] could not remove demo world at {_baseDirectory}");
        }
    }
}
```

- [x] **Step 4: Run the tests and verify they pass**

```bash
scripts/build.ps1 test tests/NovaTerminal.Shots.Tests --filter "FullyQualifiedName~DemoWorldTests"
```

Expected: PASS, 3 tests.

- [x] **Step 5: Verify `DefaultProfileId` and `Profiles` exist with those names**

If compilation fails on `settings.DefaultProfileId` or `settings.Profiles`, read `src/NovaTerminal.App/Shell/TerminalSettings.cs` around line 203 (where the built-in profile list is constructed) and use the real member names. Do not invent members.

- [x] **Step 6: Commit**

```bash
rtk git add tools/NovaTerminal.Shots/DemoWorld.cs tests/NovaTerminal.Shots.Tests/DemoWorldTests.cs && rtk git commit -m "feat(shots): isolate captures in a seeded demo profile root"
```

---

### Task 3: DemoWorld — seeded git workspace and demo scripts

Gives the shell something real and attractive to print. The scripts are authored by us, but a real shell really runs them, which is what keeps the output authentic without depending on the developer's machine.

**Files:**
- Modify: `tools/NovaTerminal.Shots/DemoWorld.cs`
- Create: `tools/NovaTerminal.Shots/Assets/nova-banner.sh`, `tools/NovaTerminal.Shots/Assets/demo-test.sh`, `tools/NovaTerminal.Shots/Assets/sixel-decoder.rs`
- Modify: `tools/NovaTerminal.Shots/NovaTerminal.Shots.csproj` (copy assets to output)
- Modify: `tests/NovaTerminal.Shots.Tests/DemoWorldTests.cs`

**Interfaces:**
- Consumes: `DemoWorld` from Task 2.
- Produces: `DemoWorld.SeedWorkspace()`, which leaves `WorkspaceRoot` a git repo on branch `feat/sixel-decoder` containing `scripts/nova-banner.sh`, `scripts/demo-test.sh`, and `src/sixel-decoder.rs`.

- [x] **Step 1: Write the failing test**

Append to `tests/NovaTerminal.Shots.Tests/DemoWorldTests.cs`:

```csharp
    [Fact]
    public void SeedWorkspace_CreatesAGitRepoOnTheDemoBranch()
    {
        using var world = DemoWorld.Create(NewBaseDir());

        world.SeedWorkspace();

        Assert.True(Directory.Exists(Path.Combine(world.WorkspaceRoot, ".git")));
        Assert.True(File.Exists(Path.Combine(world.WorkspaceRoot, "scripts", "nova-banner.sh")));
        Assert.True(File.Exists(Path.Combine(world.WorkspaceRoot, "src", "sixel-decoder.rs")));

        string head = File.ReadAllText(Path.Combine(world.WorkspaceRoot, ".git", "HEAD"));
        Assert.Contains("feat/sixel-decoder", head, StringComparison.Ordinal);
    }
```

- [x] **Step 2: Run the test and verify it fails**

```bash
scripts/build.ps1 test tests/NovaTerminal.Shots.Tests --filter "FullyQualifiedName~SeedWorkspace"
```

Expected: FAIL — `SeedWorkspace` is not defined.

- [x] **Step 3: Author the demo scripts**

`tools/NovaTerminal.Shots/Assets/nova-banner.sh` — prints a compact colored identity block. Uses only ANSI SGR so it renders identically on both platforms:

```bash
#!/usr/bin/env bash
printf '\033[38;5;213m  ███╗   ██╗ ██████╗ ██╗   ██╗ █████╗ \033[0m\n'
printf '\033[38;5;213m  ████╗  ██║██╔═══██╗██║   ██║██╔══██╗\033[0m\n'
printf '\033[38;5;213m  ██╔██╗ ██║██║   ██║██║   ██║███████║\033[0m\n'
printf '\033[38;5;177m  ██║╚██╗██║██║   ██║╚██╗ ██╔╝██╔══██║\033[0m\n'
printf '\033[38;5;177m  ██║ ╚████║╚██████╔╝ ╚████╔╝ ██║  ██║\033[0m\n'
printf '\n'
printf '  \033[1mterminal\033[0m   NovaTerminal 0.9.0 (win-x64)\n'
printf '  \033[1mengine\033[0m     VT parser · conformance matrix 100%%\n'
printf '  \033[1mrenderer\033[0m   Skia · GPU glyph cache\n'
printf '  \033[1mbackend\033[0m    Rust PTY\n'
printf '  \033[1magents\033[0m     MCP observe \033[32m●\033[0m  act \033[32m●\033[0m\n'
printf '\n'
```

`tools/NovaTerminal.Shots/Assets/demo-test.sh` — a passing test run with color:

```bash
#!/usr/bin/env bash
printf '\033[90mRunning 6 test suites…\033[0m\n\n'
for suite in "vt::parser" "vt::reflow" "render::glyph_cache" "pty::session" "replay::roundtrip" "agent::journal"; do
  printf '  \033[32m✓\033[0m %-24s \033[90m%s\033[0m\n' "$suite" "$(( (RANDOM % 40) + 4 ))ms"
done
printf '\n\033[32m  6 passed\033[0m \033[90m·\033[0m 0 failed \033[90m·\033[0m 0 skipped\n\n'
```

`tools/NovaTerminal.Shots/Assets/sixel-decoder.rs` — 40-60 lines of plausible Rust with comments and a struct, so `tui-vim` has something worth showing. Write real, compiling-looking code: a `SixelDecoder` struct with a `state: DecoderState` enum, a `feed(&mut self, byte: u8)` match, and a doc comment.

- [x] **Step 4: Copy assets to output**

In `tools/NovaTerminal.Shots/NovaTerminal.Shots.csproj`, add:

```xml
  <ItemGroup>
    <Content Include="Assets\**\*.*" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

- [x] **Step 5: Implement SeedWorkspace**

Add to `DemoWorld`:

```csharp
    private const string CommitDate = "2026-08-20T10:15:00+00:00";

    /// <summary>
    /// Lays down the demo project and a scripted git history. Author and committer identity
    /// and dates are fixed so `git log --graph` renders the same story on every run and on
    /// every machine.
    /// </summary>
    public void SeedWorkspace()
    {
        string assets = Path.Combine(AppContext.BaseDirectory, "Assets");
        Directory.CreateDirectory(Path.Combine(WorkspaceRoot, "scripts"));
        Directory.CreateDirectory(Path.Combine(WorkspaceRoot, "src"));

        CopyAsset(assets, "nova-banner.sh", Path.Combine(WorkspaceRoot, "scripts", "nova-banner.sh"));
        CopyAsset(assets, "demo-test.sh", Path.Combine(WorkspaceRoot, "scripts", "demo-test.sh"));
        CopyAsset(assets, "sixel-decoder.rs", Path.Combine(WorkspaceRoot, "src", "sixel-decoder.rs"));

        Git("init --initial-branch=feat/sixel-decoder");
        Git("config user.name nova");
        Git("config user.email nova@demo");
        Git("add .");
        Commit("feat(vt): add sixel decoder skeleton");

        File.AppendAllText(Path.Combine(WorkspaceRoot, "src", "sixel-decoder.rs"),
            "\n// TODO: raster attributes\n");
        Git("add .");
        Commit("feat(vt): parse sixel raster attributes");

        File.WriteAllText(Path.Combine(WorkspaceRoot, "README.md"), "# nova-demo\n");
        Git("add .");
        Commit("docs: describe the decoder pipeline");
    }

    private static void CopyAsset(string assetsDirectory, string name, string destination)
    {
        File.Copy(Path.Combine(assetsDirectory, name), destination, overwrite: true);
    }

    private void Commit(string message) => Git($"commit -m \"{message}\"");

    private void Git(string arguments)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = WorkspaceRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.Environment["GIT_AUTHOR_DATE"] = CommitDate;
        psi.Environment["GIT_COMMITTER_DATE"] = CommitDate;

        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start git.");
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {arguments} failed with {process.ExitCode}: {process.StandardError.ReadToEnd()}");
        }
    }
```

- [x] **Step 6: Run the tests and verify they pass**

```bash
scripts/build.ps1 test tests/NovaTerminal.Shots.Tests --filter "FullyQualifiedName~DemoWorldTests"
```

Expected: PASS, 4 tests.

- [x] **Step 7: Commit**

```bash
rtk git add tools/NovaTerminal.Shots tests/NovaTerminal.Shots.Tests && rtk git commit -m "feat(shots): seed a scripted demo workspace and git history"
```

---

### Task 4: Rasterizer

**Files:**
- Create: `tools/NovaTerminal.Shots/Rasterizer.cs`
- Create: `tests/NovaTerminal.Shots.Tests/RasterizerTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `Rasterizer` with `static SKBitmap CaptureWindow(Window window, double scale)`, `static void WritePng(SKBitmap bitmap, string path)`, `static double InkFraction(SKBitmap bitmap)`.

`InkFraction` returns the share of pixels differing from the image's most common color — the same blank-raster guard `CommandAssistOverlayContentRenderTests` uses. It is pure and testable without Avalonia, which is why it is the unit under test here.

- [x] **Step 1: Write the failing test**

`tests/NovaTerminal.Shots.Tests/RasterizerTests.cs`:

```csharp
using NovaTerminal.Shots;
using SkiaSharp;

namespace NovaTerminal.ShotsTests;

public sealed class RasterizerTests
{
    private static SKBitmap Uniform(int width, int height, SKColor color)
    {
        var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(color);
        return bitmap;
    }

    [Fact]
    public void InkFraction_IsZeroForABlankImage()
    {
        using SKBitmap blank = Uniform(64, 64, SKColors.Black);

        Assert.Equal(0.0, Rasterizer.InkFraction(blank), precision: 6);
    }

    [Fact]
    public void InkFraction_CountsPixelsThatDifferFromTheDominantColour()
    {
        using SKBitmap bitmap = Uniform(10, 10, SKColors.Black);
        for (int x = 0; x < 10; x++)
        {
            bitmap.SetPixel(x, 0, SKColors.White);
        }

        Assert.Equal(0.10, Rasterizer.InkFraction(bitmap), precision: 6);
    }

    [Fact]
    public void WritePng_CreatesADecodableFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"shots-{Guid.NewGuid():N}.png");
        using SKBitmap bitmap = Uniform(8, 8, SKColors.Red);

        try
        {
            Rasterizer.WritePng(bitmap, path);

            using SKBitmap? decoded = SKBitmap.Decode(path);
            Assert.NotNull(decoded);
            Assert.Equal(8, decoded!.Width);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
```

- [x] **Step 2: Run the test and verify it fails**

```bash
scripts/build.ps1 test tests/NovaTerminal.Shots.Tests --filter "FullyQualifiedName~RasterizerTests"
```

Expected: FAIL — `Rasterizer` is not defined.

- [x] **Step 3: Implement Rasterizer**

`tools/NovaTerminal.Shots/Rasterizer.cs`:

```csharp
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SkiaSharp;

namespace NovaTerminal.Shots;

public static class Rasterizer
{
    /// <summary>
    /// Rasterizes the whole window, chrome included. CaptureRenderedFrame is used rather than
    /// RenderTargetBitmap because the window's own title bar, tab strip, and every in-window
    /// overlay must appear; MainWindow sets ExtendClientAreaToDecorationsHint, so this frame
    /// contains essentially the entire visual identity.
    /// </summary>
    public static SKBitmap CaptureWindow(Window window, double scale)
    {
        window.SetRenderScaling(scale);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();

        using WriteableBitmap frame = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException(
                "CaptureRenderedFrame returned null. The window is not rendering — check that " +
                "ShotsAppBuilder still sets UseHeadlessDrawing = false.");

        using var stream = new MemoryStream();
        frame.Save(stream);
        stream.Position = 0;

        return SKBitmap.Decode(stream)
            ?? throw new InvalidOperationException("Could not decode the captured frame.");
    }

    public static void WritePng(SKBitmap bitmap, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using SKData data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using FileStream file = File.Create(path);
        data.SaveTo(file);
    }

    /// <summary>
    /// Share of pixels differing from the image's most common colour. A capture that comes back
    /// near zero is a blank raster, which is the failure mode that looks like success.
    /// </summary>
    public static double InkFraction(SKBitmap bitmap)
    {
        var counts = new Dictionary<uint, int>();

        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                uint key = (uint)bitmap.GetPixel(x, y);
                counts[key] = counts.TryGetValue(key, out int existing) ? existing + 1 : 1;
            }
        }

        int total = bitmap.Width * bitmap.Height;
        int dominant = counts.Values.Max();

        return (double)(total - dominant) / total;
    }
}
```

- [x] **Step 4: Run the tests and verify they pass**

```bash
scripts/build.ps1 test tests/NovaTerminal.Shots.Tests --filter "FullyQualifiedName~RasterizerTests"
```

Expected: PASS, 3 tests.

- [x] **Step 5: Commit**

```bash
rtk git add tools/NovaTerminal.Shots/Rasterizer.cs tests/NovaTerminal.Shots.Tests/RasterizerTests.cs && rtk git commit -m "feat(shots): rasterize the full window with a blank-raster guard"
```

---

### Task 5: Driver — input, waiting, and reflection access

**Files:**
- Create: `tools/NovaTerminal.Shots/Driver.cs`
- Create: `tests/NovaTerminal.Shots.Tests/DriverWaitTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `Driver` with constructor `Driver(Window window)` and members `void Pump(int rounds = 3)`, `void PressKey(Key key, RawInputModifiers modifiers, PhysicalKey physicalKey, string? text)`, `void TypeText(string text)`, `void WaitFor(Func<bool> condition, TimeSpan timeout, string description)`, `T Require<T>(string name) where T : Control`, `object? InvokePrivate(object target, string method, params object?[] arguments)`.

`WaitFor` is the only piece that is unit-testable without a window, so it is what the test covers; the rest is exercised by every scenario.

- [x] **Step 1: Write the failing test**

`tests/NovaTerminal.Shots.Tests/DriverWaitTests.cs`:

```csharp
using NovaTerminal.Shots;

namespace NovaTerminal.ShotsTests;

public sealed class DriverWaitTests
{
    [Fact]
    public void WaitFor_ThrowsWithTheDescriptionWhenTheConditionNeverHolds()
    {
        var exception = Assert.Throws<TimeoutException>(() =>
            Driver.WaitFor(() => false, TimeSpan.FromMilliseconds(50), "the prompt to appear", pump: () => { }));

        Assert.Contains("the prompt to appear", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WaitFor_ReturnsAsSoonAsTheConditionHolds()
    {
        int calls = 0;

        Driver.WaitFor(() => ++calls >= 3, TimeSpan.FromSeconds(5), "three polls", pump: () => { });

        Assert.Equal(3, calls);
    }
}
```

- [x] **Step 2: Run the test and verify it fails**

```bash
scripts/build.ps1 test tests/NovaTerminal.Shots.Tests --filter "FullyQualifiedName~DriverWaitTests"
```

Expected: FAIL — `Driver` is not defined.

- [x] **Step 3: Implement Driver**

`tools/NovaTerminal.Shots/Driver.cs`:

```csharp
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;

namespace NovaTerminal.Shots;

/// <summary>
/// Drives the window the way a user would. Key presses go through the app's real bindings,
/// so a scenario that produces a wrong image is telling you a shortcut broke — which is more
/// useful than a capture that quietly bypasses the binding and always succeeds.
///
/// Private MainWindow members are reached by reflection. That is the established pattern in
/// this repo (see MainWindowStartupTests, which invokes ToggleCommandPalette and
/// RegisterPaneOwners the same way) and it keeps this tool from forcing production changes.
/// </summary>
public sealed class Driver
{
    private readonly Window _window;

    public Driver(Window window) => _window = window;

    public void Pump(int rounds = 3)
    {
        for (int i = 0; i < rounds; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    public void PressKey(Key key, RawInputModifiers modifiers, PhysicalKey physicalKey, string? text)
    {
        _window.KeyPress(key, modifiers, physicalKey, text ?? string.Empty);
        Pump();
    }

    public void TypeText(string text)
    {
        _window.KeyTextInput(text);
        Pump();
    }

    public T Require<T>(string name) where T : Control =>
        _window.FindControl<T>(name)
        ?? throw new InvalidOperationException(
            $"No control named '{name}' of type {typeof(T).Name}. The markup changed — update the scenario.");

    public object? InvokePrivate(object target, string method, params object?[] arguments)
    {
        MethodInfo info = target.GetType().GetMethod(
            method,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException(
                $"{target.GetType().Name} has no method '{method}'. It was renamed — update the scenario.");

        object? result = info.Invoke(target, arguments);
        Pump();
        return result;
    }

    public void WaitFor(Func<bool> condition, TimeSpan timeout, string description) =>
        WaitFor(condition, timeout, description, () => Pump(1));

    /// <summary>Pump-agnostic core, so the timeout behaviour can be unit tested off the UI thread.</summary>
    public static void WaitFor(Func<bool> condition, TimeSpan timeout, string description, Action pump)
    {
        DateTime deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            pump();
            Thread.Sleep(10);
        }

        throw new TimeoutException(
            $"Timed out after {timeout.TotalSeconds:F1}s waiting for {description}. " +
            "A capture must never proceed from an unsettled frame, so this fails rather than " +
            "producing a half-drawn image.");
    }
}
```

- [x] **Step 4: Run the tests and verify they pass**

```bash
scripts/build.ps1 test tests/NovaTerminal.Shots.Tests --filter "FullyQualifiedName~DriverWaitTests"
```

Expected: PASS, 2 tests.

- [x] **Step 5: Commit**

```bash
rtk git add tools/NovaTerminal.Shots/Driver.cs tests/NovaTerminal.Shots.Tests/DriverWaitTests.cs && rtk git commit -m "feat(shots): drive the window through real input with settle waits"
```

---

### Task 6: Scenario model, first real shot, CLI, and manifest

The first end-to-end capture. After this task, `scripts/shots.ps1 hero-single` writes a real PNG.

**Files:**
- Create: `tools/NovaTerminal.Shots/ShotSpec.cs`, `IScenario.cs`, `ShotContext.cs`, `ShotRun.cs`, `Manifest.cs`, `ScenarioCatalog.cs`
- Create: `tools/NovaTerminal.Shots/Scenarios/HeroSingleScenario.cs`
- Modify: `tools/NovaTerminal.Shots/Program.cs`
- Create: `scripts/shots.ps1`
- Create: `tests/NovaTerminal.Shots.Tests/ScenarioCatalogTests.cs`, `ManifestTests.cs`

**Interfaces:**
- Consumes: `ShotHost`, `DemoWorld`, `Rasterizer`, `Driver`.
- Produces:
  - `record ShotSpec(string Name, int Tier, int LogicalWidth, int LogicalHeight, string Intent)`
  - `interface IScenario { ShotSpec Spec { get; } Task RunAsync(ShotContext context); }`
  - `ShotContext` with `MainWindow Window`, `Driver Driver`, `DemoWorld World`, `TerminalPane OpenTab(TerminalProfile profile)`, `Task RunCommandAsync(TerminalPane pane, string command)`, `void Capture(string? suffix = null)`
  - `record ShotAsset(string Name, int Tier, string File, int Width, int Height, string Scenario, string Commit, string Os, string TimestampUtc)`
  - `ScenarioCatalog.All()` and `ScenarioCatalog.Find(string name)`

- [x] **Step 1: Write the failing catalog test**

`tests/NovaTerminal.Shots.Tests/ScenarioCatalogTests.cs`:

```csharp
using NovaTerminal.Shots;

namespace NovaTerminal.ShotsTests;

public sealed class ScenarioCatalogTests
{
    [Fact]
    public void EveryScenarioHasAUniqueName()
    {
        string[] names = ScenarioCatalog.All().Select(s => s.Spec.Name).ToArray();

        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryScenarioStatesItsIntent()
    {
        foreach (IScenario scenario in ScenarioCatalog.All())
        {
            Assert.False(
                string.IsNullOrWhiteSpace(scenario.Spec.Intent),
                $"Scenario '{scenario.Spec.Name}' has no Intent. Claude reads Intent to judge whether " +
                "the produced image is right, so a blank one makes the review step useless.");
        }
    }

    [Fact]
    public void Find_ReturnsTheNamedScenario()
    {
        Assert.Equal("hero-single", ScenarioCatalog.Find("hero-single")!.Spec.Name);
        Assert.Null(ScenarioCatalog.Find("no-such-shot"));
    }
}
```

- [x] **Step 2: Run the test and verify it fails**

```bash
scripts/build.ps1 test tests/NovaTerminal.Shots.Tests --filter "FullyQualifiedName~ScenarioCatalogTests"
```

Expected: FAIL — `ScenarioCatalog` is not defined.

- [x] **Step 3: Implement the model types**

`tools/NovaTerminal.Shots/ShotSpec.cs`:

```csharp
namespace NovaTerminal.Shots;

/// <param name="Intent">
/// What the finished image must show, in plain language. This is the sentence Claude judges
/// the produced PNG against during the /shots review loop, so write it as an observable claim
/// ("the palette is open with results filtered"), not a title.
/// </param>
public sealed record ShotSpec(
    string Name,
    int Tier,
    int LogicalWidth,
    int LogicalHeight,
    string Intent);
```

`tools/NovaTerminal.Shots/IScenario.cs`:

```csharp
using NovaTerminal.Shell;

namespace NovaTerminal.Shots;

public interface IScenario
{
    ShotSpec Spec { get; }

    /// <summary>
    /// Settings this scenario needs seeded before its window is constructed, or null for the
    /// defaults. It must be applied before construction rather than after: MainWindow reads
    /// TerminalSettings in its constructor, so a theme or tab-orientation change made later
    /// only half-applies and produces an image that looks almost right.
    /// </summary>
    Action<TerminalSettings>? Settings => null;

    Task RunAsync(ShotContext context);
}
```

`tools/NovaTerminal.Shots/Manifest.cs`:

```csharp
using System.Text.Json;

namespace NovaTerminal.Shots;

public sealed record ShotAsset(
    string Name,
    int Tier,
    string File,
    int Width,
    int Height,
    string Scenario,
    string Commit,
    string Os,
    string TimestampUtc);

public static class Manifest
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static void Write(string path, IReadOnlyList<ShotAsset> assets)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(assets, Options));
    }

    public static IReadOnlyList<ShotAsset> Read(string path) =>
        JsonSerializer.Deserialize<List<ShotAsset>>(File.ReadAllText(path)) ?? [];
}
```

- [x] **Step 4: Implement ShotRun and ShotContext**

`tools/NovaTerminal.Shots/ShotRun.cs`:

```csharp
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NovaTerminal.Shots;

/// <summary>Output directory, scale, and the manifest accumulated across one invocation.</summary>
public sealed class ShotRun
{
    private readonly List<ShotAsset> _assets = [];

    public ShotRun(string outputDirectory, double scale)
    {
        OutputDirectory = outputDirectory;
        Scale = scale;
        Commit = ReadCommit();
        Os = RuntimeInformation.RuntimeIdentifier;
    }

    public string OutputDirectory { get; }

    public double Scale { get; }

    public string Commit { get; }

    public string Os { get; }

    public IReadOnlyList<ShotAsset> Assets => _assets;

    public void Record(ShotAsset asset) => _assets.Add(asset);

    public void WriteManifest() =>
        Manifest.Write(Path.Combine(OutputDirectory, "shots.json"), _assets);

    private static string ReadCommit()
    {
        try
        {
            var psi = new ProcessStartInfo("git", "rev-parse --short HEAD")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            using Process? process = Process.Start(psi);
            return process?.StandardOutput.ReadToEnd().Trim() ?? "unknown";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "unknown";
        }
    }
}
```

`tools/NovaTerminal.Shots/ShotContext.cs`:

```csharp
using Avalonia.Controls;
using NovaTerminal.Controls;
using NovaTerminal.Pty;
using NovaTerminal.Shell;
using SkiaSharp;

namespace NovaTerminal.Shots;

public sealed class ShotContext
{
    private readonly IScenario _scenario;

    public ShotContext(MainWindow window, Driver driver, DemoWorld world, ShotRun run, IScenario scenario)
    {
        Window = window;
        Driver = driver;
        World = world;
        Run = run;
        _scenario = scenario;
    }

    public MainWindow Window { get; }

    public Driver Driver { get; }

    public DemoWorld World { get; }

    public ShotRun Run { get; }

    /// <summary>
    /// Opens a tab through MainWindow's own AddTab, so the pane is wired, registered with the
    /// agent-session registry, and themed exactly as a user-opened tab is.
    /// </summary>
    public TerminalPane OpenTab(TerminalProfile profile)
    {
        Driver.InvokePrivate(Window, "AddTab", profile, SshDiagnosticsLevel.None);

        var tabs = Window.FindControl<TabControl>("Tabs")
            ?? throw new InvalidOperationException("MainWindow has no 'Tabs' control.");

        var selected = tabs.SelectedItem as TabItem
            ?? throw new InvalidOperationException("AddTab did not select a tab.");

        TerminalPane pane = FindPane(selected)
            ?? throw new InvalidOperationException("The new tab contains no TerminalPane.");

        Driver.WaitFor(
            () => pane.Session is not null && pane.IsProcessRunning,
            TimeSpan.FromSeconds(30),
            $"the shell in the '{profile.Name}' profile to start");

        return pane;
    }

    private static TerminalPane? FindPane(Control control) => control switch
    {
        TerminalPane pane => pane,
        ContentControl content when content.Content is Control inner => FindPane(inner),
        Decorator decorator when decorator.Child is Control child => FindPane(child),
        Panel panel => panel.Children.OfType<Control>().Select(FindPane).FirstOrDefault(p => p is not null),
        _ => null
    };

    /// <summary>Sends a command and waits for the pane's output to go quiet.</summary>
    public Task RunCommandAsync(TerminalPane pane, string command)
    {
        ITerminalSession session = pane.Session
            ?? throw new InvalidOperationException("The pane has no session.");

        session.SendInput(command + "\n");
        WaitForQuiet(pane, TimeSpan.FromMilliseconds(600), TimeSpan.FromSeconds(30), command);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Waits until the rendered frame stops changing. Sleeping a fixed interval would either
    /// truncate a slow command or waste time on a fast one; comparing frames measures the thing
    /// that actually matters — that the image is finished.
    /// </summary>
    private void WaitForQuiet(TerminalPane pane, TimeSpan quietFor, TimeSpan timeout, string what)
    {
        string? previous = null;
        DateTime quietSince = DateTime.UtcNow;

        Driver.WaitFor(
            () =>
            {
                using SKBitmap frame = Rasterizer.CaptureWindow(Window, 1.0);
                string fingerprint = Fingerprint(frame);

                if (fingerprint != previous)
                {
                    previous = fingerprint;
                    quietSince = DateTime.UtcNow;
                    return false;
                }

                return DateTime.UtcNow - quietSince >= quietFor;
            },
            timeout,
            $"output of '{what}' to settle");
    }

    private static string Fingerprint(SKBitmap bitmap)
    {
        using SKData data = bitmap.Encode(SKEncodedImageFormat.Png, 20);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data.AsSpan()));
    }

    /// <summary>Captures the window and records it in the run manifest.</summary>
    public void Capture(string? suffix = null)
    {
        string name = suffix is null ? _scenario.Spec.Name : $"{_scenario.Spec.Name}-{suffix}";
        string path = Path.Combine(Run.OutputDirectory, $"{name}@{Run.Scale:0}x.png");

        using SKBitmap bitmap = Rasterizer.CaptureWindow(Window, Run.Scale);

        double ink = Rasterizer.InkFraction(bitmap);
        if (ink < 0.01)
        {
            throw new InvalidOperationException(
                $"'{name}' rasterized to a near-uniform image ({ink:P2} ink). That is the blank-raster " +
                "failure mode, not a screenshot. Check that the window laid out and the scenario waited.");
        }

        Rasterizer.WritePng(bitmap, path);

        Run.Record(new ShotAsset(
            Name: name,
            Tier: _scenario.Spec.Tier,
            File: path,
            Width: bitmap.Width,
            Height: bitmap.Height,
            Scenario: _scenario.Spec.Name,
            Commit: Run.Commit,
            Os: Run.Os,
            TimestampUtc: DateTime.UtcNow.ToString("O")));
    }
}
```

- [x] **Step 5: Implement the first scenario**

`tools/NovaTerminal.Shots/Scenarios/HeroSingleScenario.cs`:

```csharp
using NovaTerminal.Controls;

namespace NovaTerminal.Shots.Scenarios;

internal sealed class HeroSingleScenario : IScenario
{
    public ShotSpec Spec { get; } = new(
        Name: "hero-single",
        Tier: 1,
        LogicalWidth: 1280,
        LogicalHeight: 800,
        Intent: "A single calm pane showing the Nova banner, a short git status, and a passing " +
                "test run. No overlays open, no empty space below the prompt, colours clearly visible.");

    public async Task RunAsync(ShotContext context)
    {
        TerminalPane pane = context.OpenTab(context.World.DemoProfile);

        await context.RunCommandAsync(pane, "clear");
        await context.RunCommandAsync(pane, "bash scripts/nova-banner.sh");
        await context.RunCommandAsync(pane, "git status --short --branch");
        await context.RunCommandAsync(pane, "bash scripts/demo-test.sh");

        context.Capture();
    }
}
```

- [x] **Step 6: Implement the catalog**

`tools/NovaTerminal.Shots/ScenarioCatalog.cs`:

```csharp
using NovaTerminal.Shots.Scenarios;

namespace NovaTerminal.Shots;

public static class ScenarioCatalog
{
    private static readonly IScenario[] Scenarios =
    [
        new HeroSingleScenario()
    ];

    public static IReadOnlyList<IScenario> All() => Scenarios;

    public static IScenario? Find(string name) =>
        Scenarios.FirstOrDefault(s => string.Equals(s.Spec.Name, name, StringComparison.OrdinalIgnoreCase));
}
```

- [x] **Step 7: Implement the CLI**

`tools/NovaTerminal.Shots/Program.cs`:

```csharp
using Avalonia.Controls;
using NovaTerminal.Shell;

namespace NovaTerminal.Shots;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--list"))
        {
            foreach (IScenario scenario in ScenarioCatalog.All().OrderBy(s => s.Spec.Tier))
            {
                Console.WriteLine($"{scenario.Spec.Tier}  {scenario.Spec.Name,-24}{scenario.Spec.Intent}");
            }

            return 0;
        }

        string outputDirectory = ArgumentValue(args, "--out")
            ?? Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "shots");
        double scale = double.TryParse(ArgumentValue(args, "--scale"), out double parsed) ? parsed : 2.0;

        IReadOnlyList<IScenario> requested = ResolveScenarios(args);
        if (requested.Count == 0)
        {
            Console.Error.WriteLine("No matching scenarios. Use --list to see them.");
            return 1;
        }

        string baseDirectory = Path.Combine(Path.GetTempPath(), "nova-shots", Guid.NewGuid().ToString("N"));
        using var world = DemoWorld.Create(baseDirectory);
        world.SeedWorkspace();

        var run = new ShotRun(outputDirectory, scale);
        int failures = 0;

        using ShotHost host = ShotHost.Start();

        foreach (IScenario scenario in requested)
        {
            try
            {
                // Re-seeded per scenario, before the window exists, so a scenario that needs a
                // different theme or tab orientation gets it applied at construction time.
                world.SeedSettings(scenario.Settings);

                await host.RunAsync(async () =>
                {
                    var window = new MainWindow(AppServices.BuildForDesigner())
                    {
                        Width = scenario.Spec.LogicalWidth,
                        Height = scenario.Spec.LogicalHeight
                    };

                    var driver = new Driver(window);

                    try
                    {
                        window.Show();
                        driver.Pump(5);

                        await scenario.RunAsync(new ShotContext(window, driver, world, run, scenario));
                    }
                    finally
                    {
                        window.Close();
                        driver.Pump(3);
                    }
                });

                Console.WriteLine($"[shots] {scenario.Spec.Name} ok");
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine($"[shots] {scenario.Spec.Name} FAILED: {ex.Message}");
            }
        }

        run.WriteManifest();
        Console.WriteLine($"[shots] {run.Assets.Count} asset(s) in {outputDirectory}");

        return failures == 0 ? 0 : 1;
    }

    private static IReadOnlyList<IScenario> ResolveScenarios(string[] args)
    {
        string[] names = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();

        if (names.Length == 0 || names.Contains("all", StringComparer.OrdinalIgnoreCase))
        {
            return ScenarioCatalog.All();
        }

        return names.Select(ScenarioCatalog.Find).OfType<IScenario>().ToArray();
    }

    private static string? ArgumentValue(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
```

Note: the `--scale N` argument is skipped by `ResolveScenarios` only because it starts with `--`; its **value** does not. Guard it by filtering out any token that immediately follows a `--` flag. Implement that filter inside `ResolveScenarios` before shipping the task:

```csharp
        var skip = new HashSet<int>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length)
            {
                skip.Add(i + 1);
            }
        }

        string[] names = args.Where((a, i) => !skip.Contains(i) && !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
```

- [x] **Step 8: Add the runner script**

`scripts/shots.ps1`:

```powershell
#!/usr/bin/env pwsh
# Builds and runs the screenshot harness. Uses scripts/build.ps1 rather than raw `dotnet`
# for the reason documented in CLAUDE.md: raw dotnet build leaves MSBuild daemons holding
# the caller's stdout and hangs anything reading via pipes.

$ErrorActionPreference = 'Stop'

& "$PSScriptRoot/build.ps1" build -c Release tools/NovaTerminal.Shots
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$dll = Join-Path $PSScriptRoot '../tools/NovaTerminal.Shots/bin/Release/net10.0/NovaTerminal.Shots.dll'
& dotnet $dll @args
exit $LASTEXITCODE
```

- [x] **Step 9: Run the catalog tests and verify they pass**

```bash
scripts/build.ps1 test tests/NovaTerminal.Shots.Tests --filter "FullyQualifiedName~ScenarioCatalogTests"
```

Expected: PASS, 3 tests.

- [x] **Step 10: Run the harness end to end**

```bash
scripts/shots.ps1 hero-single --scale 2
```

Expected: exit 0, and `artifacts/shots/hero-single@2x.png` exists at roughly 2560×1600.

- [x] **Step 11: Look at the image**

Read `artifacts/shots/hero-single@2x.png` and check it against `HeroSingleScenario.Spec.Intent`: banner visible, colours present, prompt reads `nova@demo`, no `C:\Users\` or real hostname anywhere. If the prompt still shows the developer's identity, fix the prompt setup in `DemoWorld` before continuing — every later scenario inherits it.

- [x] **Step 12: Commit**

```bash
rtk git add tools/NovaTerminal.Shots tests/NovaTerminal.Shots.Tests scripts/shots.ps1 && rtk git commit -m "feat(shots): capture the first end-to-end hero still"
```

---

### Task 7: `settings-agent-access` scenario

First half of the agent story. Opens the settings window on the Agent Access tab with observe on and act off.

**Files:**
- Create: `tools/NovaTerminal.Shots/Scenarios/SettingsAgentAccessScenario.cs`
- Modify: `tools/NovaTerminal.Shots/ScenarioCatalog.cs`
- Modify: `tools/NovaTerminal.Shots/ShotContext.cs` (add `CaptureWindow(Window, string)`)

**Interfaces:**
- Consumes: `ShotContext`, `Driver`, `DemoWorld`.
- Produces: `ShotContext.CaptureOther(Window window, string suffix)` — captures a window other than the main one (the settings window is a separate `Window`).

- [x] **Step 1: Confirm the settings window's tab headers**

```bash
rtk grep -n 'TabItem Header' src/NovaTerminal.App/SettingsWindow.axaml
```

Expected: `Appearance`, `Profiles`, `Shortcuts`, `Command Assist`, `Agent Access`, `SSH`. Note the index of `Agent Access` (0-based, currently 4) — the scenario selects by header text, not index, but the count confirms the markup has not changed.

- [x] **Step 2: Add CaptureOther to ShotContext**

```csharp
    /// <summary>
    /// Captures a window other than the main one — the settings window is its own Window, so
    /// it never appears in a MainWindow frame.
    /// </summary>
    public void CaptureOther(Window window, string suffix)
    {
        string name = $"{_scenario.Spec.Name}-{suffix}";
        string path = Path.Combine(Run.OutputDirectory, $"{name}@{Run.Scale:0}x.png");

        using SKBitmap bitmap = Rasterizer.CaptureWindow(window, Run.Scale);

        double ink = Rasterizer.InkFraction(bitmap);
        if (ink < 0.01)
        {
            throw new InvalidOperationException($"'{name}' rasterized to a near-uniform image ({ink:P2} ink).");
        }

        Rasterizer.WritePng(bitmap, path);

        Run.Record(new ShotAsset(
            name, _scenario.Spec.Tier, path, bitmap.Width, bitmap.Height,
            _scenario.Spec.Name, Run.Commit, Run.Os, DateTime.UtcNow.ToString("O")));
    }
```

- [x] **Step 3: Implement the scenario**

`tools/NovaTerminal.Shots/Scenarios/SettingsAgentAccessScenario.cs`:

```csharp
using Avalonia.Controls;

namespace NovaTerminal.Shots.Scenarios;

/// <summary>
/// The observe/act opt-in pair. This is the differentiating screenshot, so it shows the real
/// settings surface with act deliberately OFF — the honest default, and the one that makes the
/// separate-opt-in design legible at a glance.
/// </summary>
internal sealed class SettingsAgentAccessScenario : IScenario
{
    public ShotSpec Spec { get; } = new(
        Name: "settings-agent-access",
        Tier: 2,
        LogicalWidth: 1000,
        LogicalHeight: 760,
        Intent: "The settings window on the Agent Access tab, with the observe toggle on, the act " +
                "toggle visibly off beneath it, and the explanatory text readable.");

    public Task RunAsync(ShotContext context)
    {
        var settingsWindow = new SettingsWindow();
        settingsWindow.Width = Spec.LogicalWidth;
        settingsWindow.Height = Spec.LogicalHeight;
        settingsWindow.Show();
        context.Driver.Pump(5);

        TabControl tabs = settingsWindow.GetVisualDescendants().OfType<TabControl>().First();
        TabItem agentTab = tabs.Items.OfType<TabItem>()
            .First(item => string.Equals(item.Header as string, "Agent Access", StringComparison.Ordinal));

        tabs.SelectedItem = agentTab;
        context.Driver.Pump(5);

        try
        {
            context.CaptureOther(settingsWindow, "tab");
        }
        finally
        {
            settingsWindow.Close();
            context.Driver.Pump(3);
        }

        return Task.CompletedTask;
    }
}
```

Add `using Avalonia.VisualTree;` for `GetVisualDescendants`. If `SettingsWindow`'s constructor requires arguments, read `src/NovaTerminal.App/SettingsWindow.axaml.cs` and pass what it needs — do not guess.

- [x] **Step 4: Seed the agent toggles**

In `Program.cs`, change the seeding call so agent access is enabled for the run:

```csharp
        world.SeedSettings(settings =>
        {
            // The agent scenarios photograph these toggles; the rest of the catalogue is
            // unaffected by them being on.
            settings.AgentAccessObserveEnabled = true;
            settings.AgentAccessActEnabled = false;
        });
```

Confirm the real property names first:

```bash
rtk grep -n "AgentAccess\|AgentObserve\|AgentAct" src/NovaTerminal.App/Shell/TerminalSettings.cs
```

Use the names that grep returns. If a new field is ever added to `TerminalSettings`, remember it must also be registered in `McpServer` `SettingsTools` or two drift-guard tests fail — but this task adds no fields, only sets existing ones.

- [x] **Step 5: Register the scenario**

Add `new SettingsAgentAccessScenario()` to the `Scenarios` array in `ScenarioCatalog`.

- [x] **Step 6: Run it and look at the image**

```bash
scripts/shots.ps1 settings-agent-access --scale 2
```

Expected: exit 0. Read `artifacts/shots/settings-agent-access-tab@2x.png` and confirm against the Intent: Agent Access tab selected, observe on, act off, text legible.

- [x] **Step 7: Commit**

```bash
rtk git add tools/NovaTerminal.Shots && rtk git commit -m "feat(shots): capture the agent-access opt-in settings tab"
```

---

### Task 8: `agent-session` scenario

Second half of the agent story, and the one that must not be faked: the journal in the image has to contain real entries produced by the real agent-host path.

**Files:**
- Create: `tools/NovaTerminal.Shots/Scenarios/AgentSessionScenario.cs`
- Modify: `tools/NovaTerminal.Shots/ScenarioCatalog.cs`

**Interfaces:**
- Consumes: `ShotContext`, `NovaTerminal.AgentHost.AgentSessionRegistry`.
- Produces: nothing new.

- [x] **Step 1: Confirm the wire contract still matches**

```bash
rtk grep -n "SendInputParams" -A 12 src/NovaTerminal.App/AgentHost/AgentHostService.cs | head -20
rtk grep -rn "class SendInputParams" -A 12 src/NovaTerminal.AgentHost.Contracts/
```

Expected: `AgentHostService.HandleRequestLineAsync(string line, CancellationToken)` is the line-level entry point (internal, reachable via the `InternalsVisibleTo` added in Task 1), its `sendInput` branch wraps every outcome in `Journaled(...)`, and `SendInputParams` carries `PaneId`, `Text`, and `Submit`. Note the exact `JsonPropertyName` values — the JSON below must match them character for character.

- [x] **Step 2: Implement the scenario**

`tools/NovaTerminal.Shots/Scenarios/AgentSessionScenario.cs`:

```csharp
using NovaTerminal.AgentHost;
using NovaTerminal.Controls;

namespace NovaTerminal.Shots.Scenarios;

/// <summary>
/// An agent driving a live session. The journal entries in this image are produced by the same
/// code path NovaTerminal.McpServer's send_input reaches — a staged screenshot of a security
/// surface would be exactly the wrong shortcut, because the journal's whole purpose is that it
/// records what really happened.
/// </summary>
internal sealed class AgentSessionScenario : IScenario
{
    public ShotSpec Spec { get; } = new(
        Name: "agent-session",
        Tier: 2,
        LogicalWidth: 1280,
        LogicalHeight: 800,
        Intent: "A pane with the agent indicator lit, terminal output that an agent produced, and " +
                "the activity journal listing at least two real entries with their tool names.");

    public async Task RunAsync(ShotContext context)
    {
        TerminalPane pane = context.OpenTab(context.World.DemoProfile);

        await context.RunCommandAsync(pane, "clear");
        await context.RunCommandAsync(pane, "bash scripts/nova-banner.sh");

        context.Driver.WaitFor(
            () => AgentSessionRegistry.Instance.TryGet(pane.PaneId, out _),
            TimeSpan.FromSeconds(10),
            "the pane to register with the agent-session registry");

        int journalBefore = AgentActivityJournal.Instance.Count;

        await SendAsAgentAsync(context, pane, "git log --graph --oneline -5");
        await SendAsAgentAsync(context, pane, "bash scripts/demo-test.sh");

        if (AgentActivityJournal.Instance.Count <= journalBefore)
        {
            throw new InvalidOperationException(
                "The agent journal recorded nothing, so this image would be a staged screenshot of " +
                "a security feature. Check that act is enabled and the host is running.");
        }

        OpenJournal(context);
        context.Capture();
    }

    /// <summary>
    /// Issues the request exactly as NovaTerminal.McpServer does: a protocol line into
    /// AgentHostService. Everything downstream — the act gate, the pane indicator, and the
    /// journal entry — therefore runs for real.
    /// </summary>
    private static async Task SendAsAgentAsync(ShotContext context, TerminalPane pane, string command)
    {
        string line = System.Text.Json.JsonSerializer.Serialize(new
        {
            v = AgentHostProtocol.Version,
            id = Interlocked.Increment(ref _requestId),
            method = AgentHostProtocol.Methods.SendInput,
            @params = new { paneId = pane.PaneId, text = command, submit = true }
        });

        AgentHostResponse response = await AgentHostService.Instance
            .HandleRequestLineAsync(line, CancellationToken.None);

        if (response.Error is not null)
        {
            throw new InvalidOperationException(
                $"agent sendInput was rejected: {response.Error.Code} {response.Error.Message}. " +
                "Act is probably still disabled, or the pane is not registered.");
        }

        context.Driver.Pump(10);
        await context.RunCommandAsync(pane, string.Empty);
    }

    private static long _requestId;

    private static void OpenJournal(ShotContext context)
    {
        // Opened through the palette so the shot exercises the real user path to the journal.
        context.Driver.InvokePrivate(context.Window, "ToggleCommandPalette");
        context.Driver.TypeText("journal");
        context.Driver.Pump(5);

        var list = context.Driver.Require<Avalonia.Controls.ListBox>("CommandList");
        if (list.ItemCount == 0)
        {
            throw new InvalidOperationException(
                "No palette command matches 'journal'. Find the journal's real entry point with " +
                "`rtk grep -rn \"Journal\" src/NovaTerminal.App/MainWindow.axaml.cs` and open that instead.");
        }

        context.Driver.PressKey(
            Avalonia.Input.Key.Enter,
            Avalonia.Input.RawInputModifiers.None,
            Avalonia.Input.PhysicalKey.Enter,
            "\r");
    }
}
```

Add `using NovaTerminal.AgentHost.Contracts;` for `AgentHostProtocol` and `AgentHostResponse`.

`RunCommandAsync(pane, string.Empty)` is used only for its settle wait — the input was already delivered by the agent path, and this waits for the resulting output to finish drawing. If `RunCommandAsync` rejects an empty command, extract its `WaitForQuiet` call into a public `Task SettleAsync(TerminalPane pane, string what)` on `ShotContext` and call that instead.

- [x] **Step 2a: Ensure the host is actually acting**

`AgentHostService.Instance` must be started with act enabled before the first `SendAsAgentAsync`. Check how `MainWindow` turns it on:

```bash
rtk grep -n "AgentHostService" src/NovaTerminal.App/MainWindow.axaml.cs | head -10
```

If `MainWindow` already applies it from the settings this run seeds (observe on, act on), nothing further is needed — but Task 7 seeded act as **off**. Give `AgentSessionScenario` a `Settings` override that turns act on for this scenario only:

```csharp
    public Action<TerminalSettings>? Settings => settings =>
    {
        settings.AgentAccessObserveEnabled = true;
        settings.AgentAccessActEnabled = true;
    };
```

using the real property names confirmed in Task 7. Keeping act off in `settings-agent-access` and on here is deliberate: the settings shot documents the safe default, this one documents the capability.

- [x] **Step 3: Register the scenario**

Add `new AgentSessionScenario()` to `ScenarioCatalog.Scenarios`.

- [x] **Step 4: Run it and look at the image**

```bash
scripts/shots.ps1 agent-session --scale 2
```

Read `artifacts/shots/agent-session@2x.png`. Verify against the Intent: indicator lit, journal entries present and readable, output visible. If the journal is empty, Step 2 was not completed correctly.

- [x] **Step 5: Commit**

```bash
rtk git add tools/NovaTerminal.Shots && rtk git commit -m "feat(shots): capture a real agent-driven session with journal entries"
```

---

### Task 9: FrameRecorder, Encoder, and `clip-agent`

**Files:**
- Create: `tools/NovaTerminal.Shots/FrameRecorder.cs`, `tools/NovaTerminal.Shots/Encoder.cs`
- Create: `tools/NovaTerminal.Shots/Scenarios/ClipAgentScenario.cs`
- Modify: `tools/NovaTerminal.Shots/ShotContext.cs`, `ScenarioCatalog.cs`, `Program.cs`
- Create: `tests/NovaTerminal.Shots.Tests/EncoderTests.cs`

**Interfaces:**
- Consumes: `Rasterizer`, `ShotRun`.
- Produces: `FrameRecorder` with `FrameRecorder(Window window, string frameDirectory, double scale)`, `void CaptureFrame()`, `int FrameCount { get; }`; `Encoder` with `static bool IsAvailable()`, `static void ToWebm(string frameDirectory, string outputPath, int fps)`, `static void ToGif(string frameDirectory, string outputPath, int fps)`; `ShotContext.Recorder` property and `Task RecordAsync(Func<Task> body, int fps)`.

- [x] **Step 1: Write the failing encoder-availability test**

`tests/NovaTerminal.Shots.Tests/EncoderTests.cs`:

```csharp
using NovaTerminal.Shots;

namespace NovaTerminal.ShotsTests;

public sealed class EncoderTests
{
    [Fact]
    public void IsAvailable_DetectsFfmpegOnPath()
    {
        // ffmpeg is a documented prerequisite for clips. If this fails on a machine that has
        // it, the detection is wrong; if it fails on one that does not, install ffmpeg.
        Assert.True(Encoder.IsAvailable(), "ffmpeg was not found on PATH.");
    }
}
```

- [x] **Step 2: Run it and verify it fails**

```bash
scripts/build.ps1 test tests/NovaTerminal.Shots.Tests --filter "FullyQualifiedName~EncoderTests"
```

Expected: FAIL — `Encoder` is not defined.

- [x] **Step 3: Implement FrameRecorder**

```csharp
using Avalonia.Controls;
using SkiaSharp;

namespace NovaTerminal.Shots;

/// <summary>Writes a numbered PNG per captured frame for ffmpeg to consume.</summary>
public sealed class FrameRecorder
{
    private readonly Window _window;
    private readonly string _frameDirectory;
    private readonly double _scale;

    public FrameRecorder(Window window, string frameDirectory, double scale)
    {
        _window = window;
        _frameDirectory = frameDirectory;
        _scale = scale;
        Directory.CreateDirectory(frameDirectory);
    }

    public int FrameCount { get; private set; }

    public void CaptureFrame()
    {
        using SKBitmap bitmap = Rasterizer.CaptureWindow(_window, _scale);
        Rasterizer.WritePng(bitmap, Path.Combine(_frameDirectory, $"frame-{FrameCount:D5}.png"));
        FrameCount++;
    }
}
```

- [x] **Step 4: Implement Encoder**

```csharp
using System.Diagnostics;

namespace NovaTerminal.Shots;

public static class Encoder
{
    public static bool IsAvailable()
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo("ffmpeg", "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });

            process?.WaitForExit();
            return process?.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    public static void ToWebm(string frameDirectory, string outputPath, int fps) =>
        Run($"-y -framerate {fps} -i \"{Path.Combine(frameDirectory, "frame-%05d.png")}\" " +
            $"-c:v libvpx-vp9 -pix_fmt yuv420p -b:v 0 -crf 32 \"{outputPath}\"");

    /// <summary>
    /// Two passes: palettegen then paletteuse. A GIF encoded without a generated palette
    /// banding-crushes terminal text, which is the one thing these clips exist to show.
    /// </summary>
    public static void ToGif(string frameDirectory, string outputPath, int fps)
    {
        string pattern = Path.Combine(frameDirectory, "frame-%05d.png");
        string palette = Path.Combine(frameDirectory, "palette.png");

        Run($"-y -framerate {fps} -i \"{pattern}\" -vf palettegen=stats_mode=diff \"{palette}\"");
        Run($"-y -framerate {fps} -i \"{pattern}\" -i \"{palette}\" " +
            $"-lavfi \"paletteuse=dither=bayer:bayer_scale=3\" \"{outputPath}\"");
    }

    private static void Run(string arguments)
    {
        using Process process = Process.Start(new ProcessStartInfo("ffmpeg", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Could not start ffmpeg.");

        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg failed ({process.ExitCode}): {stderr}");
        }
    }
}
```

- [x] **Step 5: Add clip support to ShotContext**

```csharp
    public FrameRecorder? Recorder { get; private set; }

    /// <summary>
    /// Runs <paramref name="body"/> while capturing frames, then encodes WebM and GIF.
    /// Frames are captured by the scenario calling <c>Recorder.CaptureFrame()</c> between
    /// steps: a timer-driven recorder would race the dispatcher and drop the frames that
    /// matter, so the scenario decides when the picture has changed.
    /// </summary>
    public async Task RecordAsync(Func<Task> body, int fps = 20)
    {
        string frameDirectory = Path.Combine(Run.OutputDirectory, "frames", _scenario.Spec.Name);
        Recorder = new FrameRecorder(Window, frameDirectory, 1.0);

        try
        {
            await body();
        }
        finally
        {
            FrameRecorder recorder = Recorder;
            Recorder = null;

            if (recorder.FrameCount == 0)
            {
                throw new InvalidOperationException($"'{_scenario.Spec.Name}' recorded no frames.");
            }

            if (!Encoder.IsAvailable())
            {
                Console.Error.WriteLine("[shots] ffmpeg not found; frames kept, clips skipped.");
                return;
            }

            string webm = Path.Combine(Run.OutputDirectory, $"{_scenario.Spec.Name}.webm");
            string gif = Path.Combine(Run.OutputDirectory, $"{_scenario.Spec.Name}.gif");

            Encoder.ToWebm(frameDirectory, webm, fps);
            Encoder.ToGif(frameDirectory, gif, fps);
        }
    }
```

- [x] **Step 6: Implement `clip-agent`**

`tools/NovaTerminal.Shots/Scenarios/ClipAgentScenario.cs`: reuse `AgentSessionScenario`'s real acting path, wrapping the command sequence in `context.RecordAsync(...)` and calling `context.Recorder!.CaptureFrame()` after each `Pump` inside a short loop so the typing animates. Capture roughly 100 frames at 20 fps for a 5-second clip. End with a final still via `context.Capture()`.

- [x] **Step 7: Register and run**

Add `new ClipAgentScenario()` to the catalog, then:

```bash
scripts/shots.ps1 clip-agent
```

Expected: `artifacts/shots/clip-agent.webm` and `.gif` exist and are non-empty.

- [x] **Step 8: Watch the clip**

Open the GIF and confirm the journal fills over time rather than appearing fully-formed in frame one.

- [x] **Step 9: Commit**

```bash
rtk git add tools/NovaTerminal.Shots tests/NovaTerminal.Shots.Tests && rtk git commit -m "feat(shots): record and encode the agent-driven clip"
```

---

### Task 10: Tier 1 remainder — `hero-split`, `tabs-vertical`, `command-palette`

**Files:**
- Create: `tools/NovaTerminal.Shots/Scenarios/HeroSplitScenario.cs`, `TabsVerticalScenario.cs`, `CommandPaletteScenario.cs`
- Modify: `tools/NovaTerminal.Shots/ScenarioCatalog.cs`

- [x] **Step 1: Implement `hero-split`**

```csharp
using Avalonia.Layout;
using NovaTerminal.Controls;

namespace NovaTerminal.Shots.Scenarios;

internal sealed class HeroSplitScenario : IScenario
{
    public ShotSpec Spec { get; } = new(
        Name: "hero-split",
        Tier: 1,
        LogicalWidth: 1440,
        LogicalHeight: 900,
        Intent: "Three panes at once: a colourful test run on the left, a git graph top-right, and " +
                "a process monitor bottom-right. Every pane full of text, splitters clearly visible.");

    public async Task RunAsync(ShotContext context)
    {
        TerminalPane left = context.OpenTab(context.World.DemoProfile);
        await context.RunCommandAsync(left, "clear");
        await context.RunCommandAsync(left, "bash scripts/demo-test.sh");

        context.Driver.InvokePrivate(context.Window, "SplitPane", Orientation.Horizontal);
        TerminalPane topRight = CurrentPane(context);
        await context.RunCommandAsync(topRight, "git log --graph --oneline --all -12");

        context.Driver.InvokePrivate(context.Window, "SplitPane", Orientation.Vertical);
        TerminalPane bottomRight = CurrentPane(context);
        await context.RunCommandAsync(bottomRight, "ps aux | head -20");

        context.Capture();
    }

    private static TerminalPane CurrentPane(ShotContext context) =>
        (TerminalPane)context.Driver.InvokePrivate(context.Window, "get_CurrentPaneForShots")!;
}
```

`CurrentPane` above needs a real accessor. Find how `MainWindow` exposes `_currentPane`:

```bash
rtk grep -n "_currentPane" src/NovaTerminal.App/MainWindow.axaml.cs | head -5
```

`_currentPane` is a private **field**, not a property, so replace `CurrentPane` with a field read:

```csharp
    private static TerminalPane CurrentPane(ShotContext context)
    {
        var field = typeof(MainWindow).GetField("_currentPane",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MainWindow._currentPane no longer exists.");

        return (TerminalPane)field.GetValue(context.Window)!;
    }
```

Also confirm `SplitPane`'s parameter type is `Avalonia.Layout.Orientation` (it is, at `MainWindow.axaml.cs:5223`).

- [x] **Step 2: Implement `tabs-vertical`**

Seed settings with the vertical orientation, open five tabs with distinct names, and capture:

```csharp
namespace NovaTerminal.Shots.Scenarios;

internal sealed class TabsVerticalScenario : IScenario
{
    private static readonly string[] TabNames =
        ["claude-code", "codex", "build", "logs", "ssh · edge-01"];

    public ShotSpec Spec { get; } = new(
        Name: "tabs-vertical",
        Tier: 1,
        LogicalWidth: 1440,
        LogicalHeight: 900,
        Intent: "The vertical tab sidebar with five distinctly named tabs, each showing its status " +
                "indicator and output preview line, and one tab visibly carrying agent activity.");

    public async Task RunAsync(ShotContext context)
    {
        foreach (string name in TabNames)
        {
            var profile = context.World.DemoProfile.ShallowCopy();
            profile.Name = name;

            TerminalPane pane = context.OpenTab(profile);
            await context.RunCommandAsync(pane, "bash scripts/demo-test.sh");
        }

        context.Capture();
    }
}
```

Set the orientation through the `Settings` member declared on `IScenario` in Task 6, which `Program` applies before the window is constructed. Confirm the setting and enum names first:

```bash
rtk grep -n "TabStripOrientation" src/NovaTerminal.App/Shell/TerminalSettings.cs
```

Then add to `TabsVerticalScenario`:

```csharp
    public Action<TerminalSettings>? Settings => settings =>
        settings.TabStripOrientation = TabStripOrientation.Vertical;
```

with `using NovaTerminal.Shell;`. If this shot comes out with a horizontal strip, the seeding ran after construction — check `Program`'s ordering, not the setting.

Also confirm `ShallowCopy()` exists on `TerminalProfile`:

```bash
rtk grep -n "ShallowCopy" src/NovaTerminal.App/Shell/TerminalProfile.cs
```

- [x] **Step 3: Implement `command-palette`**

```csharp
namespace NovaTerminal.Shots.Scenarios;

internal sealed class CommandPaletteScenario : IScenario
{
    public ShotSpec Spec { get; } = new(
        Name: "command-palette",
        Tier: 1,
        LogicalWidth: 1280,
        LogicalHeight: 800,
        Intent: "The command palette open over a populated terminal, a query typed in the box, and " +
                "several matching commands listed with their keyboard shortcuts on the right.");

    public async Task RunAsync(ShotContext context)
    {
        TerminalPane pane = context.OpenTab(context.World.DemoProfile);
        await context.RunCommandAsync(pane, "clear");
        await context.RunCommandAsync(pane, "bash scripts/nova-banner.sh");

        context.Driver.InvokePrivate(context.Window, "ToggleCommandPalette");
        context.Driver.TypeText("split");
        context.Driver.Pump(5);

        context.Driver.WaitFor(
            () => context.Driver.Require<ListBox>("CommandList").ItemCount > 0,
            TimeSpan.FromSeconds(5),
            "the palette to filter to at least one command");

        context.Capture();
    }
}
```

Add `using Avalonia.Controls;` and `using NovaTerminal.Controls;`.

- [x] **Step 4: Register all three and run them**

```bash
scripts/shots.ps1 hero-split tabs-vertical command-palette --scale 2
```

- [x] **Step 5: Look at all three images**

Check each against its Intent. `hero-split` most often fails by having one empty pane — if so, the split happened before the previous command settled; add a `WaitForQuiet` before splitting.

- [x] **Step 6: Commit**

```bash
rtk git add tools/NovaTerminal.Shots && rtk git commit -m "feat(shots): add split, vertical-tabs, and palette hero shots"
```

---

### Task 11: `themes-grid`

**Files:**
- Create: `tools/NovaTerminal.Shots/Scenarios/ThemesGridScenario.cs`
- Modify: `tools/NovaTerminal.Shots/PostProcess.cs` (created here), `ScenarioCatalog.cs`
- Create: `tests/NovaTerminal.Shots.Tests/PostProcessGridTests.cs`

**Interfaces:**
- Produces: `PostProcess.Grid(IReadOnlyList<SKBitmap> tiles, int columns, int gap, SKColor background) -> SKBitmap`.

- [x] **Step 1: Write the failing test**

```csharp
using NovaTerminal.Shots;
using SkiaSharp;

namespace NovaTerminal.ShotsTests;

public sealed class PostProcessGridTests
{
    [Fact]
    public void Grid_LaysTilesOutInRowsWithGaps()
    {
        var tiles = Enumerable.Range(0, 5).Select(_ => new SKBitmap(100, 50)).ToList();

        using SKBitmap grid = PostProcess.Grid(tiles, columns: 2, gap: 10, background: SKColors.Black);

        // 2 columns -> 3 rows for 5 tiles.
        Assert.Equal(100 * 2 + 10 * 3, grid.Width);
        Assert.Equal(50 * 3 + 10 * 4, grid.Height);

        foreach (SKBitmap tile in tiles)
        {
            tile.Dispose();
        }
    }
}
```

- [x] **Step 2: Run it and verify it fails**

```bash
scripts/build.ps1 test tests/NovaTerminal.Shots.Tests --filter "FullyQualifiedName~PostProcessGridTests"
```

Expected: FAIL — `PostProcess` is not defined.

- [x] **Step 3: Implement PostProcess.Grid**

```csharp
using SkiaSharp;

namespace NovaTerminal.Shots;

public static class PostProcess
{
    public static SKBitmap Grid(IReadOnlyList<SKBitmap> tiles, int columns, int gap, SKColor background)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfZero(tiles.Count);

        int tileWidth = tiles.Max(t => t.Width);
        int tileHeight = tiles.Max(t => t.Height);
        int rows = (tiles.Count + columns - 1) / columns;

        var result = new SKBitmap(
            tileWidth * columns + gap * (columns + 1),
            tileHeight * rows + gap * (rows + 1));

        using var canvas = new SKCanvas(result);
        canvas.Clear(background);

        for (int i = 0; i < tiles.Count; i++)
        {
            int column = i % columns;
            int row = i / columns;

            canvas.DrawBitmap(
                tiles[i],
                gap + column * (tileWidth + gap),
                gap + row * (tileHeight + gap));
        }

        return result;
    }
}
```

- [x] **Step 4: Implement the scenario**

`ThemesGridScenario` captures `hero-single`'s content once per theme. Because a theme change is a settings re-apply, the cleanest correct approach is one window per theme:

```csharp
namespace NovaTerminal.Shots.Scenarios;

internal sealed class ThemesGridScenario : IScenario
{
    private static readonly string[] Themes =
        ["Dracula", "GitHubDark", "Monokai", "OneHalfDark", "SolarizedDark"];

    public ShotSpec Spec { get; } = new(
        Name: "themes-grid",
        Tier: 1,
        LogicalWidth: 1280,
        LogicalHeight: 800,
        Intent: "Five tiles showing identical terminal content in each built-in theme, arranged in " +
                "a grid, with each theme's palette clearly distinct from its neighbours.");

    public async Task RunAsync(ShotContext context)
    {
        // Captured per theme by Program's multi-pass path; see Step 5.
        await Task.CompletedTask;
        throw new NotSupportedException("themes-grid is composed by Program, not run directly.");
    }
}
```

- [x] **Step 5: Add the multi-pass path in Program**

`themes-grid` is the one scenario that needs a fresh window per tile. Add to `Program.Main`, before the normal loop, a special case: for each theme in `ThemesGridScenario.Themes`, re-seed settings with that `ThemeName`, run the `hero-single` scenario body into a per-theme file, then compose the five PNGs with `PostProcess.Grid(tiles, columns: 2, gap: 24, background: new SKColor(0x0E, 0x10, 0x14))` and write `themes-grid@2x.png`. Record it in the manifest with `Tier = 1`.

- [x] **Step 6: Run and inspect**

```bash
scripts/shots.ps1 themes-grid --scale 2
```

Read the image and confirm the five palettes are visibly different — if two tiles look identical, the theme did not apply and the re-seed happened after the window was constructed.

- [x] **Step 7: Commit**

```bash
rtk git add tools/NovaTerminal.Shots tests/NovaTerminal.Shots.Tests && rtk git commit -m "feat(shots): compose the built-in themes grid"
```

---

### Task 12: Tier 2 — `search-overlay`, `tui-vim`, `tui-htop`

**Files:**
- Create: `tools/NovaTerminal.Shots/Scenarios/SearchOverlayScenario.cs`, `TuiVimScenario.cs`, `TuiHtopScenario.cs`
- Modify: `ScenarioCatalog.cs`

- [x] **Step 1: Implement `search-overlay`**

```csharp
internal sealed class SearchOverlayScenario : IScenario
{
    public ShotSpec Spec { get; } = new(
        Name: "search-overlay",
        Tier: 2,
        LogicalWidth: 1280,
        LogicalHeight: 800,
        Intent: "The search panel open over scrollback with a term typed, the match counter showing " +
                "a position within a larger total, and matches highlighted in the buffer behind it.");

    public async Task RunAsync(ShotContext context)
    {
        TerminalPane pane = context.OpenTab(context.World.DemoProfile);
        await context.RunCommandAsync(pane, "clear");
        await context.RunCommandAsync(pane, "git log --oneline -40");

        pane.ToggleSearch();
        context.Driver.Pump(3);
        context.Driver.TypeText("decoder");
        context.Driver.Pump(5);

        context.Driver.WaitFor(
            () => context.Driver.Require<TextBlock>("SearchCount").Text is { Length: > 0 } text
                  && !text.StartsWith("0/", StringComparison.Ordinal),
            TimeSpan.FromSeconds(5),
            "the search counter to report at least one match");

        context.Capture();
    }
}
```

Note `SearchPanel` and `SearchCount` exist both on `MainWindow` and inside `TerminalPane`. `pane.ToggleSearch()` opens the pane-level one, so `Require` must search the pane's tree, not the window's. Add a `Driver.RequireIn<T>(Control root, string name)` overload that calls `root.FindControl<T>(name)` and use it with `pane` as the root.

- [x] **Step 2: Implement `tui-vim`**

```csharp
internal sealed class TuiVimScenario : IScenario
{
    public ShotSpec Spec { get; } = new(
        Name: "tui-vim",
        Tier: 2,
        LogicalWidth: 1280,
        LogicalHeight: 800,
        Intent: "A full-screen editor on the alternate screen showing the Rust source with syntax " +
                "colouring, a status line at the bottom, and no shell prompt visible.");

    public async Task RunAsync(ShotContext context)
    {
        TerminalPane pane = context.OpenTab(context.World.DemoProfile);
        await context.RunCommandAsync(pane, "vim src/sixel-decoder.rs");

        context.Driver.WaitFor(
            () => pane.Buffer.IsAlternateScreen,
            TimeSpan.FromSeconds(20),
            "vim to switch to the alternate screen");

        context.Capture();

        pane.Session!.SendInput("\u001b:q!\n");
        context.Driver.Pump(10);
    }
}
```

Confirm the alternate-screen property name:

```bash
rtk grep -n "AlternateScreen\|IsAltScreen" src/NovaTerminal.VT/TerminalBuffer.cs | head
```

Use whatever it actually is. If `vim` is unavailable on the capture machine, the scenario must fail loudly rather than capture a shell prompt — add an explicit check that the alternate screen was entered, which the `WaitFor` above already provides.

- [x] **Step 3: Implement `tui-htop`**

Same shape as `tui-vim`, running `htop -d 5` on Linux or `btop`/`top` where available, waiting on the alternate screen, capturing, then sending `q`. State the fallback explicitly in the Intent so a reviewer knows which program should appear.

- [x] **Step 4: Register, run, and inspect all three**

```bash
scripts/shots.ps1 search-overlay tui-vim tui-htop --scale 2
```

- [x] **Step 5: Commit**

```bash
rtk git add tools/NovaTerminal.Shots && rtk git commit -m "feat(shots): add search overlay and TUI scenarios"
```

---

### Task 13: Tier 2 — `sixel-graphics` and `iterm2-inline-image`

**Files:**
- Create: `tools/NovaTerminal.Shots/Scenarios/SixelGraphicsScenario.cs`, `Iterm2InlineImageScenario.cs`
- Create: `tools/NovaTerminal.Shots/Assets/nova-logo.png`, `tools/NovaTerminal.Shots/Assets/plot.sixel`
- Modify: `DemoWorld.SeedWorkspace` (copy the two new assets), `ScenarioCatalog.cs`

The image assets are pre-generated rather than produced by `gnuplot`/`imgcat` at capture time, because those tools are not present on every machine and their absence would silently degrade the shot to a bare prompt. The terminal still decodes the real protocols — only the producer is fixed.

- [x] **Step 1: Generate the sixel asset**

```bash
rtk ffmpeg -y -f lavfi -i "color=c=0x1E1E2E:s=480x320" -frames:v 1 /tmp/plot-bg.png
```

Then produce a real sixel stream from a plot image using `img2sixel` if available; otherwise commit a sixel file generated once by any tool and record its provenance in a comment at the top of `plot.sixel`. The file must be a genuine sixel stream — the point of the shot is that NovaTerminal decodes it.

- [x] **Step 2: Implement `sixel-graphics`**

```csharp
internal sealed class SixelGraphicsScenario : IScenario
{
    public ShotSpec Spec { get; } = new(
        Name: "sixel-graphics",
        Tier: 2,
        LogicalWidth: 1280,
        LogicalHeight: 800,
        Intent: "A sixel image rendered inline in the terminal, sitting between two shell prompts, " +
                "with the image sharp and correctly positioned relative to the surrounding text.");

    public async Task RunAsync(ShotContext context)
    {
        TerminalPane pane = context.OpenTab(context.World.DemoProfile);
        await context.RunCommandAsync(pane, "clear");
        await context.RunCommandAsync(pane, "echo 'sixel decode · 480x320'");
        await context.RunCommandAsync(pane, "cat assets/plot.sixel");
        await context.RunCommandAsync(pane, "echo done");

        context.Capture();
    }
}
```

- [x] **Step 3: Implement `iterm2-inline-image`**

Same shape, emitting the iTerm2 OSC 1337 `File=` sequence with the base64 of `nova-logo.png`. Write a small `scripts/imgcat.sh` into the seeded workspace that does the base64 and escape assembly, so the command in the shot reads naturally as `bash scripts/imgcat.sh assets/nova-logo.png`.

- [x] **Step 4: Verify the images actually decoded**

Both scenarios must assert more than "a frame was captured". After capture, check the ink fraction of the region where the image should be — if the terminal did not decode the protocol, that region is uniform background. Add to each scenario:

```csharp
        using SKBitmap frame = Rasterizer.CaptureWindow(context.Window, 1.0);
        Assert(Rasterizer.InkFraction(frame) > 0.05, "the inline image did not decode");
```

Implement `Assert` as a small private static helper that throws `InvalidOperationException` — this tool has no test framework at runtime.

- [ ] **Step 5: Register, run, inspect**

```bash
scripts/shots.ps1 sixel-graphics iterm2-inline-image --scale 2
```

- [x] **Step 6: Commit**

```bash
rtk git add tools/NovaTerminal.Shots && rtk git commit -m "feat(shots): add inline image protocol scenarios"
```

---

### Task 14: Tier 2 — `settings-appearance`, `connection-manager`, `remote-files`, `command-assist`

**Files:**
- Create: four scenario files under `tools/NovaTerminal.Shots/Scenarios/`
- Modify: `ScenarioCatalog.cs`, and `DemoWorld.SeedSettings` (add fictional SSH profiles)

- [ ] **Step 1: Seed fictional SSH profiles**

Add to `DemoWorld.SeedSettings`, before `customize`:

```csharp
        settings.Profiles.Add(new TerminalProfile
        {
            Name = "edge-01",
            Type = ConnectionType.SSH,
            SshHost = "edge-01.demo.internal",
            SshUser = "nova",
            SshPort = 22
        });
        settings.Profiles.Add(new TerminalProfile
        {
            Name = "build-runner",
            Type = ConnectionType.SSH,
            SshHost = "build.demo.internal",
            SshUser = "ci",
            SshPort = 2222
        });
```

Hostnames are under `.internal` and obviously fictional — no real host may appear in a published image.

- [x] **Step 2: Implement `settings-appearance`**

Same shape as `SettingsAgentAccessScenario` from Task 7, selecting the `Appearance` tab. Intent: "The settings window on Appearance, showing theme selection, font family and size, with a live preview of the chosen theme."

- [x] **Step 3: Implement `connection-manager`**

```csharp
        context.Driver.Require<Border>("ConnectionOverlay").IsVisible = true;
        context.Driver.Pump(5);
        context.Capture();
```

Open it through its real command-palette entry if one exists (check `PopulateNewTabMenu` and the command registry first); fall back to setting `IsVisible` only if no command path exists. Intent: "The connection manager overlay listing two SSH profiles with their hosts and users, over a populated terminal."

- [x] **Step 4: Implement `remote-files`**

Call `pane.ToggleRemoteFilesSidebar()` (a public method on `TerminalPane`), then show the transfer overlay via `Require<Border>("TransferOverlay").IsVisible = true`. Intent: "The remote files sidebar open beside a terminal pane, with the transfer centre visible in the lower right showing recent transfers."

Note that without a live SSH connection the sidebar may render empty. If it does, the scenario must fail rather than capture an empty sidebar — add a `WaitFor` on the sidebar having at least one row, and if that cannot be satisfied offline, drop this scenario from the catalogue and record why in the commit message. An empty panel is not a feature screenshot.

- [x] **Step 5: Implement `command-assist`**

Call `pane.ToggleCommandAssist()`, type a partial command, wait for the suggestion popup to have items, capture. Intent: "The command assist popup open beneath the prompt with several ranked suggestions visible, the typed prefix highlighted in each."

- [ ] **Step 6: Register, run, inspect all four**

```bash
scripts/shots.ps1 settings-appearance connection-manager remote-files command-assist --scale 2
```

- [x] **Step 7: Commit**

```bash
rtk git add tools/NovaTerminal.Shots && rtk git commit -m "feat(shots): add settings, connections, remote files, and assist shots"
```

---

### Task 15: Post-processing and derived variants

**Files:**
- Modify: `tools/NovaTerminal.Shots/PostProcess.cs`
- Create: `tools/NovaTerminal.Shots/VariantBuilder.cs`
- Modify: `Program.cs`
- Create: `tests/NovaTerminal.Shots.Tests/VariantBuilderTests.cs`

**Interfaces:**
- Produces: `PostProcess.RoundedWithShadow(SKBitmap source, float cornerRadius, float shadowBlur, int margin) -> SKBitmap`; `PostProcess.OnBackdrop(SKBitmap source, int width, int height, SKColor top, SKColor bottom) -> SKBitmap`; `VariantBuilder.BuildAll(ShotAsset master, ShotRun run) -> IReadOnlyList<ShotAsset>`.

- [x] **Step 1: Write the failing test**

```csharp
using NovaTerminal.Shots;
using SkiaSharp;

namespace NovaTerminal.ShotsTests;

public sealed class VariantBuilderTests
{
    [Fact]
    public void RoundedWithShadow_LeavesTheCornersTransparent()
    {
        using var source = new SKBitmap(200, 120);
        using (var canvas = new SKCanvas(source))
        {
            canvas.Clear(SKColors.White);
        }

        using SKBitmap result = PostProcess.RoundedWithShadow(source, cornerRadius: 16, shadowBlur: 12, margin: 24);

        Assert.Equal(0, result.GetPixel(0, 0).Alpha);
        Assert.True(result.Width > source.Width, "the margin should widen the image");
    }

    [Fact]
    public void OnBackdrop_ProducesExactlyTheRequestedSize()
    {
        using var source = new SKBitmap(1000, 600);

        using SKBitmap card = PostProcess.OnBackdrop(source, 1200, 630, SKColors.Black, SKColors.DarkBlue);

        Assert.Equal(1200, card.Width);
        Assert.Equal(630, card.Height);
    }
}
```

- [x] **Step 2: Run it and verify it fails**

```bash
scripts/build.ps1 test tests/NovaTerminal.Shots.Tests --filter "FullyQualifiedName~VariantBuilderTests"
```

Expected: FAIL — the methods do not exist.

- [x] **Step 3: Implement RoundedWithShadow and OnBackdrop**

```csharp
    /// <summary>
    /// Rounds the corners and drops a shadow. The headless renderer cannot produce the OS
    /// window shadow or rounded corners, so they are added here; without them a capture reads
    /// as a flat rectangle pasted onto the page rather than a window.
    /// </summary>
    public static SKBitmap RoundedWithShadow(SKBitmap source, float cornerRadius, float shadowBlur, int margin)
    {
        var result = new SKBitmap(source.Width + margin * 2, source.Height + margin * 2);

        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Transparent);

        var bounds = new SKRect(margin, margin, margin + source.Width, margin + source.Height);
        var rounded = new SKRoundRect(bounds, cornerRadius, cornerRadius);

        using (var shadowPaint = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 140),
            IsAntialias = true,
            ImageFilter = SKImageFilter.CreateBlur(shadowBlur, shadowBlur)
        })
        {
            canvas.DrawRoundRect(rounded, shadowPaint);
        }

        canvas.Save();
        canvas.ClipRoundRect(rounded, antialias: true);
        canvas.DrawBitmap(source, bounds.Left, bounds.Top);
        canvas.Restore();

        return result;
    }

    public static SKBitmap OnBackdrop(SKBitmap source, int width, int height, SKColor top, SKColor bottom)
    {
        var result = new SKBitmap(width, height);

        using var canvas = new SKCanvas(result);
        using (var background = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(0, height),
                [top, bottom],
                null,
                SKShaderTileMode.Clamp)
        })
        {
            canvas.DrawRect(new SKRect(0, 0, width, height), background);
        }

        float scale = Math.Min((width * 0.86f) / source.Width, (height * 0.86f) / source.Height);
        float drawWidth = source.Width * scale;
        float drawHeight = source.Height * scale;

        canvas.DrawBitmap(
            source,
            new SKRect(
                (width - drawWidth) / 2,
                (height - drawHeight) / 2,
                (width + drawWidth) / 2,
                (height + drawHeight) / 2));

        return result;
    }
```

- [x] **Step 4: Implement VariantBuilder**

`BuildAll` produces, for each Tier 1/2 master: `<name>-readme.png` (1280 wide), `<name>-site.png` (2400 wide), and for `hero-single` only, `og-card.png` (1200×630) and `social-square.png` (1080×1080), each built by `RoundedWithShadow` then `OnBackdrop` with the brand gradient `#0E1014` → `#1B1330`. Every produced file is recorded as a `ShotAsset` with `Tier = 3`.

- [x] **Step 5: Call it from Program**

After the scenario loop and before `run.WriteManifest()`, iterate `run.Assets.Where(a => a.Tier <= 2)` into `VariantBuilder.BuildAll`. Take a snapshot of the list first — `BuildAll` records new assets and iterating the live list while appending throws.

- [x] **Step 6: Run the full set and inspect the variants**

```bash
scripts/shots.ps1 all --scale 2
```

Read `og-card.png` and `social-square.png`. Check that the terminal is not cropped and the shadow reads as a shadow rather than a grey band.

- [x] **Step 7: Commit**

```bash
rtk git add tools/NovaTerminal.Shots tests/NovaTerminal.Shots.Tests && rtk git commit -m "feat(shots): derive README, site, and social variants"
```

---

### Task 16: Remaining clips — `clip-palette`, `clip-split`, `clip-tui`

**Files:**
- Create: `tools/NovaTerminal.Shots/Scenarios/ClipPaletteScenario.cs`, `ClipSplitScenario.cs`, `ClipTuiScenario.cs`
- Modify: `ScenarioCatalog.cs`

- [x] **Step 1: Implement `clip-palette`**

Wrap the `command-palette` sequence in `RecordAsync`, capturing a frame after every single typed character rather than after the whole string, so the filtering animates:

```csharp
    public async Task RunAsync(ShotContext context)
    {
        TerminalPane pane = context.OpenTab(context.World.DemoProfile);
        await context.RunCommandAsync(pane, "bash scripts/nova-banner.sh");

        await context.RecordAsync(async () =>
        {
            for (int i = 0; i < 10; i++)
            {
                context.Recorder!.CaptureFrame();
            }

            context.Driver.InvokePrivate(context.Window, "ToggleCommandPalette");

            foreach (char c in "split pane")
            {
                context.Driver.TypeText(c.ToString());
                context.Recorder!.CaptureFrame();
                context.Recorder!.CaptureFrame();
            }

            for (int i = 0; i < 20; i++)
            {
                context.Recorder!.CaptureFrame();
            }

            await Task.CompletedTask;
        });
    }
```

- [x] **Step 2: Implement `clip-split`**

Record frames across two `SplitPane` calls and a broadcast-input toggle, then send a command that appears in both panes. Find the broadcast entry point:

```bash
rtk grep -n "Broadcast" src/NovaTerminal.App/MainWindow.axaml.cs | head -10
```

- [x] **Step 3: Implement `clip-tui`**

Launch the process monitor, then capture ~60 frames spaced by `Driver.Pump(2)` so the redraw is visible.

- [x] **Step 4: Register, run, and watch all three**

```bash
scripts/shots.ps1 clip-palette clip-split clip-tui
```

Open each GIF. A clip whose first and last frames are identical means no frames captured the change — fix the capture points, not the frame count.

- [x] **Step 5: Commit**

```bash
rtk git add tools/NovaTerminal.Shots && rtk git commit -m "feat(shots): add palette, split, and TUI clips"
```

---

### Task 17: Publishing and the `/shots` command

**Files:**
- Create: `tools/NovaTerminal.Shots/Publisher.cs`
- Create: `.claude/commands/shots.md`
- Modify: `Program.cs` (add `--publish`)
- Create: `tests/NovaTerminal.Shots.Tests/PublisherTests.cs`

**Interfaces:**
- Produces: `Publisher.Publish(ShotRun run, string repositoryRoot) -> IReadOnlyList<string>` returning the published relative paths; `Publisher.ResolveDestination(ShotAsset asset, string repositoryRoot) -> string`.

- [x] **Step 1: Write the failing guard test**

```csharp
using NovaTerminal.Shots;

namespace NovaTerminal.ShotsTests;

public sealed class PublisherTests
{
    [Fact]
    public void ResolveDestination_KeepsEveryAssetUnderDocsAssetsShots()
    {
        var asset = new ShotAsset("hero-single", 1, "/tmp/hero-single@2x.png", 2560, 1600,
            "hero-single", "abc1234", "win-x64", "2026-08-28T00:00:00Z");

        string destination = Publisher.ResolveDestination(asset, repositoryRoot: "/repo");

        Assert.StartsWith(Path.Combine("/repo", "docs", "assets", "shots"), destination, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveDestination_RejectsANameThatEscapesTheAssetDirectory()
    {
        var asset = new ShotAsset("../../etc/passwd", 1, "/tmp/x.png", 10, 10,
            "x", "abc1234", "win-x64", "2026-08-28T00:00:00Z");

        Assert.Throws<InvalidOperationException>(() =>
            Publisher.ResolveDestination(asset, repositoryRoot: "/repo"));
    }
}
```

- [x] **Step 2: Run it and verify it fails**

```bash
scripts/build.ps1 test tests/NovaTerminal.Shots.Tests --filter "FullyQualifiedName~PublisherTests"
```

Expected: FAIL — `Publisher` is not defined.

- [x] **Step 3: Implement Publisher**

`ResolveDestination` builds `<root>/docs/assets/shots/<name><extension>`, calls `Path.GetFullPath`, and throws `InvalidOperationException` if the result does not start with the full path of the assets directory. `Publish` copies each Tier 1–3 asset and each clip to its destination and returns the relative paths. Masters (`@2x`) are **not** published; only the README, site, OG, square variants and the clips are.

- [x] **Step 4: Add `--publish` to Program**

When `--publish` is passed, call `Publisher.Publish(run, repositoryRoot)` after variants are built and print each published path.

- [x] **Step 5: Write the slash command**

`.claude/commands/shots.md`:

```markdown
---
description: Regenerate marketing screenshots and clips, then review every image
---

Regenerate NovaTerminal's marketing assets and verify each one visually.

Scenarios requested: $ARGUMENTS (empty means `all`).

1. Build and run the harness. Never use raw `dotnet build`:

   `scripts/shots.ps1 $ARGUMENTS --scale 2`

2. Read `artifacts/shots/shots.json` to get the produced asset list.

3. **Look at every PNG you produced.** For each one, read the image and compare it against
   that scenario's `Intent` string, which you can list with `scripts/shots.ps1 --list`. Judge:
   - Is the pane full of content, or mostly empty?
   - Is the overlay the scenario opened actually open?
   - Is any text clipped at an edge?
   - Did the intended theme apply?
   - Does the frame look half-drawn — a partial redraw, a missing tab strip?
   - Does anything real leak in: a real username, hostname, path, or branch name?

4. Re-run only the scenarios that failed judgement. The usual causes, in order of likelihood:
   a command captured before its output settled (extend the settle wait), a split taken before
   the previous pane filled, or a theme re-seeded after the window was constructed.

5. When every image passes, publish and report:

   `scripts/shots.ps1 all --scale 2 --publish`

   Then give a table of what changed under `docs/assets/shots/`.

Never publish an image you have not looked at.
```

- [x] **Step 6: Run the tests and the command end to end**

```bash
scripts/build.ps1 test tests/NovaTerminal.Shots.Tests
```

Expected: PASS, all tests.

- [x] **Step 7: Commit**

```bash
rtk git add tools/NovaTerminal.Shots tests/NovaTerminal.Shots.Tests .claude/commands/shots.md && rtk git commit -m "feat(shots): publish assets and add the /shots review command"
```

---

### Task 18: Hero capture script

**Files:**
- Create: `scripts/capture-hero.ps1`
- Create: `docs/assets/shots/hero/README.md`

- [x] **Step 1: Write the script**

`scripts/capture-hero.ps1` — captures a window by process id, using `DwmGetWindowAttribute` with `DWMWA_EXTENDED_FRAME_BOUNDS` (attribute 9) so the real shadow and rounded corners are included:

```powershell
#!/usr/bin/env pwsh
# Captures the real NovaTerminal window, including the OS drop shadow and rounded corners
# that a headless render cannot produce.
#
# This script does NOT drive the app. You arrange the window; it only captures. Foreground
# automation on Windows is unreliable enough that leaving arrangement to a human is the
# design, not a limitation.

param(
    [Parameter(Mandatory = $true)][string] $Name,
    [string] $OutputDirectory = "docs/assets/shots/hero"
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public struct RECT { public int Left, Top, Right, Bottom; }
public static class Dwm {
    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
}
"@

$process = Get-Process -Name 'NovaTerminal' -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $process) { throw 'NovaTerminal is not running. Start it, arrange the window, then re-run.' }

Write-Host 'Arrange the window now. Capturing in 5 seconds…'
Start-Sleep -Seconds 5

$rect = New-Object RECT
$null = [Dwm]::DwmGetWindowAttribute($process.MainWindowHandle, 9, [ref] $rect, 16)

$pad = 40
$width = ($rect.Right - $rect.Left) + ($pad * 2)
$height = ($rect.Bottom - $rect.Top) + ($pad * 2)

$bitmap = New-Object System.Drawing.Bitmap $width, $height
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.CopyFromScreen($rect.Left - $pad, $rect.Top - $pad, 0, 0, $bitmap.Size)

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$path = Join-Path $OutputDirectory "$Name.png"
$bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)

$graphics.Dispose()
$bitmap.Dispose()

Write-Host "Saved $path ($width x $height)"
```

- [x] **Step 2: Write the arrangement checklist**

`docs/assets/shots/hero/README.md` lists, for each hero shot, the exact window arrangement to produce before running the script: which profile, which commands, which theme, and the window size. Include a line recording which OS and build each committed hero image came from.

- [x] **Step 3: Capture one hero shot manually**

```bash
scripts/capture-hero.ps1 -Name hero-real-split
```

Verify the shadow and rounded corners are present and that no other window intrudes into the padded region.

- [x] **Step 4: Commit**

```bash
rtk git add scripts/capture-hero.ps1 docs/assets/shots/hero && rtk git commit -m "feat(shots): add the real-window hero capture script"
```

---

### Task 19: Switch README and site to the generated assets

**Files:**
- Modify: `README.md`
- Modify: `site/src/pages/*`, `site/public/`
- Create: `docs/assets/shots/README.md`

- [x] **Step 1: Generate and publish the full set**

```bash
scripts/shots.ps1 all --scale 2 --publish
```

- [x] **Step 2: Replace the README images**

Swap the five `user-attachments` URLs at the top of `README.md` for relative paths to the published assets. Use `hero-split`, `command-palette`, `tabs-vertical`, `agent-session`, and `themes-grid`. Keep them in a single row as today, sized consistently.

Add a line under the feature list linking the agent clip, since it is the differentiator:

```markdown
![An agent driving a live session](docs/assets/shots/clip-agent.gif)
```

- [x] **Step 3: Update the site**

Copy the site-width variants into `site/public/shots/` and reference them from the Astro pages. Point the OG meta tag at `og-card.png` instead of the current `og.svg`.

- [x] **Step 4: Document the asset directory**

`docs/assets/shots/README.md` explains that everything in the directory is generated, that `/shots` regenerates it, that masters live in gitignored `artifacts/shots/`, and that `shots.json` in a run's output records the commit and OS behind each image.

- [x] **Step 5: Verify the README renders**

Check that every image path resolves from the repository root and that no `user-attachments` URL remains:

```bash
rtk grep -n "user-attachments" README.md
```

Expected: no matches.

- [x] **Step 6: Build the site**

```bash
rtk npm --prefix site run build
```

Expected: build succeeds and the referenced images exist.

- [x] **Step 7: Commit**

```bash
rtk git add README.md site docs/assets/shots && rtk git commit -m "docs: replace attachment screenshots with generated assets"
```

---

## Verification before calling this done

- [x] `scripts/build.ps1 build -c Release` succeeds with no new warnings.
- [x] `scripts/build.ps1 test tests/NovaTerminal.Shots.Tests` passes in full.
- [x] `scripts/build.ps1 test tests/NovaTerminal.App.Tests --blame-hang-timeout 5m` is no worse than the pre-change baseline — this change must not perturb the headless suite.
- [x] `scripts/shots.ps1 all --scale 2` exits 0 and produces every catalogued asset.
- [x] Every published image has been looked at and matches its scenario's `Intent`.
- [x] No published image contains a real username, hostname, path, or branch. One caveat: `command-assist`'s popup footer shows the harness's own temp workspace (`/tmp/nova-shots/<guid>`, truncated), which leaks no identity but contradicts the `~/projects/nova-demo` prompt above it. Fixing it means giving `DemoWorld` a workspace path that renders as the demo path.
- [x] `rtk grep -rn "user-attachments" README.md` returns nothing.
- [ ] CI passes, including the solution-wide filtered test jobs that would break on an artifact-list omission. **Not yet run** - the workflow triggers on pull requests and no PR is open for this branch, so nothing has executed against it. Verified locally instead: `build -c Release` on the full solution, the Shots suite, and App.Tests against a main baseline.
