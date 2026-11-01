using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace WinIDM.Backup;

/// <summary>
/// Custom REST server destination. API contract (relative to the base URL):
/// <list type="bullet">
/// <item><description>PUT backups/{name} - upload a binary body (application/octet-stream). 200/201/204.</description></item>
/// <item><description>GET backups - JSON array [{ "name", "modifiedUtc", "sizeBytes" }].</description></item>
/// <item><description>GET backups/{name} - download binary. 404 if missing.</description></item>
/// <item><description>DELETE backups/{name} - delete. 404 is treated as already deleted.</description></item>
/// </list>
/// An optional Bearer token is sent in the Authorization header and is never logged.
/// </summary>
public sealed class RestServerDestination : IBackupDestination
{
    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private readonly string? _bearerToken;

    public RestServerDestination(HttpClient http, Uri baseUri, string? bearerToken = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        ArgumentNullException.ThrowIfNull(baseUri);
        if (!baseUri.IsAbsoluteUri || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("The base URL must be an absolute http or https URL.", nameof(baseUri));
        }

        _baseUri = new Uri(baseUri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/");
        _bearerToken = string.IsNullOrWhiteSpace(bearerToken) ? null : bearerToken;
    }

    public string DisplayName => $"REST server: {_baseUri.Host}";

    public async Task UploadAsync(string localPath, string remoteName, CancellationToken ct = default)
    {
        var uri = ItemUri(remoteName);
        await using var input = File.OpenRead(localPath);
        using var content = new StreamContent(input);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await SendAsync(HttpMethod.Put, uri, content, ct);
        EnsureSuccess(response, "upload");
    }

    public async Task<IReadOnlyList<BackupEntry>> ListAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, new Uri(_baseUri, "backups"), null, ct);
        EnsureSuccess(response, "list");

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = ParseArray(json);
        var entries = new List<BackupEntry>();
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            var name = element.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (!BackupNames.IsSafe(name))
            {
                continue;
            }

            var modified = element.TryGetProperty("modifiedUtc", out var m)
                && DateTimeOffset.TryParse(m.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                    ? parsed
                    : DateTimeOffset.MinValue;
            var size = element.TryGetProperty("sizeBytes", out var s) && s.TryGetInt64(out var sizeValue) ? sizeValue : 0;
            entries.Add(new BackupEntry(name!, modified, size));
        }

        return entries.OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
    }

    public async Task DownloadAsync(string remoteName, string localPath, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, ItemUri(remoteName), null, ct);
        EnsureSuccess(response, "download");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await DestinationIo.SaveStreamAtomicAsync(stream, localPath, ct);
    }

    public async Task DeleteAsync(string remoteName, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Delete, ItemUri(remoteName), null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        EnsureSuccess(response, "delete");
    }

    private Uri ItemUri(string remoteName) =>
        new(_baseUri, "backups/" + Uri.EscapeDataString(BackupNames.Validate(remoteName)));

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri uri, HttpContent? content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, uri) { Content = content };
        if (_bearerToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearerToken);
        }

        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new BackupTransportException("Cannot reach the backup server.", ex);
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response, string operation)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var status = (int)response.StatusCode;
        throw new BackupTransportException($"The {operation} operation failed (HTTP {status}).", status);
    }

    private static JsonDocument ParseArray(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new BackupTransportException("The server's list response is not valid JSON.", ex);
        }

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            doc.Dispose();
            throw new BackupTransportException("The server's list response must be a JSON array.");
        }

        return doc;
    }
}
