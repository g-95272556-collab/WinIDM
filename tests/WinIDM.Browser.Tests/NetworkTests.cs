using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Buffers.Binary;

namespace WinIDM.Browser.Tests;

/// <summary>Builds fake DNS messages for tests.</summary>
internal static class DnsTestMessages
{
    public static ushort QueryId(byte[] query) => BinaryPrimitives.ReadUInt16BigEndian(query);

    public static ushort QueryType(byte[] query)
    {
        int pos = 12;
        while (query[pos] != 0) pos += query[pos] + 1;
        return BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(pos + 1));
    }

    public static byte[] Answer(byte[] query, IPAddress? address, uint ttl = 120, int rcode = 0, ushort? overrideId = null)
    {
        var msg = new List<byte>();
        ushort id = overrideId ?? QueryId(query);
        msg.Add((byte)(id >> 8));
        msg.Add((byte)id);
        ushort flags = (ushort)(0x8180 | rcode);
        msg.Add((byte)(flags >> 8));
        msg.Add((byte)flags);
        msg.AddRange(new byte[] { 0, 1 });
        msg.AddRange(new byte[] { 0, (byte)(address is null ? 0 : 1) });
        msg.AddRange(new byte[] { 0, 0, 0, 0 });

        // Copy the question section (one question only).
        int qEnd = 12;
        while (query[qEnd] != 0) qEnd += query[qEnd] + 1;
        qEnd += 5;
        msg.AddRange(query.AsSpan(12, qEnd - 12).ToArray());

        if (address is not null)
        {
            msg.AddRange(new byte[] { 0xC0, 0x0C }); // pointer to the question name
            bool v4 = address.AddressFamily == AddressFamily.InterNetwork;
            ushort type = v4 ? (ushort)1 : (ushort)28;
            msg.Add((byte)(type >> 8));
            msg.Add((byte)type);
            msg.AddRange(new byte[] { 0, 1 });
            msg.Add((byte)(ttl >> 24));
            msg.Add((byte)(ttl >> 16));
            msg.Add((byte)(ttl >> 8));
            msg.Add((byte)ttl);
            byte[] raw = address.GetAddressBytes();
            msg.Add((byte)(raw.Length >> 8));
            msg.Add((byte)raw.Length);
            msg.AddRange(raw);
        }
        return msg.ToArray();
    }
}

public class DnsWireFormatTests
{
    [Fact]
    public void Query_encodes_labels_type_and_class()
    {
        byte[] q = DnsWireFormat.BuildQuery("www.example.test", DnsRecordType.A, 0x1234);
        Assert.Equal(0x12, q[0]);
        Assert.Equal(0x34, q[1]);
        Assert.Equal(1, DnsTestMessages.QueryType(q));
        Assert.Equal(3, q[12]);
        Assert.Throws<ArgumentException>(() => DnsWireFormat.BuildQuery("a..b", DnsRecordType.A, 1));
        Assert.Throws<ArgumentException>(() => DnsWireFormat.BuildQuery(new string('x', 64) + ".test", DnsRecordType.A, 1));
    }

    [Fact]
    public void Response_parses_compressed_answers_and_ttl()
    {
        byte[] q = DnsWireFormat.BuildQuery("cdn.example.test", DnsRecordType.A, 0x0BAD);
        byte[] resp = DnsTestMessages.Answer(q, IPAddress.Parse("203.0.113.9"), ttl: 300);
        var reply = DnsWireFormat.ParseResponse(resp, 0x0BAD);
        Assert.Equal(0, reply.Rcode);
        Assert.Single(reply.Answers);
        Assert.Equal("203.0.113.9", reply.Answers[0].Address.ToString());
        Assert.Equal(300u, reply.Answers[0].Ttl);
    }

    [Fact]
    public void Malformed_responses_are_rejected()
    {
        byte[] q = DnsWireFormat.BuildQuery("x.test", DnsRecordType.A, 7);
        byte[] good = DnsTestMessages.Answer(q, IPAddress.Loopback);

        Assert.Throws<FormatException>(() => DnsWireFormat.ParseResponse(new byte[5], 7));
        Assert.Throws<FormatException>(() => DnsWireFormat.ParseResponse(good, 8));

        byte[] notResponse = (byte[])good.Clone();
        notResponse[2] = 0x01; // QR = 0
        Assert.Throws<FormatException>(() => DnsWireFormat.ParseResponse(notResponse, 7));

        byte[] truncated = good[..(good.Length - 3)];
        Assert.Throws<FormatException>(() => DnsWireFormat.ParseResponse(truncated, 7));

        // Pointer loop: the question name points to itself.
        byte[] loop = new byte[14];
        loop[1] = 7;
        loop[2] = 0x81;
        loop[5] = 1;
        loop[12] = 0xC0;
        loop[13] = 0x0C;
        Assert.Throws<FormatException>(() => DnsWireFormat.ParseResponse(loop, 7));
    }

    [Fact]
    public void Non_address_records_are_skipped()
    {
        byte[] q = DnsWireFormat.BuildQuery("alias.test", DnsRecordType.A, 9);
        byte[] resp = DnsTestMessages.Answer(q, null);
        resp[7] = 1; // ANCOUNT = 1 with a dummy CNAME record
        var list = new List<byte>(resp) { 0xC0, 0x0C, 0, 5, 0, 1, 0, 0, 0, 60, 0, 2, 0xC0, 0x0C };
        var reply = DnsWireFormat.ParseResponse(list.ToArray(), 9);
        Assert.Empty(reply.Answers);
    }
}

public class DnsResolverTests : IDisposable
{
    private static readonly IPAddress Answer = IPAddress.Parse("192.0.2.10");
    private TestHttpServer? _server;

    public void Dispose() => _server?.Dispose();

    private TestHttpServer StartDoh(Func<byte[], byte[]> responder, int status = 200)
    {
        _server = new TestHttpServer(new Dictionary<string, Func<HttpCall, HttpReply>>
        {
            ["/dns-query"] = call =>
            {
                if (status != 200) return HttpReply.Text("boom", status);
                return new HttpReply(200, responder(call.Body), "application/dns-message");
            },
        });
        return _server;
    }

    private static byte[] AnswerForA(byte[] query, IPAddress? address = null, uint ttl = 120) =>
        DnsTestMessages.QueryType(query) == 1
            ? DnsTestMessages.Answer(query, address ?? Answer, ttl)
            : DnsTestMessages.Answer(query, null);

    [Fact]
    public async Task Doh_resolves_ipv4_and_ipv6_and_caches_answers()
    {
        var server = StartDoh(q => AnswerForA(q));
        using var resolver = new DnsResolver(new DohUpstream(new Uri(server.BaseUrl + "/dns-query")));

        var addresses = await resolver.ResolveAsync("Example.Test.");
        Assert.Equal(new[] { "192.0.2.10" }, addresses.Select(a => a.ToString()).ToArray());
        Assert.Equal(2, server.CallCount("/dns-query"));
        Assert.Equal("application/dns-message", server.Calls[0].Headers["Content-Type"]);

        Assert.Single(await resolver.ResolveAsync("example.test", DnsRecordType.A));
        Assert.Equal(2, server.CallCount("/dns-query"));
        Assert.True(resolver.CacheCount >= 2);

        resolver.ClearCache();
        Assert.Equal(0, resolver.CacheCount);
    }

    [Fact]
    public async Task Cache_entries_expire_after_their_ttl()
    {
        var server = StartDoh(q => AnswerForA(q, ttl: 120));
        using var resolver = new DnsResolver(new DohUpstream(new Uri(server.BaseUrl + "/dns-query")));
        var now = DateTimeOffset.Parse("2026-10-31T10:00:00Z");
        resolver.Clock = () => now;

        await resolver.ResolveAsync("ttl.test", DnsRecordType.A);
        await resolver.ResolveAsync("ttl.test", DnsRecordType.A);
        Assert.Equal(1, server.CallCount("/dns-query"));

        now = now.AddSeconds(121);
        await resolver.ResolveAsync("ttl.test", DnsRecordType.A);
        Assert.Equal(2, server.CallCount("/dns-query"));
    }

    [Fact]
    public async Task Ttl_is_clamped_to_configured_bounds()
    {
        var server = StartDoh(q => AnswerForA(q, ttl: 5));
        var options = new DnsResolverOptions { MinTtl = TimeSpan.FromSeconds(60), MaxTtl = TimeSpan.FromMinutes(10) };
        using var resolver = new DnsResolver(new DohUpstream(new Uri(server.BaseUrl + "/dns-query")), options);
        var now = DateTimeOffset.Parse("2026-10-31T10:00:00Z");
        resolver.Clock = () => now;

        await resolver.ResolveAsync("clamp.test", DnsRecordType.A);
        now = now.AddSeconds(59);
        await resolver.ResolveAsync("clamp.test", DnsRecordType.A);
        Assert.Equal(1, server.CallCount("/dns-query"));
        now = now.AddSeconds(2);
        await resolver.ResolveAsync("clamp.test", DnsRecordType.A);
        Assert.Equal(2, server.CallCount("/dns-query"));
    }

    [Fact]
    public async Task Nxdomain_returns_empty_and_servfail_throws()
    {
        var nx = StartDoh(q => DnsTestMessages.Answer(q, null, rcode: 3));
        using (var resolver = new DnsResolver(new DohUpstream(new Uri(nx.BaseUrl + "/dns-query"))))
        {
            Assert.Empty(await resolver.ResolveAsync("missing.test", DnsRecordType.A));
        }

        _server?.Dispose();
        var servfail = StartDoh(q => DnsTestMessages.Answer(q, null, rcode: 2));
        using var resolver2 = new DnsResolver(new DohUpstream(new Uri(servfail.BaseUrl + "/dns-query")));
        var ex = await Assert.ThrowsAsync<DnsResolutionException>(() => resolver2.ResolveAsync("broken.test", DnsRecordType.A));
        Assert.Equal(2, ex.Rcode);
    }

    [Fact]
    public async Task Transport_errors_and_id_mismatch_surface_to_caller()
    {
        var failing = StartDoh(_ => Array.Empty<byte>(), status: 500);
        using (var resolver = new DnsResolver(new DohUpstream(new Uri(failing.BaseUrl + "/dns-query"))))
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => resolver.ResolveAsync("down.test"));
        }

        _server?.Dispose();
        var wrongId = StartDoh(q => DnsTestMessages.Answer(q, Answer, overrideId: 1));
        using var resolver2 = new DnsResolver(new DohUpstream(new Uri(wrongId.BaseUrl + "/dns-query")));
        await Assert.ThrowsAsync<FormatException>(() => resolver2.ResolveAsync("id.test", DnsRecordType.A));
    }

    [Fact]
    public async Task Ip_literals_skip_the_network_and_bad_names_are_rejected()
    {
        var server = StartDoh(q => AnswerForA(q));
        using var resolver = new DnsResolver(new DohUpstream(new Uri(server.BaseUrl + "/dns-query")));

        var literal = await resolver.ResolveAsync("198.51.100.1");
        Assert.Equal("198.51.100.1", literal[0].ToString());
        Assert.Equal(0, server.CallCount("/dns-query"));

        await Assert.ThrowsAsync<ArgumentException>(() => resolver.ResolveAsync("   "));
        await Assert.ThrowsAsync<ArgumentException>(() => resolver.ResolveAsync("bad..name.test"));
        await Assert.ThrowsAsync<ArgumentException>(() => resolver.ResolveAsync(new string('a', 260) + ".test"));
        await Assert.ThrowsAsync<ArgumentException>(() => resolver.ResolveAsync("bad_name!.test"));
    }

    [Fact]
    public void Constructor_validates_upstream_and_options()
    {
        Assert.Throws<ArgumentException>(() => new DnsResolver(new DohUpstream(new Uri("http://dns.example.com/dns-query"))));
        Assert.Throws<ArgumentException>(() => new DnsResolver(new DotUpstream("198.51.100.53")));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DnsResolver(new DotUpstream("dns.example.com", 0)));
        Assert.Throws<ArgumentException>(() => new DnsResolver(new DotUpstream(" ")));
        Assert.Throws<ArgumentException>(() => new DnsResolver(new DohUpstream(new Uri("/relative", UriKind.Relative))));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DnsResolver(new DotUpstream("dns.example.com"), new DnsResolverOptions { MaxCacheEntries = 0 }));
        Assert.Throws<ArgumentException>(() => new DnsResolver(new DotUpstream("dns.example.com"),
            new DnsResolverOptions { MinTtl = TimeSpan.FromHours(2), MaxTtl = TimeSpan.FromHours(1) }));
        Assert.Throws<ArgumentNullException>(() => new DnsResolver(null!));

        using var ok = new DnsResolver(new DohUpstream(new Uri("https://dns.example.com/dns-query")));
        ok.Dispose();
        ok.Dispose();
        Assert.Throws<ObjectDisposedException>(() => ok.ResolveAsync("x.test", DnsRecordType.A).GetAwaiter().GetResult());
    }

    [Fact]
    public async Task Max_cache_entries_are_bounded()
    {
        var server = StartDoh(q => AnswerForA(q));
        var options = new DnsResolverOptions { MaxCacheEntries = 3 };
        using var resolver = new DnsResolver(new DohUpstream(new Uri(server.BaseUrl + "/dns-query")), options);
        for (int i = 0; i < 6; i++)
            await resolver.ResolveAsync($"host{i}.test", DnsRecordType.A);
        Assert.True(resolver.CacheCount <= 3);
    }

    [Fact]
    public async Task Dot_resolves_over_tls_with_length_prefix()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var created = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var cert = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx, "pw"), "pw", X509KeyStorageFlags.Exportable);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var tls = new SslStream(client.GetStream(), false);
            await tls.AuthenticateAsServerAsync(cert);
            var lenBuf = new byte[2];
            await tls.ReadExactlyAsync(lenBuf);
            int len = BinaryPrimitives.ReadUInt16BigEndian(lenBuf);
            var query = new byte[len];
            await tls.ReadExactlyAsync(query);
            byte[] answer = AnswerForA(query, IPAddress.Parse("198.51.100.7"));
            var framed = new byte[2 + answer.Length];
            BinaryPrimitives.WriteUInt16BigEndian(framed, (ushort)answer.Length);
            answer.CopyTo(framed, 2);
            await tls.WriteAsync(framed);
            await tls.FlushAsync();
        });

        try
        {
            var options = new DnsResolverOptions { CertificateValidationCallback = (_, _, _, _) => true };
            using var resolver = new DnsResolver(new DotUpstream("127.0.0.1", port, "localhost"), options);
            var result = await resolver.ResolveAsync("tls.example.test", DnsRecordType.A);
            Assert.Equal("198.51.100.7", result[0].ToString());
            await serverTask;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Dot_fails_when_certificate_name_does_not_match()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=wrong.example", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var created = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var cert = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx, "pw"), "pw", X509KeyStorageFlags.Exportable);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync();
                await using var tls = new SslStream(client.GetStream(), false);
                await tls.AuthenticateAsServerAsync(cert);
            }
            catch (Exception)
            {
                // The client rejected the certificate; expected.
            }
        });

        try
        {
            using var resolver = new DnsResolver(new DotUpstream("127.0.0.1", port, "localhost"),
                new DnsResolverOptions { Timeout = TimeSpan.FromSeconds(5) });
            await Assert.ThrowsAnyAsync<Exception>(() => resolver.ResolveAsync("x.example.test", DnsRecordType.A));
        }
        finally
        {
            listener.Stop();
            await serverTask;
        }
    }

    [Fact]
    public async Task Combined_resolve_returns_partial_results_when_one_family_fails()
    {
        var server = StartDoh(q => DnsTestMessages.QueryType(q) == 1
            ? DnsTestMessages.Answer(q, IPAddress.Parse("192.0.2.44"))
            : DnsTestMessages.Answer(q, null, rcode: 2));
        using var resolver = new DnsResolver(new DohUpstream(new Uri(server.BaseUrl + "/dns-query")));
        var result = await resolver.ResolveAsync("partial.test");
        Assert.Equal("192.0.2.44", result[0].ToString());
    }
}

public class ProxyConfigTests
{
    [Fact]
    public void Http_proxy_is_returned_for_external_hosts_and_bypassed_for_loopback()
    {
        var proxy = ProxyConfig.Http("proxy.local", 3128);
        Assert.Equal(ProxyMode.Http, proxy.Mode);
        Assert.Equal(new Uri("http://proxy.local:3128/"), proxy.GetProxy(new Uri("https://site.test/")));
        Assert.Equal(new Uri("http://localhost/"), proxy.GetProxy(new Uri("http://localhost/")));
        Assert.True(proxy.IsBypassed(new Uri("http://127.0.0.1/")));
        Assert.False(proxy.IsBypassed(new Uri("https://site.test/")));
        Assert.Equal("Http proxy.local:3128", proxy.ToString());

        var custom = ProxyConfig.Http("p", 1, bypass: new[] { "intranet.test" });
        Assert.True(custom.IsBypassed(new Uri("http://intranet.test/")));
    }

    [Fact]
    public void Tor_and_socks_presets_and_direct_mode()
    {
        var tor = ProxyConfig.Tor();
        Assert.Equal(ProxyMode.Socks5, tor.Mode);
        Assert.Equal("127.0.0.1", tor.Host);
        Assert.Equal(9050, tor.Port);
        Assert.Equal(9150, ProxyConfig.Tor(9150).Port);
        Assert.Equal("Socks5 127.0.0.1:9050", tor.ToString());

        var direct = ProxyConfig.Direct();
        Assert.Equal(ProxyMode.Direct, direct.Mode);
        Assert.Equal("Direct", direct.ToString());
        Assert.Equal(new Uri("https://site.test/"), direct.GetProxy(new Uri("https://site.test/")));
        Assert.True(direct.IsBypassed(new Uri("https://site.test/")));
    }

    [Fact]
    public void Handler_configuration_follows_mode_and_disables_cookies()
    {
        using var http = ProxyConfig.Http("proxy.local", 3128).CreateSocketsHandler();
        Assert.True(http.UseProxy);
        Assert.NotNull(http.Proxy);
        Assert.False(http.UseCookies);

        using var socks = ProxyConfig.Tor().CreateSocketsHandler();
        Assert.False(socks.UseProxy);
        Assert.NotNull(socks.ConnectCallback);

        using var direct = ProxyConfig.Direct().CreateSocketsHandler();
        Assert.False(direct.UseProxy);
    }

    [Fact]
    public void Invalid_endpoints_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => ProxyConfig.Http(" ", 3128));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProxyConfig.Http("p", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProxyConfig.Socks5("p", 70000));
    }

    [Fact]
    public async Task Socks5_connect_with_domain_target_and_relay_to_local_server()
    {
        using var target = new TestHttpServer(new Dictionary<string, Func<HttpCall, HttpReply>>
        {
            ["/hello"] = _ => HttpReply.Text("hello-through-socks"),
        });

        using var socks = new FakeSocksServer(targetPort: target.Port, requiredUser: null, requiredPassword: null, replyCode: 0);
        var proxy = ProxyConfig.Socks5("127.0.0.1", socks.Port);
        using var handler = proxy.CreateSocketsHandler();
        using var client = new HttpClient(handler);

        string body = await client.GetStringAsync("http://127.0.0.1:" + target.Port + "/hello");
        Assert.Equal("hello-through-socks", body);
        Assert.Equal("127.0.0.1", socks.LastRequestedHost);
        Assert.Equal(target.Port, socks.LastRequestedPort);
        Assert.Equal(1, socks.LastAtyp);
    }

    [Fact]
    public async Task Socks5_with_username_password_authentication()
    {
        using var socks = new FakeSocksServer(targetPort: 1, requiredUser: "tor", requiredPassword: "secret", replyCode: 0);
        var stream = await Socks5Client.ConnectAsync("127.0.0.1", socks.Port, "example.test", 443, "tor", "secret", CancellationToken.None);
        Assert.True(stream.CanWrite);
        await stream.DisposeAsync();
        Assert.Equal("example.test", socks.LastRequestedHost);
        Assert.Equal(3, socks.LastAtyp);
        Assert.Equal(443, socks.LastRequestedPort);
    }

    [Fact]
    public async Task Socks5_failures_are_reported()
    {
        using var refused = new FakeSocksServer(targetPort: 1, requiredUser: null, requiredPassword: null, replyCode: 5);
        await Assert.ThrowsAsync<IOException>(() => Socks5Client.ConnectAsync("127.0.0.1", refused.Port, "x.test", 80, null, null, CancellationToken.None));

        using var wrongAuth = new FakeSocksServer(targetPort: 1, requiredUser: "tor", requiredPassword: "secret", replyCode: 0);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Socks5Client.ConnectAsync("127.0.0.1", wrongAuth.Port, "x.test", 80, "tor", "bad", CancellationToken.None));

        // The fake server accepts only one connection, so a second attempt needs a fresh instance.
        using var noAuthOffered = new FakeSocksServer(targetPort: 1, requiredUser: "tor", requiredPassword: "secret", replyCode: 0);
        await Assert.ThrowsAsync<IOException>(() => Socks5Client.ConnectAsync("127.0.0.1", noAuthOffered.Port, "x.test", 80, null, null, CancellationToken.None));

        using var noAcceptable = new FakeSocksServer(targetPort: 1, requiredUser: null, requiredPassword: null, replyCode: 0, rejectAllMethods: true);
        await Assert.ThrowsAsync<IOException>(() => Socks5Client.ConnectAsync("127.0.0.1", noAcceptable.Port, "x.test", 80, null, null, CancellationToken.None));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Socks5Client.ConnectAsync("127.0.0.1", 1, "x.test", 0, null, null, CancellationToken.None));
    }

    [Fact]
    public async Task Socks5_ipv6_target_is_encoded_as_atyp_4()
    {
        using var socks = new FakeSocksServer(targetPort: 1, requiredUser: null, requiredPassword: null, replyCode: 0);
        var stream = await Socks5Client.ConnectAsync("127.0.0.1", socks.Port, "::1", 8080, null, null, CancellationToken.None);
        await stream.DisposeAsync();
        Assert.Equal(4, socks.LastAtyp);
        Assert.Equal(8080, socks.LastRequestedPort);
    }

    /// <summary>Minimal SOCKS5 server that validates the protocol steps and can connect to the real target.</summary>
    private sealed class FakeSocksServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly int _targetPort;
        private readonly string? _user;
        private readonly string? _password;
        private readonly byte _replyCode;
        private readonly bool _rejectAll;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public FakeSocksServer(int targetPort, string? requiredUser, string? requiredPassword, byte replyCode, bool rejectAllMethods = false)
        {
            _targetPort = targetPort;
            _user = requiredUser;
            _password = requiredPassword;
            _replyCode = replyCode;
            _rejectAll = rejectAllMethods;
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Task.Run(RunAsync);
        }

        public int Port { get; }
        public string? LastRequestedHost { get; private set; }
        public int LastRequestedPort { get; private set; }
        public int LastAtyp { get; private set; }

        private async Task RunAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                using var stream = client.GetStream();

                var greet = await Read(stream, 2);
                if (greet[0] != 5) return;
                var methods = await Read(stream, greet[1]);

                if (_rejectAll)
                {
                    await stream.WriteAsync(new byte[] { 5, 0xFF });
                    return;
                }

                byte chosen = _user is not null
                    ? (methods.Contains((byte)2) ? (byte)2 : (byte)0xFF)
                    : (methods.Contains((byte)0) ? (byte)0 : (byte)0xFF);
                await stream.WriteAsync(new[] { (byte)5, chosen });
                if (chosen == 0xFF) return;

                if (chosen == 2)
                {
                    var v = await Read(stream, 2);
                    var user = Encoding.UTF8.GetString(await Read(stream, v[1]));
                    var plen = await Read(stream, 1);
                    var pass = Encoding.UTF8.GetString(await Read(stream, plen[0]));
                    bool ok = user == _user && pass == _password;
                    await stream.WriteAsync(new[] { (byte)1, (byte)(ok ? 0 : 1) });
                    if (!ok) return;
                }

                var head = await Read(stream, 4);
                LastAtyp = head[3];
                string host = head[3] switch
                {
                    1 => new IPAddress(await Read(stream, 4)).ToString(),
                    3 => Encoding.ASCII.GetString(await Read(stream, (await Read(stream, 1))[0])),
                    4 => new IPAddress(await Read(stream, 16)).ToString(),
                    _ => "",
                };
                var portBytes = await Read(stream, 2);
                LastRequestedHost = host;
                LastRequestedPort = (portBytes[0] << 8) | portBytes[1];

                await stream.WriteAsync(new byte[] { 5, _replyCode, 0, 1, 0, 0, 0, 0, 0, 0 });
                if (_replyCode != 0) return;

                using var upstream = new TcpClient();
                await upstream.ConnectAsync(IPAddress.Loopback, _targetPort);
                var up = upstream.GetStream();
                var pumpUp = Pump(stream, up);
                var pumpDown = Pump(up, stream);
                await Task.WhenAny(pumpUp, pumpDown);
            }
            catch (Exception)
            {
                // Connection closed by the client.
            }
        }

        private static async Task Pump(Stream from, Stream to)
        {
            var buffer = new byte[8192];
            try
            {
                int n;
                while ((n = await from.ReadAsync(buffer)) > 0)
                {
                    await to.WriteAsync(buffer.AsMemory(0, n));
                    await to.FlushAsync();
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }

        private static async Task<byte[]> Read(Stream s, int count)
        {
            var buf = new byte[count];
            await s.ReadExactlyAsync(buf);
            return buf;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        }
    }
}
