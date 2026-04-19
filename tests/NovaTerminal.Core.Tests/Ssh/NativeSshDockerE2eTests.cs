using NovaTerminal.Core.Replay;
using NovaTerminal.Core.Ssh.Models;
using NovaTerminal.Core.Ssh.Sessions;
using NovaTerminal.Core.Tests.Infra;
using System.Text;

namespace NovaTerminal.Core.Tests.Ssh;

public sealed class NativeSshDockerE2eTests
{
    [DockerFact]
    [Trait("Category", "DockerE2E")]
    [Trait("Target", "NativeSsh")]
    public async Task NativeSsh_CanAuthenticate_RunCommand_AndReturnToPrompt()
    {
        await using var fixture = await DockerSshFixture.StartAsync();

        var buffer = new TerminalBuffer(120, 30);
        var parser = new AnsiParser(buffer);
        var handler = new NativeSshTestInteractionHandler(fixture.Password);
        var logs = new List<string>();

        using var session = new NativeSshSession(
            CreateProfile(fixture),
            cols: 120,
            rows: 30,
            log: logs.Add,
            interactionHandler: handler);

        session.AttachBuffer(buffer);
        session.OnOutputReceived += parser.Process;

        await WaitUntilAsync(() => SnapshotContains(buffer, "nova$"), TimeSpan.FromSeconds(20), "initial prompt");

        session.SendInput("printf 'hello\\n'\n");

        await WaitUntilAsync(
            () => SnapshotContains(buffer, "hello") && SnapshotContainsExactLine(buffer, "nova$"),
            TimeSpan.FromSeconds(20),
            "command output and prompt return");

        BufferSnapshot snapshot = BufferSnapshot.Capture(buffer);

        Assert.Contains(snapshot.Lines, line => line.Contains("printf 'hello\\n'", StringComparison.Ordinal));
        Assert.Contains(snapshot.Lines, line => line == "hello");
        Assert.Contains(snapshot.Lines, line => line == "nova$");
        Assert.Contains(handler.Requests, request =>
            request.Kind == NovaTerminal.Core.Ssh.Interactions.SshInteractionKind.UnknownHostKey
            || request.Kind == NovaTerminal.Core.Ssh.Interactions.SshInteractionKind.ChangedHostKey);
        Assert.Contains(handler.Requests, request => request.Kind == NovaTerminal.Core.Ssh.Interactions.SshInteractionKind.Password);
    }

    [DockerFact]
    [Trait("Category", "DockerE2E")]
    [Trait("Target", "NativeSsh")]
    public async Task NativeSsh_CanExitAlternateScreen_AndReturnToPrompt()
    {
        await using var fixture = await DockerSshFixture.StartAsync();

        var buffer = new TerminalBuffer(120, 30);
        var parser = new AnsiParser(buffer);
        var handler = new NativeSshTestInteractionHandler(fixture.Password);
        var logs = new List<string>();
        const string altMarker = "ALT SCREEN LIVE";

        using var session = new NativeSshSession(
            CreateProfile(fixture),
            cols: 120,
            rows: 30,
            log: logs.Add,
            interactionHandler: handler);

        session.AttachBuffer(buffer);
        session.OnOutputReceived += parser.Process;

        await WaitUntilAsync(() => SnapshotContainsExactLine(buffer, "nova$"), TimeSpan.FromSeconds(20), "initial prompt");

        session.SendInput($"printf '{BuildAltScreenPayload(altMarker)}'\n");

        await WaitUntilAsync(
            () => SnapshotContainsExactLine(buffer, "nova$"),
            TimeSpan.FromSeconds(20),
            "prompt after alternate screen exit");

        BufferSnapshot snapshot = BufferSnapshot.Capture(buffer);

        Assert.False(snapshot.IsAltScreen);
        Assert.DoesNotContain(snapshot.Lines, line => line.Contains(altMarker, StringComparison.Ordinal));
        Assert.Contains(snapshot.Lines, line => line == "nova$");
    }

    [DockerFact]
    [Trait("Category", "DockerE2E")]
    [Trait("Target", "NativeSsh")]
    public async Task NativeSsh_CanResizeDuringAlternateScreen_AndKeepPromptUsable()
    {
        await using var fixture = await DockerSshFixture.StartAsync();

        var buffer = new TerminalBuffer(120, 30);
        var parser = new AnsiParser(buffer);
        var handler = new NativeSshTestInteractionHandler(fixture.Password);
        var logs = new List<string>();
        const string altMarker = "ALT RESIZE LIVE";
        const int finalCols = 120;
        const int finalRows = 35;

        using var session = new NativeSshSession(
            CreateProfile(fixture),
            cols: 120,
            rows: 30,
            log: logs.Add,
            interactionHandler: handler);

        session.AttachBuffer(buffer);
        session.OnOutputReceived += parser.Process;

        await WaitUntilAsync(() => SnapshotContainsExactLine(buffer, "nova$"), TimeSpan.FromSeconds(20), "initial prompt");

        session.SendInput($"printf '\\033[?1049h{ToPrintfOctalLiteral(altMarker)}'; sleep 1; printf '\\033[?1049l'\n");

        await Task.Delay(250);
        ResizeTerminal(buffer, session, 100, 25);
        await Task.Delay(100);
        ResizeTerminal(buffer, session, 90, 20);
        await Task.Delay(100);
        ResizeTerminal(buffer, session, finalCols, finalRows);

        await WaitUntilAsync(
            () => SnapshotContainsExactLine(buffer, "nova$"),
            TimeSpan.FromSeconds(20),
            "prompt after resize burst");

        session.SendInput("stty size\n");

        await WaitUntilAsync(
            () => SnapshotContainsExactLine(buffer, $"{finalRows} {finalCols}") && SnapshotContainsExactLine(buffer, "nova$"),
            TimeSpan.FromSeconds(20),
            "final terminal size and prompt");

        BufferSnapshot snapshot = BufferSnapshot.Capture(buffer);

        Assert.False(snapshot.IsAltScreen);
        Assert.DoesNotContain(snapshot.Lines, line => line.Contains(altMarker, StringComparison.Ordinal));
        Assert.Contains(snapshot.Lines, line => line == $"{finalRows} {finalCols}");
        Assert.Contains(snapshot.Lines, line => line == "nova$");
    }

    [DockerFact]
    [Trait("Category", "DockerE2E")]
    [Trait("Target", "NativeSsh")]
    public async Task NativeSsh_VimDownwardScroll_UpdatesVisibleWindowWithoutCtrlL()
    {
        await using var fixture = await DockerSshFixture.StartAsync();

        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer);
        var handler = new NativeSshTestInteractionHandler(fixture.Password);
        var logs = new List<string>();
        var scrollOutput = new List<string>();
        bool captureScrollOutput = false;

        using var session = new NativeSshSession(
            CreateProfile(fixture),
            cols: 80,
            rows: 24,
            log: logs.Add,
            interactionHandler: handler);

        session.AttachBuffer(buffer);
        session.OnOutputReceived += text =>
        {
            if (captureScrollOutput)
            {
                scrollOutput.Add(text);
            }

            parser.Process(text);
        };

        await WaitUntilAsync(() => SnapshotContainsExactLine(buffer, "nova$"), TimeSpan.FromSeconds(20), "initial prompt");

        session.SendInput("for i in $(seq -w 1 40); do echo \"line $i\"; done >/tmp/vim-scroll.txt\n");
        await WaitUntilAsync(() => SnapshotContainsExactLine(buffer, "nova$"), TimeSpan.FromSeconds(20), "prompt after creating vim test file");

        session.SendInput("vim.tiny -u NONE -N -n +'set scrolloff=5' /tmp/vim-scroll.txt\n");

        await WaitUntilAsync(
            () =>
            {
                BufferSnapshot snapshot = BufferSnapshot.Capture(buffer);
                return snapshot.IsAltScreen && snapshot.Lines.Any(line => line.Contains("line 01", StringComparison.Ordinal));
            },
            TimeSpan.FromSeconds(20),
            "vim initial screen");

        BufferSnapshot initial = BufferSnapshot.Capture(buffer);

        captureScrollOutput = true;
        session.SendInput(new string('j', 35));

        await WaitUntilAsync(
            () =>
            {
                BufferSnapshot snapshot = BufferSnapshot.Capture(buffer);
                return snapshot.IsAltScreen && snapshot.Lines.Any(line => line.Contains("line 40", StringComparison.Ordinal));
            },
            TimeSpan.FromSeconds(20),
            "vim downward scroll to later file content");

        BufferSnapshot after = BufferSnapshot.Capture(buffer);
        captureScrollOutput = false;
        int changedEditingRows = CountChangedRows(initial, after, editingRows: 20);

        Assert.True(
            changedEditingRows >= 5,
            $"Expected vim downward scroll to update multiple editing rows, but only {changedEditingRows} rows changed.{Environment.NewLine}Before:{Environment.NewLine}{FormatLines(initial)}{Environment.NewLine}After:{Environment.NewLine}{FormatLines(after)}{Environment.NewLine}Raw:{Environment.NewLine}{EscapeControlText(string.Concat(scrollOutput))}");
        Assert.Contains(after.Lines, line => line.Contains("line 40", StringComparison.Ordinal));
        Assert.DoesNotContain(after.Lines.Take(20), line => line.Contains("line 01", StringComparison.Ordinal));
    }

    private static SshProfile CreateProfile(DockerSshFixture fixture)
    {
        return new SshProfile
        {
            Id = Guid.Parse("d0ec8131-f7fe-40b3-992d-174da56fa5cd"),
            Name = "Docker Native SSH",
            BackendKind = SshBackendKind.Native,
            Host = fixture.Host,
            User = fixture.UserName,
            Port = fixture.Port
        };
    }

    private static bool SnapshotContains(TerminalBuffer buffer, string value)
    {
        return BufferSnapshot.Capture(buffer).Lines.Any(line => line.Contains(value, StringComparison.Ordinal));
    }

    private static bool SnapshotContainsExactLine(TerminalBuffer buffer, string value)
    {
        return BufferSnapshot.Capture(buffer).Lines.Any(line => line == value);
    }

    private static void ResizeTerminal(TerminalBuffer buffer, NativeSshSession session, int cols, int rows)
    {
        buffer.Resize(cols, rows);
        session.Resize(cols, rows);
    }

    private static int CountChangedRows(BufferSnapshot before, BufferSnapshot after, int editingRows)
    {
        IReadOnlyList<string> beforeLines = before.Lines.ToArray();
        IReadOnlyList<string> afterLines = after.Lines.ToArray();
        int rowCount = Math.Min(Math.Min(beforeLines.Count, afterLines.Count), editingRows);
        int changed = 0;
        for (int i = 0; i < rowCount; i++)
        {
            if (!string.Equals(beforeLines[i], afterLines[i], StringComparison.Ordinal))
            {
                changed++;
            }
        }

        return changed;
    }

    private static bool SnapshotsEqual(BufferSnapshot before, BufferSnapshot after)
    {
        IReadOnlyList<string> beforeLines = before.Lines.ToArray();
        IReadOnlyList<string> afterLines = after.Lines.ToArray();
        if (before.IsAltScreen != after.IsAltScreen || beforeLines.Count != afterLines.Count)
        {
            return false;
        }

        for (int i = 0; i < beforeLines.Count; i++)
        {
            if (!string.Equals(beforeLines[i], afterLines[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string FormatLines(BufferSnapshot snapshot)
    {
        return string.Join(
            Environment.NewLine,
            snapshot.Lines.Select((line, index) => $"{index:D2}: {line}"));
    }

    private static string EscapeControlText(string text)
    {
        var builder = new StringBuilder();
        foreach (char c in text)
        {
            switch (c)
            {
                case '\u001b':
                    builder.Append("\\e");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                default:
                    if (char.IsControl(c))
                    {
                        builder.Append("\\x");
                        builder.Append(((int)c).ToString("X2"));
                    }
                    else
                    {
                        builder.Append(c);
                    }
                    break;
            }
        }

        return builder.ToString();
    }

    private static string BuildAltScreenPayload(string text)
    {
        return $"\\033[?1049h{ToPrintfOctalLiteral(text)}\\033[?1049l";
    }

    private static string ToPrintfOctalLiteral(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        var builder = new StringBuilder(bytes.Length * 4);
        foreach (byte value in bytes)
        {
            builder.Append('\\');
            builder.Append(Convert.ToString(value, 8).PadLeft(3, '0'));
        }

        return builder.ToString();
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout, string description)
    {
        DateTime deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.True(predicate(), $"Timed out waiting for {description}.");
    }
}
