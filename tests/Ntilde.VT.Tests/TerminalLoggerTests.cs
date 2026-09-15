using System.Collections.Generic;
using Ntilde.VT;

namespace Ntilde.VT.Tests;

// Covers the leveled logging added for #109. Tests run sequentially within a class, and each
// restores the static hooks/level it touches so it doesn't leak into other tests.
public class TerminalLoggerTests
{
    [Fact]
    public void Log_String_RoutesToOnLog_Verbatim_AsInfo()
    {
        var prevOnLog = TerminalLogger.OnLog;
        var prevOnLevel = TerminalLogger.OnLogLevel;
        var prevMin = TerminalLogger.MinimumLevel;
        try
        {
            TerminalLogger.ResetRateLimiter();
            TerminalLogger.OnLogLevel = null;
            TerminalLogger.MinimumLevel = LogLevel.Debug;
            string? got = null;
            TerminalLogger.OnLog = m => got = m;

            TerminalLogger.Log("hello"); // back-compat: Log(string) == Info, no prefix

            Assert.Equal("hello", got);
        }
        finally
        {
            TerminalLogger.OnLog = prevOnLog;
            TerminalLogger.OnLogLevel = prevOnLevel;
            TerminalLogger.MinimumLevel = prevMin;
        }
    }

    [Fact]
    public void LeveledMessages_FallBackToOnLog_WithPrefix()
    {
        var prevOnLog = TerminalLogger.OnLog;
        var prevOnLevel = TerminalLogger.OnLogLevel;
        var prevMin = TerminalLogger.MinimumLevel;
        try
        {
            TerminalLogger.ResetRateLimiter();
            TerminalLogger.OnLogLevel = null;
            TerminalLogger.MinimumLevel = LogLevel.Debug;
            string? got = null;
            TerminalLogger.OnLog = m => got = m;

            TerminalLogger.Error("boom");

            Assert.Equal("[Error] boom", got);
        }
        finally
        {
            TerminalLogger.OnLog = prevOnLog;
            TerminalLogger.OnLogLevel = prevOnLevel;
            TerminalLogger.MinimumLevel = prevMin;
        }
    }

    [Fact]
    public void StructuredHook_ReceivesLevel_AndBypassesOnLog()
    {
        var prevOnLog = TerminalLogger.OnLog;
        var prevOnLevel = TerminalLogger.OnLogLevel;
        var prevMin = TerminalLogger.MinimumLevel;
        try
        {
            TerminalLogger.ResetRateLimiter();
            TerminalLogger.MinimumLevel = LogLevel.Debug;
            var captured = new List<(LogLevel, string)>();
            TerminalLogger.OnLogLevel = (lvl, m) => captured.Add((lvl, m));
            bool onLogCalled = false;
            TerminalLogger.OnLog = _ => onLogCalled = true;

            TerminalLogger.Warning("careful");

            Assert.Equal((LogLevel.Warning, "careful"), Assert.Single(captured));
            Assert.False(onLogCalled);
        }
        finally
        {
            TerminalLogger.OnLog = prevOnLog;
            TerminalLogger.OnLogLevel = prevOnLevel;
            TerminalLogger.MinimumLevel = prevMin;
        }
    }

    [Fact]
    public void ThrowingHook_DoesNotPropagate()
    {
        // TerminalLogger is called from critical recovery paths; a throwing hook must not escape.
        var prevOnLog = TerminalLogger.OnLog;
        var prevOnLevel = TerminalLogger.OnLogLevel;
        var prevMin = TerminalLogger.MinimumLevel;
        try
        {
            TerminalLogger.ResetRateLimiter();
            TerminalLogger.MinimumLevel = LogLevel.Debug;
            TerminalLogger.OnLogLevel = (_, _) => throw new System.InvalidOperationException("boom");
            TerminalLogger.Error("x"); // must not throw

            TerminalLogger.OnLogLevel = null;
            TerminalLogger.OnLog = _ => throw new System.InvalidOperationException("boom");
            TerminalLogger.Log("y");   // must not throw
        }
        finally
        {
            TerminalLogger.OnLog = prevOnLog;
            TerminalLogger.OnLogLevel = prevOnLevel;
            TerminalLogger.MinimumLevel = prevMin;
        }
    }

    [Fact]
    public void MinimumLevel_FiltersBelowThreshold()
    {
        var prevOnLog = TerminalLogger.OnLog;
        var prevOnLevel = TerminalLogger.OnLogLevel;
        var prevMin = TerminalLogger.MinimumLevel;
        try
        {
            TerminalLogger.ResetRateLimiter();
            var captured = new List<LogLevel>();
            TerminalLogger.OnLogLevel = (lvl, _) => captured.Add(lvl);
            TerminalLogger.MinimumLevel = LogLevel.Warning;

            TerminalLogger.Debug("d");
            TerminalLogger.Info("i");
            TerminalLogger.Warning("w");
            TerminalLogger.Error("e");

            Assert.Equal(new[] { LogLevel.Warning, LogLevel.Error }, captured);
        }
        finally
        {
            TerminalLogger.OnLog = prevOnLog;
            TerminalLogger.OnLogLevel = prevOnLevel;
            TerminalLogger.MinimumLevel = prevMin;
        }
    }

    // ── Rate limiting ────────────────────────────────────────────────────────────────────
    //
    // The durable half of the debug-log fix. Demoting the chatty call sites to Debug stops
    // today's flood; this stops the next one, wherever it comes from, without every future call
    // site having to remember.

    [Fact]
    public void RepeatingShape_IsMutedAfterTheCap_AndSaysSo()
    {
        var prevOnLog = TerminalLogger.OnLog;
        var prevOnLevel = TerminalLogger.OnLogLevel;
        var prevMin = TerminalLogger.MinimumLevel;
        var prevCap = TerminalLogger.MaxRepeatsPerShape;
        try
        {
            TerminalLogger.ResetRateLimiter();
            TerminalLogger.OnLogLevel = null;
            TerminalLogger.MinimumLevel = LogLevel.Debug;
            TerminalLogger.MaxRepeatsPerShape = 5;
            var captured = new List<string>();
            TerminalLogger.OnLog = captured.Add;

            for (int i = 0; i < 500; i++)
            {
                // Digits vary between occurrences, exactly as they do for the parser's
                // "Unhandled CSI ... params=39" / "params=60". One problem, not two.
                TerminalLogger.Debug($"[ANSI_PARSER] Unhandled CSI: b, params={i}");
            }

            Assert.Equal(6, captured.Count); // the cap, plus the one that announces the mute
            Assert.Contains("muted", captured[^1]);
        }
        finally
        {
            TerminalLogger.OnLog = prevOnLog;
            TerminalLogger.OnLogLevel = prevOnLevel;
            TerminalLogger.MinimumLevel = prevMin;
            TerminalLogger.MaxRepeatsPerShape = prevCap;
            TerminalLogger.ResetRateLimiter();
        }
    }

    [Fact]
    public void DistinctShapes_AreCountedSeparately()
    {
        var prevOnLog = TerminalLogger.OnLog;
        var prevOnLevel = TerminalLogger.OnLogLevel;
        var prevMin = TerminalLogger.MinimumLevel;
        var prevCap = TerminalLogger.MaxRepeatsPerShape;
        try
        {
            TerminalLogger.ResetRateLimiter();
            TerminalLogger.OnLogLevel = null;
            TerminalLogger.MinimumLevel = LogLevel.Debug;
            TerminalLogger.MaxRepeatsPerShape = 2;
            var captured = new List<string>();
            TerminalLogger.OnLog = captured.Add;

            TerminalLogger.Debug("alpha 1");
            TerminalLogger.Debug("alpha 2");
            TerminalLogger.Debug("beta 1");
            TerminalLogger.Debug("beta 2");

            Assert.Equal(4, captured.Count);
        }
        finally
        {
            TerminalLogger.OnLog = prevOnLog;
            TerminalLogger.OnLogLevel = prevOnLevel;
            TerminalLogger.MinimumLevel = prevMin;
            TerminalLogger.MaxRepeatsPerShape = prevCap;
            TerminalLogger.ResetRateLimiter();
        }
    }

    [Fact]
    public void Errors_AreNeverMuted()
    {
        // An error that stops being written because it happened too often is the one case where
        // muting hides exactly what the log exists to capture.
        var prevOnLog = TerminalLogger.OnLog;
        var prevOnLevel = TerminalLogger.OnLogLevel;
        var prevMin = TerminalLogger.MinimumLevel;
        var prevCap = TerminalLogger.MaxRepeatsPerShape;
        try
        {
            TerminalLogger.ResetRateLimiter();
            TerminalLogger.OnLogLevel = null;
            TerminalLogger.MinimumLevel = LogLevel.Debug;
            TerminalLogger.MaxRepeatsPerShape = 3;
            var captured = new List<string>();
            TerminalLogger.OnLog = captured.Add;

            for (int i = 0; i < 50; i++)
            {
                TerminalLogger.Error("write failed");
            }

            Assert.Equal(50, captured.Count);
        }
        finally
        {
            TerminalLogger.OnLog = prevOnLog;
            TerminalLogger.OnLogLevel = prevOnLevel;
            TerminalLogger.MinimumLevel = prevMin;
            TerminalLogger.MaxRepeatsPerShape = prevCap;
            TerminalLogger.ResetRateLimiter();
        }
    }

    [Fact]
    public void RateLimiting_CanBeDisabled()
    {
        var prevOnLog = TerminalLogger.OnLog;
        var prevOnLevel = TerminalLogger.OnLogLevel;
        var prevMin = TerminalLogger.MinimumLevel;
        var prevCap = TerminalLogger.MaxRepeatsPerShape;
        try
        {
            TerminalLogger.ResetRateLimiter();
            TerminalLogger.OnLogLevel = null;
            TerminalLogger.MinimumLevel = LogLevel.Debug;
            TerminalLogger.MaxRepeatsPerShape = 0;
            var captured = new List<string>();
            TerminalLogger.OnLog = captured.Add;

            for (int i = 0; i < 100; i++)
            {
                TerminalLogger.Debug("same message");
            }

            Assert.Equal(100, captured.Count);
        }
        finally
        {
            TerminalLogger.OnLog = prevOnLog;
            TerminalLogger.OnLogLevel = prevOnLevel;
            TerminalLogger.MinimumLevel = prevMin;
            TerminalLogger.MaxRepeatsPerShape = prevCap;
            TerminalLogger.ResetRateLimiter();
        }
    }

    [Theory]
    [InlineData("Unhandled CSI: b, params=39", "Unhandled CSI: b, params=#")]
    [InlineData("rows=24 cols=80", "rows=# cols=#")]
    [InlineData("no digits here", "no digits here")]
    public void ShapeOf_CollapsesDigitRuns(string message, string expected)
    {
        Assert.Equal(expected, TerminalLogger.ShapeOf(message));
    }
}
