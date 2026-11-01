using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinIDM.Core.Contracts;

namespace WinIDM.Browser;

/// <summary>
/// Public filter list catalog entry. This is DATA only: name, URL, and format. No list code or
/// content is bundled. A list is downloaded only after the user enables it and requests an
/// update (or through a scheduled update for lists that are already enabled).
/// </summary>
public sealed record FilterListSource(string Id, string Name, string Url, string Format, string Category);

/// <summary>State of one filter list (catalog or custom) as stored on disk.</summary>
public sealed record FilterListState(
    string Id,
    string Name,
    string Url,
    bool Enabled,
    bool IsCustom,
    string? ETag,
    DateTimeOffset? LastCheckedUtc,
    DateTimeOffset? LastUpdatedUtc,
    long CachedBytes,
    string? LastError);

public enum FilterListUpdateStatus
{
    /// <summary>The server answered 304; the local cache is still valid.</summary>
    NotModified,
    /// <summary>The list was downloaded and the cache rewritten.</summary>
    Updated,
    /// <summary>The update failed; the old cache is kept.</summary>
    Failed,
}

public sealed record FilterListUpdateResult(string Id, FilterListUpdateStatus Status, string? Error);

/// <summary>
/// Filter list manager: public catalog (data only), user custom lists, downloads with
/// ETag / If-None-Match, on-disk cache, and scheduled updates. No device identifiers or
/// cookies are sent; every request is a plain cookie-free GET.
/// </summary>
public sealed class FilterListManager : IDisposable
{
    public const long MaxListBytes = 16 * 1024 * 1024;

    private const string StateFileName = "filter-lists.json";

    /// <summary>
    /// Known public list catalog. Data only: name, reference URL, format, and category.
    /// URLs must be re-validated before release; they are not tested against the internet in this project.
    /// </summary>
    public static IReadOnlyList<FilterListSource> Catalog { get; } = new[]
    {
        new FilterListSource("easylist", "EasyList", "https://easylist.to/easylist/easylist.txt", "adblock", "Ads"),
        new FilterListSource("easyprivacy", "EasyPrivacy", "https://easylist.to/easylist/easyprivacy.txt", "adblock", "Privacy"),
        new FilterListSource("fanboy-annoyance", "Fanboy Annoyance", "https://easylist.to/easylist/fanboy-annoyance.txt", "adblock", "Annoyance"),
        new FilterListSource("ublock-filters", "uBlock Origin filters", "https://ublockorigin.github.io/uAssets/filters/filters.txt", "adblock", "Ads"),
        new FilterListSource("ublock-privacy", "uBlock Origin privacy", "https://ublockorigin.github.io/uAssets/filters/privacy.txt", "adblock", "Privacy"),
        new FilterListSource("ublock-badware", "uBlock Origin badware", "https://ublockorigin.github.io/uAssets/filters/badware.txt", "adblock", "Malware"),
        new FilterListSource("ublock-unbreak", "uBlock Origin unbreak", "https://ublockorigin.github.io/uAssets/filters/unbreak.txt", "adblock", "Fixes"),
        new FilterListSource("adguard-base", "AdGuard Base", "https://filters.adtidy.org/extension/ublock/filters/2.txt", "adblock", "Ads"),
        new FilterListSource("adguard-tracking", "AdGuard Tracking Protection", "https://filters.adtidy.org/extension/ublock/filters/3.txt", "adblock", "Privacy"),
        new FilterListSource("hagezi-pro", "Hagezi Pro", "https://raw.githubusercontent.com/hagezi/dns-blocklists/main/adblock/pro.txt", "adblock", "Ads"),
        new FilterListSource("stevenblack-hosts", "StevenBlack hosts", "https://raw.githubusercontent.com/StevenBlack/hosts/master/hosts", "hosts", "Ads"),
    };

    private readonly object _gate = new();
    private readonly SemaphoreSlim _io = new(1, 1);
    private readonly string _directory;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Dictionary<string, Record> _records = new(StringComparer.Ordinal);
    private bool _disposed;

    private sealed class Record
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
        public bool Enabled { get; set; }
        public bool IsCustom { get; set; }
        public string? ETag { get; set; }
        public DateTimeOffset? LastCheckedUtc { get; set; }
        public DateTimeOffset? LastUpdatedUtc { get; set; }
        public long CachedBytes { get; set; }
        public string? LastError { get; set; }
    }

    private sealed class StoreFile
    {
        public int Version { get; set; } = 1;
        public List<Record> Lists { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <param name="directory">Folder for list state and cache (created if missing).</param>
    /// <param name="httpClient">Optional HTTP client (for tests). Defaults to a cookie-free client.</param>
    public FilterListManager(string directory, HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);

        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient(new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
        })
        {
            Timeout = TimeSpan.FromSeconds(60),
        };

        foreach (var src in Catalog)
        {
            _records[src.Id] = new Record { Id = src.Id, Name = src.Name, Url = src.Url };
        }
        Load();
    }

    /// <summary>When true (the default), downloads must use HTTPS.</summary>
    public bool RequireHttps { get; set; } = true;

    /// <summary>All lists (catalog first, then custom) with their current state.</summary>
    public IReadOnlyList<FilterListState> Lists()
    {
        lock (_gate) return _records.Values.Select(ToState).ToList();
    }

    public FilterListState? Get(string id)
    {
        lock (_gate) return _records.TryGetValue(id, out var r) ? ToState(r) : null;
    }

    /// <summary>Enable or disable a list. Enabling downloads nothing.</summary>
    public void SetEnabled(string id, bool enabled)
    {
        lock (_gate)
        {
            var rec = Find(id);
            rec.Enabled = enabled;
            Save();
        }
    }

    /// <summary>Add a custom list (http/https URL). Returns the generated ID.</summary>
    public string AddCustomSource(string name, string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!HostUtil.TryGetHttpUri(url, out var uri)) throw new ArgumentException("URL must be http(s).", nameof(url));
        if (RequireHttps && uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Custom lists must use HTTPS.", nameof(url));

        string id = "custom-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)))[..16].ToLowerInvariant();
        lock (_gate)
        {
            if (!_records.ContainsKey(id))
            {
                _records[id] = new Record { Id = id, Name = name.Trim(), Url = uri.AbsoluteUri, IsCustom = true };
                Save();
            }
        }
        return id;
    }

    /// <summary>Remove a custom list and its cache. Catalog lists cannot be removed, only disabled.</summary>
    public void RemoveCustomSource(string id)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(id, out var rec) || !rec.IsCustom)
                throw new InvalidOperationException("Only custom lists can be removed.");
            _records.Remove(id);
            Save();
        }
        TryDelete(CachePath(id));
    }

    /// <summary>Re-download a list if it changed (ETag). Called on user request or by the scheduler.</summary>
    public async Task<FilterListUpdateResult> UpdateAsync(string id, CancellationToken ct = default)
    {
        string url;
        string? etag;
        bool hasCache;
        lock (_gate)
        {
            var rec = Find(id);
            url = rec.Url;
            etag = rec.ETag;
            hasCache = File.Exists(CachePath(id));
        }

        await _io.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!HostUtil.TryGetHttpUri(url, out var uri))
                return Fail(id, "Invalid list URL.");
            if (RequireHttps && uri.Scheme != Uri.UriSchemeHttps)
                return Fail(id, "The list must be downloaded over HTTPS.");

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", "WinIDM-FilterList/1.0");
            request.Headers.TryAddWithoutValidation("Accept", "text/plain, */*;q=0.1");
            if (etag is not null && hasCache) request.Headers.TryAddWithoutValidation("If-None-Match", etag);

            try
            {
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (response.StatusCode == System.Net.HttpStatusCode.NotModified && hasCache)
                {
                    MarkChecked(id, null);
                    return new FilterListUpdateResult(id, FilterListUpdateStatus.NotModified, null);
                }
                if (!response.IsSuccessStatusCode)
                    return Fail(id, $"Server responded {(int)response.StatusCode}.");

                byte[] data = await ReadLimitedAsync(response, MaxListBytes, ct).ConfigureAwait(false);
                ValidateText(data);
                string? newEtag = response.Headers.ETag?.ToString();

                WriteCacheAtomic(id, data);
                lock (_gate)
                {
                    var rec = Find(id);
                    rec.ETag = newEtag;
                    rec.LastCheckedUtc = DateTimeOffset.UtcNow;
                    rec.LastUpdatedUtc = rec.LastCheckedUtc;
                    rec.CachedBytes = data.Length;
                    rec.LastError = null;
                    Save();
                }
                return new FilterListUpdateResult(id, FilterListUpdateStatus.Updated, null);
            }
            catch (Exception ex) when ((ex is HttpRequestException or InvalidDataException or IOException or TaskCanceledException) && !ct.IsCancellationRequested)
            {
                return Fail(id, ex is TaskCanceledException ? "Download timed out." : ex.Message);
            }
        }
        finally
        {
            _io.Release();
        }
    }

    /// <summary>Update every enabled list not checked within <paramref name="interval"/>.</summary>
    public async Task<IReadOnlyList<FilterListUpdateResult>> UpdateDueAsync(DateTimeOffset now, TimeSpan interval, CancellationToken ct = default)
    {
        var due = new List<string>();
        lock (_gate)
        {
            foreach (var rec in _records.Values)
            {
                if (!rec.Enabled) continue;
                if (rec.LastCheckedUtc is null || now - rec.LastCheckedUtc.Value >= interval) due.Add(rec.Id);
            }
        }

        var results = new List<FilterListUpdateResult>(due.Count);
        foreach (string id in due)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await UpdateAsync(id, ct).ConfigureAwait(false));
        }
        return results;
    }

    /// <summary>
    /// Run the update scheduler until <paramref name="ct"/> is canceled. Every
    /// <paramref name="checkEvery"/> it checks the lists that are already enabled and updates the ones that are due.
    /// </summary>
    public async Task RunScheduleAsync(TimeSpan interval, TimeSpan checkEvery, CancellationToken ct)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        if (checkEvery <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(checkEvery));

        using var timer = new PeriodicTimer(checkEvery);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            await UpdateDueAsync(DateTimeOffset.UtcNow, interval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Load the cache of every enabled list into the filter. Returns the number of lists loaded.
    /// Lists without a cache are skipped.
    /// </summary>
    public int ApplyTo(IAdBlocker blocker)
    {
        ArgumentNullException.ThrowIfNull(blocker);
        var toLoad = new List<(string Name, string Id)>();
        lock (_gate)
        {
            foreach (var rec in _records.Values)
            {
                if (rec.Enabled) toLoad.Add((rec.Name, rec.Id));
            }
        }

        int loaded = 0;
        foreach (var (name, id) in toLoad)
        {
            string path = CachePath(id);
            if (!File.Exists(path)) continue;
            if (new FileInfo(path).Length > MaxListBytes) continue;
            string text = File.ReadAllText(path, Encoding.UTF8);
            blocker.LoadFilterList(name, text);
            loaded++;
        }
        return loaded;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsHttp) _http.Dispose();
        _io.Dispose();
    }

    // ---------------------------------------------------------------------
    // Internals

    private Record Find(string id) =>
        _records.TryGetValue(id, out var rec) ? rec : throw new KeyNotFoundException("List not found.");

    private static FilterListState ToState(Record r) => new(
        r.Id, r.Name, r.Url, r.Enabled, r.IsCustom, r.ETag, r.LastCheckedUtc, r.LastUpdatedUtc, r.CachedBytes, r.LastError);

    private FilterListUpdateResult Fail(string id, string error)
    {
        lock (_gate)
        {
            if (_records.TryGetValue(id, out var rec))
            {
                rec.LastCheckedUtc = DateTimeOffset.UtcNow;
                rec.LastError = Truncate(error, 300);
                Save();
            }
        }
        return new FilterListUpdateResult(id, FilterListUpdateStatus.Failed, error);
    }

    private void MarkChecked(string id, string? error)
    {
        lock (_gate)
        {
            var rec = Find(id);
            rec.LastCheckedUtc = DateTimeOffset.UtcNow;
            rec.LastError = error;
            Save();
        }
    }

    private string CachePath(string id)
    {
        // IDs contain only letters, digits, and '-' (catalog IDs or custom hashes). No path is built from a URL.
        if (id.Length == 0 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            throw new ArgumentException("Invalid list ID.", nameof(id));
        return Path.Combine(_directory, id + ".cache.txt");
    }

    private static string ValidateText(byte[] data)
    {
        if (data.Length == 0) throw new InvalidDataException("Empty list.");
        if (data.AsSpan().IndexOf((byte)0) >= 0) throw new InvalidDataException("Content is not text.");
        return Encoding.UTF8.GetString(data);
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, long maxBytes, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is long declared && declared > maxBytes)
            throw new InvalidDataException("List exceeds the size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + read > maxBytes) throw new InvalidDataException("List exceeds the size limit.");
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    private void WriteCacheAtomic(string id, byte[] data)
    {
        string path = CachePath(id);
        string tmp = path + ".tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, path, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void Load()
    {
        string path = Path.Combine(_directory, StateFileName);
        if (!File.Exists(path)) return;
        try
        {
            var file = JsonSerializer.Deserialize<StoreFile>(File.ReadAllText(path, Encoding.UTF8)) ?? new StoreFile();
            foreach (var saved in file.Lists)
            {
                if (string.IsNullOrEmpty(saved.Id)) continue;
                if (saved.IsCustom)
                {
                    if (!HostUtil.TryGetHttpUri(saved.Url, out _)) continue;
                    _records[saved.Id] = saved;
                    continue;
                }
                // Catalog: only user state is kept; name and URL come from the current catalog.
                if (_records.TryGetValue(saved.Id, out var current))
                {
                    current.Enabled = saved.Enabled;
                    current.ETag = saved.ETag;
                    current.LastCheckedUtc = saved.LastCheckedUtc;
                    current.LastUpdatedUtc = saved.LastUpdatedUtc;
                    current.CachedBytes = saved.CachedBytes;
                    current.LastError = saved.LastError;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            // A corrupt state file is ignored; cache and catalog keep working. The user can re-enable.
        }
    }

    private void Save()
    {
        var file = new StoreFile { Lists = _records.Values.ToList() };
        string path = Path.Combine(_directory, StateFileName);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(file, JsonOptions), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
