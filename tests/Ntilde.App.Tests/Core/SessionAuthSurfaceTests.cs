using Ntilde.Shell;
using Ntilde.Platform;
using Ntilde.VT;
using Ntilde.Pty;

namespace Ntilde.Tests.Core;

public class SessionAuthSurfaceTests
{
    [Fact]
    public void ITerminalSession_DoesNotExposePasswordInjectionApi()
    {
        var methodNames = typeof(ITerminalSession)
            .GetMethods()
            .Select(m => m.Name)
            .ToArray();

        Assert.DoesNotContain("SetSavedPassword", methodNames);
    }
}
