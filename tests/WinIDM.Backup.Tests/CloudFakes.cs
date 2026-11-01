using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinIDM.Backup.Tests;

/// <summary>A request received by the mock server (a copy of the body bytes).</summary>
internal sealed class FakeRequest
{
    public FakeRequest(string method, string path, string query, IReadOnlyDictionary<string, string> headers, string? contentType, byte[] body)
    {
        Method = method;
        Path = path;
        Query = query;
        Headers = headers;
        ContentType = contentType;
        Body = body;
    }

    public string Method { get; }

    public string Path { get; }

    public string Query { get; }

    public IReadOnlyDictionary<string, string> Headers { get; }

    public string? ContentType { get; }

    public byte[] Body { get; }

    public string BodyText => Encoding.UTF8.GetString(Body);

    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;

    public string? QueryValue(string name)
    {
        foreach (var pair in Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var key = Uri.UnescapeDataString(separator < 0 ? pair : pair[..separator]);
            if (key == name)
            {
                return Uri.UnescapeDataString(separator < 0 ? string.Empty : pair[(separator + 1)..]);
            }
        }

        return null;
    }
}

/// <summary>Mock response.</summary>
internal sealed record FakeResponse(int Status, byte[] Body, string ContentType, IReadOnlyDictionary<string, string> Headers)
{
    public static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();

    public static FakeResponse Json(int status, string json, IReadOnlyDictionary<string, string>? headers = null) =>
        new(status, Encoding.UTF8.GetBytes(json), "application/json", headers ?? NoHeaders);

    public static FakeResponse Bytes(int status, byte[] body) =>
        new(status, body, "application/octet-stream", NoHeaders);

    public static FakeResponse Empty(int status, IReadOnlyDictionary<string, string>? headers = null) =>
        new(status, Array.Empty<byte>(), "text/plain", headers ?? NoHeaders);

    public static IReadOnlyDictionary<string, string> Header(string name, string value) =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [name] = value };
}

internal enum FaultKind
{
    /// <summary>Return a specific response without running the handler (for example 429 with Retry-After).</summary>
    Respond,

    /// <summary>Drop the connection before the server changes state.</summary>
    AbortBeforeApply,

    /// <summary>Run the handler (state changes), then drop the connection so the response is lost.</summary>
    AbortAfterApply,
}

internal sealed class FakeFault
{
    public FakeFault(Func<FakeRequest, bool> match, FaultKind kind, int times = 1, FakeResponse? response = null)
    {
        Match = match;
        Kind = kind;
        Remaining = times;
        Response = response;
    }

    public Func<FakeRequest, bool> Match { get; }

    public FaultKind Kind { get; }

    public int Remaining { get; set; }

    public FakeResponse? Response { get; }
}

/// <summary>
/// Mock HTTP server on 127.0.0.1 (HttpListener). No internet access. Requests are processed serially.
/// </summary>
internal abstract class FakeCloudServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly object _gate = new();
    private readonly List<FakeRequest> _requests = new();
    private readonly List<FakeFault> _faults = new();
    private readonly Task _loop;

    protected FakeCloudServer()
    {
        var port = TestHttp.FreePort();
        var prefix = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(prefix);
        _listener.Start();
        BaseUri = new Uri(prefix);
        _loop = Task.Run(LoopAsync);
    }

    public Uri BaseUri { get; }

    /// <summary>Accepted Bearer token. Null means any token is accepted.</summary>
    public string? AcceptedAccessToken { get; set; } = "at-valid";

    public IReadOnlyList<FakeRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToList();
            }
        }
    }

    public void AddFault(FakeFault fault)
    {
        lock (_gate)
        {
            _faults.Add(fault);
        }
    }

    protected object State { get; } = new();

    protected abstract FakeResponse Handle(FakeRequest request);

    protected virtual FakeResponse Unauthorized() =>
        FakeResponse.Json(401, "{\"error\":{\"code\":401,\"message\":\"Invalid Credentials\"}}");

    private async Task LoopAsync()
    {
        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            try
            {
                Process(context);
            }
            catch (Exception)
            {
                try
                {
                    context.Response.StatusCode = 500;
                    context.Response.Close();
                }
                catch (Exception)
                {
                    // The connection was already closed by the client.
                }
            }
        }
    }

    private void Process(HttpListenerContext context)
    {
        var req = context.Request;
        using var buffer = new MemoryStream();
        req.InputStream.CopyTo(buffer);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in req.Headers.AllKeys)
        {
            if (key is not null)
            {
                headers[key] = req.Headers[key] ?? string.Empty;
            }
        }

        var request = new FakeRequest(req.HttpMethod, req.Url!.AbsolutePath, req.Url.Query, headers, req.ContentType, buffer.ToArray());
        lock (_gate)
        {
            _requests.Add(request);
        }

        var fault = TakeFault(request);
        if (fault?.Kind == FaultKind.AbortBeforeApply)
        {
            context.Response.Abort();
            return;
        }

        FakeResponse response;
        if (fault?.Kind == FaultKind.Respond)
        {
            response = fault.Response!;
        }
        else if (AcceptedAccessToken is not null && request.Header("Authorization") != "Bearer " + AcceptedAccessToken)
        {
            response = Unauthorized();
        }
        else
        {
            response = Handle(request);
        }

        if (fault?.Kind == FaultKind.AbortAfterApply)
        {
            context.Response.Abort();
            return;
        }

        Write(context.Response, response);
    }

    private FakeFault? TakeFault(FakeRequest request)
    {
        lock (_gate)
        {
            var fault = _faults.FirstOrDefault(f => f.Remaining > 0 && f.Match(request));
            if (fault is null)
            {
                return null;
            }

            fault.Remaining--;
            return fault;
        }
    }

    private static void Write(HttpListenerResponse res, FakeResponse r)
    {
        res.StatusCode = r.Status;
        foreach (var (key, value) in r.Headers)
        {
            res.Headers[key] = value;
        }

        res.ContentType = r.ContentType;
        res.ContentLength64 = r.Body.Length;
        if (r.Body.Length > 0)
        {
            res.OutputStream.Write(r.Body, 0, r.Body.Length);
        }

        res.Close();
    }

    public virtual void Dispose()
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

    /// <summary>Header bytes and body bytes of a multipart/related body are split by boundary.</summary>
    protected static List<(byte[] Headers, byte[] Content)> SplitMultipart(FakeRequest r)
    {
        var boundary = Regex.Match(r.ContentType ?? string.Empty, "boundary=([^;]+)").Groups[1].Value.Trim('"');
        var body = r.Body;
        var delimiter = Encoding.ASCII.GetBytes("--" + boundary);
        var parts = new List<(byte[], byte[])>();

        var pos = IndexOf(body, delimiter, 0);
        if (pos < 0)
        {
            throw new InvalidOperationException("Multipart boundary is missing.");
        }

        pos += delimiter.Length;
        while (true)
        {
            if (pos + 1 < body.Length && body[pos] == '-' && body[pos + 1] == '-')
            {
                break;
            }

            pos += 2; // CRLF after the boundary
            var next = IndexOf(body, delimiter, pos);
            if (next < 0)
            {
                throw new InvalidOperationException("Multipart part is incomplete.");
            }

            var part = body[pos..(next - 2)];
            var headerEnd = IndexOf(part, "\r\n\r\n"u8.ToArray(), 0);
            parts.Add((part[..headerEnd], part[(headerEnd + 4)..]));
            pos = next + delimiter.Length;
        }

        return parts;
    }

    protected static int IndexOf(byte[] haystack, byte[] needle, int start)
    {
        var index = haystack.AsSpan(start).IndexOf(needle);
        return index < 0 ? -1 : start + index;
    }

    protected static string ToUtf8(byte[] bytes) => Encoding.UTF8.GetString(bytes);
}

/// <summary>Mock server that follows the Google Drive v3 contract (list with q, multipart, resumable, download, delete).</summary>
internal sealed class FakeDriveServer : FakeCloudServer
{
    private const string FolderMime = "application/vnd.google-apps.folder";

    private sealed class DriveFile
    {
        public required string Id { get; init; }

        public required string Name { get; set; }

        public required string MimeType { get; set; }

        public List<string> Parents { get; set; } = new();

        public byte[] Content { get; set; } = Array.Empty<byte>();
    }

    private sealed class UploadSession
    {
        public string? TargetId { get; init; }

        public string Name { get; init; } = string.Empty;

        public List<string> Parents { get; init; } = new();

        public long Total { get; init; }

        public MemoryStream Received { get; } = new();
    }

    private readonly Dictionary<string, DriveFile> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, UploadSession> _sessions = new(StringComparer.Ordinal);
    private int _nextId;

    /// <summary>Server page limit (honored together with the request pageSize).</summary>
    public int PageSize { get; set; } = 1000;

    public byte[]? ContentOf(string name)
    {
        lock (State)
        {
            return _files.Values.FirstOrDefault(f => f.Name == name && f.MimeType != FolderMime)?.Content;
        }
    }

    public int CountNamed(string name)
    {
        lock (State)
        {
            return _files.Values.Count(f => f.Name == name && f.MimeType != FolderMime);
        }
    }

    public string? FolderId(string name)
    {
        lock (State)
        {
            return _files.Values.FirstOrDefault(f => f.Name == name && f.MimeType == FolderMime)?.Id;
        }
    }

    public IReadOnlyList<string> ParentsOf(string name)
    {
        lock (State)
        {
            return _files.Values.FirstOrDefault(f => f.Name == name && f.MimeType != FolderMime)?.Parents.ToList()
                ?? new List<string>();
        }
    }

    /// <summary>Add a file directly to the server (for example a root file not created by the folder destination).</summary>
    public void SeedFile(string name, byte[] content, string? parent = null)
    {
        lock (State)
        {
            var id = "seed-" + (++_nextId);
            _files[id] = new DriveFile
            {
                Id = id,
                Name = name,
                MimeType = "application/octet-stream",
                Parents = parent is null ? new List<string> { "root" } : new List<string> { parent },
                Content = content,
            };
        }
    }

    protected override FakeResponse Handle(FakeRequest r)
    {
        var path = r.Path;
        if (path == "/drive/v3/files")
        {
            return r.Method == "GET" ? ListFiles(r) : CreateJson(r);
        }

        if (path == "/upload/drive/v3/files")
        {
            return r.QueryValue("uploadType") == "multipart" ? CreateMultipart(r) : StartSession(r, null);
        }

        if (path.StartsWith("/upload/drive/v3/files/", StringComparison.Ordinal))
        {
            var id = path["/upload/drive/v3/files/".Length..];
            return r.QueryValue("uploadType") == "multipart" ? UpdateMultipart(r, id) : StartSession(r, id);
        }

        if (path.StartsWith("/upload/session/", StringComparison.Ordinal))
        {
            return SessionPut(r, path["/upload/session/".Length..]);
        }

        if (path.StartsWith("/drive/v3/files/", StringComparison.Ordinal))
        {
            var id = path["/drive/v3/files/".Length..];
            return r.Method == "DELETE" ? Delete(id) : Download(id);
        }

        return FakeResponse.Json(404, "{}");
    }

    protected override FakeResponse Unauthorized() =>
        FakeResponse.Json(401, "{\"error\":{\"code\":401,\"message\":\"Invalid Credentials\",\"status\":\"UNAUTHENTICATED\"}}");

    private FakeResponse ListFiles(FakeRequest r)
    {
        var q = r.QueryValue("q") ?? string.Empty;
        var spaces = r.QueryValue("spaces") ?? "drive";
        var clauses = q.Split(" and ", StringSplitOptions.RemoveEmptyEntries).Select(c => c.Trim()).ToList();
        var pageSize = Math.Min(int.Parse(r.QueryValue("pageSize") ?? "100"), PageSize);
        var offset = int.Parse(r.QueryValue("pageToken") ?? "0");

        List<DriveFile> page;
        bool more;
        lock (State)
        {
            var matches = _files.Values
                .Where(f => (spaces == "appDataFolder") == f.Parents.Contains("appDataFolder"))
                .Where(f => clauses.All(c => Matches(f, c)))
                .OrderBy(f => f.Name, StringComparer.Ordinal)
                .ThenBy(f => f.Id, StringComparer.Ordinal)
                .ToList();
            page = matches.Skip(offset).Take(pageSize).ToList();
            more = offset + pageSize < matches.Count;
        }

        var files = page.Select(f => new Dictionary<string, object?>
        {
            ["id"] = f.Id,
            ["name"] = f.Name,
            ["mimeType"] = f.MimeType,
            ["size"] = f.Content.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["modifiedTime"] = "2026-10-31T00:00:00Z",
        });
        var body = new Dictionary<string, object?> { ["files"] = files };
        if (more)
        {
            body["nextPageToken"] = (offset + pageSize).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return FakeResponse.Json(200, JsonSerializer.Serialize(body));
    }

    private static bool Matches(DriveFile f, string clause)
    {
        if (clause == "trashed = false")
        {
            return true;
        }

        var name = Regex.Match(clause, "^name = '([^']*)'$");
        if (name.Success)
        {
            return f.Name == name.Groups[1].Value;
        }

        var contains = Regex.Match(clause, "^name contains '([^']*)'$");
        if (contains.Success)
        {
            return f.Name.Contains(contains.Groups[1].Value, StringComparison.Ordinal);
        }

        var mime = Regex.Match(clause, "^mimeType = '([^']*)'$");
        if (mime.Success)
        {
            return f.MimeType == mime.Groups[1].Value;
        }

        var parent = Regex.Match(clause, "^'([^']*)' in parents$");
        if (parent.Success)
        {
            return f.Parents.Contains(parent.Groups[1].Value);
        }

        throw new InvalidOperationException("The q clause is not supported by the mock server: " + clause);
    }

    private FakeResponse CreateJson(FakeRequest r)
    {
        var meta = JsonDocument.Parse(r.Body).RootElement;
        var name = meta.GetProperty("name").GetString()!;
        var mime = meta.TryGetProperty("mimeType", out var m) ? m.GetString()! : "application/octet-stream";
        lock (State)
        {
            var file = NewFile(name, mime, ParentsFrom(meta), Array.Empty<byte>());
            return FakeResponse.Json(200, JsonSerializer.Serialize(new { id = file.Id, name, mimeType = mime }));
        }
    }

    private FakeResponse CreateMultipart(FakeRequest r)
    {
        var (meta, media) = ReadMultipart(r);
        var name = meta.GetProperty("name").GetString()!;
        lock (State)
        {
            var file = NewFile(name, "application/octet-stream", ParentsFrom(meta), media);
            return FakeResponse.Json(200, JsonSerializer.Serialize(new { id = file.Id }));
        }
    }

    private FakeResponse UpdateMultipart(FakeRequest r, string id)
    {
        var (_, media) = ReadMultipart(r);
        lock (State)
        {
            if (!_files.TryGetValue(id, out var file))
            {
                return FakeResponse.Json(404, "{\"error\":{\"code\":404}}");
            }

            file.Content = media;
            return FakeResponse.Json(200, JsonSerializer.Serialize(new { id = file.Id }));
        }
    }

    private FakeResponse StartSession(FakeRequest r, string? targetId)
    {
        var meta = JsonDocument.Parse(r.Body.Length == 0 ? "{}"u8.ToArray() : r.Body).RootElement;
        var total = long.Parse(r.Header("X-Upload-Content-Length") ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        var sid = "session-" + Guid.NewGuid().ToString("N");
        lock (State)
        {
            _sessions[sid] = new UploadSession
            {
                TargetId = targetId,
                Name = meta.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty,
                Parents = ParentsFrom(meta),
                Total = total,
            };
        }

        var location = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Location"] = BaseUri + "upload/session/" + sid,
        };
        return FakeResponse.Empty(200, location);
    }

    private FakeResponse SessionPut(FakeRequest r, string sid)
    {
        lock (State)
        {
            if (!_sessions.TryGetValue(sid, out var session))
            {
                return FakeResponse.Json(404, "{\"error\":{\"code\":404}}");
            }

            var spec = (r.Header("Content-Range") ?? throw new InvalidOperationException("Content-Range is missing."))["bytes ".Length..];
            var slash = spec.LastIndexOf('/');
            var rangePart = spec[..slash];

            if (rangePart == "*")
            {
                // Status query: no bytes are sent.
                return session.Received.Length == session.Total ? Finish(sid, session) : Incomplete(session);
            }

            var dash = rangePart.IndexOf('-');
            var start = long.Parse(rangePart[..dash], System.Globalization.CultureInfo.InvariantCulture);
            var received = session.Received.Length;
            if (start > received)
            {
                return FakeResponse.Json(400, "{\"error\":{\"code\":400,\"message\":\"Range not contiguous\"}}");
            }

            var skip = (int)Math.Min(received - start, r.Body.Length);
            if (skip < r.Body.Length)
            {
                session.Received.Write(r.Body, skip, r.Body.Length - skip);
            }

            return session.Received.Length == session.Total ? Finish(sid, session) : Incomplete(session);
        }
    }

    private static FakeResponse Incomplete(UploadSession session)
    {
        var headers = session.Received.Length > 0
            ? FakeResponse.Header("Range", $"bytes=0-{session.Received.Length - 1}")
            : FakeResponse.NoHeaders;
        return FakeResponse.Empty(308, headers);
    }

    private FakeResponse Finish(string sid, UploadSession session)
    {
        var content = session.Received.ToArray();
        _sessions.Remove(sid);
        DriveFile file;
        if (session.TargetId is not null)
        {
            if (!_files.TryGetValue(session.TargetId, out file!))
            {
                return FakeResponse.Json(404, "{\"error\":{\"code\":404}}");
            }

            file.Content = content;
        }
        else
        {
            file = NewFile(session.Name, "application/octet-stream", session.Parents, content);
        }

        return FakeResponse.Json(200, JsonSerializer.Serialize(new { id = file.Id, name = file.Name }));
    }

    private FakeResponse Download(string id)
    {
        lock (State)
        {
            return _files.TryGetValue(id, out var file)
                ? FakeResponse.Bytes(200, file.Content)
                : FakeResponse.Json(404, "{\"error\":{\"code\":404,\"message\":\"File not found\"}}");
        }
    }

    private FakeResponse Delete(string id)
    {
        lock (State)
        {
            return _files.Remove(id)
                ? FakeResponse.Empty(204)
                : FakeResponse.Json(404, "{\"error\":{\"code\":404,\"message\":\"File not found\"}}");
        }
    }

    private DriveFile NewFile(string name, string mime, List<string> parents, byte[] content)
    {
        var id = "id-" + (++_nextId);
        var file = new DriveFile
        {
            Id = id,
            Name = name,
            MimeType = mime,
            Parents = parents.Count == 0 ? new List<string> { "root" } : parents,
            Content = content,
        };
        _files[id] = file;
        return file;
    }

    private static List<string> ParentsFrom(JsonElement meta) =>
        meta.TryGetProperty("parents", out var parents) && parents.ValueKind == JsonValueKind.Array
            ? parents.EnumerateArray().Select(p => p.GetString()!).ToList()
            : new List<string>();

    private (JsonElement Meta, byte[] Media) ReadMultipart(FakeRequest r)
    {
        var parts = SplitMultipart(r);
        var meta = JsonDocument.Parse(parts[0].Content).RootElement.Clone();
        return (meta, parts[1].Content);
    }
}

/// <summary>Mock server that follows the Dropbox API v2 contract (list_folder, upload, upload_session, download, delete_v2).</summary>
internal sealed class FakeDropboxServer : FakeCloudServer
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _folders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> _sessions = new(StringComparer.Ordinal);
    private int _nextSession;

    public int PageSize { get; set; } = 1000;

    public byte[]? ContentAt(string path)
    {
        lock (State)
        {
            return _files.TryGetValue(path, out var bytes) ? bytes : null;
        }
    }

    public int FileCount
    {
        get
        {
            lock (State)
            {
                return _files.Count;
            }
        }
    }

    public void Seed(string path, byte[] content)
    {
        lock (State)
        {
            _files[path] = content;
            _folders.Add(ParentOf(path));
        }
    }

    protected override FakeResponse Handle(FakeRequest r) => r.Path switch
    {
        "/2/files/list_folder" => ListFolder(JsonDocument.Parse(r.Body).RootElement),
        "/2/files/list_folder/continue" => ListContinue(JsonDocument.Parse(r.Body).RootElement),
        "/2/files/upload" => Upload(Arg(r), r.Body),
        "/2/files/download" => Download(Arg(r)),
        "/2/files/delete_v2" => DeleteV2(JsonDocument.Parse(r.Body).RootElement),
        "/2/files/upload_session/start" => Start(r.Body),
        "/2/files/upload_session/append_v2" => Append(Arg(r), r.Body),
        "/2/files/upload_session/finish" => Finish(Arg(r), r.Body),
        _ => FakeResponse.Json(404, "{\"error_summary\":\"not found\"}"),
    };

    protected override FakeResponse Unauthorized() => FakeResponse.Json(401,
        "{\"error_summary\":\"expired_access_token/...\",\"error\":{\".tag\":\"expired_access_token\"}}");

    private const int ListPageCap = 2000;

    private FakeResponse ListFolder(JsonElement arg)
    {
        var path = arg.GetProperty("path").GetString() ?? string.Empty;
        var limit = arg.TryGetProperty("limit", out var l) ? l.GetInt32() : ListPageCap;
        lock (State)
        {
            if (path.Length > 0 && !_folders.Contains(path))
            {
                return FakeResponse.Json(409, "{\"error_summary\":\"path/not_found/..\",\"error\":{\".tag\":\"path\",\"path\":{\".tag\":\"not_found\"}}}");
            }

            var all = EntriesUnder(path);
            return PageResponse(all, 0, Math.Min(PageSize, limit));
        }
    }

    private FakeResponse ListContinue(JsonElement arg)
    {
        var cursor = arg.GetProperty("cursor").GetString() ?? throw new InvalidOperationException("Cursor is missing.");
        var parts = cursor.Split('|');
        var path = parts[0];
        var offset = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        lock (State)
        {
            return PageResponse(EntriesUnder(path), offset, PageSize);
        }
    }

    private List<(string Name, string Path, long Size)> EntriesUnder(string folder)
    {
        return _files
            .Where(kv => string.Equals(ParentOf(kv.Key), folder, StringComparison.OrdinalIgnoreCase))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => (kv.Key[(kv.Key.LastIndexOf('/') + 1)..], kv.Key, (long)kv.Value.Length))
            .ToList();
    }

    private FakeResponse PageResponse(List<(string Name, string Path, long Size)> all, int offset, int pageSize)
    {
        var page = all.Skip(offset).Take(pageSize).ToList();
        var next = offset + page.Count;
        var hasMore = next < all.Count;
        var folderPath = all.Count > 0 ? ParentOf(all[0].Path) : string.Empty;
        var entries = page.Select(e => new Dictionary<string, object>
        {
            [".tag"] = "file",
            ["name"] = e.Name,
            ["path_display"] = e.Path,
            ["size"] = e.Size,
            ["server_modified"] = "2026-10-31T00:00:00Z",
        });
        var body = new Dictionary<string, object>
        {
            ["entries"] = entries,
            ["cursor"] = folderPath + "|" + next.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["has_more"] = hasMore,
        };
        return FakeResponse.Json(200, System.Text.Json.JsonSerializer.Serialize(body));
    }

    private FakeResponse Upload(JsonElement arg, byte[] body)
    {
        var path = arg.GetProperty("path").GetString()!;
        lock (State)
        {
            _files[path] = body;
            _folders.Add(ParentOf(path));
        }

        return FakeResponse.Json(200, System.Text.Json.JsonSerializer.Serialize(new { name = Leaf(path), path_display = path, size = body.Length }));
    }

    private FakeResponse Download(JsonElement arg)
    {
        var path = arg.GetProperty("path").GetString()!;
        lock (State)
        {
            return _files.TryGetValue(path, out var bytes)
                ? FakeResponse.Bytes(200, bytes)
                : NotFound();
        }
    }

    private FakeResponse DeleteV2(JsonElement body)
    {
        var path = body.GetProperty("path").GetString()!;
        lock (State)
        {
            if (!_files.Remove(path))
            {
                return NotFound();
            }
        }

        return FakeResponse.Json(200, "{\"metadata\":{\".tag\":\"file\"}}");
    }

    private FakeResponse Start(byte[] body)
    {
        lock (State)
        {
            var sid = "dbx-session-" + (++_nextSession);
            _sessions[sid] = body;
            return FakeResponse.Json(200, System.Text.Json.JsonSerializer.Serialize(new { session_id = sid }));
        }
    }

    private FakeResponse Append(JsonElement arg, byte[] body)
    {
        var cursor = arg.GetProperty("cursor");
        var sid = cursor.GetProperty("session_id").GetString()!;
        var offset = cursor.GetProperty("offset").GetInt64();
        lock (State)
        {
            if (!_sessions.TryGetValue(sid, out var data))
            {
                return FakeResponse.Json(409, "{\"error_summary\":\"closed_session/..\",\"error\":{\".tag\":\"closed_session\"}}");
            }

            if (offset != data.Length)
            {
                return IncorrectOffset(data.Length);
            }

            _sessions[sid] = Concat(data, body);
            return FakeResponse.Empty(200);
        }
    }

    private FakeResponse Finish(JsonElement arg, byte[] body)
    {
        var cursor = arg.GetProperty("cursor");
        var sid = cursor.GetProperty("session_id").GetString()!;
        var offset = cursor.GetProperty("offset").GetInt64();
        var path = arg.GetProperty("commit").GetProperty("path").GetString()!;
        lock (State)
        {
            if (!_sessions.TryGetValue(sid, out var data))
            {
                return FakeResponse.Json(409, "{\"error_summary\":\"closed_session/..\",\"error\":{\".tag\":\"closed_session\"}}");
            }

            if (offset != data.Length)
            {
                return IncorrectOffset(data.Length);
            }

            var full = Concat(data, body);
            _sessions.Remove(sid);
            _files[path] = full;
            _folders.Add(ParentOf(path));
            return FakeResponse.Json(200, System.Text.Json.JsonSerializer.Serialize(new { name = Leaf(path), path_display = path, size = full.Length }));
        }
    }

    private static FakeResponse IncorrectOffset(long correct) => FakeResponse.Json(409,
        "{\"error_summary\":\"incorrect_offset/..\",\"error\":{\".tag\":\"incorrect_offset\",\"correct_offset\":" + correct + "}}");

    private static FakeResponse NotFound() => FakeResponse.Json(409,
        "{\"error_summary\":\"path/not_found/..\",\"error\":{\".tag\":\"path\",\"path\":{\".tag\":\"not_found\"}}}");

    private static JsonElement Arg(FakeRequest r) =>
        JsonDocument.Parse(r.Header("Dropbox-API-Arg") ?? throw new InvalidOperationException("Dropbox-API-Arg is missing.")).RootElement.Clone();

    private static string ParentOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash <= 0 ? string.Empty : path[..slash];
    }

    private static string Leaf(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var result = new byte[a.Length + b.Length];
        a.CopyTo(result, 0);
        b.CopyTo(result, a.Length);
        return result;
    }
}

/// <summary>Cloud test helpers: fixed token provider, fake clock and HTTP client without a proxy.</summary>
internal sealed class FixedToken : IOAuthTokenProvider
{
    public FixedToken(string token = "at-valid") => Token = token;

    public string Token { get; }

    public int RefreshCalls { get; private set; }

    public Task<string> GetAccessTokenAsync(CancellationToken ct = default) => Task.FromResult(Token);

    public Task<string> RefreshAccessTokenAsync(CancellationToken ct = default)
    {
        RefreshCalls++;
        return Task.FromResult(Token);
    }
}

/// <summary>Collects progress for assertions in tests.</summary>
internal sealed class RecordingProgress : IProgress<TransferProgress>
{
    private readonly List<TransferProgress> _items = new();

    public void Report(TransferProgress value)
    {
        lock (_items)
        {
            _items.Add(value);
        }
    }

    public IReadOnlyList<TransferProgress> Items
    {
        get
        {
            lock (_items)
            {
                return _items.ToList();
            }
        }
    }
}

internal static class CloudTest
{
    public static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-31T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    public static HttpClient NewHttp() => new(new HttpClientHandler { UseProxy = false });

    /// <summary>Drive resumable upload chunk (not a status query).</summary>
    public static bool IsDataPut(FakeRequest r) =>
        r.Method == "PUT"
        && r.Path.StartsWith("/upload/session/", StringComparison.Ordinal)
        && !(r.Header("Content-Range") ?? string.Empty).Contains('*', StringComparison.Ordinal);

    /// <summary>Start offset of the Content-Range range "bytes start-end/total".</summary>
    public static long RangeStart(FakeRequest r)
    {
        var spec = (r.Header("Content-Range") ?? string.Empty)["bytes ".Length..];
        return long.Parse(spec[..spec.IndexOf('-')], System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Dropbox cursor offset from Dropbox-API-Arg.</summary>
    public static long DropboxArgOffset(FakeRequest r)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(r.Header("Dropbox-API-Arg") ?? "{}");
        return doc.RootElement.GetProperty("cursor").GetProperty("offset").GetInt64();
    }

    public static byte[] RandomBytes(int length, int seed)
    {
        var random = new Random(seed);
        var bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }

    /// <summary>Build a ZIP in memory with specific entries (UTF-8 strings).</summary>
    public static byte[] ZipBytes(params (string Name, byte[] Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = zip.CreateEntry(name);
                using var stream = entry.Open();
                stream.Write(content, 0, content.Length);
            }
        }

        return ms.ToArray();
    }
}
