using NovaTerminal.CommandAssist.Application;

namespace NovaTerminal.Tests.CommandAssist;

public sealed class CommandAssistKeyRouterTests
{
    [Theory]
    [InlineData(AssistKey.Up)]
    [InlineData(AssistKey.Down)]
    [InlineData(AssistKey.Escape)]
    public void IsAssistOwnedKey_WhenAssistVisible_ConsumesNavigationKeys(AssistKey key)
    {
        bool owned = CommandAssistKeyRouter.IsAssistOwnedKey(true, key, AssistModifiers.None);

        Assert.True(owned);
    }

    [Fact]
    public void IsAssistOwnedKey_WhenAssistVisible_DoesNotConsumeTab()
    {
        bool owned = CommandAssistKeyRouter.IsAssistOwnedKey(true, AssistKey.Tab, AssistModifiers.None);

        Assert.False(owned);
    }

    [Fact]
    public void IsAssistOwnedKey_WhenAssistVisible_ConsumesCtrlEnter()
    {
        bool owned = CommandAssistKeyRouter.IsAssistOwnedKey(true, AssistKey.Enter, AssistModifiers.Control);

        Assert.True(owned);
    }

    [Fact]
    public void IsAssistOwnedKey_WhenAssistVisible_ConsumesPinShortcut()
    {
        bool owned = CommandAssistKeyRouter.IsAssistOwnedKey(
            true,
            AssistKey.P,
            AssistModifiers.Control | AssistModifiers.Shift);

        Assert.True(owned);
    }

    [Theory]
    [InlineData(AssistKey.Up)]
    [InlineData(AssistKey.Down)]
    [InlineData(AssistKey.Escape)]
    public void IsAssistOwnedKey_WhenAssistHidden_DoesNotConsumeNavigationKeys(AssistKey key)
    {
        bool owned = CommandAssistKeyRouter.IsAssistOwnedKey(false, key, AssistModifiers.None);

        Assert.False(owned);
    }

    [Fact]
    public void IsAssistOwnedKey_WhenAssistHidden_DoesNotConsumeCtrlEnter()
    {
        bool owned = CommandAssistKeyRouter.IsAssistOwnedKey(false, AssistKey.Enter, AssistModifiers.Control);

        Assert.False(owned);
    }
}
