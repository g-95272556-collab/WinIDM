using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace WinIDM.Backup;

/// <summary>Location of backup files in Google Drive.</summary>
public enum GoogleDriveLocation
{
    /// <summary>Directly in the user's Drive (drive.file scope).</summary>
    Root,

    /// <summary>Hidden folder appDataFolder (requires the drive.appdata scope).</summary>
    AppDataFolder,

    /// <summary>Named folder at the root; created if missing during upload.</summary>
    NamedFolder,
}

/// <summary>Google Drive destination configuration. The defaults point at the real Google API.</summary>
public sealed class GoogleDriveOptions
{
    /// <summary>A resumable chunk size must be a multiple of 256 KiB per Drive documentation.</summary>
    public const int ChunkAlignment = 256 * 1024;

    public Uri ApiBase { get; init; } = new("https://www.googleapis.com/drive/v3/");

    public Uri UploadBase { get; init; } = new("https://www.googleapis.com/upload/drive/v3/");

    public GoogleDriveLocation Location { get; init; } = GoogleDriveLocation.Root;

    public string FolderName { get; init; } = "WinIDM Backups";

    /// <summary>Files up to this size are uploaded in a single multipart request; larger ones use resumable.</summary>
    public long MultipartMaxBytes { get; init; } = 5L * 1024 * 1024;

    public int ChunkBytes { get; init; } = 8 * 1024 * 1024;

    public int PageSize { get; init; } = 1000;

    public CloudOptions Cloud { get; init; } = new();

    public void Validate()
    {
        if (!ApiBase.IsAbsoluteUri || !UploadBase.IsAbsoluteUri)
        {
            throw new ArgumentException("The Drive base URL must be absolute.");
        }

        if (ChunkBytes <= 0 || ChunkBytes % ChunkAlignment != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ChunkBytes), "Chunk size must be a multiple of 256 KiB.");
        }

        if (PageSize is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(PageSize), "Page size must be between 1 and 1000.");
        }

        if (MultipartMaxBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MultipartMaxBytes));
        }

        if (Location == GoogleDriveLocation.NamedFolder
            && (string.IsNullOrWhiteSpace(FolderName) || FolderName.Contains('\'') || FolderName.Contains('\\')))
        {
            throw new ArgumentException("Invalid Drive folder name (must not contain ' or \\).", nameof(FolderName));
        }

        Cloud.Validate();
    }
}

/// <summary>
/// Google Drive API v3 destination. Small files use multipart; large files use a resumable session with chunks
/// and recover by querying the bytes received (Content-Range "bytes */total") after an interruption.
/// Listing uses <c>q</c> with <c>nextPageToken</c> paging.
/// </summary>
public sealed class GoogleDriveDestination : IProgressBackupDestination
{
    private const string FolderMime = "application/vnd.google-apps.folder";
    private const string BackupQuery = "name contains 'winidm-backup-' and trashed = false";

    private readonly IOAuthTokenProvider _auth;
    private readonly HttpClient _http;
    private readonly GoogleDriveOptions _options;
    private readonly string _api;
    private readonly string _upload;

    public GoogleDriveDestination(IOAuthTokenProvider auth, HttpClient http, GoogleDriveOptions? options = null)
    {
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? new GoogleDriveOptions();
        _options.Validate();
        _api = WithTrailingSlash(_options.ApiBase);
        _upload = WithTrailingSlash(_options.UploadBase);
    }

    public string DisplayName => _options.Location switch
    {
        GoogleDriveLocation.AppDataFolder => "Google Drive (appDataFolder)",
        GoogleDriveLocation.NamedFolder => $"Google Drive (folder {_options.FolderName})",
        _ => "Google Drive",
    };

    public Task UploadAsync(string localPath, string remoteName, CancellationToken ct = default) =>
        UploadAsync(localPath, remoteName, null, ct);

    public async Task UploadAsync(string localPath, string remoteName, IProgress<TransferProgress>? progress, CancellationToken ct = default)
    {
        BackupNames.Validate(remoteName);
        var total = new FileInfo(localPath).Length;
        var folderId = await FolderIdAsync(create: true, ct);
        var existingId = await FindFileIdAsync(remoteName, folderId, ct);
        var parents = existingId is null ? ParentsFor(folderId) : null;

        if (total <= _options.MultipartMaxBytes)
        {
            await UploadMultipartAsync(localPath, remoteName, total, existingId, parents, progress, ct);
        }
        else
        {
            await UploadResumableAsync(localPath, remoteName, total, existingId, parents, progress, ct);
        }
    }

    public async Task<IReadOnlyList<BackupEntry>> ListAsync(CancellationToken ct = default)
    {
        var folderId = await FolderIdAsync(create: false, ct);
        if (FolderMissing(folderId))
        {
            return Array.Empty<BackupEntry>();
        }

        var entries = new List<BackupEntry>();
        string? pageToken = null;
        do
        {
            var clauses = new List<string> { BackupQuery };
            AddLocationClause(clauses, folderId);
            var url = _api + "files?q=" + Escape(string.Join(" and ", clauses))
                + "&fields=" + Escape("nextPageToken,files(id,name,size,modifiedTime)")
                + "&pageSize=" + _options.PageSize.ToString(CultureInfo.InvariantCulture)
                + SpacesParam()
                + (pageToken is null ? string.Empty : "&pageToken=" + Escape(pageToken));

            using var doc = await GetJsonAsync(url, ct);
            if (doc.RootElement.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
            {
                foreach (var file in files.EnumerateArray())
                {
                    var name = file.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (!BackupNames.IsSafe(name))
                    {
                        continue;
                    }

                    var modified = file.TryGetProperty("modifiedTime", out var m)
                        && DateTimeOffset.TryParse(m.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                            ? parsed
                            : DateTimeOffset.MinValue;
                    var size = file.TryGetProperty("size", out var s) && long.TryParse(s.GetString(), out var sizeValue) ? sizeValue : 0;
                    entries.Add(new BackupEntry(name!, modified, size));
                }
            }

            pageToken = doc.RootElement.TryGetProperty("nextPageToken", out var t) ? t.GetString() : null;
        }
        while (pageToken is not null);

        return entries.OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
    }

    public Task DownloadAsync(string remoteName, string localPath, CancellationToken ct = default) =>
        DownloadAsync(remoteName, localPath, null, ct);

    public async Task DownloadAsync(string remoteName, string localPath, IProgress<TransferProgress>? progress, CancellationToken ct = default)
    {
        BackupNames.Validate(remoteName);
        var folderId = await FolderIdAsync(create: false, ct);
        var id = FolderMissing(folderId) ? null : await FindFileIdAsync(remoteName, folderId, ct);
        if (id is null)
        {
            throw new FileNotFoundException("Backup file not found on Google Drive.", remoteName);
        }

        using var response = await CloudHttp.SendAsync(_http, _auth, _options.Cloud,
            () => new HttpRequestMessage(HttpMethod.Get, _api + "files/" + Escape(id) + "?alt=media"),
            ct, HttpCompletionOption.ResponseHeadersRead);
        CloudHttp.EnsureSuccess(response, "download");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await DestinationIo.SaveStreamAtomicAsync(stream, localPath, progress, response.Content.Headers.ContentLength ?? -1, ct);
    }

    public async Task DeleteAsync(string remoteName, CancellationToken ct = default)
    {
        BackupNames.Validate(remoteName);
        var folderId = await FolderIdAsync(create: false, ct);
        if (FolderMissing(folderId))
        {
            return;
        }

        var id = await FindFileIdAsync(remoteName, folderId, ct);
        if (id is null)
        {
            return;
        }

        using var response = await CloudHttp.SendAsync(_http, _auth, _options.Cloud,
            () => new HttpRequestMessage(HttpMethod.Delete, _api + "files/" + Escape(id)), ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        CloudHttp.EnsureSuccess(response, "delete");
    }

    private async Task UploadMultipartAsync(
        string localPath, string name, long total, string? existingId, string[]? parents,
        IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var metadata = BuildMetadata(name, parents);
        var isNew = existingId is null;
        var url = isNew
            ? _upload + "files?uploadType=multipart&fields=id"
            : _upload + "files/" + Escape(existingId!) + "?uploadType=multipart&fields=id";
        var method = isNew ? HttpMethod.Post : HttpMethod.Patch;

        using var response = await CloudHttp.SendAsync(_http, _auth, _options.Cloud, () =>
        {
            var multipart = new MultipartContent("related");
            multipart.Add(new StringContent(metadata, Encoding.UTF8, "application/json"));
            var media = new StreamContent(new ProgressReadStream(File.OpenRead(localPath), progress, total));
            media.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            media.Headers.ContentLength = total;
            multipart.Add(media);
            return new HttpRequestMessage(method, url) { Content = multipart };
        }, ct);
        CloudHttp.EnsureSuccess(response, "upload");
    }

    private async Task UploadResumableAsync(
        string localPath, string name, long total, string? existingId, string[]? parents,
        IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var isNew = existingId is null;
        var metadata = BuildMetadata(name, isNew ? parents : null);
        var url = isNew
            ? _upload + "files?uploadType=resumable&fields=id"
            : _upload + "files/" + Escape(existingId!) + "?uploadType=resumable&fields=id";
        var method = isNew ? HttpMethod.Post : HttpMethod.Patch;

        using var start = await CloudHttp.SendAsync(_http, _auth, _options.Cloud, () =>
        {
            var request = new HttpRequestMessage(method, url)
            {
                Content = new StringContent(metadata, Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("X-Upload-Content-Type", "application/octet-stream");
            request.Headers.Add("X-Upload-Content-Length", total.ToString(CultureInfo.InvariantCulture));
            return request;
        }, ct);
        CloudHttp.EnsureSuccess(start, "upload");

        var session = start.Headers.Location
            ?? throw new BackupTransportException("The server did not return an upload session.");
        if (!session.IsAbsoluteUri)
        {
            session = new Uri(new Uri(_upload), session);
        }

        await UploadChunksAsync(session, localPath, total, progress, ct);
    }

    /// <summary>
    /// Send chunks in order. After a transient error (beyond the normal retries), ask the server
    /// how many bytes it received and resume from there. The recovery limit is the retry policy.
    /// </summary>
    private async Task UploadChunksAsync(Uri session, string localPath, long total, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        long offset = 0;
        var recoveries = 0;
        var queryFirst = false;

        while (true)
        {
            try
            {
                if (queryFirst)
                {
                    queryFirst = false;
                    var status = await QueryStatusAsync(session, total, ct);
                    if (status.Complete)
                    {
                        progress?.Report(new TransferProgress(total, total));
                        return;
                    }

                    if (status.Offset >= total)
                    {
                        throw new BackupTransportException("The server did not confirm a complete upload.");
                    }

                    offset = status.Offset;
                }

                while (offset < total)
                {
                    var start = offset;
                    var length = (int)Math.Min(_options.ChunkBytes, total - start);
                    var chunk = DestinationIo.ReadRange(localPath, start, length);

                    using var response = await CloudHttp.SendAsync(_http, _auth, _options.Cloud,
                        () => ChunkRequest(session, chunk, start, total), ct);

                    if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
                    {
                        progress?.Report(new TransferProgress(total, total));
                        return;
                    }

                    if ((int)response.StatusCode == 308)
                    {
                        offset = NextOffset(response);
                        progress?.Report(new TransferProgress(offset, total));
                        continue;
                    }

                    CloudHttp.EnsureSuccess(response, "upload");
                    return;
                }

                // All bytes were sent but no completion response arrived: confirm with the server.
                queryFirst = true;
            }
            catch (BackupTransportException ex) when (IsRecoverable(ex) && recoveries < _options.Cloud.Retry.MaxAttempts)
            {
                recoveries++;
                queryFirst = true;
            }
        }
    }

    private static HttpRequestMessage ChunkRequest(Uri session, byte[] chunk, long start, long total)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, session) { Content = new ByteArrayContent(chunk) };
        request.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, start + chunk.Length - 1, total);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return request;
    }

    private async Task<(bool Complete, long Offset)> QueryStatusAsync(Uri session, long total, CancellationToken ct)
    {
        using var response = await CloudHttp.SendAsync(_http, _auth, _options.Cloud, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Put, session) { Content = new ByteArrayContent(Array.Empty<byte>()) };
            request.Content.Headers.ContentRange = new ContentRangeHeaderValue(total);
            return request;
        }, ct);

        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
        {
            return (true, total);
        }

        if ((int)response.StatusCode == 308)
        {
            return (false, NextOffset(response));
        }

        CloudHttp.EnsureSuccess(response, "upload");
        return (false, 0);
    }

    /// <summary>Next byte the server has accepted (Range: bytes=0-N). No Range means no bytes were accepted.</summary>
    private static long NextOffset(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Range", out var values)
            && values.FirstOrDefault() is { } range
            && range.StartsWith("bytes=0-", StringComparison.Ordinal)
            && long.TryParse(range.AsSpan("bytes=0-".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var last))
        {
            return last + 1;
        }

        return 0;
    }

    private static bool IsRecoverable(BackupTransportException ex) =>
        ex.StatusCode is null or 429 || ex.StatusCode >= 500;

    private async Task<string?> FolderIdAsync(bool create, CancellationToken ct)
    {
        if (_options.Location != GoogleDriveLocation.NamedFolder)
        {
            return null;
        }

        var query = $"name = '{_options.FolderName}' and mimeType = '{FolderMime}' and trashed = false and 'root' in parents";
        var url = _api + "files?q=" + Escape(query) + "&fields=" + Escape("files(id)") + "&pageSize=1";
        using var doc = await GetJsonAsync(url, ct);
        var id = FirstId(doc.RootElement);
        if (id is not null || !create)
        {
            return id;
        }

        var body = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["name"] = _options.FolderName,
            ["mimeType"] = FolderMime,
        });
        using var response = await CloudHttp.SendAsync(_http, _auth, _options.Cloud,
            () => new HttpRequestMessage(HttpMethod.Post, _api + "files?fields=id")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            }, ct);
        CloudHttp.EnsureSuccess(response, "create folder");
        using var created = await CloudHttp.ReadJsonAsync(response, ct);
        return FirstId(created.RootElement, singleObject: true)
            ?? throw new BackupTransportException("The server did not return a folder id.");
    }

    private async Task<string?> FindFileIdAsync(string remoteName, string? folderId, CancellationToken ct)
    {
        var clauses = new List<string> { $"name = '{remoteName}'", "trashed = false" };
        AddLocationClause(clauses, folderId);
        var url = _api + "files?q=" + Escape(string.Join(" and ", clauses))
            + "&fields=" + Escape("files(id)") + "&pageSize=1" + SpacesParam();
        using var doc = await GetJsonAsync(url, ct);
        return FirstId(doc.RootElement);
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using var response = await CloudHttp.SendAsync(_http, _auth, _options.Cloud,
            () => new HttpRequestMessage(HttpMethod.Get, url), ct);
        CloudHttp.EnsureSuccess(response, "list");
        return await CloudHttp.ReadJsonAsync(response, ct);
    }

    private void AddLocationClause(List<string> clauses, string? folderId)
    {
        if (_options.Location == GoogleDriveLocation.NamedFolder && folderId is not null)
        {
            clauses.Add($"'{folderId}' in parents");
        }
    }

    private string SpacesParam() =>
        _options.Location == GoogleDriveLocation.AppDataFolder ? "&spaces=appDataFolder" : string.Empty;

    private bool FolderMissing(string? folderId) =>
        _options.Location == GoogleDriveLocation.NamedFolder && folderId is null;

    private string[]? ParentsFor(string? folderId) => _options.Location switch
    {
        GoogleDriveLocation.AppDataFolder => new[] { "appDataFolder" },
        GoogleDriveLocation.NamedFolder when folderId is not null => new[] { folderId },
        _ => null,
    };

    private static string BuildMetadata(string name, string[]? parents)
    {
        var metadata = new Dictionary<string, object> { ["name"] = name };
        if (parents is { Length: > 0 })
        {
            metadata["parents"] = parents;
        }

        return JsonSerializer.Serialize(metadata);
    }

    private static string? FirstId(JsonElement root, bool singleObject = false)
    {
        if (singleObject)
        {
            return root.TryGetProperty("id", out var single) ? single.GetString() : null;
        }

        if (root.TryGetProperty("files", out var files)
            && files.ValueKind == JsonValueKind.Array
            && files.GetArrayLength() > 0
            && files[0].TryGetProperty("id", out var id))
        {
            return id.GetString();
        }

        return null;
    }

    private static string WithTrailingSlash(Uri uri) => uri.ToString().TrimEnd('/') + "/";

    private static string Escape(string value) => Uri.EscapeDataString(value);
}
