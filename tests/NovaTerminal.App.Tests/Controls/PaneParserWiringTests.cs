using System;
using Avalonia.Headless.XUnit;
using NovaTerminal.Controls;
using NovaTerminal.VT;
using Xunit;

namespace NovaTerminal.Tests.Controls;

/// <summary>
/// Pins the invariant that makes #102's headline finding a non-issue.
///
/// <c>CreateAndWireParser</c> attaches 8 handlers to <see cref="AnsiParser"/> and never removes
/// them, which #102 read as an accumulation bug on session restart. It is safe for exactly one
/// reason: the method assigns a <b>fresh</b> parser first, so every session starts from empty
/// handler lists and the previous parser is garbage along with its handlers.
///
/// One line holds that up. Hoisting the parser out to reuse it across sessions — a
/// reasonable-looking change — would silently double all 8 on every <c>Reconnect()</c>, producing
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
}
