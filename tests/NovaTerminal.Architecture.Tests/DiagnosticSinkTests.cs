using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NovaTerminal.Pty;
using NovaTerminal.VT;

namespace NovaTerminal.Architecture.Tests;

/// <summary>
/// #109: diagnostics in the GUI and library layers used to go to <c>Console.WriteLine</c>. A Windows GUI
/// process has no console attached, so those messages — spawn parameters, read-loop failures, join
/// timeouts, lost input on a short write, theme load errors — were written to nothing. They read as
/// logging and behaved like comments.
///
/// A source scan rather than an IL check, because <c>Console.WriteLine</c> resolves to a BCL call that
/// carries no marker distinguishing "console tool printing its output" from "GUI code losing a
/// diagnostic". The distinction is which project it lives in, which only the source tells us.
/// </summary>
public class DiagnosticSinkTests
{
    /// <summary>
    /// Projects whose product *is* stdout. Console writes here are correct and must not be "fixed".
    /// </summary>
    private static readonly string[] ConsoleToolProjects =
    [
        "NovaTerminal.Cli",
        "NovaTerminal.Conformance",
    ];

    /// <summary>
    /// The single file allowed to name <c>Console</c> outside the console tools.
    /// </summary>
    /// <remarks>
    /// <c>PtyLogger</c> *is* the destination decision. The rule this test enforces is that call sites do
    /// not each pick a destination; a logging sink's documented default is where that choice belongs, and
    /// its default has to be the console so that hosts which have one keep seeing PTY diagnostics.
    /// Deliberately a one-element list, and <see cref="Only_PtyLogger_is_exempt_from_the_console_rule"/>
    /// keeps it that way.
    /// </remarks>
    private static readonly string[] SinkImplementationFiles =
    [
        "PtyLogger.cs",
    ];

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "NovaTerminal.sln")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repository root from test output path.");
    }

    [Fact]
    public void Gui_and_library_code_must_not_write_diagnostics_to_the_console()
    {
        string src = Path.Combine(RepoRoot(), "src");
        var offenders = new List<string>();

        foreach (string file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            if (ConsoleToolProjects.Any(p =>
                    file.Contains($"{Path.DirectorySeparatorChar}{p}{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
            {
                continue;
            }

            if (SinkImplementationFiles.Contains(Path.GetFileName(file), StringComparer.Ordinal))
            {
                continue;
            }

            string text = File.ReadAllText(file);

            // Console.Out / Console.Error *as values* are fine: Program.cs hands them to the CLI
            // commands, which is how a GUI executable serves `--replay` and friends. What must not
            // appear is a write performed by GUI or library code itself.
            foreach (Match match in Regex.Matches(text, @"Console\s*\.\s*(WriteLine|Write)\s*\(", RegexOptions.None, TimeSpan.FromSeconds(5)))
            {
                int line = text.Take(match.Index).Count(c => c == '\n') + 1;
                offenders.Add($"{Path.GetRelativePath(RepoRoot(), file)}:{line}");
            }

            foreach (Match match in Regex.Matches(text, @"Console\s*\.\s*Error\s*\.\s*(WriteLine|Write)\s*\(", RegexOptions.None, TimeSpan.FromSeconds(5)))
            {
                int line = text.Take(match.Index).Count(c => c == '\n') + 1;
                offenders.Add($"{Path.GetRelativePath(RepoRoot(), file)}:{line}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Diagnostics written to the console are lost in a GUI process (#109). Use TerminalLogger, "
            + "or PtyLogger in NovaTerminal.Pty which may not reference VT. Offenders:\n  "
            + string.Join("\n  ", offenders.Distinct()));
    }

    [Fact]
    public void Console_tool_projects_are_still_allowed_to_print()
    {
        // Guards the guard: if the exclusion above stopped matching - a project rename, a path-separator
        // slip - the test would silently police nothing in those projects while appearing to pass.
        // NovaTerminal.Conformance prints its report to stdout, so it must contain console writes.
        string conformance = Path.Combine(RepoRoot(), "src", "NovaTerminal.Conformance");
        int writes = Directory.EnumerateFiles(conformance, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Sum(f => Regex.Count(File.ReadAllText(f), @"Console\s*\.", RegexOptions.None, TimeSpan.FromSeconds(5)));

        Assert.True(writes > 0, "expected the conformance tool to print to stdout; the exclusion list may be stale");
    }

    [Fact]
    public void Only_PtyLogger_is_exempt_from_the_console_rule()
    {
        // An exemption list is how a guard like this rots. One entry, asserted.
        Assert.Equal(["PtyLogger.cs"], SinkImplementationFiles);
    }

    [Fact]
    public void PtyLogger_has_a_sink_by_default()
    {
        // Review of #244: a null default would have fixed the GUI while making every other consumer
        // worse, since PTY diagnostics in the test host previously reached a real console. The GUI
        // replaces this before creating any session.
        Assert.NotNull(PtyLogger.Sink);
    }

    [Fact]
    public void PtyLogLevels_match_app_log_levels()
    {
        // Program.ToLogLevel maps between these by name rather than by cast, but the mapping is only
        // meaningful while the two enums agree. If a member is inserted into one, this fails here rather
        // than silently mislevelling every PTY message.
        Assert.Equal(
            Enum.GetNames<LogLevel>(),
            Enum.GetNames<PtyLogLevel>());
    }
}
