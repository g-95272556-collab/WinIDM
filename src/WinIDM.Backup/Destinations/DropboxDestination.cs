using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace WinIDM.Backup;

/// <summary>Dropbox destination configuration. The defaults point at the real Dropbox API.</summary>
public sealed class DropboxOptions
{
    public Uri ApiBase { get; init; } = new("https://api.dropboxapi.com/2/");

    public Uri ContentBase { get; init; } = new("https://content.dropboxapi.com/2/");

    /// <summary>Backup folder inside Dropbox. An empty string means the root.</summary>
    public string FolderPath { get; init; } = DropboxDestination.FolderPath;

    /// <summary>Files up to this size are uploaded in a single request; larger ones use upload_session.</summary>
    public long SingleUploadMaxBytes { get; init; } = 128L * 1024 * 1024;

    public int SessionChunkBytes { get; init; } = 8 * 1024 * 1024;

    public int ListPageSize { get; init; } = 2000;

    public CloudOptions Cloud { get; init; } = new();

    public void Validate()
    {
        if (!ApiBase.IsAbsoluteUri || !ContentBase.IsAbsoluteUri)
        {
            throw new ArgumentException("The Dropbox base URL must be absolute.");
        }

        if (SessionChunkBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SessionChunkBytes), "Chunk size must be positive.");
        }

        if (SingleUploadMaxBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SingleUploadMaxBytes));
        }

        if (ListPageSize is < 1 or > 2000)
        {
            throw new ArgumentOutOfRangeException(nameof(ListPageSize), "Page size must be between 1 and 2000.");
        }

        Cloud.Validate();
    }
}

/// <summary>
/// Dropbox API v2 destination. Small files use files/upload; large files use upload_session (start, append_v2, finish).
/// If the append response is lost but the bytes were received, the server returns 409 incorrect_offset with
/// correct_offset and the upload resumes from there. Listing follows list_folder/continue.
/// </summary>
public sealed class DropboxDestination : IProgressBackupDestination
{
    public const string FolderPath = "/WinIDM Backups";

    private static readonly JsonSerializerOptions ArgJson = new()
    {
        // Dropbox-API-Arg must be ASCII only: non-ASCII characters are escaped as \uXXXX.
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.BasicLatin),
    };

    private readonly IOAuthTokenProvider _auth;
    private readonly HttpClient _http;
    private readonly DropboxOptions _options;
    private readonly string _api;
    private readonly string _content;
    private readonly string _folder;

    public DropboxDestination(IOAuthTokenProvider auth, HttpClient http, DropboxOptions? options = null)
    {
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? new DropboxOptions();
        _options.Validate();
        _api = WithTrailingSlash(_options.ApiBase);
        _content = WithTrailingSlash(_options.ContentBase);
        _folder = NormalizeFolder(_options.FolderPath);
    }

    public string DisplayName => "Dropbox";

    public Task UploadAsync(string localPath, string remoteName, CancellationToken ct = default) =>
        UploadAsync(localPath, remoteName, null, ct);

    public async Task UploadAsync(string localPath, string remoteName, IProgress<TransferProgress>? progress, CancellationToken ct = default)
    {
        BackupNames.Validate(remoteName);
        var path = PathOf(remoteName);
        var total = new FileInfo(localPath).Length;

        if (total <= _options.SingleUploadMaxBytes)
        {
            var arg = ApiArg(new { path, mode = "overwrite", autorename = false, mute = true });
            using var response = await CloudHttp.SendAsync(_http, _auth, _options.Cloud, () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, _content + "files/upload")
                {
                    Content = FileContent(localPath, total, progress),
                };
                request.Headers.Add("Dropbox-API-Arg", arg);
                return request;
            }, ct);
            CloudHttp.EnsureSuccess(response, "upload");
            return;
        }

        await UploadSessionAsync(localPath, path, total, progress, ct);
    }

    public async Task<IReadOnlyList<BackupEntry>> ListAsync(CancellationToken ct = default)
    {
        var entries = new List<BackupEntry>();
        var current = await PostJsonAsync(_api + "files/list_folder",
            ApiJson(new { path = ApiFolder, recursive = false, include_deleted = false, limit = _options.ListPageSize }), ct);
        if (current is null)
        {
            return entries;
        }

        try
        {
            while (true)
            {
                CollectEntries(current.RootElement, entries);
                var hasMore = current.RootElement.TryGetProperty("has_more", out var hm) && hm.ValueKind == JsonValueKind.True;
                if (!hasMore || !current.RootElement.TryGetProperty("cursor", out var cursor))
                {
                    break;
                }

                var next = await PostJsonAsync(_api + "files/list_folder/continue",
                    ApiJson(new { cursor = cursor.GetString() }), ct);
                if (next is null)
                {
                    break;
                }

                current.Dispose();
                current = next;
            }
        }
        finally
        {
            current.Dispose();
        }

        return entries.OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
    }

    public Task DownloadAsync(string remoteName, string localPath, CancellationToken ct = default) =>
        DownloadAsync(remoteName, localPath, null, ct);

    public async Task DownloadAsync(string remoteName, string localPath, IProgress<TransferProgress>? progress, CancellationToken ct = default)
    {
        BackupNames.Validate(remoteName);
        var arg = ApiArg(new { path = PathOf(remoteName) });
        using var response = await CloudHttp.SendAsync(_http, _auth, _options.Cloud, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, _content + "files/download");
            request.Headers.Add("Dropbox-API-Arg", arg);
            return request;
        }, ct, HttpCompletionOption.ResponseHeadersRead);

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (body.Contains("not_found", StringComparison.Ordinal))
            {
                throw new FileNotFoundException("Backup file not found on Dropbox.", remoteName);
            }
        }

        CloudHttp.EnsureSuccess(response, "download");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await DestinationIo.SaveStreamAtomicAsync(stream, localPath, progress, response.Content.Headers.ContentLength ?? -1, ct);
    }

    public async Task DeleteAsync(string remoteName, CancellationToken ct = default)
    {
        BackupNames.Validate(remoteName);
        var body = ApiJson(new { path = PathOf(remoteName) });
        using var response = await CloudHttp.SendAsync(_http, _auth, _options.Cloud, () =>
            new HttpRequestMessage(HttpMethod.Post, _api + "files/delete_v2")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            }, ct);

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            // The file is already gone (path_lookup/not_found): treat it as deleted.
            var text = await response.Content.ReadAsStringAsync(ct);
            if (text.Contains("not_found", StringComparison.Ordinal))
            {
                return;
            }
        }

        CloudHttp.EnsureSuccess(response, "delete");
    }

    private async Task UploadSessionAsync(string localPath, string path, long total, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var chunk = _options.SessionChunkBytes;
        var firstLength = (int)Math.Min(chunk, total);

        using var started = await SendChunkAsync("files/upload_session/start", ApiArg(new { close = false }),
            localPath, 0, firstLength, ct);
        CloudHttp.EnsureSuccess(started, "upload");
        var sessionId = await ReadStringPropertyAsync(started, "session_id", ct)
            ?? throw new BackupTransportException("The server did not return an upload session id.");

        long offset = firstLength;
        progress?.Report(new TransferProgress(offset, total));
        var resyncs = 0;

        while (true)
        {
            var remaining = total - offset;
            var last = remaining <= chunk;
            var length = (int)Math.Min(chunk, remaining);
            var cursor = new { session_id = sessionId, offset };

            var endpoint = last ? "files/upload_session/finish" : "files/upload_session/append_v2";
            var arg = last
                ? ApiArg(new { cursor, commit = new { path, mode = "overwrite", autorename = false, mute = true } })
                : ApiArg(new { cursor, close = false });

            using var response = await SendChunkAsync(endpoint, arg, localPath, offset, length, ct);
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                if (TryReadCorrectOffset(body, out var correct))
                {
                    if (correct < 0 || correct > total || ++resyncs > _options.Cloud.Retry.MaxAttempts)
                    {
                        throw new BackupTransportException("The upload session offset does not match the server.");
                    }

                    offset = correct;
                    progress?.Report(new TransferProgress(offset, total));
                    continue;
                }

                throw new BackupTransportException($"Upload failed (HTTP 409): {SummaryOf(body)}");
            }

            CloudHttp.EnsureSuccess(response, "upload");
            if (last)
            {
                progress?.Report(new TransferProgress(total, total));
                return;
            }

            offset += length;
            progress?.Report(new TransferProgress(offset, total));
        }
    }

    private Task<HttpResponseMessage> SendChunkAsync(string endpoint, string arg, string localPath, long start, int length, CancellationToken ct) =>
        CloudHttp.SendAsync(_http, _auth, _options.Cloud, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, _content + endpoint)
            {
                Content = new ByteArrayContent(DestinationIo.ReadRange(localPath, start, length)),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            request.Headers.Add("Dropbox-API-Arg", arg);
            return request;
        }, ct);

    private static HttpContent FileContent(string localPath, long total, IProgress<TransferProgress>? progress)
    {
        var content = new StreamContent(new ProgressReadStream(File.OpenRead(localPath), progress, total));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentLength = total;
        return content;
    }

    /// <summary>Returns null if the folder does not exist yet (no backups yet).</summary>
    private async Task<JsonDocument?> PostJsonAsync(string url, string json, CancellationToken ct)
    {
        using var response = await CloudHttp.SendAsync(_http, _auth, _options.Cloud,
            () => new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            }, ct);

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (body.Contains("not_found", StringComparison.Ordinal))
            {
                return null;
            }
        }

        CloudHttp.EnsureSuccess(response, "list");
        return await CloudHttp.ReadJsonAsync(response, ct);
    }

    private static async Task<string?> ReadStringPropertyAsync(HttpResponseMessage response, string property, CancellationToken ct)
    {
        using var doc = await CloudHttp.ReadJsonAsync(response, ct);
        return doc.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool TryReadCorrectOffset(string body, out long offset)
    {
        offset = 0;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("error", out var error)
                && error.TryGetProperty("correct_offset", out var value)
                && value.TryGetInt64(out offset);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string SummaryOf(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error_summary", out var summary) && summary.ValueKind == JsonValueKind.String)
            {
                var text = summary.GetString() ?? string.Empty;
                return text.Length > 160 ? text[..160] : text;
            }
        }
        catch (JsonException)
        {
            // Ignore non-JSON bodies.
        }

        return "unknown error";
    }

    private static void CollectEntries(JsonElement root, List<BackupEntry> entries)
    {
        if (!root.TryGetProperty("entries", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in list.EnumerateArray())
        {
            if (!item.TryGetProperty(".tag", out var tag) || tag.GetString() != "file")
            {
                continue;
            }

            var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (!BackupNames.IsSafe(name))
            {
                continue;
            }

            var modified = item.TryGetProperty("server_modified", out var m)
                && DateTimeOffset.TryParse(m.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                    ? parsed
                    : DateTimeOffset.MinValue;
            var size = item.TryGetProperty("size", out var s) && s.TryGetInt64(out var sizeValue) ? sizeValue : 0;
            entries.Add(new BackupEntry(name!, modified, size));
        }
    }

    private string ApiFolder => _folder;

    private string PathOf(string remoteName) => _folder + "/" + BackupNames.Validate(remoteName);

    private static string NormalizeFolder(string folder)
    {
        var trimmed = (folder ?? string.Empty).Trim().TrimEnd('/');
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        return trimmed.StartsWith('/') ? trimmed : "/" + trimmed;
    }

    private static string WithTrailingSlash(Uri uri) => uri.ToString().TrimEnd('/') + "/";

    private static string ApiJson(object value) => JsonSerializer.Serialize(value);

    private static string ApiArg(object value) => JsonSerializer.Serialize(value, ArgJson);
}
