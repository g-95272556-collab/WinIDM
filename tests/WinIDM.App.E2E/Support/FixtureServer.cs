using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace WinIDM.App.E2E.Support;

/// <summary>Record of one request received by the fixture server.</summary>
internal sealed record FixtureRequest(DateTimeOffset At, string Method, string Path, long RangeStart, long RangeEnd, int Status, long BytesSent);

/// <summary>
/// HTTP fixture server on 127.0.0.1 (random port). Supports Range (206), HEAD, per-connection rate limiting
/// (bytes per second) and records every request. No internet: every test target is this server.
/// </summary>
internal sealed class FixtureServer : IAsyncDisposable, IDisposable
{
    private const int ChunkSize = 16 * 1024;

    private readonly ConcurrentDictionary<string, Fixture> _fixtures = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<FixtureRequest> _log = new();
    private readonly ConcurrentDictionary<string, long> _live = new(StringComparer.Ordinal);
    private WebApplication? _app;

    private sealed record Fixture(byte[] Content, string ContentType, int BytesPerSecond, bool AcceptRanges,
        Func<HttpContext, bool>? Authorize);

    /// <summary>Short body of a guarded fixture's 403 response (text/plain, so the media detector ignores it).</summary>
    private static readonly byte[] DenyBody = Encoding.UTF8.GetBytes("media session cookie missing");

    public string BaseUrl { get; private set; } = "";

    /// <summary>
    /// Synchronous start for the UI Automation tests, which stay synchronous on purpose (one thread drives the
    /// desktop from start to finish). Kestrel starting on 127.0.0.1 has no UI context to deadlock on.
    /// </summary>
    public static FixtureServer Start() => StartAsync().GetAwaiter().GetResult();

    public static async Task<FixtureServer> StartAsync()
    {
        var server = new FixtureServer();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = Array.Empty<string>() });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.Run(server.HandleAsync);
        await app.StartAsync();

        var addr = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        server.BaseUrl = addr.TrimEnd('/');
        server._app = app;
        return server;
    }

    /// <summary>Register a file. <paramref name="bytesPerSecond"/> &gt; 0 limits the rate per connection.</summary>
    public void Add(string path, byte[] content, string contentType = "application/octet-stream", int bytesPerSecond = 0, bool acceptRanges = true) =>
        _fixtures[path] = new Fixture(content, contentType, bytesPerSecond, acceptRanges, null);

    /// <summary>
    /// Register a file that is served only to requests carrying the given cookie; every other request gets a
    /// short 403 (text/plain) response. Used to prove that a browser download repeats the page's session cookie:
    /// the payload is returned only to a request that has it.
    /// </summary>
    public void AddCookieGuarded(string path, byte[] content, string contentType, string cookieName, string cookieValue) =>
        _fixtures[path] = new Fixture(content, contentType, 0, true,
            ctx => HasCookie(ctx.Request, cookieName, cookieValue));

    private static bool HasCookie(HttpRequest request, string name, string value)
    {
        foreach (var header in request.Headers.Cookie)
        {
            if (string.IsNullOrWhiteSpace(header)) continue;
            foreach (var pair in header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                if (string.Equals(pair[..eq].Trim(), name, StringComparison.Ordinal)
                    && string.Equals(pair[(eq + 1)..].Trim(), value, StringComparison.Ordinal))
                    return true;
            }
        }
        return false;
    }

    public string Url(string path) => BaseUrl + path;

    public IReadOnlyList<FixtureRequest> Requests => _log.ToArray();

    /// <summary>Bytes already written to the socket for a given path (live per-chunk count, including unfinished requests).</summary>
    public long LiveBytes(string path) => _live.TryGetValue(path, out var n) ? Interlocked.Read(ref n) : 0;

    /// <summary>Total bytes sent for a given path (all requests).</summary>
    public long BytesSent(string path) => _log.Where(r => r.Path == path).Sum(r => r.BytesSent);

    /// <summary>Fixture bytes generated deterministically (fixed seed) so the hash can be predicted.</summary>
    public static byte[] Deterministic(int size, int seed)
    {
        var buf = new byte[size];
        new Random(seed).NextBytes(buf);
        return buf;
    }

    /// <summary>Write the request log to a file (diagnostics; stored in artifacts).</summary>
    public void DumpLog(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, Requests.Select(r =>
            $"{r.At:HH:mm:ss.fff} {r.Method} {r.Path} range={r.RangeStart}-{r.RangeEnd} status={r.Status} sent={r.BytesSent}"));
    }

    public static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static string Sha256File(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    private async Task HandleAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "/";
        var method = ctx.Request.Method;
        if (!_fixtures.TryGetValue(path, out var fx))
        {
            ctx.Response.StatusCode = 404;
            _log.Enqueue(new FixtureRequest(DateTimeOffset.UtcNow, method, path, -1, -1, 404, 0));
            return;
        }

        // Guarded fixture (session cookie): deny before any content/range handling.
        if (fx.Authorize is not null && !fx.Authorize(ctx))
        {
            ctx.Response.StatusCode = 403;
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            ctx.Response.ContentLength = DenyBody.Length;
            ctx.Response.Headers["Cache-Control"] = "no-store";
            try
            {
                await ctx.Response.Body.WriteAsync(DenyBody, ctx.RequestAborted);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ConnectionResetException)
            {
                // The client dropped the connection; the 403 was still sent and is logged below.
            }
            _log.Enqueue(new FixtureRequest(DateTimeOffset.UtcNow, method, path, -1, -1, 403, DenyBody.Length));
            return;
        }

        var total = fx.Content.LongLength;
        long start = 0, end = total - 1;
        int status = 200;
        long rangeStart = -1, rangeEnd = -1;

        var rangeHeader = ctx.Request.Headers.Range.ToString();
        if (fx.AcceptRanges)
        {
            ctx.Response.Headers["Accept-Ranges"] = "bytes";
            if (rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                var spec = rangeHeader["bytes=".Length..].Split('-', 2);
                if (spec[0].Length == 0 && spec.Length > 1 && long.TryParse(spec[1], out var suffix))
                {
                    // Suffix range "bytes=-N": the last N bytes.
                    start = Math.Max(0, total - suffix);
                }
                else
                {
                    if (long.TryParse(spec[0], out var s)) start = s;
                    if (spec.Length > 1 && long.TryParse(spec[1], out var e)) end = Math.Min(e, total - 1);
                }
                if (start > end || start >= total)
                {
                    ctx.Response.StatusCode = 416;
                    ctx.Response.Headers["Content-Range"] = $"bytes */{total}";
                    _log.Enqueue(new FixtureRequest(DateTimeOffset.UtcNow, method, path, start, end, 416, 0));
                    return;
                }
                status = 206;
                rangeStart = start;
                rangeEnd = end;
                ctx.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{total}";
            }
        }

        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = fx.ContentType;
        ctx.Response.ContentLength = end - start + 1;
        ctx.Response.Headers["Cache-Control"] = "no-store";

        long sent = 0;
        if (HttpMethods.IsGet(method))
        {
            try
            {
                int offset = 0;
                long remaining = end - start + 1;
                while (remaining > 0)
                {
                    int n = (int)Math.Min(ChunkSize, remaining);
                    await ctx.Response.Body.WriteAsync(fx.Content.AsMemory((int)(start + offset), n), ctx.RequestAborted);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                    sent += n;
                    _live.AddOrUpdate(path, n, (_, cur) => cur + n);
                    offset += n;
                    remaining -= n;
                    if (fx.BytesPerSecond > 0)
                        await Task.Delay(TimeSpan.FromMilliseconds(1000.0 * n / fx.BytesPerSecond), ctx.RequestAborted);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ConnectionResetException)
            {
                // The client dropped the connection (pause/cancel): expected for the pause test.
            }
        }

        _log.Enqueue(new FixtureRequest(DateTimeOffset.UtcNow, method, path, rangeStart, rangeEnd, status, sent));
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                await _app.StopAsync(cts.Token);
            await _app.DisposeAsync();
            _app = null;
        }
    }
}
