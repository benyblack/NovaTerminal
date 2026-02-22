using NovaTerminal.Core;
using Xunit;

namespace NovaTerminal.Tests.Core
{
    public class BatchWriteTests
    {
        [Fact]
        public void BatchWrite_DefersInvalidateUntilExit()
        {
            var buffer = new TerminalBuffer(20, 5);
            int invalidateCount = 0;
            buffer.OnInvalidate += () => invalidateCount++;

            buffer.EnterBatchWrite();
            try
            {
                buffer.WriteChar('A');
                buffer.WriteChar('B');
                buffer.WriteContent("CD");
                Assert.Equal(0, invalidateCount);
            }
            finally
            {
                buffer.ExitBatchWrite();
            }

            Assert.Equal(1, invalidateCount);
        }

        [Fact]
        public void ParserProcess_BatchesInvalidateForMixedControlAndText()
        {
            var buffer = new TerminalBuffer(20, 5);
            var parser = new AnsiParser(buffer);
            int invalidateCount = 0;
            buffer.OnInvalidate += () => invalidateCount++;

            parser.Process("\x1b[2;2HABC");

            Assert.Equal(1, invalidateCount);
        }
    }
}
