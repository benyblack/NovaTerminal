using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using NovaTerminal.Pty;
using NovaTerminal.VT;
using Xunit;

namespace NovaTerminal.Tests;

/// <summary>
/// The empirical foundation under Command Assist's replace-on-accept arithmetic: <c>DEL</c>
/// (<c>0x7f</c>) deletes exactly one character, and once the input line is empty further deletes do
/// nothing at all.
/// </summary>
/// <remarks>
/// <para>
/// <c>CommandAssistInsertionPlanner</c> counts backspaces in UTF-16 code units, which is an
/// <em>upper bound</em> on the number of backward-deletes any line editor needs - readline, zle and
/// fish delete a codepoint at a time, PSReadLine a .NET <c>char</c>. The safety argument for using an
/// upper bound is that the overshoot is absorbed at the start of the input buffer. That argument is
/// load-bearing (an undershoot would leave debris the inserted command is appended to), so it is
/// established here by measurement rather than by assertion, through the product's own PTY layer and
/// its own parser and grid.
/// </para>
/// <para>
/// <strong>Each case is a round trip, and that is what stops it passing vacuously.</strong> Type
/// three characters, wait until the grid shows them, then send <em>five</em> deletes and require the
/// grid to come back to exactly the prompt it started from. The typing leg proves the write actually
/// reached the shell and the shell is alive and echoing - without it, a session that died after
/// painting its prompt, or a <c>SendInput</c> that dropped the write (it swallows the two
/// shutdown-race exceptions by design), would leave before and after trivially equal and the test
/// green having measured nothing. The delete leg then proves three things at once: <c>0x7f</c> is the
/// delete byte, it removes one character per byte, and the two surplus deletes are absorbed rather
/// than eating the prompt.
/// </para>
/// <para>
/// The final assertion reads the whole cursor row, not just the text behind the cursor. Behind the
/// cursor alone cannot tell "deleted three characters" from "moved the cursor three columns left",
/// which is exactly the vi-command-mode behaviour the planner records as an accepted risk; requiring
/// the probe string to be gone from the row distinguishes them.
/// </para>
/// <para>
/// <strong>Coverage, stated so a green run is not over-read.</strong> This class runs on the gating
/// PtySmoke job on both <c>windows-latest</c> and <c>ubuntu-latest</c>, so pwsh 7 and Windows
/// PowerShell are measured on the Windows runner and bash on the Linux one. On a Windows box bash is
/// reached through WSL if a distribution is installed - a maintainer convenience, not CI coverage,
/// since the Windows runner has no distribution and reports that case as an explicit skip. zsh and
/// fish are not measured anywhere: the code's claim about them rests on zle and fish documentation.
/// Nothing here is ever submitted, so no shell writes a history entry.
/// </para>
/// </remarks>
[Collection(PtyRealShellCollection.Name)]
public class PtyBackspaceAtLineStartTests
{
    /// <summary>
    /// Three characters, deleted by five bytes: the surplus is the point. Deliberately a string no
    /// prompt, path or banner would contain, because the final assertion is that it is gone.
    /// </summary>
    private const string Probe = "zqj";

    private const string FiveDeletes = "\u007f\u007f\u007f\u007f\u007f";

    /// <summary>Enough to cover any prompt these shells draw on one row, and less than a row.</summary>
    private const int PromptReadLength = 60;

    private const int PollIntervalMs = 250;
    private const int PollAttempts = 60;

    [Fact]
    [Trait("Category", "PtySmoke")]
    public async Task InPwsh7_TypingThenFiveDeletesLeavesThePromptUntouched()
    {
        string? shell = FindFirstExisting(
            @"C:\Program Files\PowerShell\7\pwsh.exe",
            @"C:\Program Files\PowerShell\7-preview\pwsh.exe");

        await AssertTypeThenDeleteRoundTripAsync(shell, args: "-NoLogo -NoProfile", label: "pwsh 7");
    }

    [Fact]
    [Trait("Category", "PtySmoke")]
    public async Task InWindowsPowerShell_TypingThenFiveDeletesLeavesThePromptUntouched()
    {
        // PSReadLine 2.0 rather than 2.3, which is a different renderer and has already produced one
        // Command Assist bug the newer one did not (the right-prompt trim, PR #301).
        string? shell = FindFirstExisting(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe"));

        await AssertTypeThenDeleteRoundTripAsync(shell, args: "-NoLogo -NoProfile", label: "Windows PowerShell");
    }

    /// <summary>
    /// bash/readline in emacs mode: natively on the Linux runner, and through WSL on a Windows box
    /// that has a distribution. <c>--norc</c> keeps the user's own configuration - and their
    /// <c>INPUTRC</c> - out of it, so what is measured is stock readline.
    /// </summary>
    [Fact]
    [Trait("Category", "PtySmoke")]
    public async Task InBash_TypingThenFiveDeletesLeavesThePromptUntouched()
    {
        if (!OperatingSystem.IsWindows())
        {
            await AssertTypeThenDeleteRoundTripAsync(
                FindFirstExisting("/bin/bash", "/usr/bin/bash"),
                args: "--norc -i",
                label: "bash");
            return;
        }

        await AssertTypeThenDeleteRoundTripAsync(
            IsWslUsable() ? "wsl.exe" : null,
            args: "-e /bin/bash --norc -i",
            label: "bash (WSL)");
    }

    private static async Task AssertTypeThenDeleteRoundTripAsync(string? shell, string? args, string label)
    {
        if (shell is null)
        {
            // An explicit, visible skip rather than a silent return: this class gates CI on two
            // runners, and a case that quietly measures nothing must still say so in the results.
            Assert.Skip($"{label} is not available on this machine; nothing was measured.");
            return;
        }

        var buffer = new TerminalBuffer(80, 24);

        using var session = new RustPtySession(
            shell, 80, 24, args, cwd: null, skipPowerShellPostLaunchInit: true);

        // OnResponse is not optional here. ConPTY opens with a cursor-position report request
        // (ESC [ 6 n) and the shell does not draw its first prompt until the terminal answers, so a
        // parser wired without it sits at a blank grid forever. Production wires exactly this, which
        // is also why an insertion has to share the session's writer with the parse thread.
        var parser = new AnsiParser(buffer)
        {
            OnResponse = session.SendInput
        };

        // The parse runs on the session's own output thread, exactly as it does in a live pane; the
        // buffer's reader/writer lock is what makes the reads below safe against it.
        session.OnOutputReceived += text => parser.Process(text);

        CursorContext before = await WaitForASettledPromptAsync(buffer, label);

        // Leg one: prove the write path works and the shell is echoing. If this times out the failure
        // names the real cause instead of surfacing later as a meaningless prompt diff.
        session.SendInput(Probe);
        await WaitForAsync(
            buffer,
            context => context.TextBeforeCursor == before.TextBeforeCursor + Probe,
            $"'{label}' never echoed the typed probe '{Probe}'. The shell may have exited, or the " +
            $"write was dropped. Prompt before typing was '{Describe(before.TextBeforeCursor)}'");

        // Leg two: five deletes for three characters. Polling for the return to `before` rather than
        // sleeping a fixed period - a no-op produces no output in some shells and a bell in others, so
        // there is nothing to wait for except the state we are asserting, and on a fast box this costs
        // one poll interval instead of two seconds on every CI run.
        session.SendInput(FiveDeletes);
        CursorContext after = await WaitForAsync(
            buffer,
            context => context == before,
            $"'{label}' did not return to its original prompt after {FiveDeletes.Length} deletes over " +
            $"{Probe.Length} typed characters. Expected '{Describe(before.TextBeforeCursor)}' at " +
            $"({before.Row},{before.Col})");

        Assert.Equal(before, after);

        // Behind the cursor is not enough on its own: an editor that moved the cursor left rather than
        // deleting would produce the same text behind it and leave the probe sitting past it.
        Assert.DoesNotContain(Probe, ReadCursorRowText(buffer), StringComparison.Ordinal);
    }

    /// <summary>
    /// Polls until the text ending at the cursor stops changing, so the measurement is not taken
    /// mid-banner or mid-repaint.
    /// </summary>
    /// <remarks>
    /// Fails at the settle point rather than returning whatever it last saw. This class runs on a
    /// gating job with no <c>continue-on-error</c>, and on a cold runner still repainting at the cap
    /// the old behaviour surfaced as a prompt diff that said nothing about the real cause.
    /// </remarks>
    private static async Task<CursorContext> WaitForASettledPromptAsync(TerminalBuffer buffer, string label)
    {
        CursorContext previous = default;

        for (int i = 0; i < PollAttempts; i++)
        {
            await Task.Delay(PollIntervalMs);
            CursorContext current = ReadCursorContext(buffer);

            if (!string.IsNullOrWhiteSpace(current.TextBeforeCursor) && current == previous)
            {
                return current;
            }

            previous = current;
        }

        Assert.Fail(
            $"'{label}' never settled on a prompt within {PollAttempts * PollIntervalMs / 1000}s. " +
            $"Last read was '{Describe(previous.TextBeforeCursor)}' at ({previous.Row},{previous.Col}).");
        return default;
    }

    private static async Task<CursorContext> WaitForAsync(
        TerminalBuffer buffer,
        Func<CursorContext, bool> predicate,
        string failureMessage)
    {
        CursorContext current = default;

        for (int i = 0; i < PollAttempts; i++)
        {
            await Task.Delay(PollIntervalMs);
            current = ReadCursorContext(buffer);
            if (predicate(current))
            {
                return current;
            }
        }

        Assert.Fail(
            $"{failureMessage}, but after {PollAttempts * PollIntervalMs / 1000}s the grid read " +
            $"'{Describe(current.TextBeforeCursor)}' at ({current.Row},{current.Col}).");
        return default;
    }

    private static CursorContext ReadCursorContext(TerminalBuffer buffer)
    {
        buffer.Lock.EnterReadLock();
        try
        {
            GridQueryReader.TryReadTextEndingAtCursor(buffer, PromptReadLength, out string text);
            return new CursorContext(text, buffer.CursorRow, buffer.CursorCol);
        }
        finally
        {
            buffer.Lock.ExitReadLock();
        }
    }

    /// <summary>The whole row the cursor is on, including anything painted past the cursor.</summary>
    private static string ReadCursorRowText(TerminalBuffer buffer)
    {
        buffer.Lock.EnterReadLock();
        try
        {
            int row = buffer.Scrollback.Count + buffer.CursorRow;
            var text = new StringBuilder(buffer.Cols);
            for (int col = 0; col < buffer.Cols; col++)
            {
                string grapheme = buffer.GetGraphemeAbsolute(col, row);
                text.Append(string.IsNullOrEmpty(grapheme) || grapheme == "\0" ? " " : grapheme);
            }

            return text.ToString();
        }
        finally
        {
            buffer.Lock.ExitReadLock();
        }
    }

    /// <summary>Renders a grid read for a failure message without letting control bytes garble it.</summary>
    private static string Describe(string? text) =>
        (text ?? string.Empty).Replace("\u001b", "<ESC>", StringComparison.Ordinal);

    private static string? FindFirstExisting(params string[] candidates)
    {
        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether <c>wsl.exe</c> can actually run something. The executable ships with Windows whether or
    /// not a distribution is installed, so its presence proves nothing and the Windows CI runner would
    /// otherwise spawn a session that only ever prints an installation notice.
    /// </summary>
    private static bool IsWslUsable()
    {
        Process? probe = null;
        try
        {
            probe = Process.Start(new ProcessStartInfo("wsl.exe", "-e /bin/true")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });

            if (probe is null)
            {
                return false;
            }

            if (!probe.WaitForExit(10_000))
            {
                // Disposing the Process object does not stop the process. A probe that hung would
                // otherwise leave wsl.exe running for the life of the agent, and this is the only
                // place in this file that starts something outside the PTY layer's own lifecycle.
                TryKill(probe);
                return false;
            }

            return probe.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            probe?.Dispose();
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Already gone, or not ours to kill. Nothing useful to do either way.
        }
    }

    /// <summary>
    /// What the grid says about the input line: the text ending at the cursor, and where the cursor
    /// is. Value equality is the whole assertion - "the prompt came back and the caret came back".
    /// </summary>
    /// <remarks>
    /// The row past the cursor is deliberately not part of this. A shell's inline prediction lives
    /// there and repaints on its own schedule, which would make the settle loop and the equality
    /// check flaky; the one thing that has to be checked past the cursor is checked separately and
    /// by content (<see cref="Probe"/> is gone), which prediction noise cannot forge.
    /// </remarks>
    private readonly record struct CursorContext(string TextBeforeCursor, int Row, int Col);
}
