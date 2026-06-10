using System;
using Microsoft.Win32.SafeHandles;

namespace NovaTerminal.Platform.Ssh.Native;

// Owns a native SSH session registry id (returned by nova_ssh_connect). Passing
// this to every session P/Invoke makes the marshaller AddRef before / Release
// after each call, so nova_ssh_close (ReleaseHandle) cannot run while a poll or
// write is in flight — closing the poll-vs-close use-after-free (#121/#118).
public sealed class NovaSshSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public NovaSshSafeHandle() : base(ownsHandle: true) { }

    protected override bool ReleaseHandle()
    {
        NativeSshInterop.NativeMethods.nova_ssh_close_raw(handle);
        return true;
    }
}
