using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WinIDM.Torrent.Tests.Support;

/// <summary>
/// Simple HTTP tracker (BEP 3 / BEP 23 compact) that listens on 127.0.0.1 only.
/// Stores peers by info hash and returns the other peers in the same swarm.
/// It does not contact any external server.
/// </summary>
internal sealed class LocalHttpTracker : IAsyncDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _sync = new();
    private readonly Dictionary<string, Dictionary<string, Registration>> _swarms = new(StringComparer.Ordinal);
    private readonly Task _acceptLoop;
    private int _announces;

    public LocalHttpTracker()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    /// <summary>Announce URL to put in a .torrent or magnet link.</summary>
    public string AnnounceUrl => $"http://127.0.0.1:{Port}/announce";

    /// <summary>Total announce requests received.</summary>
    public int Announces => Volatile.Read(ref _announces);

    /// <summary>Number of registered peers for an info hash (lowercase hex).</summary>
    public int PeerCount(string infoHashHex)
    {
        lock (_sync)
        {
            return _swarms.TryGetValue(infoHashHex.ToLowerInvariant(), out var swarm) ? swarm.Count : 0;
        }
    }

    /// <summary>Registered peers (address:port) for an info hash.</summary>
    public IReadOnlyList<string> Peers(string infoHashHex)
    {
        lock (_sync)
        {
            return _swarms.TryGetValue(infoHashHex.ToLowerInvariant(), out var swarm)
                ? swarm.Keys.ToList()
                : new List<string>();
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (Exception) when (_cts.IsCancellationRequested || _listener.Server is null)
            {
                return;
            }
            catch (SocketException)
            {
                return;
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
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                timeout.CancelAfter(RequestTimeout);
                var stream = client.GetStream();

                var head = await ReadHeadAsync(stream, timeout.Token).ConfigureAwait(false);
                if (head is null) return;

                var requestLine = head.Split("\r\n", 2)[0];
                var parts = requestLine.Split(' ');
                if (parts.Length < 2 || parts[0] != "GET")
                {
                    await WriteAsync(stream, 405, "text/plain", "Method not supported"u8.ToArray(), timeout.Token).ConfigureAwait(false);
                    return;
                }

                var target = parts[1];
                var q = target.IndexOf('?');
                var path = q < 0 ? target : target[..q];
                if (path != "/announce")
                {
                    await WriteAsync(stream, 404, "text/plain", "Not found"u8.ToArray(), timeout.Token).ConfigureAwait(false);
                    return;
                }

                var query = ParseQuery(q < 0 ? "" : target[(q + 1)..]);
                if (!query.TryGetValue("info_hash", out var infoHashBytes) || infoHashBytes.Length != 20)
                {
                    await WriteAsync(stream, 400, "text/plain", "invalid info_hash"u8.ToArray(), timeout.Token).ConfigureAwait(false);
                    return;
                }
                if (!query.TryGetValue("port", out var portBytes) || !int.TryParse(Encoding.ASCII.GetString(portBytes), out var port))
                {
                    await WriteAsync(stream, 400, "text/plain", "invalid port"u8.ToArray(), timeout.Token).ConfigureAwait(false);
                    return;
                }

                var ip = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
                if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
                var left = query.TryGetValue("left", out var leftBytes) && long.TryParse(Encoding.ASCII.GetString(leftBytes), out var l) ? l : 0;
                var evt = query.TryGetValue("event", out var evtBytes) ? Encoding.ASCII.GetString(evtBytes) : "";

                var infoHash = Convert.ToHexString(infoHashBytes).ToLowerInvariant();
                var key = $"{ip}:{port}";
                Interlocked.Increment(ref _announces);

                List<string> others;
                int seeds, leechers;
                lock (_sync)
                {
                    if (!_swarms.TryGetValue(infoHash, out var swarm))
                    {
                        swarm = new Dictionary<string, Registration>(StringComparer.Ordinal);
                        _swarms[infoHash] = swarm;
                    }

                    if (evt == "stopped")
                    {
                        swarm.Remove(key);
                    }
                    else
                    {
                        swarm[key] = new Registration(ip, port, left == 0);
                    }

                    others = swarm.Keys.Where(k => k != key).ToList();
                    seeds = swarm.Values.Count(r => r.Seed);
                    leechers = swarm.Values.Count(r => !r.Seed);
                }

                var compact = new MemoryStream();
                foreach (var other in others.Take(50))
                {
                    var (addr, otherPort) = SplitKey(other);
                    compact.Write(addr.GetAddressBytes());
                    compact.WriteByte((byte)(otherPort >> 8));
                    compact.WriteByte((byte)(otherPort & 0xFF));
                }

                var body = Bencode.Encode(new SortedDictionary<string, object>(StringComparer.Ordinal)
                {
                    ["complete"] = (long)seeds,
                    ["incomplete"] = (long)leechers,
                    ["interval"] = 2L,
                    ["min interval"] = 1L,
                    ["peers"] = compact.ToArray(),
                });

                await WriteAsync(stream, 200, "text/plain", body, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception) when (!_cts.IsCancellationRequested)
            {
                // Malformed request or dropped connection: the tracker must not go down.
            }
        }
    }

    private static async Task<string?> ReadHeadAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var used = 0;
        while (used < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(used), ct).ConfigureAwait(false);
            if (read == 0) break;
            used += read;
            var text = Encoding.ASCII.GetString(buffer, 0, used);
            var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end >= 0) return text[..end];
        }
        return null;
    }

    private static async Task WriteAsync(NetworkStream stream, int status, string contentType, byte[] body, CancellationToken ct)
    {
        var reason = status switch { 200 => "OK", 400 => "Bad Request", 404 => "Not Found", _ => "Method Not Allowed" };
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Parse a query string; values are decoded to bytes (percent codes produce raw bytes).</summary>
    private static Dictionary<string, byte[]> ParseQuery(string query)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var name = eq < 0 ? pair : pair[..eq];
            var value = eq < 0 ? "" : pair[(eq + 1)..];
            result[name] = DecodeBytes(value);
        }
        return result;
    }

    private static byte[] DecodeBytes(string s)
    {
        var bytes = new List<byte>(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '%' && i + 2 < s.Length && IsHex(s[i + 1]) && IsHex(s[i + 2]))
            {
                bytes.Add(Convert.ToByte(s.Substring(i + 1, 2), 16));
                i += 2;
            }
            else if (c == '+')
            {
                bytes.Add((byte)' ');
            }
            else
            {
                bytes.Add((byte)c);
            }
        }
        return bytes.ToArray();
    }

    private static bool IsHex(char c) => Uri.IsHexDigit(c);

    private static (IPAddress Address, int Port) SplitKey(string key)
    {
        var idx = key.LastIndexOf(':');
        return (IPAddress.Parse(key[..idx]), int.Parse(key[(idx + 1)..]));
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch (Exception) { }
        try { await _acceptLoop.ConfigureAwait(false); } catch (Exception) { }
        _cts.Dispose();
    }

    private sealed record Registration(IPAddress Address, int Port, bool Seed);
}
