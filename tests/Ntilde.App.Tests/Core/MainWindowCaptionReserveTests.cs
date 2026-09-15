using Xunit;

namespace NovaTerminal.Tests.Core;

/// <summary>
/// Covers <see cref="NovaTerminal.MainWindow.ComputeCaptionButtonReserve"/>, which replaced the
/// literal 140 in MainWindow.axaml's title bar margin.
///
/// 140 is three 45px caption buttons plus 2px spacing plus the 1px border margin, and it is only
/// right when all three buttons are there. How many are there is not the app's decision: the theme
/// hides minimize and maximize on <c>:not(:has-minimize)</c> / <c>:not(:has-maximize)</c>, and X11
/// sets neither pseudoclass unless the window manager advertises the action. Under a tiling
/// compositor only close survives, so ~94px of the reserve was dead space.
///
/// The lookup that feeds this - hopping to TopLevelHost and translating the strip's origin - needs a
/// real platform window and is not covered here. What is covered is the arithmetic and, more
/// usefully, its behaviour at the edges: every input comes from a live layout pass that can be read
/// mid-transition, and a bad answer here moves the user's buttons off the window.
/// </summary>
public class MainWindowCaptionReserveTests
{
    private const double Gutter = NovaTerminal.MainWindow.CaptionReserveGutter;

    // Measured off a real X11 window on Hyprland (only the close button survives there): the window
    // spanned 1227 DIPs, the strip sat at x=1173 and was 45 wide. Pinned as the concrete case so a
    // change to the formula has to explain itself against a real observation rather than an invented
    // one.
    private const double RealWindowWidth = 1227;
    private const double RealStripLeft = 1173;
    private const double RealStripWidth = 45;

    [Fact]
    public void CloseOnlyStrip_ReservesTheStripPlusItsInsetPlusTheGutter()
    {
        var reserve = NovaTerminal.MainWindow.ComputeCaptionButtonReserve(
            RealWindowWidth, RealStripLeft, RealStripWidth);

        // 1227 - 1173 = 54 (the 45px button plus the 9px frame inset to its right), + 8 gutter.
        Assert.Equal(62, reserve);
    }

    [Fact]
    public void CloseOnlyStrip_ReservesFarLessThanTheOldHardcoded140()
    {
        var reserve = NovaTerminal.MainWindow.ComputeCaptionButtonReserve(
            RealWindowWidth, RealStripLeft, RealStripWidth);

        Assert.True(reserve < 140, $"expected the measured reserve to beat the old constant, got {reserve}");
    }

    /// <summary>
    /// The regression in one assertion: a window whose WM advertises all three actions and one whose
    /// WM advertises none differ by exactly the width of the two hidden buttons, and nothing else.
    /// The old constant charged the wider figure to both.
    /// </summary>
    [Fact]
    public void ThreeButtonAndOneButtonStrips_DifferByExactlyTheHiddenButtonWidth()
    {
        const double windowWidth = 1227;
        const double inset = 9;

        // 3 x 45 plus 2 x 2px spacing; then the same strip with minimize and maximize hidden.
        const double threeWide = 139;
        const double oneWide = 45;

        var three = NovaTerminal.MainWindow.ComputeCaptionButtonReserve(
            windowWidth, windowWidth - inset - threeWide, threeWide);
        var one = NovaTerminal.MainWindow.ComputeCaptionButtonReserve(
            windowWidth, windowWidth - inset - oneWide, oneWide);

        Assert.Equal(threeWide - oneWide, three - one);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void NoStripToClear_CollapsesToTheGutter(double stripWidth)
    {
        // Zero width is a real state, not just a guard: the theme collapses the whole overlay panel
        // in fullscreen. A platform drawing native chrome instead reaches the same answer via a null
        // strip in UpdateCaptionButtonReserve.
        var reserve = NovaTerminal.MainWindow.ComputeCaptionButtonReserve(
            RealWindowWidth, RealStripLeft, stripWidth);

        Assert.Equal(Gutter, reserve);
    }

    [Theory]
    [InlineData(double.NaN, RealStripLeft)]
    [InlineData(RealWindowWidth, double.NaN)]
    public void UnmeasuredGeometry_CollapsesToTheGutter(double windowWidth, double stripLeft)
    {
        // Both are NaN before the first arrange. Without the guard the subtraction would propagate
        // NaN into a Thickness, and Avalonia lays out a NaN margin as zero - silently putting our
        // buttons underneath the caption buttons rather than failing.
        var reserve = NovaTerminal.MainWindow.ComputeCaptionButtonReserve(
            windowWidth, stripLeft, RealStripWidth);

        Assert.Equal(Gutter, reserve);
    }

    [Fact]
    public void AStripMeasuredPastTheRightEdge_NeverYieldsANegativeReserve()
    {
        // left > windowWidth is reachable mid-transition, when the frame has resized on one layout
        // pass and the strip has not caught up. A negative margin is legal in Avalonia and would let
        // our buttons overhang the window, so the floor matters.
        var reserve = NovaTerminal.MainWindow.ComputeCaptionButtonReserve(
            windowWidth: 800, captionStripLeft: 900, captionStripWidth: 45);

        Assert.Equal(Gutter, reserve);
    }

    [Fact]
    public void AnAbsurdlyWideStrip_IsClampedToHalfTheWindow()
    {
        // Same mid-transition origin: a strip still holding a previous, much larger window's
        // coordinates would otherwise reserve most of the title bar and push our buttons out of view.
        var reserve = NovaTerminal.MainWindow.ComputeCaptionButtonReserve(
            windowWidth: 800, captionStripLeft: 10, captionStripWidth: 700);

        Assert.Equal(400, reserve);
    }

    [Fact]
    public void ANarrowWindow_StillHonoursTheGutterFloorOverTheHalfWidthCeiling()
    {
        // The ceiling is max(gutter, width/2) rather than width/2 so the two bounds cannot cross on a
        // window narrower than twice the gutter, which Math.Clamp would throw on.
        var reserve = NovaTerminal.MainWindow.ComputeCaptionButtonReserve(
            windowWidth: 10, captionStripLeft: 0, captionStripWidth: 45);

        Assert.Equal(Gutter, reserve);
    }
}
