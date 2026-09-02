using System;
using System.Collections.Generic;

namespace NovaTerminal.Shell
{
    /// <summary>
    /// Pure math for pointer-driven tab reordering. Axis-generic: the caller passes
    /// whichever coordinate applies (Y in vertical mode, X in horizontal), so there is
    /// no orientation concept here. No Avalonia types so the tests stay plain [Fact]s
    /// (same split as TabStripLayout).
    /// </summary>
    internal static class TabDragModel
    {
        /// <summary>Movement (DIP) along the drag axis required before a press becomes a drag.</summary>
        internal const double DragStartThreshold = 5;

        /// <summary>Distance (DIP) from either viewport edge that triggers auto-scroll during a drag.</summary>
        internal const double AutoScrollEdgeZone = 24;

        /// <summary>Distance (DIP) scrolled per auto-scroll tick.</summary>
        internal const double AutoScrollStep = 12;

        /// <summary>True once the pointer has moved at least <paramref name="threshold"/> along the
        /// drag axis from the press position, in either direction.</summary>
        internal static bool ShouldStartDrag(double pressPos, double currentPos, double threshold = DragStartThreshold)
            => Math.Abs(currentPos - pressPos) >= threshold;

        /// <summary>Insertion index in [0..count]: the number of headers whose center is strictly
        /// less than <paramref name="pointerPos"/>, i.e. the drop happens before the first header
        /// whose center is below/right of the pointer. An empty list returns 0.</summary>
        internal static int ComputeInsertIndex(IReadOnlyList<double> headerCenters, double pointerPos)
        {
            var index = 0;
            for (var i = 0; i < headerCenters.Count; i++)
            {
                if (headerCenters[i] < pointerPos)
                    index++;
            }

            return index;
        }

        /// <summary>Auto-scroll offset for one tick while dragging near a viewport edge: negative
        /// scrolls toward the start (up/left), positive toward the end (down/right), 0 inside the
        /// safe zone. Degenerate inputs (non-finite pointer, viewportLength &lt;= 0, or an edge zone
        /// that covers the whole viewport) return 0.</summary>
        internal static double ComputeAutoScrollDelta(double viewportStart, double viewportLength, double pointerPos, double edgeZone = AutoScrollEdgeZone, double step = AutoScrollStep)
        {
            if (!double.IsFinite(pointerPos) || viewportLength <= 0 || edgeZone * 2 >= viewportLength)
                return 0;

            if (pointerPos < viewportStart + edgeZone)
                return -step;
            if (pointerPos > viewportStart + viewportLength - edgeZone)
                return step;
            return 0;
        }
    }
}
