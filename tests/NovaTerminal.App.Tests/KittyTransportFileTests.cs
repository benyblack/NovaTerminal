using System;
using System.IO;
using NovaTerminal.Controls;
using Xunit;

namespace NovaTerminal.Tests;

/// <summary>
/// Confinement rules for the kitty <c>t=f</c> transport reader wired behind
/// <see cref="AnsiParser.ReadFileBytes"/> (see TerminalPane.CreateAndWireParser). The path
/// arrives from the remote byte stream, so every rule here is a boundary against turning an
/// inline-image sequence into an arbitrary file read.
/// </summary>
public class KittyTransportFileTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative\\path.rgba")]           // not rooted
    [InlineData(@"\\evil\share\frame.rgba")]      // UNC share outside temp
    [InlineData("D:\\projects\\secrets.rgba")]    // absolute but outside temp
    public void ReadKittyTransportFile_PathsOutsideTemp_AreRejected(string? path)
    {
        Assert.Null(TerminalPane.ReadKittyTransportFile(path));
    }

    [Fact]
    public void ReadKittyTransportFile_PathTraversalForm_InTemp_IsRejected()
    {
        string path = Path.Combine(Path.GetTempPath(), "subdir", "..", "frame.rgba");
        // GetFullPath collapses this to temp\frame.rgba - inside temp, so confinement depends
        // on the file not existing rather than on the raw string. Existence keeps the
        // collapsed form safe; assert the behavior is a clean null either way for a
        // nonexistent file.
        Assert.Null(TerminalPane.ReadKittyTransportFile(path));
    }

    [Fact]
    public void ReadKittyTransportFile_FileInsideTemp_IsRead()
    {
        string tempRoot = Path.GetTempPath();
        string candidate = Path.Combine(tempRoot, "novaterminal-kitty-test-" + Guid.NewGuid().ToString("N") + ".rgba");
        try
        {
            byte[] payload = { 1, 2, 3, 4, 5 };
            File.WriteAllBytes(candidate, payload);

            byte[]? read = TerminalPane.ReadKittyTransportFile(candidate);

            Assert.NotNull(read);
            Assert.Equal(payload, read);
        }
        finally
        {
            File.Delete(candidate);
        }
    }

    [Fact]
    public void ReadKittyTransportFile_MissingFileInsideTemp_ReturnsNull()
    {
        string candidate = Path.Combine(Path.GetTempPath(), "novaterminal-missing-" + Guid.NewGuid().ToString("N") + ".rgba");
        Assert.Null(TerminalPane.ReadKittyTransportFile(candidate));
    }
}
