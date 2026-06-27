# MCP stdio End-to-End Handshake Test — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an end-to-end test that launches the built `NovaTerminal.McpServer` over stdio and drives a real MCP handshake + tool calls, closing the protocol-level coverage gap.

**Architecture:** One new xUnit test class in `tests/NovaTerminal.McpServer.Tests` that uses the ModelContextProtocol SDK *client* to spawn the server's compiled DLL as a subprocess and exercise `tools/list` and `tools/call`. No server changes.

**Tech Stack:** C# / .NET 10, xUnit v3, `ModelContextProtocol` 1.4.0 (client: `StdioClientTransport`, `McpClient`).

## Global Constraints

- Build/test ONLY via wrappers: `scripts/build.ps1 <args>` (PowerShell) / `scripts/build.sh <args>`. Never raw `dotnet build`/`dotnet test` (hangs when stdout is captured).
- No changes to `src/NovaTerminal.McpServer` (the server project). This is test-only.
- The server is launched as `dotnet <NovaTerminal.McpServer.dll>` from the server's **own** build output (which has `runtimeconfig.json`/`deps.json`), located at `{repoRoot}/src/NovaTerminal.McpServer/bin/{config}/{tfm}/NovaTerminal.McpServer.dll`, where `{config}`/`{tfm}` are derived from the test's `AppContext.BaseDirectory`.
- Repo root is passed to the subprocess via the `NOVATERMINAL_REPO_ROOT` environment variable (found by walking up from `AppContext.BaseDirectory` to the directory containing `NovaTerminal.sln`).
- Every call runs under a `CancellationTokenSource` with a 60-second hard timeout, so a hang fails fast instead of stalling CI.
- Tests are tagged `[Trait("Category", "E2E")]` but run in the normal pass.
- These are characterization tests of already-working behavior, so they PASS on first correct run; each task includes a "verify the test has teeth" step (make it fail on a deliberately wrong expectation, then revert) to prove the assertion is real.

**SDK API reference (ModelContextProtocol 1.4.0; from the official csharp-sdk docs):**
```csharp
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

var transport = new StdioClientTransport(new StdioClientTransportOptions
{
    Name = "novaterminal-dev-e2e",
    Command = "dotnet",
    Arguments = [ serverDllPath ],
    EnvironmentVariables = new Dictionary<string, string?> { ["NOVATERMINAL_REPO_ROOT"] = repoRoot },
});
await using var client = await McpClient.CreateAsync(transport, cancellationToken: ct);
IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: ct);   // tool.Name
CallToolResult result = await client.CallToolAsync("tool.name",
    new Dictionary<string, object?> { ["arg"] = "value" }, cancellationToken: ct);
string text = result.Content.OfType<TextContentBlock>().First().Text;
```
> If a symbol does not resolve against the pinned 1.4.0 package, use the documented equivalent and keep the same call shape: `McpClient.CreateAsync` ↔ `McpClientFactory.CreateAsync` (returns `IMcpClient`); `TextContentBlock` ↔ `TextContent` (both expose `.Text`). The compile step in Task 1 pins this definitively.

---

### Task 1: Test harness + `tools/list` registration guard

**Files:**
- Modify: `tests/NovaTerminal.McpServer.Tests/NovaTerminal.McpServer.Tests.csproj` (add `ModelContextProtocol` package reference)
- Create: `tests/NovaTerminal.McpServer.Tests/McpServerStdioE2ETests.cs`

**Interfaces:**
- Consumes: the built server DLL (via `ProjectReference` already present; the server's own bin is the launch target).
- Produces: `McpServerStdioE2ETests` with private static helpers `ServerDllPath()` and `RepoRoot()` and `StartClientAsync(CancellationToken)` reused by Task 2.

- [ ] **Step 1: Add the client package reference**

In `tests/NovaTerminal.McpServer.Tests/NovaTerminal.McpServer.Tests.csproj`, add `ModelContextProtocol` to the existing package `ItemGroup` so it reads:

```xml
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="coverlet.collector" />
    <!-- MCP client (StdioClientTransport / McpClient) for the e2e handshake test. -->
    <PackageReference Include="ModelContextProtocol" />
  </ItemGroup>
```

(Version comes from Central Package Management — `Directory.Packages.props` pins `ModelContextProtocol` 1.4.0. Do not add a `Version` attribute.)

- [ ] **Step 2: Write the harness + registration-guard test**

Create `tests/NovaTerminal.McpServer.Tests/McpServerStdioE2ETests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace NovaTerminal.McpServer.Tests;

// End-to-end tests: launch the built server over stdio and drive a real MCP handshake.
// These exercise what unit tests cannot — tool discovery/registration, the stdio JSON-RPC
// contract, and the stdout-cleanliness invariant (a successful handshake proves nothing
// leaked to stdout). They are characterization tests of working behavior, so they pass on
// first correct run.
public class McpServerStdioE2ETests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    // The 13 tools the server is expected to register (v0.3).
    private static readonly string[] ExpectedToolNames =
    {
        "novaterminal.get_project_summary",
        "novaterminal.get_architecture_map",
        "novaterminal.list_docs",
        "novaterminal.read_doc",
        "novaterminal.get_vt_conformance_summary",
        "novaterminal.explain_escape_sequence",
        "novaterminal.generate_vt_test_plan",
        "novaterminal.get_theme_schema",
        "novaterminal.validate_theme_json",
        "novaterminal.get_connection_profile_schema",
        "novaterminal.validate_connection_profile_json",
        "novaterminal.generate_codex_prompt_for_issue",
        "novaterminal.suggest_relevant_files",
    };

    [Fact]
    [Trait("Category", "E2E")]
    public async Task ToolsList_ReturnsExactlyTheRegisteredTools()
    {
        using var cts = new CancellationTokenSource(Timeout);
        await using var client = await StartClientAsync(cts.Token);

        var actual = (await client.ListToolsAsync(cancellationToken: cts.Token))
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        var expected = ExpectedToolNames.OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.Equal(expected, actual);
    }

    private static async Task<McpClient> StartClientAsync(CancellationToken ct)
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "novaterminal-dev-e2e",
            Command = "dotnet",
            Arguments = [ ServerDllPath() ],
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["NOVATERMINAL_REPO_ROOT"] = RepoRoot(),
            },
        });

        return await McpClient.CreateAsync(transport, cancellationToken: ct);
    }

    // The server's own build output (has runtimeconfig.json/deps.json), in the same
    // config/TFM as this test assembly.
    private static string ServerDllPath()
    {
        var baseDir = new DirectoryInfo(AppContext.BaseDirectory); // .../bin/{config}/{tfm}
        string tfm = baseDir.Name;
        string config = baseDir.Parent!.Name;
        string path = Path.Combine(
            RepoRoot(), "src", "NovaTerminal.McpServer", "bin", config, tfm,
            "NovaTerminal.McpServer.dll");

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Server DLL not found at '{path}'. Build the solution/test project first.", path);
        }

        return path;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NovaTerminal.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException(
                "Could not locate 'NovaTerminal.sln' above the test output directory.");
    }
}
```

- [ ] **Step 3: Run the test — expect PASS**

Run: `scripts/build.ps1 test tests/NovaTerminal.McpServer.Tests --filter "FullyQualifiedName~McpServerStdioE2ETests"`
Expected: PASS (1 test). The server already registers all 13 tools, so the handshake completes and the names match.

If it fails to compile on an SDK symbol, apply the documented equivalent from the Global Constraints SDK note (e.g. `McpClientFactory.CreateAsync` returning `IMcpClient`, or `TextContent` instead of `TextContentBlock`) and re-run.

- [ ] **Step 4: Verify the test has teeth**

Temporarily append a bogus name to `ExpectedToolNames` (e.g. `"novaterminal.__does_not_exist"`).
Run: `scripts/build.ps1 test tests/NovaTerminal.McpServer.Tests --filter "FullyQualifiedName~McpServerStdioE2ETests"`
Expected: FAIL — `Assert.Equal` reports the set mismatch (proves the assertion isn't vacuous).
Then **revert** the bogus name and re-run to confirm PASS again.

- [ ] **Step 5: Commit**

```bash
git add tests/NovaTerminal.McpServer.Tests/NovaTerminal.McpServer.Tests.csproj tests/NovaTerminal.McpServer.Tests/McpServerStdioE2ETests.cs
git commit -m "test(mcp): e2e stdio handshake + tools/list registration guard"
```

---

### Task 2: `tools/call` assertions (self-contained + repo-reading)

**Files:**
- Modify: `tests/NovaTerminal.McpServer.Tests/McpServerStdioE2ETests.cs`

**Interfaces:**
- Consumes: `StartClientAsync`, `Timeout` from Task 1.
- Produces: two more `[Fact]` tests in the same class.

- [ ] **Step 1: Add the two tool-call tests**

Add a `ValidTheme` const and two test methods inside the `McpServerStdioE2ETests` class (after `ToolsList_ReturnsExactlyTheRegisteredTools`, before the private helpers):

```csharp
    private const string ValidTheme = """
        {
          "Name": "Dracula",
          "Foreground": "#F8F8F2", "Background": "#282A36", "CursorColor": "#F8F8F2",
          "Black": "#21222C", "Red": "#FF5555", "Green": "#50FA7B", "Yellow": "#F1FA8C",
          "Blue": "#BD93F9", "Magenta": "#FF79C6", "Cyan": "#8BE9FD", "White": "#F8F8F2",
          "BrightBlack": "#6272A4", "BrightRed": "#FF6E6E", "BrightGreen": "#69FF94",
          "BrightYellow": "#FFFFA5", "BrightBlue": "#D6ACFF", "BrightMagenta": "#FF92DF",
          "BrightCyan": "#A4FFFF", "BrightWhite": "#FFFFFF"
        }
        """;

    [Fact]
    [Trait("Category", "E2E")]
    public async Task CallTool_ValidateThemeJson_ReturnsValid()
    {
        using var cts = new CancellationTokenSource(Timeout);
        await using var client = await StartClientAsync(cts.Token);

        var result = await client.CallToolAsync(
            "novaterminal.validate_theme_json",
            new Dictionary<string, object?> { ["themeJson"] = ValidTheme },
            cancellationToken: cts.Token);

        string text = result.Content.OfType<TextContentBlock>().First().Text;
        Assert.StartsWith("VALID", text);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task CallTool_ListDocs_ReadsRepoDocs()
    {
        using var cts = new CancellationTokenSource(Timeout);
        await using var client = await StartClientAsync(cts.Token);

        var result = await client.CallToolAsync(
            "novaterminal.list_docs",
            cancellationToken: cts.Token);

        string text = result.Content.OfType<TextContentBlock>().First().Text;
        Assert.Contains("mcp/tools.md", text);
    }
```

- [ ] **Step 2: Run the tests — expect PASS**

Run: `scripts/build.ps1 test tests/NovaTerminal.McpServer.Tests --filter "FullyQualifiedName~McpServerStdioE2ETests"`
Expected: PASS (3 tests total). `validate_theme_json` returns a `VALID…` report for the Dracula theme; `list_docs` lists `docs/` entries including `mcp/tools.md` (resolved via `NOVATERMINAL_REPO_ROOT`).

- [ ] **Step 3: Verify the new tests have teeth**

Temporarily change the `CallTool_ListDocs_ReadsRepoDocs` assertion to a path that does not exist (e.g. `Assert.Contains("mcp/__nope__.md", text)`).
Run: `scripts/build.ps1 test tests/NovaTerminal.McpServer.Tests --filter "FullyQualifiedName~CallTool_ListDocs_ReadsRepoDocs"`
Expected: FAIL — substring not found (proves the repo-reading path is actually exercised, not silently empty).
Then **revert** to `"mcp/tools.md"` and re-run to confirm PASS.

- [ ] **Step 4: Run the whole test project**

Run: `scripts/build.ps1 test tests/NovaTerminal.McpServer.Tests`
Expected: PASS — all existing tests plus the 3 new e2e tests.

- [ ] **Step 5: Commit**

```bash
git add tests/NovaTerminal.McpServer.Tests/McpServerStdioE2ETests.cs
git commit -m "test(mcp): e2e tools/call coverage (self-contained + repo-reading)"
```

---

## Notes for the implementer

- **Why launch from the server's own bin, not the test bin:** running `dotnet X.dll` needs `X.runtimeconfig.json` next to `X.dll`. The server's own `bin/{config}/{tfm}/` always has it; relying on what a referenced-exe copies into the test bin is less certain. The `ProjectReference` guarantees the server is built (into its own bin) whenever the test project builds.
- **`await using var client`:** disposing the client shuts the subprocess down. Don't skip it, or you leak `dotnet` processes.
- **Timeouts:** every SDK call takes `cts.Token`; the 60 s budget turns a hung handshake into a fast failure rather than a stalled CI job (this repo has a history of headless hangs).
- **CI:** `NovaTerminal.McpServer.Tests` is already in CI's unit-test loop; no `ci.yml` change is needed. The new `ModelContextProtocol` package ref restores from the same pinned version already used by the server.
- **Pre-existing convention:** tests use xUnit v3 with the global `using Xunit;` from the csproj; `[Fact]`/`[Trait]` need no extra using.
