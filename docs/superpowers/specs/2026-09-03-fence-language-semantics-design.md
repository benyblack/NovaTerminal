# Fenced-Block Language Semantics in the Agent Output Panel

Date: 2026-09-03
Status: designed (not implemented)
Depends on: PR #378 (`agent-output-markdown-panel`) — this branch is stacked on it, because
`main` has no `AgentOutput` subsystem yet. Rebase onto `main` once #378 merges.

## Summary

Give the Agent Output panel an opinion about **what a fenced block contains**, keyed on its
info string:

- ` ```markdown ` / ` ```md ` renders as a nested document instead of as source, governed by a
  single panel-level switch;
- ` ```diff ` keeps its monospace body but colors each line by its leading marker;
- **every other info string, and every block without one, renders exactly as it does today.**

The language is already captured — `MarkdownRenderer.cs:105` passes `fenced.Info.ToString()`
into `BuildCodeBlock` — and used for exactly one thing: a label `TextBlock`. This makes it
mean something.

## Why

Reported from real use: `claude -p "generate some markdown example"` answers with its entire
response wrapped in a ` ```markdown ` fence, so the panel showed a page of unrendered markdown
source with a language label on top. That rendering is *correct* — a fence means source — but
it revealed that the panel has no way to distinguish "here is code" from "here is a document I
wrote for you", which is a distinction agents make constantly.

The insight this came from: the markdown case is not a markdown special case. It is the first
instance of the renderer needing an opinion about a fence's *content type*, and the same seam
serves diffs, JSON, and anything later.

## Non-goals

- **Syntax highlighting.** Coloring `csharp`/`python`/`bash` bodies per token is the other axis
  entirely — it needs a tokenizer per language (a new dependency or N hand-rolled ones), token
  colors as theme resources across light and dark, and a perf budget that survives re-rendering
  on the 300ms streaming debounce. Explicitly deferred.
- **Actionability.** No Run button on ` ```bash `, no Apply on ` ```diff `. Executing
  agent-authored commands needs a confirmation-and-trust design of its own.
- **An open/plugin registry.** Handlers are resolved by a closed `switch` over info strings.
  Nothing outside the assembly registers handlers, so a registration mechanism would be
  machinery without a consumer.
- **Persisting the switch.** It is per-pane runtime state, not a `TerminalSettings` field. A
  settings field would also have to be threaded through `TerminalPane.ApplySettings`'s
  effective-settings whitelist and registered in the MCP `SettingsTools` drift guard; that cost
  is not worth a view preference.
- **A JSON handler.** A third handler shape, cheap but out of the first cut.

## Design

### 1. The seam

`BuildCodeBlock` hardcodes the body as a single flat `Run` (`MarkdownRenderer.cs:192`). It
instead asks a resolver:

    internal static IFenceBody? Resolve(string? info)   // null => today's behavior, unchanged

A handler produces the block **body only**:

    internal interface IFenceBody
    {
        /// True when this handler replaces the source with something else, and so
        /// participates in the panel's rendered/source switch. A restyle (diff) is not
        /// a transform.
        bool IsTransform { get; }

        Control Build(string code, MarkdownTheme theme, FenceContext context);
    }

    internal sealed record FenceContext(
        int Depth,
        bool RenderFencedMarkdown,
        NestedMarkdownRenderer RenderNested,
        Action<string>? OnCopyText);

    internal delegate Control NestedMarkdownRenderer(string markdown, int depth);

**Block chrome is not a handler's business.** The border, header row, language label and Copy
button stay in `BuildCodeBlock`. This keeps two guarantees that would otherwise erode
per-handler: every code block looks like every other one, and **Copy always yields the raw
source** regardless of what is displayed — automatic, since the Copy handler already closes
over `code`.

Two rejected alternatives, on the record:

- **Pre-transforming the source text** (unwrapping fences before Markdig parses) is the
  cheapest option and is wrong twice over: it destroys the source irrecoverably, so the switch
  cannot exist, and it cannot express diff coloring at all, which is styling rather than text.
- **Post-processing the built control tree** (walking it and swapping code-block `Border`s) has
  no parse information left at that point and couples to the tree's exact shape.

### 2. Info-string matching

Match on the **first whitespace-delimited token** of the info string, trimmed and lowercased
with `ToLowerInvariant`. Deliberately not on the whole string: ` ```markdown title="README" `
must still resolve to the markdown handler. Taking the first token ourselves makes the match
independent of how Markdig chooses to split `Info` from `Arguments`.

Recognized: `markdown`, `md` → markdown handler. `diff`, `patch` → diff handler. Anything else,
including the empty string and indented (`CodeBlock`) blocks that have no info at all, resolves
to `null` and takes the existing path untouched.

### 3. Handler: markdown

Parses the body and calls back into the renderer's existing `AppendBlocks`, so every block type
the panel already supports — headings, tables, task lists, nested code blocks, links with their
scheme allowlist — works inside a fence for free, with no duplicated rendering logic.

**Depth cap: `MaxFenceDepth = 1`.** A markdown fence encountered at depth 0 renders nested; one
encountered at depth 1 or deeper renders as source. Agent output is untrusted content and this
tree is rebuilt on the streaming debounce, so unbounded recursion is a cost multiplier against
a case that does not occur in practice.

`IsTransform => true`.

When `FenceContext.RenderFencedMarkdown` is false the handler returns the plain source body —
identical to the unhandled path, but still reporting itself as a transform so the switch stays
visible and the choice is reversible.

### 4. Handler: diff

Keeps the body monospace and emits one `Run` per line, colored by leading marker. Order of tests
matters — the three-character file headers must be checked before the one-character markers, or
`+++ b/file` reads as an addition:

| Line starts with | Brush | Rationale |
|---|---|---|
| `+++ ` or `--- ` | `Secondary` | File headers, not content changes |
| `diff --git`, `index ` | `Secondary` | Git envelope |
| `@@` | `NtYellow` | Hunk header |
| `+` | `NtGreen` | Addition |
| `-` | `NtRed` | Removal |
| anything else | `Foreground` | Context line |

`NtGreen`, `NtRed` and `NtYellow` already exist as theme resource keys, so this adds three
properties to the theme record using the established `Find(anchor, "NtGreen", fallback)` pattern
and no new theme plumbing.

`IsTransform => false` — nothing is hidden by a restyle, so there is nothing to recover and the
switch does not apply.

### 5. The panel switch

One `bool RenderFencedMarkdown` on `AgentOutputViewModel`, default **true**, surfaced as a
switch in the **panel** header beside Copy — not in each block's header.

The reason is the re-render: `AgentOutputPanel.axaml.cs:74` rebuilds the entire control tree
whenever `MarkdownText` changes, which during streaming is every ~300ms. Per-block state living
in a block's own control would be reset on every tick, and keying it by block ordinal would
silently reattach a choice to the wrong block when a new block arrives earlier in the stream.
One view-model bool survives every re-render for free and has no identity problem at all. The
cost is granularity — it flips every markdown fence in the response together — which is a case
that does not arise in practice.

Per-pane, resets with the pane. Not persisted (see non-goals).

### 6. Render result and switch visibility

A switch that is usually a no-op is clutter, so it appears only when the current render actually
produced a transform block. `Build` therefore returns a small result instead of a bare
`Control`:

    internal sealed record MarkdownRenderResult(Control Root, bool HasTransformBlock);

`AgentOutputPanel` binds the switch's visibility to `HasTransformBlock`. There is exactly one
caller of `Build`, so this is a contained change.

### 7. Targeted refactor

`MarkdownRenderer.cs` is ~700 lines and its private nested `Theme` class is needed by handlers in
other files. Extract it to `AgentOutput/MarkdownTheme.cs` as `internal sealed class MarkdownTheme`
(same `Resolve`/`Find` behavior, three brushes added), and put the handlers in
`AgentOutput/Fences/`. This keeps the renderer focused on block dispatch and makes each handler
independently testable, which is the stated benefit of having an interface at all.

No layering change: everything stays inside `NovaTerminal.App`, so the architecture guards are
unaffected.

## Edge cases

- **Empty or whitespace-only fence body** — renders as today. A handler that would produce
  nothing must not produce an empty bordered box.
- **Unterminated fence while streaming** — Markdig treats it as a fenced block whose content is
  everything to the end. The markdown handler renders a partial document, which grows on the
  next tick; no special handling.
- **Markdown fence whose content is not markdown** — renders as one paragraph. Acceptable; the
  switch recovers the source.
- **Malformed diff** (no markers) — every line takes `Foreground`, i.e. looks like today.
- **Info string casing and aliases** — `MARKDOWN`, `Md`, `Patch` all match, per §2.

## Testing

Unit, against the renderer and handlers directly:

- a markdown fence renders nested blocks (a heading inside a fence produces a heading control,
  not a code block);
- the same fence with `RenderFencedMarkdown: false` renders source, and still reports
  `HasTransformBlock`;
- depth cap: a markdown fence inside a markdown fence renders the inner one as source;
- diff marker mapping, including that `+++ b/file` is `Secondary` and not `NtGreen`;
- unknown info strings (`csharp`, `bash`, `""`) and indented code blocks are byte-identical to
  current output;
- info-string normalization: `MARKDOWN`, `md`, and `markdown title="x"` all resolve;
- Copy yields raw source in both switch positions.

Panel and view-model:

- the switch is hidden when a response has no transform block and visible when it has one;
- toggling it re-renders, and the choice survives a subsequent `MarkdownText` update.

Regression: the existing `MarkdownRendererTests` (block subset, raw-HTML inertness, link
allowlist) must keep asserting exactly what they assert today — any change to an *expectation*
there means the unhandled path moved and is a bug in this work. Their call sites do need one
mechanical edit each, because `Build` returns `MarkdownRenderResult` rather than `Control` (§6):
14 sites currently written `(StackPanel)MarkdownRenderer.Build(...)` become
`(StackPanel)MarkdownRenderer.Build(...).Root`. That is the only permitted diff in that file's
existing tests.

`MarkdownRendererTests.cs:120` already renders a ` ```csharp ` block, so it doubles as the
guard that an unrecognized language is untouched.

## Files touched

- `src/NovaTerminal.App/AgentOutput/MarkdownRenderer.cs` — resolver call in `BuildCodeBlock`,
  depth parameter through `AppendBlocks`, `MarkdownRenderResult`
- `src/NovaTerminal.App/AgentOutput/MarkdownTheme.cs` — **new** (extracted, +3 brushes)
- `src/NovaTerminal.App/AgentOutput/Fences/IFenceBody.cs` — **new**
- `src/NovaTerminal.App/AgentOutput/Fences/FenceBodyResolver.cs` — **new**
- `src/NovaTerminal.App/AgentOutput/Fences/MarkdownFenceBody.cs` — **new**
- `src/NovaTerminal.App/AgentOutput/Fences/DiffFenceBody.cs` — **new**
- `src/NovaTerminal.App/AgentOutput/AgentOutputViewModel.cs` — `RenderFencedMarkdown`
- `src/NovaTerminal.App/AgentOutput/AgentOutputPanel.axaml` and `.axaml.cs` — header switch,
  pass the flag
- `tests/NovaTerminal.App.Tests/AgentOutput/MarkdownRendererTests.cs` — extended
- `tests/NovaTerminal.App.Tests/AgentOutput/FenceBodyTests.cs` — **new**
- `tests/NovaTerminal.App.Tests/AgentOutput/AgentOutputPanelTests.cs` — switch visibility
- `tests/NovaTerminal.App.Tests/AgentOutput/AgentOutputViewModelTests.cs` — the new property

## Accepted limitations

- The switch is **all-or-nothing per response**, by design (§5).
- The switch does **not persist** across panes or restarts (non-goals).
- A markdown fence nested inside another renders as source, by design (§3).
- `IsTransform` exists so the switch can be gated, which means a future handler that transforms
  content inherits the switch whether or not that is the right affordance for it. Revisit when a
  third transform handler appears, not before.

## Sequencing

1. #378 merges.
2. Rebase `feat/fence-language-semantics` onto `main`.
3. Implement per the plan that follows this spec.

Implementing before step 1 is possible — the branch is stacked and builds — but the PR cannot
merge until #378 does, and any further review churn on #378's `MarkdownRenderer.cs` lands here
as a conflict.
