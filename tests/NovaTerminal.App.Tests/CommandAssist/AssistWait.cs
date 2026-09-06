using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace NovaTerminal.Tests.CommandAssist;

/// <summary>
/// Waits for the assist surface to reach a state, rather than for a fixed number of milliseconds.
/// </summary>
/// <remarks>
/// <para>
/// A Help or Fix pass awaits its providers and then posts the surface write to the pane's
/// dispatcher, so a test that drives one and sleeps is betting that the whole round trip fits
/// inside the sleep. On a loaded machine it does not, and the assertion on the far side fails for
/// a reason that has nothing to do with what it is testing (#424). Two tests were losing that bet
/// roughly once per full run.
/// </para>
/// <para>
/// The timeout is generous on purpose: it is there to turn a hang into a legible failure, not to
/// bound the wait tightly. A test that needs the whole two seconds is telling you something real,
/// and the failure reports what actually elapsed so that message is worth reading.
/// </para>
/// </remarks>
internal static class AssistWait
{
    private const int DefaultTimeoutMs = 2000;
    private const int PollMs = 10;

    public static async Task UntilAsync(Func<bool> condition, string because, int timeoutMs = DefaultTimeoutMs)
    {
        // A stopwatch rather than a count of polls. Multiplying iterations by the poll interval
        // assumes each Task.Delay(10) takes 10 ms, and under the loaded machine this helper exists
        // for it does not - so the ceiling would stretch exactly when it is needed, and the message
        // would name a duration that never elapsed (local codex review).
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.ElapsedMilliseconds >= timeoutMs)
            {
                throw new TimeoutException(
                    $"Timed out after {clock.ElapsedMilliseconds} ms (ceiling {timeoutMs} ms) waiting until {because}.");
            }

            await Task.Delay(PollMs);
        }
    }
}
