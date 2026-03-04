using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Xunit;
using NovaTerminal.Core;
using NovaTerminal.Core.Paths;

namespace NovaTerminal.Tests.Input
{
    public class DropRouterTests
    {
        [Fact]
        public async Task HandleDropAsync_WithEchoDisabledAndNoAlt_BlocksDrop()
        {
            var context = new SessionContext { IsEchoEnabled = false };
            var paths = new List<string> { @"C:\test.txt" };

            var result = await DropRouter.HandleDropAsync(context, paths, isAltHeld: false);

            Assert.True(result.Handled);
            Assert.Null(result.TextToSend);
            Assert.NotNull(result.ToastMessage);
            Assert.Contains("blocked", result.ToastMessage);
        }

        [Fact]
        public async Task HandleDropAsync_WithEchoDisabledButAltHeld_AllowsDrop()
        {
            var context = new SessionContext { IsEchoEnabled = false, DetectedShell = DetectedShell.Pwsh };
            var paths = new List<string> { @"C:\test.txt" };

            var result = await DropRouter.HandleDropAsync(context, paths, isAltHeld: true);

            Assert.True(result.Handled);
            Assert.Null(result.ToastMessage);
            Assert.Equal(@"'C:\test.txt' ", result.TextToSend); // Note trailing space
        }

        [Fact]
        public async Task HandleDropAsync_WithMultipleFiles_JoinsWithSpace()
        {
            var context = new SessionContext { IsEchoEnabled = true, DetectedShell = DetectedShell.PosixSh };
            var paths = new List<string> { "/a.txt", "/b c.txt", "/d.txt" };

            var result = await DropRouter.HandleDropAsync(context, paths, isAltHeld: false);

            Assert.True(result.Handled);
            Assert.Equal(@"'/a.txt' '/b c.txt' '/d.txt' ", result.TextToSend);
        }

        [Fact]
        public async Task HandleDropAsync_RespectsShellOverride()
        {
            var context = new SessionContext { 
                IsEchoEnabled = true, 
                DetectedShell = DetectedShell.PosixSh,
                ShellOverride = ShellOverride.Pwsh
            };
            
            var paths = new List<string> { "a'b.txt" };

            var result = await DropRouter.HandleDropAsync(context, paths, isAltHeld: false);

            Assert.True(result.Handled);
            Assert.Equal("'a''b.txt' ", result.TextToSend); 
        }

        [Fact]
        public async Task HandleDropAsync_EmptyPaths_ReturnsUnhandled()
        {
            var context = new SessionContext { IsEchoEnabled = true };
            var paths = new List<string>();

            var result = await DropRouter.HandleDropAsync(context, paths, isAltHeld: false);

            Assert.False(result.Handled);
            Assert.Null(result.TextToSend);
            Assert.Null(result.ToastMessage);
        }

        [Fact]
        public async Task HandleDropAsync_WithWslSession_UsesMapper()
        {
            var context = new SessionContext { IsEchoEnabled = true, IsWslSession = true, DetectedShell = DetectedShell.PosixSh };
            var paths = new List<string> { @"C:\test.txt" };

            var mapperMock = new Mock<IPathMapper>();
            mapperMock.Setup(m => m.MapAsync(@"C:\test.txt", It.IsAny<CancellationToken>()))
                      .ReturnsAsync("/mnt/c/test.txt");

            var result = await DropRouter.HandleDropAsync(context, paths, isAltHeld: false, mapperMock.Object);

            Assert.True(result.Handled);
            Assert.Equal(@"'/mnt/c/test.txt' ", result.TextToSend);
            mapperMock.Verify(m => m.MapAsync(@"C:\test.txt", It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task HandleDropAsync_WithWslSessionAndMappingFails_ReturnsOriginalAndToast()
        {
            var context = new SessionContext { IsEchoEnabled = true, IsWslSession = true, DetectedShell = DetectedShell.PosixSh };
            var paths = new List<string> { @"C:\test.txt" };

            var mapperMock = new Mock<IPathMapper>();
            // Mapper returning the original path simulates a fallback/failure
            mapperMock.Setup(m => m.MapAsync(@"C:\test.txt", It.IsAny<CancellationToken>()))
                      .ReturnsAsync(@"C:\test.txt");

            var result = await DropRouter.HandleDropAsync(context, paths, isAltHeld: false, mapperMock.Object);

            Assert.True(result.Handled);
            Assert.Equal(@"'C:\test.txt' ", result.TextToSend);
            Assert.NotNull(result.ToastMessage);
            Assert.Contains("WSL path mapping failed", result.ToastMessage);
        }
    }
}
