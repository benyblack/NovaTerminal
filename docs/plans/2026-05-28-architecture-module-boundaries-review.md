# NovaTerminal — Architecture & Module Boundaries Review (Findings)

Date: 2026-05-28
Scope: structural layering, project graph, namespace/assembly alignment, separation of concerns at the module level.
Status: **Findings only — no remediation plan attached.** That's a follow-up.

## A. Documented architecture vs. real architecture have diverged

The single biggest issue. `docs/ARCHITECTURE.md` and `docs/MODULE_OWNERSHIP.md` describe a 4-layer model (UI → Renderer → Terminal Core → PTY Backend) with file references like `Core/AnsiParser.cs`, `Core/TerminalBuffer.cs`, `Core/TerminalView.cs`, `Core/RustPtySession.cs`. None of those paths exist anymore.

Real layout vs. doc claim:

| Doc claim | Reality |
|---|---|
| `Core/AnsiParser.cs` is "Terminal Core" | Lives in `src/NovaTerminal.VT/AnsiParser.cs` |
| `Core/TerminalBuffer.cs` is "Terminal Core" | Lives in `src/NovaTerminal.VT/` (split across 10 partial files) |
| `Core/TerminalView.cs` is "Renderer" | Lives in `src/NovaTerminal.App/Core/TerminalView.cs` (1,912 lines) |
| `Core/TerminalDrawOperation.cs` is "Renderer" | Lives in `src/NovaTerminal.App/Core/TerminalDrawOperation.cs` (2,723 lines) |
| `Core/RustPtySession.cs` is "PTY Backend" | Lives in `src/NovaTerminal.Pty/RustPtySession.cs` |
| PTY Backend sits *under* Terminal Core | csproj: **Core depends on Pty**, not the reverse |
| PTY layer never interprets VT | csproj: **Pty references VT**; `ITerminalSession.AttachBuffer(TerminalBuffer)` puts VT types on Pty's public surface |
| "Renderer" is a single module | Skia primitives in `NovaTerminal.Rendering` (11 files), but the actual renderer entry points (`TerminalView`, `TerminalDrawOperation`) live in `App/Core/` |

The README's mermaid graph reflects today's reality; the architecture document does not. New contributors reading ARCHITECTURE.md will form an incorrect mental model on day one.

## B. The "Core" name is overloaded three different ways

This is a major source of confusion:

1. **The `NovaTerminal.Core` *project*** — actually a platform-utilities library: Input routing, path mapping (WSL), Process abstraction, and an entire SSH stack (`Ssh/Interactions`, `Launch`, `Models`, `Native`, `OpenSsh`, `Sessions`, `Storage`, `Transport`). Should be named something like `NovaTerminal.Platform` or split.
2. **The `App/Core/` *folder*** — semantic glue inside the UI shell: `TerminalView`, `TerminalDrawOperation`, `SessionManager`, `WorkspaceManager`, 8 `Startup*` files, `VaultService`, `SftpService`, `SshAskPassCommand`, `BundledFontCatalog`, `ThemeManager`, `ProfileImporter`, `CommandRegistry`, etc.
3. **The `NovaTerminal.Core` *namespace*** — declared by **5 of 6 libraries**: VT, Core, Pty, Rendering, and Replay all put types into `namespace NovaTerminal.Core` (and sub-namespaces). The assembly boundary is invisible at the language level — you cannot tell from a `using` whether a type came from VT, Pty, or Rendering.

Consequences:
- `ITerminalSession.cs` lives in `src/NovaTerminal.Pty/` but declares `namespace NovaTerminal.Core` — the file moved between projects in a refactor and the namespace was never updated.
- Sub-namespaces are scattered inconsistently: `NovaTerminal.Core.Replay` lives in *both* the VT project and the Replay project; `NovaTerminal.Core.Storage` lives in VT only; `NovaTerminal.Core.Execution` lives in the Core project but no folder matches.
- Refactor moves between projects are "free" (no rename required), which is exactly what got us here.

## C. The PTY/VT/Core/Replay layering is inverted from the documented intent

Actual project dependency edges from `.csproj` reads:

```
Conformance     (no refs, orphan exe)
VT              (leaf)
Replay          → VT
Rendering       → VT, SkiaSharp
Pty             → VT, Replay
Core            → Pty
App             → Core, VT, Rendering, Pty, Replay
Cli             → App
```

Specific concerns:

1. **`Core → Pty`** inverts the documented "Core is cross-platform pure / Pty is OS-specific" stack. Today, `NovaTerminal.Core` (which contains SSH transport, OpenSSH bridge, native interop) sits **above** the rust-PTY adapter. That is defensible for *this* Core (it's the platform layer), but it makes the name lie even worse.
2. **`Pty → VT, Replay`** violates the documented invariant "PTY layer never interprets VT / Bytes delivered verbatim." `ITerminalSession` defines `AttachBuffer(TerminalBuffer)` and `StartRecording(path)` — so the session is responsible for plumbing bytes into the parser *and* writing recordings. That belongs in an orchestration layer, not the PTY adapter.
3. **`Replay → VT`** is fine, but combined with `Pty → Replay → VT` it means a session, a recorder, and a parser are all in the same dependency cluster with no seam. There's nowhere to mock the parser to test the recorder, or vice versa.
4. **No "Engine" or "Session" assembly**. The thing that owns "bytes-in → parsed → buffer mutated → snapshot out" is split across `Pty.RustPtySession`, `VT.AnsiParser`, `VT.TerminalBuffer`, and `App.Core.SessionManager`/`App.Core.TerminalView`. There is no single module representing a terminal session as a unit.

## D. `ITerminalSession` is a kitchen-sink god-interface

```csharp
public interface ITerminalSession : IDisposable {
    Guid Id;
    void SendInput(string input);              // input/output transport
    void Resize(int cols, int rows);           // lifecycle
    string ShellCommand;                        // shell metadata
    string? ShellArguments;
    bool IsProcessRunning;                      // lifecycle
    bool HasActiveChildProcesses;
    int? ExitCode;
    void StartRecording(string filePath);       // recording
    void StopRecording();
    bool IsRecording;
    void AttachBuffer(TerminalBuffer buffer);   // buffer wiring (VT coupling)
    void TakeSnapshot();
    event Action<string>? OnOutputReceived;     // input/output transport
    event Action<int>? OnExit;                  // lifecycle
}
```

- Five orthogonal concerns on one interface (I/O transport, lifecycle, shell metadata, recording, buffer attachment) — no way to mock or substitute any one in isolation.
- `SendInput(string)` and `OnOutputReceived(string)` use `string`, not `ReadOnlySpan<byte>` / `byte[]`. For a system that prides itself on "deterministic byte delivery" (per the docs) and which carries raw escape sequences, going through UTF-16 strings at the PTY boundary is a correctness smell — UTF-8 split across reads, embedded `\0`, lone surrogates, etc. all become ambiguous. Worth verifying against the rust PTY callsites.
- `AttachBuffer` taking a concrete `TerminalBuffer` is exactly what forces `Pty → VT`.

## E. The "App" project is a god-module hosting code that doesn't belong there

`src/NovaTerminal.App/` is 155 of ~267 source files (~58% of src/), and several pieces are mis-located:

- **`App/Core/TerminalDrawOperation.cs` (2,723 LOC)** and **`App/Core/TerminalView.cs` (1,912 LOC)** are the actual rendering pipeline. Per the architecture doc they belong in `Rendering`. The `Rendering` project today only holds Skia primitives (atlas, cache, font wrappers, sixel decoder), not the renderer composition.
- **SSH UX is fragmented across 4 places** with no SSH assembly: `Core/Ssh/` (transport, sessions, native), `App/Services/Ssh/` (SshConnectionService), `App/ViewModels/Ssh/`, `App/Views/Ssh/`, plus `App/Core/SshAskPassCommand.cs`, `App/Core/SftpService.cs`, `App/Core/VaultService.cs`. There should be a `NovaTerminal.Ssh` (or `.Remote`) module that the App project consumes.
- **CommandAssist** lives entirely under `App/CommandAssist/{Application,Domain,Models,ShellIntegration,Storage,ViewModels,Views}` — that's a complete sub-architecture inside App, including its own Domain/Storage/Application layers. It's a candidate for its own assembly.
- **Startup orchestration** is 8 files in `App/Core/` (`StartupOrchestrator`, `StartupPerformanceTracker`, `StartupRestoreCoordinator`, `StartupRestorePlan`, `StartupMetricsAnalysis`, `StartupMetricsSerializationContext`, `StartupMetricsWriter`, plus interactions with `WorkspaceManager`/`SessionManager`). Reasonable in App, but should at least be its own folder/namespace.
- **Code-behinds are unmanageable**: `MainWindow.axaml.cs` 5,259 LOC, `TerminalPane.axaml.cs` 2,572 LOC, `SettingsWindow.axaml.cs` 1,672 LOC, `ConnectionManager.axaml.cs` 652 LOC. These are MVVM red flags; they almost certainly contain business logic that belongs in services/view-models.

## F. Solution and test-project hygiene

1. **`NovaTerminal.sln` is missing two real projects:** `src/NovaTerminal.Conformance` and `tests/NovaTerminal.ExternalSuites` exist on disk but are not in the sln. They won't be built or tested by IDE-driven workflows; they get built only if someone references them directly.
2. **Two xUnit majors in one sln:** `NovaTerminal.Tests` uses `xunit.v3 3.2.2`; `NovaTerminal.Core.Tests` uses `xunit 2.9.3`. Different attribute models, different runner contracts, different fact/theory semantics. One should be picked.
3. **Test-name vs. test-content mismatch:** `NovaTerminal.Tests` references `App` + `Cli` + `Conformance` — i.e. it's an *App* integration suite. `NovaTerminal.Core.Tests` references `Core` + `Conformance` — Core unit suite. Either rename `NovaTerminal.Tests` → `NovaTerminal.App.Tests`, or align naming per-assembly (`*.VT.Tests`, `*.Rendering.Tests` are missing entirely).
4. **No `*.VT.Tests` or `*.Rendering.Tests` projects.** The single most important assembly (the parser/buffer in VT) is tested only indirectly through `NovaTerminal.Tests` which transitively pulls in Avalonia, Skia, and the rust PTY. There is no thin unit-test project for VT alone — meaning a tiny parser change rebuilds the world.
5. **No central package management.** No `Directory.Packages.props`. Versions are duplicated (e.g. `SkiaSharp` is `3.116.1` in `Rendering` but `3.119.4-preview.1.1` in `App` — different majors of skia loaded depending on entry point; if both are loaded, undefined behavior).
6. **`Directory.Build.props` is nearly empty** (just version + `UseSharedCompilation=false`). No common LangVersion pin, no `TreatWarningsAsErrors`, no analyzer/style enforcement, no nullable defaults centralized (each csproj repeats them). This is where layering rules could be encoded via NetArchTest or `BannedSymbols`.
7. **No `InternalsVisibleTo` consistency:** `Core` exposes internals to `Core.Tests`, `App` exposes internals to `Tests` + `Cli`. VT, Pty, Rendering, Replay don't, so their tests can only touch public surface — which means the public surface of VT is wider than it should be (because internals would otherwise be untestable).

## G. The CLI-shim build dance is structurally wrong

`Cli → App` (CLI references the GUI app). To get a single artifact, `App.csproj` has two custom targets (`BuildCliShim`, `PublishCliShim`) that invoke a *nested* `dotnet build` of the Cli project and copy the outputs back into `App`'s `OutDir`. The flags `-nodeReuse:false -p:BuildProjectReferences=false -p:UseSharedCompilation=false` are required to avoid daemon-pipe hangs (documented in `CLAUDE.md`).

The deeper issue: a CLI shim shouldn't reference a WinExe/Avalonia app. The real layering is the other way around — there's some shared startup/configuration code that both Gui and Cli want, and rather than extracting it into a shared library, the Cli was wired to depend on App. Symptoms:

- `App.csproj` has `<InternalsVisibleTo Include="NovaTerminal.Cli" />`, confirming the Cli reaches into App internals.
- `App/Core/VtReportCli.cs`, `App/Core/VtReportCommand.cs`, `App/Core/CliConsoleBindings.cs` live in the WinExe project but exist solely for the CLI's benefit.
- A `NovaTerminal.Shell` (or `.Bootstrap`) library that both `App` (GUI) and `Cli` consume would eliminate the inverted reference and the build-dance.
- A separate concern: `CliBuildOutputDir` is constructed from `bin/$(Configuration)/$(TargetFramework)/` — it hardcodes the layout MSBuild uses and will silently break under artifact-output mode (`UseArtifactsOutput`).

## H. `NovaTerminal.Conformance` is structurally orphaned

- No `ProjectReference` (it's a standalone `OutputType=Exe`).
- Not in the `.sln`.
- Yet `NovaTerminal.Tests` and `NovaTerminal.Core.Tests` both reference it as a project dep. So it's a build-time tool with no clear ownership, and the architecture doc treats it as a peer of the libraries.
- Either it should be inside the sln (so the IDE/test runners can discover it) or be relocated under `tools/` to signal it's a developer tool, not a library.

## I. Missing modules that the codebase has *grown* but never created

Based on code distribution and folder structure, the following are de facto modules but haven't been promoted to assemblies:

| De facto module | Currently lives in | Why it should be an assembly |
|---|---|---|
| **Ssh / Remote** | Split across `Core/Ssh/`, `App/Services/Ssh/`, `App/ViewModels/Ssh`, `App/Views/Ssh`, `App/Core/Sftp,Vault,SshAskPass` | Largest non-terminal subsystem; has its own native interop; ~3,000+ LOC; testable in isolation |
| **CommandAssist** | `App/CommandAssist/` with Application/Domain/Storage layers | Already has internal layering; an entire sub-architecture inside App |
| **Theming / Profiles** | `App/Core/Theme*.cs`, `App/Core/TerminalProfile.cs`, `App/Core/ProfileImporter.cs`, `App/themes/` | Config/serialization concerns mixed with UI |
| **Bootstrap / Startup** | `App/Core/Startup*.cs` + `App/Program.cs` + Cli's `Program.cs` | The thing both Cli and App should share to fix finding G |
| **Renderer composition** | `App/Core/TerminalView.cs`, `App/Core/TerminalDrawOperation.cs` | The actual renderer the architecture doc describes; today it's in App |

## J. Smaller items worth flagging

- **`NovaTerminal.VT/TerminalBuffer.*.cs`** — 10 partial files for one class totalling 4–5K LOC. Partial-class splitting is a workaround for a class that wants to be several classes. Worth a look at whether `WritePath`, `ReflowEngine`, `ThreadingAndInvalidation`, `TabStops`, `Style`, `Maintenance`, `Metrics`, `ResizeAndReflow`, `AccessAndSnapshot`, `State` should be collaborators rather than partial sections.
- **No analyzer/architecture-test layer.** With this many implicit boundaries, a `NetArchTest` (or `ArchUnitNET`) test ensuring "Pty cannot reference VT", "no `App.*` type leaks into `VT`", etc., would have caught most of section A automatically. There's nothing of the sort.
- **`Models/` is half-empty in App** (5 files, all `RemotePath*`/`Transfer*`). Other model classes are scattered through `Core/` and `CommandAssist/Models`. Convention isn't followed.
- **`UI/Replay` and `Services/` are nearly empty** (`Services/BackgroundWork.cs` + `Services/Ssh/`). Either fill the convention or collapse the folders.

## Most concentrated risk

If I had to point at one thing: **the namespace collapse (section B/C)**. As long as VT, Core, Pty, Rendering, and Replay all live in `namespace NovaTerminal.Core`, no automated rule can enforce layering — every type in every assembly looks the same to a `using` statement, and refactors silently move types across assembly lines. Until namespaces match assemblies, every other finding here will keep regressing.
