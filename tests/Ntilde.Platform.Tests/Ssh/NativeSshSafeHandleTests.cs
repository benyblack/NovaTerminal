using NovaTerminal.Platform.Ssh.Native;
using Xunit;

namespace NovaTerminal.Platform.Tests.Ssh;

public class NativeSshSafeHandleTests
{
    [Fact]
    public void Dispose_IsIdempotent_AndDoesNotThrow()
    {
        var handle = new NovaSshSafeHandle();
        Assert.True(handle.IsInvalid);

        var ex = Record.Exception(() =>
        {
            handle.Dispose();
            handle.Dispose();
        });

        Assert.Null(ex);
        Assert.True(handle.IsClosed);
    }

    [Fact]
    public void Interop_SessionMethods_NoOp_OnDisposedHandle()
    {
        var interop = new NativeSshInterop();
        var handle = new NovaSshSafeHandle();
        handle.Dispose();

        var ex = Record.Exception(() =>
        {
            _ = interop.PollEvent(handle);
            interop.Write(handle, new byte[] { 1 });
            interop.Resize(handle, 80, 24);
            interop.Close(handle);
        });

        Assert.Null(ex);
    }
}
