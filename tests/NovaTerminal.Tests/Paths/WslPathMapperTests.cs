using System.Threading;
using System.Threading.Tasks;
using Moq;
using Xunit;
using NovaTerminal.Core.Paths;
using NovaTerminal.Core.Execution;

namespace NovaTerminal.Tests.Paths
{
    public class WslPathMapperTests
    {
        [Fact]
        public async Task MapAsync_NormalizesAndCallsWslExe()
        {
            var runnerMock = new Mock<IProcessRunner>();
            runnerMock.Setup(r => r.RunProcessAsync("wsl.exe", "wslpath -a -u \"C:\\Test Folder\\file.txt\"", It.IsAny<CancellationToken>()))
                      .ReturnsAsync((0, "/mnt/c/Test Folder/file.txt\n"));

            var mapper = new WslPathMapper(runnerMock.Object, distroName: null);

            // Using unnormalized path (lower-case drive letter, trailing slashes)
            string result = await mapper.MapAsync("c:\\Test Folder\\file.txt\\\\");

            Assert.Equal("/mnt/c/Test Folder/file.txt", result);
            runnerMock.Verify(r => r.RunProcessAsync("wsl.exe", "wslpath -a -u \"C:\\Test Folder\\file.txt\"", It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task MapAsync_IncludesDistroNameIfProvided()
        {
            var runnerMock = new Mock<IProcessRunner>();
            runnerMock.Setup(r => r.RunProcessAsync("wsl.exe", "-d Ubuntu wslpath -a -u \"C:\\file.txt\"", It.IsAny<CancellationToken>()))
                      .ReturnsAsync((0, "/mnt/c/file.txt\n"));

            var mapper = new WslPathMapper(runnerMock.Object, "Ubuntu");

            string result = await mapper.MapAsync("C:\\file.txt");

            Assert.Equal("/mnt/c/file.txt", result);
            runnerMock.Verify(r => r.RunProcessAsync("wsl.exe", "-d Ubuntu wslpath -a -u \"C:\\file.txt\"", It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task MapAsync_CachesSubsequentCalls()
        {
            var runnerMock = new Mock<IProcessRunner>();
            runnerMock.Setup(r => r.RunProcessAsync("wsl.exe", "-d Debian wslpath -a -u \"D:\\cached.txt\"", It.IsAny<CancellationToken>()))
                      .ReturnsAsync((0, "/mnt/d/cached.txt\n"));

            var mapper1 = new WslPathMapper(runnerMock.Object, "Debian");
            var mapper2 = new WslPathMapper(runnerMock.Object, "Debian");

            string result1 = await mapper1.MapAsync("d:\\cached.txt\\");
            string result2 = await mapper1.MapAsync("D:\\cached.txt");
            string result3 = await mapper2.MapAsync("D:\\cached.txt\\");

            Assert.Equal("/mnt/d/cached.txt", result1);
            Assert.Equal("/mnt/d/cached.txt", result2);
            Assert.Equal("/mnt/d/cached.txt", result3);

            // Verify called exactly once for the same normalized key across multiple mapper instances
            runnerMock.Verify(r => r.RunProcessAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task MapAsync_ReturnsHostPathOnFailure()
        {
            var runnerMock = new Mock<IProcessRunner>();
            runnerMock.Setup(r => r.RunProcessAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                      .ReturnsAsync((1, "wslpath: C:\\nonexistent: No such file or directory"));

            var mapper = new WslPathMapper(runnerMock.Object, null);

            string hostPath = "C:\\nonexistent";
            string result = await mapper.MapAsync(hostPath);

            Assert.Equal(hostPath, result); // Expected fallback
        }
    }
}
