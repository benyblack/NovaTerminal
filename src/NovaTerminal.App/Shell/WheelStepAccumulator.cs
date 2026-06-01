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
        // Largest accumulated magnitude allowed before the int cast. A single wheel
        // event never legitimately approaches this; it only guards a pathological delta
        // from casting to int.MinValue (negative overflow saturates there on .NET),
        // which would make a caller's Math.Abs(steps) throw OverflowException.
        private const double MaxAccumulation = 10_000.0;

        public int Accumulate(double value)
        {
            // Drop non-finite deltas: Math.Sign(NaN) throws and (int)Infinity is
            // garbage. A bad device reading must not crash scrolling.
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return 0;
            }

            // Reverse direction -> discard the stale remainder so the reversal
            // registers immediately. Sign comparison avoids Math.Sign (NaN-safe).
            if (value != 0 && _remainder != 0 && (value > 0) != (_remainder > 0))
            {
                _remainder = 0;
            }

            _remainder = Math.Clamp(_remainder + value, -MaxAccumulation, MaxAccumulation);
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

        /// <summary>
        /// Per-notch sensitivity for forwarding wheel events to a mouse-reporting TUI.
        /// A full-notch event (|delta| &gt;= 1.0, i.e. a classic stepped wheel) forwards
        /// 1:1 so list/menu TUIs advance one item per notch; sub-notch events (precision
        /// touchpad / hi-res wheel) use the configured multiplier so scrolling stays
        /// smooth. Non-finite deltas fall through to <paramref name="configuredUnitsPerNotch"/>
        /// and are dropped later by <see cref="Accumulate(double)"/>.
        /// </summary>
        public static double ReportUnitsPerNotch(double normalizedDelta, double configuredUnitsPerNotch)
            => Math.Abs(normalizedDelta) >= 1.0 ? 1.0 : configuredUnitsPerNotch;

        /// <summary>Drops any pending fraction (e.g. when scrolling context changes).</summary>
        public void Reset() => _remainder = 0;
    }
}
