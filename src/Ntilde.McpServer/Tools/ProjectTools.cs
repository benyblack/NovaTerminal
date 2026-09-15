using System.ComponentModel;
using System.Linq;
using ModelContextProtocol.Server;

namespace Ntilde.McpServer.Tools;

[McpServerToolType]
public static class ProjectTools
{
    [McpServerTool(Name = "ntilde.get_project_summary"),
     Description("Returns a high-level summary of the Ntilde project: what it is, its tech stack, and its assembly/module layout. Use this first to orient before working in the codebase.")]
    public static string GetProjectSummary() =>
        """
        # Ntilde — project summary

        A cross-platform terminal emulator built on .NET 10 and Avalonia (UI) with SkiaSharp
        (glyph/atlas rendering). It uses a custom, correctness-first VT/ANSI engine and a Rust
        native PTY backend.

        ## Tech stack
        - .NET 10, C#
        - Avalonia 12 (UI), SkiaSharp 3 (rendering)
        - Rust native PTY (`librusty_pty`) via P/Invoke
        - xUnit v3 tests; BenchmarkDotNet + SharpFuzz for perf/fuzzing

        ## Assemblies (leaf → app)
        - **Ntilde.VT** — VT/ANSI state machine, screen buffers, scrollback, lossless
          reflow, cell/grapheme/Unicode width. Leaf (BCL only).
        - **Ntilde.Replay** — record/replay of byte streams; golden-master harness. (→ VT)
        - **Ntilde.Pty** — PTY session abstraction over the Rust backend.
        - **Ntilde.Rendering** — Skia glyph cache/atlas, render snapshots, statistics.
        - **Ntilde.Platform** — OS/SSH integration (native + external OpenSSH).
        - **Ntilde.App** — Avalonia UI, panes/tabs, command assist, SFTP, theming.
        - **Ntilde.Conformance / .Cli** — VT conformance reporting and CLI tooling.
        - **Ntilde.McpServer** — this read-only MCP Dev Companion.

        ## Key conventions
        - Build via `scripts/build.{ps1,sh}` (not raw `dotnet`) — see CLAUDE.md.
        - All `TerminalBuffer` reads require holding `TerminalBuffer.Lock`.
        - Lossless reflow is a headline invariant — resize never silently drops content.
        - For authoritative module boundaries/invariants, call `ntilde.get_architecture_map`.
        """;

    [McpServerTool(Name = "ntilde.get_architecture_map"),
     Description("Returns the authoritative module-ownership / architecture map (docs/MODULE_OWNERSHIP.md): each assembly's namespace, dependencies, owned responsibilities, and enforced invariants.")]
    public static string GetArchitectureMap(RepoContext repo)
    {
        if (repo.TryReadDoc("MODULE_OWNERSHIP.md", out var content, out var error))
        {
            return content;
        }
        return $"Architecture map unavailable: {error}";
    }

    [McpServerTool(Name = "ntilde.list_docs"),
     Description("Lists the Markdown documents available under the repository's docs/ directory (paths are relative to docs/). Use with ntilde.read_doc.")]
    public static string ListDocs(RepoContext repo)
    {
        var docs = repo.ListDocs();
        if (docs.Count == 0)
        {
            return "No docs found (repository docs/ directory could not be located — set NTILDE_REPO_ROOT).";
        }
        return string.Join("\n", docs.Select(d => "- " + d));
    }

    [McpServerTool(Name = "ntilde.read_doc"),
     Description("Reads a Markdown document from the repository's docs/ directory by its path relative to docs/ (e.g. 'ThemeSystem.md'). Reads are confined to docs/ — paths outside it are rejected.")]
    public static string ReadDoc(
        RepoContext repo,
        [Description("Path of the document relative to docs/, e.g. 'ThemeSystem.md' or 'plans/foo.md'.")] string path)
    {
        return repo.TryReadDoc(path, out var content, out var error) ? content : $"Error: {error}";
    }
}
