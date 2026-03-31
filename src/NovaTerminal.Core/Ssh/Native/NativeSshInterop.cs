using System.Runtime.InteropServices;

namespace NovaTerminal.Core.Ssh.Native;

public sealed class NativeSshInterop : INativeSshInterop
{
    private const string LibName = "rusty_ssh";
    private const int ResultOk = 0;
    private const int ResultEventReady = 1;
    private const int ResultInvalidArgument = -1;
    private const int ResultBufferTooSmall = -2;
    private const int ResultClosed = -3;

    public IntPtr Connect(NativeSshConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        IntPtr hostPtr = IntPtr.Zero;
        IntPtr userPtr = IntPtr.Zero;
        IntPtr termPtr = IntPtr.Zero;
        IntPtr identityPtr = IntPtr.Zero;

        try
        {
            hostPtr = Marshal.StringToCoTaskMemUTF8(options.Host);
            userPtr = Marshal.StringToCoTaskMemUTF8(options.User);
            termPtr = Marshal.StringToCoTaskMemUTF8(options.Term);
            if (!string.IsNullOrWhiteSpace(options.IdentityFilePath))
            {
                identityPtr = Marshal.StringToCoTaskMemUTF8(options.IdentityFilePath);
            }

            NativeConnectArgs args = new()
            {
                Host = hostPtr,
                User = userPtr,
                Port = checked((ushort)options.Port),
                Cols = checked((ushort)options.Cols),
                Rows = checked((ushort)options.Rows),
                Term = termPtr,
                IdentityFile = identityPtr
            };

            IntPtr handle = NativeMethods.nova_ssh_connect(in args);
            if (handle == IntPtr.Zero)
            {
                throw new InvalidOperationException("Failed to create native SSH session.");
            }

            return handle;
        }
        finally
        {
            FreeUtf8(hostPtr);
            FreeUtf8(userPtr);
            FreeUtf8(termPtr);
            FreeUtf8(identityPtr);
        }
    }

    public NativeSshEvent? PollEvent(IntPtr sessionHandle)
    {
        if (sessionHandle == IntPtr.Zero)
        {
            return null;
        }

        byte[] buffer = Array.Empty<byte>();
        NativeEventHeader header = default;

        while (true)
        {
            int rc = NativeMethods.nova_ssh_poll_event(sessionHandle, out header, buffer, (nuint)buffer.Length);
            if (rc == ResultOk)
            {
                return null;
            }

            if (rc == ResultBufferTooSmall)
            {
                buffer = new byte[header.PayloadLength];
                continue;
            }

            if (rc == ResultEventReady)
            {
                int payloadLength = checked((int)header.PayloadLength);
                byte[] payload = payloadLength == 0
                    ? Array.Empty<byte>()
                    : buffer[..payloadLength];
                return new NativeSshEvent((NativeSshEventKind)header.Kind, payload, header.StatusCode, (NativeSshEventFlags)header.Flags);
            }

            throw new InvalidOperationException($"Native SSH poll failed with result {rc}.");
        }
    }

    public void Write(IntPtr sessionHandle, ReadOnlySpan<byte> data)
    {
        if (sessionHandle == IntPtr.Zero)
        {
            return;
        }

        byte[] payload = data.ToArray();
        int rc = NativeMethods.nova_ssh_write(sessionHandle, payload, (nuint)payload.Length);
        if (rc is ResultOk or ResultInvalidArgument)
        {
            return;
        }

        throw new InvalidOperationException($"Native SSH write failed with result {rc}.");
    }

    public void Resize(IntPtr sessionHandle, int cols, int rows)
    {
        if (sessionHandle == IntPtr.Zero || cols <= 0 || rows <= 0)
        {
            return;
        }

        int rc = NativeMethods.nova_ssh_resize(sessionHandle, checked((ushort)cols), checked((ushort)rows));
        if (rc is ResultOk or ResultInvalidArgument)
        {
            return;
        }

        throw new InvalidOperationException($"Native SSH resize failed with result {rc}.");
    }

    public int OpenDirectTcpIp(IntPtr sessionHandle, NativePortForwardOpenOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (sessionHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Cannot open a native port-forward channel without a session handle.");
        }

        IntPtr hostPtr = IntPtr.Zero;
        IntPtr originatorPtr = IntPtr.Zero;

        try
        {
            hostPtr = Marshal.StringToCoTaskMemUTF8(options.HostToConnect);
            originatorPtr = Marshal.StringToCoTaskMemUTF8(options.OriginatorAddress);

            NativeDirectTcpIpOpenArgs args = new()
            {
                HostToConnect = hostPtr,
                PortToConnect = checked((ushort)options.PortToConnect),
                OriginatorAddress = originatorPtr,
                OriginatorPort = checked((ushort)options.OriginatorPort)
            };

            int channelId = NativeMethods.nova_ssh_open_direct_tcpip(sessionHandle, in args);
            if (channelId >= 0)
            {
                return channelId;
            }

            throw new InvalidOperationException($"Native SSH direct-tcpip open failed with result {channelId}.");
        }
        finally
        {
            FreeUtf8(hostPtr);
            FreeUtf8(originatorPtr);
        }
    }

    public void WriteChannel(IntPtr sessionHandle, int channelId, ReadOnlySpan<byte> data)
    {
        if (sessionHandle == IntPtr.Zero || channelId < 0)
        {
            return;
        }

        byte[] payload = data.ToArray();
        int rc = NativeMethods.nova_ssh_channel_write(sessionHandle, checked((uint)channelId), payload, (nuint)payload.Length);
        if (rc is ResultOk or ResultInvalidArgument or ResultClosed)
        {
            return;
        }

        throw new InvalidOperationException($"Native SSH channel write failed with result {rc}.");
    }

    public void SendChannelEof(IntPtr sessionHandle, int channelId)
    {
        if (sessionHandle == IntPtr.Zero || channelId < 0)
        {
            return;
        }

        int rc = NativeMethods.nova_ssh_channel_eof(sessionHandle, checked((uint)channelId));
        if (rc is ResultOk or ResultInvalidArgument or ResultClosed)
        {
            return;
        }

        throw new InvalidOperationException($"Native SSH channel EOF failed with result {rc}.");
    }

    public void CloseChannel(IntPtr sessionHandle, int channelId)
    {
        if (sessionHandle == IntPtr.Zero || channelId < 0)
        {
            return;
        }

        int rc = NativeMethods.nova_ssh_channel_close(sessionHandle, checked((uint)channelId));
        if (rc is ResultOk or ResultInvalidArgument or ResultClosed)
        {
            return;
        }

        throw new InvalidOperationException($"Native SSH channel close failed with result {rc}.");
    }

    public void Close(IntPtr sessionHandle)
    {
        if (sessionHandle == IntPtr.Zero)
        {
            return;
        }

        int rc = NativeMethods.nova_ssh_close(sessionHandle);
        if (rc is ResultOk or ResultInvalidArgument)
        {
            return;
        }

        throw new InvalidOperationException($"Native SSH close failed with result {rc}.");
    }

    public void SubmitResponse(IntPtr sessionHandle, NativeSshResponseKind responseKind, ReadOnlySpan<byte> data)
    {
        if (sessionHandle == IntPtr.Zero)
        {
            return;
        }

        byte[] payload = data.ToArray();
        int rc = NativeMethods.nova_ssh_submit_response(sessionHandle, (uint)responseKind, payload, (nuint)payload.Length);
        if (rc is ResultOk or ResultInvalidArgument)
        {
            return;
        }

        throw new InvalidOperationException($"Native SSH submit response failed with result {rc}.");
    }

    private static void FreeUtf8(IntPtr pointer)
    {
        if (pointer != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeConnectArgs
    {
        public IntPtr Host;
        public IntPtr User;
        public ushort Port;
        public ushort Cols;
        public ushort Rows;
        public IntPtr Term;
        public IntPtr IdentityFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeEventHeader
    {
        public uint Kind;
        public uint PayloadLength;
        public int StatusCode;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeDirectTcpIpOpenArgs
    {
        public IntPtr HostToConnect;
        public ushort PortToConnect;
        public IntPtr OriginatorAddress;
        public ushort OriginatorPort;
    }

    private static class NativeMethods
    {
        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nova_ssh_connect")]
        public static extern IntPtr nova_ssh_connect(in NativeConnectArgs args);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nova_ssh_poll_event")]
        public static extern int nova_ssh_poll_event(IntPtr session, out NativeEventHeader @event, byte[] payload, nuint payloadCapacity);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nova_ssh_write")]
        public static extern int nova_ssh_write(IntPtr session, byte[] data, nuint dataLength);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nova_ssh_resize")]
        public static extern int nova_ssh_resize(IntPtr session, ushort cols, ushort rows);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nova_ssh_open_direct_tcpip")]
        public static extern int nova_ssh_open_direct_tcpip(IntPtr session, in NativeDirectTcpIpOpenArgs args);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nova_ssh_channel_write")]
        public static extern int nova_ssh_channel_write(IntPtr session, uint channelId, byte[] data, nuint dataLength);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nova_ssh_channel_eof")]
        public static extern int nova_ssh_channel_eof(IntPtr session, uint channelId);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nova_ssh_channel_close")]
        public static extern int nova_ssh_channel_close(IntPtr session, uint channelId);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nova_ssh_close")]
        public static extern int nova_ssh_close(IntPtr session);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nova_ssh_submit_response")]
        public static extern int nova_ssh_submit_response(IntPtr session, uint responseKind, byte[] data, nuint dataLength);
    }
}
