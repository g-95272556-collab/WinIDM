using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace WinIDM.Engine.Tests.Support;

/// <summary>Test certificate helper: a self-signed certificate for localhost, exportable and usable by SslStream.</summary>
public static class TestCertificate
{
    public static X509Certificate2 CreateSelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=winidm-proxy-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        // The key must be exportable; a directly generated certificate does not support ephemeral keys on Windows.
        return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pfx), (string?)null,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
    }
}

/// <summary>
/// Forward HTTP proxy over TLS on 127.0.0.1 (tests only). Each TLS connection carries one absolute-form
/// request (<c>GET http://host:port/path</c>) that is forwarded to the target in origin-form.
/// </summary>
public sealed class TlsForwardProxy : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly X509Certificate2 _certificate;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentQueue<string> _requestLines = new();
    private readonly Task _accept;

    public TlsForwardProxy(X509Certificate2 certificate)
    {
        _certificate = certificate;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accept = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    /// <summary>Request lines received (for example <c>GET http://127.0.0.1:1234/file.bin HTTP/1.1</c>).</summary>
    public IReadOnlyList<string> RequestLines => _requestLines.ToArray();

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try
        {
            await _accept;
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
                using var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _certificate });

                var head = await ReadHeadAsync(tls);
                if (head is null) return;

                var lines = head.Split("\r\n");
                var requestLine = lines[0];
                _requestLines.Enqueue(requestLine);

                var parts = requestLine.Split(' ');
                var target = new Uri(parts[1], UriKind.Absolute);
                using var upstream = new TcpClient();
                await upstream.ConnectAsync(target.Host, target.Port);

                var rewritten = $"{parts[0]} {target.PathAndQuery} {parts[2]}\r\n" + string.Join("\r\n", lines.Skip(1));
                var upstreamStream = upstream.GetStream();
                await upstreamStream.WriteAsync(Encoding.ASCII.GetBytes(rewritten));
                await upstreamStream.CopyToAsync(tls);
                await tls.FlushAsync();
            }
            catch (Exception)
            {
                // A failed connection (for example a certificate rejected by the client) is part of the test.
            }
        }
    }

    private static async Task<string?> ReadHeadAsync(Stream stream)
    {
        var buffer = new List<byte>(512);
        var single = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(single);
            if (read == 0) return null;

            buffer.Add(single[0]);
            var count = buffer.Count;
            if (count >= 4 && buffer[count - 4] == '\r' && buffer[count - 3] == '\n' &&
                buffer[count - 2] == '\r' && buffer[count - 1] == '\n')
            {
                return Encoding.ASCII.GetString(buffer.ToArray());
            }

            if (count > 16 * 1024) return null;
        }
    }
}
