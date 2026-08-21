# Agent Access Pane Indicator Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A pane an AI agent can type into says so, and says when an agent read it or typed into it.

**Architecture:** A pure `AgentAttentionMachine` (sibling of the existing `AgentSessionStatusMachine`) hangs off each `AgentSessionRegistration`. `AgentHostService` pushes `NoteRead` / `NoteWrote` from its IPC handlers and `Tick` from its existing 1 s sweep; the pane subscribes to the registration and renders a segment in its existing `StatusBar`; `MainWindow` subscribes for the tab-header rollup and the window-level observe indicator.

**Tech Stack:** C# / .NET 10, Avalonia 12, xunit v3 (`[Fact]` for pure logic, `[AvaloniaFact]` for anything touching controls).

Design: [`docs/superpowers/specs/2026-08-21-agent-access-pane-indicator-design.md`](../specs/2026-08-21-agent-access-pane-indicator-design.md).

## Global Constraints

- **Build and test only through the wrappers:** `scripts/build.ps1 <args>` (PowerShell) or `scripts/build.sh <args>` (bash). A raw `dotnet build` hangs when stdout is captured. See CLAUDE.md.
- **Never run the whole solution's tests** — that is 20–30 minutes of headless Avalonia. Run the one project, with a `--filter`.
- The first build in a fresh worktree compiles the Rust natives via cargo (several minutes). Do not pass `SKIP_RUST_NATIVE_BUILD=1` for the `[AvaloniaFact]` tasks here: `NovaTerminal.App.Tests` panes need `rusty_pty.dll` in the output.
- `NovaTerminal.App.Tests` also runs on ubuntu. No `FileShare.None` locking semantics, no font-metric assumptions.
- **Thresholds, exactly:** `AgentAttentionMachine.ReadDecaySeconds = 3`, `AgentAttentionMachine.WriteFloorSeconds = 10`.
- **Setting name and values, exactly:** `AgentIndicatorTabRollup`, one of `"WritesOnly"` or `"All"`, default `"WritesOnly"`. Unrecognised values behave as `"WritesOnly"` — a typo must not make the chrome noisier than the default.
- **Do not** add `AgentIndicatorTabRollup` to `TerminalPane.ApplySettings`'s `effectiveSettings` whitelist. `MainWindow` is the only consumer, exactly like its sibling `ShellExitPolicy`.
- **Indicator colours, exactly:** baseline `#6B737F`, Watched `#4FB0D4`, Wrote `#E8A33D`.
- **The load-bearing invariant:** `StatusBar` visibility is driven only by the persistent layer (SSH forwards present, or the pane is agent-actable). The attention tiers change segment content and colour and **never** touch `IsVisible` — otherwise an agent read would resize the user's terminal.
- Only **successful** writes raise the `Wrote` tier. Denied attempts are the activity journal's job; the pane indicator reports what actually happened to that pane.

## File Structure

| File | Responsibility | Task |
|---|---|---|
| `src/NovaTerminal.App/AgentHost/AgentAttentionMachine.cs` (new) | Pure tier state machine + snapshot type | 1 |
| `src/NovaTerminal.App/AgentHost/AgentSessionRegistration.cs` | Hosts the machine; publishes actability; forwards focus | 2 |
| `src/NovaTerminal.App/AgentHost/AgentHostService.cs` | Pushes read/write/tick signals; actability; in-flight poll count | 3 |
| `src/NovaTerminal.App/Controls/TerminalPane.axaml` | `AgentStatusSegment` in the status bar | 4 |
| `src/NovaTerminal.App/Controls/TerminalPane.axaml.cs` | `UpdateStatusBarVisibility`, segment rendering, focus push | 4 |
| `src/NovaTerminal.App/Shell/TerminalSettings.cs` | `AgentIndicatorTabRollup` | 5 |
| `src/NovaTerminal.App/SettingsWindow.axaml` + `.axaml.cs` | Rollup dropdown | 5 |
| `src/NovaTerminal.App/MainWindow.axaml.cs` | Tab label marker + rollup policy | 6 |
| `src/NovaTerminal.App/MainWindow.axaml` | Window observe indicator | 7 |
| `tests/NovaTerminal.App.Tests/AgentHost/AgentAttentionMachineTests.cs` (new) | Tier rules | 1 |
| `tests/NovaTerminal.App.Tests/AgentHost/AgentAttentionRegistrationTests.cs` (new) | Registration plumbing | 2 |
| `tests/NovaTerminal.App.Tests/AgentHost/AgentHostAttentionProtocolTests.cs` (new) | Endpoint signal wiring | 3 |
| `tests/NovaTerminal.App.Tests/Controls/PaneAgentStatusBarTests.cs` (new) | Status-bar composition | 4 |
| `tests/NovaTerminal.App.Tests/Core/AgentIndicatorTabRollupTests.cs` (new) | Setting fallback + rollup policy | 5, 6 |
| `docs/mcp/security.md`, `docs/agent-host/DIRECTION.md` | Document the surface | 8 |

---

### Task 1: The attention state machine

Pure logic with an injectable clock. Nothing is wired, so behaviour does not change.

**Files:**
- Create: `src/NovaTerminal.App/AgentHost/AgentAttentionMachine.cs`
- Test: `tests/NovaTerminal.App.Tests/AgentHost/AgentAttentionMachineTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `enum NovaTerminal.AgentHost.AgentAttentionTier { Idle, Watched, Wrote }`; `readonly record struct NovaTerminal.AgentHost.AgentAttentionSnapshot(AgentAttentionTier Tier, DateTimeOffset? LastWriteUtc, string? LastWriteMethod)`; `sealed class NovaTerminal.AgentHost.AgentAttentionMachine` with `const int ReadDecaySeconds = 3`, `const int WriteFloorSeconds = 10`, ctor `(Func<DateTimeOffset>? nowProvider = null)`, methods `void NoteRead()`, `void NoteWrote(string method)`, `void NoteFocusChanged(bool isFocused)`, `void Tick()`, `AgentAttentionSnapshot Snapshot()`, and `event Action<AgentAttentionSnapshot>? Changed`.

- [ ] **Step 1: Write the failing tests**

Create `tests/NovaTerminal.App.Tests/AgentHost/AgentAttentionMachineTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using NovaTerminal.AgentHost;

namespace NovaTerminal.AppTests.AgentHost;

/// <summary>
/// Deterministic tests for the per-pane agent attention tiers
/// (docs/superpowers/specs/2026-08-21-agent-access-pane-indicator-design.md).
/// A fake clock drives every threshold; no UI, no timers, no PTY. Mirrors the
/// shape of <see cref="AgentSessionStatusMachineTests"/>.
/// </summary>
public class AgentAttentionMachineTests
{
    private sealed class FakeClock
    {
        public DateTimeOffset Now { get; private set; } = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);
        public void Advance(TimeSpan by) => Now += by;
        public Func<DateTimeOffset> Provider => () => Now;
    }

    private static (AgentAttentionMachine Machine, FakeClock Clock, List<AgentAttentionSnapshot> Changes) Make()
    {
        var clock = new FakeClock();
        var machine = new AgentAttentionMachine(clock.Provider);
        var changes = new List<AgentAttentionSnapshot>();
        machine.Changed += changes.Add;
        return (machine, clock, changes);
    }

    [Fact]
    public void Fresh_machine_is_idle()
    {
        var (machine, _, _) = Make();
        Assert.Equal(AgentAttentionTier.Idle, machine.Snapshot().Tier);
    }

    [Fact]
    public void A_read_lights_watched_and_decays_after_three_seconds()
    {
        var (machine, clock, _) = Make();

        machine.NoteRead();
        Assert.Equal(AgentAttentionTier.Watched, machine.Snapshot().Tier);

        clock.Advance(TimeSpan.FromSeconds(2));
        machine.Tick();
        Assert.Equal(AgentAttentionTier.Watched, machine.Snapshot().Tier);

        clock.Advance(TimeSpan.FromSeconds(1));
        machine.Tick();
        Assert.Equal(AgentAttentionTier.Idle, machine.Snapshot().Tier);
    }

    [Fact]
    public void A_write_outranks_a_concurrent_read()
    {
        var (machine, _, _) = Make();

        machine.NoteRead();
        machine.NoteWrote("sendInput");

        var snapshot = machine.Snapshot();
        Assert.Equal(AgentAttentionTier.Wrote, snapshot.Tier);
        Assert.Equal("sendInput", snapshot.LastWriteMethod);
    }

    [Fact]
    public void A_write_does_not_decay_on_its_own()
    {
        var (machine, clock, _) = Make();

        machine.NoteWrote("sendInput");
        clock.Advance(TimeSpan.FromMinutes(5));
        machine.Tick();

        Assert.Equal(AgentAttentionTier.Wrote, machine.Snapshot().Tier);
    }

    [Fact]
    public void Focus_clears_a_write_once_the_floor_has_elapsed()
    {
        var (machine, clock, _) = Make();

        machine.NoteWrote("sendInput");
        clock.Advance(TimeSpan.FromSeconds(10));
        machine.NoteFocusChanged(true);

        Assert.Equal(AgentAttentionTier.Idle, machine.Snapshot().Tier);
    }

    [Fact]
    public void Focus_before_the_floor_does_not_clear_a_write()
    {
        var (machine, clock, _) = Make();

        machine.NoteWrote("sendInput");
        clock.Advance(TimeSpan.FromSeconds(9));
        machine.NoteFocusChanged(true);

        Assert.Equal(AgentAttentionTier.Wrote, machine.Snapshot().Tier);
    }

    [Fact]
    public void An_already_focused_pane_clears_the_write_when_the_floor_expires()
    {
        // The case focus events cannot cover: the agent typed into the pane the
        // user was already looking at, so no focus change will ever arrive.
        var (machine, clock, _) = Make();
        machine.NoteFocusChanged(true);

        machine.NoteWrote("sendInput");
        Assert.Equal(AgentAttentionTier.Wrote, machine.Snapshot().Tier);

        clock.Advance(TimeSpan.FromSeconds(10));
        machine.Tick();
        Assert.Equal(AgentAttentionTier.Idle, machine.Snapshot().Tier);
    }

    [Fact]
    public void An_unfocused_pane_holds_the_write_past_the_floor()
    {
        var (machine, clock, _) = Make();
        machine.NoteFocusChanged(false);

        machine.NoteWrote("sendInput");
        clock.Advance(TimeSpan.FromMinutes(2));
        machine.Tick();

        Assert.Equal(AgentAttentionTier.Wrote, machine.Snapshot().Tier);
    }

    [Fact]
    public void A_second_write_re_arms_a_cleared_one()
    {
        var (machine, clock, _) = Make();

        machine.NoteWrote("sendInput");
        clock.Advance(TimeSpan.FromSeconds(10));
        machine.NoteFocusChanged(true);
        Assert.Equal(AgentAttentionTier.Idle, machine.Snapshot().Tier);

        machine.NoteWrote("closeSession");
        var snapshot = machine.Snapshot();
        Assert.Equal(AgentAttentionTier.Wrote, snapshot.Tier);
        Assert.Equal("closeSession", snapshot.LastWriteMethod);
    }

    [Fact]
    public void Changed_fires_only_on_tier_transitions()
    {
        var (machine, _, changes) = Make();

        machine.NoteRead();          // Idle -> Watched
        machine.NoteRead();          // Watched -> Watched, no event
        machine.NoteWrote("sendInput"); // Watched -> Wrote

        Assert.Equal(2, changes.Count);
        Assert.Equal(AgentAttentionTier.Watched, changes[0].Tier);
        Assert.Equal(AgentAttentionTier.Wrote, changes[1].Tier);
    }

    [Fact]
    public void Last_write_timestamp_survives_acknowledgement()
    {
        // The pane still wants to render "agent typed - 12s ago" text after the
        // tier itself has gone quiet, so the timestamp must not be erased.
        var (machine, clock, _) = Make();
        var writeAt = clock.Now;

        machine.NoteWrote("sendInput");
        clock.Advance(TimeSpan.FromSeconds(10));
        machine.NoteFocusChanged(true);

        var snapshot = machine.Snapshot();
        Assert.Equal(AgentAttentionTier.Idle, snapshot.Tier);
        Assert.Equal(writeAt, snapshot.LastWriteUtc);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
scripts/build.sh test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~AgentAttentionMachineTests"
```

Expected: compile failure — `AgentAttentionMachine` does not exist.

- [ ] **Step 3: Write the implementation**

Create `src/NovaTerminal.App/AgentHost/AgentAttentionMachine.cs`:

```csharp
using System;

namespace NovaTerminal.AgentHost
{
    /// <summary>How much agent attention a pane is currently getting.</summary>
    public enum AgentAttentionTier
    {
        /// <summary>Nothing recent.</summary>
        Idle,

        /// <summary>An agent read this pane within the last <see cref="AgentAttentionMachine.ReadDecaySeconds"/> seconds.</summary>
        Watched,

        /// <summary>An agent typed into, opened, or closed this pane, and the user has not acknowledged it yet.</summary>
        Wrote,
    }

    /// <summary>Tear-free view of a pane's attention state; safe from any thread.</summary>
    public readonly record struct AgentAttentionSnapshot(
        AgentAttentionTier Tier,
        DateTimeOffset? LastWriteUtc,
        string? LastWriteMethod);

    /// <summary>
    /// Per-pane agent attention state machine
    /// (docs/superpowers/specs/2026-08-21-agent-access-pane-indicator-design.md).
    ///
    /// Reads decay on their own; writes are sticky and are retired only once the
    /// user has plausibly seen them — the pane is focused AND at least
    /// <see cref="WriteFloorSeconds"/> have passed since the write. The floor
    /// exists because an agent can type into the pane the user is already
    /// looking at, where no focus change will ever arrive to acknowledge it;
    /// <see cref="Tick"/> retires those.
    ///
    /// Signals arrive from the endpoint's IPC thread (<see cref="NoteRead"/>,
    /// <see cref="NoteWrote"/>), its timer thread (<see cref="Tick"/>), and the
    /// UI thread (<see cref="NoteFocusChanged"/>). All state is guarded by one
    /// lock; <see cref="Snapshot"/> is safe from any thread and
    /// <see cref="Changed"/> is raised outside the lock. The clock is injectable
    /// so every threshold is deterministic in tests, matching
    /// <see cref="AgentSessionStatusMachine"/>.
    /// </summary>
    public sealed class AgentAttentionMachine
    {
        /// <summary>How long a single read keeps the pane in <see cref="AgentAttentionTier.Watched"/>.</summary>
        public const int ReadDecaySeconds = 3;

        /// <summary>Minimum time a write stays visible, even on an already-focused pane.</summary>
        public const int WriteFloorSeconds = 10;

        private readonly object _gate = new();
        private readonly Func<DateTimeOffset> _now;

        private DateTimeOffset? _lastReadAt;
        private DateTimeOffset? _lastWriteAt;
        private string? _lastWriteMethod;
        private bool _writeAcknowledged;
        private bool _isFocused;
        private AgentAttentionTier _tier = AgentAttentionTier.Idle;

        /// <summary>Raised outside the lock whenever the tier changes.</summary>
        public event Action<AgentAttentionSnapshot>? Changed;

        public AgentAttentionMachine(Func<DateTimeOffset>? nowProvider = null)
        {
            _now = nowProvider ?? (static () => DateTimeOffset.UtcNow);
        }

        /// <summary>A pane-addressed read landed (readScreen, readScrollback, getSessionStatus, captureScreen).</summary>
        public void NoteRead() => RunUnderGate(now => _lastReadAt = now);

        /// <summary>A successful write landed. <paramref name="method"/> is the protocol method name.</summary>
        public void NoteWrote(string method) => RunUnderGate(now =>
        {
            _lastWriteAt = now;
            _lastWriteMethod = method;
            _writeAcknowledged = false;
        });

        /// <summary>The owning pane gained or lost focus. Pushed from the UI thread.</summary>
        public void NoteFocusChanged(bool isFocused) => RunUnderGate(_ => _isFocused = isFocused);

        /// <summary>Periodic clock advance: decays reads and retires acknowledged writes.</summary>
        public void Tick() => RunUnderGate(_ => { });

        public AgentAttentionSnapshot Snapshot()
        {
            lock (_gate)
            {
                return MakeSnapshot(ComputeTier(_now()));
            }
        }

        private void RunUnderGate(Action<DateTimeOffset> mutate)
        {
            AgentAttentionSnapshot? changed = null;
            lock (_gate)
            {
                var now = _now();
                mutate(now);

                // Acknowledgement is evaluated on every signal, not only on focus
                // changes: a write onto an already-focused pane is retired by the
                // tick that carries it past the floor.
                if (_isFocused
                    && _lastWriteAt.HasValue
                    && !_writeAcknowledged
                    && now - _lastWriteAt.Value >= TimeSpan.FromSeconds(WriteFloorSeconds))
                {
                    _writeAcknowledged = true;
                }

                var after = ComputeTier(now);
                if (after != _tier)
                {
                    _tier = after;
                    changed = MakeSnapshot(after);
                }
            }

            if (changed.HasValue)
            {
                Changed?.Invoke(changed.Value);
            }
        }

        private AgentAttentionTier ComputeTier(DateTimeOffset now)
        {
            if (_lastWriteAt.HasValue && !_writeAcknowledged)
            {
                return AgentAttentionTier.Wrote;
            }
            if (_lastReadAt.HasValue
                && now - _lastReadAt.Value < TimeSpan.FromSeconds(ReadDecaySeconds))
            {
                return AgentAttentionTier.Watched;
            }
            return AgentAttentionTier.Idle;
        }

        private AgentAttentionSnapshot MakeSnapshot(AgentAttentionTier tier)
            => new(tier, _lastWriteAt, _lastWriteMethod);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
scripts/build.sh test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~AgentAttentionMachineTests"
```

Expected: PASS, 11 tests.

- [ ] **Step 5: Commit**

```bash
git add src/NovaTerminal.App/AgentHost/AgentAttentionMachine.cs tests/NovaTerminal.App.Tests/AgentHost/AgentAttentionMachineTests.cs
git commit -m "feat(agent-host): per-pane agent attention state machine"
```

---

### Task 2: Host the machine on the registration

Give every registered pane a machine, publish act-reachability onto the registration, and forward the pane's focus state into it. Still no UI.

**Files:**
- Modify: `src/NovaTerminal.App/AgentHost/AgentSessionRegistration.cs` (ctor around line 52; `IsActive` around line 295; `UpdateSnapshot`)
- Test: `tests/NovaTerminal.App.Tests/AgentHost/AgentAttentionRegistrationTests.cs` (create)

**Interfaces:**
- Consumes: `AgentAttentionMachine`, `AgentAttentionTier`, `AgentAttentionSnapshot` from Task 1.
- Produces: on `AgentSessionRegistration` — `AgentAttentionMachine AttentionMachine { get; }` and `bool IsAgentActable { get; internal set; }` (lock-guarded, like `TabId`).

- [ ] **Step 1: Write the failing tests**

Create `tests/NovaTerminal.App.Tests/AgentHost/AgentAttentionRegistrationTests.cs`:

```csharp
using System;
using NovaTerminal.AgentHost;
using NovaTerminal.Core;

namespace NovaTerminal.AppTests.AgentHost;

/// <summary>
/// The registration owns a pane's attention machine and its published
/// act-reachability. No endpoint, no UI.
/// </summary>
public class AgentAttentionRegistrationTests
{
    private static AgentSessionRegistration MakeRegistration()
        => new(
            paneId: Guid.NewGuid(),
            buffer: new TerminalBuffer(80, 24),
            title: "pane",
            profileName: "Terminal",
            kind: "local",
            isActive: false);

    [Fact]
    public void Registration_exposes_an_attention_machine_starting_idle()
    {
        var registration = MakeRegistration();
        Assert.Equal(AgentAttentionTier.Idle, registration.AttentionMachine.Snapshot().Tier);
    }

    [Fact]
    public void Act_reachability_defaults_to_false()
    {
        Assert.False(MakeRegistration().IsAgentActable);
    }

    [Fact]
    public void Becoming_the_active_pane_forwards_focus_to_the_machine()
    {
        var registration = MakeRegistration();
        registration.AttentionMachine.NoteWrote("sendInput");

        // Not focused: the write stays lit no matter how much time passes.
        registration.AttentionMachine.Tick();
        Assert.Equal(AgentAttentionTier.Wrote, registration.AttentionMachine.Snapshot().Tier);

        // The pane becomes active; the snapshot push must forward that.
        registration.UpdateSnapshot("pane", "Terminal", "local", isActive: true, profileId: null);

        Assert.True(registration.IsActive);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
scripts/build.sh test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~AgentAttentionRegistrationTests"
```

Expected: compile failure — `AttentionMachine` and `IsAgentActable` do not exist.

- [ ] **Step 3: Write the implementation**

In `src/NovaTerminal.App/AgentHost/AgentSessionRegistration.cs`, add the backing field beside the other gated fields (near `private Guid? _profileId;`):

```csharp
        private bool _isAgentActable;
```

In the constructor, beside `StatusMachine = new AgentSessionStatusMachine(nowProvider);`:

```csharp
            AttentionMachine = new AgentAttentionMachine(nowProvider);
            AttentionMachine.NoteFocusChanged(isActive);
```

Add the two members next to `StatusMachine`:

```csharp
        /// <summary>
        /// Per-pane agent attention tiers (read / wrote). Signals come from the
        /// endpoint on its IPC and timer threads and from the pane on the UI
        /// thread; the machine is internally locked.
        /// </summary>
        public AgentAttentionMachine AttentionMachine { get; }

        /// <summary>
        /// Whether an agent may currently act on this pane: the global act
        /// toggle plus, for SSH panes, the per-profile allowlist. Published by
        /// <see cref="AgentHostService"/> rather than derived here — the
        /// registration does not know the settings or the allowlist. Drives
        /// whether the pane shows its agent status segment at all.
        /// </summary>
        public bool IsAgentActable
        {
            get { lock (_gate) { return _isAgentActable; } }
            internal set { lock (_gate) { _isAgentActable = value; } }
        }
```

In `UpdateSnapshot`, after the existing assignment of `_isActive`, forward focus to the machine. It must be called **outside** the registration's `_gate` (the machine takes its own lock and raises `Changed`), so capture the value under the gate and push after:

```csharp
            // Focus feeds the write-acknowledgement rule. Pushed after the gate
            // is released: the machine locks internally and raises Changed.
            AttentionMachine.NoteFocusChanged(isActive);
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
scripts/build.sh test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~AgentAttentionRegistrationTests"
```

Expected: PASS, 3 tests.

- [ ] **Step 5: Commit**

```bash
git add src/NovaTerminal.App/AgentHost/AgentSessionRegistration.cs tests/NovaTerminal.App.Tests/AgentHost/AgentAttentionRegistrationTests.cs
git commit -m "feat(agent-host): host the attention machine on the session registration"
```

---

### Task 3: Endpoint wiring — signals, actability, poll count

Push signals from the real request handlers, publish actability, and count in-flight long polls. Behaviour is now observable through the registration but still not rendered.

**Files:**
- Modify: `src/NovaTerminal.App/AgentHost/AgentHostService.cs` — `ActEnabled` setter (~line 120), `SweepStatuses` (~line 430), `HandleGetSessionStatus` (~685), `HandleWaitForEventsAsync` (~701), `HandleCaptureScreen` (~780), `HandleSendInput` (~872), `HandleSpawnSessionAsync` (~945), `HandleCloseSessionAsync` (~1002), `HandleReadScreen` (~1074), `HandleReadScrollback` (~1116)
- Test: `tests/NovaTerminal.App.Tests/AgentHost/AgentHostAttentionProtocolTests.cs` (create)

**Interfaces:**
- Consumes: `AgentSessionRegistration.AttentionMachine` and `.IsAgentActable` from Task 2.
- Produces: on `AgentHostService` — `int InFlightPollCount { get; }` and `event Action? ObserveActivityChanged`; `internal void RefreshActability()`.

- [ ] **Step 1: Write the failing tests**

Create `tests/NovaTerminal.App.Tests/AgentHost/AgentHostAttentionProtocolTests.cs`. The harness
below is exactly the one `AgentHostStatusProtocolTests.cs` already uses — `HandleRequestLineAsync`
is the existing internal seam, so do **not** add a new test-only seam to the service.
`StubExecutor` already exists at
`tests/NovaTerminal.App.Tests/AgentHost/AgentHostActProtocolTests.cs:323`; move it into its own
file under `tests/NovaTerminal.App.Tests/AgentHost/` so both test classes share one copy.

```csharp
using System;
using System.IO;
using System.Threading.Tasks;
using NovaTerminal.AgentHost;
using NovaTerminal.AgentHost.Contracts;
using NovaTerminal.Core;

namespace NovaTerminal.AppTests.AgentHost;

/// <summary>
/// The endpoint pushes attention signals from the real handlers: reads mark
/// Watched, successful writes mark Wrote, denied writes mark nothing, and
/// act-reachability is republished on demand.
/// </summary>
public class AgentHostAttentionProtocolTests : IDisposable
{
    private readonly string _tempDir;

    public AgentHostAttentionProtocolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nova-agentattention-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private AgentHostService NewRunningService(AgentSessionRegistry registry, bool act)
    {
        var endpoint = OperatingSystem.IsWindows()
            ? "novaterminal-agent-attention-test-" + Guid.NewGuid().ToString("N")
            : Path.Combine(_tempDir, Guid.NewGuid().ToString("N")[..8] + ".sock");
        var service = new AgentHostService(registry, endpoint, _tempDir);
        service.ActEnabled = act;
        service.Start();
        Assert.True(service.IsRunning);
        return service;
    }

    private static AgentSessionRegistration Register(AgentSessionRegistry registry, string kind = "local", Guid? profileId = null)
    {
        var registration = new AgentSessionRegistration(
            Guid.NewGuid(), new TerminalBuffer(80, 24), "title", "Profile", kind,
            isActive: true, profileId: profileId);
        Assert.True(registry.Register(registration));
        return registration;
    }

    private static async Task<AgentHostResponse> HandleAsync(
        AgentHostService service, string method, string paramsJson = "null", long id = 1)
        => await service.HandleRequestLineAsync(
            $"{{\"v\":{AgentHostProtocol.Version},\"id\":{id},\"method\":\"{method}\",\"params\":{paramsJson}}}",
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task Read_screen_marks_the_pane_watched()
    {
        var registry = new AgentSessionRegistry();
        using var service = NewRunningService(registry, act: false);
        var registration = Register(registry);

        var response = await HandleAsync(service, "readScreen", $"{{\"paneId\":\"{registration.PaneId}\"}}");

        Assert.Null(response.Error);
        Assert.Equal(AgentAttentionTier.Watched, registration.AttentionMachine.Snapshot().Tier);
    }

    [Fact]
    public async Task Read_scrollback_marks_the_pane_watched()
    {
        var registry = new AgentSessionRegistry();
        using var service = NewRunningService(registry, act: false);
        var registration = Register(registry);

        await HandleAsync(service, "readScrollback", $"{{\"paneId\":\"{registration.PaneId}\",\"maxLines\":10}}");

        Assert.Equal(AgentAttentionTier.Watched, registration.AttentionMachine.Snapshot().Tier);
    }

    [Fact]
    public async Task Send_input_marks_the_pane_wrote_when_allowed()
    {
        var registry = new AgentSessionRegistry();
        using var service = NewRunningService(registry, act: true);
        service.SetActionExecutor(new StubExecutor());
        var registration = Register(registry);

        var response = await HandleAsync(
            service, "sendInput", $"{{\"paneId\":\"{registration.PaneId}\",\"text\":\"ls\"}}");

        Assert.Null(response.Error);
        var snapshot = registration.AttentionMachine.Snapshot();
        Assert.Equal(AgentAttentionTier.Wrote, snapshot.Tier);
        Assert.Equal("sendInput", snapshot.LastWriteMethod);
    }

    [Fact]
    public async Task A_denied_send_input_marks_nothing()
    {
        // Act is off: the journal records the denial, but the pane indicator must
        // not claim something happened to this pane, because nothing did.
        var registry = new AgentSessionRegistry();
        using var service = NewRunningService(registry, act: false);
        var registration = Register(registry);

        var response = await HandleAsync(
            service, "sendInput", $"{{\"paneId\":\"{registration.PaneId}\",\"text\":\"ls\"}}");

        Assert.NotNull(response.Error);
        Assert.Equal(AgentAttentionTier.Idle, registration.AttentionMachine.Snapshot().Tier);
    }

    [Fact]
    public void Refresh_actability_marks_a_local_pane_actable_when_act_is_on()
    {
        var registry = new AgentSessionRegistry();
        using var service = NewRunningService(registry, act: true);
        var registration = Register(registry);

        service.RefreshActability();

        Assert.True(registration.IsAgentActable);
    }

    [Fact]
    public void Refresh_actability_marks_nothing_when_act_is_off()
    {
        var registry = new AgentSessionRegistry();
        using var service = NewRunningService(registry, act: false);
        var registration = Register(registry);

        service.RefreshActability();

        Assert.False(registration.IsAgentActable);
    }

    [Fact]
    public void An_ssh_pane_is_not_actable_without_an_allowlist_probe()
    {
        var registry = new AgentSessionRegistry();
        using var service = NewRunningService(registry, act: true);
        var registration = Register(registry, kind: "ssh", profileId: Guid.NewGuid());

        service.RefreshActability();

        Assert.False(registration.IsAgentActable);
    }

    [Fact]
    public void An_allowlisted_ssh_pane_is_actable()
    {
        var registry = new AgentSessionRegistry();
        using var service = NewRunningService(registry, act: true);
        var profileId = Guid.NewGuid();
        var registration = Register(registry, kind: "ssh", profileId: profileId);
        service.SetSshProfileAllowlist(id => id == profileId);

        service.RefreshActability();

        Assert.True(registration.IsAgentActable);
    }

    [Fact]
    public async Task The_in_flight_poll_count_returns_to_zero()
    {
        var registry = new AgentSessionRegistry();
        using var service = NewRunningService(registry, act: false);
        Assert.Equal(0, service.InFlightPollCount);

        await HandleAsync(service, "waitForEvents", "{\"sinceSeq\":0,\"timeoutMs\":300}");

        Assert.Equal(0, service.InFlightPollCount);
    }

    [Fact]
    public async Task Observe_activity_fires_when_a_poll_parks_and_returns()
    {
        // The zero -> non-zero -> zero transition is what the window indicator
        // subscribes to; observing the mid-flight count from the awaiting thread
        // would be racy, so assert on the event instead.
        var registry = new AgentSessionRegistry();
        using var service = NewRunningService(registry, act: false);
        int transitions = 0;
        service.ObserveActivityChanged += () => Interlocked.Increment(ref transitions);

        await HandleAsync(service, "waitForEvents", "{\"sinceSeq\":0,\"timeoutMs\":300}");

        Assert.Equal(2, Volatile.Read(ref transitions));
    }
}
```

Add `using System.Threading;` to that file for `Interlocked` / `Volatile`.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
scripts/build.sh test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~AgentHostAttentionProtocolTests"
```

Expected: compile failure — `InFlightPollCount` and `RefreshActability` do not exist.

- [ ] **Step 3: Write the implementation**

Add to `AgentHostService`, near the other public state:

```csharp
        private int _inFlightPolls;

        /// <summary>
        /// How many <c>waitForEvents</c> long polls are parked right now. The
        /// subscription names no pane (WaitForEventsParams carries only
        /// sinceSeq/timeoutMs), so it drives the window-level observe indicator
        /// rather than any pane's tier.
        /// </summary>
        public int InFlightPollCount => Volatile.Read(ref _inFlightPolls);

        /// <summary>Raised when <see cref="InFlightPollCount"/> transitions between zero and non-zero.</summary>
        public event Action? ObserveActivityChanged;
```

Wrap the body of `HandleWaitForEventsAsync`'s wait:

```csharp
            if (Interlocked.Increment(ref _inFlightPolls) == 1)
            {
                ObserveActivityChanged?.Invoke();
            }
            try
            {
                var result = await ring.WaitSinceAsync(sinceSeq, timeout, cancellationToken).ConfigureAwait(false);
                return Ok(request.Id, JsonSerializer.SerializeToElement(result, AgentHostJsonContext.Default.WaitForEventsResult));
            }
            finally
            {
                if (Interlocked.Decrement(ref _inFlightPolls) == 0)
                {
                    ObserveActivityChanged?.Invoke();
                }
            }
```

Add the actability publisher:

```csharp
        /// <summary>
        /// Recomputes and publishes act-reachability onto every registration:
        /// the global act toggle, plus the per-profile allowlist for SSH panes.
        /// Called from the 1 s sweep and immediately whenever the act toggle
        /// flips, so the pane chrome cannot lag a permission change.
        /// </summary>
        internal void RefreshActability()
        {
            bool act = ActEnabled;
            foreach (var registration in _registry.GetRegistrations())
            {
                bool actable = act;
                if (actable && string.Equals(registration.Kind, "ssh", StringComparison.Ordinal))
                {
                    var profileId = registration.ProfileId;
                    var probe = _sshProfileAllowlist;
                    actable = profileId.HasValue && probe != null && probe(profileId.Value);
                }
                registration.IsAgentActable = actable;
            }
        }
```

In the `ActEnabled` setter, after the existing assignment, call `RefreshActability();`.

In `SweepStatuses`, tick the attention machine alongside the status machine and refresh actability once per pass:

```csharp
        private void SweepStatuses()
        {
            foreach (var registration in _registry.GetRegistrations())
            {
                try
                {
                    registration.StatusMachine.Sweep(registration.ProbeHasActiveChildProcesses());
                    registration.AttentionMachine.Tick();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AgentHost] status sweep failed for {registration.PaneId}: {ex.Message}");
                }
            }

            try
            {
                RefreshActability();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AgentHost] actability refresh failed: {ex.Message}");
            }
        }
```

Add `registration.AttentionMachine.NoteRead();` immediately after the successful `_registry.TryGet(...)` in each of `HandleReadScreen`, `HandleReadScrollback`, `HandleGetSessionStatus`, and `HandleCaptureScreen`. For `HandleCaptureScreen`, place it after the screenshot sub-toggle check passes, so a denied capture marks nothing.

For the writes, mark only the success paths:

- `HandleSendInput`: after the input is accepted and before returning the `Journaled(...)` success response, `registration.AttentionMachine.NoteWrote(AgentHostProtocol.Methods.SendInput);`
- `HandleCloseSessionAsync`: after `closed` is confirmed true, `registration.AttentionMachine.NoteWrote(AgentHostProtocol.Methods.CloseSession);` — look the registration up via `_registry.TryGet` before the pane is torn down, and tolerate it already being gone.
- `HandleSpawnSessionAsync`: the new pane does not exist when the call starts, so mark the **created** pane once it registers — after the executor returns the new paneId, `if (_registry.TryGet(newPaneId, out var spawned)) spawned.AttentionMachine.NoteWrote(AgentHostProtocol.Methods.SpawnSession);`

Add `using System.Threading;` if not already present.

- [ ] **Step 4: Run the tests to verify they pass**

```bash
scripts/build.sh test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~AgentHostAttentionProtocolTests"
```

Expected: PASS, 6 tests.

- [ ] **Step 5: Check for regressions in the existing agent-host suite**

```bash
scripts/build.sh test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~AppTests.AgentHost"
```

Expected: PASS. The signal pushes must not change any existing protocol response.

- [ ] **Step 6: Commit**

```bash
git add src/NovaTerminal.App/AgentHost/AgentHostService.cs tests/NovaTerminal.App.Tests/AgentHost/AgentHostAttentionProtocolTests.cs
git commit -m "feat(agent-host): push attention signals and act-reachability from the endpoint"
```

---

### Task 4: The pane status-bar segment

Render the tier in the pane's existing status bar, and make the visibility invariant structural.

**Files:**
- Modify: `src/NovaTerminal.App/Controls/TerminalPane.axaml:154` (add the segment)
- Modify: `src/NovaTerminal.App/Controls/TerminalPane.axaml.cs:3991` and `:4083` (route through the new visibility helper), plus the registration site at `:578`
- Test: `tests/NovaTerminal.App.Tests/Controls/PaneAgentStatusBarTests.cs` (create)

**Interfaces:**
- Consumes: `AgentSessionRegistration.AttentionMachine`, `.IsAgentActable`, `AgentAttentionSnapshot`, `AgentAttentionTier`.
- Produces: on `TerminalPane` — `internal void UpdateStatusBarVisibility()` and `internal void ApplyAgentAttention(AgentAttentionSnapshot snapshot, bool isActable)`.

- [ ] **Step 1: Write the failing tests**

Create `tests/NovaTerminal.App.Tests/Controls/PaneAgentStatusBarTests.cs`:

```csharp
using System;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using NovaTerminal.AgentHost;
using NovaTerminal.Controls;

namespace NovaTerminal.Tests.Controls;

/// <summary>
/// The agent segment shares the pane status bar with SSH port forwards.
/// Two independent features, one bar: neither may erase the other, and only
/// the persistent layer may change the bar's visibility (a visibility change
/// resizes the terminal, so an agent read must never cause one).
/// </summary>
public class PaneAgentStatusBarTests
{
    [AvaloniaFact]
    public void A_non_actable_local_pane_shows_no_status_bar()
    {
        using var pane = new TerminalPane("cmd.exe");
        pane.ApplyAgentAttention(new AgentAttentionSnapshot(AgentAttentionTier.Idle, null, null), isActable: false);

        Assert.False(GetStatusBar(pane).IsVisible);
    }

    [AvaloniaFact]
    public void An_actable_pane_shows_the_bar_with_the_baseline_segment()
    {
        using var pane = new TerminalPane("cmd.exe");
        pane.ApplyAgentAttention(new AgentAttentionSnapshot(AgentAttentionTier.Idle, null, null), isActable: true);

        Assert.True(GetStatusBar(pane).IsVisible);
        Assert.True(GetAgentSegment(pane).IsVisible);
    }

    [AvaloniaFact]
    public void Activity_does_not_change_the_bars_visibility()
    {
        using var pane = new TerminalPane("cmd.exe");
        pane.ApplyAgentAttention(new AgentAttentionSnapshot(AgentAttentionTier.Idle, null, null), isActable: false);
        Assert.False(GetStatusBar(pane).IsVisible);

        // A read arriving on a non-actable pane must not summon the bar: that
        // would take 22px from the terminal and fire a PTY resize.
        pane.ApplyAgentAttention(new AgentAttentionSnapshot(AgentAttentionTier.Watched, null, null), isActable: false);

        Assert.False(GetStatusBar(pane).IsVisible);
    }

    [AvaloniaFact]
    public void The_wrote_tier_names_the_method()
    {
        using var pane = new TerminalPane("cmd.exe");
        pane.ApplyAgentAttention(
            new AgentAttentionSnapshot(AgentAttentionTier.Wrote, DateTimeOffset.UtcNow, "sendInput"),
            isActable: true);

        Assert.Contains("typed", GetAgentSegmentText(pane), StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public void An_ssh_pane_with_forwards_shows_the_bar_without_an_agent_segment()
    {
        // SSH-only: the bar exists for port forwards, but a pane that is not
        // agent-actable must not claim it is.
        using var pane = MakeSshPaneWithForward();
        pane.ApplyAgentAttention(new AgentAttentionSnapshot(AgentAttentionTier.Idle, null, null), isActable: false);

        Assert.True(GetStatusBar(pane).IsVisible);
        Assert.False(GetAgentSegment(pane).IsVisible);
    }

    [AvaloniaFact]
    public void An_actable_ssh_pane_with_forwards_shows_both()
    {
        using var pane = MakeSshPaneWithForward();
        pane.ApplyAgentAttention(new AgentAttentionSnapshot(AgentAttentionTier.Idle, null, null), isActable: true);

        Assert.True(GetStatusBar(pane).IsVisible);
        Assert.False(string.IsNullOrEmpty(GetStatusBarLabel(pane)));
        Assert.True(GetAgentSegment(pane).IsVisible);
    }

    [AvaloniaFact]
    public void An_ssh_forward_refresh_does_not_erase_the_agent_segment()
    {
        // UpdateStatusBarUI clears StatusBarRules wholesale; the agent segment
        // lives in its own container precisely so it survives that.
        using var pane = new TerminalPane("cmd.exe");
        pane.ApplyAgentAttention(
            new AgentAttentionSnapshot(AgentAttentionTier.Wrote, DateTimeOffset.UtcNow, "sendInput"),
            isActable: true);

        pane.UpdateStatusBarVisibility();

        Assert.True(GetAgentSegment(pane).IsVisible);
        Assert.Contains("typed", GetAgentSegmentText(pane), StringComparison.OrdinalIgnoreCase);
    }

    private static Border GetStatusBar(TerminalPane pane)
        => pane.GetControl<Border>("StatusBar");

    private static StackPanel GetAgentSegment(TerminalPane pane)
        => pane.GetControl<StackPanel>("AgentStatusSegment");

    private static string GetAgentSegmentText(TerminalPane pane)
        => pane.GetControl<TextBlock>("AgentStatusText").Text ?? string.Empty;

    private static string GetStatusBarLabel(TerminalPane pane)
        => pane.GetControl<TextBlock>("StatusBarLabel").Text ?? string.Empty;

    // An SSH pane with one local forward, so the SSH half of the visibility OR
    // is exercised. Build the profile the same way the existing SSH pane tests
    // in tests/NovaTerminal.App.Tests/Ssh/ do; do not spin up a real session.
    private static TerminalPane MakeSshPaneWithForward()
    {
        var profile = new SshProfile
        {
            Id = Guid.NewGuid(),
            Name = "host",
            Type = ConnectionType.SSH,
        };
        profile.Forwards.Add(new ForwardingRule
        {
            Type = ForwardingType.Local,
            LocalAddress = "8080",
            RemoteAddress = "localhost:80",
        });
        return new TerminalPane(profile, SshDiagnosticsLevel.None);
    }
}
```

If `TerminalPane` has no `GetControl<T>` accessible from tests, use the same lookup the neighbouring pane tests use (check `PaneAssistInsertionTests.cs`) rather than widening the pane's API.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
scripts/build.sh test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~PaneAgentStatusBarTests"
```

Expected: compile failure — `ApplyAgentAttention` does not exist.

- [ ] **Step 3: Add the segment to the XAML**

In `src/NovaTerminal.App/Controls/TerminalPane.axaml`, inside the status bar's `StackPanel`, immediately after the `StatusBarRules` panel:

```xml
                    <StackPanel Name="AgentStatusSegment" Orientation="Horizontal" Spacing="5" VerticalAlignment="Center" IsVisible="False">
                        <Ellipse Name="AgentStatusDot" Width="6" Height="6" Fill="#6B737F" VerticalAlignment="Center"/>
                        <TextBlock Name="AgentStatusText" Foreground="#AAA" FontSize="10" VerticalAlignment="Center"/>
                    </StackPanel>
```

- [ ] **Step 4: Write the code-behind**

In `TerminalPane.axaml.cs`, add the fields and methods:

```csharp
        private bool _agentActable;
        private AgentHost.AgentAttentionSnapshot _agentAttention =
            new(AgentHost.AgentAttentionTier.Idle, null, null);

        /// <summary>
        /// Single owner of StatusBar visibility. Two independent features want
        /// the bar (SSH port forwards, agent access), so neither writes
        /// IsVisible directly. Only persistent conditions appear here: the bar
        /// appearing or disappearing resizes the terminal, so agent *activity*
        /// must never reach this.
        /// </summary>
        internal void UpdateStatusBarVisibility()
        {
            bool sshForwards = Profile != null && Profile.Forwards.Count > 0;
            StatusBar.IsVisible = sshForwards || _agentActable;
            AgentStatusSegment.IsVisible = _agentActable;
        }

        /// <summary>
        /// Renders the pane's agent attention tier. Called on the UI thread from
        /// the registration's Changed event and whenever act-reachability is
        /// republished.
        /// </summary>
        internal void ApplyAgentAttention(AgentHost.AgentAttentionSnapshot snapshot, bool isActable)
        {
            _agentAttention = snapshot;
            _agentActable = isActable;
            UpdateStatusBarVisibility();

            if (!_agentActable) return;

            switch (snapshot.Tier)
            {
                case AgentHost.AgentAttentionTier.Wrote:
                    AgentStatusDot.Fill = new SolidColorBrush(Color.Parse("#E8A33D"));
                    AgentStatusText.Text = "agent typed";
                    AgentStatusText.Foreground = new SolidColorBrush(Color.Parse("#F0C07A"));
                    break;
                case AgentHost.AgentAttentionTier.Watched:
                    AgentStatusDot.Fill = new SolidColorBrush(Color.Parse("#4FB0D4"));
                    AgentStatusText.Text = "agent reading";
                    AgentStatusText.Foreground = new SolidColorBrush(Color.Parse("#7FC3DC"));
                    break;
                default:
                    AgentStatusDot.Fill = new SolidColorBrush(Color.Parse("#6B737F"));
                    AgentStatusText.Text = "agent access";
                    AgentStatusText.Foreground = new SolidColorBrush(Color.Parse("#AAAAAA"));
                    break;
            }
        }
```

Replace the direct visibility writes:

- At `:3991` (`UpdateForwardingStatus`, the early return when there are no forwards): replace `StatusBar.IsVisible = false;` with `UpdateStatusBarVisibility();`
- At `:4083` (`UpdateStatusBarUI`): replace `StatusBar.IsVisible = true;` with `UpdateStatusBarVisibility();`

At the registration site (`:578`, right after `AgentSessionRegistry.Instance.Register(_agentRegistration);`), subscribe and marshal to the UI thread:

```csharp
            _agentRegistration.AttentionMachine.Changed += OnAgentAttentionChanged;
```

with:

```csharp
        // Raised on the endpoint's IPC or timer thread; Avalonia controls are
        // UI-thread only, so hop before touching the status bar.
        private void OnAgentAttentionChanged(AgentHost.AgentAttentionSnapshot snapshot)
        {
            var registration = _agentRegistration;
            if (registration == null) return;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                ApplyAgentAttention(snapshot, registration.IsAgentActable));
        }
```

Unsubscribe where the pane unregisters (`:3872`, beside `AgentSessionRegistry.Instance.Unregister(PaneId);`):

```csharp
            if (_agentRegistration != null)
            {
                _agentRegistration.AttentionMachine.Changed -= OnAgentAttentionChanged;
            }
```

- [ ] **Step 5: Run the tests to verify they pass**

```bash
scripts/build.sh test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~PaneAgentStatusBarTests"
```

Expected: PASS, 5 tests.

- [ ] **Step 6: Check the SSH status bar still behaves**

```bash
scripts/build.sh test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~Tests.Ssh"
```

Expected: PASS — the forwarding status bar is unchanged for SSH panes.

- [ ] **Step 7: Commit**

```bash
git add src/NovaTerminal.App/Controls/TerminalPane.axaml src/NovaTerminal.App/Controls/TerminalPane.axaml.cs tests/NovaTerminal.App.Tests/Controls/PaneAgentStatusBarTests.cs
git commit -m "feat(agent-host): show agent attention in the pane status bar"
```

---

### Task 5: The tab-rollup setting

The setting and its parse policy, plus the Settings UI. Nothing reads it yet.

**Files:**
- Modify: `src/NovaTerminal.App/Shell/TerminalSettings.cs:62` (add beside `ShellExitPolicy`)
- Modify: `src/NovaTerminal.App/SettingsWindow.axaml:951` (after the act toggle row)
- Modify: `src/NovaTerminal.App/SettingsWindow.axaml.cs` (~2022 load, ~2277 save)
- Modify: `src/NovaTerminal.App/MainWindow.axaml.cs` (add the policy function beside `ShouldClosePaneOnExit`)
- Test: `tests/NovaTerminal.App.Tests/Core/AgentIndicatorTabRollupTests.cs` (create)

**Interfaces:**
- Consumes: `AgentAttentionTier` from Task 1.
- Produces: `TerminalSettings.AgentIndicatorTabRollup` (string, default `"WritesOnly"`); `internal static bool NovaTerminal.MainWindow.ShouldShowTierInTabStrip(string? rollupPolicy, AgentAttentionTier tier)`.

- [ ] **Step 1: Write the failing test**

Create `tests/NovaTerminal.App.Tests/Core/AgentIndicatorTabRollupTests.cs`:

```csharp
using NovaTerminal.AgentHost;
using NovaTerminal.Shell;

namespace NovaTerminal.Tests.Core;

/// <summary>
/// Which attention tiers reach the tab strip. Pure policy — no window, no
/// pane, no Avalonia — mirroring how ShellExitPolicyTests covers its sibling.
/// </summary>
public sealed class AgentIndicatorTabRollupTests
{
    [Theory]
    // WritesOnly (the default): only a write reaches the tab strip.
    [InlineData("WritesOnly", AgentAttentionTier.Idle, false)]
    [InlineData("WritesOnly", AgentAttentionTier.Watched, false)]
    [InlineData("WritesOnly", AgentAttentionTier.Wrote, true)]
    // All: reads surface too.
    [InlineData("All", AgentAttentionTier.Idle, false)]
    [InlineData("All", AgentAttentionTier.Watched, true)]
    [InlineData("All", AgentAttentionTier.Wrote, true)]
    // Unrecognised and absent values fall back to the quieter behaviour: a
    // typo must not make the chrome noisier than the default.
    [InlineData("all", AgentAttentionTier.Watched, false)]
    [InlineData("Everything", AgentAttentionTier.Watched, false)]
    [InlineData("", AgentAttentionTier.Watched, false)]
    [InlineData(null, AgentAttentionTier.Watched, false)]
    // ...but a write still shows under any policy value.
    [InlineData("Everything", AgentAttentionTier.Wrote, true)]
    [InlineData(null, AgentAttentionTier.Wrote, true)]
    public void Rollup_policy_selects_which_tiers_reach_the_tab_strip(
        string? policy, AgentAttentionTier tier, bool expected)
    {
        Assert.Equal(expected, MainWindow.ShouldShowTierInTabStrip(policy, tier));
    }

    [Fact]
    public void Default_setting_value_is_writes_only()
    {
        Assert.Equal("WritesOnly", new TerminalSettings().AgentIndicatorTabRollup);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
scripts/build.sh test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~AgentIndicatorTabRollupTests"
```

Expected: compile failure — `ShouldShowTierInTabStrip` and `AgentIndicatorTabRollup` do not exist.

- [ ] **Step 3: Write the implementation**

In `TerminalSettings.cs`, beside `ShellExitPolicy`:

```csharp
        /// <summary>
        /// Which agent attention tiers reach the tab strip: "WritesOnly"
        /// (default) or "All". Unrecognised values behave as "WritesOnly" — a
        /// typo must not make the chrome noisier than the default. Read by
        /// MainWindow, which owns the tab strip; the pane never reads it, so it
        /// is deliberately absent from TerminalPane.ApplySettings.
        /// </summary>
        public string AgentIndicatorTabRollup { get; set; } = "WritesOnly";
```

In `MainWindow.axaml.cs`, beside `ShouldClosePaneOnExit`:

```csharp
        /// <summary>
        /// Whether an attention tier is loud enough for the tab strip under the
        /// given rollup policy. A write always shows: the setting only governs
        /// whether reads do.
        /// </summary>
        internal static bool ShouldShowTierInTabStrip(string? rollupPolicy, AgentHost.AgentAttentionTier tier)
        {
            if (tier == AgentHost.AgentAttentionTier.Wrote) return true;
            if (tier == AgentHost.AgentAttentionTier.Idle) return false;
            return string.Equals(rollupPolicy, "All", StringComparison.Ordinal);
        }
```

In `SettingsWindow.axaml`, after the `AgentAccessActToggle` grid, add the separator and dropdown:

```xml
                            <Border BorderBrush="{StaticResource NtHairline}" BorderThickness="0,0,0,1" Margin="0,14,0,14"/>

                            <Grid ColumnDefinitions="*,360">
                                <StackPanel Grid.Column="0" Spacing="2">
                                    <TextBlock Classes="RowLabel" Text="Tab indicator"/>
                                    <TextBlock Classes="RowDesc" Text="Which agent activity is shown on tab headers. Writes are always shown; choose All to also mark tabs whose panes are being read."/>
                                </StackPanel>
                                <ComboBox Name="AgentIndicatorTabRollupList" Grid.Column="1" HorizontalAlignment="Right" MinWidth="180">
                                    <ComboBoxItem>WritesOnly</ComboBoxItem>
                                    <ComboBoxItem>All</ComboBoxItem>
                                </ComboBox>
                            </Grid>
```

In `SettingsWindow.axaml.cs`, in the load block beside the agent toggles (~2022):

```csharp
            var agentIndicatorTabRollupList = this.FindControl<ComboBox>("AgentIndicatorTabRollupList");
            if (agentIndicatorTabRollupList != null)
            {
                foreach (ComboBoxItem item in agentIndicatorTabRollupList.Items.Cast<ComboBoxItem>())
                {
                    if (string.Equals(item.Content?.ToString(), _settings.AgentIndicatorTabRollup, StringComparison.Ordinal))
                    {
                        agentIndicatorTabRollupList.SelectedItem = item;
                    }
                }
                if (agentIndicatorTabRollupList.SelectedItem == null) agentIndicatorTabRollupList.SelectedIndex = 0;
            }
```

and in the save block beside them (~2277):

```csharp
            var agentIndicatorTabRollupList = this.FindControl<ComboBox>("AgentIndicatorTabRollupList");
            if (agentIndicatorTabRollupList?.SelectedItem is ComboBoxItem agentRollupItem)
            {
                _settings.AgentIndicatorTabRollup = agentRollupItem.Content?.ToString() ?? "WritesOnly";
            }
```

- [ ] **Step 4: Run the test to verify it passes**

```bash
scripts/build.sh test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~AgentIndicatorTabRollupTests"
```

Expected: PASS, 13 cases.

- [ ] **Step 5: Commit**

```bash
git add src/NovaTerminal.App/Shell/TerminalSettings.cs src/NovaTerminal.App/MainWindow.axaml.cs src/NovaTerminal.App/SettingsWindow.axaml src/NovaTerminal.App/SettingsWindow.axaml.cs tests/NovaTerminal.App.Tests/Core/AgentIndicatorTabRollupTests.cs
git commit -m "feat(agent-host): add the AgentIndicatorTabRollup setting"
```

---

### Task 6: The tab label rollup

Roll each tab's panes up to an indicator on its tab header.

**Follow the existing pattern here.** This codebase already has per-tab attention state and
already renders it: `TabRuntimeState` carries `HasBell` / `HasActivity`
(`MainWindow.axaml.cs:111`), and the indicators are **glyph suffixes appended to the tab label
text** in `BuildTabDisplayLabels` (`MainWindow.axaml.cs:924`: `label += " BELL";`), mirrored as
prefixes in `GetTabMenuLabel` (`:612`) and words in the automation label (`:3142`).
`UpdateTabVisuals` rewrites every label on each pass, so this needs no new visual element, no
header restructuring, and no special handling for renames.

Consequence worth knowing: the label is a single `TextBlock` with one `Foreground` set by
`UpdateTabVisuals`, so the tab rollup distinguishes the tiers by **glyph**, not colour — which
is also the colourblind-safe choice. Colour stays in the pane segment, where it has its own
control.

**Files:**
- Modify: `src/NovaTerminal.App/MainWindow.axaml.cs:111` (`TabRuntimeState`), `:612`
  (`GetTabMenuLabel`), `:924` (`BuildTabDisplayLabels`), `:3142` (automation label), `:4125`
  (`UpdateTabVisuals`)
- Test: `tests/NovaTerminal.App.Tests/Core/AgentIndicatorTabRollupTests.cs` (extend)

**Interfaces:**
- Consumes: `MainWindow.ShouldShowTierInTabStrip` (Task 5), `AgentSessionRegistry.Instance`,
  `AgentSessionRegistration.AttentionMachine` / `.TabId`.
- Produces: on `MainWindow` — `internal void RefreshTabAgentAttention()`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/NovaTerminal.App.Tests/Core/AgentIndicatorTabRollupTests.cs`. Use
`TestMainWindowFactory.Create()` — the existing way MainWindow is built in tests
(`tests/NovaTerminal.App.Tests/Core/TestMainWindowFactory.cs`). Add
`using Avalonia.Controls; using Avalonia.Headless.XUnit; using System.Linq; using NovaTerminal.AgentHost;`.

```csharp
    [AvaloniaFact]
    public void A_write_marks_the_owning_tabs_label()
    {
        using var window = TestMainWindowFactory.Create();
        window.Show();

        var registration = SoleRegistration();
        registration.AttentionMachine.NoteWrote("sendInput");
        window.RefreshTabAgentAttention();

        Assert.Contains(MainWindow.AgentWroteGlyph, FirstTabLabel(window));
    }

    [AvaloniaFact]
    public void A_read_does_not_mark_the_tab_under_the_default_policy()
    {
        using var window = TestMainWindowFactory.Create();
        window.Show();

        var registration = SoleRegistration();
        registration.AttentionMachine.NoteRead();
        window.RefreshTabAgentAttention();

        var label = FirstTabLabel(window);
        Assert.DoesNotContain(MainWindow.AgentWatchedGlyph, label);
        Assert.DoesNotContain(MainWindow.AgentWroteGlyph, label);
    }

    [AvaloniaFact]
    public void A_read_marks_the_tab_under_the_All_policy()
    {
        using var window = TestMainWindowFactory.Create();
        window.Show();
        SetRollupPolicy(window, "All");

        var registration = SoleRegistration();
        registration.AttentionMachine.NoteRead();
        window.RefreshTabAgentAttention();

        Assert.Contains(MainWindow.AgentWatchedGlyph, FirstTabLabel(window));
    }

    [AvaloniaFact]
    public void Updating_tab_visuals_preserves_the_marker()
    {
        // UpdateTabVisuals rewrites every label from scratch, so the marker has
        // to come from tab state, not be patched onto the label after the fact.
        using var window = TestMainWindowFactory.Create();
        window.Show();

        var registration = SoleRegistration();
        registration.AttentionMachine.NoteWrote("sendInput");
        window.RefreshTabAgentAttention();

        window.UpdateTabVisuals();

        Assert.Contains(MainWindow.AgentWroteGlyph, FirstTabLabel(window));
    }

    private static AgentSessionRegistration SoleRegistration()
        => Assert.Single(AgentSessionRegistry.Instance.GetRegistrations());

    private static string FirstTabLabel(MainWindow window)
    {
        var tabs = window.FindControl<TabControl>("Tabs")!;
        var tab = tabs.Items.Cast<TabItem>().First();
        var host = (Border)tab.Header!;
        return ((TextBlock)host.Child!).Text ?? string.Empty;
    }

    private static void SetRollupPolicy(MainWindow window, string policy)
    {
        var settings = (TerminalSettings)typeof(MainWindow)
            .GetField("_settings", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(window)!;
        settings.AgentIndicatorTabRollup = policy;
    }
```

`SoleRegistration` assumes the freshly built window owns exactly one pane, and that
`AgentSessionRegistry.Instance` is process-wide — so these cases must not run in parallel with
other tests that register panes. If the existing MainWindow tests already carry a collection
attribute for that reason, put this class in the same collection.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
scripts/build.sh test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~AgentIndicatorTabRollupTests"
```

Expected: compile failure — `RefreshTabAgentAttention`, `AgentWroteGlyph`, `AgentWatchedGlyph`
do not exist.

- [ ] **Step 3: Write the implementation**

Add the glyph constants beside the other MainWindow constants (near `BellDebounceWindow`, `:81`):

```csharp
        /// <summary>Tab-label marker for "an agent typed into a pane in this tab".</summary>
        internal const string AgentWroteGlyph = "\u2328";  // keyboard

        /// <summary>Tab-label marker for "an agent is reading a pane in this tab".</summary>
        internal const string AgentWatchedGlyph = "\U0001F441";  // eye
```

Add the state to `TabRuntimeState` (`:111`):

```csharp
            public AgentHost.AgentAttentionTier AgentTier { get; set; }
```

Add the rollup, beside the other tab-state helpers:

```csharp
        /// <summary>
        /// Recomputes each tab's agent marker from the loudest attention tier
        /// among its panes, filtered by the rollup setting, then refreshes the
        /// labels. Tiers are stored on tab state rather than patched onto labels
        /// because UpdateTabVisuals rebuilds every label from scratch.
        /// </summary>
        internal void RefreshTabAgentAttention()
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null) return;

            var loudestByTab = new Dictionary<Guid, AgentHost.AgentAttentionTier>();
            foreach (var registration in AgentHost.AgentSessionRegistry.Instance.GetRegistrations())
            {
                var tabId = registration.TabId;
                if (!tabId.HasValue) continue;
                var tier = registration.AttentionMachine.Snapshot().Tier;
                if (!loudestByTab.TryGetValue(tabId.Value, out var current) || tier > current)
                {
                    loudestByTab[tabId.Value] = tier;
                }
            }

            foreach (TabItem tab in tabs.Items.Cast<TabItem>())
            {
                var state = GetOrCreateTabState(tab);
                var tier = loudestByTab.TryGetValue(GetPersistentTabId(tab), out var found)
                    ? found
                    : AgentHost.AgentAttentionTier.Idle;

                state.AgentTier = ShouldShowTierInTabStrip(_settings.AgentIndicatorTabRollup, tier)
                    ? tier
                    : AgentHost.AgentAttentionTier.Idle;
            }

            UpdateTabVisuals();
            RefreshAgentObserveIndicator();
        }
```

`AgentAttentionTier` is declared `Idle, Watched, Wrote`, so `tier > current` orders the tiers
correctly. Keep that declaration order.

In `BuildTabDisplayLabels`, immediately after the existing bell / activity block (`:924`):

```csharp
            if (state.AgentTier == AgentHost.AgentAttentionTier.Wrote)
            {
                label += " " + AgentWroteGlyph;
            }
            else if (state.AgentTier == AgentHost.AgentAttentionTier.Watched)
            {
                label += " " + AgentWatchedGlyph;
            }
```

In `GetTabMenuLabel` (`:612`), extend the icon prefix the same way:

```csharp
            if (state.AgentTier == AgentHost.AgentAttentionTier.Wrote) icon += AgentWroteGlyph + " ";
            else if (state.AgentTier == AgentHost.AgentAttentionTier.Watched) icon += AgentWatchedGlyph + " ";
```

In the automation label at `:3142`, extend the existing `attention` string so screen readers get
words rather than glyphs:

```csharp
                string agent = state.AgentTier == AgentHost.AgentAttentionTier.Wrote
                    ? " agent-typed"
                    : state.AgentTier == AgentHost.AgentAttentionTier.Watched ? " agent-reading" : string.Empty;
```

and append `agent` where `attention` is already appended.

Subscribe so the labels refresh when attention changes. In the constructor:

```csharp
            AgentHost.AgentSessionRegistry.Instance.SessionRegistered += OnAgentSessionRegisteredForAttention;
            AgentHost.AgentSessionRegistry.Instance.SessionUnregistered += OnAgentSessionUnregisteredForAttention;
            foreach (var registration in AgentHost.AgentSessionRegistry.Instance.GetRegistrations())
            {
                registration.AttentionMachine.Changed += OnAgentAttentionChangedForTabs;
            }
```

```csharp
        private void OnAgentSessionRegisteredForAttention(AgentHost.AgentSessionRegistration registration)
            => registration.AttentionMachine.Changed += OnAgentAttentionChangedForTabs;

        private void OnAgentSessionUnregisteredForAttention(AgentHost.AgentSessionRegistration registration)
        {
            registration.AttentionMachine.Changed -= OnAgentAttentionChangedForTabs;
            Avalonia.Threading.Dispatcher.UIThread.Post(RefreshTabAgentAttention);
        }

        // Raised on the endpoint's IPC or timer thread; hop before touching tabs.
        private void OnAgentAttentionChangedForTabs(AgentHost.AgentAttentionSnapshot _)
            => Avalonia.Threading.Dispatcher.UIThread.Post(RefreshTabAgentAttention);
```

Unsubscribe both registry handlers in the window's close/dispose path, next to where the other
registry and pane handlers are detached.

**Note:** `RefreshTabAgentAttention` calls `RefreshAgentObserveIndicator`, which Task 7 adds. Do
Task 7 before running the full suite, or stub the call and fill it in there.

- [ ] **Step 4: Run the tests to verify they pass**

```bash
scripts/build.sh test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~AgentIndicatorTabRollupTests"
```

Expected: PASS.

- [ ] **Step 5: Check the tab suite for regressions**

```bash
scripts/build.sh test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~TabSystemTests|FullyQualifiedName~MainWindowTabLookupTests"
```

Expected: PASS. Labels gain a suffix only when an agent tier is set, so bell / activity /
forwarding markers must be untouched.

- [ ] **Step 6: Commit**

```bash
git add src/NovaTerminal.App/MainWindow.axaml.cs tests/NovaTerminal.App.Tests/Core/AgentIndicatorTabRollupTests.cs
git commit -m "feat(agent-host): roll pane agent attention up to tab labels"
```

---

### Task 7: The window observe indicator

One app-level light for "agent access is on", picking up the polling and observe-only read states.

**Files:**
- Modify: `src/NovaTerminal.App/MainWindow.axaml:150` (after `TabOverflowBadge`)
- Modify: `src/NovaTerminal.App/MainWindow.axaml.cs:2004` and `:5283` (the two settings-apply sites)
- Test: `tests/NovaTerminal.App.Tests/Core/AgentObserveIndicatorTests.cs` (create)

**Interfaces:**
- Consumes: `AgentHostService.Instance.InFlightPollCount`, `.ObserveActivityChanged` (Task 3); `AgentSessionRegistry`.
- Produces: on `MainWindow` — `internal void RefreshAgentObserveIndicator()`.

- [ ] **Step 1: Write the failing tests**

Create `tests/NovaTerminal.App.Tests/Core/AgentObserveIndicatorTests.cs`:

```csharp
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using NovaTerminal.AgentHost;

namespace NovaTerminal.Tests.Core;

/// <summary>
/// The window-level observe light. It is a permission indicator first: visible
/// exactly while observe is enabled, regardless of any pane's activity.
/// </summary>
public class AgentObserveIndicatorTests
{
    [AvaloniaFact]
    public void Hidden_when_observe_is_disabled()
    {
        using var window = new MainWindow();
        window.Show();

        AgentHostService.Instance.Apply(false);
        window.RefreshAgentObserveIndicator();

        Assert.False(GetIndicator(window).IsVisible);
    }

    [AvaloniaFact]
    public void Visible_when_observe_is_enabled()
    {
        using var window = new MainWindow();
        window.Show();

        AgentHostService.Instance.Apply(true);
        window.RefreshAgentObserveIndicator();

        Assert.True(GetIndicator(window).IsVisible);

        AgentHostService.Instance.Apply(false); // leave global state as found
    }

    private static Button GetIndicator(MainWindow window)
        => window.GetControl<Button>("AgentObserveIndicator");
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
scripts/build.sh test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~AgentObserveIndicatorTests"
```

Expected: compile failure — `RefreshAgentObserveIndicator` does not exist.

- [ ] **Step 3: Add the control**

In `MainWindow.axaml`, immediately after the `TabOverflowBadge` `TextBlock`:

```xml
                <Button Name="AgentObserveIndicator"
                        IsVisible="False"
                        Background="Transparent"
                        BorderThickness="0"
                        Padding="6,0"
                        Focusable="False"
                        VerticalAlignment="Center"
                        ToolTip.Tip="Agent access is enabled. Click to open the agent activity journal.">
                    <Ellipse Name="AgentObserveIndicatorDot" Width="7" Height="7" Fill="#6B737F"/>
                </Button>
```

- [ ] **Step 4: Write the code-behind**

```csharp
        /// <summary>
        /// The application-level agent light. Visible exactly while observe is
        /// enabled. It also carries two activity states, because it is the only
        /// surface at the right scope for them: an in-flight waitForEvents long
        /// poll names no pane, and in observe-only mode no pane carries a status
        /// bar, so reads would otherwise be invisible everywhere.
        /// </summary>
        internal void RefreshAgentObserveIndicator()
        {
            var indicator = this.FindControl<Button>("AgentObserveIndicator");
            var dot = this.FindControl<Avalonia.Controls.Shapes.Ellipse>("AgentObserveIndicatorDot");
            if (indicator == null || dot == null) return;

            bool observing = AgentHost.AgentHostService.Instance.IsRunning;
            indicator.IsVisible = observing;
            if (!observing) return;

            bool polling = AgentHost.AgentHostService.Instance.InFlightPollCount > 0;
            bool readingSomewhere = !_settings.AgentAccessActEnabled
                && AgentHost.AgentSessionRegistry.Instance.GetRegistrations()
                    .Any(r => r.AttentionMachine.Snapshot().Tier == AgentHost.AgentAttentionTier.Watched);

            dot.Fill = new SolidColorBrush(Color.Parse(
                polling || readingSomewhere ? "#4FB0D4" : "#6B737F"));
        }
```

Wire the click to the existing journal window, in `SetupUI` beside the other button wiring:

```csharp
            var agentObserveIndicator = this.FindControl<Button>("AgentObserveIndicator");
            if (agentObserveIndicator != null)
            {
                agentObserveIndicator.Click += async (_, _) => await ShowAgentActivityJournalAsync();
            }
```

Subscribe to poll transitions in the constructor:

```csharp
            AgentHost.AgentHostService.Instance.ObserveActivityChanged += OnAgentObserveActivityChanged;
```

```csharp
        // Raised on an IPC thread when the in-flight poll count leaves or
        // returns to zero.
        private void OnAgentObserveActivityChanged()
            => Avalonia.Threading.Dispatcher.UIThread.Post(RefreshAgentObserveIndicator);
```

Call `RefreshAgentObserveIndicator();` at both settings-apply sites — after `AgentHostService.Instance.Apply(_settings.AgentAccessObserveEnabled);` at `:2004` and at `:5283` — It is already called at the end of `RefreshTabAgentAttention` (Task 6), so a read that moves a pane's tier also refreshes the observe-only state.

**Note:** `SetupCommandPalette` runs lazily (on palette-open / settings-save), not at startup, so do **not** hang this wiring off it — put it where the other startup UI wiring lives.

- [ ] **Step 5: Run the tests to verify they pass**

```bash
scripts/build.sh test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~AgentObserveIndicatorTests"
```

Expected: PASS, 2 tests.

- [ ] **Step 6: Commit**

```bash
git add src/NovaTerminal.App/MainWindow.axaml src/NovaTerminal.App/MainWindow.axaml.cs tests/NovaTerminal.App.Tests/Core/AgentObserveIndicatorTests.cs
git commit -m "feat(agent-host): window-level agent observe indicator"
```

---

### Task 8: Document the surface

The security doc currently tells users the only visibility surface is the journal. That is now wrong.

**Files:**
- Modify: `docs/mcp/security.md`
- Modify: `docs/agent-host/DIRECTION.md` (permission table)

- [ ] **Step 1: Update the security doc**

In `docs/mcp/security.md`, after the "Act" bullet in the live-session tools section, add:

```markdown
- **Visibility while it happens.** Panes an agent may act on carry an agent segment in
  their status bar; it turns amber and names the action when an agent types into, opens,
  or closes that pane, and stays lit until you have looked at the pane. Tab headers carry
  a badge for the same event (reads too, if **Settings → Agent Access → Tab indicator**
  is set to `All`), and a window-level light shows whenever agent access is enabled. The
  activity journal remains the retrospective record; these are the live ones. There is
  no way to silence them — the way to have no indicator is to turn agent access off.
```

- [ ] **Step 2: Update the permission table**

In `docs/agent-host/DIRECTION.md`, add this row to the permission table (the one whose header is
`| Capability | Default | Gate |`), after the notifications row:

```markdown
| live indicators (pane status segment, tab marker, window light) | on with the toggle | none of their own — they follow observe / act and cannot be silenced separately |
```

- [ ] **Step 3: Verify the docs build path is unaffected**

The MCP server serves files under `docs/` through `RepoContext`; both edited files are
already inside `docs/`, so no allowlist change is needed. Confirm nothing else asserts on
their content:

```bash
rtk grep -n "mcp/security.md" tests src
```

Expected: no test asserts on the prose.

- [ ] **Step 4: Commit**

```bash
git add docs/mcp/security.md docs/agent-host/DIRECTION.md
git commit -m "docs(agent-host): document the live agent-access indicators"
```

---

## Verification before calling this done

- [ ] `scripts/build.sh build src/NovaTerminal.App` succeeds.
- [ ] `scripts/build.sh test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~AppTests.AgentHost"` passes.
- [ ] `scripts/build.sh test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~Tests.Core"` passes.
- [ ] `scripts/build.sh test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~Tests.Controls"` passes.
- [ ] Manual smoke test (GUI automation is unreliable here — do these by hand):
  1. Launch the app. Confirm no agent light in the chrome and no pane status bar.
  2. Settings → Agent Access → enable observe. Confirm the window light appears; panes still show no bar.
  3. Enable act. Confirm every local pane grows a status bar reading "agent access" — once, with a single reflow.
  4. From an MCP client, call `read_screen` on a pane. Confirm that pane's segment turns blue-ish and reads "agent reading", then goes quiet after ~3 s, and that no tab marker appears.
  5. Call `send_input` on a pane in a **background** tab. Confirm that tab's label gains the keyboard marker, and the pane's segment reads "agent typed" and is still lit when you switch to it, clearing ~10 s later.
  6. Set Tab indicator to `All`, repeat step 4, and confirm the tab label now gains the eye marker for the read.
