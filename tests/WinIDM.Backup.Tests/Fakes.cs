using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using WinIDM.Backup;
using WinIDM.Core.Contracts;
using WinIDM.Core.Models;

namespace WinIDM.Backup.Tests;

internal sealed class FakeDownloadRepository : IDownloadRepository
{
    private readonly Dictionary<long, DownloadItem> _items = new();
    private readonly Dictionary<long, List<SegmentInfo>> _segments = new();
    private long _next = 1;

    public Task<long> InsertAsync(DownloadItem item, CancellationToken ct = default)
    {
        var id = _next++;
        item.Id = id;
        _items[id] = item;
        return Task.FromResult(id);
    }

    public Task UpdateAsync(DownloadItem item, CancellationToken ct = default)
    {
        _items[item.Id] = item;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(long id, CancellationToken ct = default)
    {
        _items.Remove(id);
        _segments.Remove(id);
        return Task.CompletedTask;
    }

    public Task<DownloadItem?> GetAsync(long id, CancellationToken ct = default) =>
        Task.FromResult(_items.TryGetValue(id, out var item) ? item : null);

    public Task<IReadOnlyList<DownloadItem>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DownloadItem>>(_items.Values.OrderBy(i => i.Id).ToList());

    public Task SaveSegmentsAsync(long downloadId, IReadOnlyList<SegmentInfo> segments, CancellationToken ct = default)
    {
        _segments[downloadId] = segments.ToList();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SegmentInfo>> LoadSegmentsAsync(long downloadId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SegmentInfo>>(
            _segments.TryGetValue(downloadId, out var list) ? list.ToList() : new List<SegmentInfo>());

    public IReadOnlyList<DownloadItem> All => _items.Values.OrderBy(i => i.Id).ToList();

    public IReadOnlyList<SegmentInfo> SegmentsOf(long id) =>
        _segments.TryGetValue(id, out var list) ? list : new List<SegmentInfo>();
}

internal sealed class FakeSettingsStore : ISettingsStore
{
    private Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public T Get<T>(string key, T defaultValue) =>
        _values.TryGetValue(key, out var s)
            ? (T)Convert.ChangeType(s, typeof(T), CultureInfo.InvariantCulture)
            : defaultValue;

    public void Set<T>(string key, T value) =>
        _values[key] = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

    public IReadOnlyDictionary<string, string> Snapshot() => new Dictionary<string, string>(_values, StringComparer.Ordinal);

    public void Restore(IReadOnlyDictionary<string, string> values) =>
        _values = new Dictionary<string, string>(values, StringComparer.Ordinal);

#pragma warning disable CS0067 // The event is required by ISettingsStore; not used in tests.
    public event Action<string>? Changed;
#pragma warning restore CS0067

    public void Seed(params (string Key, string Value)[] pairs)
    {
        foreach (var (key, value) in pairs)
        {
            _values[key] = value;
        }
    }
}

internal sealed class FakeDataSource : IBackupDataSource
{
    public Dictionary<string, string> Data { get; } = new(StringComparer.Ordinal);

    public HashSet<string> FailOnImport { get; } = new(StringComparer.Ordinal);

    public List<(string Name, RestoreMode Mode)> Imports { get; } = new();

    public Task<string?> ExportAsync(string name, CancellationToken ct = default) =>
        Task.FromResult(Data.TryGetValue(name, out var json) ? json : null);

    public Task ImportAsync(string name, string json, RestoreMode mode, CancellationToken ct = default)
    {
        if (FailOnImport.Contains(name))
        {
            throw new InvalidOperationException($"Import '{name}' was rejected by the data source.");
        }

        Imports.Add((name, mode));
        Data[name] = json;
        return Task.CompletedTask;
    }
}

internal sealed class FakeClock : ISystemClock
{
    public FakeClock(DateTimeOffset start) => UtcNow = start;

    public DateTimeOffset UtcNow { get; private set; }

    public void Advance(TimeSpan delta) => UtcNow = UtcNow + delta;

    public Task DelayAsync(TimeSpan delay, CancellationToken ct = default)
    {
        Advance(delay);
        return Task.CompletedTask;
    }
}

/// <summary>Fake backup service: writes small files and counts calls.</summary>
internal sealed class CountingBackupService : IBackupService
{
    public int CreateCount { get; private set; }

    public Exception? ThrowOnCreate { get; set; }

    public Task CreateAsync(string destinationPath, BackupCategories categories, string? password, CancellationToken ct = default)
    {
        if (ThrowOnCreate is not null)
        {
            throw ThrowOnCreate;
        }

        CreateCount++;
        File.WriteAllText(destinationPath, "fake-backup-" + CreateCount);
        return Task.CompletedTask;
    }

    public Task RestoreAsync(string sourcePath, BackupCategories categories, string? password, CancellationToken ct = default) =>
        throw new NotSupportedException();
}

/// <summary>Temporary directory deleted after the test.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "winidm-backup-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Ignore files that are still locked.
        }
    }
}

/// <summary>Mock HTTP handler: records requests and returns the generated response.</summary>
internal sealed class MockHttpHandler : HttpMessageHandler
{
    private readonly Func<SentRequest, HttpResponseMessage> _respond;

    public MockHttpHandler(Func<SentRequest, HttpResponseMessage> respond) => _respond = respond;

    public List<SentRequest> Sent { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var sent = new SentRequest(
            request.Method.Method,
            request.RequestUri!.ToString(),
            request.Headers.Authorization?.ToString(),
            request.Content?.Headers.ContentType?.MediaType,
            body);
        Sent.Add(sent);
        return _respond(sent);
    }
}

internal sealed record SentRequest(string Method, string Url, string? Authorization, string? ContentType, string? Body = null);

internal static class TestHttp
{
    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    /// <summary>Build a raw ZIP with specific entries (for format and zip-slip tests).</summary>
    public static void WriteZip(string path, params (string Name, string Content)[] entries)
    {
        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = zip.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
    }

    public const string ManifestSettingsOnly =
        "{\"format\":\"WinIDM.Backup\",\"schemaVersion\":1,\"createdUtc\":\"2026-10-31T00:00:00Z\",\"categories\":1,\"files\":[\"idm_settings.json\"]}";
}
