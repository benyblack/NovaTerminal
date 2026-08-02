using System;
using System.IO;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using NovaTerminal.Controls;
using NovaTerminal.Shell;
using NovaTerminal.VT;
using Xunit;

namespace NovaTerminal.Tests.Controls;

/// <summary>
/// Pins the invariant that makes #102's headline finding a non-issue.
///
/// <c>CreateAndWireParser</c> attaches 9 handlers to <see cref="AnsiParser"/> and never removes
/// them, which #102 read as an accumulation bug on session restart. It is safe for exactly one
/// reason: the method assigns a <b>fresh</b> parser first, so every session starts from empty
/// handler lists and the previous parser is garbage along with its handlers.
///
/// One line holds that up. Hoisting the parser out to reuse it across sessions — a
/// reasonable-looking change — would silently double all 9 on every <c>Reconnect()</c>, producing
/// exactly the duplicate bell/title symptom #102 describes, with no <c>-=</c> anywhere to fall back
/// on.
///
/// The parser's hooks are <c>Action</c>-typed properties rather than events, so <c>+=</c> is plain
/// delegate combination and the invocation list is readable without reflection.
/// </summary>
public class PaneParserWiringTests
{
    private static int HandlerCount(Delegate? handler) => handler?.GetInvocationList().Length ?? 0;

    /// Every parser hook the pane wires, with its current handler count.
    private static (string Name, int Count)[] HookCounts(AnsiParser parser) =>
    [
        (nameof(parser.OnBell), HandlerCount(parser.OnBell)),
        (nameof(parser.OnClipboardWrite), HandlerCount(parser.OnClipboardWrite)),
        (nameof(parser.OnWorkingDirectoryChanged), HandlerCount(parser.OnWorkingDirectoryChanged)),
        (nameof(parser.OnTitleChanged), HandlerCount(parser.OnTitleChanged)),
        (nameof(parser.OnPromptReady), HandlerCount(parser.OnPromptReady)),
        (nameof(parser.OnCommandAccepted), HandlerCount(parser.OnCommandAccepted)),
        (nameof(parser.OnCommandStarted), HandlerCount(parser.OnCommandStarted)),
        (nameof(parser.OnCommandFinished), HandlerCount(parser.OnCommandFinished)),
        (nameof(parser.OnCommandFinishedDetailed), HandlerCount(parser.OnCommandFinishedDetailed)),
    ];

    [AvaloniaFact]
    public void RewiringTheParser_ReplacesItRatherThanReusingIt()
    {
        using var pane = new TerminalPane();

        pane.CreateAndWireParser();
        AnsiParser? first = pane.Parser;
        Assert.NotNull(first);

        pane.CreateAndWireParser();
        AnsiParser? second = pane.Parser;
        Assert.NotNull(second);

        Assert.False(
            ReferenceEquals(first, second),
            "Session setup reused the AnsiParser. Its 9 handler subscriptions are only safe because "
            + "a fresh parser starts with empty handler lists - reusing one duplicates every handler "
            + "per reconnect (#102). Either restore the fresh parser, or add a matching -= for each.");
    }

    [AvaloniaFact]
    public void RewiringTheParser_LeavesExactlyOneHandlerPerHook()
    {
        using var pane = new TerminalPane();

        // Twenty reconnects' worth. If the parser were reused, each hook would end up with 20
        // handlers and a single bell would fire the pane's handler twenty times.
        for (int i = 0; i < 20; i++)
        {
            pane.CreateAndWireParser();
        }

        Assert.NotNull(pane.Parser);
        foreach ((string name, int count) in HookCounts(pane.Parser!))
        {
            Assert.Equal(1, count);
        }
    }

    [AvaloniaFact]
    public void TheSupersededParser_KeepsItsOwnHandlersAndIsSimplyDropped()
    {
        // Documents *why* not unsubscribing is acceptable: the old parser is not mutated or cleaned
        // up, it is abandoned. Nothing feeds it once Session output goes to the new one, so its
        // handlers are unreachable and collectible along with it.
        using var pane = new TerminalPane();

        pane.CreateAndWireParser();
        AnsiParser? superseded = pane.Parser;
        Assert.NotNull(superseded);

        pane.CreateAndWireParser();

        Assert.Equal(1, HandlerCount(superseded!.OnBell));
        Assert.False(ReferenceEquals(superseded, pane.Parser));
    }

    /// <summary>
    /// Regression test for the PR #275 review finding: <c>CreateAndWireParser</c> used to seed
    /// the parser's OSC 10/11 answer colors from the global <c>_settings.ActiveTheme</c> instead
    /// of the profile-merged theme that <see cref="TerminalPane.ApplySettings"/> resolves (via
    /// <c>Profile?.ThemeName ?? settings.ThemeName</c>). Because <c>CreateAndWireParser</c> runs
    /// after <c>ApplySettings</c> on pane construction, and again on every session
    /// (re)initialization, a pane whose profile overrides the theme would silently answer color
    /// queries with the global theme's colors until the next settings apply.
    /// </summary>
    [AvaloniaFact]
    public void CreateAndWireParser_UsesProfileThemeOverride_NotGlobalTheme()
    {
        string tempRoot = CreateTempAppRoot();
        string? previousRoot = Environment.GetEnvironmentVariable("NOVATERM_APPDATA_ROOT");

        try
        {
            Environment.SetEnvironmentVariable("NOVATERM_APPDATA_ROOT", tempRoot);

            var globalTheme = new TerminalTheme
            {
                Name = "PaneWiringGlobalTheme",
                Foreground = TermColor.FromRgb(10, 20, 30),
                Background = TermColor.FromRgb(40, 50, 60),
            };
            var profileTheme = new TerminalTheme
            {
                Name = "PaneWiringProfileTheme",
                Foreground = TermColor.FromRgb(200, 210, 220),
                Background = TermColor.FromRgb(5, 6, 7),
            };

            var themeManager = new ThemeManager();
            themeManager.SaveTheme(globalTheme);
            themeManager.SaveTheme(profileTheme);

            var settings = new TerminalSettings { ThemeName = globalTheme.Name };
            var profile = new TerminalProfile { ThemeName = profileTheme.Name };

            // The constructor's SetupCommon path already called ApplySettings(settings) with
            // this profile attached, exactly like MainWindow does before a pane is attached
            // and its session starts.
            using var pane = new TerminalPane(profile, settings);

            // Simulates session init (InitializeSession -> CreateAndWireParser). This is the
            // call that used to clobber the profile override with _settings.ActiveTheme.
            pane.CreateAndWireParser();

            Assert.NotNull(pane.Parser);
            Assert.Equal(profileTheme.Foreground, pane.Parser!.DefaultForeground);
            Assert.Equal(profileTheme.Background, pane.Parser!.DefaultBackground);

            // Simulate a Reconnect(), which calls CreateAndWireParser again with no
            // intervening ApplySettings. The profile override must still win every time.
            pane.CreateAndWireParser();

            Assert.Equal(profileTheme.Foreground, pane.Parser!.DefaultForeground);
            Assert.Equal(profileTheme.Background, pane.Parser!.DefaultBackground);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NOVATERM_APPDATA_ROOT", previousRoot);
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    /// <summary>
    /// PR #284's one behaviour change: "the command is running" is driven by OSC 133;C
    /// (command accepted / execution start), not OSC 133;B (prompt end). B fires once per
    /// painted prompt — including every repaint — while the shell sits idle waiting for
    /// input, so wiring Running to it would report every idle prompt as a busy session,
    /// clear <see cref="TerminalPane.LastExitCode"/> under the user's feet, and fire
    /// <c>CommandStarted</c> as terminal noise.
    ///
    /// Driven through the real parser so the assertion covers the pane's wiring, not a
    /// hand-called notifier.
    /// </summary>
    [AvaloniaFact]
    public void PromptEndMark_DoesNotStartACommand_ButCommandAcceptedDoes()
    {
        using var pane = new TerminalPane();
        pane.CreateAndWireParser();
        Assert.NotNull(pane.Parser);
        AnsiParser parser = pane.Parser!;

        Assert.True(
            NovaTerminal.AgentHost.AgentSessionRegistry.Instance.TryGet(pane.PaneId, out var registration),
            "the pane registers itself with the agent-session registry in SetupCommon");

        int commandStartedCount = 0;
        pane.CommandStarted += _ => commandStartedCount++;

        // Seed a finished command so the LastExitCode reset has something to clear.
        parser.OnCommandFinished?.Invoke(3);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(3, pane.LastExitCode);

        // A + prompt text + B: the whole prompt cycle, with the shell now idle at the
        // input cell.
        parser.Process("\x1b]133;A\x07");
        parser.Process("user@host:~$ ");
        parser.Process("\x1b]133;B\x07");
        Dispatcher.UIThread.RunJobs();

        var afterPromptEnd = registration.StatusMachine.Snapshot();
        Assert.Equal(NovaTerminal.AgentHost.AgentSessionStatusKind.AwaitingInput, afterPromptEnd.Kind);
        // Precise, not merely "heuristic and nothing running": A already put the machine on
        // the precise tier, so AwaitingInput here is a real statement about the shell.
        Assert.Equal(NovaTerminal.AgentHost.AgentSessionStatusConfidence.Precise, afterPromptEnd.Confidence);
        Assert.Null(afterPromptEnd.CurrentCommand);
        Assert.Equal(3, pane.LastExitCode);
        Assert.Equal(0, commandStartedCount);

        // C is the execution-start edge.
        string encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("sleep 5"));
        parser.Process($"\x1b]133;C;{encoded}\x07");
        Dispatcher.UIThread.RunJobs();

        var afterAccepted = registration.StatusMachine.Snapshot();
        Assert.Equal(NovaTerminal.AgentHost.AgentSessionStatusKind.Running, afterAccepted.Kind);
        Assert.Equal("sleep 5", afterAccepted.CurrentCommand);
        Assert.Null(pane.LastExitCode);
        Assert.Equal(1, commandStartedCount);
    }

    /// <summary>
    /// The other half of the B contract: a prompt repaint re-emits B, and that must stay a
    /// no-op for session status even while a command is genuinely running (a prompt-drawing
    /// TUI, or a shell that repaints after a resize mid-command).
    /// </summary>
    [AvaloniaFact]
    public void RepeatedPromptEndMarks_DoNotDisturbARunningCommand()
    {
        using var pane = new TerminalPane();
        pane.CreateAndWireParser();
        Assert.True(
            NovaTerminal.AgentHost.AgentSessionRegistry.Instance.TryGet(pane.PaneId, out var registration));

        AnsiParser parser = pane.Parser!;
        string encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("sleep 5"));
        parser.Process("\x1b]133;A\x07$ \x1b]133;B\x07");
        parser.Process($"\x1b]133;C;{encoded}\x07");
        Assert.Equal(
            NovaTerminal.AgentHost.AgentSessionStatusKind.Running,
            registration.StatusMachine.Snapshot().Kind);

        parser.Process("\x1b]133;B\x07");
        parser.Process("\x1b]133;B\x07");
        Dispatcher.UIThread.RunJobs();

        var snapshot = registration.StatusMachine.Snapshot();
        Assert.Equal(NovaTerminal.AgentHost.AgentSessionStatusKind.Running, snapshot.Kind);
        Assert.Equal("sleep 5", snapshot.CurrentCommand);
    }

    private static string CreateTempAppRoot()
    {
        string path = Path.Combine(Path.GetTempPath(), $"nova_pane_wiring_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Issue #268: <c>TerminalSettings.AllowOsc52ClipboardWrite</c> gates OSC 52 clipboard
    /// writes at the App layer (AnsiParser itself stays policy-free and always raises
    /// <c>OnClipboardWrite</c>). The real write path continues on through
    /// <c>Dispatcher.UIThread.Post</c> into <c>TermView.SetClipboardTextAsync</c>, which needs a
    /// live UI-thread <c>TopLevel</c>/<c>Clipboard</c> a headless test does not provide - so this
    /// asserts the gate itself via <see cref="TerminalPane.ClipboardWriteAttemptsForTest"/>, the
    /// synchronous counter bumped only once a payload has passed the gate and would otherwise
    /// reach the clipboard.
    /// </summary>
    [AvaloniaFact]
    public void ClipboardWrite_SettingOff_DoesNotReachClipboardWritePath()
    {
        using var pane = new TerminalPane();
        pane.ApplySettings(new TerminalSettings { AllowOsc52ClipboardWrite = false });
        pane.CreateAndWireParser();

        Assert.NotNull(pane.Parser);
        pane.Parser!.OnClipboardWrite?.Invoke("c", System.Text.Encoding.UTF8.GetBytes("hello"));

        Assert.Equal(0, pane.ClipboardWriteAttemptsForTest);
    }

    [AvaloniaFact]
    public void ClipboardWrite_SettingOn_ReachesClipboardWritePath()
    {
        using var pane = new TerminalPane();
        pane.ApplySettings(new TerminalSettings { AllowOsc52ClipboardWrite = true });
        pane.CreateAndWireParser();

        Assert.NotNull(pane.Parser);
        pane.Parser!.OnClipboardWrite?.Invoke("c", System.Text.Encoding.UTF8.GetBytes("hello"));

        Assert.Equal(1, pane.ClipboardWriteAttemptsForTest);
    }

    /// <summary>
    /// PR #280 review, test gap: the two gate tests above both call <c>ApplySettings</c> *before*
    /// <c>CreateAndWireParser</c>, so they would still pass if the handler captured the bool at
    /// wire time. The design rests on the gate being read at *invocation* time, so that a live
    /// settings change takes effect on an already-running session without re-wiring the parser.
    /// This wires first and toggles after, which is the only ordering that actually pins that.
    /// </summary>
    [AvaloniaFact]
    public void ClipboardWrite_SettingToggledAfterWiring_TakesEffectWithoutRewiring()
    {
        using var pane = new TerminalPane();
        pane.ApplySettings(new TerminalSettings { AllowOsc52ClipboardWrite = true });
        pane.CreateAndWireParser();

        Assert.NotNull(pane.Parser);
        AnsiParser parser = pane.Parser!;

        parser.OnClipboardWrite?.Invoke("c", System.Text.Encoding.UTF8.GetBytes("first"));
        Assert.Equal(1, pane.ClipboardWriteAttemptsForTest);

        // Toggle off on the *same* parser instance - no CreateAndWireParser in between.
        pane.ApplySettings(new TerminalSettings { AllowOsc52ClipboardWrite = false });
        Assert.Same(parser, pane.Parser);

        parser.OnClipboardWrite?.Invoke("c", System.Text.Encoding.UTF8.GetBytes("second"));
        Assert.Equal(1, pane.ClipboardWriteAttemptsForTest);

        // And back on again, so this cannot pass by the gate simply latching off.
        pane.ApplySettings(new TerminalSettings { AllowOsc52ClipboardWrite = true });
        parser.OnClipboardWrite?.Invoke("c", System.Text.Encoding.UTF8.GetBytes("third"));
        Assert.Equal(2, pane.ClipboardWriteAttemptsForTest);
    }
}
