using Avalonia.Input;
using NovaTerminal.CommandAssist.Application;

namespace NovaTerminal.Tests.CommandAssist;

public sealed class CommandAssistKeyRouterTests
{
    [Theory]
    [InlineData(Key.Tab)]
    [InlineData(Key.Up)]
    [InlineData(Key.Down)]
    [InlineData(Key.Escape)]
    public void IsAssistOwnedKey_WhenAssistVisible_ConsumesNavigationKeys(Key key)
    {
        bool owned = CommandAssistKeyRouter.IsAssistOwnedKey(true, key, KeyModifiers.None);

        Assert.True(owned);
    }

    [Fact]
    public void IsAssistOwnedKey_WhenAssistVisible_ConsumesPinShortcut()
    {
        bool owned = CommandAssistKeyRouter.IsAssistOwnedKey(true, Key.P, KeyModifiers.Control | KeyModifiers.Shift);

        Assert.True(owned);
    }

    [Theory]
    [InlineData(Key.Tab)]
    [InlineData(Key.Up)]
    [InlineData(Key.Down)]
    [InlineData(Key.Escape)]
    public void IsAssistOwnedKey_WhenAssistHidden_DoesNotConsumeNavigationKeys(Key key)
    {
        bool owned = CommandAssistKeyRouter.IsAssistOwnedKey(false, key, KeyModifiers.None);

        Assert.False(owned);
    }
}
