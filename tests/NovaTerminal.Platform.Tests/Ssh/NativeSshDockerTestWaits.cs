using System.Net;
using System.Net.Sockets;
using System.Text;
using NovaTerminal.Replay;
using NovaTerminal.VT;

namespace NovaTerminal.Platform.Tests.Ssh;

/// <summary>
/// Waiting and traffic helpers shared by the Docker end-to-end suites.
///
/// NativeSshDockerE2eTests predates this and carries its own private copies of the snapshot/wait
/// helpers; they are left alone rather than migrated, because that file is the only coverage for the
/// existing e2e set and churning it to share twenty lines is a poor trade.
/// </summary>
internal static class NativeSshDockerTestWaits
{
    public static bool SnapshotContains(TerminalBuffer buffer, string value) =>
        BufferSnapshot.Capture(buffer).Lines.Any(line => line.Contains(value, StringComparison.Ordinal));

    public static bool SnapshotContainsExactLine(TerminalBuffer buffer, string value) =>
        BufferSnapshot.Capture(buffer).Lines.Any(line => line == value);

    public static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout, string description)
    {
        DateTime deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        Assert.True(predicate(), $"Timed out waiting for {description}.");
    }

    /// <summary>
    /// True once <paramref name="predicate"/> has held for the whole window. For asserting something
    /// does NOT happen — where a single check right after connecting proves nothing, because the thing
    /// under test may simply not have got there yet.
    /// </summary>
    public static async Task AssertStaysTrueAsync(Func<bool> predicate, TimeSpan window, string description)
    {
        DateTime deadline = DateTime.UtcNow.Add(window);
        while (DateTime.UtcNow < deadline)
        {
            Assert.True(predicate(), $"Expected {description} to hold for {window.TotalSeconds:0}s, but it stopped holding.");
            await Task.Delay(100).ConfigureAwait(false);
        }
    }

    public static int GetFreeLocalPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// Writes a payload to an already-connected stream and reads exactly as many bytes back, which is
    /// what the fixture's echo service returns. Proves a forward carries data, not merely that a
    /// channel opened.
    /// </summary>
    public static async Task<string> EchoRoundTripAsync(NetworkStream stream, string payload, TimeSpan timeout)
    {
        byte[] outbound = Encoding.ASCII.GetBytes(payload);
        using var cts = new CancellationTokenSource(timeout);

        await stream.WriteAsync(outbound, cts.Token).ConfigureAwait(false);
        await stream.FlushAsync(cts.Token).ConfigureAwait(false);

        byte[] inbound = new byte[outbound.Length];
        int offset = 0;
        while (offset < inbound.Length)
        {
            int read = await stream.ReadAsync(inbound.AsMemory(offset, inbound.Length - offset), cts.Token).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException($"Forward closed after {offset} of {inbound.Length} echoed bytes.");
            }

            offset += read;
        }

        return Encoding.ASCII.GetString(inbound);
    }

    /// <summary>
    /// Minimal SOCKS5 client: no-auth greeting, then CONNECT to the given host and port. Returns once
    /// the proxy has reported success, leaving the stream ready for payload traffic.
    /// </summary>
    public static async Task Socks5ConnectAsync(NetworkStream stream, string host, int port, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);

        byte[] greeting = [0x05, 0x01, 0x00];
        await stream.WriteAsync(greeting, cts.Token).ConfigureAwait(false);
        await stream.FlushAsync(cts.Token).ConfigureAwait(false);

        byte[] greetingReply = await ReadExactlyAsync(stream, 2, cts.Token).ConfigureAwait(false);
        Assert.Equal(0x05, greetingReply[0]);
        Assert.Equal(0x00, greetingReply[1]);

        byte[] hostBytes = Encoding.ASCII.GetBytes(host);
        byte[] request = new byte[7 + hostBytes.Length];
        request[0] = 0x05;             // version
        request[1] = 0x01;             // CONNECT
        request[2] = 0x00;             // reserved
        request[3] = 0x03;             // domain name
        request[4] = (byte)hostBytes.Length;
        Buffer.BlockCopy(hostBytes, 0, request, 5, hostBytes.Length);
        request[5 + hostBytes.Length] = (byte)((port >> 8) & 0xFF);
        request[6 + hostBytes.Length] = (byte)(port & 0xFF);

        await stream.WriteAsync(request, cts.Token).ConfigureAwait(false);
        await stream.FlushAsync(cts.Token).ConfigureAwait(false);

        // Reply is 10 bytes for the IPv4 bind address this backend always reports.
        byte[] connectReply = await ReadExactlyAsync(stream, 10, cts.Token).ConfigureAwait(false);
        Assert.Equal(0x05, connectReply[0]);
        Assert.Equal(0x00, connectReply[1]);
    }

    private static async Task<byte[]> ReadExactlyAsync(NetworkStream stream, int length, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[length];
        int offset = 0;
        while (offset < length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, length - offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException($"Stream closed after {offset} of {length} expected bytes.");
            }

            offset += read;
        }

        return buffer;
    }
}
