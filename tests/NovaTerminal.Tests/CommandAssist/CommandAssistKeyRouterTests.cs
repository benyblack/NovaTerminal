using Avalonia.Input;
using NovaTerminal.CommandAssist.Application;

namespace NovaTerminal.Tests.CommandAssist;

public sealed class CommandAssistKeyRouterTests
{
    [Theory]
    [InlineData(Key.Up)]
    [InlineData(Key.Down)]
    [InlineData(Key.Escape)]
    public void IsAssistOwnedKey_WhenAssistVisible_ConsumesNavigationKeys(Key key)
    {
        bool owned = CommandAssistKeyRouter.IsAssistOwnedKey(true, key, KeyModifiers.None);

        Assert.True(owned);
    }

    [Fact]
    public void IsAssistOwnedKey_WhenAssistVisible_DoesNotConsumeTab()
    {
        bool owned = CommandAssistKeyRouter.IsAssistOwnedKey(true, Key.Tab, KeyModifiers.None);

        Assert.False(owned);
    }

    [Fact]
    public void IsAssistOwnedKey_WhenAssistVisible_ConsumesCtrlEnter()
    {
        bool owned = CommandAssistKeyRouter.IsAssistOwnedKey(true, Key.Enter, KeyModifiers.Control);

        Assert.True(owned);
    }

    [Fact]
    public void IsAssistOwnedKey_WhenAssistVisible_ConsumesPinShortcut()
    {
        bool owned = CommandAssistKeyRouter.IsAssistOwnedKey(true, Key.P, KeyModifiers.Control | KeyModifiers.Shift);

        Assert.True(owned);
    }

    [Theory]
    [InlineData(Key.Up)]
    [InlineData(Key.Down)]
    [InlineData(Key.Escape)]
    public void IsAssistOwnedKey_WhenAssistHidden_DoesNotConsumeNavigationKeys(Key key)
    {
        bool owned = CommandAssistKeyRouter.IsAssistOwnedKey(false, key, KeyModifiers.None);

        Assert.False(owned);
    }

    [Fact]
    public void IsAssistOwnedKey_WhenAssistHidden_DoesNotConsumeCtrlEnter()
    {
        bool owned = CommandAssistKeyRouter.IsAssistOwnedKey(false, Key.Enter, KeyModifiers.Control);

        Assert.False(owned);
    }
}
