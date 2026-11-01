using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WinIDM.Engine.Tests.Support;

/// <summary>A request received by the test server.</summary>
public sealed class RecordedRequest
{
    public RecordedRequest(string method, string target, IReadOnlyDictionary<string, string> headers)
    {
        Method = method;
        Target = target;
        Headers = headers;
    }

    public string Method { get; }

    public string Target { get; }

    public IReadOnlyDictionary<string, string> Headers { get; }

    public string? Get(string name) => Headers.TryGetValue(name, out var value) ? value : null;

    /// <summary>Parse a Range header "bytes=a-b" or "bytes=a-"; null if absent.</summary>
    public (long From, long? To)? Range
    {
        get
        {
            var raw = Get("Range");
            if (raw is null || !raw.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return null;

            var parts = raw["bytes=".Length..].Split('-');
            var from = long.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
            long? to = parts.Length > 1 && parts[1].Length > 0
                ? long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)
                : null;
            return (from, to);
        }
    }
}

/// <summary>A response that the test server will send.</summary>
public sealed class ServerResponse
{
    public int Status { get; init; } = 200;

    public string Reason { get; init; } = "OK";

    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public byte[] Body { get; init; } = [];

    /// <summary>If set, the connection is dropped after this many body bytes (incomplete response).</summary>
    public int? AbortAfterBytes { get; init; }

    /// <summary>Delay between 16 KiB blocks, to simulate a slow download.</summary>
    public int ChunkDelayMs { get; init; }
}

/// <summary>
/// Minimal HTTP/1.1 server on 127.0.0.1 (TcpListener, ephemeral port). Local only; no internet.
/// Each connection is closed after one response (Connection: close).
/// </summary>
public sealed class LocalHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;

    public LocalHttpServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>Request handler; defaults to 404.</summary>
    public Func<RecordedRequest, ServerResponse> Handler { get; set; } =
        _ => new ServerResponse { Status = 404, Reason = "Not Found" };

    public int Port { get; }

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public IReadOnlyList<RecordedRequest> Requests => _requests.ToArray();

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
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
                var head = await ReadHeadAsync(stream);
                if (head is null) return;

                var lines = head.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                var requestLine = lines[0].Split(' ');
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1))
                {
                    var colon = line.IndexOf(':');
                    if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                }

                var request = new RecordedRequest(requestLine[0], requestLine[1], headers);
                _requests.Enqueue(request);

                ServerResponse response;
                try
                {
                    response = Handler(request);
                }
                catch (Exception)
                {
                    response = new ServerResponse { Status = 500, Reason = "Internal Server Error" };
                }

                await WriteResponseAsync(stream, response);
            }
            catch (IOException)
            {
                // The client dropped the connection; normal in resume tests.
            }
            catch (SocketException)
            {
                // Same as above.
            }
        }
    }

    private static async Task<string?> ReadHeadAsync(NetworkStream stream)
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

            if (count > 64 * 1024) return null;
        }
    }

    private static async Task WriteResponseAsync(NetworkStream stream, ServerResponse response)
    {
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(response.Status).Append(' ').Append(response.Reason).Append("\r\n");
        foreach (var (name, value) in response.Headers)
        {
            sb.Append(name).Append(": ").Append(value).Append("\r\n");
        }

        sb.Append("Content-Length: ").Append(response.Body.Length).Append("\r\n");
        sb.Append("Connection: close\r\n\r\n");
        var head = Encoding.ASCII.GetBytes(sb.ToString());
        await stream.WriteAsync(head);

        var limit = response.AbortAfterBytes ?? response.Body.Length;
        var offset = 0;
        while (offset < limit && offset < response.Body.Length)
        {
            var chunk = Math.Min(16 * 1024, Math.Min(limit, response.Body.Length) - offset);
            await stream.WriteAsync(response.Body.AsMemory(offset, chunk));
            await stream.FlushAsync();
            offset += chunk;

            if (response.ChunkDelayMs > 0) await Task.Delay(response.ChunkDelayMs);
        }

        // If truncated, disposal closes the connection before the body is complete.
    }
}
