using NovaTerminal.Core;
using System.Threading.Tasks;
using Xunit;

namespace NovaTerminal.Tests.Core
{
    public class SynchronizedRenderingTests
    {
        [Fact]
        public void SyncRendering_ShouldDeferInvalidation()
        {
            var buffer = new TerminalBuffer(80, 24);
            bool invalidated = false;
            buffer.OnInvalidate += () => invalidated = true;

            // 1. Enter Sync Mode
            buffer.BeginSync();
            Assert.True(buffer.IsSynchronizedOutput);

            // 2. Perform operations that usually invalidate
            buffer.WriteChar('A');
            buffer.Invalidate(); 

            // 3. Assert NO invalidation
            Assert.False(invalidated, "Invalidate should be deferred while in Sync mode");

            // 4. Exit Sync Mode
            buffer.EndSync();
            Assert.False(buffer.IsSynchronizedOutput);
            
            // 5. Assert Invalidation Triggered
            Assert.True(invalidated, "Invalidate should be triggered after EndSync");
        }

        [Fact]
        public async Task SyncRendering_ShouldFlushOnTimeout()
        {
            var buffer = new TerminalBuffer(80, 24);
            bool invalidated = false;
            buffer.OnInvalidate += () => invalidated = true;

            // 1. Enter Sync Mode
            buffer.BeginSync();
            
            // 2. Write invalidation
            buffer.Invalidate();
            Assert.False(invalidated);

            // 3. Wait for timeout > 200ms
            await Task.Delay(250);

            // 4. Trigger Invalidate again (simulating next frame/parser cycle)
            // The logic checks timeout INSIDE Invalidate()
            buffer.Invalidate();

            // 5. Assert Forced Flush
            Assert.True(invalidated, "Should force flush after timeout");
            Assert.False(buffer.IsSynchronizedOutput, "Should exit sync mode on timeout");
        }

        [Fact]
        public void Parser_ShouldHandle_2026_Sequence()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            // DECSET ?2026 h (Begin Sync)
            parser.Process("\x1b[?2026h");
            Assert.True(buffer.IsSynchronizedOutput);

            // DECRST ?2026 l (End Sync)
            parser.Process("\x1b[?2026l");
            Assert.False(buffer.IsSynchronizedOutput);
        }
    }
}
