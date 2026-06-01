using System;

namespace NovaTerminal.Shell
{
    /// <summary>
    /// Accumulates fractional, high-resolution mouse-wheel deltas and emits whole
    /// discrete scroll steps, carrying the unconsumed fraction between events.
    /// </summary>
    /// <remarks>
    /// Avalonia normalizes one classic mouse-wheel notch to a delta of 1.0. Precision
    /// touchpads and high-resolution wheels instead emit a stream of sub-notch deltas
    /// (as small as 1/120 per event). Treating each such micro-event as its own scroll
    /// step turned one notch-worth of swipe into ~120 steps — the "runaway scroll" bug.
    /// Accumulating the fraction so one notch produces exactly one step matches how
    /// Windows Terminal behaves with the same devices.
    /// </remarks>
    public sealed class WheelStepAccumulator
    {
        private double _remainder;

        /// <summary>
        /// Adds a normalized wheel delta (positive == up) and returns the number of
        /// whole steps to emit now. The leftover fraction is retained for the next
        /// call. Reversing scroll direction discards the stale remainder so the
        /// reversal registers immediately instead of after "climbing back" through it.
        /// </summary>
        public int Accumulate(double value)
        {
            if (value != 0 && _remainder != 0 && Math.Sign(value) != Math.Sign(_remainder))
            {
                _remainder = 0;
            }

            _remainder += value;
            int steps = (int)_remainder; // truncate toward zero; keep the fraction
            _remainder -= steps;
            return steps;
        }

        /// <summary>
        /// Scales a normalized wheel delta by <paramref name="unitsPerNotch"/> (the
        /// scroll-units-per-notch sensitivity) before accumulating. Higher values yield
        /// more, finer steps per notch (smoother); lower values are coarser.
        /// </summary>
        public int Accumulate(double normalizedDelta, double unitsPerNotch)
            => Accumulate(normalizedDelta * unitsPerNotch);

        /// <summary>Drops any pending fraction (e.g. when scrolling context changes).</summary>
        public void Reset() => _remainder = 0;
    }
}
