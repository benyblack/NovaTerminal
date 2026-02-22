using System;
using System.Threading.Tasks;
using NovaTerminal.Core;
using Xunit;

namespace NovaTerminal.Tests
{
    public class PtySmokeTests
    {
        [Fact]
        [Trait("Category", "PtySmoke")]
        public async Task RustPtySession_CanSpawnResizeWriteAndDispose()
        {
            string shell = ShellHelper.GetDefaultShell();
            using var session = new RustPtySession(shell, 80, 24);

            // Basic lifecycle and native interop sanity checks.
            await Task.Delay(250);
            session.Resize(100, 30);
            session.SendInput("\r");
            await Task.Delay(150);
        }
    }
}
