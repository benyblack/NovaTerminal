using System.IO;
using Xunit;
using NovaTerminal.Core.Input;

namespace NovaTerminal.Tests.Input
{
    public class TextFileDetectorTests
    {
        [Fact]
        public void IsTextFile_WithNullBytes_ReturnsFalse()
        {
            // Arrange
            string tempFile = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(tempFile, new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F, 0x00, 0x57, 0x6F });

                // Act
                bool result = TextFileDetector.IsTextFile(tempFile);

                // Assert
                Assert.False(result);
            }
            finally
            {
                File.Delete(tempFile);
            }
        }

        [Fact]
        public void IsTextFile_WithLargeFile_ReturnsFalse()
        {
            // Arrange
            string tempFile = Path.GetTempFileName();
            try
            {
                byte[] largeData = new byte[256 * 1024 + 1];
                for (int i = 0; i < largeData.Length; i++) largeData[i] = 0x41;
                File.WriteAllBytes(tempFile, largeData);

                // Act
                bool result = TextFileDetector.IsTextFile(tempFile);

                // Assert
                Assert.False(result);
            }
            finally
            {
                File.Delete(tempFile);
            }
        }

        [Fact]
        public void IsTextFile_WithValidText_ReturnsTrue()
        {
            // Arrange
            string tempFile = Path.GetTempFileName();
            try
            {
                File.WriteAllText(tempFile, "Console.WriteLine(\"Hello World!\");\n// A comment");

                // Act
                bool result = TextFileDetector.IsTextFile(tempFile);

                // Assert
                Assert.True(result);
            }
            finally
            {
                File.Delete(tempFile);
            }
        }
    }
}
