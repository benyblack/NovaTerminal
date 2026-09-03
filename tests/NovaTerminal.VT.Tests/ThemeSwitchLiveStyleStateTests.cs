namespace NovaTerminal.VT.Tests;

// Regression tests for "text printed after a theme switch keeps the old theme's colors".
//
// The user-visible symptom: switch light -> dark (or back) and parts of the screen stay in the
// previous palette - most visibly a fresh tab's very first prompt line ("PS C:\Users\behna>")
// and long-lived TUI output, while everything printed later looks right.
//
// Root cause: the CurrentForeground/CurrentBackground setters clear IsDefaultForeground/
// IsDefaultBackground as a side effect (assigning a color means "an SGR asked for this exact
// color"). UpdateThemeColors and Reset assigned through those setters while intending to keep
// the default flags, so the live SGR state silently became "explicit" the moment a theme was
// applied - and TerminalView.ApplySettings applies a theme on every settings pass, including a
// new pane's first one. Every cell written from then on stored a literal resolved color with no
// default flag, so it never followed a later theme switch. Worse, the `if (IsDefaultForeground)`
// guard was false on the second switch, so the live state stayed pinned to the OLD theme.
public class ThemeSwitchLiveStyleStateTests
{
    private static readonly string Esc = ((char)0x1b).ToString();

    private static TerminalTheme MakeTheme(string name, byte fg, byte bg) => new()
    {
        Name = name,
        Foreground = TermColor.FromRgb(fg, fg, fg),
        Background = TermColor.FromRgb(bg, bg, bg)
    };

    /// <summary>Mirrors TerminalView.ApplySettings: swap Theme, then migrate existing cells.</summary>
    private static void ApplyTheme(TerminalBuffer buffer, TerminalTheme next)
    {
        var old = buffer.Theme;
        buffer.Theme = next;
        buffer.UpdateThemeColors(old);
    }

    [Fact]
    public void ThemeSwitch_KeepsLiveSgrStateDefault()
    {
        var themeA = MakeTheme("A", 0x10, 0x20);
        var buffer = new TerminalBuffer(20, 4) { Theme = themeA };

        ApplyTheme(buffer, MakeTheme("B", 0xE0, 0xF0));

        Assert.True(buffer.IsDefaultForeground, "theme switch must not make the live foreground explicit");
        Assert.True(buffer.IsDefaultBackground, "theme switch must not make the live background explicit");
    }

    [Fact]
    public void TextPrintedAfterThemeSwitch_StillFollowsTheNextThemeSwitch()
    {
        var themeA = MakeTheme("A", 0x10, 0x20);
        var themeB = MakeTheme("B", 0x30, 0x40);
        var themeC = MakeTheme("C", 0xE0, 0xF0);

        var buffer = new TerminalBuffer(20, 4) { Theme = themeA };
        var parser = new AnsiParser(buffer);

        // A -> B, then the shell prints its prompt (plain, no SGR - inherits the live state).
        ApplyTheme(buffer, themeB);
        parser.Process(@"PS C:\>");

        var written = buffer.ViewportRows[0].Cells[0];
        Assert.True(written.IsDefaultForeground, "plain text must be stored as a theme-following default");
        Assert.True(written.IsDefaultBackground, "plain text must be stored as a theme-following default");

        // B -> C: that prompt line has to move with the theme.
        ApplyTheme(buffer, themeC);

        var migrated = buffer.ViewportRows[0].Cells[0];
        Assert.Equal(themeC.Foreground.ToUint(), migrated.Fg);
        Assert.Equal(themeC.Background.ToUint(), migrated.Bg);
    }

    [Fact]
    public void FullReset_KeepsLiveSgrStateDefault()
    {
        var buffer = new TerminalBuffer(20, 4) { Theme = MakeTheme("A", 0x10, 0x20) };
        var parser = new AnsiParser(buffer);

        // ESC, not \x1b: a \x escape swallows up to four hex digits, so "\x1bc" would be the
        // single char U+01BC instead of ESC followed by 'c' (RIS).
        parser.Process(Esc + "[31mred" + Esc + "c");

        Assert.True(buffer.IsDefaultForeground, "RIS must leave the foreground following the theme");
        Assert.True(buffer.IsDefaultBackground, "RIS must leave the background following the theme");

        parser.Process("after");
        var cell = buffer.ViewportRows[0].Cells[0];
        Assert.True(cell.IsDefaultForeground);
        Assert.True(cell.IsDefaultBackground);
    }

    [Fact]
    public void FullResetOnAltScreen_KeepsLiveSgrStateDefault()
    {
        var buffer = new TerminalBuffer(20, 4) { Theme = MakeTheme("A", 0x10, 0x20) };
        var parser = new AnsiParser(buffer);

        // Explicit color on the main screen, enter the alt screen, then RIS. Leaving the alt
        // screen restores the main screen's saved style, so RIS must not reset SGR before the
        // switch or that restore hands the explicit color straight back.
        parser.Process(Esc + "[31m" + Esc + "[?1049h" + Esc + "c");

        Assert.True(buffer.IsDefaultForeground, "RIS from the alt screen must leave the foreground following the theme");
        Assert.True(buffer.IsDefaultBackground, "RIS from the alt screen must leave the background following the theme");

        parser.Process("after");
        var cell = buffer.ViewportRows[0].Cells[0];
        Assert.True(cell.IsDefaultForeground);
        Assert.True(cell.IsDefaultBackground);
    }

    [Fact]
    public void DecrcAfterFullReset_DoesNotResurrectPreResetStyle()
    {
        var buffer = new TerminalBuffer(20, 4) { Theme = MakeTheme("A", 0x10, 0x20) };
        var parser = new AnsiParser(buffer);

        // DECSC banks an explicit color, RIS reinitializes, then DECRC. RIS reinitializes the
        // saved slots too, so the restore must not bring the pre-RIS style back.
        parser.Process(Esc + "[31m" + Esc + "7" + Esc + "c" + Esc + "8");

        Assert.True(buffer.IsDefaultForeground, "RIS must reinitialize the saved cursor style");
        Assert.True(buffer.IsDefaultBackground, "RIS must reinitialize the saved cursor style");
    }
}
