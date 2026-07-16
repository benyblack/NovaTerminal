# NovaTerminal — Deep Code Review (2026-07-05)

Scope: full `main` working tree at `D:\projects\nova2` — all `src/` projects, both Rust crates, all test projects, CI workflows, build system, docs, and repo hygiene. All findings were verified by reading the code; line numbers reference the tree as of the review (pre-remediation). No builds or tests were executed during the review itself.

---

## Remediation log (2026-07-05, same day)

All eight high-severity findings were remediated the same day. Each fix went through: GitHub issue → PR → Codex review (`@codex review`) → review findings addressed → squash merge to `main`.

| High # | Finding | Issue | Resolution |
|---|---|---|---|
| 1 | ED 2 erases scrollback | #149 | ✅ Merged — PR #157. Split `Clear()` into `ClearScreen()`/`ClearScrollbackHistory()`; ED 2 → screen only, ED 3 → scrollback only. Review rounds additionally introduced `TerminalImage.IsAltScreenImage` and made every image mutation (scroll, IL/DL, DCH, eviction, resize/reflow, render snapshot) screen-ownership-aware; ED 2 no longer resets SGR attributes. 9 new VT tests. |
| 2 | Zero-length regex crash in search | #150 | ✅ Merged — PR #158. Skip zero-length matches. 7 new tests. |
| 3 | Vault fallback crypto (Linux/macOS) | #151 | ➡️ Closed as duplicate of pre-existing #100; open **PR #132** (platform keychain backends) already implements the fix and awaits completion. |
| 4 | ANSI vs UTF-8 P/Invoke marshalling | #152 | ✅ Merged — PR #160. `LPUTF8Str` on all `rusty_pty` string parameters. |
| 5 | `LC_ALL=C` locale clobber | #153 | ✅ Merged — PR #159. Inherit user locale; fallback `LANG=C.UTF-8` (glibc) / `LC_CTYPE=UTF-8` (Darwin) only when no locale is set. |
| 6 | Pane disposal orphans PTY | #154 | ✅ Merged — PR #161. Two-phase teardown: `DetachFromUiThread()` (UI thread, `VerifyAccess`-guarded) + background session dispose with real logging. |
| 7 | SSH close hang / no connect timeout | #155 | ✅ Merged — PR #162. Establishment races `SharedState::wait_closed()` (tokio Notify); raw TCP connects (incl. jump-hop `direct-tcpip`) bounded by 30 s. Handshake/auth deliberately untimed (user prompts) — close unblocks them. 3 new Rust tests. |
| 8 | CI gating gaps | #156 | ✅ Merged — PR #163. `release.yml` now runs the gating unit lane on win/linux/macos before publishing (first automated macOS coverage); `vt-conformance.yml` triggers on `src/NovaTerminal.VT/**`; CONTRIBUTING.md describes actual CI enforcement. Deferred (maintainer calls): re-gating App.Tests after the #81 quarantine; replay/parity back on PRs. |

Test deltas: VT.Tests 55 → 64; rusty_ssh crate tests 33 → 36.

**Everything below this line is the original review.** The Medium/Low sections remain open backlog, except where superseded by the fixes above (e.g. the ED 2 items). Line numbers in the High findings may have shifted on `main`.

Medium findings filed as issues (same day): #164 row metadata lifecycle, #165 write-path allocations, #166 ImageRegistry dispose race, #167 settings atomicity/preview, #168 PTY decoder edge + partial writes, #169 parser correctness batch, #170 cmd/scp quoting, #171 workspace bundle command execution, #172 GlyphCache skew/clipping/emoji, #173 SSH output-path throughput, #174 global.json/.editorconfig. Already tracked elsewhere: MainWindow decomposition (#110), event cleanup (#102), warning debt (#108), logging (#109), vault (#100/PR #132), SSH hardening (#121), App.Tests re-gating (#117/#81). Closed as fixed: #101 (by PR #161); #120's locale item fixed by PR #159.

---

**Inventory:** ~10 source projects. VT engine ~9,000 LOC / 40 files; App ~31,300 LOC / ~156 files (largest: `MainWindow.axaml.cs` 5,338 LOC); Platform 47 files (largest: `NativeSshInterop.cs` 1,001 LOC); Rust: `rusty_pty` 794 lines, `rusty_ssh` 3,604 lines. Tests: 1,068 test methods across 195 files in 8 projects. Docs: 161 markdown files.

---

## Overall assessment

This is a genuinely well-engineered codebase with an unusually strong correctness culture: architecture invariants are *enforced* (NetArchTest at IL + csproj + namespace level), fuzzing is real (nightly libFuzzer with corpus persistence, plus per-commit deterministic fuzz smoke asserting buffer invariants), replay/parity snapshot infrastructure exists with a cross-OS comparator, FFI lifetime discipline is careful (SafeHandles, `catch_unwind` guards, FFI layout contract tests), and comments cite regression numbers at the exact code that fixes them. TODO/HACK density is near zero.

The debt is concentrated and identifiable: a handful of user-visible correctness bugs in the VT layer, one real security weakness in the non-Windows credential vault, a CI configuration where **~76% of tests don't gate merges**, and two god-class files in the App layer that predate the (much better) newer MVVM code.

---

## Critical / High findings

### 1. `ESC[2J` erases the entire scrollback (VT correctness, user-facing)
`src/NovaTerminal.VT/AnsiParser.cs:803-807` routes ED mode 2 **and** 3 to `_buffer.Clear()`, and `TerminalBuffer.Maintenance.cs` clears `_scrollback` there. Per xterm and every mainstream terminal, ED 2 clears the screen only; only ED 3 touches scrollback. `clear`, ConPTY `cls`, and many TUI init sequences emit ED 2 — users silently lose their history constantly. One-line behavioral fix with the biggest user impact in this review.

### 2. Zero-length regex matches crash search
`src/NovaTerminal.VT/TerminalBuffer.AccessAndSnapshot.cs:730-737` — a user search pattern like `a*` yields `m.Length == 0`, so `colMapping[m.Index + m.Length - 1]` indexes `-1` → exception thrown while holding the read lock. Trivial guard: skip zero-length matches and clamp the end index.

### 3. Vault fallback crypto (Linux/macOS) is decryptable by any local process
`src/NovaTerminal.App/Shell/VaultService.cs:418-472`. Comments claim AES-256-GCM; the implementation is default-mode AES-**CBC** with no auth tag, keyed by PBKDF2 over `/etc/machine-id` (world-readable) on Linux or literally `$USER` on macOS, with a hardcoded salt and 10k iterations. Any local user who can read `vault.dat` can recover all stored SSH passwords. Windows correctly uses DPAPI/Credential Manager; profiles/settings JSON correctly never contain secrets. Fix: libsecret/Keychain backends, or at minimum AES-GCM with a per-user random key file at `0600` — and fix the misleading comments.

### 4. PTY P/Invoke marshals strings as ANSI; Rust decodes UTF-8
`src/NovaTerminal.Pty/RustPtySession.cs:50-76` — no `CharSet`/`MarshalAs` on `pty_spawn(string cmd, ...)` etc., so strings marshal as the active Windows codepage while Rust does `CStr::to_string_lossy()` (UTF-8). Non-ASCII shell paths, cwd (e.g. `C:\Users\Bähnam`), args, and env blobs get mangled. `NativeSshInterop.cs` does this correctly (`StringToCoTaskMemUTF8`). Fix: `[MarshalAs(UnmanagedType.LPUTF8Str)]` or `LibraryImport` + `StringMarshalling.Utf8`.

### 5. Rust portable PTY path forces `LC_ALL=C` / `LANG=C`
`src/NovaTerminal.App/native/src/lib.rs:402-403` — the only path on Unix. Puts every child shell in the ASCII locale: mangled filenames, broken multibyte readline input, no Unicode line drawing — contradicting the `TERM`/`COLORTERM` setup and the C# UTF-8 decoder. Delete the two lines (or inherit/`C.UTF-8`).

### 6. Pane disposal can orphan PTY sessions on tab close
`src/NovaTerminal.App/MainWindow.axaml.cs:3331-3340` — `Task.Run(() => { try { pane.Dispose(); } catch { } })`. `TerminalPane.Dispose` does UI-thread-affine work (control visibility, `DispatcherTimer.Stop`) before `session.Dispose()`; a `VerifyAccess` throw is swallowed and the PTY + child shell leak. Split teardown: UI detach on the dispatcher, session disposal off-thread, no blanket catch.

### 7. SSH close can hang Dispose / the finalizer thread indefinitely
`src/NovaTerminal.App/native/rusty_ssh/src/lib.rs:905-934, 3477-3486` — `nova_ssh_close` joins the worker with no timeout, and the worker has no connect/auth timeout (`inactivity_timeout: None`); `Close` is only consumed once the main select loop is reached. Closing a tab stuck connecting to a dead host blocks `ReleaseHandle` — potentially on the .NET finalizer thread, stalling process-wide finalization. Wrap connect/auth in `tokio::time::timeout` and/or select on a shutdown notify during the connect phase.

### 8. CI: ~76% of the test suite does not gate merges
- `NovaTerminal.App.Tests` (~815 of 1,068 methods) runs with `continue-on-error: true` (`ci.yml:276`) due to a dump-confirmed Avalonia.Headless teardown deadlock (upstream #21467, local #81). Understandable, but a red-yet-non-blocking check trains reviewers to ignore it. Consider quarantining only the deadlock-prone fixtures, or running with a hard timeout + retry, so the rest of the suite blocks again.
- `replay_tests` and `parity_compare` are skipped on PRs (`ci.yml:363, 494`) — while CONTRIBUTING.md and ROADMAP explicitly state they gate merges. Documented and enforced policy have diverged; a parity break lands on `main` before detection. Either restore the PR gate or update the docs.
- macOS is Tier-1 in ROADMAP but has zero automated coverage anywhere, and `release.yml` runs **no tests** before publishing AOT bundles (including untested `osx-arm64`).

---

## Medium findings

**VT / parser**
- **ICH/DCH/insert-mode don't shift grapheme/hyperlink side-tables** (`TerminalBuffer.WritePath.cs:556-636`): after `CSI @`/`CSI P` on lines with emoji/CJK/OSC-8 links, extended text and links attach to wrong columns; wide-pair splits at boundaries aren't handled either.
- **Per-grapheme string allocation in the hottest path** (`WritePath.cs:147-155`): every printable char goes through `StringInfo.GetTextElementEnumerator` + `GetTextElement()` (one string per grapheme) plus LINQ rune boxing. `cat`-ing a large file allocates GBs. Add a contiguous ASCII/narrow-BMP fast path.
- **Row metadata dropped on resize/reflow** (`ReflowEngine.cs:351-596`, `ResizeAndReflow.cs:102, 259-290`): hyperlinks never survive width resize; rows flowed into scrollback lose extended text; height-resize paths drop `IsWrapped` (breaking later re-joins). Related: `ScrollbackPages.TryPopLastRow` leaves stale per-row maps that a later `AppendRow` inherits, and scrollback snapshot/`GetGraphemeAbsolute` never surface stored extended text (`ThreadingAndInvalidation.cs:717` — the storage side is done; the read side isn't wired).
- **Any DCS containing `q` is treated as Sixel** (`AnsiParser.cs:1347-1356`): DECRQSS (vim uses it) and XTGETTCAP are misrouted into the Sixel decoder and get no response. Parse the DCS prefix properly.
- **C0 controls mid-CSI abort the sequence and are swallowed** (`AnsiParser.cs:507-512`): spec says execute the control and continue; abort only on CAN/SUB. Split sequences from ConPTY/tmux lose both the control and the SGR.
- **`DetectConPtyFiltering` always returns `true` on Windows** (`AnsiParser.cs:90-106`) — the env checks above it are dead code; non-tunneled Kitty graphics are permanently answered `ERR` on Windows.
- **Buffer events raised under the write lock** (`AccessAndSnapshot.cs:595, 642`): alt-screen switch events can deadlock or throw `LockRecursionException` in subscribers; other paths already do capture-then-raise after unlock.
- `CSI n S/T` loops single-row scrolls up to 65,535 times (`AnsiParser.cs:696-707`); scrollback page lookups are linear per row during reflow (~millions of hops on a large-history resize); interpolated `TerminalLogger.Log($...)` strings are built even when logging is off in IL/DL and unknown-CSI hot paths.

**Rendering / PTY / Platform**
- **`ImageRegistry.GetImage` use-after-dispose race** (`Rendering/ImageRegistry.cs:19-57`): eviction disposes an `SKBitmap` the render thread may still be drawing. Every other cache here uses deferred disposal drained at the frame boundary — apply the same pattern.
- **GlyphCache**: `Skew` is in the cache key but never applied when rasterizing (synthetic italics render upright, `GlyphCache.cs:139-143`); atlas packs by advance width + font-wide ascent/descent so overhanging ink (italic *f*, Nerd Font icons, emoji) is clipped; color-glyph detection misses regional-indicator flags, keycaps, VS16-forced emoji → monochrome silhouettes.
- **PTY read loop can die silently** (`RustPtySession.cs:373-391`): stateful UTF-8 decoder can emit 4096+1 chars into a 4096 char buffer → `GetChars` throws → catch-all terminates the loop, session goes mute. Size via `GetMaxCharCount` (the SSH path already does).
- **`pty_write` result ignored** (`RustPtySession.cs:465-474`; Rust does a single `write`, not `write_all`): large pastes can silently drop bytes.
- **rusty_ssh event queue unbounded + payload copied per poll** (`lib.rs:491-521, 683-706`); port-forward channel writes run synchronously on the SSH poll loop (`NativePortForwardSession.cs:96-97`) so one stalled local consumer freezes all terminal output for the session; SSH output polling adds up to 25 ms latency with no backpressure (`NativeSshSession.cs:14, 323-401`).
- **cmd.exe quoting is wrong** (`Platform/Input/Quoters.cs:12-19`): backslash-escaped quotes aren't a cmd convention and `%VAR%` still expands inside quotes — this is the drag-and-drop path, so file names are attacker-influenceable. Similarly `SftpService.QuoteArg` (`SftpService.cs:813-817`) corrupts args for local paths ending in `\`.
- **SixelDecoder**: unguarded `int.Parse` on remote-controlled data (`#1;;2;...` throws) and per-pixel `SetPixel` rendering (`SixelDecoder.cs:77-80, 192-215`).
- Windows passthrough spawn leaks pipe handles on error paths and never deletes the proc-thread attribute list on success (`lib.rs:44-174`); `pty_cancel_read` can sleep-spin ~1 s during dispose (`lib.rs:632-653`); `HasChildProcesses` shells out to `pgrep` synchronously on every read (`RustPtySession.cs:194-213`); PowerShell init writes a temp `.ps1` per tab and never deletes it, and fakes the Microsoft copyright banner (`RustPtySession.cs:288-320`).

**App layer**
- **Settings preview isn't reverted on Cancel** (`MainWindow.axaml.cs:4632-4658`; `SettingsWindow.axaml.cs:767`): live `_settings` is mutated during preview, Cancel just closes, and any later unrelated `Save()` persists the canceled values.
- **Settings/session/vault persistence is silently lossy and non-atomic** (`TerminalSettings.cs:122-130, 198-207` and same pattern in `VaultService.Save`, `SessionManager.SaveSession`): corrupt file → silent reset to defaults; bare `File.WriteAllText` → crash mid-write corrupts. Use temp-file + atomic rename (the codebase already does this correctly in `JsonSshProfileStore` and `OpenSshConfigCompiler`).
- **`MainWindow.axaml.cs` (5,338 LOC) and `SettingsWindow.axaml.cs` (1,674 LOC) are code-behind god classes** with ~60/~50 stringly-typed `FindControl` lookups and a static service locator (`MainWindow.Vault` assigned inside `try { } catch { }`). The newer subsystems (SSH VMs, CommandAssist, Shortcuts) show the target style; extract TabManager / PaneLayoutController / CommandPalette services onto the existing `AppServiceBundle` seam.
- **Workspace bundles execute stored commands on restore with no confirmation** (`SessionManager.cs:297-306`; import flows at `MainWindow.axaml.cs:1395-1469`): a foreign `.novaws.json` spawns arbitrary `Command`/`Arguments` on open. The policy gate for import exists, but show the command list (or restore with default shell) for non-locally-authored bundles.
- Blocking `InvokeAsync(...).GetAwaiter().GetResult()` on a worker thread (`MainWindow.axaml.cs:4544-4548`) is deadlock-prone; broadcast-input reimplements key encoding in parallel with the real pipeline (`:1813-1844`); quake-mode hotkey is hardcoded while `Settings.GlobalHotkey` exists and is documented (`:130-141`).
- **Conformance is validated self-reporting, not test-derived** (`VtConformanceReportTool.cs:33-47, 289-298, 407-430`): the tool lints the matrix, checks evidence paths exist, and SHA-pins the report (good process), but evidence-kind detection is substring matching (`"unit"` matches *community*) and links only need to exist, not test the feature. Also, `vt-conformance.yml` doesn't trigger on `src/NovaTerminal.VT/**` changes.

**Build / repo**
- **No `global.json`** — SDK unpinned; CI floats `10.0.x` while cache keys hardcode `10.0.300`.
- **No root `.editorconfig`** despite `EnforceCodeStyleInBuild=true` and `dotnet format --verify-no-changes` in `ci/run.*` — style enforcement is largely a no-op as configured.
- `TreatWarningsAsErrors=false` with an honestly documented ~350-warning debt including untriaged CS8602 null-derefs in `TerminalPane` — those are potential real bugs, worth a triage pass.
- `ci/run.ps1`/`run.sh` use raw `dotnet` — the exact hang hazard CLAUDE.md, the wrappers, and `Directory.Build.props` all encode against — and enforce `-warnaserror` that GH CI doesn't (local/CI strictness drift).
- Timing-threshold tests (`Category=Performance|Latency`, e.g. avg < 100 µs/char) gate PRs on shared runners — a flake vector; 53 sleeps/delays across 23 test files, concentrated in PTY/SSH/Docker suites.
- Hygiene: nothing suspect is git-tracked, but the working tree carries gigabytes of dead weight — `.dotnetcli` package cache, four stale `.claude/worktrees` (feat-velopack, shortcuts-palette, startup-metrics-baseline, startup-orchestrator) full of built binaries, `artifacts/`, `_temp_out/`, and bins of two deleted test projects. `.artifacts-codex/` is ignored only incidentally via `[Bb]in/` — add it to `.gitignore` explicitly. `UnitTest1.cs` template placeholder still sits in App.Tests.

---

## Selected low findings (abridged)

Dead code: `ConPtyNative.cs` (183 lines, fully superseded by the Rust crate), `CircularBuffer<T>`, `_ResizeRow`, write-only fields `_prevCursorCol/_prevCursorRow/_maxColThisRow`, the vestigial `_verticalOffset` ConPTY-sync mechanism, per-chunk discarded `Stopwatch.StartNew()`, a parameter literally named `ignored`. `SshSession` is a 100% delegation duplicate of `OpenSshSession`. `SharedSKFont/SharedSKTypeface` refcounts have no underflow guard. `NativeKnownHostsStore` writes non-atomically (unlike its siblings) and silently resets on corruption. ECH/IL/DL treat explicit param 0 as 0 (xterm: 1); SGR 256-color index >255 isn't validated (`(short)65535 == -1` → silently "default"); multi-char charset designators leak a printable; erase ops discard palette indices so BCE cells don't retint on theme change; selection copy hard-breaks soft-wrapped lines; duplicated hard-coded "Campbell snapping" RGB remap corrupts legitimate truecolor that hits those exact values. `SshAskPassCommand` auto-answers any prompt containing "password" from the vault. `ReplayViewModel` fires `async void InitializeAsync()` from its constructor. Kitty chunking detected via `Contains("m=1")` substring. Replay v2 header parse failure silently degrades to v1. `TryGetLatestByRowId` is O(entries) under lock per call. MCP `ListDocs` reads unbounded file sizes; `SettingsTools` field list has no drift guard (profile tools do).

Notably clean areas: the MCP server is stdio-only, read-only, path-safe with symlink-aware traversal checks — no execution surface; SSH profile storage never persists secrets and validators actively police it; TOFU known-hosts with fail-closed mismatch on both C# and Rust sides; Rust FFI wraps every export in panic guards with a layout contract test; replay format is versioned with binary-layout guards and graceful truncation handling; async-void hygiene outside one case is good; per-tab state cleanup on close is symmetric and thorough.

---

## Priorities

1. **One-day wins, high impact:** ED 2 scrollback wipe (#1), zero-length regex guard (#2), `LC_ALL=C` removal (#5), UTF-8 marshalling attributes (#4), decoder buffer sizing, `.gitignore` + worktree cleanup, add `global.json`.
2. **Security:** vault fallback rework (#3), workspace-bundle restore confirmation, cmd/scp quoting.
3. **Stability:** pane teardown thread-correctness (#6), SSH connect timeout (#7), ImageRegistry deferred disposal, event-under-lock fixes.
4. **CI honesty:** restore App.Tests as a gate (quarantine the deadlock fixtures), reconcile PR replay/parity gates with CONTRIBUTING/ROADMAP, add any macOS lane, run tests in `release.yml`.
5. **Workstreams:** "row metadata lifecycle" (ICH/DCH maps + reflow threading + scrollback read-side), write-path ASCII fast path, MainWindow decomposition, warning-debt triage (CS8602 first).
