using System.Collections.Generic;
using NovaTerminal.Shell;
using Xunit;

namespace NovaTerminal.Tests
{
    public sealed class TabDragModelTests
    {
        [Theory]
        [InlineData(100, 100, false)] // no movement
        [InlineData(100, 104.9, false)] // just under default threshold
        [InlineData(100, 105, true)] // exactly at threshold
        [InlineData(100, 106, true)] // past threshold
        [InlineData(100, 95, true)] // either direction counts
        [InlineData(100, 95.1, false)] // just under threshold in the negative direction
        public void ShouldStartDrag_UsesAbsoluteDeltaVersusThreshold(double pressPos, double currentPos, bool expected)
            => Assert.Equal(expected, TabDragModel.ShouldStartDrag(pressPos, currentPos));

        [Theory]
        [InlineData(100, 109.9, 10, false)]
        [InlineData(100, 110, 10, true)]
        [InlineData(100, 90, 10, true)]
        [InlineData(100, 90.1, 10, false)]
        public void ShouldStartDrag_HonorsCustomThreshold(double pressPos, double currentPos, double threshold, bool expected)
            => Assert.Equal(expected, TabDragModel.ShouldStartDrag(pressPos, currentPos, threshold));

        [Theory]
        [InlineData(double.NaN, 100)]
        [InlineData(100, double.NaN)]
        [InlineData(double.PositiveInfinity, 100)]
        [InlineData(100, double.PositiveInfinity)]
        [InlineData(double.NegativeInfinity, 100)]
        [InlineData(100, double.NegativeInfinity)]
        public void ShouldStartDrag_NonFinitePositionsReturnFalse(double pressPos, double currentPos)
            => Assert.False(TabDragModel.ShouldStartDrag(pressPos, currentPos));

        [Fact]
        public void ComputeInsertIndex_EmptyListReturnsZero()
            => Assert.Equal(0, TabDragModel.ComputeInsertIndex(new List<double>(), pointerPos: 100));

        [Fact]
        public void ComputeInsertIndex_PointerAboveAllHeadersReturnsZero()
            => Assert.Equal(0, TabDragModel.ComputeInsertIndex(new List<double> { 100, 200, 300 }, pointerPos: 50));

        [Fact]
        public void ComputeInsertIndex_PointerBelowAllHeadersReturnsCount()
            => Assert.Equal(3, TabDragModel.ComputeInsertIndex(new List<double> { 100, 200, 300 }, pointerPos: 350));

        [Fact]
        public void ComputeInsertIndex_PointerExactlyOnCenterStaysBeforeThatHeader()
            => Assert.Equal(1, TabDragModel.ComputeInsertIndex(new List<double> { 100, 200, 300 }, pointerPos: 200));

        [Theory]
        [InlineData(150, 1)] // between first and second
        [InlineData(250, 2)] // between second and third
        [InlineData(100.5, 1)] // just past the first center
        [InlineData(299.9, 2)] // just before the last center
        public void ComputeInsertIndex_InterleavedPositionsCountPassedHeaders(double pointerPos, int expected)
            => Assert.Equal(expected, TabDragModel.ComputeInsertIndex(new List<double> { 100, 200, 300 }, pointerPos));

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(double.NegativeInfinity)]
        public void ComputeInsertIndex_NonFinitePointerReturnsZero(double pointerPos)
            => Assert.Equal(0, TabDragModel.ComputeInsertIndex(new List<double> { 100, 200, 300 }, pointerPos));

        [Theory]
        [InlineData(300)] // well inside
        [InlineData(24)] // exactly at the start boundary is inside the safe zone
        [InlineData(576)] // exactly at the end boundary is inside the safe zone
        public void ComputeAutoScrollDelta_PointerInsideSafeZoneReturnsZero(double pointerPos)
            => Assert.Equal(0, TabDragModel.ComputeAutoScrollDelta(viewportStart: 0, viewportLength: 600, pointerPos));

        [Theory]
        [InlineData(0)] // at the very edge
        [InlineData(23.9)] // just inside the edge zone
        [InlineData(-10)] // dragged past the start of the viewport
        public void ComputeAutoScrollDelta_NearStartEdgeScrollsTowardStart(double pointerPos)
            => Assert.Equal(-TabDragModel.AutoScrollStep,
                TabDragModel.ComputeAutoScrollDelta(viewportStart: 0, viewportLength: 600, pointerPos));

        [Theory]
        [InlineData(600)] // at the very edge
        [InlineData(576.1)] // just inside the edge zone
        [InlineData(610)] // dragged past the end of the viewport
        public void ComputeAutoScrollDelta_NearEndEdgeScrollsTowardEnd(double pointerPos)
            => Assert.Equal(TabDragModel.AutoScrollStep,
                TabDragModel.ComputeAutoScrollDelta(viewportStart: 0, viewportLength: 600, pointerPos));

        [Fact]
        public void ComputeAutoScrollDelta_NonZeroViewportStartMeasuresZonesRelativeToIt()
        {
            // Viewport [100..400]: start zone is [100..124), end zone is (376..400].
            Assert.Equal(0, TabDragModel.ComputeAutoScrollDelta(viewportStart: 100, viewportLength: 300, pointerPos: 124));
            Assert.Equal(-TabDragModel.AutoScrollStep,
                TabDragModel.ComputeAutoScrollDelta(viewportStart: 100, viewportLength: 300, pointerPos: 123.9));
            Assert.Equal(0, TabDragModel.ComputeAutoScrollDelta(viewportStart: 100, viewportLength: 300, pointerPos: 376));
            Assert.Equal(TabDragModel.AutoScrollStep,
                TabDragModel.ComputeAutoScrollDelta(viewportStart: 100, viewportLength: 300, pointerPos: 376.1));
        }

        [Fact]
        public void ComputeAutoScrollDelta_HonorsCustomEdgeZoneAndStep()
            => Assert.Equal(-20, TabDragModel.ComputeAutoScrollDelta(viewportStart: 0, viewportLength: 600, pointerPos: 29.9, edgeZone: 30, step: 20));

        [Theory]
        [InlineData(0)] // no viewport
        [InlineData(-50)] // nonsensical length
        [InlineData(48)] // edgeZone*2 == length leaves no safe zone
        [InlineData(40)] // edgeZone*2 > length
        public void ComputeAutoScrollDelta_DegenerateViewportReturnsZero(double viewportLength)
            => Assert.Equal(0, TabDragModel.ComputeAutoScrollDelta(viewportStart: 0, viewportLength, pointerPos: 10));

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(double.NegativeInfinity)]
        public void ComputeAutoScrollDelta_NonFinitePointerReturnsZero(double pointerPos)
            => Assert.Equal(0, TabDragModel.ComputeAutoScrollDelta(viewportStart: 0, viewportLength: 600, pointerPos));
    }
}
