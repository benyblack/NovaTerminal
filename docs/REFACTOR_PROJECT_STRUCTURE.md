# NovaTerminal Modularization (Split into Libraries + App) — IDE Execution Prompt

You are working inside the NovaTerminal repository in an IDE. Your task is to refactor the solution into several smaller class library projects plus one Avalonia app project, with minimal behavior change and a clean dependency DAG to enable headless VT correctness testing and future TUI harness work.

## High-Level Goal
Split the current single-project structure into:

- src/NovaTerminal.App            (WinExe Avalonia app — UI + product glue)
- src/NovaTerminal.VT             (Pure VT engine: parser + state + buffers — NO Avalonia/Skia/OS calls)
- src/NovaTerminal.Rendering      (Renderer planning + diff + caches — NO Avalonia, Skia allowed if currently used)
- src/NovaTerminal.Pty            (PTY abstraction + ConPTY/Rust PTY impl — NO Avalonia)
- src/NovaTerminal.Replay         (Replay format + reader/writer + golden runner — NO Avalonia)
- (optional) src/NovaTerminal.Testing (Harness utilities; can be postponed if too much)

## Critical Constraints
1) Preserve existing behavior (no feature removals).
2) Keep namespaces stable at first if that reduces churn (it’s fine if file namespaces remain NovaTerminal.Core initially).
3) Enforce dependency DAG:
   - VT has no dependency on App/Rendering/Pty/Replay.
   - Rendering depends on VT only.
   - Pty independent of UI.
   - Replay depends on VT (and optionally Pty if recorder uses PTY interface).
   - App depends on all.
4) Remove ALL Avalonia references from VT/Pty/Replay. Rendering must not reference Avalonia; it may reference SkiaSharp if already used.
5) Keep builds green after each milestone; do incremental commits.

---

## Step 0 — Inspect Current Layout (DO THIS FIRST)
- Open solution and enumerate projects, target frameworks, and project references.
- Search for files referencing:
  - "Avalonia"
  - "SkiaSharp"
  - "ConPty"
  - "Replay"
  - "TerminalView"
- Produce a short mapping list: file → category (VT / Rendering / Pty / Replay / App).

Do NOT start moving files until you’ve produced this list.

---

## Step 1 — Create New Projects Under /src
Create these projects (net10.0 unless repo uses different):
- src/NovaTerminal.App (WinExe; Avalonia; this will become the main app)
- src/NovaTerminal.VT (class library)
- src/NovaTerminal.Rendering (class library)
- src/NovaTerminal.Pty (class library)
- src/NovaTerminal.Replay (class library)

Actions:
1) Create new csproj files with consistent settings (nullable enable, implicit usings enable).
2) Update solution (.sln) to include them.
3) Add ProjectReferences according to DAG:
   - App references VT, Rendering, Pty, Replay.
   - Rendering references VT.
   - Replay references VT (and optionally Pty later).
4) Ensure App is the startup project and still builds (even before moves, app can be empty temporarily).

---

## Step 2 — Move UI Files Into NovaTerminal.App
Find all files that include `using Avalonia` or are clearly UI:
- TerminalView (currently in Core/TerminalView.cs)
- Controls/*
- UI/*
- Any App.axaml / MainWindow.* / viewmodels / dialogs

Move them into src/NovaTerminal.App, preserving folder structure as much as possible:
- e.g. NovaTerminal.App/Controls/...
- NovaTerminal.App/UI/...

Fix compile errors by updating namespaces OR keeping namespaces unchanged temporarily.

Ensure App builds.

---

## Step 3 — Extract Pure VT Core Into NovaTerminal.VT
Move all VT engine/state files that MUST be UI/OS independent:
Typical candidates (confirm by scan):
- Ansi/VT parser (AnsiParser, CSI/OSC handling)
- TerminalBuffer + TerminalRow/Cell + selection/search state that is buffer-level
- Cursor + Mode state
- TermColor structs/converters (but no Avalonia Color types)
- Unicode width / grapheme logic (if exists)

Hard rule:
- NovaTerminal.VT must not reference Avalonia, SkiaSharp, or any OS APIs.
- If a file depends on Avalonia Color, split it: keep a pure color model in VT, and conversion helpers in App.

After moving:
1) Add a public façade class if it doesn’t exist:
   - TerminalEmulator (or Terminal)
   - Methods: ProcessInput(ReadOnlySpan<byte>), Resize(cols, rows), Snapshot()
2) Define a UI-free snapshot type in VT (if not already):
   - TerminalSnapshot: rows/cols, cells with attributes, cursor state, mode flags.
   - Keep it simple; can be internal for now if used only by tests.

Ensure App builds after switching to VT reference.

---

## Step 4 — Extract PTY Into NovaTerminal.Pty
Move PTY/session logic:
- ConPTY native wrappers
- Rust PTY session integration
- ITerminalSession abstractions (rename to IPtySession if appropriate)
- Shell spawn helpers (if purely spawn related)
- Any PTY resize propagation

Rules:
- No Avalonia references.
- Provide a small stable interface in Pty, e.g.:
  interface IPtySession { ReadAsync; WriteAsync; Resize; ExitCode; Dispose; }

Update App to use NovaTerminal.Pty.

Build.

---

## Step 5 — Extract Replay Into NovaTerminal.Replay
Move everything under Core/Replay/* into Replay project:
- Replay models (event types)
- Reader/writer
- Runner
- Golden master utilities
- Recorder (if it uses PTY, depend on NovaTerminal.Pty; otherwise keep recorder in App)

Rules:
- No Avalonia references.
- Replay types should reference VT snapshot types if needed.

Update App references.

Build.

---

## Step 6 — Extract Rendering Into NovaTerminal.Rendering
Move render/diff/caching logic:
- RowCache
- RenderSnapshots
- GlyphCache/Atlas
- SharedSKFont/Typeface
- TerminalDrawOperation
- RendererStatistics

Rules:
- Rendering project must not reference Avalonia.
- If it depends on SkiaSharp already, keep SkiaSharp in Rendering (acceptable).
- Rendering can consume VT snapshot types to compute damage regions / draw ops.

Update App to call Rendering layer to produce draw commands and then draw with Avalonia.

Build.

---

## Step 7 — Fix Namespaces + Internals (Churn Control)
After everything compiles:
- Decide whether to keep legacy namespaces (NovaTerminal.Core.*) or rename to NovaTerminal.VT / Rendering / Pty / Replay gradually.
- Avoid huge rename diffs in this refactor; only do minimal changes needed.

Where needed, add `InternalsVisibleTo` to allow tests access to VT internals without exposing too much public API.

---

## Step 8 — Update Tests and CI References
Update test projects to reference libraries instead of App:
- NovaTerminal.Tests should reference VT + Replay (+ Rendering only for renderer tests).
- UI automation tests (if any) can reference App.

Ensure `dotnet test` works.

---

## Step 9 — Deliverables / Definition of Done
You must produce:
1) New /src structure with projects as described.
2) Solution builds in Release.
3) Tests still pass.
4) A short `docs/ARCHITECTURE_SPLIT.md` with:
   - project responsibilities
   - dependency diagram (text)
   - “do not reference Avalonia from VT” rule
5) A minimal “ownership map” section listing which project owns:
   - VT correctness
   - Rendering perf/damage tracking
   - PTY I/O
   - Replay format

---

## Guardrails / Common Pitfalls
- If a type is used by both VT and UI, keep the model in VT and add conversion adapters in App.
- Avoid circular references; if you hit one, extract a small shared primitives project ONLY if unavoidable (try hard to avoid).
- Keep commits incremental: after each Step (2..6), project must compile.

---

## Output Required from You
At the end, provide:
- A list of moved files per project.
- The final dependency graph.
- Any “TODO” notes that were intentionally deferred.