# Issue Triage — 2026-07-27

**Scope:** all 28 open issues on `benyblack/NovaTerminal` at `main@1aee7c3`.
**Method:** every claim in every issue body was re-verified against the current
source tree. No code was changed. Verdicts below are backed by `file:line`
evidence; where an issue's premise no longer holds, that is called out
explicitly.

**Headline:** 1 issue is fully obsolete, 6 issues are materially stale (large
parts already fixed by later PRs), and 1 security issue is a genuine
arbitrary-file-write that should be the next thing anyone touches.

> **Update 2026-07-29** — re-verified against `main@1aee7c3`; every `file:line`
> claim below held. Actions taken since:
>
> - **#166 closed** (not planned — file deleted in #176) and **#81 closed**
>   (completed — remaining re-gating item folded into #117).
> - **#104 + #144 fixed and merged** as PR #210 (squash `29ae428`). Review surfaced
>   three further defects in the fix itself, all addressed: the `.novapart` scratch
>   name was deterministic (concurrent transfers to one destination could interleave
>   — `SftpService` runs each job on its own `Task.Run` with no per-destination
>   serialization), creation followed symlinks (now `O_EXCL`), and the rename
>   preceded a fallible remote close (now reordered so rename is the sole commit
>   point). Residual, deliberately out of scope: the final rename still follows a
>   symlink at the *destination* path.
> - Open count is now **24**.
> - Correction to §3 below: #103 and #119 are *also* already closed; of the Mode A
>   follow-on family only **#102** remains open.
>
> - **#121 items 1 (Rust side) + 4 merged** as PR #212 (`c0cee63`). Issue stays open for
>   the C# half of item 1 (managed `string` retention), which is the larger piece.
> - **#107 items (a) + (b) merged** as PR #214 (`5c8e98a`). Issue stays open for (c),
>   which #109 must own. Review caught two further defects in the fix: reporting the
>   failure exit without tearing the session down left the child process alive, and the
>   init-script cleanup raced `Dispose`. Both fixed before merge.
> - **PR #213 merged** (`90c9b3d`): pins that rename replaces an existing destination,
>   after review of #210 suspected a Windows defect there. Probed empirically — the
>   defect does not reproduce; the assumption was just untested.
> - **#211 filed**: the MCP dev companion runs from `bin/`, so a connected client blocks
>   repo builds. Docs/DX, P3.
>
> - **Quick-win batch done.** `#174` (PR #217, closed), `#126` (PR #219, closed), `#95 gap 1`
>   (PR #220, issue stays open for gaps 2-6), `#182` (PR #221, closed). Plus `#117 item 1`
>   (PR #222; issue stays open for item 2, blocked on AvaloniaUI/Avalonia#21467).
>
> **Findings worth carrying forward:**
>
> - **`dotnet format` fails on `main`** — 649 whitespace violations across 79 files,
>   pre-existing. Filed as **#216**. `ci/run.*` therefore reports rather than gates on it.
>   ~480 of the 649 are in `TerminalDrawOperation.cs` and `TerminalBuffer.ReflowEngine.cs`,
>   the files #113 and #164 will touch — sequence the sweep against those.
> - **`dotnet test --no-build --settings <file>` silently does nothing** on this SDK: exit 0,
>   no output, no report. Cost two CI round trips on #117. The coverage job builds from
>   source instead.
> - **PTY injection timing is load-bearing** for
>   `PtySmokeTests.AgentSentInput_IsByteFaithful`. Two separate changes to PTY timing broke
>   it (#214, #218) before the underlying race was fixed at its source.
> - **`RustPtySession` starts its loops in the constructor**, so anything subscribing to its
>   events can miss one that already fired. Bit a test in #215.
> - **Real-shell PTY tests now share one xUnit collection** (#218). Adding a new one outside
>   that collection reintroduces the starvation that broke the flight-recording test.
> - **Coverage baseline: `NovaTerminal.VT` 53.2% line / 47.86% branch**, floor 50%.
>   Understates true coverage because `App.Tests` — which holds the buffer and reflow suites
>   — is excluded from the coverage loop, mirroring the gating lane's #81 exclusion.
>
> - **#108 started** (PR #223): 6 of 18 projects now build with
>   `TreatWarningsAsErrors`. Real count is **1822 unique diagnostics across 13 projects**,
>   not the ~350 `Directory.Build.props` documents. Ladder and a detailed batch-2 analysis
>   (including two diagnostics that need justified *suppressions* rather than fixes) are in
>   a comment on #108.
>   - **CA2101 in `RustPtySession.cs` must not be "fixed"**: the `[MarshalAs(LPUTF8Str)]`
>     it objects to is deliberate per #152; following the analyzer reintroduces that bug.
>   - **A build that fails reads as zero warnings.** `McpServer.Tests` measured clean only
>     because the #211 lock stopped it building; clearing the lock surfaced 4 real
>     diagnostics. Never take a diagnostic count from a red build.
>
> Next on the §7 slate: nothing quick remains. The open items are the P2 perf work (`#165`,
> `#173`, `#172`), `#109` logging (needs a logging-abstraction decision, since
> `NovaTerminal.Pty` may not reference VT where `TerminalLogger` lives), `#108`
> warnings-as-errors (now unblocked by #174's `.editorconfig`, ~350 diagnostics), and the
> refactor cluster `#110`–`#115` gated on `#112`.

---

## 1. Summary table

Legend — **Verdict:** `VALID` = confirmed as written · `STALE` = partly fixed,
needs re-scope · `OBSOLETE` = close it.
**P:** P0 ship-blocker · P1 next · P2 soon · P3 backlog.
**Effort:** S ≤ 1 day · M ≈ 2–5 days · L > 1 week.

| # | Title (short) | Area | Verdict | P | Sev | Effort |
|---|---|---|---|---|---|---|
| ~~104~~ | SFTP recursive download: remote filename path traversal | Rust/SFTP | **FIXED 07-29** (PR #210) | — | — | — |
| ~~144~~ | SFTP partial download leaves truncated file | Rust/SFTP | **FIXED 07-29** (PR #210) | — | — | — |
| 164 | Row metadata lifecycle (ICH/DCH, reflow, scrollback) | VT | **STALE** | P1 | High | M |
| 107 | RustPtySession: read error vs EOF, temp file leak | Pty | **(a)+(b) FIXED 07-29** (PR #214); (c) → #109 | P3 | Low | S |
| 121 | Native SSH hardening (zeroize, SafeHandle, FFI tests) | Rust/SSH | **STALE** | P1 | High | S |
| 120 | PTY Unix path: locale, orphaned children, no error channel | Rust/Pty | **STALE** | P1 | Medium | S–M |
| 102 | TerminalPane/MainWindow subscriptions never cleaned up | App | VALID | P1 | Medium | M |
| 165 | Write path allocates a string per grapheme | VT | VALID | P2 | Medium | M |
| 173 | Native SSH output path: unbounded queue, sync writes | Platform/Rust | VALID | P2 | Medium | M |
| ~~126~~ | Cursor blink forces a render pass every 530 ms at idle | Rendering | **FIXED 07-30** (PR #219) | — | — | — |
| 172 | GlyphCache: skew unapplied, ink overhang clipped, emoji gaps | Rendering | VALID | P2 | Medium | M |
| ~~174~~ | Pin SDK with global.json; add .editorconfig; align ci/run.* | Build | **FIXED 07-30** (PR #217) | — | — | — |
| 117 | Add code-coverage reporting; re-gate headless App tests | CI | **item 1 FIXED 07-30** (PR #222); item 2 blocked upstream | P3 | Low | — |
| 95 | Full OSC 8 hyperlink adoption | VT/UX | **gap 1 FIXED 07-30** (PR #220); gaps 2–6 open | P2 | Low | S→L (staged) |
| 109 | Consolidate logging | src-wide | **STALE** | P3 | Low | S–M |
| ~~182~~ | Toast when a file drop is blocked | UX | **FIXED 07-30** (PR #221) | — | — | — |
| 110 | Extract TabManager + PaneLayout from MainWindow | App | **STALE** | P3 | Medium | L |
| 113 | Move TerminalView/TerminalDrawOperation into Rendering | Arch | VALID | P3 | Low | L |
| 112 | DI composition root; remove static command registry | App | **STALE** | P3 | Medium | M |
| 111 | Single ThemeService | App | VALID | P3 | Low | M |
| 114 | Extract CommandAssist to its own assembly | App | **STALE** | P3 | Low | M |
| 115 | Consolidate SSH into NovaTerminal.Ssh | Arch | **STALE** | P3 | Low | L |
| 127 | Rendering benchmarks + thresholds | CI | VALID | P3 | Low | M |
| 108 | Re-enable TreatWarningsAsErrors incrementally | Build | **6/18 projects ratcheted 07-30** (PR #223) | P3 | Low | M |
| 96 | SSH key management + provisioning wizard | Feature | VALID | P3 | — | L |
| 91 | Windows installer + auto-update + code signing | Release | VALID | P3 | — | M |
| ~~81~~ | CI: Unit Tests 8-min testhost teardown hang | CI | **CLOSED 07-29** | — | — | — |
| ~~166~~ | ImageRegistry eviction can dispose a live bitmap | Rendering | **CLOSED 07-29** | — | — | — |

---

## 2. Act on these first

### #104 — SFTP recursive download path traversal · **P0, Critical**

The only issue here with a remote-attacker-controlled arbitrary file write.

`src/NovaTerminal.App/native/rusty_ssh/src/lib.rs:1990-2000`

```rust
let file_name = entry.file_name();          // untrusted: from the server's read_dir
let local_child = local_dir.join(&file_name);
```

No `..` rejection, no separator check, no absolute-path check, no containment
test. `Path::join` with an absolute component *replaces* the base outright, so
`C:\...` (Windows) or `/etc/...` (Unix) escapes completely; `..\..\evil.exe`
escapes relatively. Worse, an escaped directory `local_child` is pushed back
onto `pending`, so a whole subtree can be written outside the chosen root.

Verified there is no mitigation anywhere on the path: the file's only
`canonicalize` calls are `sftp.canonicalize(".")` at `:1798` and `:1935` (remote
home expansion), and there are zero occurrences of `sanitiz`, `components()`,
`ParentDir`, or `starts_with(local_root)`. The managed side does not guard
either — `NativeSftpTransferModels.cs` contains no `..`, `GetFullPath`, or
`StartsWith`.

Secondary, lower severity: `remote_basename` (`:2287-2296`) →
`normalize_remote_directory_path` (`:2344-2358`) trims whitespace and trailing
`/` and rejects only exactly `"."` — **not** `..`. So a user remote path ending
in `/..` yields basename `".."`, and `:1749` does
`PathBuf::from(&transfer.local_path).join(remote_basename(...)?)`, relocating
the download root one level up. Input is user-supplied rather than
server-supplied, so this is hygiene rather than a vuln.

**Recommendation:** fix as written in the issue. Ship with #144 — same
function family, same native rebuild, one review.

### #144 — SFTP partial download leaves a truncated file · **P1, High**

`lib.rs:1873-1875` still writes straight to the final destination:

```rust
let mut local_file = TokioFile::create(local_path).await
    .map_err(|error| map_local_transfer_error(local_path, error))?;
```

Zero occurrences of `.part`, no `tokio::fs::rename`, no `remove_file` on any
error path. Note the aggravating detail the issue does not mention: `create`
**truncates an existing file** before any bytes arrive, so a cancelled
re-download destroys the previously good local copy. That raises this from
"leaves junk" to "silent data loss".

**Recommendation:** temp-then-rename, bundled into the #104 PR.

### #121 — Native SSH hardening · **P1, but 3 of 4 items are already done**

| Item | Verdict | Evidence |
|---|---|---|
| (a) No secret zeroization | **VALID** | `rusty_ssh/Cargo.toml` (19 lines) has no `zeroize`; it appears only transitively in `Cargo.lock`. Passwords are plain `String`: `lib.rs:289`, `:369`, `:2838-2845`, `:2868-2874`, plus extra `.clone()` copies at `:1524`, `:1593`. |
| (b) Raw `IntPtr`, no SafeHandle | **FIXED** | `Platform/Ssh/Native/NativeSshSafeHandle.cs` defines `NovaSshSafeHandle : SafeHandleZeroOrMinusOneIsInvalid`; every session P/Invoke at `NativeSshInterop.cs:956-988` takes it. |
| (c) FFI error-string ownership unaudited | **FIXED** | `lib.rs:128` `static OUTSTANDING_FFI_STRINGS: AtomicI64`, inc at `:136`, dec in `nova_ssh_string_free` `:1098-1102`; managed side frees in `finally` (`NativeSshInterop.cs:207-210`, `:288-291`, `:776-792`); `alloc_balance_tests` at `:3725-3738`. |
| (d) `ffi_contract.rs` thin; `cargo test` not in CI | ~~**PARTLY FIXED**~~ → **FIXED** | CI *does* run it — `ci.yml:43-60` `rust-ffi-tests` job runs `cargo test` for both crates. **Correction (07-29): this row was wrong.** It claimed call-after-close, double-close, and concurrent poll+close were "still missing" — all three already exist in the `handle_abuse_tests` module in `lib.rs` (`calls_after_close_fail_closed` covering 5 exports, `double_close_is_rejected`, `concurrent_poll_and_close_never_crashes` at 200 iterations). The triage measured only `tests/ffi_contract.rs` and missed that module. Two genuine gaps (two racing closers; handle-id non-reuse) were closed in PR #212. |

**Recommendation:** ~~re-scope to "(a) zeroize + (d) three concurrency abuse
tests"~~. Effort drops from M to S. Close out (b) and (c) in the issue body so the
scope doesn't get re-litigated.

> **Update 07-29 (PR #212):** (a) done for the Rust-owned copies —
> `TransferAuthConfig::password` is `Zeroizing`, the two `password.clone()` calls
> became a single `take`, and the raw JSON response payloads (which carry the secret
> in cleartext pre-parse) are wiped too. (d) needed almost nothing; see the corrected
> row above. **What actually remains on #121 is the C# half of (a)** — managed
> `string` retention — which is the larger piece: Rust can only wipe what Rust owns,
> and the credential also lives in a managed string, a JSON buffer, the marshalled
> native string, and russh's protocol buffers. #212 shortens retention; it does not
> remove credentials from process memory.

### #164 — Row metadata lifecycle · **P1, High, but half is done**

| Item | Verdict | Evidence |
|---|---|---|
| 1. ICH/DCH/insert-mode don't shift side-tables | **VALID** | `WritePath.cs:568-590` and `:603-651` shift only `row.Cells[c]`; no `SetExtendedText`/`SetHyperlink` in either method. `TerminalRow._extendedText`/`_hyperlinks` (`TerminalRow.cs:18-19`) stay keyed to pre-shift columns. |
| 2a. Reflow drops hyperlinks | **VALID** | `ReflowEngine.cs:61` still a 2-field tuple `(TerminalCell Cell, string? ExtendedText)`; populated `:351,:371,:387,:399`, replayed `:547-550`. Ironic consequence: the engine *reads* hyperlinks in (`:123`) and *writes* them out (`:606`), but nothing ever populates the flowed rows, so `GetHyperlinkMap()` is always null there. |
| 2b. `AppendRow(cells)` drops IsWrapped + maps | **FIXED** | All four call sites now pass wrap + both maps: `ResizeAndReflow.cs:106-110`, `:311-315`, `ReflowEngine.cs:602-606`, `WritePath.cs:421-425`. Height-grow restores via `TryPopLastRow(..., out extendedText, out hyperlinks)` + `TerminalRow.RestoreSideTables` (`:86-90`). |
| 3. `TryPopLastRow` leaves stale maps | **FIXED** | `ScrollbackPages.cs:283-289` now calls `page.ClearRowMetadata(rowIndex)`; `TerminalPage.cs:209-214` resets wrap + both arrays. |
| 4. Scrollback read side never surfaces metadata | **VALID** | `ThreadingAndInvalidation.cs:717` `Text = null, // TODO Step 5`. `AccessAndSnapshot.cs:87-91` `GetGraphemeAbsolute` degrades to `.Character.ToString()`; feeds `SelectionState.cs:97` (copy) and `Links/RowTextExtractor.cs:31` (link detection). `RenderCellSnapshot` (`RenderSnapshots.cs:98-99`) has no hyperlink field at all. |

Item 1 is the one that produces *visibly wrong output* — after `CSI @` / `CSI P`
on a line with emoji/CJK/OSC-8, cells carry `HasExtendedText` while their
strings stay at old columns. Insert mode hits it on every printable char.

**Recommendation:** re-scope to items 1, 2a, 4 and treat as one "row metadata
lifecycle" workstream. Item 4 is the shared prerequisite for #95 gap 1 — do
them together.

> **Update 07-30 (PR #225):** item 1 done — `TerminalRow.ShiftRowMetadata` moves both
> side tables with the cells for ICH, DCH and insert mode. #164 stays open for 2a and 4.
>
> Three findings worth carrying forward:
>
> - **Stale side-table entries are invisible through the rendered accessors.**
>   `GetGrapheme`/`GetGraphemeAbsolute` only consult the map when the cell carries
>   `HasExtendedText`, so a stale entry shows nothing until some later write sets that
>   flag on the same column. Four of my drop-case tests passed against the *unfixed*
>   code because of this — they were asserting through `GetGrapheme`. Anything testing
>   metadata lifecycle has to read the map directly (`GetRowAbsolute(row).GetExtendedText(col)`).
>   Expect the same trap on items 2a and 4.
> - **The maps must go back to `null`, not to an empty map.** `HasRowMetadata` is the
>   write path's fast-path guard; an empty-but-present map makes it permanently true for
>   that row and silently reintroduces per-character work in insert mode.
> - **`CommandAssistLayoutTests.TerminalPane_WhenRemotePromptSettlesLowOnShortPane_ResumesConservativeAssistLayout`
>   fails locally on clean `main`** (verified in a fresh worktree at `ef02e93`) while CI is
>   green — so it is environment-dependent, not a regression. Don't chase it as collateral
>   damage from an unrelated branch. `StressTests.DataFlood_Backpressure_StressTest` is
>   separately flaky under full-suite load and passes in isolation.

> **Update 07-30 (PR #226):** item 2a done, plus two siblings found while doing it. The reflow
> engine's logical-cell tuple now carries `Hyperlink`; the alt-screen resize and
> `ResizeDetachedScreenBufferNoLock` now copy side tables via `TerminalRow.CopyRowMetadataFrom`.
>
> - **The reflow bug hid behind code that looked complete.** Hyperlinks were read *in* from
>   paged scrollback and handed *out* to the rebuilt scrollback, so both ends looked wired
>   up — the two-field tuple in the middle meant `GetHyperlinkMap()` on every flowed row was
>   unconditionally null. Read both ends of a data path, not one.
> - **Mutation-check each site separately, not the set.** I asserted the detached-buffer fix was
>   covered because a test failed with all three mutants planted. Isolating that one mutant
>   showed zero failures — the test had failed for an unrelated reason (malformed escape
>   sequences printing the OSC introducer as literal text). `ResizeDetachedScreenBufferNoLock`
>   is likely **unreachable**: it only runs if `_mainScreen` geometry is stale leaving the alt
>   screen, and the background resize keeps it in step. Worth deleting on its own.
> - **The trailing-content trim has a cursor-row rescue clause that masks metadata bugs.**
>   `if (i == absCursorPhysicalIdx && _cursorCol > validLen) validLen = _cursorCol;` — so a
>   test that writes content and immediately resizes tests the *protected* path. Move off the
>   row with a newline. This cost me a second false pass, on the review fix for linked
>   trailing spaces.

> **Update 07-30 (PR #227):** item 4 done — the paged-scrollback render builder had
> `Text = null, // TODO Step 5`, so extended graphemes were *drawn* as their first UTF-16 code
> unit after scrolling off, even though #95 gap 1 had already made copy/selection correct. The
> screen and the clipboard disagreed. **#164 closed.**
>
> Deliberate non-change: `RenderCellSnapshot` has no hyperlink field and doesn't need one — hover
> and click resolve links live via `GetHyperlinkAbsolute` (`TerminalView.cs:1937, :2027`). Only a
> persistent link-underline feature would need it.

### #107 — RustPtySession · **P1, High**

All three items valid, and the read-error one is worse than described.

- **(a)** `RustPtySession.cs:585-590` — negative `pty_read` just does
  `_utf8Decoder.Reset(); Thread.Sleep(50);`. No code inspection, no log, no
  exit notification, no loop exit. A permanently failing handle spins at 20 Hz
  **forever** with the user seeing a frozen tab and no error. Contrast the
  `read == 0` EOF branch at `:580-584`, which correctly breaks.
- **(b)** `:408-435` — discarded `Task.Delay(300).ContinueWith(...)`, no
  cancellation tied to `_cts`, failure path is `Console.WriteLine` (`:433`).
  Zero `File.Delete` in the file, so **every PowerShell session permanently
  leaks a `nova_init_{guid}.ps1` into `%TEMP%`**. (Aside: the script writes a
  hardcoded fake "Windows PowerShell / Copyright (C) Microsoft" banner plus
  `Clear-Host` at `:416-422` — worth a second look on its own merits.)
- **(c)** 18 `Console.WriteLine` remain, none routed through `AppLogger`.

**Recommendation:** (a) and (b) are small and independently shippable. Do them
before any of the P2 perf work. (c) folds into #109.

> **Update 07-29 (PR #214, merged `5c8e98a`):** (a) and (b) done. #107 stays open for
> (c) only, which cannot be fixed in place: `NovaTerminal.Pty` is barred from
> referencing VT (where `TerminalLogger` lives) by `LayeringTests`
> `.Pty_must_not_depend_on_Vt`, so routing this file's `Console.WriteLine` calls needs
> a logging abstraction #109 must design.
>
> Two findings from doing the work:
> - **`pty_read` collapses every failure to `-1`** (null args, read error with errno
>   discarded, poisoned lock, caught panic), so the issue's "treat distinct negative
>   codes as terminal" is not achievable until #120 adds `pty_last_error`. The fix
>   bounds the retry instead.
> - **PTY injection timing is load-bearing for an unrelated test.** Switching the init
>   script write to `WriteAllTextAsync` moved the injected keystrokes later and broke
>   `PtySmokeTests.AgentSentInput_IsByteFaithful`. The synchronous write is deliberate.
>
> ~~Neither exit-code path is directly tested — `Native.pty_read` is a static P/Invoke
> with no seam. Making it injectable is a worthwhile follow-up.~~ **Done in PR #215
> (`df7ab1e`):** the read call is injectable via an internal constructor, and all four
> paths are covered (bounded retry, failure exit code, session teardown, EOF still 0,
> counter reset). Mutation-verified — disabling the bound fails exactly the two
> bound-dependent tests.
>
> That work also surfaced a general hazard in this class: **the read/process loops start
> inside the constructor**, so anything subscribing to a `RustPtySession` event can miss
> an exit that fires first. The test helper had to consult `ExitCode` as well as `OnExit`;
> the existing buffered-output replay exists for the same reason on the output path.

### #120 — PTY Unix path · **P1, and item 1 is already fixed**

| Item | Verdict | Evidence |
|---|---|---|
| 1. `LC_ALL=C`/`LANG=C` forced | **FIXED** (via #153) | `native/src/lib.rs:402-419` now probes `LC_ALL`/`LC_CTYPE`/`LANG` and only supplies `C.UTF-8` (or `LC_CTYPE=UTF-8` on macOS) when none is set. |
| 2. Child not killed on close | **PARTLY** | `pty_close` (`:686-712`) closes `HPCON`/`h_process` only, comment says "Drop logic handles the rest". The `child.kill()` lives in a *different* export, `pty_cancel_read` (`:677-681`). The normal path is safe (`RustPtySession.Dispose` calls `pty_cancel_read` at `:735` before `pty_close` at `:758-760`), but the `:726` branch deliberately skips cancel while unwinding — so orphaning is reachable on the exception path. |
| 3. No error reporting channel | **VALID** | `pty_spawn_impl` returns `null_mut()` at `:313, 378, 428, 433, 437`, discarding via `Err(_) =>`. No `pty_last_error` export exists. |
| 4. Windows handle leaks (minor) | **VALID + worse** | Three leak paths in `win32::spawn_with_passthrough`: `:53-55` (2nd `CreatePipe` fails → first pipe's 2 handles leak), `:69-71` (`CreatePseudoConsole` fails → all 4 leak), `:154-159` (`CreateProcessW` fails → `h_out_read` + `h_in_write` leak). Also unchecked: `InitializeProcThreadAttributeList` (`:81, :84`) and `UpdateProcThreadAttribute` (`:86-94`) return values ignored, and `DeleteProcThreadAttributeList` is **not** called on the success path (only in the failure branch at `:157`).

**Recommendation:** re-scope to items 2–4; item 3 is the user-visible one
("binary not found" is currently indistinguishable from "openpty failed").
Item 4 grew — add the unchecked-return and success-path-leak findings.

> **Update 07-30 (PRs #228, #229): #120 CLOSED.** Item 4 in #228 (RAII drop guards for
> handles/HPCON/attribute list, checked returns, `DeleteProcThreadAttributeList` on the
> success path). Items 2 and 3 in #229 (`pty_close` kills the child; new `pty_last_error`
> thread-local channel). Item 1 was already #153.
>
> Findings worth carrying:
>
> - **`Rust FFI Tests` ran on ubuntu only, and all of `rusty_pty`'s ConPTY code is
>   `#[cfg(windows)]`.** So its Windows tests — including the pre-existing
>   `cancel_read_unblocks_a_pending_read`, the #119 Dispose-join guard — were compiled out
>   and never ran in CI. Now a Linux + Windows matrix. **Check the OS matrix before
>   trusting a "the tests cover it" claim on platform-gated code.**
> - **Item 2 is unobservable on Windows.** Dropping the ConPTY master closes the
>   pseudoconsole, which ends the child, so the new `kill()` changes nothing there;
>   mutation-checking proved the test is a guard, not a demonstration. The orphan is real
>   on Unix. Verified via the Linux CI leg.
> - **`UpdateProcThreadAttribute`'s unchecked return was the worst of the four.** On
>   failure the child launches with no pseudoconsole attached, so its output never reaches
>   our pipes — a silently blank tab with no error anywhere.
> - **The error channel must never leave a stale message behind a `-1`.** Review caught
>   `pty_read`/`pty_write` returning `-1` on invalid arguments without touching the
>   channel, so callers were handed an older, unrelated failure. That arrived in the
>   review *summary* ("Comments Outside Diff"), not as an inline comment — **counting
>   inline review comments is not sufficient.**
> - **#107 item (a) note resolved:** `pty_read` still collapses failures to `-1` and keeps
>   its bounded retry (classifying on error text would be worse), but the reason is now
>   recorded and logged. #107 is down to item (c) alone.
>
> Open follow-up: 8 pre-existing `clippy::not_unsafe_ptr_arg_deref` errors across the FFI
> surface in `native/src/lib.rs`. The new export carries a targeted allow so the baseline
> did not grow; clearing the rest deserves its own change.

---

## 3. Close or re-scope

### #166 — ImageRegistry · **CLOSE AS OBSOLETE**

`src/NovaTerminal.Rendering/ImageRegistry.cs` no longer exists. Zero matches for
`ImageRegistry`, `RegisterImage`, or `GetImage` anywhere in `src/`.
`git log` on the path: `6ccd926 refactor(rendering): remove dead ImageRegistry (#176)`.

The use-after-dispose was resolved by deletion rather than by fixing the
disposal design. (Copies survive only in detached worktrees such as
`.claude/worktrees/feat-velopack/`, which are not on `main`.) Note the
`RowImageCache` the issue cites as the good example is also gone; only
`RowCache.cs` remains.

### #81 — CI testhost teardown hang · **RE-SCOPE, effectively resolved**

The original symptom is contained. `ci.yml:284` now runs
`--blame-hang-timeout 5m --blame-hang-dump-type mini` (not `8m`), the step is
`continue-on-error: true` (`:277`), and `:287-297` uploads `**/*.dmp` on
`if: always()`. The 8-line comment at `:266-275` records that the root cause was
dump-confirmed as a framework deadlock in `Avalonia.Headless.XUnit` 12.0.4
(`Directory.Packages.props:14`), tracked upstream as `AvaloniaUI/Avalonia#21467`,
and that collection serialization, `maxParallelThreads:1`, and
`ThreadPool.SetMinThreads` were each ruled out with dump evidence.

`8m` survives only in non-gating jobs, all with `--blame-hang-dump-type none`
(golden-PNG `:350`, replay `:410`, pty-smoke `:481`, render-metrics `:576`,
tab-perf `:634`, nightly-stress `:692`).

**Recommendation:** close #81 and fold the only remaining action —
"re-gate headless App.Tests once Avalonia#21467 ships" — into #117, which
already owns that item. Two issues currently track the same waiting-on-upstream
task.

### #109 — Logging · **RE-SCOPE, two of four items already fixed**

| Claim | Verdict |
|---|---|
| `TerminalLogger` is a 6-line `Action<string>` with no levels | **FIXED.** `VT/TerminalLogger.cs`, 55 LOC: `LogLevel` enum (`:5-11`), `MinimumLevel` filter (`:23, :29`), structured `Action<LogLevel,string>? OnLogLevel` (`:20`), `Debug/Info/Warning/Error` helpers (`:50-53`). The bare `Action<string>` survives only as back-compat with a `[LEVEL]` prefix (`:42`). |
| `File.AppendAllText("error.log", …)` in the draw path | **FIXED.** No hardcoded `"error.log"` anywhere. Only 2 `AppendAllText` calls in `src/`, both to structured paths: `AppLogger.cs:35`, `WorkspaceManager.cs:448`. |
| 28 `Console.WriteLine` in src | **PARTLY.** Now 34 across 9 files, but 6 are in `Conformance/VtConformanceReportTool.cs` (a console tool — legitimate). The real smell is `RustPtySession.cs` (18) plus GUI-side strays in `ThemeManager.cs` (3), `MainWindow.axaml.cs` (2), `OpenSshSession.cs`, `NativeSshSession.cs`, `ReplayWindow.axaml.cs`, `ReplayViewModel.cs`, `PtyRecorder.cs`. |
| 26 empty/silent catch blocks | **UNDERSTATED.** ~79 total: **41** truly empty across 19 files, plus 38 comment-only across 26 files. Heaviest: `NativeSshInterop.cs` (11), `NativePortForwardSession.cs` (6), `MainWindow.axaml.cs` (3). |

**Recommendation:** re-scope to "route the 28 non-tool `Console.WriteLine` calls
through `TerminalLogger`/`AppLogger`, and audit the 41 empty catches". Merge the
`RustPtySession` half with #107(c) so one PR covers that file.

### #115 — SSH consolidation · **RE-SCOPE, second claim not supported**

Assembly sprawl confirmed: no `NovaTerminal.Ssh` project exists (the 10 projects
are Cli, Conformance, Rendering, Replay, VT, Platform, Pty, AgentHost.Contracts,
McpServer, App). SSH lives in `Platform/Ssh/` (37 files across 8 subfolders) plus
`App/Services/Ssh/` (10), `App/ViewModels/Ssh/` (6), `App/Views/Ssh/` (4), and
`App/Shell/{SshAskPassCommand,SftpService,VaultService}.cs`. (There is no
`App/Shell/Ssh/` subdirectory, contra the issue's Area line.)

But the "two owners of the same edit flow" claim **does not hold**.
`ConnectionManager.axaml.cs` (654 LOC, as stated) is list + read-only detail
rendering: `RenderDetail` (`:372`) writes display-only fields (`:381-382`,
`:407-409`) and delegates editing outward via
`public event Action<TerminalProfile>? OnEditProfile` (`:29`, raised `:467`),
which `MainWindow.axaml.cs:4825-4827` routes to
`ShowNewSshConnectionDialogAsync(profile)` → `new NewSshConnectionView(vm)`
(`:5076`). `NewSshConnectionViewModel` (596 LOC) is the sole owner of the mapping
(`ToSshProfile()` `:296`, `ApplySshProfile()` `:394`). They share
`SshProfileRowViewModel`/`TerminalProfile`, not editing logic.

**Recommendation:** drop item 2, keep item 1 (assembly consolidation). Priority
stays P3 — this is tidiness, not risk.

### #112 — DI · **RE-SCOPE, partially landed and the named type is wrong**

- A minimal hand-rolled composition root **does** exist:
  `App/Shell/AppServiceBundle.cs:3` (`record AppServiceBundle(StartupOrchestrator Startup)`)
  and `App/Shell/AppServices.cs:7-31`, injected via
  `MainWindow(AppServiceBundle services)` (`:1934`) — but it carries exactly one
  dependency.
- `MainWindow` still `new`s its services inline (`:1942-1946`:
  `CommandPaletteUsageStore`, `SshConnectionService`, `SshInteractionService`,
  `SshLegacyProfileMigrationService`; plus `TerminalSettings.Load()` at `:1940`
  and statics `AgentHostService.Instance`, `AgentSessionRegistry.Instance`,
  `SftpService.Instance`, `ActiveSshSessionRegistry.Instance`).
- **`GlobalCommandRegistry` does not exist.** The type is `CommandRegistry`
  (`App/Shell/CommandRegistry.cs:18-20`) — still a mutable, non-thread-safe
  static whose `GetCommands()` (`:36`) hands out the live internal list.
  MainWindow calls `Clear()` then ~120 `Register(...)` on every settings or
  shortcut change (`:4254-4374+`).
- `Microsoft.Extensions.DependencyInjection` is used in exactly one place in
  `src/`: `McpServer/Program.cs:1, 16, 20`.

**Recommendation:** rename the referenced type in the issue body (otherwise the
next person greps for a symbol that doesn't exist), note the `AppServiceBundle`
starting point, and re-scope to "widen `AppServiceBundle`; make `CommandRegistry`
instance-scoped".

### #114 — CommandAssist · **RE-SCOPE, flag count halved**

Assembly extraction still pending: 62 `.cs` files, **4,766 LOC** (matching the
issue), still under `App/CommandAssist/`. `CommandAssistController.cs` is **959
LOC** (also matching).

The state-machine claim is now overstated: `:15-26` has **7** bool fields, not
~12 (`_isAltScreenActive`, `_isRemote`, `_isShellIntegrationEnabled`,
`_hasObservedShellIntegrationMarker`,
`_hasObservedStructuredCommandCaptureMarker`, `_ignoreCurrentSubmission`,
`_isExplicitAssistSession`). A `CommandAssistMode` enum and
`CommandAssistModeRouter` already exist, so *mode* is enum-driven; what remains
boolean is the session/marker lifecycle (see the implicit state machine in
`IsStructuredShellIntegrationActive()` `:889`).

### #110 — MainWindow extraction · **RE-SCOPE, one claim unsupported**

MainWindow is **5,826 LOC** — it has *grown* since the issue was filed (5,297).
`TabManagerService` / `PaneLayoutService` do not exist; all tab/pane/MRU/zoom/
broadcast state is inline (fields at `:60-70`).

The "`_tabMru`/`_broadcastEnabledTabs` mutated both directly and from
`Dispatcher.UIThread.Post`" claim is **not supported**. Both are mutated only
from synchronous UI-thread paths (`TouchTabMru`/`CleanupTabMru` `:375-392` via
`SwitchTabByMru` `:399, :407`; the `tabs.SelectionChanged` handler body `:2060`,
*not* inside a `Post`; `AddTab` `:3859`; `InitializeRestoredTabs` `:2476`). The
nearby `Post` at `:355-373` mutates `_pendingVisualRefreshTabs`, a different
collection. The underlying smell (plain `List`/`HashSet`, no thread-affinity
assertion) stands; the specific race does not.

`_isDraggingTransferOverlay` **is** still guard-without-`try/finally` (set `:4931`,
cleared `:4960`/`:4970`); mitigated in practice by the `PointerCaptureLost`
handler.

### #91 — Installer · **RE-SCOPE, adjacent work landed**

No Velopack, MSI/MSIX/NSIS, `.deb`/`.rpm`, `.dmg`/`.pkg`, `signtool`, `codesign`,
`notarytool`, or Trusted Signing step anywhere in `release.yml` — still portable
zip only (`:225-233`, uploaded `:235-239`, for `win-x64`/`linux-x64`/`osx-arm64`
`:193-199`). macOS builds will be Gatekeeper-quarantined.

Two things landed since filing that the issue should reference: a `submit_winget`
job (`:249-268`, `wingetcreate` PR to `microsoft/winget-pkgs`, gated on a
`WINGET_PAT` secret and self-skipping if unset) and a `release_tests` gate
(`:128-183`, `fail-fast: true`, three OSes, `#156` — releases used to publish AOT
bundles without running any test). `publish_aot` now depends on it (`:189`).

---

## 4. Notable corrections to issue bodies

Worth fixing in place so future readers aren't misled:

- **#172(a)** — `Skew` is genuinely never applied (`GlyphCache.cs:140-143` builds
  `physFont` with no `SkewX`), but it currently **cannot produce wrong output**:
  `SkewX` is never *set* anywhere in `src` (the only repo-wide hit is the read at
  `GlyphCache.cs:79`, so it is always `0.0`), and italics bypass the atlas
  entirely — `TerminalDrawOperation.cs:1279` `if (_glyphCache != null && !runIsItalic)`.
  This is latent/dead-weight, not a live rendering bug. Severity down.
- **#172(c)** — the missing ranges are real (`GlyphCache.cs:126-134` covers only
  `1F300-1FAFF` and `2600-27BF`), but flags and ZWJ sequences are rescued by a
  *different* mechanism: `ContainsRunesRequiringComplexShaping`
  (`TerminalDrawOperation.cs:1834-1849`) routes `1F300-1FAFF`, regional
  indicators (`:1890-1891`), `2600-27BF`, and ZWJ `200D` to the HarfBuzz path
  (`:1270`) when `_enableComplexShaping` is true (the default,
  `TerminalView.cs:549`). Genuinely uncovered by *both* lists: `2B50`, bare
  `FE0F`, `20E3`, `1F000-1F2FF`, `2B00-2BFF`. With `EnableComplexShaping=false`
  the narrow ranges become the sole gate.
- **#172(b)** is the one with real user-visible impact and it is *harder* than
  written: `GlyphAtlas.cs:97` hard-clips with `canvas.ClipRect(rect)` sized to
  advance × (ascent+descent), and the consumer at
  `TerminalDrawOperation.cs:1294/:1453` positions blits assuming exactly that
  packing. Fixing it requires storing per-glyph bearings and updating the blit
  math, not just switching to the `MeasureText(text, out SKRect)` overload.
- **#126** — confirmed at `TerminalView.cs:77` (530 ms,
  `DispatcherPriority.Render`) and `:330-344` (unconditional `_isDirty = true` →
  plain `InvalidateVisual()` at `:326`). The strongest argument for the fix isn't
  in the issue: `ShouldRunUiTimers()` (`:1464-1471`) checks visibility but never
  focus, while `Render` already does `bool hideCursor = !IsKeyboardFocusWithin;`
  (`:1512`) — so every blink frame on an unfocused pane is **provably** wasted
  work. No reduced-motion setting exists anywhere in `src` (only the
  all-or-nothing `settings.CursorBlink`, `:678`).
- **#127** — worse than "not covered": `NovaTerminal.Benchmarks.csproj:13` has a
  single project reference (`NovaTerminal.VT`), so the benchmark assembly
  **cannot see** `NovaTerminal.Rendering` at all. All 6 existing `[Benchmark]`
  methods are parser/reflow/scrollback. And the metrics the issue assumes exist
  partly don't: `RendererStatistics` has raw `RowCacheHits`/`RowCacheMisses`
  (`:59-60`) but **no glyph cache hit/miss counters at all** (only
  `GlyphAtlasResets` `:100`) and no computed `HitRate` property. Item 2 needs a
  metrics-plumbing prerequisite.
- **#173** — every sub-item confirmed. Two details worth adding: the
  `BUFFER_TOO_SMALL` retry is a **three**-copy path, not two (`peek_event` clone
  `:524-535` → `ptr::copy_nonoverlapping` → managed `buffer[..payloadLength]`
  slice at `NativeSshInterop.cs:324-326`), and `NativePortForwardSession.cs:97`
  is a lone synchronous `channel.Stream.Write` in a file that already uses async
  writes at `:426` and `:482` — so it looks like an oversight, not a design
  choice, and should be a cheap fix.
- **#165** — the LINQ boxing is **partly** mitigated: `WritePath.cs:163-172` now
  short-circuits `grapheme.Length == 1` with `Rune.TryCreate`, so
  `EnumerateRunes().FirstOrDefault(...)` only runs for multi-char graphemes. The
  per-grapheme `string` from `GetTextElement()` (`:150-154`) still allocates for
  **every** char including plain ASCII, and `IsLastRuneZwj` (`:381-390`) still
  re-enumerates on every write (`:366`, `:272`). Additional finding not in the
  issue: `Write(string)` at `ResizeAndReflow.cs:18-21` loops `WriteCharCore(c)`,
  which does `grapheme = c.ToString()` (`WritePath.cs:61`) — a *second*
  per-char allocation on that path.
> **Update 07-30 (PR #233): #172 items 1 and 3 done; item 2 still open.** Filed #234 off the back
> of review. Three lessons, all about *verifying the prescription, not just the diagnosis*:
>
> - **#172 item 1's proposed fix was wrong.** It says "Skew is in the key but never applied — set
>   `physFont.SkewX = key.Skew`". But synthetic italic is a *canvas* transform
>   (`canvas.Skew(-0.22f, 0f)`, `TerminalDrawOperation.cs:1247`), so atlas glyphs are meant to be
>   upright and that fix would double the slant. The real defect: `SkewX` is never *set* anywhere
>   (one read, in the key construction), so the component was always 0 — dead weight. Removed.
> - **Most emoji never reach GlyphCache at all.**
>   `ContainsRunesRequiringComplexShaping` diverts `1F300-1FAFF`, `2600-27BF`, regional indicators
>   and ZWJ to the shaper, bypassing the atlas. So my first PR body claimed to fix flags when they
>   already worked. **Check which path a fix is actually on before claiming what it fixes.** What the
>   routing change really fixes: single-rune emoji-default codepoints that do fall through — ⭐ ⭕ ⬛
>   ⌚ ⌛ 🀄 🃏 and the enclosed-ideographic set.
> - **The per-glyph loop enumerates runes, not clusters** (`TerminalDrawOperation.cs:1300`, variable
>   named `grapheme` but assigned `rune.ToString()`). So keycaps were split into three glyphs whatever
>   atlas the pieces went to — fixed for the default path by diverting `FE0F`/`20E3` to the shaper,
>   but **still broken for every cluster when `EnableComplexShaping` is off**. That's #234, and it
>   wants doing with item 2 since both change what "a glyph" means to that code.
>
> Also: I wrote "a range table is how the original bug happened" in a comment and then took
> `1F200-1F2FF` as a whole block — which includes two `Emoji_Presentation=No` members. Review caught
> it. And two of my own VS16 tests passed with the fix reverted, because their bases were already in
> range; mutation-checking per clause is what surfaced that.

> **Update 07-30 (PR #235): #165 CLOSED.** Filed #236 off the back of it. Measured, not estimated:
> 50,000 ASCII characters allocated **1,212,000 bytes before and 0 after** — the write path no
> longer allocates for ASCII output at all. `GetNextTextElementLength` over a span replaces the
> allocating enumerator; `WriteGraphemeInternal` takes a `ReadOnlySpan<char>`; an exact ASCII fast
> path skips segmentation entirely (everything that can extend a cluster is ≥ U+0300, so ASCII
> followed by ASCII is always a complete cluster).
>
> **The lesson here is about the tests, not the fix.** Three separate ways my allocation tests were
> wrong before they were right, each of which would have shipped a test proving nothing:
>
> 1. **Scrollback confounded the measurement.** A 50-row viewport with 200 lines meant page eviction
>    dominated — 2.5 KB for 50 lines, 496 KB for 200. Size rows so nothing scrolls off.
> 2. **The scaling test measured buffer construction.** It built the `TerminalBuffer` *inside* the
>    measured region, where a 260-row allocation dwarfed the difference — so it **passed against the
>    pre-fix code**. Construct fixtures outside the measured region.
> 3. **A zero baseline broke the ratio.** After the fix both measurements are 0, and `0 < 0` is
>    false, so a bare ratio failed on a *perfect* result. Absolute slack needed.
>
> (1) and (3) only surfaced because I ran the tests against the **fixed** code as well as the mutant.
> **A mutant-only check would have left (2) in place looking green** — mutation testing proves a test
> can fail, not that it fails for the right reason.

> **Update 07-30 (PR #237): #127 partially done, stays open for timing + stress.** The finding worth
> keeping is not about caches at all:
>
> **A CI job named after a thing does not mean the thing is tested.** `Render Metrics (os)` existed,
> ran on both OSes, and filtered `Category=RenderMetrics` — and every test behind that filter called
> `RendererStatistics.RecordFrame(...)` by hand and asserted the counter moved. None of them
> rendered. `RendererMetricsTests` admits it in a closing comment. That is *worse* than no job,
> because the green check reads as coverage on every PR page.
>
> Third time this pattern has bitten in this pass: #102's raw `+=`/`-=` counts, #172's assumption
> that emoji reach the glyph cache, and now this. **Before trusting that something is gated, read
> what the gating test actually asserts.**
>
> Also: the golden-PNG harness *could* exercise the caches and was deliberately configured not to
> (`rowCache: null, glyphCache: null`, so baselines come from one uncached path). Injecting caches is
> now opt-in; baselines unchanged.
>
> And once more, mutation-checking the *opposite* direction found a hole in my own test —
> `MutatingOneRow` had only an upper bound on misses, so a cache that never invalidated would have
> passed while rendering stale pixels. Three PRs running. **"What would a broken-the-other-way
> implementation do?" is now a required question, not a nice-to-have.**

> **Update 07-30 (PR #238): I broke `main`, second time this pass.** `Rust FFI Tests
> (windows-latest)` — the leg I added in #228 — failed on `owned_handle_closes_on_drop`. Not a
> product regression: **my test's assumption.**
>
> Windows reuses handle values eagerly and cargo runs tests as parallel threads in **one process**.
> After `CloseHandle`, the same numeric value can be handed back to a `CreatePipe` on another test
> thread — and `failed_spawn_does_not_leak_handles` creates 400 pipes in that very binary. So
> `is_open(closed_value)` returned true, reporting someone else's handle as ours.
>
> Two lessons, and the second is the sharper one:
>
> - **Ask "does this test read state another test can write?"** Both times I've broken `main` this
>   pass (#215's exit-code race, this) the answer was yes and I hadn't asked. Process-global state:
>   handle values, handle counts, `RendererStatistics`, `%TEMP%`, static registries.
> - **A partial fix to a race is the worst option available.** My first attempt locked only
>   `handle_ownership_tests`, and my own PR body admitted the spawn tests outside it still perturb
>   the handle *count* — I just failed to carry that reasoning to handle *values*. Review caught it.
>   It would have looked fixed and come back weeks later. The lock is now crate-scoped and held by
>   all eight tests that touch handles.
>
> Also worth noting: I could not reproduce the interleaving locally either before or after, so the
> fix is reasoned from Windows semantics plus cargo's threading model, with CI as the confirmation.
> Said so on the PR rather than claiming verification I didn't have.

> **Update 07-31 (PR #244): #109 items 1+2 — the diagnostics were going nowhere at all.**
>
> Items 1 and 2's `error.log` half were **already done** and the issue still described them as broken.
> Third time this week a stale issue body sent me looking for something that wasn't there. **Read the
> code before believing the ticket, every time.**
>
> The real defect was sharper than "inconsistent logging": a Windows GUI process has no console
> attached, so all ~30 `Console.WriteLine` calls were **written to nothing**. Read-loop failures, join
> timeouts, `input may be lost` — diagnostics that read as logging and behaved like comments.
>
> **The issue's count would have broken the build.** "28 `Console.WriteLine` calls in src" sweeps in
> `NovaTerminal.Cli` and `NovaTerminal.Conformance`, whose *product* is stdout. Converting those would
> have silently emptied the conformance report. **A count is not a work list.**
>
> **The architecture tests caught me.** My first pass added `using NovaTerminal.VT;` to `RustPtySession`
> and it compiled, because VT is reachable transitively through Replay — `Pty_must_not_depend_on_Vt`
> failed at IL level. Added a `PtyLogger` sink in the Pty layer instead, bridged from `Program`. It
> duplicates a little of `TerminalLogger`; the honest fix is relocating the logging facility to a shared
> leaf, which does not belong in the change that stops dropping messages. **Compiling is not the same
> as being allowed.**
>
> Two details worth keeping: the level-enum mapping is written out rather than cast, because a cast
> works right up until someone inserts a member and then silently mislevels everything; and the guard
> test **asserts its own exclusion list still matches something**, since a project rename would
> otherwise leave it policing nothing while passing.
>
> **Item 3's count is low.** The issue says 26 empty catches; catches with an entirely bare body — no
> statement, no comment — number **43**. Left open: each needs a judgement on whether swallowing is
> correct, and burying 43 of those in a logging PR would hide the ones that are real bugs.

> **Update 07-31 (PR #243): #232 closed — the tests were reading *my* settings file.**
>
> `ConfigureCommandAssist` called `TerminalSettings.Load()`, which reads the developer's config. So the
> font every test in that file ran against was whatever this machine had configured — 18pt here, 14pt
> defaults on a CI runner with no settings file. That is the entire "red locally, green in CI"
> mechanism, and it applied to the whole file; one test merely sat close enough to a threshold to
> notice.
>
> Chain, measured rather than reasoned: 18pt → 28px cell → arranging TermView resizes the buffer to
> `Bounds.Height / CellHeight` = 7 rows → `SetCursorPosition(0, 7)` **silently clamped to 6** →
> `6/11 = 0.545` falls under the 0.55 band-start ratio → suppressed → null layout. In CI, more rows,
> row 7 survives, `7/11 = 0.636` clears it. **A margin of 0.005, decided by a font.**
>
> **The issue's warning was right and I nearly ignored it.** It said to settle whether the threshold is
> legitimately metric-sensitive *before* the numbers tempt a threshold nudge. Once I had the numbers the
> nudge looked obvious — and it would have been wrong twice over: the threshold is fine, and the clamp
> is correct behaviour. The test was feeding inputs it never intended.
>
> Two fixes, both needed. Constructing settings removes the machine-specific input; pinning metrics
> *before* Measure/Arrange removes the font-measurement dependence that survives even with default
> settings, because default-font cell height still differs across platforms. Four tests had the ordering
> backwards, two already had it right — **the correct pattern was sitting in the same file**, which is
> the cheapest kind of evidence and I should look for it earlier.
>
> Also: the failure presented as `Assert.IsType() Failure: Value is null`, twenty lines downstream with
> no numbers. Added `AssertPromptHint` so the inputs are asserted where they are established.
> **A test that fails without saying what it measured costs more than one that fails loudly.**
>
> Local App.Tests: **1294 passed, 0 failed** — first clean local run in days. That was the whole point:
> a suite that permanently reports `1 failed` stops functioning as a gate, because new failures have to
> be spotted by diffing against a remembered baseline.
>
> - Open count is now **18**.

> **Update 07-31 (PR #242): #172 closed — the atlas packs ink bounds now, and the issue overstated it.**
>
> Last of the three items. The atlas packed each glyph into `ceil(advance)` x `ceil(descent-ascent)`
> and `DrawGlyph` clips, so overhanging ink was simply gone. Real in the app's own font at its own
> default size: `j` lost two of five columns of its descender hook.
>
> **The overhang is smaller than the issue implies, and I nearly published the inflated number.**
> Hinted glyph bounds round *outward*, so bounds arithmetic reports up to a pixel of overhang with no
> ink in it. My first probe counted 10 clipped glyphs at 14px and 62 at 1.5x scaling on that basis;
> comparing actual rasterized pixels, most of those are empty. Both test suites now compare pixels —
> **including the guard that asserts the sample set isn't vacuous**, which would otherwise have been
> built on the same overstating arithmetic and quietly claimed more than it checked.
>
> **The fix creates a new hazard and needed its own guard.** Ink boxes can exceed advance boxes, so a
> glyph larger than the whole atlas surface becomes reachable — and `GetOrAdd`'s overflow path would
> then evict, reset and fail *every frame forever*, because eviction cannot make room for something
> bigger than the surface. Declined up front instead. **Ask what the change makes newly reachable, not
> just what it fixes.**
>
> **Probing beat assuming again.** My first render-level test compared whole strings drawn with and
> without the cache and showed a 22px disagreement on `Hello, world` — nothing to do with this bug:
> the uncached path draws a run with one `DrawText` accumulating font advances, the cached path places
> each glyph on the cell grid. One glyph per frame is the only valid comparison. Had I trusted the
> first shape I'd have "discovered" a defect that isn't one.
>
> Mutation-checked both ways: advance-box packing fails 4 of 5 cache tests; ink packed with the
> bearings ignored at the blit fails all 16 placement tests — the second mutant matters because it is
> exactly the half-fix the cache-level tests cannot see.
>
> **Third instance of the same CI shape.** Every golden PNG passes, and that is worth nothing here:
> `SnapshotService` captures baselines with both caches off, so *no golden exercises the glyph atlas
> at all*. Same gap as `Render Metrics` (#127) and the `alloc balance` step (#173). Said so in the PR
> rather than listing the goldens as evidence.
>
> - Open count is now **19**.

> **Update 07-31 (PR #241): #234 closed — one glyph per cluster, and two of my own claims corrected.**
>
> The per-glyph draw loop enumerated *runes* while naming the result `grapheme`, so keycaps, flags,
> ZWJ families and skin-tone sequences were rasterized in pieces whenever `EnableComplexShaping` is off.
>
> **The obvious fix was wrong and I nearly shipped it.** Swapping in a text-element enumerator over
> `runText` re-segments the *concatenation of the run's cells* — and cluster boundaries need not line up
> with cell boundaries, so it can fuse two cells into one glyph while the column arithmetic still
> advances by two. Kept the per-cell texts alongside the run instead and bounded segmentation to a
> cell. **When replacing an enumerator, ask what the thing being enumerated actually is; `runText` is
> not one cell's worth of anything.**
>
> Two corrections to my own issue body, both from measuring rather than reasoning:
>
> 1. I wrote "the default configuration is fine" because emoji reach the shaper. False:
>    `ContainsRunesRequiringComplexShaping` covers U+0590-U+0FFF and the emoji ranges but **not**
>    U+0300-U+036F, so `a` + combining acute fell through the per-rune loop with shaping *on* too.
> 2. I wrote that the width totals "may or may not already agree". They disagree in **both**
>    directions — a ZWJ family is 2 columns as a cluster and 6 as runes; a VS16 sequence 2 and 1. So
>    the underline test is a theory over one overshoot and one undershoot; a fix that merely widened
>    something would have passed half of it.
>
> Also nearly shipped a bad assertion: I assumed a keycap is 2 columns wide and wrote the underline
> test around it. `GetGraphemeWidth("1<FE0F><20E3>")` is **1** in this codebase, so the 1-cell underline
> I was calling a bug was correct. **Probe the number before asserting on it** — I only caught this
> because the test failed against the *fixed* code.
>
> Mutation-checked: a per-rune enumerator fails 8 of 9 tests; the survivor is the plain-ASCII guard,
> which must pass both ways. Every existing golden PNG matched byte for byte — no baseline regenerated.
>
> Measured allocation while I was in there, and got a surprise worth recording: the cluster change is
> **exactly neutral** (43.39 B/cell before and after) because `Substring` returns the receiver when the
> range is the whole string, so the old per-rune path wasn't allocating for ASCII either. Substituting
> a cached single-char table for `next.Character.ToString()` drops it to 17.75 B/cell — ~59% of the
> draw path's per-cell garbage is that one call. Reported on #127, not fixed here.
>
> - Open count is now **20**.

> **Update 07-31 (PR #240): #236 closed — the ZWJ continuation flag now means what its name says.**
>
> `IsLastRuneZwj` returned true for a ZWJ *anywhere* in the cluster, with a comment claiming that as
> deliberate. But `_isAfterZwj` asks "did the cluster just written end expecting a continuation", and
> a *completed* ZWJ sequence contains ZWJ without ending on one — so the flag stayed set and the next
> grapheme, any grapheme, merged into a finished family emoji. **A comment asserting that surprising
> behaviour is intentional is not evidence that it is; check it against what the caller asks for.**
>
> Bigger than the issue said: the same flag also suppresses pending-wrap, so a family emoji at the
> right margin deferred the line break by one character too. I found that by reading every consumer
> of the flag rather than only the one the repro exercised.
>
> Fix is `EndsWithZwj` testing the last `char` — exact, not a shortcut, because U+200D is in the BMP
> and not a surrogate, so it can only be a whole trailing rune.
>
> **The mutation table is the useful artefact here.** Narrowing a predicate is only safe if the case
> it still has to accept keeps working, so seven tests split 3/4 and both directions checked:
> the old "any ZWJ" behaviour fails exactly the 3 completed-sequence tests; a `return false` mutant
> fails exactly the 4 split-read tests. No overlap and no gap — every test discriminates, and neither
> direction is left unguarded. Also wrote `CharacterAfterASequenceCompletedAcrossReadsLandsInItsOwnCell`
> specifically to kill the tempting wrong fix (clear the flag after one attachment), which would pass
> every other test in the file.
>
> Found while writing #235's tests, where I had *removed* the failing assertion and left a comment
> calling the behaviour pre-existing. That was the right call at the time only because I filed it;
> the assertion is restored now. **Deleting an assertion that caught something real is only acceptable
> if the something real gets an issue number.**
>
> - Open count is now **21**.

> **Update 07-31 (PR #239): #173 item 1's copy half done; queue bound + items 2/3 open.**
>
> **The issue understated it.** It says the `BUFFER_TOO_SMALL` retry copies payloads twice "larger
> than the last buffer" — but `buffer` is a *local* in `PollEvent`, reset to `Array.Empty` every
> call, so the retry was the **steady state**: every non-empty payload took 2 FFI transitions,
> 2 Rust clones and 2 managed allocations, traversing memory 4 times. **Read the caller before
> trusting a claim about a callee's edge case.**
>
> Fixed by folding peek+pop into one `take_event_if_fits`, which also closed a latent TOCTOU (two
> lock acquisitions meant a second consumer could pop between them). Managed buffer now retained
> per-thread — instance field would race, since the interop is injectable across sessions.
>
> **Old bug found in passing:** a null payload pointer with non-zero capacity passed the size check,
> skipped the copy, and popped the event anyway — silently destroying it. The retry path had *zero*
> test coverage, which is what made touching it risky.
>
> **Third label that overpromises.** The CI step is named `rusty_ssh FFI tests (… alloc balance)`
> and there is no allocation harness behind it — same shape as `Render Metrics` running tests that
> never rendered (#127), and `NovaTerminal.App.Tests` reporting "0 warnings" from a build that
> failed (#211). **A name is not a guarantee; open the thing.**
>
> Also caught myself about to grow this crate's clippy baseline 14 → 18: writing the event header in
> both match arms duplicates four raw-pointer stores, and `not_unsafe_ptr_arg_deref` fires per site.
> Restructured to write it once. Measured against a clean `main` worktree, not assumed.
>
> Deliberately deferred: the queue bound needs a **control-event carve-out** (`queue_event` has 15
> call sites; blocking `Closed`/`Eof`/`Error` behind a full data queue deadlocks teardown), which
> the issue's "stop reading the channel" sketch omits.

- **#102** — sharper numbers. `TerminalPane.axaml.cs`: 10 Parser `+=` / 0 `-=`;
  ~24 TerminalView `+=` / 2 `-=`. `Dispose()` (`:2349-2355`) →
  `DetachFromUiThread()` (`:2366-2401`) removes 3 of ~34 registrations.
  `_statusTimer` is stopped (`:2389-2390`) but its `Tick` lambda (`:467`) is
  never detached; `_shellLifecycleTracker.EventObserved` (`:2688`) has no `-=`.
  MainWindow: `_recordingToastTimer.Stop()` **is** called in `OnClosing`
  (`:5506-5517`) — that sub-claim is fixed — but the `Tick`/`Loaded`/`Activated`/
  `SizeChanged` lambdas are still never unsubscribed. TerminalView timers are
  **fine**: `OnDetachedFromVisualTree` (`:788-793`) → `StopUiTimers()`
  (`:1451-1462`) stops all five, and both timers use the ctor-callback overload
  so there are no lambdas to leak.

  > **Update 07-30 (PR #230): #102 CLOSED — the raw `+=` / `-=` counts above are
  > misleading, which is the lesson.** A missing `-=` only matters when the target
  > outlives the subscriber. Sorting the ~34 registrations by target lifetime:
  >
  > - **Parser (10)** — a *fresh* `AnsiParser` per session, so lists start empty and
  >   the old parser is garbage with its handlers. Nothing to unsubscribe.
  > - **TermView (~24)** — the pane's own XAML child (`TerminalPane.axaml:50`), dies
  >   with it. The 2 subscribed per-session use remove-before-add.
  > - **`_statusTimer.Tick`** — timer is stopped *and nulled*, so the lambda goes with it.
  > - **`_shellLifecycleTracker.EventObserved`** — a fresh tracker per launch plan.
  > - **`SftpService.Instance.JobUpdated`** — the *only* cross-lifetime subscription,
  >   and it is removed.
  >
  > So "3 of ~34 removed" was true and irrelevant. **Count subscriptions whose target
  > outlives the subscriber, not subscriptions.**
  >
  > Also found and fixed: the `SetupCommon` `MetricsChanged` lambda was uncached, the one
  > `TermView` handler still attached post-dispose. And a correction — the `??=` delegate
  > caching is *not* what makes the wiring idempotent (mutating it to a plain assignment
  > still passes: lambdas capturing only `this` compile to an instance method, so delegate
  > equality holds on target+method); the remove-before-add is.
  >
  > Deliberately did not do the proposed `CompositeDisposable` refactor — every `+=` site
  > across two large files, with dropped/duplicated UI events as the failure mode, to fix
  > nothing broken. Worth its own issue as a convention for new code.
- **#182** — confirmed dead at `TerminalView.cs:459-467` (message read for
  truthiness, then silent `return`). Both `DropRouter` block branches
  (`DropRouter.cs:24-29` secure-input, `:64-68` metacharacters) are swallowed.
  Additional finding: the third producer at `:84` ("WSL path mapping failed")
  sets `TextToSend`, so it falls through to `:481` and is *also* never shown —
  so three messages are dead, not two. No suitable channel exists: none of
  TerminalView's 11 public events carries a message, and MainWindow's only toast
  (`ShowRecordingToast`, `:5756`) is recording-specific.
- **#174** — all valid, and the cache-key drift is concrete: `setup-dotnet` uses
  floating `"10.0.x"` (`ci.yml:134, 190, 320, 386, 459, 552, 610, 668, 736`;
  `release.yml:146, 208`) while cache keys hardcode a non-matching
  `dotnet-sdk-${{ runner.os }}-10.0.300` (`ci.yml:126, 182, 312, 378, 450, 543,
  602, 660, 728`). `ci/run.ps1`/`run.sh` use bare `dotnet` throughout
  (`:5,10,13,16,19,22,25[,32]`) and never call `scripts/build.*` — the exact
  daemon-hang hazard `Directory.Build.props:10-13` documents. They also pass
  `-warnaserror` (`:16` in both) which GH CI does not (`ci.yml:142`), so there
  are two divergent build contracts.
- **#117** — `coverlet.collector` 6.0.4 is already pinned
  (`Directory.Packages.props:29`) and referenced by all 5 test projects, but no
  `dotnet test` passes `--collect` and no coverage artifact or gate exists: the
  dependency is present and entirely unused. The headless suppression is
  `continue-on-error: true` (`ci.yml:277`), and App.Tests is excluded from the
  gating lane (`:252-264`) altogether.
- **#95** — all six checked gaps confirmed, including gap 1
  (`AccessAndSnapshot.cs:106-110` returns `null` for scrollback) with the
  storage side already present (`ScrollbackPages.cs:222-226`,
  `TerminalPage.cs:184-190`). Gap 2: `AnsiParser.cs:1584-1592` keeps only the
  URI. Bonus finding — a malformed `OSC 8;URI` with no second `;` is silently
  ignored rather than treated as a close. Gap 3:
  `TerminalView.cs:1822-1832` passes `(absRow, col, col, osc8)` while the
  auto-detect path passes a real span (`:1852`). Gap 4: 4 schemes at
  `Links/LinkSchemes.cs:12-13`. Gap 6: zero `Hyperlink|Uri|Link` occurrences in
  either `RenderSnapshots.cs` or `ReplayModels.cs`.
- **#113** — TerminalView 2,103 LOC, TerminalDrawOperation 2,755 LOC, both still
  in `App/Shell/` under `namespace NovaTerminal.Shell`. Blocker the issue omits:
  `NovaTerminal.Rendering.csproj` references only `NovaTerminal.VT` + SkiaSharp —
  **no Avalonia** — while both files depend on `Avalonia`, `Avalonia.Media`,
  `Avalonia.Platform`, `Avalonia.Rendering.SceneGraph`, `Avalonia.Skia`
  (`TerminalDrawOperation.cs:1-5`). A straight move is impossible without either
  adding Avalonia to Rendering (defeating the boundary) or first splitting out a
  Skia-only core. Re-estimate accordingly.
- **#111** — confirmed and slightly worse: no `ThemeService`/`SettingsApplier`
  exists (`ThemeManager.cs` only loads definitions). `ApplyThemeToUI()` (`:4153`)
  is called from 6 sites (`:2086, 4370, 4371, 4994, 5001, 5012, 5037, 5064`), and
  each live-preview handler (`:4994-5015`) hand-rolls a *different* combination
  of `ApplyThemeToUI` / `ApplySettingsToAllTabs` / `UpdateTransparencyHints` /
  `UpdateTabVisuals` — the drift is already present. `_settings = sw.Settings`
  wholesale swap still happens (`:5019-5029`). Partial mitigation since filing: a
  `previewSnapshot` + Cancel revert path (`:4981-4991`, `:5053-5067`) for #167 —
  which addresses preview leakage, not the reference swap.
- **#96** — nothing exists yet, confirmed. The only key-adjacent code is
  identity-file *selection* (`NativeSshConnectionOptions.IdentityFilePath` →
  `load_secret_key`, `rusty_ssh/src/lib.rs:2865-2876`). Note
  `SettingsWindow.axaml:718` has an `SshKeyPathInput` TextBox with
  `IsEnabled="False"` — a disabled placeholder that should either be wired up or
  removed as part of this.
- **#108** — confirmed off (`Directory.Build.props:61`) with a 34-line rationale
  comment (`:27-60`) enumerating the ~350 hits: CA1834 (500+, "the bulk comes
  from a single file"), CA1865/CA1866, CA1051 (46), CA1305/CA1310/CA1304/CA1311,
  CA1861, CS8602 (5 in TerminalPane), CS0618 (SkiaSharp).

---

## 5. Dependency clusters

Sequencing that avoids rework:

1. **Rust SFTP** — #104 → #144. Same functions, one native rebuild, one security
   review.
2. **Row metadata + hyperlinks** — #164 item 4 (scrollback read path) unblocks
   #95 gap 1. #164 item 2a (reflow carries hyperlinks) and #95 gap 2 (`id=`
   param) both change the side-table value type, so doing them separately means
   touching `ReflowEngine` twice. Do #164 (1, 2a, 4) then #95 (1, 2, 3).
3. **Pty logging** — #107(c) and #109's `RustPtySession` share one file. One PR.
4. **Build hygiene** — #174 must land before #108: `.editorconfig` is where the
   per-rule suppressions live, so re-enabling `TreatWarningsAsErrors` without it
   forces a global `NoWarn` list that #108 explicitly wants to avoid.
5. **CI hardening** — #117 absorbs #81's remaining item. #127 needs a
   `RendererStatistics` glyph-counter prerequisite plus a project reference from
   the benchmarks csproj.
6. **App refactors** — #112 (DI + instance-scoped `CommandRegistry`) is the
   stated prerequisite for #110/#111/#114. #113 needs an Avalonia/Skia split
   decided first. Treat the whole cluster as one program, not five issues.

## 6. Quick wins

Small, independent, verified-valid, each shippable in under a day:

| # | Why it's cheap |
|---|---|
| ~~166~~ | ~~Close it. File is gone.~~ **Done 07-29.** |
| ~~81~~ | ~~Close it. Fold the one live item into #117.~~ **Done 07-29.** |
| 174 | Add `global.json` + `.editorconfig`, fix 9 cache keys, point `ci/run.*` at the wrappers. Config only. |
| 144 | Temp-then-rename in one Rust function (and it prevents silent data loss today). |
| 95 gap 1 | Wire one `if` branch at `AccessAndSnapshot.cs:106-110` to the page accessors that already exist. The issue itself labels it "good first issue". |
| 126 | Stop the blink timer on focus loss. `Render` already hides the cursor when unfocused, so no visual change. |
| 107 (b) | Delete the temp file; await the injection task. |
| 182 | One event + one toast call. |

## 7. Suggested next-sprint slate

`#104 + #144` (one security PR) → `#121a` → `#107` → `#164 (1, 2a, 4)` →
then the quick-win batch (`#174`, `#126`, `#95 gap 1`, `#182`).
~~and close `#166` / `#81`~~ — done 2026-07-29.

Leave the refactor cluster (#110–#115) parked until #112 is scheduled
deliberately — it is the gate on the other four, and MainWindow grew 529 LOC
while those five issues sat open.

---

*Triage performed read-only. No source files were modified.*
