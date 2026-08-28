using NovaTerminal.Shots;

namespace NovaTerminal.ShotsTests;

public sealed class DriverWaitTests
{
    [Fact]
    public void WaitFor_ThrowsWithTheDescriptionWhenTheConditionNeverHolds()
    {
        var exception = Assert.Throws<TimeoutException>(() =>
            Driver.WaitFor(() => false, TimeSpan.FromMilliseconds(50), "the prompt to appear", pump: () => { }));

        Assert.Contains("the prompt to appear", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WaitFor_ReturnsAsSoonAsTheConditionHolds()
    {
        int calls = 0;

        Driver.WaitFor(() => ++calls >= 3, TimeSpan.FromSeconds(5), "three polls", pump: () => { });

        Assert.Equal(3, calls);
    }

    [Fact]
    public void WaitFor_ChecksTheConditionAtLeastOnceEvenWithAZeroTimeout()
    {
        // A zero timeout must not skip the check: a condition that is already true when
        // WaitFor is called must return normally, never report a timeout on something that
        // already happened.
        Driver.WaitFor(() => true, TimeSpan.Zero, "an already-satisfied condition", pump: () => { });
    }
}
