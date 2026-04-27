using System.Runtime.InteropServices;
using System.Text.Json;

namespace NovaTerminal.Core.Ssh.Native;

public sealed class NativeSshInterop : INativeSshInterop
{
    private const string LibName = "rusty_ssh";
    private const int ResultOk = 0;
    private const int ResultEventReady = 1;
    private const int ResultInvalidArgument = -1;
    private const int ResultBufferTooSmall = -2;
    private const int ResultClosed = -3;
    private const int ResultCanceled = -6;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly NativeSftpTransferProgressCallback SftpTransferProgressCallback = OnNativeSftpTransferProgress;
    private static readonly IntPtr SftpTransferProgressCallbackPointer =
        Marshal.GetFunctionPointerForDelegate(SftpTransferProgressCallback);

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

            IntPtr jumpHostPtr = IntPtr.Zero;
            IntPtr jumpUserPtr = IntPtr.Zero;

            try
            {
                if (options.JumpHost != null)
                {
                    jumpHostPtr = Marshal.StringToCoTaskMemUTF8(options.JumpHost.Host);
                    if (!string.IsNullOrWhiteSpace(options.JumpHost.User))
                    {
                        jumpUserPtr = Marshal.StringToCoTaskMemUTF8(options.JumpHost.User);
                    }
                }

                NativeConnectArgs args = new()
                {
                    Host = hostPtr,
                    User = userPtr,
                    Port = checked((ushort)options.Port),
                    Cols = checked((ushort)options.Cols),
                    Rows = checked((ushort)options.Rows),
                    Term = termPtr,
                    IdentityFile = identityPtr,
                    JumpHost = jumpHostPtr,
                    JumpUser = jumpUserPtr,
                    JumpPort = checked((ushort)(options.JumpHost?.Port ?? 0)),
                    KeepAliveIntervalSeconds = checked((uint)Math.Max(0, options.KeepAliveIntervalSeconds)),
                    KeepAliveCountMax = checked((uint)Math.Max(0, options.KeepAliveCountMax))
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
                FreeUtf8(jumpHostPtr);
                FreeUtf8(jumpUserPtr);
            }
        }
        finally
        {
            FreeUtf8(hostPtr);
            FreeUtf8(userPtr);
            FreeUtf8(termPtr);
            FreeUtf8(identityPtr);
        }
    }

    public void RunSftpTransfer(
        NativeSshConnectionOptions connectionOptions,
        NativeSftpTransferOptions transferOptions,
        Action<NativeSftpTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connectionOptions);
        ArgumentNullException.ThrowIfNull(transferOptions);

        ValidateConnectionOptions(connectionOptions);
        ValidateSftpConnectionOptions(connectionOptions);
        transferOptions.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        SftpTransferRequest request = SftpTransferRequest.From(connectionOptions, transferOptions);
        string requestJson = JsonSerializer.Serialize(request, JsonOptions);

        IntPtr requestPtr = IntPtr.Zero;
        IntPtr responsePtr = IntPtr.Zero;
        GCHandle progressStateHandle = default;
        string cancellationMarkerPath = Path.Combine(
            Path.GetTempPath(),
            $"nova-sftp-cancel-{Guid.NewGuid():N}.signal");
        using CancellationTokenRegistration cancellationRegistration = cancellationToken.Register(() =>
        {
            try
            {
                File.WriteAllText(cancellationMarkerPath, string.Empty);
            }
            catch
            {
            }
        });

        try
        {
            request = request with
            {
                Transfer = request.Transfer with
                {
                    CancellationMarkerPath = cancellationMarkerPath
                }
            };
            requestJson = JsonSerializer.Serialize(request, JsonOptions);
            requestPtr = Marshal.StringToCoTaskMemUTF8(requestJson);
            if (progress is not null)
            {
                // Native SFTP transfer callbacks are expected to stay synchronous within the
                // nova_ssh_sftp_transfer call, so this GCHandle only needs to live for that invocation.
                progressStateHandle = GCHandle.Alloc(new NativeSftpTransferProgressCallbackState(progress));
            }

            int rc = NativeMethods.nova_ssh_sftp_transfer(
                requestPtr,
                progressStateHandle.IsAllocated ? SftpTransferProgressCallbackPointer : IntPtr.Zero,
                progressStateHandle.IsAllocated ? GCHandle.ToIntPtr(progressStateHandle) : IntPtr.Zero,
                out responsePtr);
            string? responseJson = TakeNativeUtf8AndFree(ref responsePtr);
            if (rc == ResultCanceled)
            {
                throw new OperationCanceledException(BuildSftpTransferFailureMessage(rc, responseJson), cancellationToken);
            }

            if (rc != ResultOk)
            {
                throw new InvalidOperationException(BuildSftpTransferFailureMessage(rc, responseJson));
            }
        }
        finally
        {
            if (progressStateHandle.IsAllocated)
            {
                progressStateHandle.Free();
            }

            FreeUtf8(requestPtr);
            if (responsePtr != IntPtr.Zero)
            {
                NativeMethods.nova_ssh_string_free(responsePtr);
            }

            try
            {
                if (File.Exists(cancellationMarkerPath))
                {
                    File.Delete(cancellationMarkerPath);
                }
            }
            catch
            {
            }
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

    internal static void InvokeManagedProgressCallbackForTest(
        Action<NativeSftpTransferProgress> progress,
        ulong bytesDone,
        ulong bytesTotal,
        string currentPath)
    {
        ArgumentNullException.ThrowIfNull(progress);

        IntPtr currentPathPtr = IntPtr.Zero;

        try
        {
            currentPathPtr = Marshal.StringToCoTaskMemUTF8(currentPath);
            InvokeManagedProgressCallback(
                progress,
                new NativeSftpTransferProgressCallbackData
                {
                    BytesDone = bytesDone,
                    BytesTotal = bytesTotal,
                    CurrentPath = currentPathPtr
                });
        }
        finally
        {
            FreeUtf8(currentPathPtr);
        }
    }

    private static void FreeUtf8(IntPtr pointer)
    {
        if (pointer != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    private static void OnNativeSftpTransferProgress(IntPtr context, NativeSftpTransferProgressCallbackData progress)
    {
        if (context == IntPtr.Zero)
        {
            return;
        }

        GCHandle handle = GCHandle.FromIntPtr(context);
        if (handle.Target is not NativeSftpTransferProgressCallbackState state)
        {
            return;
        }

        try
        {
            InvokeManagedProgressCallback(state.Progress, progress);
        }
        catch
        {
        }
    }

    private static void InvokeManagedProgressCallback(
        Action<NativeSftpTransferProgress> progress,
        NativeSftpTransferProgressCallbackData nativeProgress)
    {
        progress(new NativeSftpTransferProgress
        {
            BytesDone = checked((long)nativeProgress.BytesDone),
            BytesTotal = checked((long)nativeProgress.BytesTotal),
            CurrentPath = nativeProgress.CurrentPath == IntPtr.Zero
                ? null
                : Marshal.PtrToStringUTF8(nativeProgress.CurrentPath)
        });
    }

    private static void ValidateConnectionOptions(NativeSshConnectionOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Host))
        {
            throw new ArgumentException("A host is required for native SSH operations.", nameof(options.Host));
        }

        if (string.IsNullOrWhiteSpace(options.User))
        {
            throw new ArgumentException("A user is required for native SSH operations.", nameof(options.User));
        }

        if (options.Port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(options.Port), "The SSH port must be between 1 and 65535.");
        }

        if (options.JumpHost is { } jumpHost)
        {
            if (string.IsNullOrWhiteSpace(jumpHost.Host))
            {
                throw new ArgumentException("A jump-host name is required when jump-host options are supplied.", nameof(options.JumpHost));
            }

            if (jumpHost.Port is < 1 or > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(options.JumpHost), "The jump-host port must be between 1 and 65535.");
            }
        }
    }

    private static void ValidateSftpConnectionOptions(NativeSshConnectionOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.KnownHostsFilePath))
        {
            throw new ArgumentException(
                "A known-hosts store path is required for native SFTP transfers.",
                nameof(options.KnownHostsFilePath));
        }
    }

    private static string BuildSftpTransferFailureMessage(int resultCode, string? responseJson)
    {
        string message = $"Native SFTP transfer failed with result {resultCode}.";
        if (string.IsNullOrWhiteSpace(responseJson))
        {
            return message;
        }

        try
        {
            NativeSftpTransferResponse? response = JsonSerializer.Deserialize<NativeSftpTransferResponse>(responseJson, JsonOptions);
            if (!string.IsNullOrWhiteSpace(response?.Message))
            {
                return $"{message} {response.Message}";
            }
        }
        catch (JsonException)
        {
        }

        return $"{message} {responseJson}";
    }

    private static string? TakeNativeUtf8AndFree(ref IntPtr pointer)
    {
        if (pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUTF8(pointer);
        }
        finally
        {
            NativeMethods.nova_ssh_string_free(pointer);
            pointer = IntPtr.Zero;
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
        public IntPtr JumpHost;
        public IntPtr JumpUser;
        public ushort JumpPort;
        public uint KeepAliveIntervalSeconds;
        public uint KeepAliveCountMax;
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

    private sealed record SftpTransferRequest(SftpConnectionRequest Connection, SftpTransferRequestBody Transfer)
    {
        public static SftpTransferRequest From(NativeSshConnectionOptions connectionOptions, NativeSftpTransferOptions transferOptions)
        {
            SftpJumpHostRequest? jumpHost = connectionOptions.JumpHost is null
                ? null
                : new SftpJumpHostRequest(
                    connectionOptions.JumpHost.Host,
                    string.IsNullOrWhiteSpace(connectionOptions.JumpHost.User) ? null : connectionOptions.JumpHost.User,
                    connectionOptions.JumpHost.Port);

            return new SftpTransferRequest(
                new SftpConnectionRequest(
                    connectionOptions.Host,
                    connectionOptions.User,
                    connectionOptions.Port,
                    string.IsNullOrWhiteSpace(connectionOptions.Password) ? null : connectionOptions.Password,
                    string.IsNullOrWhiteSpace(connectionOptions.IdentityFilePath) ? null : connectionOptions.IdentityFilePath,
                    connectionOptions.KnownHostsFilePath!,
                    jumpHost),
                new SftpTransferRequestBody(
                    transferOptions.Direction.ToString().ToLowerInvariant(),
                    transferOptions.Kind.ToString().ToLowerInvariant(),
                    transferOptions.LocalPath!,
                    transferOptions.RemotePath!));
        }
    }

    private sealed record SftpConnectionRequest(
        string Host,
        string User,
        int Port,
        string? Password,
        string? IdentityFilePath,
        string KnownHostsFilePath,
        SftpJumpHostRequest? JumpHost);

    private sealed record SftpJumpHostRequest(string Host, string? User, int Port);

    private sealed record SftpTransferRequestBody(
        string Direction,
        string Kind,
        string LocalPath,
        string RemotePath,
        string? CancellationMarkerPath = null);

    private sealed class NativeSftpTransferResponse
    {
        public string? Status { get; init; }
        public string? Message { get; init; }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void NativeSftpTransferProgressCallback(
        IntPtr context,
        NativeSftpTransferProgressCallbackData progress);

    private sealed class NativeSftpTransferProgressCallbackState
    {
        public NativeSftpTransferProgressCallbackState(Action<NativeSftpTransferProgress> progress)
        {
            Progress = progress;
        }

        public Action<NativeSftpTransferProgress> Progress { get; }
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

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nova_ssh_sftp_transfer")]
        public static extern int nova_ssh_sftp_transfer(
            IntPtr requestJson,
            IntPtr progressCallback,
            IntPtr progressContext,
            out IntPtr responseJson);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nova_ssh_string_free")]
        public static extern void nova_ssh_string_free(IntPtr value);
    }
}
