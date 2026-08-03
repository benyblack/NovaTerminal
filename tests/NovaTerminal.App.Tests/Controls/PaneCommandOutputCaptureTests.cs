using System.Reflection;
using System.Text;
using Avalonia.Headless.XUnit;
using NovaTerminal.Controls;
using NovaTerminal.Shell;
using NovaTerminal.VT;
using Xunit;

namespace NovaTerminal.Tests.Controls;

/// <summary>
/// The pane's half of V2 Phase 4a task 1: capturing a failing command's output at the
/// <c>OSC 133;D</c> edge, redacting it, and handing it to Command Assist.
/// </summary>
/// <remarks>
/// <para>
/// Extraction semantics are covered exhaustively in
/// <c>NovaTerminal.VT.Tests.CommandOutputReaderTests</c>. What is pinned here is the wiring, and
/// specifically the three things that are properties of the <em>call site</em> rather than of the
/// reader:
/// </para>
/// <list type="number">
/// <item><description><strong>The exit-0 gate.</strong> A successful command must cost nothing -
/// no grid walk, no regex pass - because the overwhelming majority of commands succeed and this
/// runs on the PTY read thread.</description></item>
/// <item><description><strong>Redaction before the boundary.</strong> The text crosses out of the
/// VT layer into a plain string that Phase 5's provider seam may eventually send off the machine.
/// <c>SecretsFilter</c> runs at the one call site, not somewhere downstream.</description></item>
/// <item><description><strong>Timing.</strong> The read happens synchronously on the parse thread
/// at <c>D</c>. One frame later the next prompt has been painted over the last rows of the output.
/// </description></item>
/// </list>
/// </remarks>
public class PaneCommandOutputCaptureTests
{
    private const string PromptStart = "\x1b]133;A\x07";
    private const string PromptEnd = "\x1b]133;B\x07";

    // ---------------------------------------------------------------- capture

    [AvaloniaFact]
    public void OnAFailingCommand_TheOutputTailIsCaptured()
    {
        using var pane = new TerminalPane();
        pane.CreateAndWireParser();

        RunCommand(pane, "git status", "fatal: not a git repository (or any of the parent directories): .git", exitCode: 128);

        Assert.Equal(
            "fatal: not a git repository (or any of the parent directories): .git",
            pane.LastFailureOutputTailForTest);
    }

    /// <summary>
    /// The gate. Break it - capture unconditionally - and this test fails, which is the point:
    /// every successful command in every pane would otherwise pay for a grid walk and a redaction
    /// pass on the read thread.
    /// </summary>
    [AvaloniaFact]
    public void OnASuccessfulCommand_NothingIsCaptured()
    {
        using var pane = new TerminalPane();
        pane.CreateAndWireParser();

        RunCommand(pane, "git status", "On branch main\nnothing to commit, working tree clean", exitCode: 0);

        Assert.Null(pane.LastFailureOutputTailForTest);
    }

    /// <summary>An <c>OSC 133;D</c> with no exit code says nothing about success or failure.</summary>
    [AvaloniaFact]
    public void WithNoExitCode_NothingIsCaptured()
    {
        using var pane = new TerminalPane();
        pane.CreateAndWireParser();

        pane.Parser!.Process(PromptStart + "$ " + PromptEnd + "git status");
        pane.Parser!.Process(Accepted("git status"));
        pane.Parser!.Process("\r\nsomething went wrong\r\n");
        pane.Parser!.Process("\x1b]133;D\x07");

        Assert.Null(pane.LastFailureOutputTailForTest);
    }

    /// <summary>
    /// The redaction guarantee. Remove the <c>SecretsFilter</c> call in
    /// <c>TerminalPane.TryCaptureFailureOutputTail</c> and this fails.
    /// </summary>
    [AvaloniaFact]
    public void TheCapturedTailIsRedacted()
    {
        using var pane = new TerminalPane();
        pane.CreateAndWireParser();

        RunCommand(
            pane,
            "deploy",
            "error: authentication failed for --password hunter2",
            exitCode: 1);

        Assert.Equal("error: authentication failed for --password [REDACTED]", pane.LastFailureOutputTailForTest);
        Assert.DoesNotContain("hunter2", pane.LastFailureOutputTailForTest!, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void TheCapturedTailIsCapped()
    {
        using var pane = new TerminalPane();
        pane.CreateAndWireParser();

        string noise = string.Join("\r\n", Enumerable.Range(1, 200).Select(i => $"line {i}"));
        RunCommand(pane, "noisy", noise, exitCode: 1);

        string tail = Assert.IsType<string>(pane.LastFailureOutputTailForTest);
        string[] lines = tail.Split('\n');

        Assert.Equal(CommandOutputReader.MaxOutputLines, lines.Length);
        Assert.Equal("line 200", lines[^1]);
        Assert.True(tail.Length <= CommandOutputReader.MaxOutputChars);
    }

    /// <summary>
    /// Neither the prompt nor the command line is output. If the input row leaked in, every
    /// recogniser would be matching patterns against text that includes what the user typed - and
    /// "did the output mention git?" would be true for every git command.
    /// </summary>
    [AvaloniaFact]
    public void ThePromptAndCommandLineAreExcluded()
    {
        using var pane = new TerminalPane();
        pane.CreateAndWireParser();

        RunCommand(pane, "gti status", "gti: command not found", exitCode: 127, prompt: "user@host:~/repo$ ");

        string tail = pane.LastFailureOutputTailForTest!;
        Assert.Equal("gti: command not found", tail);
        Assert.DoesNotContain("user@host", tail, StringComparison.Ordinal);
        Assert.DoesNotContain("gti status", tail, StringComparison.Ordinal);
    }

    /// <summary>
    /// Without a <c>C</c> edge there is no region start, so there is nothing to bound the read and
    /// the honest answer is nothing. A <c>D</c> arriving on its own - a third-party integration
    /// that emits only the finish mark - must not read from wherever the last region happened to
    /// begin.
    /// </summary>
    [AvaloniaFact]
    public void WithoutACommandAcceptedMark_NothingIsCaptured()
    {
        using var pane = new TerminalPane();
        pane.CreateAndWireParser();

        pane.Parser!.Process(PromptStart + "$ " + PromptEnd + "git status");
        pane.Parser!.Process("\r\nfatal: not a git repository\r\n");
        pane.Parser!.Process("\x1b]133;D;128\x07");

        Assert.Null(pane.LastFailureOutputTailForTest);
    }

    /// <summary>
    /// The region does not survive its own command. A second failure with no <c>C</c> of its own
    /// must not re-read the first one's rows.
    /// </summary>
    [AvaloniaFact]
    public void TheRegionIsDroppedAtD()
    {
        using var pane = new TerminalPane();
        pane.CreateAndWireParser();

        RunCommand(pane, "first", "first failure", exitCode: 1);
        Assert.Equal("first failure", pane.LastFailureOutputTailForTest);

        pane.Parser!.Process("\r\nlater unrelated content\r\n");
        pane.Parser!.Process("\x1b]133;D;1\x07");

        Assert.Null(pane.LastFailureOutputTailForTest);
    }

    [AvaloniaFact]
    public void OnTheAltScreen_NothingIsCaptured()
    {
        using var pane = new TerminalPane();
        pane.CreateAndWireParser();

        pane.Parser!.Process("\x1b[?1049h");
        pane.Parser!.Process(PromptStart + "$ " + PromptEnd + "vim");
        pane.Parser!.Process(Accepted("vim"));
        pane.Parser!.Process("\r\nE325: ATTENTION\r\n");
        pane.Parser!.Process("\x1b]133;D;1\x07");

        Assert.Null(pane.LastFailureOutputTailForTest);
    }

    // ---------------------------------------------------------------- end to end

    /// <summary>
    /// The whole point of the phase: a typo'd command in an instrumented pane produces a Fix popup
    /// naming the correction, driven only by escape sequences.
    /// </summary>
    [AvaloniaFact]
    public async Task AFailingTypo_OpensFixModeWithTheCorrection()
    {
        using var fixture = await Fixture.CreateAsync();

        RunCommand(
            fixture.Pane,
            "gti status",
            "gti: The term 'gti' is not recognized as a name of a cmdlet, function, script file, or executable program.",
            exitCode: 1);

        await fixture.SettleAsync();

        Assert.Equal("Fix", fixture.Pane.CommandAssistViewModel?.ModeLabel);
        Assert.True(fixture.Pane.CommandAssistViewModel?.IsVisible);
        Assert.True(fixture.Pane.CommandAssistViewModel?.IsPopupOpen);
        Assert.Contains(
            fixture.Pane.CommandAssistViewModel!.Suggestions,
            item => item.DisplayText.Contains("git", StringComparison.Ordinal));
    }

    /// <summary>
    /// The other half of the demo: an explanation rides below the Fix threshold, so the bubble
    /// appears and the popup does not open itself over the user's next command.
    /// </summary>
    [AvaloniaFact]
    public async Task AFailureWithOnlyAnExplanation_ShowsTheBubbleWithoutOpeningThePopup()
    {
        using var fixture = await Fixture.CreateAsync();

        RunCommand(
            fixture.Pane,
            "git status",
            "fatal: not a git repository (or any of the parent directories): .git",
            exitCode: 128);

        await fixture.SettleAsync();

        Assert.True(fixture.Pane.CommandAssistViewModel?.IsVisible);
        Assert.False(fixture.Pane.CommandAssistViewModel?.IsPopupOpen);
        Assert.Contains(
            fixture.Pane.CommandAssistViewModel!.Suggestions,
            item => item.DisplayText.Contains("not inside a Git repository", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task ASuccessfulCommandShowsNothing()
    {
        using var fixture = await Fixture.CreateAsync();

        RunCommand(fixture.Pane, "git status", "On branch main", exitCode: 0);

        await fixture.SettleAsync();

        Assert.False(fixture.Pane.CommandAssistViewModel?.IsVisible ?? false);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>One full B - C - output - D cycle, driven entirely through the parser.</summary>
    private static void RunCommand(
        TerminalPane pane,
        string commandText,
        string output,
        int? exitCode,
        string prompt = "$ ")
    {
        pane.Parser!.Process(PromptStart + prompt + PromptEnd + commandText);
        pane.Parser!.Process(Accepted(commandText));
        pane.Parser!.Process("\r\n" + output.ReplaceLineEndings("\r\n") + "\r\n");
        pane.Parser!.Process(exitCode.HasValue ? $"\x1b]133;D;{exitCode.Value}\x07" : "\x1b]133;D\x07");
    }

    private static string Accepted(string commandText)
        => "\x1b]133;C;" + Convert.ToBase64String(Encoding.UTF8.GetBytes(commandText)) + "\x07";

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory;

        private Fixture(TerminalPane pane, string directory)
        {
            Pane = pane;
            _directory = directory;
        }

        public TerminalPane Pane { get; }

        public static async Task<Fixture> CreateAsync()
        {
            // A private services graph: these tests write history entries and must not share a
            // file with whatever else is running.
            string directory = Path.Combine(
                Path.GetTempPath(),
                $"nova_output_capture_{Environment.ProcessId}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);

            var services = new CommandAssistServices(
                Path.Combine(directory, "history.jsonl"),
                legacyHistoryFilePath: null,
                Path.Combine(directory, "snippets.json"),
                () => directory);
            await services.HistoryStore.GetRecentAsync(1);

            var pane = new TerminalPane
            {
                CommandAssistServices = services,
            };

            var settings = new TerminalSettings(); // constructed, not Load() - see #232
            settings.CommandAssistEnabled = true;
            settings.CommandAssistHistoryEnabled = true;
            pane.ApplySettings(settings);

            var session = new PaneAssistInsertionTests.RecordingSession();
            typeof(TerminalPane)
                .GetProperty("Session", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
                .SetValue(pane, session);

            pane.CreateAndWireParser();
            return new Fixture(pane, directory);
        }

        /// <summary>
        /// The failure path crosses two queues: the dispatcher post out of the parse thread, and
        /// the assist controller's own serialized dispatcher.
        /// </summary>
        public async Task SettleAsync()
        {
            for (int i = 0; i < 40; i++)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                await Task.Delay(10);
                if (Pane.CommandAssistViewModel?.IsVisible == true)
                {
                    break;
                }
            }

            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }

        public void Dispose()
        {
            Pane.Dispose();
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch
            {
                // Best effort; the temp root is per-test anyway.
            }
        }
    }
}
