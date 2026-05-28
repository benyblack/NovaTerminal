using Avalonia.Input;
using NovaTerminal.Core.Shortcuts;

namespace NovaTerminal.Tests.Core;

public sealed class ShortcutMatcherTests
{
    [Fact]
    public void Matches_AcceptsSettingsShortcutCommaBinding()
    {
        var args = new KeyEventArgs
        {
            Key = Key.OemComma,
            KeyModifiers = KeyModifiers.Control,
        };

        Assert.True(ShortcutMatcher.Matches(args, "Ctrl+,"));
    }

    [Fact]
    public void Matches_RejectsExtraModifiers()
    {
        var args = new KeyEventArgs
        {
            Key = Key.OemComma,
            KeyModifiers = KeyModifiers.Control | KeyModifiers.Shift,
        };

        Assert.False(ShortcutMatcher.Matches(args, "Ctrl+,"));
    }
}
