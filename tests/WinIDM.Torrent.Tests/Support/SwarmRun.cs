using System.Collections.Concurrent;
using MonoTorrent.Client;
using WinIDM.Core.Models;

namespace WinIDM.Torrent.Tests.Support;

/// <summary>One TorrentService in a swarm test, with its own loopback port and data folder.</summary>
internal sealed class Peer
{
    public required TorrentService Service { get; init; }
    public required string Name { get; init; }
    public required int Port { get; init; }
    public required string DataDirectory { get; init; }

    /// <summary>Internal id of the torrent added in this test (one torrent per peer).</summary>
    public long Id { get; set; }

    /// <summary>All <see cref="TorrentService.ProgressChanged"/> reports for this peer.</summary>
    public ConcurrentQueue<DownloadProgress> Reports { get; } = new();

    /// <summary>The real MonoTorrent manager (internal access through InternalsVisibleTo).</summary>
    public MonoTorrent.Client.TorrentManager Manager => Service.Engine!.Torrents.Single();

    public DownloadState State => Service.GetStats(Id).State;
}

/// <summary>
/// Swarm environment for one test: one loopback tracker, several peers, and a unique temporary folder.
/// All peers are shut down and the folder is deleted in <see cref="DisposeAsync"/>.
/// </summary>
internal sealed class SwarmRun : IAsyncDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly List<Peer> _peers = new();

    public LocalHttpTracker Tracker { get; } = new();

    public string Root => _temp.Path;

    public string DownloadsOf(string peerName) => Path.Combine(Root, peerName, "downloads");

    public Task<SourceTorrent> CreateContentAsync(string name, params (string Path, int Size)[] files) =>
        IntegrationKit.CreateSourceTorrentAsync(Root, name, files, Tracker.AnnounceUrl);

    /// <summary>Create a new peer with a free loopback port. The port is not bound until the engine starts.</summary>
    public Peer CreatePeer(string name, bool seedFinished, bool fetchMetadata = false)
    {
        var port = IntegrationKit.ReserveFreePorts(1)[0];
        var dataDirectory = Path.Combine(Root, name, "data");
        var store = OfflineSettings.Create()
            .Put("use_random_port", false)
            .Put("custom_port_number", port)
            .Put("seed_finished_torrent", seedFinished)
            .Put("idm_pref_fetch_metadata", fetchMetadata);

        var peer = new Peer
        {
            Service = new TorrentService(store, dataDirectory),
            Name = name,
            Port = port,
            DataDirectory = dataDirectory,
        };
        _peers.Add(peer);
        return peer;
    }

    /// <summary>Add a .torrent file to the peer and record progress reports.</summary>
    public async Task<long> AddTorrentAsync(Peer peer, SourceTorrent torrent, string saveDirectory)
    {
        peer.Service.ProgressChanged += report => peer.Reports.Enqueue(report);
        peer.Id = await peer.Service.AddTorrentFileAsync(torrent.TorrentPath, saveDirectory).ConfigureAwait(false);
        return peer.Id;
    }

    /// <summary>Add a torrent and pause it immediately, so no transfer happens until the test sets up the state.</summary>
    public async Task<long> AddTorrentPausedAsync(Peer peer, SourceTorrent torrent, string saveDirectory)
    {
        var id = await AddTorrentAsync(peer, torrent, saveDirectory).ConfigureAwait(false);
        await peer.Service.PauseAsync(id).ConfigureAwait(false);
        return id;
    }

    public async Task WaitForStateAsync(Peer peer, DownloadState expected, TimeSpan? timeout = null)
    {
        await IntegrationKit.WaitUntilAsync(
            () => peer.Service.GetStats(peer.Id).State == expected,
            $"{peer.Name} reached state {expected} (now {peer.Service.GetStats(peer.Id).State}, progress {peer.Service.GetStats(peer.Id).Progress:F1}%)",
            timeout ?? TimeSpan.FromSeconds(60)).ConfigureAwait(false);
    }

    /// <summary>Wait until the tracker has registered at least <paramref name="count"/> peers for the torrent.</summary>
    public async Task WaitForTrackerPeersAsync(SourceTorrent torrent, int count = 1)
    {
        await IntegrationKit.WaitUntilAsync(
            () => Tracker.PeerCount(torrent.InfoHash) >= count,
            $"tracker has {count} peers for {torrent.Name}").ConfigureAwait(false);
    }

    /// <summary>Wait for a ProgressChanged report with a given state to appear for this peer.</summary>
    public async Task WaitForReportAsync(Peer peer, Func<DownloadProgress, bool> predicate, string description, TimeSpan? timeout = null)
    {
        await IntegrationKit.WaitUntilAsync(
            () => peer.Reports.Any(r => r.Id == peer.Id && predicate(r)),
            description,
            timeout ?? TimeSpan.FromSeconds(15)).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var peer in _peers)
        {
            try
            {
                await peer.Service.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Cleanup must not disturb a test that already passed or failed.
            }
        }

        await Tracker.DisposeAsync().ConfigureAwait(false);
        _temp.Dispose();
    }
}
