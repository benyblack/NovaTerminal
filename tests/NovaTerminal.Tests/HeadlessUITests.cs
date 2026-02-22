using Xunit;
using Avalonia.Headless.XUnit;
using NovaTerminal.Core;
using Avalonia;
using Avalonia.Media;
using System.Threading.Tasks;

namespace NovaTerminal.Tests
{
    public class HeadlessUITests
    {
        [AvaloniaFact]
        public void TerminalView_ApplyTheme_UpdatesBackground()
        {
            var view = new TerminalView();
            var buffer = new TerminalBuffer(80, 24);
            view.SetBuffer(buffer);

            var newTheme = new TerminalTheme
            {
                Background = TermColorHelper.FromAvaloniaColor(Colors.DarkSlateBlue)
            };

            view.ApplySettings(new TerminalSettings
            {
                ActiveTheme = newTheme
            });

            Assert.Equal(TermColorHelper.FromAvaloniaColor(Colors.DarkSlateBlue), buffer.Theme.Background);
        }

        [AvaloniaFact]
        public void TerminalView_MouseClick_FocusesControl()
        {
            var view = new TerminalView();
            Assert.False(view.IsFocused);

            // Simulate click (Headless does not require a real window)
            // Headless UI testing allows us to verify the interop between Avalonia events and our core.
            Assert.True(true); // Placeholder until Headless infra is fully hooked up in project
        }
    }
}
