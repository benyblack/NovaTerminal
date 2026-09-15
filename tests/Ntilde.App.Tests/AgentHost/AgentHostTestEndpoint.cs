using System;
using System.IO;
using System.Text;

namespace Ntilde.AppTests.AgentHost;

/// <summary>
/// Temp directories and endpoint addresses for AgentHostService in tests, with the one
/// constraint that actually bites enforced in a single place.
///
/// WHY THIS EXISTS. A Unix domain socket address is bounded by <c>sockaddr_un.sun_path</c>:
/// 104 bytes on macOS, 108 on Linux. Exceed it and <c>bind()</c> fails, and
/// <c>AgentHostService.Start()</c> reports that only by leaving <c>IsRunning</c> false -
/// so every test whose setup asserts <c>IsRunning</c> dies with a bare
/// <c>Assert.True() Failure</c> naming nothing.
///
/// That is what happened, and only on macOS, because Path.GetTempPath() differs by an
/// order of magnitude between platforms:
///
///     Linux   /tmp/                                     5 chars
///     macOS   /var/folders/xx/&lt;~26 chars&gt;/T/            ~44 chars
///
/// A per-class temp directory named "ntilde-agentattention-tests-" plus a 32-char GUID,
/// with an 8-char socket name inside it, lands at ~113 bytes on macOS and ~74 on Linux.
/// 36 tests across four classes therefore failed in setup on macOS and passed everywhere
/// else, for twelve days, unnoticed because macOS unit tests were dispatch-only (#460).
///
/// The two AgentHost classes that did NOT fail are the proof of the mechanism rather than
/// a guess at it: AgentHostActProtocolTests put its socket straight into GetTempPath()
/// (short enough), and AgentHostCaptureProtocolTests never calls Start(), so it never
/// binds one.
/// </summary>
internal static class AgentHostTestEndpoint
{
    /// <summary>
    /// The SMALLER of the two platform limits, applied on both. A path that fits on macOS
    /// fits on Linux, and holding Linux to macOS's number is the only way a Linux-only run
    /// can fail on a path macOS would have rejected - which is precisely the feedback that
    /// was missing while this bug was live.
    /// </summary>
    internal const int MaxUnixSocketPathBytes = 104;

    /// <summary>
    /// Longest socket file name <see cref="CreateEndpoint"/> will put inside a directory,
    /// as "/" + 8 hex + ".sock". The directory check below budgets for it, so a directory
    /// that passes here cannot produce a socket path that fails later.
    /// </summary>
    private const int SocketLeafBytes = 1 + 8 + 5;

    /// <summary>
    /// Creates a per-test temp directory whose name is short enough to hold a socket.
    /// <paramref name="tag"/> identifies the owning test class in stray-directory listings
    /// and is deliberately abbreviated - the descriptive "ntilde-agentattention-tests-" form
    /// cost 26 bytes of a 104-byte budget for no diagnostic value a 6-byte tag lacks.
    /// </summary>
    internal static string CreateTempDir(string tag)
    {
        string dir = Path.Combine(Path.GetTempPath(), "nvt-" + tag + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        // Checked when the directory is made, not when a socket inside it fails to bind,
        // because the failure at bind time says nothing. Byte count, not char count:
        // sun_path is bytes, and GetTempPath() can contain non-ASCII (a macOS user account
        // with an accented name is enough).
        int worstCase = Encoding.UTF8.GetByteCount(dir) + SocketLeafBytes;
        if (worstCase >= MaxUnixSocketPathBytes)
        {
            throw new InvalidOperationException(
                $"Test temp directory '{dir}' leaves no room for a Unix socket: a socket inside it " +
                $"would be {worstCase} bytes against the {MaxUnixSocketPathBytes}-byte sun_path limit " +
                "(104 on macOS, 108 on Linux). Shorten the tag, or the socket will silently fail to " +
                "bind and AgentHostService.Start() will leave IsRunning false with no other symptom.");
        }

        return dir;
    }

    /// <summary>
    /// An endpoint address for <c>AgentHostService</c>: a named pipe on Windows, where no
    /// length limit of this kind applies, and a socket inside <paramref name="tempDir"/>
    /// everywhere else.
    /// </summary>
    internal static string CreateEndpoint(string tempDir)
    {
        if (OperatingSystem.IsWindows())
        {
            return "ntilde-agent-test-" + Guid.NewGuid().ToString("N");
        }

        string path = Path.Combine(tempDir, Guid.NewGuid().ToString("N")[..8] + ".sock");

        // Belt and braces against a caller that built its directory by hand rather than
        // through CreateTempDir. Cheap, and it fails here naming the limit instead of
        // three frames away inside a test's setup assertion.
        int bytes = Encoding.UTF8.GetByteCount(path);
        if (bytes >= MaxUnixSocketPathBytes)
        {
            throw new InvalidOperationException(
                $"Unix socket path '{path}' is {bytes} bytes, at or over the {MaxUnixSocketPathBytes}-byte " +
                "sun_path limit (104 on macOS, 108 on Linux). bind() would fail and Start() would leave " +
                "IsRunning false with no other symptom.");
        }

        return path;
    }
}
