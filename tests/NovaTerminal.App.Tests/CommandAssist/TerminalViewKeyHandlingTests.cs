using NovaTerminal.Shell;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Moq;
using NovaTerminal.Controls;
using NovaTerminal.Platform;
using NovaTerminal.VT;
using NovaTerminal.Pty;

namespace NovaTerminal.Tests.CommandAssist;

public sealed class TerminalViewKeyHandlingTests
{
    private sealed class TestTerminalView : TerminalView
    {
        public void RaiseGotFocusForTest()
        {
            OnGotFocus(new FocusChangedEventArgs(InputElement.GotFocusEvent));
        }

        public void RaiseLostFocusForTest()
        {
            OnLostFocus(new FocusChangedEventArgs(InputElement.LostFocusEvent));
        }
    }

    [AvaloniaFact]
    public void HandleKeyDownCore_WhenInterceptorHandlesTab_DoesNotForwardTabToSession()
    {
        var session = new Mock<ITerminalSession>();
        var view = new TerminalView();
        view.SetSession(session.Object);
        view.KeyDownInterceptor = (key, modifiers) => key == Key.Tab;

        bool handled = view.HandleKeyDownCore(Key.Tab, KeyModifiers.None);

        Assert.True(handled);
        session.Verify(x => x.SendInput(It.IsAny<string>()), Times.Never);
    }

    [AvaloniaFact]
    public void HandleKeyDownCore_WhenTabIsNotIntercepted_ForwardsTabToSession()
    {
        var session = new Mock<ITerminalSession>();
        var view = new TerminalView();
        view.SetSession(session.Object);

        bool handled = view.HandleKeyDownCore(Key.Tab, KeyModifiers.None);

        Assert.True(handled);
        session.Verify(x => x.SendInput("\t"), Times.Once);
    }

    [AvaloniaFact]
    public void HandleKeyDownCore_WhenShiftTabPressed_SendsBackTabSequenceNotLiteralTab()
    {
        // Regression: Shift+Tab must emit the back-tab (CBT) sequence ESC [ Z, matching
        // xterm. The unconditional Key.Tab case used to swallow the Shift modifier and send
        // a literal tab, which broke Claude Code's reverse permission-mode cycling.
        var session = new Mock<ITerminalSession>();
        var view = new TerminalView();
        view.SetSession(session.Object);

        bool handled = view.HandleKeyDownCore(Key.Tab, KeyModifiers.Shift);

        Assert.True(handled);
        session.Verify(x => x.SendInput("\x1b[Z"), Times.Once);
        session.Verify(x => x.SendInput("\t"), Times.Never);
    }

    [AvaloniaFact]
    public void HandleKeyDownCore_WhenSessionNotRunning_DoesNotConsumeEnter_SoPaneCanReconnect()
    {
        // Regression: after an SSH disconnect the dead session object is kept around so the
        // "[Press Enter to reconnect]" banner works. TerminalView (the focused child) must not
        // swallow Enter into the dead PTY — it has to bubble up to TerminalPane.OnKeyDown, which
        // owns the reconnect-on-Enter logic. Consuming it here is what made reconnect impossible.
        var session = new Mock<ITerminalSession>();
        session.SetupGet(x => x.IsProcessRunning).Returns(false);
        var view = new TerminalView();
        view.SetSession(session.Object);

        bool handled = view.HandleKeyDownCore(Key.Enter, KeyModifiers.None);

        Assert.False(handled);
        session.Verify(x => x.SendInput(It.IsAny<string>()), Times.Never);
    }

    [AvaloniaFact]
    public void HandleKeyDownCore_WhenSessionRunning_ForwardsEnterToSession()
    {
        var session = new Mock<ITerminalSession>();
        session.SetupGet(x => x.IsProcessRunning).Returns(true);
        var view = new TerminalView();
        view.SetSession(session.Object);

        bool handled = view.HandleKeyDownCore(Key.Enter, KeyModifiers.None);

        Assert.True(handled);
        session.Verify(x => x.SendInput("\r"), Times.Once);
    }

    [AvaloniaFact]
    public void HandleKeyDownCore_WhenAltLetterPressed_SendsEscapePrefixedSequence()
    {
        var session = new Mock<ITerminalSession>();
        var view = new TerminalView();
        view.SetSession(session.Object);

        bool handled = view.HandleKeyDownCore(Key.V, KeyModifiers.Alt);

        Assert.True(handled);
        session.Verify(x => x.SendInput("\x1bv"), Times.Once);
    }

    [AvaloniaFact]
    public void HandleKeyDownCore_WhenAltBackspacePressed_SendsEscDeleteNotBareDelete()
    {
        // Regression: the unconditional Key.Back switch case must not swallow Alt+Backspace
        // before meta-encoding runs. Expect ESC + DEL (readline backward-kill-word).
        var session = new Mock<ITerminalSession>();
        var view = new TerminalView();
        view.SetSession(session.Object);

        bool handled = view.HandleKeyDownCore(Key.Back, KeyModifiers.Alt);

        Assert.True(handled);
        session.Verify(x => x.SendInput("\x1b\x7f"), Times.Once);
        session.Verify(x => x.SendInput("\x7f"), Times.Never);
    }

    [AvaloniaFact]
    public void AltVKeyPress_RoutesThroughRealInputPipeline_SendsEscapePrefixedV()
    {
        // End-to-end: dispatch a real Alt+V key event through Avalonia's full input pipeline
        // (incl. the access-key handler) to confirm it is not swallowed before reaching the
        // terminal's key handler, and emerges as ESC v — Claude Code's paste-image trigger.
        var session = new Mock<ITerminalSession>();
        var view = new TerminalView { Focusable = true };
        view.SetSession(session.Object);

        var window = new Window { Content = view, Width = 400, Height = 300 };
        window.Show();
        view.Focus();

        window.KeyPress(Key.V, RawInputModifiers.Alt, PhysicalKey.V, "v");

        session.Verify(x => x.SendInput("\x1bv"), Times.AtLeastOnce);
    }

    [AvaloniaFact]
    public void HandleKeyDownCore_WhenApplicationCursorKeysEnabled_UsesSs3ForArrowsAndHomeEnd()
    {
        var session = new Mock<ITerminalSession>();
        var view = new TerminalView();
        var buffer = new TerminalBuffer(80, 24);
        buffer.Modes.IsApplicationCursorKeys = true;
        view.SetBuffer(buffer);
        view.SetSession(session.Object);

        Assert.True(view.HandleKeyDownCore(Key.Up, KeyModifiers.None));
        Assert.True(view.HandleKeyDownCore(Key.Down, KeyModifiers.None));
        Assert.True(view.HandleKeyDownCore(Key.Right, KeyModifiers.None));
        Assert.True(view.HandleKeyDownCore(Key.Left, KeyModifiers.None));
        Assert.True(view.HandleKeyDownCore(Key.Home, KeyModifiers.None));
        Assert.True(view.HandleKeyDownCore(Key.End, KeyModifiers.None));

        session.Verify(x => x.SendInput("\x1bOA"), Times.Once);
        session.Verify(x => x.SendInput("\x1bOB"), Times.Once);
        session.Verify(x => x.SendInput("\x1bOC"), Times.Once);
        session.Verify(x => x.SendInput("\x1bOD"), Times.Once);
        session.Verify(x => x.SendInput("\x1bOH"), Times.Once);
        session.Verify(x => x.SendInput("\x1bOF"), Times.Once);
    }

    [AvaloniaFact]
    public void FocusReporting_WhenEnabled_EmitsFocusInAndFocusOut()
    {
        var session = new Mock<ITerminalSession>();
        var view = new TestTerminalView();
        var buffer = new TerminalBuffer(80, 24);
        buffer.Modes.IsFocusEventReporting = true;
        view.SetBuffer(buffer);
        view.SetSession(session.Object);

        view.RaiseGotFocusForTest();
        view.RaiseLostFocusForTest();

        session.Verify(x => x.SendInput("\x1b[I"), Times.Once);
        session.Verify(x => x.SendInput("\x1b[O"), Times.Once);
    }

    [AvaloniaFact]
    public void GetCommandAssistPromptHint_WhenMetricsAndCursorAreAvailable_ReturnsVisibleCursorRow()
    {
        var buffer = new TerminalBuffer(80, 24);
        var view = new TerminalView();
        view.SetBuffer(buffer);
        view.Measure(new Avalonia.Size(800, 432));
        view.Arrange(new Avalonia.Rect(0, 0, 800, 432));
        view.SetMetricsForTest(10, 18);
        buffer.SetCursorPosition(3, 7);

        CommandAssistPromptHint? hint = view.GetCommandAssistPromptHint();

        Assert.NotNull(hint);
        Assert.Equal(7, hint.Value.VisibleCursorVisualRow);
        Assert.Equal(view.Rows, hint.Value.VisibleRows);
        Assert.Equal(18, hint.Value.CellHeight);
    }

    [AvaloniaFact]
    public void PromptHintAndPaneAnchorLayout_WhenCursorRowOrMetricsChange_UpdateWithoutBufferMutation()
    {
        using var pane = new TerminalPane
        {
            Width = 900,
            Height = 500
        };
        pane.CommandAssistServices = TestCommandAssistServices.Instance;
        var settings = new TerminalSettings(); // constructed, not Load() - see #232
        settings.CommandAssistEnabled = true;
        settings.CommandAssistHistoryEnabled = true;
        pane.ApplySettings(settings);
        pane.Measure(new Avalonia.Size(900, 500));
        pane.Arrange(new Avalonia.Rect(0, 0, 900, 500));

        var view = pane.FindControl<TerminalView>("TermView");
        Assert.NotNull(view);
        Assert.NotNull(pane.Buffer);
        view.Measure(new Avalonia.Size(900, 478));
        view.Arrange(new Avalonia.Rect(0, 0, 900, 478));

        view.SetMetricsForTest(10, 18);
        pane.Buffer.SetCursorPosition(0, 5);
        CommandAssistPromptHint? firstHint = view.GetCommandAssistPromptHint();
        var firstLayout = pane.CalculateCommandAssistAnchorLayoutForTest();

        pane.Buffer.SetCursorPosition(0, 10);
        CommandAssistPromptHint? secondHint = view.GetCommandAssistPromptHint();

        view.SetMetricsForTest(10, 20);
        CommandAssistPromptHint? thirdHint = view.GetCommandAssistPromptHint();
        var secondLayout = pane.CalculateCommandAssistAnchorLayoutForTest();

        Assert.NotNull(firstHint);
        Assert.NotNull(secondHint);
        Assert.NotNull(thirdHint);
        Assert.NotNull(firstLayout);
        Assert.NotNull(secondLayout);
        Assert.Equal(5, firstHint.Value.VisibleCursorVisualRow);
        Assert.Equal(10, secondHint.Value.VisibleCursorVisualRow);
        Assert.Equal(20, thirdHint.Value.CellHeight);
        Assert.True(firstLayout!.UsesPromptAnchor);
        Assert.True(secondLayout!.UsesPromptAnchor);
        Assert.True(secondLayout.PromptRect.Y > firstLayout.PromptRect.Y);
    }

    // --- Kitty keyboard protocol (#266 / PR #277 review, Blocker 3) ---------------------
    //
    // KittyKeyboardEncodingTests exercises TerminalInputModeEncoder.EncodeKittyKey in
    // isolation. The risk the review flagged lives one layer up: TerminalView.TryEncodeKittyKey
    // (the reconnect/selection carve-outs) and the placement of the call ahead of every legacy
    // path in HandleKeyDownCore. These tests exercise that ordering end-to-end.

    private static TerminalBuffer CreateDisambiguateBuffer()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.Modes.KittyKeyboard.Push(KittyKeyboardState.FlagDisambiguateEscapeCodes);
        return buffer;
    }

    [AvaloniaFact]
    public void KittyProtocolOn_FlagsZero_EnterEscTabAreByteIdenticalToLegacy()
    {
        // The PR's central claim is "byte-identical with flags = 0" - assert it here, at the
        // level where a future refactor could actually break it, with the protocol setting on
        // and a real (empty) KittyKeyboard stack, not just by omitting SetBuffer entirely.
        var session = new Mock<ITerminalSession>();
        session.SetupGet(x => x.IsProcessRunning).Returns(true);
        var view = new TerminalView();
        view.SetBuffer(new TerminalBuffer(80, 24)); // KittyKeyboard stack empty -> flags = 0
        view.SetSession(session.Object);
        view.ApplySettings(new TerminalSettings()); // protocol on (default), nothing pushed

        Assert.True(view.HandleKeyDownCore(Key.Enter, KeyModifiers.None));
        session.Verify(x => x.SendInput("\r"), Times.Once);

        // Legacy Enter ignores Shift entirely - this is exactly what disambiguate exists to fix,
        // and is why it must stay off until something actually pushes flag 1.
        Assert.True(view.HandleKeyDownCore(Key.Enter, KeyModifiers.Shift));
        session.Verify(x => x.SendInput("\r"), Times.Exactly(2));

        Assert.True(view.HandleKeyDownCore(Key.Escape, KeyModifiers.None));
        session.Verify(x => x.SendInput("\x1b"), Times.Once);

        Assert.True(view.HandleKeyDownCore(Key.Tab, KeyModifiers.None));
        session.Verify(x => x.SendInput("\t"), Times.Once);
    }

    [AvaloniaFact]
    public void KittyProtocolOn_FlagsZero_CtrlCWithSelection_StillCopiesInsteadOfSendingSigint()
    {
        var session = new Mock<ITerminalSession>();
        session.SetupGet(x => x.IsProcessRunning).Returns(true);
        var view = new TerminalView();
        view.SetBuffer(new TerminalBuffer(80, 24));
        view.SetSession(session.Object);
        view.ApplySettings(new TerminalSettings());
        view.SetSelectionForTest(0, 0, 0, 3);

        bool handled = view.HandleKeyDownCore(Key.C, KeyModifiers.Control);

        Assert.True(handled);
        session.Verify(x => x.SendInput(It.IsAny<string>()), Times.Never);
    }

    [AvaloniaFact]
    public void KittyProtocolOn_Disambiguate_ShiftEnterSendsCsiU()
    {
        var session = new Mock<ITerminalSession>();
        session.SetupGet(x => x.IsProcessRunning).Returns(true);
        var view = new TerminalView();
        view.SetBuffer(CreateDisambiguateBuffer());
        view.SetSession(session.Object);
        view.ApplySettings(new TerminalSettings());

        bool handled = view.HandleKeyDownCore(Key.Enter, KeyModifiers.Shift);

        Assert.True(handled);
        session.Verify(x => x.SendInput("\x1b[13;2u"), Times.Once);
        session.Verify(x => x.SendInput("\r"), Times.Never);
    }

    [AvaloniaFact]
    public void KittyProtocolOn_Disambiguate_CtrlCWithSelection_CarveOutWinsOverEncoding()
    {
        var session = new Mock<ITerminalSession>();
        session.SetupGet(x => x.IsProcessRunning).Returns(true);
        var view = new TerminalView();
        view.SetBuffer(CreateDisambiguateBuffer());
        view.SetSession(session.Object);
        view.ApplySettings(new TerminalSettings());
        view.SetSelectionForTest(0, 0, 0, 3);

        bool handled = view.HandleKeyDownCore(Key.C, KeyModifiers.Control);

        Assert.True(handled);
        session.Verify(x => x.SendInput(It.IsAny<string>()), Times.Never);
    }

    [AvaloniaFact]
    public void KittyProtocolOn_Disambiguate_CtrlCWithoutSelection_SendsCsiU()
    {
        var session = new Mock<ITerminalSession>();
        session.SetupGet(x => x.IsProcessRunning).Returns(true);
        var view = new TerminalView();
        view.SetBuffer(CreateDisambiguateBuffer());
        view.SetSession(session.Object);
        view.ApplySettings(new TerminalSettings());

        bool handled = view.HandleKeyDownCore(Key.C, KeyModifiers.Control);

        Assert.True(handled);
        session.Verify(x => x.SendInput("\x1b[99;5u"), Times.Once);
    }

    [AvaloniaFact]
    public void KittyProtocolOn_Disambiguate_DeadSession_EnterCarveOutPreserved()
    {
        // Regression: TerminalPane's "[Press Enter to reconnect]" handler must still see this
        // Enter bubble up (handled == false) even with disambiguate on and a modifier held.
        var session = new Mock<ITerminalSession>();
        session.SetupGet(x => x.IsProcessRunning).Returns(false);
        var view = new TerminalView();
        view.SetBuffer(CreateDisambiguateBuffer());
        view.SetSession(session.Object);
        view.ApplySettings(new TerminalSettings());

        bool handled = view.HandleKeyDownCore(Key.Enter, KeyModifiers.Shift);

        Assert.False(handled);
        session.Verify(x => x.SendInput(It.IsAny<string>()), Times.Never);
    }

    [AvaloniaFact]
    public void KittyProtocolOn_Disambiguate_AltVSendsCsiUNotEscapeV()
    {
        // Proves the ordering at HandleKeyDownCore's top (kitty before Alt-sends-ESC).
        var session = new Mock<ITerminalSession>();
        session.SetupGet(x => x.IsProcessRunning).Returns(true);
        var view = new TerminalView();
        view.SetBuffer(CreateDisambiguateBuffer());
        view.SetSession(session.Object);
        view.ApplySettings(new TerminalSettings());

        bool handled = view.HandleKeyDownCore(Key.V, KeyModifiers.Alt);

        Assert.True(handled);
        session.Verify(x => x.SendInput("\x1b[118;3u"), Times.Once);
        session.Verify(x => x.SendInput("\x1bv"), Times.Never);
    }

    [AvaloniaFact]
    public void KittyProtocolOn_Disambiguate_ShiftTabSendsCsiUNotBackTab()
    {
        // Proves the ordering at HandleKeyDownCore's top (kitty before the legacy Tab switch).
        var session = new Mock<ITerminalSession>();
        session.SetupGet(x => x.IsProcessRunning).Returns(true);
        var view = new TerminalView();
        view.SetBuffer(CreateDisambiguateBuffer());
        view.SetSession(session.Object);
        view.ApplySettings(new TerminalSettings());

        bool handled = view.HandleKeyDownCore(Key.Tab, KeyModifiers.Shift);

        Assert.True(handled);
        session.Verify(x => x.SendInput("\x1b[9;2u"), Times.Once);
        session.Verify(x => x.SendInput("\x1b[Z"), Times.Never);
    }

    [AvaloniaFact]
    public void KittyProtocolOn_Disambiguate_AltGrControlAlt_FallsThroughToTextPath_KeyDownNotHandled()
    {
        // Blocker 1 regression (#277 review): on Windows, AltGr is reported as Control|Alt.
        // Before the carve-out in EncodeKittyKey, this returned true with a CSI u sequence,
        // which sets e.Handled = true in OnKeyDown and suppresses the WM_CHAR carrying the
        // AltGr-composed character (e.g. '@' for AltGr+Q on a German layout). It must return
        // false here so OnTextInput still gets a chance to deliver the composed text.
        var session = new Mock<ITerminalSession>();
        session.SetupGet(x => x.IsProcessRunning).Returns(true);
        var view = new TerminalView();
        view.SetBuffer(CreateDisambiguateBuffer());
        view.SetSession(session.Object);
        view.ApplySettings(new TerminalSettings());

        bool handled = view.HandleKeyDownCore(Key.Q, KeyModifiers.Alt | KeyModifiers.Control);

        Assert.False(handled);
        session.Verify(x => x.SendInput(It.IsAny<string>()), Times.Never);
    }

    [AvaloniaFact]
    public void KittyProtocolOff_KillSwitch_DisambiguatePushedByApp_LegacyBytesStillSent()
    {
        // Blocker 2 (#277 review): the kill switch must win even when a TUI already pushed
        // flag 1 directly onto the buffer's ModeState - TryEncodeKittyKey must simply never be
        // reached, so legacy bytes go out exactly as if the protocol had never existed.
        var session = new Mock<ITerminalSession>();
        session.SetupGet(x => x.IsProcessRunning).Returns(true);
        var view = new TerminalView();
        view.SetBuffer(CreateDisambiguateBuffer());
        view.SetSession(session.Object);
        view.ApplySettings(new TerminalSettings { EnableKittyKeyboardProtocol = false });

        bool handled = view.HandleKeyDownCore(Key.Enter, KeyModifiers.Shift);

        Assert.True(handled);
        session.Verify(x => x.SendInput("\r"), Times.Once);
        session.Verify(x => x.SendInput("\x1b[13;2u"), Times.Never);
    }
}
