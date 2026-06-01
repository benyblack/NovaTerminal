using NovaTerminal.Shell;

namespace NovaTerminal.Tests.Shell;

// Regression tests for the touchpad "runaway scroll" bug.
// Precision touchpads / hi-res wheels emit a flood of sub-notch wheel events
// (Avalonia normalizes one classic mouse notch to 1.0, but a touchpad emits
// events worth as little as 1/120 of a notch each). The accumulator must carry
// the fractional remainder between events and only emit whole steps, so one
// notch-worth of swipe produces one step rather than ~120.
public sealed class WheelStepAccumulatorTests
{
    [Fact]
    public void HighResolutionMicroEvents_SummingToOneAndAHalfNotches_EmitOneStep()
    {
        var acc = new WheelStepAccumulator();

        int totalSteps = 0;
        for (int i = 0; i < 180; i++) // 180 * (1/120) == 1.5 notches
        {
            totalSteps += acc.Accumulate(1.0 / 120.0);
        }

        // 180 sub-notch micro-events == 1.5 notches == one whole discrete step
        // (with half a notch carried). The buggy per-event behaviour emitted ~180
        // steps (mouse-reporting path) or 0 steps (standard path that truncated
        // each event to int).
        Assert.Equal(1, totalSteps);
    }

    [Fact]
    public void ClassicMouseNotch_EmitsOneStepImmediately()
    {
        var acc = new WheelStepAccumulator();

        Assert.Equal(1, acc.Accumulate(1.0));
    }

    [Fact]
    public void Remainder_CarriesBetweenEvents()
    {
        var acc = new WheelStepAccumulator();

        Assert.Equal(0, acc.Accumulate(0.6)); // 0.6 accrued, no whole step yet
        Assert.Equal(1, acc.Accumulate(0.6)); // 1.2 accrued -> one step, 0.2 carried
        Assert.Equal(0, acc.Accumulate(0.6)); // 0.8 accrued, still short
        Assert.Equal(1, acc.Accumulate(0.6)); // 1.4 accrued -> one step
    }

    [Fact]
    public void NegativeMicroEvents_SummingToOneAndAHalfNotches_EmitOneNegativeStep()
    {
        var acc = new WheelStepAccumulator();

        int totalSteps = 0;
        for (int i = 0; i < 180; i++) // -1.5 notches
        {
            totalSteps += acc.Accumulate(-1.0 / 120.0);
        }

        Assert.Equal(-1, totalSteps);
    }

    [Theory]
    [InlineData(1.0, 1)]   // 1 unit per notch  -> 1.5 units over the gesture -> 1 step (chunky)
    [InlineData(3.0, 4)]   // 3 units per notch  -> 4.5 units -> 4 steps (default)
    [InlineData(5.0, 7)]   // 5 units per notch  -> 7.5 units -> 7 steps (smoother)
    public void UnitsPerNotch_ScalesStepsProportionally(double unitsPerNotch, int expectedSteps)
    {
        // The caller scales the normalized delta by a "units per notch" sensitivity
        // before accumulating. A 1.5-notch gesture (180 sub-notch micro-events) must
        // then yield floor(1.5 * unitsPerNotch) whole steps. This is the knob that
        // trades chunkiness for smoothness in a TUI. (1.5 * u lands on a .5 midpoint
        // for these values, so the floor is float-rounding safe.)
        var acc = new WheelStepAccumulator();

        int totalSteps = 0;
        for (int i = 0; i < 180; i++) // 1.5 notches
        {
            totalSteps += acc.Accumulate(1.0 / 120.0, unitsPerNotch);
        }

        Assert.Equal(expectedSteps, totalSteps);
    }

    [Fact]
    public void DirectionReversal_DiscardsStaleRemainder()
    {
        var acc = new WheelStepAccumulator();

        Assert.Equal(0, acc.Accumulate(0.6)); // building toward an up-step

        // User reverses direction. The stale +0.6 must NOT force the user to
        // "climb back" before a down-step registers; reversal feels immediate.
        Assert.Equal(-1, acc.Accumulate(-1.0));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonFiniteInput_IsIgnored_AndDoesNotThrow(double value)
    {
        var acc = new WheelStepAccumulator();

        // Math.Sign(NaN) throws ArithmeticException, and (int)Infinity yields a
        // garbage value; a non-finite delta must be dropped, not crash scrolling.
        Assert.Equal(0, acc.Accumulate(value));
        // Accumulator stays healthy afterwards.
        Assert.Equal(1, acc.Accumulate(1.0));
    }

    [Theory]
    // Full-notch events (classic stepped wheel) forward 1:1 so a TUI moves one item
    // per notch, regardless of the configured smoothing multiplier.
    [InlineData(1.0, 3.0, 1.0)]
    [InlineData(-1.0, 3.0, 1.0)]
    [InlineData(2.0, 3.0, 1.0)]
    // Sub-notch events (precision touchpad / hi-res wheel) use the multiplier so
    // scrolling stays smooth.
    [InlineData(0.0083, 3.0, 3.0)]
    [InlineData(-0.5, 3.0, 3.0)]
    [InlineData(0.5, 8.0, 8.0)]
    public void ReportUnitsPerNotch_FullNotchForwards1to1_SubNotchUsesMultiplier(
        double delta, double configured, double expected)
    {
        Assert.Equal(expected, WheelStepAccumulator.ReportUnitsPerNotch(delta, configured));
    }

    [Theory]
    [InlineData(1e18)]
    [InlineData(-1e18)]
    public void HugeFiniteValue_DoesNotOverflowIntCast(double value)
    {
        var acc = new WheelStepAccumulator();

        // A pathological delta must not produce int.MinValue (negative overflow
        // saturates there on .NET), which would make the caller's Math.Abs(notches)
        // throw OverflowException.
        int steps = acc.Accumulate(value);

        Assert.NotEqual(int.MinValue, steps);
        // Safe to take the absolute value (as the mouse-reporting path does).
        Assert.True(Math.Abs(steps) >= 0);
    }
}
