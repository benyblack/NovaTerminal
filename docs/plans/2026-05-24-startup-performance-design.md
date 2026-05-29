# Startup Performance Design

**Goal:** Improve NovaTerminal startup performance across first window shown, first terminal ready, and full session restore complete, while preserving current terminal behavior and producing before/after comparison data for a measurable report.

## Problem

NovaTerminal startup currently does too much synchronous work on the critical path before the first interactive frame. `MainWindow` construction loads settings, migrates profiles, restores the full session, wires optional subsystems, and creates terminal panes before the first useful terminal is ready. `TerminalPane` also reloads settings during pane setup, multiplying startup I/O and object creation during restore-heavy launches.

This hurts three user-visible metrics at once:

- time to first window shown
- time to first terminal ready
- time to session restore complete

The current code also lacks a dedicated startup measurement surface for comparing baseline and post-change behavior across repeated launches.

## Constraints

- Do not modify VT parsing or render behavior unless explicitly required.
- Keep Command Assist as an app-layer subsystem.
- Keep UI concerns out of terminal core logic.
- Prefer additive changes over large refactors.
- Preserve performance-sensitive render and parser paths.
- Add deterministic tests for new startup orchestration and metrics logic.
- Avoid fragile UI snapshot dependencies.

## Success Criteria

The change is successful when:

- the first window is shown earlier than today
- the first selected terminal becomes usable earlier than today
- full restore still completes correctly, but does not block initial usability
- repeated startup runs can be compared before and after with saved measurements
- the comparison report can state the improvement for each startup phase with concrete numbers

## Recommended Approach

Adopt staged startup with progressive restore and explicit instrumentation.

### Why this approach

- It improves all three startup metrics rather than optimizing only first paint.
- It keeps the heavy work inside app-layer orchestration instead of pushing shortcuts into terminal core code.
- It preserves full restore semantics while moving non-critical work off the critical path.
- It creates a durable measurement system so regressions can be detected and the enhancement can be reported with evidence.

## Architecture

### 1. Define explicit startup phases

Treat startup as a sequence of app-layer phases:

- bootstrap start
- main window constructed
- first window opened
- first terminal ready
- progressive restore complete
- deferred startup work complete

These phases should be recorded once per launch and owned by app-layer code.

### 2. Keep first paint minimal

`Program`, `App`, and `MainWindow` should do the minimum needed to show the shell window and selected tab container. Optional or restore-heavy work should not block initial window display.

This means:

- keep `Program.cs` focused on CLI routing, startup timestamp capture, and Avalonia bootstrap
- keep `App.axaml.cs` focused on window creation
- reduce `MainWindow` constructor work that is not required for the first frame

### 3. Restore the selected tab first

Session restore should stop materializing the full session synchronously inside `MainWindow`.

The selected tab should restore first. Within that tab, the active pane should be prioritized for session startup so the user reaches a usable terminal quickly. Remaining panes in the selected tab may be created just after that to preserve layout fidelity, but background tabs should be restored later.

### 4. Restore background tabs progressively

Background tabs should be restored one tab at a time after first terminal readiness, using dispatcher-scheduled background work. This keeps the UI responsive while preserving full restore behavior.

Progressive restore should be owned by a small app-layer coordinator rather than expanding `SessionManager` into a large stateful service.

### 5. Defer non-critical startup subsystems

The following work should move off the first-paint path when possible:

- command palette registration and population
- transfer center initialization
- tab list and other derived menu population that is not needed for first interaction
- optional Command Assist infrastructure for panes that are not the first active pane

The first active pane may still initialize the minimum needed command and shell integration behavior if required for correctness, but background panes should not pay that cost immediately.

### 6. Load settings once per app startup

`TerminalSettings.Load()` should remain an app-level startup concern. `TerminalPane` should accept shared settings rather than synchronously reloading them from disk during pane setup.

This removes repeated file I/O and duplicated settings object construction during multi-tab or multi-pane restore.

## Instrumentation

### Primary collector

Extend `NovaTerminal.Core.RendererStatistics` with startup-specific counters and per-launch timings so startup measurements reuse the existing app-side metrics surface.

### Required startup metrics

Add measurements for:

- app boot to main window constructed
- app boot to window opened
- app boot to first terminal ready
- app boot to session restore complete
- deferred startup work duration
- background restore duration

### Event sources

- `Program.Main` records bootstrap start
- `MainWindow` records constructor completion and `OnOpened`
- `TerminalPane` records first terminal ready on the first meaningful readiness signal
- the startup/restore coordinator records restore and deferred-work completion

### First terminal ready definition

For startup instrumentation, the first terminal is considered ready when the first selected pane receives the first meaningful session readiness signal:

- first prompt-ready signal, if available
- otherwise first output received from the session

This definition avoids requiring terminal-core changes and maps well to the user’s perception of readiness.

## Baseline And Comparison Reporting

The enhancement must be measurable before and after implementation.

### Measurement protocol

Create an opt-in startup measurement path that writes structured launch metrics to an artifact file for each run. The same path will be used:

- before code changes, to capture the baseline
- after code changes, to capture the improved behavior

The protocol should support repeated launches on the same machine and same build configuration so comparisons are meaningful.

### Required comparison outputs

The workflow should be able to produce:

- per-run startup timings
- summary statistics across repeated runs
- delta and percentage improvement for each startup phase

At minimum, the final report should compare:

- first window shown
- first terminal ready
- session restore complete

### Reporting format

Use a machine-readable artifact for measurements and a human-readable summary report. The machine-readable artifact should be simple enough for repeated local runs, such as JSON or JSONL. The human-readable report can be generated from that artifact and should clearly state before/after averages or medians plus percentage change.

## Error Handling

- If progressive restore fails for a background tab, the app should continue running and restore as much of the session as possible.
- If startup metrics capture fails, the app should continue starting normally and log the failure conservatively.
- If deferred work is still pending when the app exits, the app should not hang solely to complete metrics emission.

## Testing Strategy

- Add deterministic tests for startup metric recording in `RendererStatistics`.
- Add deterministic tests for the startup orchestration and progressive restore coordinator.
- Add tests proving selected-tab and active-pane priority.
- Add tests proving background tabs are queued and completed in stable order.
- Add tests proving `TerminalPane` can use injected/shared settings without reloading from disk.
- Add a focused test around startup comparison artifact generation or summary logic.

## Alternatives Considered

### Keep startup blocking and only micro-optimize constructor work

Pros:

- smallest code change

Cons:

- does not address the structural startup bottleneck
- unlikely to improve all three target metrics substantially
- does not create a durable before/after measurement workflow

### Defer almost everything until after first paint

Pros:

- strongest first-paint improvement

Cons:

- risks delaying actual terminal usability
- can make restore appear unstable or incomplete
- over-optimizes one metric at the expense of the others

### Disable or reduce restore by default

Pros:

- easy path to faster startup numbers

Cons:

- changes product behavior rather than improving implementation quality
- does not meet the goal of preserving restore while making it non-blocking
