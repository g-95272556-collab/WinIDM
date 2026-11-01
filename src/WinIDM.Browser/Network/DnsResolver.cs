using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace WinIDM.Browser;

/// <summary>Base for a DNS upstream (DoH or DoT).</summary>
public abstract record DnsUpstream;

/// <summary>DNS upstream: DNS-over-HTTPS (RFC 8484).</summary>
public sealed record DohUpstream(Uri Endpoint) : DnsUpstream;

/// <summary>DNS upstream: DNS-over-TLS (RFC 7858). Default port 853.</summary>
public sealed record DotUpstream(string Host, int Port = 853, string? ServerName = null) : DnsUpstream;

/// <summary>DNS resolver parameters.</summary>
public sealed class DnsResolverOptions
{
    public TimeSpan MinTtl { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan MaxTtl { get; set; } = TimeSpan.FromDays(1);
    public int MaxCacheEntries { get; set; } = 5000;
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>DoT certificate validation. Null means the default Windows validation (chain and name).</summary>
    public RemoteCertificateValidationCallback? CertificateValidationCallback { get; set; }
}

/// <summary>Thrown when the DNS server returns an error RCODE (other than NXDOMAIN).</summary>
public sealed class DnsResolutionException : Exception
{
    public DnsResolutionException(int rcode, string name)
        : base($"DNS resolution failed for '{name}' (RCODE {rcode}).")
    {
        Rcode = rcode;
        Name = name;
    }

    public int Rcode { get; }
    public string Name { get; }
}

/// <summary>
/// Own DNS resolver (replacement for IDMVpnService). Supports DoH and DoT, with a TTL cache.
/// It works for this application only; it does not change the system DNS.
/// DNS queries are sent in wire format, not through the Windows name resolver.
/// </summary>
public sealed class DnsResolver : IDisposable
{
    private static readonly Regex HostLabel = new(@"^[a-z0-9_]([a-z0-9_-]{0,61}[a-z0-9_])?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly DnsUpstream _upstream;
    private readonly DnsResolverOptions _options;
    private readonly HttpClient? _http;
    private readonly bool _ownsHttp;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>Clock for tests (TTL accuracy).</summary>
    internal Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    public DnsResolver(DnsUpstream upstream, DnsResolverOptions? options = null, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(upstream);
        _upstream = upstream;
        _options = options ?? new DnsResolverOptions();
        if (_options.MaxCacheEntries < 1) throw new ArgumentOutOfRangeException(nameof(options), "MaxCacheEntries must be >= 1.");
        if (_options.MinTtl > _options.MaxTtl) throw new ArgumentException("MinTtl exceeds MaxTtl.", nameof(options));

        switch (upstream)
        {
            case DohUpstream doh:
                if (doh.Endpoint is null || !doh.Endpoint.IsAbsoluteUri)
                    throw new ArgumentException("The DoH endpoint must be an absolute URL.", nameof(upstream));
                bool loopback = IPAddress.TryParse(doh.Endpoint.Host, out var ip) && IPAddress.IsLoopback(ip);
                if (doh.Endpoint.Scheme != Uri.UriSchemeHttps && !(loopback && doh.Endpoint.Scheme == Uri.UriSchemeHttp))
                    throw new ArgumentException("The DoH endpoint must use HTTPS.", nameof(upstream));
                _ownsHttp = httpClient is null;
                _http = httpClient ?? new HttpClient(new SocketsHttpHandler { UseCookies = false })
                {
                    Timeout = _options.Timeout,
                };
                break;

            case DotUpstream dot:
                if (string.IsNullOrWhiteSpace(dot.Host)) throw new ArgumentException("A DoT host is required.", nameof(upstream));
                if (dot.Port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(upstream), "Invalid DoT port.");
                if (dot.ServerName is null && IPAddress.TryParse(dot.Host, out _))
                    throw new ArgumentException("ServerName is required for DoT when the host is an IP address.", nameof(upstream));
                break;

            default:
                throw new ArgumentException("Unsupported upstream type.", nameof(upstream));
        }
    }

    public int CacheCount => _cache.Count;

    public void ClearCache() => _cache.Clear();

    /// <summary>Resolve IPv4 and IPv6 addresses for a host.</summary>
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct = default)
    {
        if (IPAddress.TryParse(host, out var literal)) return new[] { literal };

        var results = new List<IPAddress>();
        Exception? firstError = null;
        foreach (var type in new[] { DnsRecordType.A, DnsRecordType.AAAA })
        {
            try
            {
                results.AddRange(await ResolveAsync(host, type, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is DnsResolutionException or HttpRequestException or IOException or
                                           SocketException or AuthenticationException or FormatException or OperationCanceledException)
            {
                firstError ??= ex;
            }
        }

        if (results.Count == 0 && firstError is not null)
        {
            if (firstError is OperationCanceledException && ct.IsCancellationRequested) throw firstError;
            throw firstError is OperationCanceledException
                ? new TimeoutException("DNS request timed out.")
                : firstError;
        }
        return results.Distinct().ToList();
    }

    /// <summary>Resolve a single record type (A or AAAA) with caching.</summary>
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, DnsRecordType type, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string name = NormalizeName(host);
        string key = ((ushort)type).ToString(CultureInfo.InvariantCulture) + "|" + name;

        DateTimeOffset now = Clock();
        if (_cache.TryGetValue(key, out var hit) && hit.Expires > now) return hit.Addresses;

        ushort id = (ushort)RandomNumberGenerator.GetInt32(0, 65536);
        byte[] query = DnsWireFormat.BuildQuery(name, type, id);
        byte[] response = await ExchangeAsync(query, ct).ConfigureAwait(false);
        DnsReply reply = DnsWireFormat.ParseResponse(response, id);

        if (reply.Rcode == 3)
        {
            // NXDOMAIN: negative cache for MinTtl.
            Store(key, Array.Empty<IPAddress>(), _options.MinTtl, now);
            return Array.Empty<IPAddress>();
        }
        if (reply.Rcode != 0) throw new DnsResolutionException(reply.Rcode, name);

        var matching = reply.Answers
            .Where(a => type == DnsRecordType.A
                ? a.Address.AddressFamily == AddressFamily.InterNetwork
                : a.Address.AddressFamily == AddressFamily.InterNetworkV6)
            .ToList();

        TimeSpan ttl = matching.Count == 0
            ? _options.MinTtl
            : Clamp(TimeSpan.FromSeconds(matching.Min(a => (double)a.Ttl)));

        var addresses = matching.Select(a => a.Address).Distinct().ToArray();
        Store(key, addresses, ttl, now);
        return addresses;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsHttp) _http?.Dispose();
    }

    // ---------------------------------------------------------------------

    private TimeSpan Clamp(TimeSpan ttl)
    {
        if (ttl < _options.MinTtl) return _options.MinTtl;
        if (ttl > _options.MaxTtl) return _options.MaxTtl;
        return ttl;
    }

    private void Store(string key, IReadOnlyList<IPAddress> addresses, TimeSpan ttl, DateTimeOffset now)
    {
        if (_cache.Count >= _options.MaxCacheEntries) Trim(now);
        _cache[key] = new CacheEntry(addresses, now + ttl);
    }

    private void Trim(DateTimeOffset now)
    {
        foreach (var kv in _cache)
        {
            if (kv.Value.Expires <= now) _cache.TryRemove(kv.Key, out _);
        }
        // If it is still full, clear the cache. A simple approach that adds no structural complexity.
        if (_cache.Count >= _options.MaxCacheEntries) _cache.Clear();
    }

    private static string NormalizeName(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("A host name is required.", nameof(host));
        string ascii;
        try
        {
            ascii = new System.Globalization.IdnMapping().GetAscii(host.Trim().TrimEnd('.'));
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException("Invalid host name.", nameof(host), ex);
        }
        ascii = ascii.ToLowerInvariant();
        if (ascii.Length == 0 || ascii.Length > 253) throw new ArgumentException("Invalid host name length.", nameof(host));
        foreach (string label in ascii.Split('.'))
        {
            if (!HostLabel.IsMatch(label)) throw new ArgumentException("Invalid host name label.", nameof(host));
        }
        return ascii;
    }

    private async Task<byte[]> ExchangeAsync(byte[] query, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_options.Timeout);
        return _upstream switch
        {
            DohUpstream doh => await ExchangeDohAsync(doh, query, cts.Token).ConfigureAwait(false),
            DotUpstream dot => await ExchangeDotAsync(dot, query, cts.Token).ConfigureAwait(false),
            _ => throw new InvalidOperationException("Unsupported upstream."),
        };
    }

    private async Task<byte[]> ExchangeDohAsync(DohUpstream doh, byte[] query, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, doh.Endpoint)
        {
            Content = new ByteArrayContent(query),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-message"));

        using var response = await _http!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null && !string.Equals(mediaType, "application/dns-message", StringComparison.OrdinalIgnoreCase))
            throw new HttpRequestException("Invalid DoH response type.");

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await ReadBoundedAsync(stream, ct).ConfigureAwait(false);
    }

    private async Task<byte[]> ExchangeDotAsync(DotUpstream dot, byte[] query, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(dot.Host, dot.Port, ct).ConfigureAwait(false);

        await using var tls = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false, _options.CertificateValidationCallback);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = dot.ServerName ?? dot.Host,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        }, ct).ConfigureAwait(false);

        var framed = new byte[2 + query.Length];
        BinaryPrimitives.WriteUInt16BigEndian(framed, (ushort)query.Length);
        query.CopyTo(framed, 2);
        await tls.WriteAsync(framed, ct).ConfigureAwait(false);
        await tls.FlushAsync(ct).ConfigureAwait(false);

        var lengthBuffer = new byte[2];
        await ReadExactAsync(tls, lengthBuffer, ct).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadUInt16BigEndian(lengthBuffer);
        var body = new byte[length];
        await ReadExactAsync(tls, body, ct).ConfigureAwait(false);
        return body;
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + read > 65535) throw new HttpRequestException("DNS response is too large.");
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
            if (n == 0) throw new IOException("The DoT connection was closed early.");
            offset += n;
        }
    }

    private sealed record CacheEntry(IReadOnlyList<IPAddress> Addresses, DateTimeOffset Expires);
}
