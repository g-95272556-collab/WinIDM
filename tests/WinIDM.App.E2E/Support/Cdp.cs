using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace WinIDM.App.E2E.Support;

/// <summary>Information about one DevTools target (a WebView2 page).</summary>
internal sealed record CdpPage(string Id, string Title, string Url, string WebSocketUrl);

/// <summary>
/// Minimal Chrome DevTools Protocol client for WebView2 launched with --remote-debugging-port.
/// Used to wait for NavigationCompleted (readyState) and to read the DOM changed by user scripts.
/// </summary>
internal static class Cdp
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>Random free TCP port on 127.0.0.1 (closed before use).</summary>
    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public static async Task<List<CdpPage>> ListPagesAsync(int port)
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync($"http://127.0.0.1:{port}/json/list"));
        var pages = new List<CdpPage>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (e.GetProperty("type").GetString() != "page") continue;
            pages.Add(new CdpPage(
                e.GetProperty("id").GetString()!,
                e.GetProperty("title").GetString() ?? "",
                e.GetProperty("url").GetString() ?? "",
                e.GetProperty("webSocketDebuggerUrl").GetString() ?? ""));
        }
        return pages;
    }

    /// <summary>Run a JavaScript expression on the page and return the value (returnByValue).</summary>
    public static async Task<JsonElement> EvaluateAsync(string wsUrl, string expression)
    {
        using var ws = new ClientWebSocket();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await ws.ConnectAsync(new Uri(wsUrl), cts.Token);

        var req = JsonSerializer.Serialize(new
        {
            id = 1,
            method = "Runtime.evaluate",
            @params = new { expression, returnByValue = true, awaitPromise = false },
        });
        await ws.SendAsync(Encoding.UTF8.GetBytes(req), WebSocketMessageType.Text, true, cts.Token);

        var buf = new byte[64 * 1024];
        var sb = new StringBuilder();
        while (true)
        {
            var seg = await ws.ReceiveAsync(buf, cts.Token);
            sb.Append(Encoding.UTF8.GetString(buf, 0, seg.Count));
            if (!seg.EndOfMessage) continue;

            using var msg = JsonDocument.Parse(sb.ToString());
            sb.Clear();
            if (!msg.RootElement.TryGetProperty("id", out var id) || id.GetInt32() != 1) continue;
            if (msg.RootElement.TryGetProperty("error", out var err))
                throw new InvalidOperationException("CDP error: " + err.GetRawText());
            var result = msg.RootElement.GetProperty("result");
            if (result.TryGetProperty("exceptionDetails", out var ex))
                throw new InvalidOperationException("Page script error: " + ex.GetRawText());
            return result.GetProperty("result").Clone();
        }
    }

    /// <summary>The expression value as text (string) or null.</summary>
    public static async Task<string?> EvaluateStringAsync(string wsUrl, string expression)
    {
        var r = await EvaluateAsync(wsUrl, expression);
        return r.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
