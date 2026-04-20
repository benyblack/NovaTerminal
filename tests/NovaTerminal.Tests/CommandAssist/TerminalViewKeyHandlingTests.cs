using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Moq;
using NovaTerminal.Controls;
using NovaTerminal.Core;

namespace NovaTerminal.Tests.CommandAssist;

public sealed class TerminalViewKeyHandlingTests
{
    private sealed class TestTerminalView : TerminalView
    {
        public void RaiseGotFocusForTest()
        {
            OnGotFocus(new GotFocusEventArgs());
        }

        public void RaiseLostFocusForTest()
        {
            OnLostFocus(new RoutedEventArgs());
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
        var pane = new TerminalPane
        {
            Width = 900,
            Height = 500
        };
        var settings = TerminalSettings.Load();
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
}
