using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WinIDM.Browser;

/// <summary>
/// SOCKS5 client (RFC 1928 and RFC 1929) for CONNECT connections. The host name is sent to the proxy
/// (ATYP 0x03) so that DNS resolution happens at the proxy end, not locally.
/// </summary>
internal static class Socks5Client
{
    private const byte Version = 0x05;
    private const byte MethodNoAuth = 0x00;
    private const byte MethodUserPass = 0x02;
    private const byte MethodNoAcceptable = 0xFF;
    private const byte CmdConnect = 0x01;
    private const byte AtypIPv4 = 0x01;
    private const byte AtypDomain = 0x03;
    private const byte AtypIPv6 = 0x04;

    public static async Task<Stream> ConnectAsync(string proxyHost, int proxyPort, string targetHost, int targetPort,
        string? userName, string? password, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetHost);
        if (targetPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(targetPort));

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        NetworkStream stream;
        try
        {
            await socket.ConnectAsync(proxyHost, proxyPort, ct).ConfigureAwait(false);
            stream = new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        try
        {
            await HandshakeAsync(stream, targetHost, targetPort, userName, password, ct).ConfigureAwait(false);
            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task HandshakeAsync(Stream stream, string targetHost, int targetPort,
        string? userName, string? password, CancellationToken ct)
    {
        // Greeting: list of authentication methods.
        byte[] greeting = userName is null
            ? new[] { Version, (byte)1, MethodNoAuth }
            : new[] { Version, (byte)2, MethodNoAuth, MethodUserPass };
        await stream.WriteAsync(greeting, ct).ConfigureAwait(false);

        var choice = new byte[2];
        await ReadExactAsync(stream, choice, ct).ConfigureAwait(false);
        if (choice[0] != Version) throw new IOException("Invalid SOCKS5 response.");
        if (choice[1] == MethodNoAcceptable) throw new IOException("SOCKS5 proxy rejected all authentication methods.");

        if (choice[1] == MethodUserPass)
        {
            if (userName is null) throw new IOException("Proxy requires authentication that was not supplied.");
            byte[] user = Encoding.UTF8.GetBytes(userName);
            byte[] pass = Encoding.UTF8.GetBytes(password ?? "");
            if (user.Length > 255 || pass.Length > 255) throw new ArgumentException("Proxy user name or password is too long.");

            var auth = new byte[3 + user.Length + pass.Length];
            auth[0] = 0x01;
            auth[1] = (byte)user.Length;
            user.CopyTo(auth, 2);
            auth[2 + user.Length] = (byte)pass.Length;
            pass.CopyTo(auth, 3 + user.Length);
            await stream.WriteAsync(auth, ct).ConfigureAwait(false);

            var authReply = new byte[2];
            await ReadExactAsync(stream, authReply, ct).ConfigureAwait(false);
            if (authReply[1] != 0x00) throw new UnauthorizedAccessException("SOCKS5 proxy authentication failed.");
        }
        else if (choice[1] != MethodNoAuth)
        {
            throw new IOException("Unsupported proxy authentication method.");
        }

        // CONNECT request.
        byte[] address = EncodeAddress(targetHost);
        var request = new byte[3 + address.Length + 2];
        request[0] = Version;
        request[1] = CmdConnect;
        request[2] = 0x00;
        address.CopyTo(request, 3);
        BinaryPrimitivesWritePort(request, 3 + address.Length, targetPort);
        await stream.WriteAsync(request, ct).ConfigureAwait(false);

        // Reply: VER REP RSV ATYP BND.ADDR BND.PORT.
        var header = new byte[4];
        await ReadExactAsync(stream, header, ct).ConfigureAwait(false);
        if (header[0] != Version) throw new IOException("Invalid SOCKS5 response.");
        if (header[1] != 0x00) throw new IOException($"SOCKS5 proxy rejected the connection (code {header[1]}).");

        int boundLength = header[3] switch
        {
            AtypIPv4 => 4,
            AtypIPv6 => 16,
            AtypDomain => await ReadDomainLengthAsync(stream, ct).ConfigureAwait(false),
            _ => throw new IOException("Invalid SOCKS5 reply ATYP."),
        };
        var bound = new byte[boundLength + 2];
        await ReadExactAsync(stream, bound, ct).ConfigureAwait(false);
    }

    private static async Task<int> ReadDomainLengthAsync(Stream stream, CancellationToken ct)
    {
        var len = new byte[1];
        await ReadExactAsync(stream, len, ct).ConfigureAwait(false);
        return len[0];
    }

    private static byte[] EncodeAddress(string host)
    {
        if (IPAddress.TryParse(host, out var ip))
        {
            byte[] raw = ip.GetAddressBytes();
            byte[] result = new byte[1 + raw.Length];
            result[0] = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? AtypIPv6 : AtypIPv4;
            raw.CopyTo(result, 1);
            return result;
        }

        byte[] name = Encoding.ASCII.GetBytes(host);
        if (name.Length == 0 || name.Length > 255) throw new ArgumentException("Invalid SOCKS5 host name.", nameof(host));
        var domain = new byte[2 + name.Length];
        domain[0] = AtypDomain;
        domain[1] = (byte)name.Length;
        name.CopyTo(domain, 2);
        return domain;
    }

    private static void BinaryPrimitivesWritePort(byte[] buffer, int offset, int port)
    {
        buffer[offset] = (byte)(port >> 8);
        buffer[offset + 1] = (byte)(port & 0xFF);
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
            if (n == 0) throw new IOException("SOCKS5 proxy closed the connection.");
            offset += n;
        }
    }
}
