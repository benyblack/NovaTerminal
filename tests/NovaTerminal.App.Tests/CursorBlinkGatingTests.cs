using NovaTerminal.Shell;
using Xunit;

namespace NovaTerminal.Tests
{
    /// <summary>
    /// Guards the #126 gating rule: the cursor-blink timer must only run when the cursor is
    /// actually drawn. Render() hides the cursor whenever the view lacks keyboard focus, so
    /// a blink tick on an unfocused pane produced a pixel-identical frame - a full render
    /// pass every 530 ms, per pane, indefinitely.
    ///
    /// The rule is tested as a pure function rather than by driving a real control: the
    /// timer only starts once the view is attached, visible and sized, and
    /// IsKeyboardFocusWithin is set by Avalonia's focus manager rather than by a test. A
    /// headless test would therefore pass without the visual tree ever being in the state
    /// the assertion claims to cover - see the PR for what is consequently not covered.
    /// </summary>
    public class CursorBlinkGatingTests
    {
        [Theory]
        // blink setting on, focused: the only combination that should tick.
        [InlineData(true, true, true)]
        // Focused but the user turned blinking off.
        [InlineData(true, false, false)]
        // The #126 case: blink enabled, pane unfocused - cursor is not drawn, so no ticks.
        [InlineData(false, true, false)]
        // Neither.
        [InlineData(false, false, false)]
        public void BlinkTimerRunsOnlyWhenFocusedAndEnabled(bool focused, bool blinkEnabled, bool expected)
        {
            Assert.Equal(expected, TerminalView.ShouldRunCursorBlinkTimer(blinkEnabled, focused));
        }

        [Fact]
        public void LosingFocusStopsBlinkingEvenWithTheSettingEnabled()
        {
            // Stated separately from the table because it is the actual regression: before
            // #126 the setting alone decided, so this returned true and cost a render pass.
            Assert.True(TerminalView.ShouldRunCursorBlinkTimer(blinkEnabled: true, focused: true));
            Assert.False(TerminalView.ShouldRunCursorBlinkTimer(blinkEnabled: true, focused: false));
        }
    }
}
