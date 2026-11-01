using System.Net;
using MonoTorrent;
using MonoTorrent.Dht;
using MonoTorrent.Connections.Dht;

namespace WinIDM.Torrent;

/// <summary>
/// Disabled DHT engine for the enable_dht = false setting. MonoTorrent 3.0.2 starts DHT
/// whenever any torrent starts, even when the torrent's AllowDht is false, so this class
/// replaces the real engine so that no bootstrap nodes are contacted.
/// </summary>
internal sealed class DisabledDhtEngine : IDhtEngine
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    public DisabledDhtEngine()
    {
        Monitor = new NullTransferMonitor();
    }

    public event EventHandler<PeersFoundEventArgs>? PeersFound { add { } remove { } }
    public event EventHandler? StateChanged { add { } remove { } }

    public ITransferMonitor Monitor { get; }
    public TimeSpan AnnounceInterval => Interval;
    public TimeSpan MinimumAnnounceInterval => Interval;
    public bool Disposed => false;
    public int NodeCount => 0;
    public DhtState State => DhtState.NotReady;

    public void Dispose() { }

    public void Add(IEnumerable<ReadOnlyMemory<byte>> nodes) { }
    public void Announce(InfoHash infoHash, int port) { }
    public void GetPeers(InfoHash infoHash) { }
    public Task<ReadOnlyMemory<byte>> SaveNodesAsync() => Task.FromResult(ReadOnlyMemory<byte>.Empty);
    public Task SetListenerAsync(IDhtListener listener) => Task.CompletedTask;
    public Task StartAsync() => Task.CompletedTask;
    public Task StartAsync(ReadOnlyMemory<byte> initialNodes) => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;

    private sealed class NullTransferMonitor : ITransferMonitor
    {
        public long BytesSent => 0;
        public long BytesReceived => 0;
        public long DownloadRate => 0;
        public long UploadRate => 0;
    }
}
