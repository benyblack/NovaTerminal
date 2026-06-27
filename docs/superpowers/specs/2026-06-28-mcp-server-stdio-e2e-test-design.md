# MCP Dev Companion — stdio end-to-end handshake test

**Date:** 2026-06-28
**Status:** Design approved, ready for implementation plan
**Component:** `tests/NovaTerminal.McpServer.Tests`

## Summary

Add a single end-to-end (e2e) integration test that launches the **built**
`NovaTerminal.McpServer` as a real subprocess, connects to it over stdio with the
ModelContextProtocol SDK client, and drives a full MCP handshake plus a couple of tool
calls. This closes the one coverage gap left after v0.3: the server now exposes 13 tools
but has **zero protocol-level coverage** — every existing test is method-level.

## Motivation

The unit tests verify each tool's logic in isolation. They do **not** verify the things a
real MCP client depends on, all of which live outside the tool methods:

- **Tool discovery / registration** — `WithToolsFromAssembly()` actually finding and
  registering every `[McpServerToolType]`.
- **The stdio JSON-RPC contract** — `initialize` → `tools/list` → `tools/call` succeeding
  over a real stdio pipe.
- **The stdout-cleanliness invariant** — `Program.cs` pins all logging to stderr because
  anything on stdout corrupts the JSON-RPC stream. A regression here (e.g. a stray
  `Console.WriteLine`, or a logging provider re-added on stdout) silently breaks every
  client, and no current test would catch it. A successful handshake is the assertion: if
  stdout were polluted, the client could not complete `initialize`.

## Goals

- Prove the built server starts, completes an MCP handshake over stdio, lists all tools,
  and answers tool calls.
- Catch registration regressions (a tool not discovered) and stdout-corruption regressions.
- Run in the normal CI test pass so the protection is real, with a hard timeout so a hang
  fails fast instead of stalling the job.

## Non-goals

- No in-process / in-memory transport — it would bypass `Program.cs`, the stdio framing,
  and the stdout contract, which are the whole point.
- No exhaustive per-tool e2e coverage — tool logic is already unit-tested. This test covers
  the protocol surface and a representative call of each kind (self-contained + repo-reading).
- No changes to the server project.

## Approach

The test lives in `tests/NovaTerminal.McpServer.Tests` (which already references the server
project, so the built `NovaTerminal.McpServer.dll` is copied next to the test assembly).

### Locating the server

- **DLL:** launch the server from its **own** build output —
  `{repoRoot}/src/NovaTerminal.McpServer/bin/{config}/{tfm}/NovaTerminal.McpServer.dll`,
  with `{config}`/`{tfm}` derived from the test's `AppContext.BaseDirectory`. Running
  `dotnet X.dll` requires `X.runtimeconfig.json` next to `X.dll`, which is guaranteed in the
  server's own bin but only incidentally true in the test bin; the existing `ProjectReference`
  still guarantees the server is built whenever the test builds. (Implementation note: an
  earlier draft launched from `AppContext.BaseDirectory`; the server's own bin is the robust
  choice and is what shipped.) Works for Debug/Release on Windows/Ubuntu.
- **Repo root:** walk up from `AppContext.BaseDirectory` until a directory containing
  `NovaTerminal.sln` is found, and pass it to the subprocess as the
  `NOVATERMINAL_REPO_ROOT` environment variable. This avoids relying on the subprocess's
  working directory.

### Driving the server

```csharp
var transport = new StdioClientTransport(new StdioClientTransportOptions
{
    Name = "novaterminal-dev",
    Command = "dotnet",
    Arguments = [ serverDllPath ],
    EnvironmentVariables = { ["NOVATERMINAL_REPO_ROOT"] = repoRoot },
});

await using var client = await McpClientFactory.CreateAsync(transport, cancellationToken: cts.Token);
```

All calls run under a `CancellationTokenSource` with a hard timeout (60 s) so a hung
handshake fails the test fast rather than hanging the CI job. The client is async-disposed,
which shuts the subprocess down.

> The exact SDK type/option/method names above (`StdioClientTransportOptions`,
> `McpClientFactory.CreateAsync`, `ListToolsAsync`, `CallToolAsync`, and how to read text
> content off a tool result) are to be confirmed against ModelContextProtocol 1.4.0 during
> implementation; the shape is as shown.

### Package reference

Add a direct `ModelContextProtocol` `PackageReference` to the test project (Central Package
Management supplies the version 1.4.0). The client types are available transitively today,
but a direct reference makes the dependency explicit.

## Assertions

1. **`tools/list` returns exactly the 13 expected tool names.** Compare the returned tool
   names (sorted) against the expected set (sorted) for equality. This guards discovery/
   registration, forces an intentional update whenever a tool is added or removed, and —
   because the handshake had to succeed first — implicitly proves the stdout stream is
   clean. Expected names:
   `novaterminal.get_project_summary`, `novaterminal.get_architecture_map`,
   `novaterminal.list_docs`, `novaterminal.read_doc`,
   `novaterminal.get_vt_conformance_summary`, `novaterminal.explain_escape_sequence`,
   `novaterminal.generate_vt_test_plan`, `novaterminal.get_theme_schema`,
   `novaterminal.validate_theme_json`, `novaterminal.get_connection_profile_schema`,
   `novaterminal.validate_connection_profile_json`,
   `novaterminal.generate_codex_prompt_for_issue`, `novaterminal.suggest_relevant_files`.
2. **A self-contained tool call** — call `novaterminal.validate_theme_json` with a
   known-good theme JSON; assert the returned text starts with / contains `VALID`. Proves
   the `tools/call` request → argument binding → result serialization path end-to-end.
3. **A repo-reading tool call** — call `novaterminal.list_docs`; assert the result is
   non-empty and contains a doc that is guaranteed to exist (e.g. `mcp/tools.md`). Proves
   `RepoContext` discovery + the `NOVATERMINAL_REPO_ROOT` wiring + a real file read
   end-to-end.

## Test structure & CI

- One test class, `McpServerStdioE2ETests`, in
  `tests/NovaTerminal.McpServer.Tests/McpServerStdioE2ETests.cs`.
- Tagged `[Trait("Category", "E2E")]` so it can be filtered if it ever misbehaves, but it
  runs in the **normal** test pass by default (a quarantined e2e test protects nothing).
- A small private helper resolves the DLL path and repo root; shared by the tests.
- Cross-platform: launching `dotnet <dll>` works on Windows and Ubuntu CI runners.
- `NovaTerminal.McpServer.Tests` is already registered in CI's unit-test loop; no `ci.yml`
  change is needed (no new project).

## Risks & mitigations

- **Process-spawn flakiness / hangs** — mitigated by the hard 60 s timeout and proper async
  disposal; the `E2E` trait allows filtering as a last resort.
- **`dotnet` not on PATH in some environment** — CI runners and dev machines that build this
  repo have the .NET SDK on PATH by definition; acceptable assumption for a test that
  inherently needs the runtime.
- **Tool-count assertion churn** — intentional: adding a tool must update this test, which
  is the registration guard working as designed.

## Out of scope / future

- Settings schema/validator tools (`get_settings_schema`, `validate_settings_json`) — the
  next feature candidate, separate spec.
- Running-app read-only bridge — still deferred (needs a threat model).
