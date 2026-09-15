using System.IO;
using NovaTerminal.Shell;
using Xunit;

namespace NovaTerminal.Tests.Core;

/// <summary>
/// That <see cref="TestAppDataRoot"/> delivers the two properties its callers depend on: paths
/// resolve inside the scope, and the scope is actually empty of the files that steer a
/// <c>MainWindow</c>'s startup path.
/// </summary>
/// <remarks>
/// Deliberately not <c>[AvaloniaFact]</c> and deliberately builds no window. The properties worth
/// pinning are about <see cref="AppPaths"/> and the filesystem, and <c>SaveSession</c> writes to
/// <see cref="AppPaths.SessionFilePath"/> - so asserting where that resolves covers the write path
/// without a UI test. This file also has to stay cheap in that specific way: adding an Avalonia
/// test class shifts what runs next to what, and this repo has a history of that destabilising
/// <c>VerticalTabStripTests</c> and <c>TabRunningCommandTests</c> (see the remarks in
/// <c>TestWindowTeardownTests</c>), which are among the classes the scope is being added to.
/// </remarks>
public sealed class TestAppDataRootTests
{
    [Fact]
    public void AScope_PointsAppPathsAtItsOwnDirectory()
    {
        using var scope = new TestAppDataRoot();

        Assert.Equal(Path.GetFullPath(scope.RootPath), Path.GetFullPath(AppPaths.RootDirectory));

        // The session file is the one that matters: it is what SaveSession writes on close and
        // what the startup path reads, so a test closing a window must not reach outside here.
        Assert.StartsWith(
            Path.GetFullPath(scope.RootPath),
            Path.GetFullPath(AppPaths.SessionFilePath),
            System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The scope is empty of a saved session even though AppPaths creates the directory tree and
    /// may migrate a legacy session into it. Without the sweep this is exactly #434 again.
    /// </summary>
    [Fact]
    public void AScope_StartsWithNoSavedSessionAndNoSettings()
    {
        using var scope = new TestAppDataRoot();

        Assert.False(File.Exists(AppPaths.SessionFilePath));
        Assert.False(File.Exists(AppPaths.SettingsFilePath));
    }

    [Fact]
    public void DisposingAScope_RestoresThePreviousRoot()
    {
        using (var outer = new TestAppDataRoot())
        {
            string outerRoot = Path.GetFullPath(outer.RootPath);

            using (new TestAppDataRoot())
            {
                Assert.NotEqual(outerRoot, Path.GetFullPath(AppPaths.RootDirectory));
            }

            // Nesting has to be safe: the scopes are class fields, and a test that opens its own
            // must not leave the class's one pointing at a deleted directory.
            Assert.Equal(outerRoot, Path.GetFullPath(AppPaths.RootDirectory));
        }
    }

    [Fact]
    public void DisposingAScope_RemovesItsDirectory()
    {
        string root;
        using (var scope = new TestAppDataRoot())
        {
            root = scope.RootPath;
            Assert.True(Directory.Exists(root));
        }

        Assert.False(Directory.Exists(root));
    }
}
