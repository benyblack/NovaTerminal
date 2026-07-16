using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using ModelContextProtocol.Server;

namespace NovaTerminal.McpServer.Tools;

[McpServerToolType]
public static class WorkflowTools
{
    // Keyword → (relevant area, constraint) hints, used to tailor the generated prompt.
    private static readonly (string[] Keywords, string Area)[] AreaHints =
    {
        (new[] { "ssh", "key", "auth", "connection", "jump host", "known_hosts" },
            "SSH: NovaTerminal.Platform/Ssh (native + external OpenSSH) and NovaTerminal.App SSH UI/profiles."),
        (new[] { "theme", "color", "palette" },
            "Theming: NovaTerminal.App ThemeManager + docs/ThemeSystem.md; colors via TerminalTheme (NovaTerminal.VT). Validate with novaterminal.validate_theme_json."),
        (new[] { "parser", "escape", "ansi", "csi", "osc", "dcs", "vt", "sequence" },
            "VT/ANSI: NovaTerminal.VT/AnsiParser.cs. Check novaterminal.get_vt_conformance_summary first."),
        (new[] { "reflow", "resize", "scrollback", "wrap" },
            "Reflow/buffers: NovaTerminal.VT TerminalBuffer.*; lossless reflow is a headline invariant."),
        (new[] { "render", "glyph", "atlas", "draw", "paint", "frame" },
            "Rendering: NovaTerminal.Rendering (GlyphCache/Atlas) + NovaTerminal.App TerminalView/TerminalDrawOperation."),
        (new[] { "pty", "shell", "process", "spawn" },
            "PTY: NovaTerminal.Pty (Rust native backend via P/Invoke)."),
        (new[] { "sftp", "transfer", "upload", "download" },
            "SFTP: NovaTerminal.App SftpService (native transfer is in the Rust layer)."),
        (new[] { "tab", "pane", "window", "layout" },
            "UI shell: NovaTerminal.App MainWindow / TerminalPane."),
    };

    [McpServerTool(Name = "novaterminal.generate_codex_prompt_for_issue"),
     Description("Generates a structured implementation prompt for a NovaTerminal issue/task. Given a title and description, returns relevant code areas, architectural constraints, suggested PR size, implementation steps, tests to update, acceptance criteria, and risks — tailored to NovaTerminal's conventions.")]
    public static string GenerateCodexPromptForIssue(
        [Description("Short title of the issue/task, e.g. 'Improve SSH key authentication UX'.")] string title,
        [Description("Optional fuller description / acceptance notes for the task.")] string description = "")
    {
        // External client input may be null despite the signature — normalize defensively.
        title ??= string.Empty;
        description ??= string.Empty;
        string haystack = (title + " " + description).ToLowerInvariant();
        var areas = AreaHints
            .Where(h => h.Keywords.Any(k => haystack.Contains(k, System.StringComparison.Ordinal)))
            .Select(h => h.Area)
            .ToList();
        if (areas.Count == 0)
        {
            areas.Add("Could not auto-map to a subsystem — call novaterminal.get_architecture_map and novaterminal.get_project_summary to locate the relevant module.");
        }

        var sb = new StringBuilder();
        sb.Append("# Implementation prompt: ").Append(title.Trim()).Append("\n\n");

        if (!string.IsNullOrWhiteSpace(description))
        {
            sb.Append("## Context\n").Append(description.Trim()).Append("\n\n");
        }

        sb.Append("## Relevant areas\n");
        foreach (var a in areas) sb.Append("- ").Append(a).Append('\n');

        sb.Append(
            """

            ## Architectural constraints
            - Build/test only via `scripts/build.{ps1,sh}` (never raw `dotnet`) — see CLAUDE.md.
            - Respect module boundaries (novaterminal.get_architecture_map): NovaTerminal.VT is a
              leaf (BCL only); Pty/Replay/Rendering must not reference UI/App.
            - All `TerminalBuffer` reads require holding `TerminalBuffer.Lock`.
            - Lossless reflow must be preserved; alternate-screen isolation must be preserved.
            - No secrets, private keys, or shell execution introduced into read-only/data paths.

            ## Suggested PR size
            - Prefer one focused change with tests. If it spans multiple assemblies or changes a
              public contract, split into reviewable commits.

            ## Implementation steps
            1. Reproduce / characterize current behavior (add a failing test where applicable).
            2. Make the minimal change in the owning assembly.
            3. Add/extend unit tests (prefer NovaTerminal.VT.Tests for VT logic; project-specific
               test projects otherwise).
            4. Run the targeted test project, then a broader build, via the wrapper scripts.

            ## Tests to update
            - Unit tests in the owning assembly's test project.
            - Replay/regression suites if parser/rendering/reflow behavior changes.

            ## Acceptance criteria
            - Behavior matches the task description; tests cover the new/changed behavior.
            - No regressions in the relevant test suites; build is clean.

            ## Risks
            - Concurrency around `TerminalBuffer.Lock` (re-entrancy: see docs/MODULE_OWNERSHIP.md).
            - Hostile-input robustness (the VT layer ingests untrusted byte streams).
            - Cross-platform differences (Windows ConPTY vs Unix PTY; rendering backends).
            """);

        return sb.ToString().TrimEnd();
    }

    // Topic keyword → concrete files worth opening for that area.
    private static readonly (string[] Keywords, string[] Files)[] FileHints =
    {
        (new[] { "reflow", "resize", "wrap" }, new[] {
            "src/NovaTerminal.VT/TerminalBuffer.ReflowEngine.cs",
            "src/NovaTerminal.VT/TerminalBuffer.ResizeAndReflow.cs",
            "tests/NovaTerminal.App.Tests/ReflowScenariosTests.cs",
            "tests/NovaTerminal.VT.Tests/ReflowEdgeCaseTests.cs" }),
        (new[] { "scrollback" }, new[] {
            "src/NovaTerminal.VT/Buffer/ScrollbackPages.cs",
            "src/NovaTerminal.VT/TerminalBuffer.ReflowEngine.cs" }),
        (new[] { "parser", "escape", "ansi", "csi", "osc", "dcs", "apc", "sequence" }, new[] {
            "src/NovaTerminal.VT/AnsiParser.cs",
            "tests/NovaTerminal.App.Tests/AnsiParserHardeningTests.cs",
            "tests/NovaTerminal.VT.Tests/CsiParamClampTests.cs" }),
        (new[] { "theme", "color", "palette" }, new[] {
            "src/NovaTerminal.VT/TerminalTheme.cs",
            "src/NovaTerminal.App/Shell/ThemeManager.cs",
            "docs/ThemeSystem.md" }),
        (new[] { "glyph", "atlas", "render", "draw", "paint" }, new[] {
            "src/NovaTerminal.Rendering/GlyphCache.cs",
            "src/NovaTerminal.Rendering/GlyphAtlas.cs",
            "src/NovaTerminal.App/Shell/TerminalDrawOperation.cs",
            "src/NovaTerminal.App/Shell/TerminalView.cs" }),
        (new[] { "pty", "spawn", "process" }, new[] {
            "src/NovaTerminal.Pty/RustPtySession.cs" }),
        (new[] { "ssh", "key", "auth", "connection", "jump" }, new[] {
            "src/NovaTerminal.App/Shell/TerminalProfile.cs",
            "src/NovaTerminal.Platform/Ssh/",
            "docs/SSH_ROADMAP.md" }),
        (new[] { "sftp", "transfer", "upload", "download" }, new[] {
            "src/NovaTerminal.App/Shell/SftpService.cs" }),
        (new[] { "log", "logging", "logger" }, new[] {
            "src/NovaTerminal.VT/TerminalLogger.cs" }),
        (new[] { "tab", "pane", "window", "layout" }, new[] {
            "src/NovaTerminal.App/MainWindow.axaml.cs",
            "src/NovaTerminal.App/Controls/TerminalPane.axaml.cs" }),
        (new[] { "fuzz", "robustness" }, new[] {
            "tests/NovaTerminal.Benchmarks/FuzzTarget.cs",
            "tests/NovaTerminal.VT.Tests/FuzzSmokeTests.cs" }),
    };

    [McpServerTool(Name = "novaterminal.suggest_relevant_files"),
     Description("Given a topic or task description, suggests the concrete NovaTerminal source/test files most relevant to it (e.g. 'reflow', 'OSC 8 hyperlinks', 'theme validation'). Start here to find where to work.")]
    public static string SuggestRelevantFiles(
        [Description("The topic or task, e.g. 'reflow edge cases', 'glyph atlas', 'ssh key auth'.")] string topic)
    {
        topic ??= string.Empty;
        string haystack = topic.ToLowerInvariant();

        var files = FileHints
            .Where(h => h.Keywords.Any(k => haystack.Contains(k, System.StringComparison.Ordinal)))
            .SelectMany(h => h.Files)
            .Distinct()
            .ToList();

        if (files.Count == 0)
        {
            return "No direct file mapping for that topic. Call novaterminal.get_architecture_map " +
                   "to find the owning assembly, then novaterminal.list_docs / read_doc.";
        }

        var sb = new StringBuilder();
        sb.Append("Relevant files for \"").Append(topic.Trim()).Append("\":\n");
        foreach (var f in files) sb.Append("- ").Append(f).Append('\n');
        return sb.ToString().TrimEnd();
    }
}
