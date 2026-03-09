using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Moq;
using NovaTerminal.Core;

namespace NovaTerminal.Tests.CommandAssist;

public sealed class TerminalViewKeyHandlingTests
{
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
}
