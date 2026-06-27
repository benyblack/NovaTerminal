using System.Collections.Generic;
using NovaTerminal.VT;

namespace NovaTerminal.VT.Tests;

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
    public void MinimumLevel_FiltersBelowThreshold()
    {
        var prevOnLog = TerminalLogger.OnLog;
        var prevOnLevel = TerminalLogger.OnLogLevel;
        var prevMin = TerminalLogger.MinimumLevel;
        try
        {
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
}
