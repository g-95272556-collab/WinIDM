using System.Net;
using System.Text;
using WinIDM.Backup;

namespace WinIDM.Backup.Tests;

public class DestinationTests
{
    [Fact]
    public async Task Local_folder_upload_list_download_delete_round_trip()
    {
        using var dir = new TempDir();
        var destination = new LocalFolderDestination(dir.File("dest"));
        var name = BackupNames.NewName(DateTimeOffset.UtcNow, encrypted: false);
        var source = dir.File("source.zip");
        File.WriteAllText(source, "backup-content");

        await destination.UploadAsync(source, name);

        var listed = await destination.ListAsync();
        var entry = Assert.Single(listed);
        Assert.Equal(name, entry.Name);
        Assert.Equal((long)"backup-content".Length, entry.SizeBytes);

        var copy = dir.File("copy.zip");
        await destination.DownloadAsync(name, copy);
        Assert.Equal("backup-content", File.ReadAllText(copy));

        await destination.DeleteAsync(name);
        await destination.DeleteAsync(name);
        Assert.Empty(await destination.ListAsync());
    }

    [Fact]
    public async Task Local_folder_rejects_path_like_remote_names()
    {
        using var dir = new TempDir();
        var destination = new LocalFolderDestination(dir.File("dest"));
        var source = dir.File("source.zip");
        File.WriteAllText(source, "x");

        await Assert.ThrowsAsync<ArgumentException>(() => destination.UploadAsync(source, "../outside.zip"));
        await Assert.ThrowsAsync<ArgumentException>(() => destination.DeleteAsync("sub/x.zip"));
    }

    [Fact]
    public async Task Rest_destination_uploads_lists_downloads_and_deletes_with_bearer_token()
    {
        using var dir = new TempDir();
        using var server = new FakeRestServer("secret-token");
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        var destination = new RestServerDestination(http, server.BaseUri, "secret-token");
        var name = BackupNames.NewName(DateTimeOffset.UtcNow, encrypted: true);
        var source = dir.File("source.zip.enc");
        File.WriteAllBytes(source, Encoding.UTF8.GetBytes("backup-bytes-1234"));

        await destination.UploadAsync(source, name);

        var listed = await destination.ListAsync();
        var entry = Assert.Single(listed);
        Assert.Equal(name, entry.Name);
        Assert.Equal(17, entry.SizeBytes);

        var copy = dir.File("download.zip.enc");
        await destination.DownloadAsync(name, copy);
        Assert.Equal("backup-bytes-1234", Encoding.UTF8.GetString(File.ReadAllBytes(copy)));

        await destination.DeleteAsync(name);
        Assert.Empty(await destination.ListAsync());

        var missing = await Assert.ThrowsAsync<BackupTransportException>(() => destination.DownloadAsync(name, copy));
        Assert.Equal(404, missing.StatusCode);
    }

    [Fact]
    public async Task Rest_destination_rejects_wrong_token()
    {
        using var dir = new TempDir();
        using var server = new FakeRestServer("correct");
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        var destination = new RestServerDestination(http, server.BaseUri, "bad-token");
        var source = dir.File("source.zip");
        File.WriteAllText(source, "x");

        var ex = await Assert.ThrowsAsync<BackupTransportException>(() =>
            destination.UploadAsync(source, BackupNames.NewName(DateTimeOffset.UtcNow, false)));

        Assert.Equal(401, ex.StatusCode);
        Assert.DoesNotContain("bad-token", ex.Message);
    }

    [Fact]
    public async Task Rest_destination_without_token_sends_no_authorization_header()
    {
        using var dir = new TempDir();
        using var server = new FakeRestServer(token: null);
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        var destination = new RestServerDestination(http, server.BaseUri);

        Assert.Empty(await destination.ListAsync());
    }

    [Fact]
    public void Rest_destination_requires_absolute_http_base()
    {
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        Assert.Throws<ArgumentException>(() => new RestServerDestination(http, new Uri("/relative", UriKind.Relative)));
        Assert.Throws<ArgumentException>(() => new RestServerDestination(http, new Uri("ftp://sample.example/")));
    }

    /// <summary>Local REST server (HttpListener) with in-memory storage for tests.</summary>
    private sealed class FakeRestServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Dictionary<string, byte[]> _store = new(StringComparer.Ordinal);
        private readonly string? _token;
        private readonly Task _loop;

        public FakeRestServer(string? token)
        {
            _token = token;
            var port = TestHttp.FreePort();
            var prefix = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(prefix);
            _listener.Start();
            BaseUri = new Uri($"{prefix}api/");
            _loop = Task.Run(LoopAsync);
        }

        public Uri BaseUri { get; }

        private async Task LoopAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
                {
                    break;
                }

                try
                {
                    Handle(context);
                }
                catch (Exception)
                {
                    context.Response.StatusCode = 500;
                    context.Response.Close();
                }
            }
        }

        private void Handle(HttpListenerContext context)
        {
            var req = context.Request;
            var res = context.Response;

            if (_token is not null && req.Headers["Authorization"] != "Bearer " + _token)
            {
                res.StatusCode = 401;
                res.Close();
                return;
            }

            var path = req.Url!.AbsolutePath;
            if (req.HttpMethod == "GET" && path == "/api/backups")
            {
                var items = _store.Keys.OrderBy(k => k, StringComparer.Ordinal)
                    .Select(k => $"{{\"name\":\"{k}\",\"modifiedUtc\":\"2026-10-31T00:00:00Z\",\"sizeBytes\":{_store[k].Length}}}");
                WriteText(res, 200, "[" + string.Join(",", items) + "]");
                return;
            }

            const string prefix = "/api/backups/";
            if (!path.StartsWith(prefix, StringComparison.Ordinal))
            {
                res.StatusCode = 404;
                res.Close();
                return;
            }

            var name = Uri.UnescapeDataString(path[prefix.Length..]);
            switch (req.HttpMethod)
            {
                case "PUT":
                    using (var ms = new MemoryStream())
                    {
                        req.InputStream.CopyTo(ms);
                        _store[name] = ms.ToArray();
                    }

                    res.StatusCode = 201;
                    res.Close();
                    break;
                case "GET":
                    if (!_store.TryGetValue(name, out var data))
                    {
                        res.StatusCode = 404;
                        res.Close();
                        break;
                    }

                    res.StatusCode = 200;
                    res.ContentType = "application/octet-stream";
                    res.ContentLength64 = data.Length;
                    res.OutputStream.Write(data, 0, data.Length);
                    res.Close();
                    break;
                case "DELETE":
                    res.StatusCode = _store.Remove(name) ? 204 : 404;
                    res.Close();
                    break;
                default:
                    res.StatusCode = 405;
                    res.Close();
                    break;
            }
        }

        private static void WriteText(HttpListenerResponse res, int status, string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            res.StatusCode = status;
            res.ContentType = "application/json";
            res.ContentLength64 = bytes.Length;
            res.OutputStream.Write(bytes, 0, bytes.Length);
            res.Close();
        }

        public void Dispose()
        {
            _listener.Close();
            try
            {
                _loop.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
                // The loop has already stopped.
            }
        }
    }
}
