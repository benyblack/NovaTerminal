using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Ntilde.McpServer.Tests;

// End-to-end tests: launch the built server over stdio and drive a real MCP handshake.
// These exercise what unit tests cannot — tool discovery/registration, the stdio JSON-RPC
// contract, and the stdout-cleanliness invariant (a successful handshake proves nothing
// leaked to stdout). They are characterization tests of working behavior, so they pass on
// first correct run.
public class McpServerStdioE2ETests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    // Every tool the server is expected to register (v0.4).
    private static readonly string[] ExpectedToolNames =
    {
        "ntilde.get_project_summary",
        "ntilde.get_architecture_map",
        "ntilde.list_docs",
        "ntilde.read_doc",
        "ntilde.get_vt_conformance_summary",
        "ntilde.explain_escape_sequence",
        "ntilde.generate_vt_test_plan",
        "ntilde.get_theme_schema",
        "ntilde.validate_theme_json",
        "ntilde.get_connection_profile_schema",
        "ntilde.validate_connection_profile_json",
        "ntilde.get_settings_schema",
        "ntilde.validate_settings_json",
        "ntilde.generate_codex_prompt_for_issue",
        "ntilde.suggest_relevant_files",
        "ntilde.list_sessions",
        "ntilde.read_screen",
        "ntilde.read_scrollback",
        "ntilde.get_session_status",
        "ntilde.wait_for_events",
        "ntilde.export_replay",
        "ntilde.capture_screen",
        "ntilde.send_input",
        "ntilde.spawn_session",
        "ntilde.close_session",
        "ntilde.backup_export",
        "ntilde.backup_list",
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
            "ntilde.validate_theme_json",
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
            "ntilde.list_docs",
            cancellationToken: cts.Token);

        string text = result.Content.OfType<TextContentBlock>().First().Text;
        Assert.Contains("mcp/tools.md", text);
    }

    private static async Task<McpClient> StartClientAsync(CancellationToken ct)
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "ntilde-dev-e2e",
            Command = "dotnet",
            Arguments = [ServerDllPath()],
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["NTILDE_REPO_ROOT"] = RepoRoot(),
            },
        });

        // StdioClientTransport is a lightweight connector and is not itself disposable; the
        // spawned subprocess is owned by the connected transport inside McpClient, which
        // McpClient.CreateAsync tears down if the handshake fails and which our caller's
        // `await using var client` disposes on success. So there is nothing to dispose here.
        return await McpClient.CreateAsync(transport, cancellationToken: ct);
    }

    // Launch the server from THIS test assembly's output directory. The ProjectReference to
    // the server copies Ntilde.McpServer.dll + .runtimeconfig.json + .deps.json here.
    // This is the only location guaranteed to exist in the CI unit-test job: that job artifacts
    // tests/*/bin but NOT src/Ntilde.McpServer/bin, and runs `dotnet test --no-build`.
    private static string ServerDllPath()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Ntilde.McpServer.dll");

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Server DLL not found at '{path}'. Build the test project first.", path);
        }

        return path;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Ntilde.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException(
                "Could not locate 'Ntilde.sln' above the test output directory.");
    }
}
