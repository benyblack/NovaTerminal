# Updating Replay Snapshots (Golden Masters)

NovaTerminal uses **replay-based regression tests**: a recorded terminal stream (`.rec`) is replayed through the parser/buffer, and the resulting buffer state is compared to a **golden snapshot** (`.snap`).

This document describes the **only supported workflow** for creating/updating snapshots.

---

## Concepts

- **`.rec`**: Recording of terminal output (JSONL with Base64 chunks). This is the *stimulus*.
- **`.snap`**: Deterministic snapshot of the terminal buffer after replay. This is the *oracle*.

Golden workflow:

```
.rec  → ReplayRunner → AnsiParser → TerminalBuffer → BufferSnapshot → .snap
```

> The renderer must not affect `.snap`. Snapshots validate **VT correctness**, not pixels.

---

## Directory layout

Replay fixtures live here:

```
NovaTerminal.Tests/Fixtures/Replay/
```

Each fixture is a pair:

```
<name>.rec
<name>.snap
```

Example:

```
vttest_cursor.rec
vttest_cursor.snap
```

---

## Running replay regression tests

Run only replay tests:

```bash
dotnet test NovaTerminal.Tests/NovaTerminal.Tests.csproj --filter Category=Replay
```

This should be fast and is safe to run on every PR.

---

## Creating a new snapshot (first time)

1. Add a new `.rec` file to:

   ```
   NovaTerminal.Tests/Fixtures/Replay/
   ```

2. Ensure there is a replay test that references that `.rec` (or a parameterized test that discovers it).

3. Run replay tests with snapshot update enabled:

   **Linux / macOS (bash):**
   ```bash
   UPDATE_SNAPSHOTS=1 dotnet test NovaTerminal.Tests/NovaTerminal.Tests.csproj --filter Category=Replay
   ```

   **Windows (PowerShell):**
   ```powershell
   $env:UPDATE_SNAPSHOTS="1"
   dotnet test NovaTerminal.Tests\NovaTerminal.Tests.csproj --filter Category=Replay
   Remove-Item Env:\UPDATE_SNAPSHOTS
   ```

4. Confirm the `.snap` file was generated next to the `.rec`.

5. Re-run tests **without** `UPDATE_SNAPSHOTS`:

   ```bash
   dotnet test NovaTerminal.Tests/NovaTerminal.Tests.csproj --filter Category=Replay
   ```

6. Review the generated `.snap` content and commit both `.rec` and `.snap`.

---

## Updating an existing snapshot

Only update snapshots when you *intentionally* changed VT behavior and you have reviewed the diff.

1. Make your code change.
2. Run replay tests normally first (expected to fail if behavior changed):

   ```bash
   dotnet test NovaTerminal.Tests/NovaTerminal.Tests.csproj --filter Category=Replay
   ```

3. If the new behavior is correct and desired, regenerate snapshots:

   ```bash
   UPDATE_SNAPSHOTS=1 dotnet test NovaTerminal.Tests/NovaTerminal.Tests.csproj --filter Category=Replay
   ```

4. Re-run tests without update mode to confirm stability:

   ```bash
   dotnet test NovaTerminal.Tests/NovaTerminal.Tests.csproj --filter Category=Replay
   ```

5. Commit updated `.snap` files in the same PR as the behavior change.

---

## Snapshot update policy (important)

- **Never** update snapshots in CI automatically.
- Snapshot updates must be a deliberate developer action (`UPDATE_SNAPSHOTS=1`).
- A PR that changes `.snap` files must clearly explain **why** (e.g., bug fix, spec alignment).

---

## Determinism checks

Before committing new goldens, verify snapshots are stable across runs:

```bash
sha256sum NovaTerminal.Tests/Fixtures/Replay/*.snap | sort
dotnet test NovaTerminal.Tests/NovaTerminal.Tests.csproj --filter Category=Replay
sha256sum NovaTerminal.Tests/Fixtures/Replay/*.snap | sort
```

Hashes should not change.

If they do:
- check environment determinism (TERM/LC_ALL/LANG),
- check replay timing semantics (timestamps should not affect output),
- check any non-deterministic code paths (randomness, time-based behavior).

---

## Adding new replay fixtures (recommendations)

- Keep recordings small and focused (one feature per `.rec`).
- Name fixtures by intent: `vttest_scroll`, `alt_screen_cursor`, `wrapped_text`, etc.
- Prefer **external-suite derived** `.rec` (e.g., VTTEST captures) for broad coverage.
- Prefer **handcrafted** `.rec` only for very targeted edge cases.

---

## Troubleshooting

### “Tests pass but no `.snap` was created”
Your replay tests only create `.snap` for recordings they actually run.
- Ensure the `.rec` filename matches what the test expects, or
- Ensure your parameterized test discovers it.

### “Snapshot differs on every run”
Common causes:
- capture timing affecting replay (replay should ignore `t`),
- locale/TERM differences,
- unstable macro driving (for external suites).

### “PTY capture fails on Windows”
Use WSL/Linux for VTTEST capture (Linux PTY semantics).
Replay tests can run anywhere once `.rec` is committed.

---

## Related docs

- `docs/vt_coverage_matrix.md` — VT feature coverage and evidence links
- `tests/NovaTerminal.ExternalSuites/` — external capture harnesses (e.g., VTTEST)
