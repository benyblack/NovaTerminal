using NovaTerminal.CommandAssist.Application;

namespace NovaTerminal.Tests.CommandAssist;

public sealed class CommandAssistKeyRouterTests
{
    private static readonly AssistKeyState Hidden = new(IsSurfaceVisible: false, IsAcceptOnEnterArmed: false);
    private static readonly AssistKeyState Visible = new(IsSurfaceVisible: true, IsAcceptOnEnterArmed: false);
    private static readonly AssistKeyState Browsing = new(IsSurfaceVisible: true, IsAcceptOnEnterArmed: true);

    [Theory]
    [InlineData(AssistKey.Up)]
    [InlineData(AssistKey.Down)]
    [InlineData(AssistKey.Escape)]
    public void IsAssistOwnedKey_WhenAssistVisible_ConsumesNavigationKeys(AssistKey key)
    {
        bool owned = CommandAssistKeyRouter.IsAssistOwnedKey(Visible, key, AssistModifiers.None);

        Assert.True(owned);
    }

    [Fact]
    public void IsAssistOwnedKey_WhenAssistVisible_DoesNotConsumeTab()
    {
        bool owned = CommandAssistKeyRouter.IsAssistOwnedKey(Visible, AssistKey.Tab, AssistModifiers.None);

        Assert.False(owned);
    }

    /// <summary>Tab stays shell-owned even while browsing: completion belongs to the shell.</summary>
    [Fact]
    public void IsAssistOwnedKey_WhenBrowsing_StillDoesNotConsumeTab()
    {
        bool owned = CommandAssistKeyRouter.IsAssistOwnedKey(Browsing, AssistKey.Tab, AssistModifiers.None);

        Assert.False(owned);
    }

    [Fact]
    public void IsAssistOwnedKey_WhenAssistVisible_ConsumesCtrlEnter()
    {
        bool owned = CommandAssistKeyRouter.IsAssistOwnedKey(Visible, AssistKey.Enter, AssistModifiers.Control);

        Assert.True(owned);
    }

    [Fact]
    public void IsAssistOwnedKey_WhenAssistVisible_ConsumesPinShortcut()
    {
        bool owned = CommandAssistKeyRouter.IsAssistOwnedKey(
            Visible,
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
        bool owned = CommandAssistKeyRouter.IsAssistOwnedKey(Hidden, key, AssistModifiers.None);

        Assert.False(owned);
    }

    [Fact]
    public void IsAssistOwnedKey_WhenAssistHidden_DoesNotConsumeCtrlEnter()
    {
        bool owned = CommandAssistKeyRouter.IsAssistOwnedKey(Hidden, AssistKey.Enter, AssistModifiers.Control);

        Assert.False(owned);
    }

    // -------------------------------------------------------- accept on Enter (V2 Phase 3a)

    /// <summary>
    /// The owner's first report, at the routing layer: while a row is selected in an open popup,
    /// <c>Enter</c> is the assist's, so it can insert instead of submitting an empty command line.
    /// </summary>
    [Fact]
    public void IsAssistOwnedKey_WhenBrowsing_ConsumesPlainEnter()
    {
        bool owned = CommandAssistKeyRouter.IsAssistOwnedKey(Browsing, AssistKey.Enter, AssistModifiers.None);

        Assert.True(owned);
    }

    /// <summary>
    /// The typing flow. A bubble with no row selected leaves <c>Enter</c> to the shell, so
    /// type-a-command-and-run is exactly as it was before Phase 3a.
    /// </summary>
    [Fact]
    public void IsAssistOwnedKey_WhenVisibleButNotBrowsing_LeavesPlainEnterToTheShell()
    {
        bool owned = CommandAssistKeyRouter.IsAssistOwnedKey(Visible, AssistKey.Enter, AssistModifiers.None);

        Assert.False(owned);
    }

    [Fact]
    public void IsAssistOwnedKey_WhenHidden_LeavesPlainEnterToTheShell()
    {
        bool owned = CommandAssistKeyRouter.IsAssistOwnedKey(
            new AssistKeyState(IsSurfaceVisible: false, IsAcceptOnEnterArmed: true),
            AssistKey.Enter,
            AssistModifiers.None);

        Assert.False(owned);
    }

    /// <summary>
    /// A modified <c>Enter</c> is never the accept key, however armed the session is. Shift+Enter is a
    /// newline in several line editors, and under the kitty disambiguate tier every modified Enter is a
    /// distinct CSI u sequence the shell may act on - so "unmodified" is exact rather than "no Alt".
    /// </summary>
    [Theory]
    [InlineData(AssistModifiers.Shift)]
    [InlineData(AssistModifiers.Alt)]
    [InlineData(AssistModifiers.Control | AssistModifiers.Shift)]
    public void IsAssistOwnedKey_WhenBrowsing_DoesNotConsumeModifiedEnterAsAccept(AssistModifiers modifiers)
    {
        bool owned = CommandAssistKeyRouter.IsAssistOwnedKey(Browsing, AssistKey.Enter, modifiers);

        Assert.False(owned);
    }

    /// <summary>Ctrl+Enter is unaffected by the browse state; it worked everywhere and still does.</summary>
    [Fact]
    public void IsAssistOwnedKey_WhenBrowsing_StillConsumesCtrlEnter()
    {
        bool owned = CommandAssistKeyRouter.IsAssistOwnedKey(Browsing, AssistKey.Enter, AssistModifiers.Control);

        Assert.True(owned);
    }
}
