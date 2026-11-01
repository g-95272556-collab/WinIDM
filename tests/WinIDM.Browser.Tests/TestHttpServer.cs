using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WinIDM.Browser.Tests;

/// <summary>A request received by the test server.</summary>
public sealed record HttpCall(string Method, string Path, IReadOnlyDictionary<string, string> Headers, byte[] Body);

/// <summary>A response sent by the test server.</summary>
public sealed record HttpReply(int Status, byte[] Body, string ContentType = "text/plain; charset=utf-8",
    IReadOnlyDictionary<string, string>? Headers = null)
{
    public static HttpReply Text(string text, int status = 200, string contentType = "text/plain; charset=utf-8") =>
        new(status, Encoding.UTF8.GetBytes(text), contentType);

    public static HttpReply Bytes(byte[] data, string contentType = "application/octet-stream") =>
        new(200, data, contentType);

    public static HttpReply Redirect(string location) =>
        new(302, Array.Empty<byte>(), "text/plain", new Dictionary<string, string> { ["Location"] = location });
}

/// <summary>
/// Minimal HTTP/1.1 server on 127.0.0.1 for tests. Uses TcpListener so it does not
/// need a URL ACL like HttpListener. Does not access the internet.
/// </summary>
public sealed class TestHttpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly IReadOnlyDictionary<string, Func<HttpCall, HttpReply>> _routes;
    private readonly ConcurrentQueue<HttpCall> _calls = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;

    public TestHttpServer(IDictionary<string, Func<HttpCall, HttpReply>> routes)
    {
        _routes = new Dictionary<string, Func<HttpCall, HttpReply>>(routes, StringComparer.Ordinal);
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public IReadOnlyList<HttpCall> Calls => _calls.ToArray();

    public int CallCount(string path) => _calls.Count(c => c.Path == path);

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
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
                string head = await ReadHeadAsync(stream).ConfigureAwait(false);
                if (head.Length == 0) return;

                string[] lines = head.Split("\r\n", StringSplitOptions.None);
                string[] requestLine = lines[0].Split(' ');
                string method = requestLine[0];
                string target = requestLine[1];
                int q = target.IndexOf('?');
                string path = q < 0 ? target : target[..q];

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in lines.Skip(1))
                {
                    int colon = line.IndexOf(':');
                    if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                }

                byte[] body = Array.Empty<byte>();
                if (headers.TryGetValue("Content-Length", out var lengthText) && int.TryParse(lengthText, out int length) && length > 0)
                {
                    body = new byte[length];
                    int read = 0;
                    while (read < length)
                    {
                        int n = await stream.ReadAsync(body.AsMemory(read), _cts.Token).ConfigureAwait(false);
                        if (n == 0) break;
                        read += n;
                    }
                }

                var call = new HttpCall(method, path, headers, body);
                _calls.Enqueue(call);

                HttpReply reply = _routes.TryGetValue(path, out var handler)
                    ? handler(call)
                    : HttpReply.Text("not found", 404);

                await WriteReplyAsync(stream, reply, method == "HEAD").ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Connection closed by the client. Not critical to the test.
            }
        }
    }

    private static async Task<string> ReadHeadAsync(Stream stream)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            int n = await stream.ReadAsync(one).ConfigureAwait(false);
            if (n == 0) break;
            bytes.Add(one[0]);
            int count = bytes.Count;
            if (count >= 4 && bytes[count - 4] == '\r' && bytes[count - 3] == '\n' &&
                bytes[count - 2] == '\r' && bytes[count - 1] == '\n')
            {
                break;
            }
            if (count > 65536) break;
        }
        string text = Encoding.ASCII.GetString(bytes.ToArray());
        return text.EndsWith("\r\n\r\n", StringComparison.Ordinal) ? text[..^4] : text;
    }

    private static async Task WriteReplyAsync(Stream stream, HttpReply reply, bool headOnly)
    {
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(reply.Status).Append(' ').Append(ReasonPhrase(reply.Status)).Append("\r\n");
        sb.Append("Content-Type: ").Append(reply.ContentType).Append("\r\n");
        sb.Append("Content-Length: ").Append(reply.Body.Length).Append("\r\n");
        sb.Append("Connection: close\r\n");
        if (reply.Headers is not null)
        {
            foreach (var kv in reply.Headers) sb.Append(kv.Key).Append(": ").Append(kv.Value).Append("\r\n");
        }
        sb.Append("\r\n");
        byte[] head = Encoding.ASCII.GetBytes(sb.ToString());
        await stream.WriteAsync(head).ConfigureAwait(false);
        if (!headOnly && reply.Body.Length > 0)
            await stream.WriteAsync(reply.Body).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    private static string ReasonPhrase(int status) => status switch
    {
        200 => "OK",
        302 => "Found",
        404 => "Not Found",
        405 => "Method Not Allowed",
        500 => "Internal Server Error",
        _ => "Status",
    };

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        try { _acceptLoop.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _cts.Dispose();
    }
}
