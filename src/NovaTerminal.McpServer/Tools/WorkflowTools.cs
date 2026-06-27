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
}
