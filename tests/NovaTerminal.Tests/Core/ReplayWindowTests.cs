using NovaTerminal.UI.Replay;

namespace NovaTerminal.Tests.Core;

public sealed class ReplayWindowTests
{
    [Fact]
    public void TryAttachDeveloperTools_SwallowsDuplicateAttachmentFailure()
    {
        bool invoked = false;

        Exception? error = Record.Exception(() =>
            ReplayWindow.TryAttachDeveloperToolsForTest(() =>
            {
                invoked = true;
                throw new InvalidOperationException("Developer tools have already been attached. Multiple attachments are not supported.");
            }));

        Assert.True(invoked);
        Assert.Null(error);
    }

    [Fact]
    public void TryAttachDeveloperTools_RethrowsUnexpectedFailures()
    {
        Exception ex = Assert.Throws<InvalidOperationException>(() =>
            ReplayWindow.TryAttachDeveloperToolsForTest(() =>
            {
                throw new InvalidOperationException("Some other failure");
            }));

        Assert.Equal("Some other failure", ex.Message);
    }
}
