using System;
using System.IO;
using Avalonia.Headless.XUnit;
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
            "Session setup reused the AnsiParser. Its 8 handler subscriptions are only safe because "
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
}
