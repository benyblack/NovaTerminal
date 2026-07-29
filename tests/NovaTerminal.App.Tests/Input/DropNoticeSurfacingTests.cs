using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using NovaTerminal.Platform;
using NovaTerminal.Platform.Paths;
using NovaTerminal.Shell;
using NovaTerminal.VT;
using Xunit;

namespace NovaTerminal.Tests.Input
{
    /// <summary>
    /// #182: DropRouter produced user-facing messages that nothing consumed.
    ///
    /// DropRouterTests already covers that the messages are *produced*. These tests cover
    /// the half that was missing - that every produced message is one TerminalView will
    /// surface - by feeding real DropRouter outcomes into the decision TerminalView makes.
    ///
    /// The remaining untested link is the UI plumbing itself (event to toast panel), which
    /// would need a simulated drag-and-drop gesture; see the PR.
    /// </summary>
    public class DropNoticeSurfacingTests
    {
        [Fact]
        public async Task SecureInputBlock_ProducesAMessageThatGetsSurfaced()
        {
            var context = new SessionContext { IsEchoEnabled = false };
            var result = await DropRouter.HandleDropAsync(
                context, new List<string> { @"C:\test.txt" }, isAltHeld: false);

            Assert.True(TerminalView.ShouldRaiseDropNotice(result.ToastMessage));
        }

        [Fact]
        public async Task MetacharacterBlock_ProducesAMessageThatGetsSurfaced()
        {
            // Cmd specifically: only CmdQuoter implements HasUnsafeMetacharacters (the
            // IShellQuoter default returns false), because % and ! expansion cannot be
            // neutralised in an interactive cmd session. My first attempt used PosixSh and
            // the drop was allowed - correctly - so the test was asserting nothing.
            var context = new SessionContext
            {
                IsEchoEnabled = true,
                DetectedShell = DetectedShell.Cmd
            };

            // "%APPDATA%.txt" is the injection case the rule exists for.
            var result = await DropRouter.HandleDropAsync(
                context, new List<string> { @"C:\%APPDATA%.txt" }, isAltHeld: false);

            Assert.True(
                TerminalView.ShouldRaiseDropNotice(result.ToastMessage),
                $"expected a surfaced message, got {result.ToastMessage ?? "<null>"}");
        }

        [Fact]
        public async Task WslMappingFallback_SurfacesItsMessageEvenThoughTextWasSent()
        {
            // The regression that motivated #182's third message. The path *is* inserted, so
            // the old code took the "send text" branch and returned without ever looking at
            // ToastMessage. A notice must fire even when text was sent.
            var context = new SessionContext
            {
                IsEchoEnabled = true,
                IsWslSession = true,
                DetectedShell = DetectedShell.PosixSh
            };

            var mapper = new Mock<IPathMapper>();
            // Returning the input unchanged is how a mapping failure presents.
            mapper.Setup(m => m.MapAsync(@"C:\test.txt", It.IsAny<CancellationToken>()))
                  .ReturnsAsync(@"C:\test.txt");

            var result = await DropRouter.HandleDropAsync(
                context, new List<string> { @"C:\test.txt" }, isAltHeld: false, mapper.Object);

            Assert.False(string.IsNullOrEmpty(result.TextToSend));
            Assert.True(
                TerminalView.ShouldRaiseDropNotice(result.ToastMessage),
                "a message accompanying a successful drop must still be surfaced");
        }

        [Fact]
        public async Task AnOrdinaryDrop_SurfacesNothing()
        {
            // Guards the other direction: no gratuitous toast on the common path.
            var context = new SessionContext
            {
                IsEchoEnabled = true,
                DetectedShell = DetectedShell.Pwsh
            };

            var result = await DropRouter.HandleDropAsync(
                context, new List<string> { @"C:\test.txt" }, isAltHeld: false);

            Assert.False(string.IsNullOrEmpty(result.TextToSend));
            Assert.False(TerminalView.ShouldRaiseDropNotice(result.ToastMessage));
        }
    }
}
