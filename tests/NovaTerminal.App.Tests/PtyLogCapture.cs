using System;
using System.Text;
using NovaTerminal.Pty;

namespace NovaTerminal.Tests
{
    /// <summary>
    /// Tees <see cref="PtyLogger"/> into a string for the duration of a test, so an
    /// assertion can attach what the session actually did — and so a test can assert on the
    /// diagnostics themselves, which for the PTY layer are part of the contract: several
    /// failure paths are *defined* as "log it and carry on".
    ///
    /// These sessions drive real shells and their failures are timing-shaped: without the
    /// log a failure tells you the code was wrong and nothing about why.
    ///
    /// <see cref="PtyLogger.Sink"/> is process-global, so every class using this must be in
    /// <see cref="PtyRealShellCollection"/> — otherwise two parallel classes swap the sink
    /// under each other and Dispose restores the wrong one.
    /// </summary>
    internal sealed class PtyLogCapture : IDisposable
    {
        private readonly StringBuilder _log = new();
        private readonly Action<PtyLogLevel, string>? _previous;

        private PtyLogCapture()
        {
            _previous = PtyLogger.Sink;
            PtyLogger.Sink = (level, message) =>
            {
                lock (_log) { _log.AppendLine($"[{level}] {message}"); }
                _previous?.Invoke(level, message);
            };
        }

        public static PtyLogCapture Attach() => new();

        public void Dispose() => PtyLogger.Sink = _previous;

        public override string ToString()
        {
            lock (_log) { return _log.ToString(); }
        }
    }
}
