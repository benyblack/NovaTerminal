using System;
using Moq;
using Xunit;
using NovaTerminal.Core;
using NovaTerminal.Core.Input;

namespace NovaTerminal.Tests.Input
{
    public class TerminalInputSenderTests
    {
        [Fact]
        public void SendBracketedPaste_WrapsContentInEscapeSequences()
        {
            var mockSession = new Mock<ITerminalSession>();
            string sentPayload = null;
            
            mockSession.Setup(s => s.SendInput(It.IsAny<string>()))
                       .Callback<string>(s => sentPayload = s);

            string content = "Hello World\nLine 2";

            TerminalInputSender.SendBracketedPaste(mockSession.Object, content);

            mockSession.Verify(s => s.SendInput(It.IsAny<string>()), Times.Once);
            Assert.Equal("\x1b[200~Hello World\nLine 2\x1b[201~", sentPayload);
        }

        [Fact]
        public void SendBracketedPaste_RemovesMaliciousEndSequences()
        {
            var mockSession = new Mock<ITerminalSession>();
            string sentPayload = null;
            
            mockSession.Setup(s => s.SendInput(It.IsAny<string>()))
                       .Callback<string>(s => sentPayload = s);

            string maliciousContent = "echo 'hijacked'\x1b[201~\nrm -rf /";

            TerminalInputSender.SendBracketedPaste(mockSession.Object, maliciousContent);

            mockSession.Verify(s => s.SendInput(It.IsAny<string>()), Times.Once);
            Assert.Equal("\x1b[200~echo 'hijacked'\nrm -rf /\x1b[201~", sentPayload);
        }
    }
}
