using NovaTerminal.Shell;
using Xunit;

namespace NovaTerminal.Tests
{
    public sealed class TabStripLayoutTests
    {
        [Theory]
        [InlineData("Vertical", true)]
        [InlineData("vertical", true)]
        [InlineData("VERTICAL", true)]
        [InlineData("Horizontal", false)]
        [InlineData("horizontal", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        [InlineData("Sideways", false)]
        [InlineData("2", false)] // numeric string must not sneak through Enum.TryParse
        public void IsVertical_ParsesCaseInsensitivelyWithHorizontalFallback(string? raw, bool expected)
            => Assert.Equal(expected, TabStripLayout.IsVertical(raw));

        [Theory]
        [InlineData(220, 220)]
        [InlineData(100, 140)]
        [InlineData(9999, 600)]
        [InlineData(0, 220)]
        [InlineData(-5, 220)]
        [InlineData(double.NaN, 220)]
        [InlineData(double.PositiveInfinity, 220)]
        public void ClampSidebarWidth_ClampsAndDefendsAgainstGarbage(double input, double expected)
            => Assert.Equal(expected, TabStripLayout.ClampSidebarWidth(input));

        [Fact]
        public void ComputeDraggedWidth_AddsDeltaAndClamps()
        {
            Assert.Equal(250, TabStripLayout.ComputeDraggedWidth(startWidth: 220, startX: 100, currentX: 130));
            Assert.Equal(140, TabStripLayout.ComputeDraggedWidth(startWidth: 150, startX: 100, currentX: 0));
            Assert.Equal(600, TabStripLayout.ComputeDraggedWidth(startWidth: 590, startX: 0, currentX: 500));
        }
    }
}
