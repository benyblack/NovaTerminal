using System.IO;
using System.Runtime.CompilerServices;

namespace NovaTerminal.Tests.CommandAssist.ShellIntegration;

/// <summary>
/// Where shell-integration tests let providers write their generated bootstrap.
/// </summary>
/// <remarks>
/// These tests used to pass <c>AppPaths.CommandAssistDirectory</c>, which is the developer's
/// <em>live</em> config directory — so every run rewrote the bootstrap the developer's own
/// NovaTerminal uses, and test classes running in parallel raced each other for the same file.
/// That surfaced as an IOException ("used by another process") on whichever class lost, which
/// reads like a product bug and isn't one. Same class of problem as #365, where MainWindow tests
/// were reading the developer's live settings.json.
///
/// Keyed by caller file so each test class gets its own directory: xUnit runs tests within a class
/// sequentially but classes in parallel, so per-file is exactly the granularity that removes the
/// race while keeping a stable path within one class.
/// </remarks>
internal static class BootstrapTestDirectory
{
    public static string ForCaller([CallerFilePath] string callerFilePath = "")
    {
        string dir = Path.Combine(
            Path.GetTempPath(),
            "nova-bootstrap-tests",
            Path.GetFileNameWithoutExtension(callerFilePath));

        Directory.CreateDirectory(dir);
        return dir;
    }
}
