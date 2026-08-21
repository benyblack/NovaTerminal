# Agent Access — Per-Pane Activity Indicator

**Date:** 2026-08-21
**Status:** Design approved, ready for implementation plan
**Components:** `src/NovaTerminal.App` (`AgentHost`, `Controls/TerminalPane`, `MainWindow`, `SettingsWindow`, `Shell/TerminalSettings`)

## Summary

Make it visible, in the pane and tab chrome, when an AI agent can reach a terminal
session and when one is actually reading from or typing into it.

Today the agent-host permissions are real but invisible at the point of use. A user
with `AgentAccessObserveEnabled` and `AgentAccessActEnabled` on has every pane
readable and every eligible pane writable, and the only surface that says so is the
retrospective **Agent Activity** journal window, reached from a menu. Nothing in the
pane you are typing into indicates that an agent may be reading it, or that an agent
pressed Enter in it ten seconds ago.

This design adds three signals, each carrying exactly one meaning:

| Signal | Location | Meaning |
|---|---|---|
| Observe indicator | Window chrome, near the tab strip | Agent access is on for this app |
| Pane agent segment | Pane `StatusBar`, on **actable panes only** | An agent can type into this pane; colour and text carry live activity |
| Tab label marker | Tab header | An agent **wrote** to a pane in this tab (optionally: also read) |

## Background — what already exists

- **Per-pane registration.** Every live pane registers an `AgentSessionRegistration`
  in `AgentSessionRegistry` (`src/NovaTerminal.App/AgentHost/AgentSessionRegistry.cs`),
  carrying a `TabId` association set by `MainWindow`. Per-pane *and* per-tab
  granularity is therefore already available; no new identity plumbing is needed.
- **Permissions are split by scope.** `AgentAccessObserveEnabled` is global and
  uniform: if it is on, *every* pane is readable. `AgentAccessActEnabled` is global
  too, but acting on an SSH pane additionally requires that profile's
  `AllowAgentAccess` (`MainWindow.axaml.cs:3265`). So act-reachability genuinely
  varies per pane; observe-reachability does not.
- **A background sweep already runs.** `AgentHostService` owns a 1 s
  `_sweepTimer` → `SweepStatuses()` (`AgentHostService.cs:345`) that already walks
  every registration.
- **Thread-safe snapshot precedent.** `AgentSessionRegistration` holds lock-protected
  metadata and an `AgentSessionStatusMachine` precisely because the registry is read
  from a background IPC thread while Avalonia controls are UI-thread-only.
- **Reads are currently unobservable.** The journal records acting calls and
  `captureScreen` only (`AgentHostService.cs:1064`). `readScreen`, `readScrollback`,
  `getSessionStatus`, and `waitForEvents` leave no trace anywhere. This is the one
  genuinely new piece of plumbing this design requires.
- **`waitForEvents` is not pane-scoped.** `WaitForEventsParams` carries only
  `sinceSeq` and `timeoutMs` (`src/NovaTerminal.AgentHost.Contracts/StatusContracts.cs:83`)
  and reads one app-wide `AgentEventRing`. There is no pane to attribute a
  subscription to, so it cannot drive a per-pane tier.

## Design rationale

Two decisions shape everything else.

**Signals are placed where the permission lives.** Observe is a property of the
application, so it gets one application-level indicator. Act is a property of the
pane, so the pane-level mark appears *only* on actable panes. A per-pane mark for
observe-reachability would be identical on every pane and would decay into wallpaper;
ambient chrome that never differs stops being read.

**Reads and writes must not look alike.** An indicator that treats "an agent read my
screen" and "an agent pressed Enter in my production shell" the same way fails at
exactly the moment it matters. Reads drive a quiet state that decays silently; writes
get a distinct, lingering treatment. The loud signal stays scarce so it stays
meaningful.

## Definitions

**Actable pane** — the condition that drives the pane segment's existence:

```
AgentAccessObserveEnabled
  && AgentAccessActEnabled
  && (pane is local || pane's SSH profile has AllowAgentAccess)
```

`AgentHostService` already holds every input to this: the two toggles as
`ActEnabled`, and the per-profile probe as `_sshProfileAllowlist`. The computed
result is published on the registration so the pane can read it without
re-deriving policy.

## The observe indicator

One application-level indicator, since observe-reachability is uniform: a small glyph
in the window chrome beside the existing `TabOverflowBadge`
(`MainWindow.axaml:150`), visible exactly while `AgentAccessObserveEnabled` is on.

It is primarily a permission light, with two activity states layered on because it is
the only surface at the right scope for them:

- **Polling.** While at least one `waitForEvents` long poll is in flight, the indicator
  shows a "watching" state. The subscription is app-scoped, so this is the only correct
  place for it — see the note in Background. `AgentHostService` tracks it as a counter
  incremented for the duration of each in-flight `HandleWaitForEventsAsync` call.
- **Observe-only reads.** With act off no pane carries a bar, so the indicator also
  takes on the `Watched` styling while any pane is being read. Otherwise reads would be
  invisible everywhere in that mode.

It does not otherwise aggregate the per-pane tiers. Its tooltip names the enabled
permissions; clicking it opens the Agent Activity window.

## The state machine

New `AgentAttentionMachine`, a sibling of `AgentSessionStatusMachine` on
`AgentSessionRegistration` — same shape, same threading rationale.

```csharp
enum AgentAttentionTier { Idle, Watched, Wrote }

readonly record struct AgentAttentionSnapshot(
    AgentAttentionTier Tier,
    DateTimeOffset? LastWriteUtc,
    string? LastWriteMethod);
```

Signals: `NoteRead()`, `NoteWrote(method)` and `Tick()` pushed by the endpoint;
`NoteFocusChanged(bool)` pushed by the pane. Focus is a stored flag rather than a
one-shot event, because a write can land on a pane that is *already* focused — the
floor then has to be retired by the tick, not by a focus change that never comes.

Rules:

- **Watched** while a pane-addressed read landed within the last **3 s** — that is
  `readScreen`, `readScrollback`, `getSessionStatus`, or `captureScreen`. Long polls
  are excluded because they name no pane; they drive the app-level indicator instead.
- **Wrote** on `sendInput`, `spawnSession`, or `closeSession`. Sticky: it clears when
  the pane gains focus, but never sooner than **10 s** after the write. Without the
  floor, an agent typing into the pane you are already looking at would clear the
  signal before you could perceive it.
- **`Wrote` outranks `Watched`.**

The machine takes an injectable clock and touches no Avalonia types and no locks of
its own — it is plain `[Fact]` material. The lock lives in the registration wrapper.

### Clearing semantics, and why

Time decay alone was rejected: if you were in another tab for a minute, a write would
have happened and left no trace. Explicit dismissal was rejected too — routine agent
use would become a stream of things to click away, and a nagging indicator gets
dismissed reflexively. Clearing on focus is the only rule where "the signal is gone"
actually implies "you saw it".

## Threading and propagation

Signals arrive on the IPC background thread; panes and tab headers are UI-thread-only.
This is the mirror image of the problem `AgentSessionRegistration` already solves, and
it uses the same answer: the machine is lock-protected behind the registration, the
endpoint pushes signals from the background thread, the UI pulls snapshots.

Decay requires a clock tick. The existing 1 s `SweepStatuses()` pass already walks
every registration, so it ticks the attention machines too rather than introducing a
second timer. On a tier change, the service `Dispatcher.Post`s to the owning pane and
to `MainWindow`. One-second granularity against a three-second read window is
comfortably sufficient.

## Pane rendering, and the cleanup it forces

The pane segment reuses the existing `StatusBar`
(`src/NovaTerminal.App/Controls/TerminalPane.axaml:151`) rather than adding a new
overlay: it covers no terminal content and has room for words and a timestamp, which
a corner badge does not.

`StatusBar` sits at `Grid.Row="1"` of a `RootGrid` with `RowDefinitions="*,Auto"`, so
toggling its visibility takes 22 px from the terminal row and fires a PTY resize. That
is acceptable only under a strict invariant:

> **Visibility is driven by the persistent layer alone. The activity tiers change the
> segment's content and colour, never whether the bar exists.**

The consequence is that the only reflow happens when the user toggles the permission
in Settings — a deliberate action — and never in response to anything an agent does.
It also composes cleanly: a write can only land on an actable pane, which by
definition already shows the bar, so the loud tier can never need to summon one.

Making that invariant structural requires two changes, because two independent
features cannot both own one boolean:

- Add a named `AgentStatusSegment` beside `StatusBarRules`, so the SSH port-forward
  refresh (which clears `StatusBarRules` wholesale in `UpdateStatusBarUI`) cannot
  stomp it.
- Replace the direct `StatusBar.IsVisible` writes at
  `TerminalPane.axaml.cs:3991` and `:4083` with a single
  `UpdateStatusBarVisibility()` that ORs the reasons: SSH forwards present, or the
  pane is agent-actable.

Clicking the agent segment opens the existing Agent Activity window.

## Tab rollup and its setting

Whether the tab strip surfaces reads is a genuine judgement call, so it is a setting
rather than a hardcoded choice:

- **`TerminalSettings.AgentIndicatorTabRollup`** — `"WritesOnly"` (default) or
  `"All"`. Unrecognised values behave as `"WritesOnly"`, so a typo cannot make the
  chrome noisier than requested.
- Read by `MainWindow`, which owns the tab strip. It is therefore **not** added to
  `TerminalPane.ApplySettings`'s `effectiveSettings` whitelist — the pane never reads
  it. This matches `ShellExitPolicy`, whose consumer is likewise `MainWindow`.
- Surfaced as a dropdown in the **Agent access** section of `SettingsWindow.axaml`,
  beside the permission toggles it reports on.

The app already has this exact mechanism and the rollup follows it rather than inventing
a parallel one: `TabRuntimeState` carries `HasBell` / `HasActivity`
(`MainWindow.axaml.cs:111`), and those are rendered as **glyph suffixes appended to the
tab label** in `BuildTabDisplayLabels` (`:924`), mirrored as prefixes in
`GetTabMenuLabel` (`:612`) and as words in the automation label (`:3142`). The rollup
adds an `AgentTier` to that state and one more suffix. `UpdateTabVisuals` rewrites every
label on each pass, so there is no new visual element, no header restructuring, and
nothing to re-apply after a rename.

One consequence: the label is a single `TextBlock` whose `Foreground` is set wholesale by
`UpdateTabVisuals`, so the tab rollup separates the tiers by **glyph**, not colour — a
keyboard for a write, an eye for a read. That is also the colourblind-safe choice.
Colour stays in the pane segment, which has its own control to tint.

There is no "off" value. An indicator the user can silence is not a safety surface;
the way to have no indicator is to turn agent access off.

## Deliberately out of scope

- **Reads stay out of the activity journal.** The journal is a bounded record of
  acting attempts; read volume would drown it. Attention state is ephemeral UI, not an
  audit trail.
- **No journal filtering by pane** — the journal is capped at 200 entries and small
  enough to scan.
- **No badge counts.** Several agent-touched panes in one background tab is a narrow
  case, and a count does not change the response (go look).
- **No pane-level override of the two-tier behaviour.** Suppressing reads on the pane
  you are looking at would remove the feature's main value.

## Testing

Pure logic, `[Fact]`:

- 3 s read decay from `NoteRead`.
- The in-flight `waitForEvents` counter drives the observe indicator's polling state
  and returns to zero when the poll completes, times out, or is cancelled.
- Write stickiness; the 10 s floor when the pane is already focused; clearing on focus
  after the floor.
- `Wrote` outranking `Watched`.
- `AgentIndicatorTabRollup` unknown-value fallback to `"WritesOnly"`.

Control-level, `[AvaloniaFact]`:

- The agent segment survives an SSH port-forward refresh.
- `UpdateStatusBarVisibility` ORs correctly: SSH-only, agent-only, both, neither.
- Tab rollup under both setting values.
- The marker survives an `UpdateTabVisuals` label rebuild.

`NovaTerminal.App.Tests` also runs on ubuntu, so tests must avoid `FileShare.None`
semantics and font-metric assumptions.

## Risks

- **One-time resize when act is enabled.** Local actable panes grow a 22 px bar and
  reflow once, at toggle time only. Called out here because a reflow of every pane is
  surprising if unexplained.
- **`spawnSession` has no pane to mark** when it is called — the pane does not exist
  yet. Mitigation: mark the *created* pane as `Wrote` at registration, so a pane an
  agent conjured into existence is visibly flagged as such.
- **Observe-only mode cannot say which pane is being read.** With act off, no pane
  carries a bar, so reads surface solely on the application-level observe indicator.
  Accepted: observe-only is the deliberately lower-risk mode, and the alternatives
  were a uniform mark on every pane or a permanently reserved 22 px strip.
