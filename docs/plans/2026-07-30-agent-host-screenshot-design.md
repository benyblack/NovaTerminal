# Agent Host A5 (Screenshot for Agents) Design

Proposed milestone **A5** of `docs/agent-host/DIRECTION.md` — pixel-level observe:
an agent can capture a live session's terminal content as a PNG image, either as a
deterministic headless re-render of the buffer (works for any pane, testable) or as
a true WYSIWYG capture of the on-screen view (exact pixels, visible panes only).
Still observe-only; the acting surface (A3) is unchanged.

## Goal

- **`novaterminal.capture_screenshot`** — MCP tool that returns a session's
  viewport as an image the agent can see (MCP image content) plus a file path
  for later reference.
- **Two capture modes behind one tool:**
  - `render` (default) — headless re-render from `TerminalBuffer` under the
    read lock: deterministic, works for background/hidden panes, no UI thread.
  - `live` — WYSIWYG capture of the live `TerminalView` on the UI thread:
    exact on-screen pixels (ligatures, inline images, background image).
- **Gating:** the existing "Agent access (observe)" toggle alone. No new
  setting, no journal entry (consistent with the other observe tools).

The change must:

- stay observe-only and default-off: with the observe toggle off, no endpoint
  exists and the tool returns guidance, not data (current behavior)
- respect layering: the headless renderer reuses the VT render-snapshot
  contract and Skia pieces; Avalonia is touched only by MainWindow (live mode),
  exactly like the A3 spawn/close executor
- follow the A4 file-export precedent: the app writes the PNG under
  `AppPaths.RecordingsDirectory/agent-exports/` and returns the path over IPC;
  no base64 payloads in the NDJSON frame protocol
- be additive at protocol version 1; no changes to existing methods

## Current State (verified)

- **Render snapshot contract already exists** in the VT leaf
  (`NovaTerminal.VT/RenderSnapshots.cs`): `TerminalRenderSnapshot` carries
  per-row styled cells (`RenderCellSnapshot` with colors/flags), a
  `RenderThemeSnapshot` (fg/bg/cursor + 16-color palette), cursor
  position/style, and `RenderImageSnapshot` entries with opaque `ImageHandle`s
  (Skia bitmaps for sixel/kitty). It is produced by
  `TerminalBuffer.CaptureRenderSnapshot(RenderSnapshotRequest, out long)`
  (`TerminalBuffer.ThreadingAndInvalidation.cs:224`), consumed today only by
  `TerminalDrawOperation` on the UI render path.
- **The registration exposes the buffer**: `AgentSessionRegistration.Buffer`
  (`AgentSessionRegistration.cs:196`) is what `readScreen` reads under
  `TerminalBuffer.Lock` from the IPC thread. The registration deliberately
  holds **no** Avalonia references; pane-owned data arrives via the push
  pattern (`UpdateSnapshot`, line 270). Font metrics/theme-agnostic rendering
  parameters are **not** pushed today.
- **UI-thread bridge already exists and is published unconditionally**:
  `IAgentActionExecutor` (`IAgentActionExecutor.cs:47`) is implemented by
  MainWindow (bodies marshal to the UI thread themselves) and published via
  `AgentHostService.SetActionExecutor(this)` at startup and on every settings
  apply (MainWindow.axaml.cs:1962 and :5045) — independent of the act toggle.
  So live capture needs **no** lifecycle change: the bridge is available
  whenever the observe endpoint is up.
- **Skia offscreen rendering is already used**: `GlyphAtlas`/`GlyphCache`
  render into raster `SKSurface`s; `SixelDecoder` produces `SKBitmap`s.
  PNG encoding is `SKImage.Encode` — no new dependency.
- **File-export precedent (A4)**: `exportReplay` writes to
  `AppPaths.RecordingsDirectory/agent-exports/` with a fresh random suffix per
  export (`TerminalPane.BuildRecordingFileName`) and returns the absolute path
  (AgentHostService.cs:725-751).
- **MCP image results**: the ModelContextProtocol SDK supports image content
  blocks in tool results (base64 data + MIME type), which Claude Code/Desktop
  render inline. The MCP server currently returns plain strings from all
  tools; this tool is the first to return mixed content.
- **No screenshot/capture capability exists anywhere today** (no
  `RenderTargetBitmap` use in the codebase).

## Design

### Gating (decided)

The observe toggle alone. A screenshot is the same data class as
`read_screen` + attributes, rendered. Files land in the same `agent-exports/`
directory as replay exports; no activity-journal entry (observe calls are not
journaled today). Sanity caps enforced server-side (app):

- `scale` clamped to [0.5, 2.0] (default 1.0)
- max canvas 8192×8192 px after scaling; PNG payload cap 16 MiB (a guard
  against pathological sizes, not an expected case)

### Protocol (additive, version stays 1)

New method in `AgentHostProtocol.Methods`:

```
captureScreenshot { paneId, mode?, scale? }
    → { filePath, width, height, modeUsed }
```

- `mode`: `"render"` (default) or `"live"`. Unknown values →
  `malformedRequest`. Serialized as a string (not an integer enum) so older
  servers reject unknown modes cleanly.
- `modeUsed` echoes the mode that produced the image (always equal to the
  requested mode in v1 — see "no silent fallback" below).
- New error code **`captureUnavailable`**: the pane exists but no image could
  be produced — for `live` mode: pane not currently visible (background tab,
  minimized window) or the view is mid-teardown; for `render` mode: the buffer
  is unavailable (session closing race). Message suggests the other mode when
  applicable. Distinct from `sessionNotFound` (unknown paneId) and from the
  unavailable-endpoint path in `AgentHostClient`.
- PNG file naming: `nova_shot_{yyyyMMdd_HHmmss}_{suffix}.png` in
  `agent-exports/`, fresh random suffix per call (A4 scheme — same-second
  collisions must not truncate an earlier file).

### App: render mode (headless, default)

New component **`TerminalSnapshotRenderer`** in `NovaTerminal.Rendering`
(the assembly that already owns glyph/atlas Skia code; VT stays a leaf, App
untouched):

1. Endpoint resolves the registration, takes `TerminalBuffer.Lock`, calls
   `CaptureRenderSnapshot` with a viewport request (current viewport, no
   selection/search highlights). Theme comes along in the snapshot.
2. Rendering parameters (font family, font size, cell width/height, baseline)
   are **pushed into the registration** by the pane via the existing
   `UpdateSnapshot` push pattern (new fields on the same method or a sibling
   `UpdateRenderParams`); the endpoint never reads the view. Font metrics come
   from the view's own measurements so the headless render matches the live
   layout cell-for-cell.
3. Renderer draws rows onto an offscreen raster `SKSurface` at
   `cols × cellWidth × scale` by `rows × cellHeight × scale`: background fill,
   cell backgrounds, glyph runs via `GlyphCache`/`GlyphAtlas` (SGR flags:
   bold/italic/underline/strikethrough/inverse/faint/hidden), cursor block,
   then `RenderImageSnapshot` bitmaps scaled into their cell rects.
4. Encode PNG, write to `agent-exports/`, return path + pixel dimensions.

v1 fidelity target: text + colors + attributes + cursor + inline images that
are already decoded to Skia bitmaps. Ligature/complex-shaping parity with the
live view is **not** guaranteed in v1 (the renderer uses the glyph cache path,
not the view's shaping pipeline) — this is the documented trade-off of
`render` mode and the reason `live` mode exists.

### App: live mode (WYSIWYG)

Additive member on the existing UI bridge — no new channel:

```csharp
Task<(AgentCaptureResult? Result, AgentCaptureError? Error)>
    CapturePaneAsync(Guid paneId, string targetFilePath, double scale);
```

on `IAgentActionExecutor`, implemented by MainWindow next to the A3
spawn/close bodies: resolve paneId → pane → its `TerminalView`, capture with
`RenderTargetBitmap` at the requested scale, encode PNG, write to the target
path. Marshals to the UI thread itself, per the interface contract; gating
(observe toggle) is enforced by the endpoint before the call, as with act.

Live mode fails with `captureUnavailable` when the pane is not in a visible,
arranged state (background tab, minimized window) — capturing an unarranged
control yields stale or empty pixels, which is worse than an honest error.
Scope is the pane's terminal content only: no window chrome, no tab bar, no
OS-level effects (acrylic/mica behind the window are not captured — the pane
content is).

### Endpoint handler

`HandleCaptureScreenshot` next to `HandleExportReplay`:

1. Deserialize params; `paneId` missing/malformed → `malformedRequest`;
   unknown mode string → `malformedRequest`.
2. Resolve registration → `sessionNotFound` if absent.
3. Clamp `scale`; compute target path in `agent-exports/`.
4. `render` → snapshot + render + write (endpoint thread; buffer lock only
   during `CaptureRenderSnapshot`). `live` → executor call; executor not
   published (startup/teardown race) → `captureUnavailable` with retry
   guidance (mirrors `actUnavailable` semantics).
5. Success → `{ filePath (absolute), width, height, modeUsed }`.

### MCP tool

`novaterminal.capture_screenshot` in `Tools/ScreenshotTools.cs` (new file,
auto-discovered by `WithToolsFromAssembly`):

- Params: `paneId` (GUID, validated client-side like the other session tools),
  `mode` (`"render"` default / `"live"`), `scale` (default 1.0).
- On success: read the PNG at the returned path and return **two content
  blocks** — an image block (`image/png`, base64) so the agent sees the
  screenshot inline, and a text block with path, dimensions, and `modeUsed`.
  File read happens in the MCP server (same machine, path returned by the
  app); a missing/unreadable file after a successful call is a protocol-level
  surprise and returns the path-only text block with a note.
- On error: the established `TryUnwrap` pattern — guidance for unavailable
  endpoint, `Error (code): message` otherwise.
- Description text documents both modes, the visibility requirement of
  `live`, and the observe-toggle requirement.

### Docs

- `docs/mcp/tools.md` — new tool entry (authoritative list).
- `docs/mcp/security.md` — observe-family row: screenshot under the observe
  toggle, files under `agent-exports/`, no new setting.
- `src/NovaTerminal.McpServer/README.md` — tool table + safety-posture list.
- `docs/agent-host/DIRECTION.md` — A5 milestone entry (this doc is its design).

## Alternatives Considered

- **Live capture only** — exact pixels, but fails for background tabs (a major
  agent use case: work proceeds in a pane the human isn't watching) and is
  untestable without a UI. Rejected as the only mode.
- **Render mode only** — deterministic and testable, but image-heavy and
  shaping-heavy sessions (sixel, ligatures, Nerd Font glyphs) are precisely
  where an agent wants to *see* what happened. Rejected as the only mode;
  shipped as the default instead.
- **Silent fallback from `live` to `render`** — an agent that asked for
  WYSIWYG must know it didn't get it. Rejected: `live` returns
  `captureUnavailable` with guidance to retry with `mode: "render"`.
- **Base64 image in the NDJSON IPC frame** — simple, but pushes hundreds of KB
  through a line-delimited text protocol and couples frame size to screen
  size. Rejected: the A4 file-path pattern is the established precedent; the
  MCP server reads the file locally.
- **New sub-toggle ("Agent screenshot")** — a screenshot is observe-class
  data; replay export needed its own gate because it retains history over
  time, not because of the file write. Rejected (decided with the owner):
  observe toggle alone.
- **Full-window or scrollback-tall capture** — v1 scope cut; see below.

## Testing

- **Renderer unit tests** (NovaTerminal.Rendering.Tests or McpServer-side
  harness): fixed test theme + fixed font metrics → structural assertions
  (dimensions, background color at known pixel, a known cell's ink color,
  cursor rect present, image drawn in its cell rect). No golden-hash compares —
  cross-platform font rasterization differs; assert structure, not bytes.
- **Endpoint protocol tests** (App.Tests/AgentHost): render mode writes a
  valid PNG and returns matching dimensions; `live` with hidden pane →
  `captureUnavailable`; unknown pane → `sessionNotFound`; bad mode string →
  `malformedRequest`; scale clamping; same-second filename uniqueness.
- **Executor contract tests**: `CapturePaneAsync` with unknown paneId →
  false/error; marshals to UI thread (assert dispatcher affinity in test).
- **MCP server tests** (McpServer.Tests): param validation (bad GUID, bad
  mode), formatting of the mixed image+text result from a fixture PNG,
  unavailable-endpoint guidance. Update the stdio E2E tool-list expectation.
- **Drift-guard note**: `ScreenshotTools` mirrors nothing from other
  assemblies; new contract types are covered by the existing JSON-context
  round-trip tests.

## Out of Scope

- Full-window capture (chrome, tabs, Command Assist UI)
- Scrollback-as-tall-image and multi-pane collage
- Video/GIF capture; periodic or event-triggered screenshots
- Complex-shaping/ligature parity in `render` mode (documented fidelity gap)
- Acting-surface changes (A3 untouched); new settings toggles (none added)

## Suggested PR Slicing

1. **Contracts + endpoint skeleton:** `captureScreenshot` method, params/result
   contracts, `captureUnavailable` error code, handler returning
   `captureUnavailable` for both modes, protocol tests. No behavior yet.
2. **Render mode:** pushed render params on the registration,
   `TerminalSnapshotRenderer`, endpoint wiring, PNG write, renderer + endpoint
   tests. Tool works end-to-end in `render` mode.
3. **Live mode:** `IAgentActionExecutor.CapturePaneAsync` + MainWindow
   implementation, endpoint branch, executor tests.
4. **MCP tool + docs:** `ScreenshotTools` with mixed image+text result, E2E
   list update, `tools.md`/`security.md`/README/DIRECTION updates.

Slices 2 and 3 can land in either order; the contract (slice 1) carries `mode`
from day one so neither is a breaking change.
