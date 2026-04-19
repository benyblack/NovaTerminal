using System.Text;
using NovaTerminal.Core.Replay;
using NovaTerminal.Core.Ssh.Models;
using NovaTerminal.Core.Ssh.Native;
using NovaTerminal.Core.Ssh.Sessions;

namespace NovaTerminal.Core.Tests.Ssh;

public sealed class NativeSshTerminalParityTests
{
    [Fact]
    public async Task ResizeFailure_DoesNotThrow_AndFullscreenExitStillRestoresPrompt()
    {
        var interop = new NativeSshSessionTests.FakeNativeSshInterop
        {
            ResizeException = new InvalidOperationException("resize exploded")
        };

        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer);
        var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var session = new NativeSshSession(CreateProfile(), interop: interop);
        session.OnOutputReceived += parser.Process;
        session.OnExit += code => exit.TrySetResult(code);

        Exception? resizeException = Record.Exception(() => session.Resize(100, 40));

        interop.Enqueue(NativeSshEvent.Data(Encoding.UTF8.GetBytes("\u001b[?104")));
        interop.Enqueue(NativeSshEvent.Data(Encoding.UTF8.GetBytes("9h")));
        interop.Enqueue(NativeSshEvent.Data(Encoding.UTF8.GetBytes("ALT SCREEN")));
        interop.Enqueue(NativeSshEvent.Data(Encoding.UTF8.GetBytes("\u001b[?104")));
        interop.Enqueue(NativeSshEvent.Data(Encoding.UTF8.GetBytes("9l\r\nnova$ ")));
        interop.Enqueue(NativeSshEvent.ExitStatus(0));
        interop.Enqueue(NativeSshEvent.Closed(Array.Empty<byte>()));

        Assert.Null(resizeException);
        Assert.Equal(0, await exit.Task.WaitAsync(TimeSpan.FromSeconds(2)));

        string viewportText = GetViewportText(buffer);
        Assert.Contains("nova$", viewportText, StringComparison.Ordinal);
        Assert.DoesNotContain("ALT SCREEN", viewportText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChunkedAlternateScreenSequences_DoNotPolluteScrollback()
    {
        var interop = new NativeSshSessionTests.FakeNativeSshInterop();
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer);
        var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        for (int i = 0; i < 40; i++)
        {
            parser.Process($"History {i}\n");
        }

        int initialScrollbackCount = buffer.Scrollback.Count;

        using var session = new NativeSshSession(CreateProfile(), interop: interop);
        session.OnOutputReceived += parser.Process;
        session.OnExit += code => exit.TrySetResult(code);

        interop.Enqueue(NativeSshEvent.Data(Encoding.UTF8.GetBytes("\u001b[?10")));
        interop.Enqueue(NativeSshEvent.Data(Encoding.UTF8.GetBytes("49h")));
        interop.Enqueue(NativeSshEvent.Data(Encoding.UTF8.GetBytes("mc alt ui")));
        interop.Enqueue(NativeSshEvent.Data(Encoding.UTF8.GetBytes("\u001b[?10")));
        interop.Enqueue(NativeSshEvent.Data(Encoding.UTF8.GetBytes("49l")));
        interop.Enqueue(NativeSshEvent.ExitStatus(0));
        interop.Enqueue(NativeSshEvent.Closed(Array.Empty<byte>()));

        Assert.Equal(0, await exit.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(initialScrollbackCount, buffer.Scrollback.Count);
        Assert.DoesNotContain("mc alt ui", GetScrollbackText(buffer), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostCommandPromptReturn_DoesNotAddExtraBlankLine()
    {
        var interop = new NativeSshSessionTests.FakeNativeSshInterop();
        var buffer = new TerminalBuffer(20, 5);
        var parser = new AnsiParser(buffer);
        var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var session = new NativeSshSession(CreateProfile(), interop: interop);
        session.OnOutputReceived += parser.Process;
        session.OnExit += code => exit.TrySetResult(code);

        interop.Enqueue(NativeSshEvent.Data(Encoding.UTF8.GetBytes("echo hi\r\n")));
        interop.Enqueue(NativeSshEvent.Data(Encoding.UTF8.GetBytes("hi\r\n")));
        interop.Enqueue(NativeSshEvent.Data(Encoding.UTF8.GetBytes("nova$ ")));
        interop.Enqueue(NativeSshEvent.ExitStatus(0));
        interop.Enqueue(NativeSshEvent.Closed(Array.Empty<byte>()));

        Assert.Equal(0, await exit.Task.WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Equal(new[] { "echo hi", "hi", "nova$" }, GetVisibleNonEmptyLines(buffer));
    }

    [Fact]
    public async Task VimStyleDownwardScroll_RespectsScrollRegion_AndAdvancesVisibleWindow()
    {
        var interop = new NativeSshSessionTests.FakeNativeSshInterop();
        var buffer = new TerminalBuffer(20, 10);
        var parser = new AnsiParser(buffer);
        var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var session = new NativeSshSession(CreateProfile(), interop: interop);
        session.OnOutputReceived += parser.Process;
        session.OnExit += code => exit.TrySetResult(code);

        interop.Enqueue(NativeSshEvent.Data(Encoding.UTF8.GetBytes(BuildVimScrollSequence())));
        interop.Enqueue(NativeSshEvent.ExitStatus(0));
        interop.Enqueue(NativeSshEvent.Closed(Array.Empty<byte>()));

        Assert.Equal(0, await exit.Task.WaitAsync(TimeSpan.FromSeconds(2)));

        BufferSnapshot snapshot = BufferSnapshot.Capture(buffer);
        Assert.Equal("line-02", snapshot.Lines[0]);
        Assert.Equal("line-09", snapshot.Lines[7]);
        Assert.Equal("line-10", snapshot.Lines[8]);
        Assert.Equal("status: stable", snapshot.Lines[9]);
    }

    [Fact]
    public async Task VimStyleDownwardScrollInAltScreen_AdvancesVisibleEditingRows()
    {
        var interop = new NativeSshSessionTests.FakeNativeSshInterop();
        var buffer = new TerminalBuffer(20, 10);
        var parser = new AnsiParser(buffer);
        var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var session = new NativeSshSession(CreateProfile(), interop: interop);
        session.OnOutputReceived += parser.Process;
        session.OnExit += code => exit.TrySetResult(code);

        interop.Enqueue(NativeSshEvent.Data(Encoding.UTF8.GetBytes("\u001b[?104")));
        interop.Enqueue(NativeSshEvent.Data(Encoding.UTF8.GetBytes("9h\u001b[2J\u001b[H")));
        interop.Enqueue(NativeSshEvent.Data(Encoding.UTF8.GetBytes(
            "\u001b[1;9r" +
            "\u001b[1;1Hfile 01" +
            "\u001b[2;1Hfile 02" +
            "\u001b[3;1Hfile 03" +
            "\u001b[4;1Hfile 04" +
            "\u001b[5;1Hfile 05" +
            "\u001b[6;1Hfile 06" +
            "\u001b[7;1Hfile 07" +
            "\u001b[8;1Hfile 08" +
            "\u001b[9;1Hfile 09")));
        interop.Enqueue(NativeSshEvent.Data(Encoding.UTF8.GetBytes(
            "\u001b[10;1Hstatus 09" +
            "\u001b[1;1H\u001b[M" +
            "\u001b[9;1Hfile 10\u001b[K" +
            "\u001b[10;1Hstatus 10\u001b[K")));
        interop.Enqueue(NativeSshEvent.ExitStatus(0));
        interop.Enqueue(NativeSshEvent.Closed(Array.Empty<byte>()));

        Assert.Equal(0, await exit.Task.WaitAsync(TimeSpan.FromSeconds(2)));

        string[] visibleLines = buffer.ViewportRows.Select(GetRowText).ToArray();

        Assert.Equal("file 02", visibleLines[0]);
        Assert.Equal("file 03", visibleLines[1]);
        Assert.Equal("file 04", visibleLines[2]);
        Assert.Equal("file 05", visibleLines[3]);
        Assert.Equal("file 06", visibleLines[4]);
        Assert.Equal("file 07", visibleLines[5]);
        Assert.Equal("file 08", visibleLines[6]);
        Assert.Equal("file 09", visibleLines[7]);
        Assert.Equal("file 10", visibleLines[8]);
        Assert.Equal("status 10", visibleLines[9]);
    }

    private static SshProfile CreateProfile()
    {
        return new SshProfile
        {
            Id = Guid.Parse("72fffbaf-496f-4fc4-9e48-a1e8dc5242a2"),
            BackendKind = SshBackendKind.Native,
            Host = "native.example",
            User = "nova",
            Port = 22
        };
    }

    private static string GetViewportText(TerminalBuffer buffer)
    {
        return string.Join(
            "\n",
            buffer.ViewportRows.Select(GetRowText));
    }

    private static string GetScrollbackText(TerminalBuffer buffer)
    {
        var lines = new List<string>(buffer.Scrollback.Count);
        for (int i = 0; i < buffer.Scrollback.Count; i++)
        {
            lines.Add(GetSpanText(buffer.Scrollback.GetRow(i)));
        }

        return string.Join("\n", lines);
    }

    private static IReadOnlyList<string> GetVisibleNonEmptyLines(TerminalBuffer buffer)
    {
        return buffer.ViewportRows
            .Select(GetRowText)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
    }

    private static string GetRowText(TerminalRow row)
    {
        if (row.Cells == null)
        {
            return string.Empty;
        }

        return GetSpanText(row.Cells);
    }

    private static string GetSpanText(ReadOnlySpan<TerminalCell> cells)
    {
        char[] chars = new char[cells.Length];
        for (int i = 0; i < cells.Length; i++)
        {
            chars[i] = cells[i].Character == '\0' ? ' ' : cells[i].Character;
        }

        return new string(chars).TrimEnd();
    }

    private static string BuildVimScrollSequence()
    {
        var sb = new StringBuilder();
        sb.Append("\u001b[?1049h");
        for (int row = 1; row <= 9; row++)
        {
            sb.Append($"\u001b[{row};1Hline-{row:00}");
        }

        sb.Append("\u001b[10;1Hstatus: stable");

        // Vim keeps the status row outside the scrolling region and scrolls the editing
        // window before the cursor reaches the bottom when scrolloff is nonzero.
        sb.Append("\u001b[1;9r");
        sb.Append("\u001b[9;1H");
        sb.Append("\r\n");
        sb.Append("line-10\u001b[K");
        return sb.ToString();
    }
}
