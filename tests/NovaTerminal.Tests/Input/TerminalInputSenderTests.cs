using System;
using System.Collections.Generic;
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
            // Arrange
            var mockSession = new Mock<ITerminalSession>();
            string sentPayload = null;
            
            mockSession.Setup(s => s.SendInput(It.IsAny<string>()))
                       .Callback<string>(s => sentPayload = s);

            string content = "Hello World\nLine 2";

            // Act
            TerminalInputSender.SendBracketedPaste(mockSession.Object, content);

            // Assert
            mockSession.Verify(s => s.SendInput(It.IsAny<string>()), Times.Once);
            Assert.Equal("\x1b[200~Hello World\nLine 2\x1b[201~", sentPayload);
        }

        [Fact]
        public void SendBracketedPaste_RemovesMaliciousEndSequences()
        {
            // Arrange
            var mockSession = new Mock<ITerminalSession>();
            string sentPayload = null;
            
            mockSession.Setup(s => s.SendInput(It.IsAny<string>()))
                       .Callback<string>(s => sentPayload = s);

            // Content tries to close the paste block early
            string maliciousContent = "echo 'hijacked'\x1b[201~\nrm -rf /";

            // Act
            TerminalInputSender.SendBracketedPaste(mockSession.Object, maliciousContent);

            // Assert
            mockSession.Verify(s => s.SendInput(It.IsAny<string>()), Times.Once);
            
            // The interior \x1b[201~ should be stripped out.
            Assert.Equal("\x1b[200~echo 'hijacked'\nrm -rf /\x1b[201~", sentPayload);
        }
    }
}
