using MonoTorrent;
using MonoTorrent.Dht;
using WinIDM.Core.Models;
using WinIDM.Torrent.Tests.Support;

namespace WinIDM.Torrent.Tests;

/// <summary>
/// TorrentService lifecycle tests. All tests use offline settings
/// (DHT, LSD, UPnP, NAT-PMP are off; the tracker is a non-existent 127.0.0.1 loopback).
/// </summary>
public class TorrentServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string DataDir => Path.Combine(_temp.Path, "data");
    private string Downloads => Path.Combine(_temp.Path, "downloads");

    private TestTorrent MakeTorrent(string name = "Package", string tracker = "http://127.0.0.1:9/announce")
    {
        var files = new[]
        {
            new TestFile("data/alpha.bin", TestTorrent.Bytes32(1, 40_000)),
            new TestFile("data/beta.bin", TestTorrent.Bytes32(2, 5_000)),
            new TestFile("readme.txt", TestTorrent.Bytes32(3, 123)),
        };
        return TestTorrent.Create(Path.Combine(_temp.Path, "src-" + name + Guid.NewGuid().ToString("N")[..6]), name, files, tracker);
    }

    private static async Task WaitForAsync(Func<bool> condition, string description, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException("Timed out waiting: " + description);
    }

    [Fact]
    public async Task AddTorrentFile_AssignsIdOneAndExposesFiles()
    {
        var torrent = MakeTorrent();
        await using var svc = new TorrentService(OfflineSettings.Create(), DataDir);

        var id = await svc.AddTorrentFileAsync(torrent.Path, Downloads);

        Assert.Equal(1, id);
        var files = svc.GetFiles(id);
        Assert.Equal(3, files.Count);
        Assert.All(files, f => Assert.Equal(TorrentFilePriority.Normal, f.Priority));
        Assert.Contains(files, f => f.Path.EndsWith("alpha.bin") && f.Length == 40_000);
        Assert.Contains(files, f => f.Path.EndsWith("beta.bin") && f.Length == 5_000);
        Assert.Equal(torrent.InfoHash, svc.GetStats(id).InfoHash);
    }

    [Fact]
    public async Task FilePriority_CanBeChangedToIgnoreAndHigh()
    {
        var torrent = MakeTorrent();
        await using var svc = new TorrentService(OfflineSettings.Create(), DataDir);
        var id = await svc.AddTorrentFileAsync(torrent.Path, Downloads);

        await svc.SetFilePriorityAsync(id, 1, TorrentFilePriority.Ignore);
        Assert.Equal(TorrentFilePriority.Ignore, svc.GetFiles(id)[1].Priority);

        await svc.SetFilePriorityAsync(id, 1, TorrentFilePriority.High);
        Assert.Equal(TorrentFilePriority.High, svc.GetFiles(id)[1].Priority);
        Assert.Equal(TorrentFilePriority.Normal, svc.GetFiles(id)[0].Priority);
    }

    [Fact]
    public async Task FilePriority_OutOfRangeIndex_IsRejectedAndNotStored()
    {
        var torrent = MakeTorrent();
        await using var svc = new TorrentService(OfflineSettings.Create(), DataDir);
        var id = await svc.AddTorrentFileAsync(torrent.Path, Downloads);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => svc.SetFilePriorityAsync(id, 99, TorrentFilePriority.Ignore));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => svc.SetFilePriorityAsync(id, -1, TorrentFilePriority.Ignore));
        Assert.All(svc.GetFiles(id), f => Assert.Equal(TorrentFilePriority.Normal, f.Priority));
    }

    [Fact]
    public async Task PauseAndResume_UpdatesStateAndPausedFlag()
    {
        var torrent = MakeTorrent();
        await using var svc = new TorrentService(OfflineSettings.Create(), DataDir);
        var id = await svc.AddTorrentFileAsync(torrent.Path, Downloads);

        await svc.PauseAsync(id);
        var paused = svc.GetStats(id);
        Assert.True(paused.Paused);
        Assert.Equal(DownloadState.Paused, paused.State);

        await svc.ResumeAsync(id);
        var resumed = svc.GetStats(id);
        Assert.False(resumed.Paused);
        Assert.NotEqual(DownloadState.Paused, resumed.State);
    }

    [Fact]
    public async Task Remove_WithoutFiles_DropsEntryAndTorrentCopy()
    {
        var torrent = MakeTorrent();
        await using var svc = new TorrentService(OfflineSettings.Create(), DataDir);
        var id = await svc.AddTorrentFileAsync(torrent.Path, Downloads);
        var copy = Path.Combine(DataDir, "torrents", id + ".torrent");
        Assert.True(File.Exists(copy));

        await svc.RemoveAsync(id, deleteFiles: false);

        Assert.Empty(svc.GetTorrents());
        Assert.False(File.Exists(copy));
        Assert.Throws<KeyNotFoundException>(() => svc.GetStats(id));
    }

    [Fact]
    public async Task Remove_WithFiles_DeletesDownloadedContent()
    {
        var torrent = MakeTorrent();
        torrent.WriteContent(Downloads);
        var alpha = Path.Combine(Downloads, "Package", "data", "alpha.bin");
        Assert.True(File.Exists(alpha));

        await using var svc = new TorrentService(OfflineSettings.Create(), DataDir);
        var id = await svc.AddTorrentFileAsync(torrent.Path, Downloads);
        await WaitForAsync(() => svc.GetStats(id).Progress >= 100, "download finished");

        await svc.RemoveAsync(id, deleteFiles: true);

        Assert.False(File.Exists(alpha));
        Assert.Empty(svc.GetTorrents());
    }

    [Fact]
    public async Task Persistence_RestoresPausedTorrentPrioritiesAndLimits()
    {
        var torrent = MakeTorrent();
        var store = OfflineSettings.Create();

        await using (var first = new TorrentService(store, DataDir))
        {
            var id = await first.AddTorrentFileAsync(torrent.Path, Downloads);
            await first.SetFilePriorityAsync(id, 1, TorrentFilePriority.High);
            await first.SetSpeedLimitsAsync(id, 1234, 5678);
            await first.SetSequentialDownloadAsync(id, true);
            await first.PauseAsync(id);
        }

        await using var second = new TorrentService(store, DataDir);
        await second.InitializeAsync();

        var torrents = second.GetTorrents();
        var restored = Assert.Single(torrents);
        Assert.Equal(1, restored.Id);
        Assert.True(restored.Paused);
        Assert.Equal(TorrentFilePriority.High, second.GetFiles(1)[1].Priority);

        var stats = second.GetStats(1);
        Assert.Equal(1234, stats.DownloadLimit);
        Assert.Equal(5678, stats.UploadLimit);
        Assert.True(stats.Sequential);

        // The next id continues instead of starting over.
        var secondTorrent = MakeTorrent("Second");
        var nextId = await second.AddTorrentFileAsync(secondTorrent.Path, Downloads);
        Assert.Equal(2, nextId);
    }

    [Fact]
    public async Task Pause_WritesFastResumeDataToCache()
    {
        var torrent = MakeTorrent();
        torrent.WriteContent(Downloads);
        await using var svc = new TorrentService(OfflineSettings.Create(), DataDir);
        var id = await svc.AddTorrentFileAsync(torrent.Path, Downloads);
        await WaitForAsync(() => svc.GetStats(id).State == DownloadState.Completed, "finished before pause");

        await svc.PauseAsync(id);

        var cacheDir = Path.Combine(DataDir, "cache");
        Assert.True(Directory.Exists(cacheDir));
        Assert.NotEmpty(Directory.GetFiles(cacheDir, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Trackers_CanBeReplaced()
    {
        var torrent = MakeTorrent();
        await using var svc = new TorrentService(OfflineSettings.Create(), DataDir);
        var id = await svc.AddTorrentFileAsync(torrent.Path, Downloads);
        Assert.Contains("http://127.0.0.1:9/announce", svc.GetTrackers(id));

        await svc.SetTrackersAsync(id, new[] { "http://127.0.0.1:19/announce", "udp://127.0.0.1:19/announce" });

        var trackers = svc.GetTrackers(id);
        Assert.Contains("http://127.0.0.1:19/announce", trackers);
        Assert.Contains("udp://127.0.0.1:19/announce", trackers);
        Assert.DoesNotContain("http://127.0.0.1:9/announce", trackers);
    }

    [Fact]
    public async Task Reannounce_CompletesWithoutThrowingForLoopbackTracker()
    {
        var torrent = MakeTorrent();
        await using var svc = new TorrentService(OfflineSettings.Create(), DataDir);
        var id = await svc.AddTorrentFileAsync(torrent.Path, Downloads);

        await svc.ReannounceAsync(id);
    }

    [Fact]
    public async Task SaveTorrentFile_CopiesOriginalBytes()
    {
        var torrent = MakeTorrent();
        await using var svc = new TorrentService(OfflineSettings.Create(), DataDir);
        var id = await svc.AddTorrentFileAsync(torrent.Path, Downloads);
        var destination = Path.Combine(_temp.Path, "export", "copy.torrent");

        await svc.SaveTorrentFileAsync(id, destination);

        Assert.Equal(torrent.Bytes, File.ReadAllBytes(destination));
    }

    [Fact]
    public async Task MagnetLink_ForFileTorrent_ContainsInfoHashAndTrackers()
    {
        var torrent = MakeTorrent();
        await using var svc = new TorrentService(OfflineSettings.Create(), DataDir);
        var id = await svc.AddTorrentFileAsync(torrent.Path, Downloads);

        var magnet = svc.GetMagnetLink(id);

        Assert.NotNull(magnet);
        var parsed = MagnetLink.Parse(magnet!);
        Assert.Equal(torrent.InfoHash, parsed.InfoHashes.V1OrV2.ToHex(), ignoreCase: true);
        Assert.Contains(parsed.AnnounceUrls, u => u.Contains("127.0.0.1:9"));
    }

    [Fact]
    public async Task AddMagnet_WithoutMetadataFetch_KeepsOriginalLinkAndName()
    {
        const string hash = "0123456789abcdef0123456789abcdef01234567";
        var magnet = $"magnet:?xt=urn:btih:{hash}&dn=Test+Link&tr=http%3A%2F%2F127.0.0.1%3A19%2Fannounce";
        await using var svc = new TorrentService(OfflineSettings.Create(), DataDir);

        var id = await svc.AddMagnetAsync(magnet, Downloads);

        Assert.Equal(1, id);
        var stats = svc.GetStats(id);
        Assert.Equal(hash, stats.InfoHash);
        Assert.Equal("Test Link", stats.Name);
        Assert.False(stats.HasMetadata);
        Assert.Equal(magnet, svc.GetMagnetLink(id));
    }

    [Fact]
    public async Task AddMagnet_InvalidLink_ThrowsArgumentException()
    {
        await using var svc = new TorrentService(OfflineSettings.Create(), DataDir);

        await Assert.ThrowsAsync<ArgumentException>(() => svc.AddMagnetAsync("not a magnet", Downloads));
    }

    [Fact]
    public async Task DuplicateTorrent_IsRejected()
    {
        var torrent = MakeTorrent();
        await using var svc = new TorrentService(OfflineSettings.Create(), DataDir);
        await svc.AddTorrentFileAsync(torrent.Path, Downloads);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.AddTorrentFileAsync(torrent.Path, Downloads));
        Assert.Single(svc.GetTorrents());
    }

    /// <summary>IDM+ "Automatically add trackers to duplicate torrents": the same magnet adds its trackers, not a second torrent.</summary>
    [Fact]
    public async Task DuplicateMagnet_MergesTrackers_WhenAutoAddIsOn()
    {
        const string hash = "1123456789abcdef0123456789abcdef01234567";
        var store = OfflineSettings.Create().Put("idm_pref_add_tracker_auto", true).Put("acatracker", false);
        await using var svc = new TorrentService(store, DataDir);

        var id = await svc.AddMagnetAsync($"magnet:?xt=urn:btih:{hash}&tr=http%3A%2F%2F127.0.0.1%3A19%2Fannounce", Downloads);
        var again = await svc.AddMagnetAsync($"magnet:?xt=urn:btih:{hash}&tr=udp%3A%2F%2F127.0.0.1%3A20%2Fannounce", Downloads);

        Assert.Equal(id, again);
        Assert.Single(svc.GetTorrents());
        var trackers = svc.GetTrackers(id);
        Assert.Contains(trackers, t => t.Contains("127.0.0.1:19"));
        Assert.Contains(trackers, t => t.Contains("127.0.0.1:20"));
    }

    [Fact]
    public async Task DefaultTrackers_AreAddedToNewTorrents()
    {
        const string hash = "2123456789abcdef0123456789abcdef01234567";
        var store = OfflineSettings.Create().Put("default_trackers", true);
        await using var svc = new TorrentService(store, DataDir);

        var id = await svc.AddMagnetAsync($"magnet:?xt=urn:btih:{hash}", Downloads);

        var trackers = svc.GetTrackers(id);
        Assert.All(TorrentService.DefaultTrackers, t => Assert.Contains(trackers, x => x.TrimEnd('/') == t.TrimEnd('/')));
    }

    [Fact]
    public async Task DisableTorrentSetting_BlocksNewTorrents()
    {
        var torrent = MakeTorrent();
        var store = OfflineSettings.Create().Put("disable_torrent", true);
        await using var svc = new TorrentService(store, DataDir);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.AddTorrentFileAsync(torrent.Path, Downloads));
    }

    [Fact]
    public async Task ProgressChanged_ReportsInternalIdAndTotalSize()
    {
        var torrent = MakeTorrent();
        await using var svc = new TorrentService(OfflineSettings.Create(), DataDir);
        DownloadProgress? seen = null;
        svc.ProgressChanged += p =>
        {
            if (p.Id == 1 && p.TotalBytes == torrent.TotalLength) seen = p;
        };

        await svc.AddTorrentFileAsync(torrent.Path, Downloads);
        await WaitForAsync(() => seen != null, "progress report");

        Assert.Equal(1, seen!.Id);
        Assert.Equal(torrent.TotalLength, seen.TotalBytes);
        Assert.True(seen.DownloadedBytes >= 0 && seen.DownloadedBytes <= seen.TotalBytes);
    }

    [Fact]
    public async Task ExistingContent_CompletesAndStaysCompletedWhenSeedingDisabled()
    {
        var torrent = MakeTorrent();
        torrent.WriteContent(Downloads);
        await using var svc = new TorrentService(OfflineSettings.Create(), DataDir);

        var id = await svc.AddTorrentFileAsync(torrent.Path, Downloads);

        await WaitForAsync(() => svc.GetStats(id).State == DownloadState.Completed, "Completed state");
        var stats = svc.GetStats(id);
        Assert.Equal(100.0, stats.Progress, precision: 3);
        Assert.Contains(svc.GetFiles(id), f => f.Progress >= 100.0);

        await svc.RecheckAsync(id);
        await WaitForAsync(() => svc.GetStats(id).State == DownloadState.Completed, "Completed after recheck");
    }

    [Fact]
    public async Task GlobalSpeedLimits_CanBeChangedAtRuntime()
    {
        var torrent = MakeTorrent();
        await using var svc = new TorrentService(OfflineSettings.Create(), DataDir);
        await svc.AddTorrentFileAsync(torrent.Path, Downloads);

        await svc.SetGlobalSpeedLimitsAsync(2 * 1024 * 1024, 512 * 1024);

        Assert.Single(svc.GetTorrents());
    }

    [Fact]
    public async Task IpFilter_IsLoadedFromSettingsPath()
    {
        var datPath = Path.Combine(_temp.Path, "block.dat");
        File.WriteAllText(datPath, "001.002.003.004 - 001.002.003.010 , 100 , test\n");
        var store = OfflineSettings.Create()
            .Put("enable_ip_filtering", true)
            .Put("ip_filter_path", datPath);
        await using var svc = new TorrentService(store, DataDir);

        await svc.InitializeAsync();

        Assert.Equal(1, svc.IpFilterRangeCount);
        Assert.Null(svc.IpFilterError);
    }

    [Fact]
    public async Task IpFilter_MissingFile_ReportsErrorWithoutFailing()
    {
        var store = OfflineSettings.Create()
            .Put("enable_ip_filtering", true)
            .Put("ip_filter_path", Path.Combine(_temp.Path, "missing.dat"));
        await using var svc = new TorrentService(store, DataDir);

        await svc.InitializeAsync();

        Assert.Equal(0, svc.IpFilterRangeCount);
        Assert.NotNull(svc.IpFilterError);
    }

    [Fact]
    public async Task DhtDisabled_InstallsInertDhtEngine()
    {
        var torrent = MakeTorrent();
        await using var svc = new TorrentService(OfflineSettings.Create(), DataDir);
        var id = await svc.AddTorrentFileAsync(torrent.Path, Downloads);

        // A real MonoTorrent engine switches to Initialising when a torrent starts; the inert replacement does not.
        await Task.Delay(2000);
        Assert.Equal(DhtState.NotReady, svc.Engine!.Dht.State);
        Assert.False(svc.GetStats(id).Paused);
    }

    [Fact]
    public async Task DhtEnabled_DoesNotStartDhtDuringInitialize()
    {
        var store = OfflineSettings.Create().Put("enable_dht", true);
        await using var svc = new TorrentService(store, DataDir);

        await svc.InitializeAsync();

        Assert.Equal(DhtState.NotReady, svc.Engine!.Dht.State);
    }

    [Fact]
    public async Task InitializeTwice_IsHarmless()
    {
        await using var svc = new TorrentService(OfflineSettings.Create(), DataDir);

        await svc.InitializeAsync();
        await svc.InitializeAsync();

        Assert.Empty(svc.GetTorrents());
    }
}
