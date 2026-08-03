using NovaTerminal.CommandAssist.Application;

namespace NovaTerminal.Tests.CommandAssist;

public sealed class CommandAssistKeyRouterTests
{
    private static readonly AssistKeyState Hidden = new(
        IsSurfaceVisible: false,
        IsAcceptOnEnterArmed: false,
        IsSelectionUpOwned: false);

    /// <summary>A surface the user summoned: on screen, both arrows owned, nothing selected yet.</summary>
    private static readonly AssistKeyState Visible = new(
        IsSurfaceVisible: true,
        IsAcceptOnEnterArmed: false,
        IsSelectionUpOwned: true);

    /// <summary>
    /// The passive typing bubble: on screen, but <c>Up</c> is still the shell's history recall
    /// (PR #290 review).
    /// </summary>
    private static readonly AssistKeyState PassiveBubble = new(
        IsSurfaceVisible: true,
        IsAcceptOnEnterArmed: false,
        IsSelectionUpOwned: false);

    private static readonly AssistKeyState Browsing = new(
        IsSurfaceVisible: true,
        IsAcceptOnEnterArmed: true,
        IsSelectionUpOwned: true);

    [Theory]
    [InlineData(AssistKey.Up)]
    [InlineData(AssistKey.Down)]
    [InlineData(AssistKey.Escape)]
    public void IsAssistOwnedKey_WhenAssistVisible_ConsumesNavigationKeys(AssistKey key)
    {
        bool owned = CommandAssistKeyRouter.IsAssistOwnedKey(Visible, key, AssistModifiers.None);

        Assert.True(owned);
    }

    // ------------------------------- Up is the shell's while typing (PR #290 review)

    /// <summary>
    /// The reported sequence at the routing layer: with only a passive bubble up, <c>Up</c> is not
    /// Command Assist's, so it reaches the shell and recalls the previous command.
    /// </summary>
    [Fact]
    public void IsAssistOwnedKey_WithAPassiveBubbleUp_LeavesUpToTheShell()
    {
        bool owned = CommandAssistKeyRouter.IsAssistOwnedKey(PassiveBubble, AssistKey.Up, AssistModifiers.None);

        Assert.False(owned);
    }

    /// <summary>
    /// <c>Down</c> is the one-directional way in, and it stays owned in exactly the state that refuses
    /// <c>Up</c> - without this the test above would be satisfied by the assist owning no arrows at all.
    /// </summary>
    [Fact]
    public void IsAssistOwnedKey_WithAPassiveBubbleUp_StillConsumesDown()
    {
        bool owned = CommandAssistKeyRouter.IsAssistOwnedKey(PassiveBubble, AssistKey.Down, AssistModifiers.None);

        Assert.True(owned);
    }

    /// <summary>Escape is unaffected: dismissing an uninvited bubble is the one thing it is for.</summary>
    [Fact]
    public void IsAssistOwnedKey_WithAPassiveBubbleUp_StillConsumesEscape()
    {
        bool owned = CommandAssistKeyRouter.IsAssistOwnedKey(PassiveBubble, AssistKey.Escape, AssistModifiers.None);

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

    /// <summary>
    /// Pin/unpin left the router in V2 Phase 3b. It used to be a clause here on the command palette's
    /// own chord, so whether Ctrl+Shift+P opened the palette depended on whether an assist row was
    /// selected; it is a catalogued shortcut dispatched from the window now
    /// (<c>command_assist_pin</c>), and the router must not claim the palette's key.
    /// </summary>
    [Fact]
    public void IsAssistOwnedKey_WhenAssistVisible_LeavesTheCommandPaletteChordAlone()
    {
        bool owned = CommandAssistKeyRouter.IsAssistOwnedKey(
            Visible,
            NovaTerminal.Controls.AssistKeyMapper.ToAssistKey(Avalonia.Input.Key.P),
            AssistModifiers.Control | AssistModifiers.Shift);

        Assert.False(owned);
    }

    // ------------------------------------------------- exact modifiers, and rebound chords (3b)

    /// <summary>
    /// Modifiers are matched exactly now. Ctrl+Down and Alt+Up mean something to several line
    /// editors, and the router used to swallow both because it tested the key and ignored the
    /// modifiers.
    /// </summary>
    [Theory]
    [InlineData(AssistKey.Down, AssistModifiers.Control)]
    [InlineData(AssistKey.Up, AssistModifiers.Alt)]
    [InlineData(AssistKey.Escape, AssistModifiers.Shift)]
    public void IsAssistOwnedKey_WithAModifiedNavigationKey_LeavesItToTheShell(
        AssistKey key,
        AssistModifiers modifiers)
    {
        bool owned = CommandAssistKeyRouter.IsAssistOwnedKey(Visible, key, modifiers);

        Assert.False(owned);
    }

    /// <summary>The resolved action is what the host acts on, so it is worth pinning per key.</summary>
    [Fact]
    public void Resolve_WithDefaultBindings_MapsEachKeyToItsAction()
    {
        Assert.Equal(
            AssistKeyAction.Dismiss,
            CommandAssistKeyRouter.Resolve(Visible, AssistKey.Escape, AssistModifiers.None));
        Assert.Equal(
            AssistKeyAction.SelectionDown,
            CommandAssistKeyRouter.Resolve(Visible, AssistKey.Down, AssistModifiers.None));
        Assert.Equal(
            AssistKeyAction.SelectionUp,
            CommandAssistKeyRouter.Resolve(Visible, AssistKey.Up, AssistModifiers.None));
        Assert.Equal(
            AssistKeyAction.Insert,
            CommandAssistKeyRouter.Resolve(Visible, AssistKey.Enter, AssistModifiers.Control));
        Assert.Equal(
            AssistKeyAction.Accept,
            CommandAssistKeyRouter.Resolve(Browsing, AssistKey.Enter, AssistModifiers.None));
    }

    /// <summary>
    /// A rebind moves the behavior with it: swap dismiss onto Ctrl+Escape and plain Escape goes back
    /// to the shell.
    /// </summary>
    [Fact]
    public void Resolve_WithARebindingOfDismiss_FollowsTheNewChord()
    {
        AssistKeyBindings rebound = AssistKeyBindings.Default with
        {
            Dismiss = new AssistKeyBinding(AssistKey.Escape, AssistModifiers.Control)
        };

        Assert.Equal(
            AssistKeyAction.Dismiss,
            CommandAssistKeyRouter.Resolve(Visible, AssistKey.Escape, AssistModifiers.Control, rebound));
        Assert.Equal(
            AssistKeyAction.None,
            CommandAssistKeyRouter.Resolve(Visible, AssistKey.Escape, AssistModifiers.None, rebound));
    }

    /// <summary>
    /// A binding whose key is <see cref="AssistKey.None"/> - what every key the assist does not model
    /// maps to - must match nothing. Matching everything is the failure mode this guards.
    /// </summary>
    [Fact]
    public void Resolve_WithAnUnrepresentableBinding_MatchesNothing()
    {
        AssistKeyBindings broken = AssistKeyBindings.Default with
        {
            Dismiss = new AssistKeyBinding(AssistKey.None, AssistModifiers.None)
        };

        Assert.Equal(
            AssistKeyAction.None,
            CommandAssistKeyRouter.Resolve(Visible, AssistKey.None, AssistModifiers.None, broken));
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
            new AssistKeyState(IsSurfaceVisible: false, IsAcceptOnEnterArmed: true, IsSelectionUpOwned: true),
            AssistKey.Enter,
            AssistModifiers.None);

        Assert.False(owned);
    }

    /// <summary>
    /// <c>Meta</c> reaches the router now (PR #290 review), so a <c>Win+Enter</c> is not "unmodified".
    /// It used to be dropped at the App boundary, which made the router claim a key
    /// <c>TerminalPane</c>'s own <c>modifiers == KeyModifiers.None</c> check then declined to act on.
    /// </summary>
    [Fact]
    public void IsAssistOwnedKey_WhenBrowsing_DoesNotConsumeMetaEnterAsAccept()
    {
        bool owned = CommandAssistKeyRouter.IsAssistOwnedKey(Browsing, AssistKey.Enter, AssistModifiers.Meta);

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

    /// <summary>
    /// The App-side mapping the test above depends on. Without it the router receives
    /// <see cref="AssistModifiers.None"/> for a <c>Win+Enter</c> and the "unmodified Enter" rule means
    /// something different on each side of the boundary.
    /// </summary>
    [Fact]
    public void AssistKeyMapper_MapsMeta()
    {
        AssistModifiers mapped = NovaTerminal.Controls.AssistKeyMapper.ToAssistModifiers(
            Avalonia.Input.KeyModifiers.Meta);

        Assert.Equal(AssistModifiers.Meta, mapped);
    }
}
