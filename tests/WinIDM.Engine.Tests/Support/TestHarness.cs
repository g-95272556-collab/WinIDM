using System.Security.Cryptography;
using WinIDM.Core.Models;
using WinIDM.Engine;

namespace WinIDM.Engine.Tests.Support;

/// <summary>Deterministic random data and hash helpers.</summary>
public static class TestData
{
    public static byte[] Random(int size, int seed)
    {
        var buffer = new byte[size];
        new System.Random(seed).NextBytes(buffer);
        return buffer;
    }

    public static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static string Sha256Hex(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

/// <summary>A unique temporary directory deleted after the test.</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "winidm-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // The file may still be held briefly; the temporary directory does not disturb other tests.
        }
    }
}

/// <summary>
/// Engine environment for tests: JSON settings, a SQLite repository, and an engine with short timeouts.
/// It can be reopened to simulate an app restart.
/// </summary>
public sealed class EngineHarness : IAsyncDisposable
{
    private readonly TempDirectory _temp = new();
    private SqliteDownloadRepository _repository = null!;
    private DownloadEngine _engine = null!;
    private JsonSettingsStore _settings = null!;
    private readonly INetworkPolicy? _network;

    /// <param name="configure">Initial settings.</param>
    /// <param name="network">Network policy (optional). Null = <see cref="DefaultNetworkPolicy"/>.</param>
    public EngineHarness(Action<JsonSettingsStore>? configure = null, INetworkPolicy? network = null)
    {
        _network = network;
        _settings = new JsonSettingsStore(SettingsPath);
        _settings.Set(SettingKeys.DefaultSaveDirectory, SaveDirectory);
        _settings.Set(SettingKeys.DisableCataloguing, true);
        _settings.Set(SettingKeys.RetryInterval, 0);
        configure?.Invoke(_settings);

        Open();
    }

    public string Root => _temp.Path;

    public string SaveDirectory => System.IO.Path.Combine(_temp.Path, "downloads");

    public string SettingsPath => System.IO.Path.Combine(_temp.Path, "settings.json");

    public string DatabasePath => System.IO.Path.Combine(_temp.Path, "data", "downloads.db");

    public JsonSettingsStore Settings => _settings;

    public SqliteDownloadRepository Repository => _repository;

    public DownloadEngine Engine => _engine;

    /// <summary>Close the engine and repository, then reopen from the same files (restart simulation).</summary>
    public void Reopen()
    {
        _engine.Dispose();
        _repository.Dispose();
        Open();
    }

    /// <summary>Close the engine and repository only (no restart).</summary>
    public void CloseOnly()
    {
        _engine.Dispose();
        _repository.Dispose();
    }

    private void Open()
    {
        _settings = new JsonSettingsStore(SettingsPath);
        _repository = new SqliteDownloadRepository(DatabasePath);
        _engine = new DownloadEngine(_repository, _settings, network: _network)
        {
            MinRetryDelay = TimeSpan.Zero,
            SegmentRetryDelay = TimeSpan.FromMilliseconds(5),
            CheckpointInterval = TimeSpan.FromMilliseconds(100),
        };
    }

    public async Task<DownloadItem> GetItemAsync(long id)
    {
        var item = await _repository.GetAsync(id);
        return item ?? throw new InvalidOperationException($"Download {id} does not exist.");
    }

    /// <summary>Wait until the download state matches, or fail after the timeout.</summary>
    public async Task<DownloadItem> WaitForStateAsync(long id, DownloadState state, int timeoutMs = 20000)
    {
        DownloadItem? last = null;
        await WaitUntilAsync(async () =>
        {
            last = await _repository.GetAsync(id);
            return last?.State == state;
        }, timeoutMs, $"state {state}");
        return last!;
    }

    /// <summary>Same as <see cref="WaitUntilAsync(Func{Task{bool}}, int, string)"/> for conditions that do not need await.</summary>
    public static Task WaitUntilAsync(Func<bool> condition, int timeoutMs, string description) =>
        WaitUntilAsync(() => Task.FromResult(condition()), timeoutMs, description);

    public static async Task WaitUntilAsync(Func<Task<bool>> condition, int timeoutMs, string description)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(40);
        }

        throw new TimeoutException($"Timed out waiting for {description}.");
    }

    public async ValueTask DisposeAsync()
    {
        _engine.Dispose();
        _repository.Dispose();
        await Task.CompletedTask;
        _temp.Dispose();
    }
}

/// <summary>Virtual file that can be updated (content and ETag) for the test server.</summary>
public sealed class VirtualFile
{
    public VirtualFile(byte[] content, string etag = "\"v1\"")
    {
        Content = content;
        ETag = etag;
    }

    public byte[] Content { get; set; }

    public string ETag { get; set; }

    public bool SupportRanges { get; set; } = true;

    public string? ContentDisposition { get; set; }

    /// <summary>Standard response: full 200, or a 206 range (with an If-Range check) if supported.</summary>
    public ServerResponse Serve(RecordedRequest request)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ETag"] = ETag,
            ["Content-Type"] = "application/octet-stream",
        };

        if (ContentDisposition is not null) headers["Content-Disposition"] = ContentDisposition;

        if (!SupportRanges)
        {
            return Full(headers);
        }

        headers["Accept-Ranges"] = "bytes";
        var range = request.Range;
        if (range is null) return Full(headers);

        if (request.Get("If-Range") is { } ifRange && ifRange != ETag)
        {
            // Validator mismatch: the server must send the full content (200).
            return Full(headers);
        }

        var length = Content.Length;
        var (from, to) = range.Value;
        var end = to ?? length - 1;
        if (from >= length)
        {
            headers["Content-Range"] = $"bytes */{length}";
            return new ServerResponse { Status = 416, Reason = "Range Not Satisfiable" }.WithHeaders(headers);
        }

        end = Math.Min(end, length - 1);
        headers["Content-Range"] = $"bytes {from}-{end}/{length}";
        var body = Content[(int)from..((int)end + 1)];
        return new ServerResponse { Status = 206, Reason = "Partial Content", Body = body }.WithHeaders(headers);
    }

    private ServerResponse Full(Dictionary<string, string> headers) =>
        new ServerResponse { Status = 200, Reason = "OK", Body = Content }.WithHeaders(headers);
}

internal static class ServerResponseExtensions
{
    public static ServerResponse WithHeaders(this ServerResponse response, Dictionary<string, string> headers)
    {
        foreach (var (name, value) in headers) response.Headers[name] = value;
        return response;
    }
}
