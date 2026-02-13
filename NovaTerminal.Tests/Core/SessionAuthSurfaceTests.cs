using NovaTerminal.Core;

namespace NovaTerminal.Tests.Core;

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
