using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WinIDM.Engine.Tests.Support;

/// <summary>
/// Minimal SOCKS5 proxy server on 127.0.0.1 (RFC 1928, RFC 1929 auth). Tests only; no internet access.
/// Every CONNECT is recorded (requested host, port) and connected to 127.0.0.1 on the same port.
/// </summary>
public sealed class Socks5TestServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly string? _username;
    private readonly string? _password;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;

    public Socks5TestServer(string? username = null, string? password = null)
    {
        _username = username;
        _password = password;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    /// <summary>Received CONNECT targets, format <c>host:port</c>.</summary>
    public ConcurrentQueue<string> Connects { get; } = new();

    /// <summary>Number of failed auth attempts.</summary>
    public int AuthFailures;

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        try
        {
            await _acceptLoop;
        }
        catch (Exception)
        {
            // The accept loop is stopped on purpose.
        }

        _cts.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (Exception)
            {
                break;
            }

            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();

                // Greeting: VER, NMETHODS, METHODS.
                var header = await ReadExactAsync(stream, 2);
                if (header[0] != 0x05) return;
                var methods = await ReadExactAsync(stream, header[1]);

                var requiresAuth = _username is not null;
                var wanted = requiresAuth ? (byte)0x02 : (byte)0x00;
                if (!methods.Contains(wanted))
                {
                    await stream.WriteAsync(new byte[] { 0x05, 0xFF });
                    return;
                }

                await stream.WriteAsync(new[] { (byte)0x05, wanted });
                if (requiresAuth && !await AuthenticateAsync(stream))
                {
                    Interlocked.Increment(ref AuthFailures);
                    return;
                }

                // Request: VER, CMD, RSV, ATYP, DST.ADDR, DST.PORT.
                var request = await ReadExactAsync(stream, 4);
                if (request[1] != 0x01)
                {
                    await stream.WriteAsync(new byte[] { 0x05, 0x07, 0x00, 0x01, 0, 0, 0, 0, 0, 0 });
                    return;
                }

                string host;
                switch (request[3])
                {
                    case 0x01:
                        host = new IPAddress(await ReadExactAsync(stream, 4)).ToString();
                        break;
                    case 0x03:
                        var length = (await ReadExactAsync(stream, 1))[0];
                        host = Encoding.ASCII.GetString(await ReadExactAsync(stream, length));
                        break;
                    case 0x04:
                        host = new IPAddress(await ReadExactAsync(stream, 16)).ToString();
                        break;
                    default:
                        return;
                }

                var portBytes = await ReadExactAsync(stream, 2);
                var port = (portBytes[0] << 8) | portBytes[1];
                Connects.Enqueue($"{host}:{port}");

                using var upstream = new TcpClient();
                try
                {
                    await upstream.ConnectAsync(IPAddress.Loopback, port, _cts.Token);
                }
                catch (SocketException)
                {
                    await stream.WriteAsync(new byte[] { 0x05, 0x05, 0x00, 0x01, 0, 0, 0, 0, 0, 0 });
                    return;
                }

                await stream.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 });
                await RelayAsync(stream, upstream.GetStream());
            }
            catch (Exception)
            {
                // The connection was dropped by the test client: nothing to do.
            }
        }
    }

    /// <summary>Auth RFC 1929: VER(1), ULEN, UNAME, PLEN, PASSWD.</summary>
    private async Task<bool> AuthenticateAsync(NetworkStream stream)
    {
        var version = await ReadExactAsync(stream, 1);
        var userLength = (await ReadExactAsync(stream, 1))[0];
        var user = Encoding.UTF8.GetString(await ReadExactAsync(stream, userLength));
        var passLength = (await ReadExactAsync(stream, 1))[0];
        var pass = Encoding.UTF8.GetString(await ReadExactAsync(stream, passLength));

        var ok = version[0] == 0x01 && user == _username && pass == _password;
        await stream.WriteAsync(new[] { (byte)0x01, (byte)(ok ? 0x00 : 0x01) });
        return ok;
    }

    private static async Task RelayAsync(Stream client, Stream upstream)
    {
        var up = client.CopyToAsync(upstream);
        var down = upstream.CopyToAsync(client);
        await Task.WhenAny(up, down);
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int count)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset));
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }

        return buffer;
    }
}
