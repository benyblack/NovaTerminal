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
