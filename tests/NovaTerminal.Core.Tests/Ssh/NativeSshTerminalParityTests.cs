using System.Text;
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
}
