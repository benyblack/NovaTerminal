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
}
