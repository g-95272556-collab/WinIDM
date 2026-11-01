using WinIDM.Core.Contracts;
using WinIDM.Core.Models;
using WinIDM.Engine;
using WinIDM.Engine.Tests.Support;

namespace WinIDM.Engine.Tests;

/// <summary>
/// Engine network policy with a fake <see cref="INetworkPolicy"/> implementation toggled while a download is running.
/// All servers are 127.0.0.1; no internet.
/// </summary>
public sealed class NetworkPolicyTests
{
    private const int Mib = 1024 * 1024;

    private static DownloadRequest RequestFor(LocalHttpServer server, string path) => new()
    {
        Url = server.BaseUrl + path,
        StartImmediately = true,
    };

    /// <summary>Slow stream (real ranges are slow, the 0-0 probe is fast) so there is time to change the network state.</summary>
    private static void ServeSlowRanges(LocalHttpServer server, byte[] content, int chunkDelayMs = 5)
    {
        var file = new VirtualFile(content);
        server.Handler = req =>
        {
            var response = file.Serve(req);
            return req.Range is { } range && range.To != 0 ? WithDelay(response, chunkDelayMs) : response;
        };
    }

    /// <summary>
    /// Slow only the response that starts at byte 0 (the first attempt). A resumed range (from &gt; 0) is served fast,
    /// which keeps the "toggle the setting at runtime" test short.
    /// </summary>
    private static void ServeSlowStartOnly(LocalHttpServer server, byte[] content, int chunkDelayMs = 10)
    {
        var file = new VirtualFile(content);
        server.Handler = req =>
        {
            var response = file.Serve(req);
            var firstAttempt = req.Range is { } range && range.From == 0 && range.To != 0;
            return firstAttempt ? WithDelay(response, chunkDelayMs) : response;
        };
    }

    private static ServerResponse WithDelay(ServerResponse source, int chunkDelayMs)
    {
        var copy = new ServerResponse
        {
            Status = source.Status,
            Reason = source.Reason,
            Body = source.Body,
            AbortAfterBytes = source.AbortAfterBytes,
            ChunkDelayMs = chunkDelayMs,
        };
        foreach (var (name, value) in source.Headers) copy.Headers[name] = value;
        return copy;
    }

    private static async Task WaitForBytesAsync(EngineHarness h, long id, long bytes) =>
        await EngineHarness.WaitUntilAsync(async () =>
            (await h.Repository.GetAsync(id))?.DownloadedBytes >= bytes, 20000, $"progress {bytes} bytes");

    private static string FinalPath(DownloadItem item) => Path.Combine(item.SaveDirectory, item.FileName);

    [Fact]
    public async Task MeteredNetwork_WithPauseEnabled_StopsRunningDownload_AndResumesWhenUnmetered()
    {
        var network = new FakeNetworkPolicy();
        await using var h = new EngineHarness(s =>
        {
            s.Set(SettingKeys.PauseOnMetered, true);
            s.Set(SettingKeys.DownloadPart, 0);
        }, network);
        await using var server = new LocalHttpServer();
        var content = TestData.Random(4 * Mib, seed: 41);
        ServeSlowRanges(server, content);

        var id = await h.Engine.AddAsync(RequestFor(server, "/metered.bin"));
        await WaitForBytesAsync(h, id, 256 * 1024);

        // The network becomes metered in the middle of the download.
        network.IsMetered = true;
        network.Raise();
        await h.WaitForStateAsync(id, DownloadState.Queued);

        var frozen = (await h.GetItemAsync(id)).DownloadedBytes;
        Assert.InRange(frozen, 256L * 1024, content.Length - 1L);
        await Task.Delay(1500);
        var stillFrozen = await h.GetItemAsync(id);
        Assert.Equal(DownloadState.Queued, stillFrozen.State);
        Assert.Equal(frozen, stillFrozen.DownloadedBytes);

        // The network is no longer metered: resume from the checkpoint (range), not from zero.
        var requestsBefore = server.Requests.Count;
        network.IsMetered = false;
        network.Raise();

        var done = await h.WaitForStateAsync(id, DownloadState.Completed, timeoutMs: 30000);
        Assert.Equal((long)content.Length, done.TotalBytes);
        Assert.Equal(TestData.Sha256Hex(content), TestData.Sha256Hex(FinalPath(done)));
        Assert.Contains(server.Requests.Skip(requestsBefore), r => r.Range is { } range && range.From > 0);
    }

    /// <summary>
    /// The pause-on-metered setting drives the engine policy at runtime: metered alone does not stop a download,
    /// turning the setting on does (within the 1 second tick), and turning it off resumes it from the checkpoint.
    /// </summary>
    [Fact]
    public async Task PauseOnMetered_SettingTurnedOnWhileMetered_StopsAndResumes()
    {
        var network = new FakeNetworkPolicy { IsMetered = true };
        await using var h = new EngineHarness(s => s.Set(SettingKeys.DownloadPart, 0), network);
        await using var server = new LocalHttpServer();
        var content = TestData.Random(8 * Mib, seed: 47);
        ServeSlowStartOnly(server, content);

        var id = await h.Engine.AddAsync(RequestFor(server, "/toggle-metered.bin"));
        await WaitForBytesAsync(h, id, 128 * 1024);

        // Metered while the setting is off: the download keeps running (not stopped by the network state alone).
        Assert.NotEqual(DownloadState.Queued, (await h.GetItemAsync(id)).State);

        // Turning the setting on stops the running download; the engine re-evaluates on its 1 second tick.
        h.Settings.Set(SettingKeys.PauseOnMetered, true);
        await h.WaitForStateAsync(id, DownloadState.Queued);

        var frozen = (await h.GetItemAsync(id)).DownloadedBytes;
        Assert.InRange(frozen, 128L * 1024, content.Length - 1L);
        await Task.Delay(1500);
        var stillFrozen = await h.GetItemAsync(id);
        Assert.Equal(DownloadState.Queued, stillFrozen.State);
        Assert.Equal(frozen, stillFrozen.DownloadedBytes);

        // Turning it off resumes automatically (again via the tick) and completes from the checkpoint.
        h.Settings.Set(SettingKeys.PauseOnMetered, false);
        var done = await h.WaitForStateAsync(id, DownloadState.Completed, timeoutMs: 30000);
        Assert.Equal((long)content.Length, done.TotalBytes);
        Assert.Equal(TestData.Sha256Hex(content), TestData.Sha256Hex(FinalPath(done)));
    }

    [Fact]
    public async Task Offline_PausesRunningDownload_AndResumesWhenOnline()
    {
        var network = new FakeNetworkPolicy();
        await using var h = new EngineHarness(s => s.Set(SettingKeys.DownloadPart, 0), network);
        await using var server = new LocalHttpServer();
        var content = TestData.Random(3 * Mib, seed: 42);
        ServeSlowRanges(server, content);

        var id = await h.Engine.AddAsync(RequestFor(server, "/offline.bin"));
        await WaitForBytesAsync(h, id, 128 * 1024);

        network.IsOnline = false;
        network.Raise();
        await h.WaitForStateAsync(id, DownloadState.Queued);

        network.IsOnline = true;
        network.Raise();
        var done = await h.WaitForStateAsync(id, DownloadState.Completed, timeoutMs: 30000);
        Assert.Equal(TestData.Sha256Hex(content), TestData.Sha256Hex(FinalPath(done)));
    }

    [Fact]
    public async Task WifiOnly_UsesPolicyWifiOrEthernet_AndStartsWhenAllowed()
    {
        var network = new FakeNetworkPolicy { IsWifiOrEthernet = false };
        await using var h = new EngineHarness(s => s.Set(SettingKeys.WifiOnly, true), network);
        await using var server = new LocalHttpServer();
        server.Handler = new VirtualFile(TestData.Random(1 * Mib, seed: 43)).Serve;

        var id = await h.Engine.AddAsync(RequestFor(server, "/wifi.bin"));
        await Task.Delay(1500);
        Assert.Equal(DownloadState.Queued, (await h.GetItemAsync(id)).State);
        Assert.Empty(server.Requests);

        // Ethernet counts as equivalent to Wi-Fi (not mobile data).
        network.IsWifiOrEthernet = true;
        network.Raise();
        await h.WaitForStateAsync(id, DownloadState.Completed);
    }

    [Fact]
    public async Task MeteredNetwork_WithoutPauseOrQuota_KeepsDownloading_AndCountsBytes()
    {
        var network = new FakeNetworkPolicy { IsMetered = true };
        await using var h = new EngineHarness(null, network);
        await using var server = new LocalHttpServer();
        var content = TestData.Random(2 * Mib, seed: 44);
        server.Handler = new VirtualFile(content).Serve;

        var id = await h.Engine.AddAsync(RequestFor(server, "/continue.bin"));
        await h.WaitForStateAsync(id, DownloadState.Completed);

        // The count is written during the tick (1 second).
        await EngineHarness.WaitUntilAsync(() => h.Settings.Get(SettingKeys.MeteredUsageDayBytes, 0L) >= content.Length,
            5000, "metered usage stored");
        Assert.Equal((long)content.Length, h.Settings.Get(SettingKeys.MeteredUsageDayBytes, 0L));
    }

    [Fact]
    public async Task UnmeteredTraffic_IsNotCounted()
    {
        var network = new FakeNetworkPolicy { IsMetered = false };
        await using var h = new EngineHarness(s => s.Set(SettingKeys.MeteredDailyLimitMb, 1), network);
        await using var server = new LocalHttpServer();
        server.Handler = new VirtualFile(TestData.Random(3 * Mib, seed: 45)).Serve;

        var id = await h.Engine.AddAsync(RequestFor(server, "/no-limit.bin"));
        await h.WaitForStateAsync(id, DownloadState.Completed);

        await Task.Delay(1500);
        Assert.Equal(0L, h.Settings.Get(SettingKeys.MeteredUsageDayBytes, 0L));
    }

    [Fact]
    public async Task DailyQuota_StopsAtLimit_SurvivesRestart_AndResumesAfterDayChanges()
    {
        var network = new FakeNetworkPolicy { IsMetered = true };
        var today = DateTimeOffset.Now;
        await using var h = new EngineHarness(s =>
        {
            s.Set(SettingKeys.MeteredDailyLimitMb, 1);
            s.Set(SettingKeys.DownloadPart, 0);
        }, network);
        h.Engine.Clock = () => today;
        await using var server = new LocalHttpServer();
        var content = TestData.Random(3 * Mib, seed: 46);
        ServeSlowRanges(server, content);

        // Day 1: stops after the 1 MiB limit is reached, but not before.
        var id = await h.Engine.AddAsync(RequestFor(server, "/quota.bin"));
        await EngineHarness.WaitUntilAsync(async () =>
        {
            var item = await h.GetItemAsync(id);
            return item.State == DownloadState.Queued && item.DownloadedBytes >= Mib;
        }, 20000, "stops at the quota limit");
        Assert.True((await h.GetItemAsync(id)).DownloadedBytes < content.Length);

        // Engine restart: the count persists, so the download must not restart on the same day.
        var requestsBeforeRestart = server.Requests.Count;
        h.Reopen();
        await Task.Delay(1500);
        Assert.Equal(DownloadState.Queued, (await h.GetItemAsync(id)).State);
        Assert.Equal(requestsBeforeRestart, server.Requests.Count);

        // Day 2: the daily count resets, so the download runs again up to the new 1 MiB limit.
        h.Engine.Clock = () => today.AddDays(1);
        await EngineHarness.WaitUntilAsync(async () => (await h.GetItemAsync(id)).DownloadedBytes > Mib + 256 * 1024,
            20000, "progress after the daily count resets");

        // Remove the limit: the download completes and the file matches the source.
        h.Settings.Set(SettingKeys.MeteredDailyLimitMb, 0);
        var done = await h.WaitForStateAsync(id, DownloadState.Completed, timeoutMs: 30000);
        Assert.Equal(TestData.Sha256Hex(content), TestData.Sha256Hex(FinalPath(done)));
    }

    [Fact]
    public void Engine_Dispose_DisposesDisposablePolicy()
    {
        var network = new FakeNetworkPolicy();
        using (new DownloadEngine(new EmptyRepository(), new InMemorySettings(), network: network))
        {
            Assert.False(network.Disposed);
        }

        Assert.True(network.Disposed);
    }

    [Fact]
    public void MeteredUsage_PersistsAcrossInstances_AndResetsInNewMonth()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        var january = new DateTimeOffset(2026, 1, 31, 12, 0, 0, TimeSpan.Zero);
        var february = new DateTimeOffset(2026, 2, 1, 12, 0, 0, TimeSpan.Zero);

        var usage = new MeteredUsage(new JsonSettingsStore(path), january);
        usage.Add(5L * Mib, january);
        usage.Flush(january);

        var reloaded = new MeteredUsage(new JsonSettingsStore(path), january);
        Assert.Equal((5L * Mib, 5L * Mib), reloaded.Totals(january));

        // New day and month: both counts return to zero.
        var nextMonth = new MeteredUsage(new JsonSettingsStore(path), february);
        Assert.Equal((0L, 0L), nextMonth.Totals(february));
    }

    /// <summary>Empty repository for tests that only build the engine and dispose of it.</summary>
    private sealed class EmptyRepository : IDownloadRepository
    {
        public Task<long> InsertAsync(DownloadItem item, CancellationToken ct = default) => Task.FromResult(1L);
        public Task UpdateAsync(DownloadItem item, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(long id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<DownloadItem?> GetAsync(long id, CancellationToken ct = default) => Task.FromResult<DownloadItem?>(null);
        public Task<IReadOnlyList<DownloadItem>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DownloadItem>>([]);
        public Task SaveSegmentsAsync(long downloadId, IReadOnlyList<SegmentInfo> segments, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<SegmentInfo>> LoadSegmentsAsync(long downloadId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SegmentInfo>>([]);
    }

    /// <summary>In-memory settings for tests that do not need a file.</summary>
    private sealed class InMemorySettings : ISettingsStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public T Get<T>(string key, T defaultValue) =>
            _values.TryGetValue(key, out var raw) ? (T)Convert.ChangeType(raw, typeof(T), System.Globalization.CultureInfo.InvariantCulture) : defaultValue;

        public void Set<T>(string key, T value) =>
            _values[key] = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

        public IReadOnlyDictionary<string, string> Snapshot() => new Dictionary<string, string>(_values);

        public void Restore(IReadOnlyDictionary<string, string> values)
        {
            _values.Clear();
            foreach (var (key, value) in values) _values[key] = value;
        }

        public event Action<string>? Changed
        {
            add { }
            remove { }
        }
    }
}
