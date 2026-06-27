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
