using System.Collections.Generic;
using Xunit;
using NovaTerminal.Core;

namespace NovaTerminal.Tests.Input
{
    public class DropRouterTests
    {
        [Fact]
        public void HandleDrop_WithEchoDisabledAndNoAlt_BlocksDrop()
        {
            var context = new SessionContext { IsEchoEnabled = false };
            var paths = new List<string> { @"C:\test.txt" };

            var result = DropRouter.HandleDrop(context, paths, isAltHeld: false);

            Assert.True(result.Handled);
            Assert.Null(result.TextToSend);
            Assert.NotNull(result.ToastMessage);
            Assert.Contains("blocked", result.ToastMessage);
        }

        [Fact]
        public void HandleDrop_WithEchoDisabledButAltHeld_AllowsDrop()
        {
            var context = new SessionContext { IsEchoEnabled = false, DetectedShell = DetectedShell.Pwsh };
            var paths = new List<string> { @"C:\test.txt" };

            var result = DropRouter.HandleDrop(context, paths, isAltHeld: true);

            Assert.True(result.Handled);
            Assert.Null(result.ToastMessage);
            Assert.Equal(@"'C:\test.txt' ", result.TextToSend); // Note trailing space
        }

        [Fact]
        public void HandleDrop_WithMultipleFiles_JoinsWithSpace()
        {
            var context = new SessionContext { IsEchoEnabled = true, DetectedShell = DetectedShell.PosixSh };
            var paths = new List<string> { "/a.txt", "/b c.txt", "/d.txt" };

            var result = DropRouter.HandleDrop(context, paths, isAltHeld: false);

            Assert.True(result.Handled);
            Assert.Equal(@"'/a.txt' '/b c.txt' '/d.txt' ", result.TextToSend);
        }

        [Fact]
        public void HandleDrop_RespectsShellOverride()
        {
            // Context says Posix, but Override says Pwsh
            var context = new SessionContext { 
                IsEchoEnabled = true, 
                DetectedShell = DetectedShell.PosixSh,
                ShellOverride = ShellOverride.Pwsh
            };
            
            var paths = new List<string> { "a'b.txt" };

            var result = DropRouter.HandleDrop(context, paths, isAltHeld: false);

            Assert.True(result.Handled);
            // Pwsh quoter replaces ' with ''
            Assert.Equal("'a''b.txt' ", result.TextToSend); 
        }

        [Fact]
        public void HandleDrop_EmptyPaths_ReturnsUnhandled()
        {
            var context = new SessionContext { IsEchoEnabled = true };
            var paths = new List<string>();

            var result = DropRouter.HandleDrop(context, paths, isAltHeld: false);

            Assert.False(result.Handled);
            Assert.Null(result.TextToSend);
            Assert.Null(result.ToastMessage);
        }
    }
}
