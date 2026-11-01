using System.Net;
using MonoTorrent;
using MonoTorrent.Connections;
using MonoTorrent.Connections.Peer;
using ReusableTasks;
using WinIDM.Torrent.Tests.Support;

namespace WinIDM.Torrent.Tests;

public class IpFilterTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void DatFormat_BlocksAddressesInsideRange()
    {
        const string dat =
            "000.000.000.000 - 000.255.255.255 , 000 , reserved\n" +
            "001.002.003.004 - 001.002.003.010 , 100 , test range\n";

        var filter = IpFilter.FromText(dat);

        Assert.Equal(2, filter.RangeCount);
        Assert.True(filter.IsBlocked(IPAddress.Parse("1.2.3.4")));
        Assert.True(filter.IsBlocked(IPAddress.Parse("1.2.3.10")));
        Assert.True(filter.IsBlocked(IPAddress.Parse("0.1.2.3")));
        Assert.False(filter.IsBlocked(IPAddress.Parse("1.2.3.11")));
        Assert.False(filter.IsBlocked(IPAddress.Parse("8.8.8.8")));
    }

    [Fact]
    public void P2pFormat_BlocksAddressesInsideRange()
    {
        const string p2p = "Example List:1.2.3.4-1.2.3.9\n";

        var filter = IpFilter.FromText(p2p);

        Assert.Equal(1, filter.RangeCount);
        Assert.True(filter.IsBlocked(IPAddress.Parse("1.2.3.5")));
        Assert.False(filter.IsBlocked(IPAddress.Parse("1.2.3.10")));
    }

    [Fact]
    public void EmptyFilter_BlocksNothing()
    {
        Assert.Equal(0, IpFilter.Empty.RangeCount);
        Assert.False(IpFilter.Empty.IsBlocked(IPAddress.Parse("1.2.3.4")));
    }

    [Fact]
    public void FromFile_LoadsRangesFromDisk()
    {
        var path = Path.Combine(_temp.Path, "blocklist.dat");
        File.WriteAllText(path, "010.000.000.000 - 010.000.000.255 , 000 , lan\n");

        var filter = IpFilter.FromFile(path);

        Assert.True(filter.IsBlocked(IPAddress.Parse("10.0.0.7")));
        Assert.False(filter.IsBlocked(IPAddress.Parse("11.0.0.7")));
    }

    [Fact]
    public void FilteredOutboundConnection_RefusesBlockedAddress()
    {
        var gate = new PeerConnectionGate { Filter = IpFilter.FromText("1.2.3.4 - 1.2.3.4 , 000 , x\n") };
        var inner = new FakePeerConnection(new IPEndPoint(IPAddress.Parse("1.2.3.4"), 6881));
        var wrapped = gate.Wrap(inner);

        Assert.Throws<InvalidOperationException>(() => wrapped.ConnectAsync());
        Assert.False(inner.ConnectCalled);
    }

    [Fact]
    public void FilteredOutboundConnection_AllowsOtherAddresses()
    {
        var gate = new PeerConnectionGate { Filter = IpFilter.FromText("1.2.3.4 - 1.2.3.4 , 000 , x\n") };
        var inner = new FakePeerConnection(new IPEndPoint(IPAddress.Parse("9.9.9.9"), 6881));
        var wrapped = gate.Wrap(inner);

        wrapped.ConnectAsync();

        Assert.True(inner.ConnectCalled);
    }

    [Fact]
    public void FilteredListener_DisposesBlockedIncomingConnection()
    {
        var gate = new PeerConnectionGate { Filter = IpFilter.FromText("5.6.7.8 - 5.6.7.8 , 000 , x\n") };
        var listener = new FakeListener();
        var filtered = new FilteredPeerConnectionListener(listener, gate);

        var forwarded = new List<IPeerConnection>();
        filtered.ConnectionReceived += (_, e) => forwarded.Add(e.Connection);

        var blocked = new FakePeerConnection(new IPEndPoint(IPAddress.Parse("5.6.7.8"), 40000));
        var allowed = new FakePeerConnection(new IPEndPoint(IPAddress.Parse("5.6.7.9"), 40001));
        listener.Raise(blocked);
        listener.Raise(allowed);

        Assert.True(blocked.Disposed);
        Assert.False(allowed.Disposed);
        Assert.Single(forwarded);
        Assert.Same(allowed, forwarded[0]);
    }

    private sealed class FakePeerConnection : IPeerConnection
    {
        public FakePeerConnection(IPEndPoint endPoint)
        {
            EndPoint = endPoint;
        }

        public bool ConnectCalled { get; private set; }
        public bool Disposed { get; private set; }

        public ReusableTask ConnectAsync()
        {
            ConnectCalled = true;
            return ReusableTask.CompletedTask;
        }

        public ReusableTask<int> ReceiveAsync(Memory<byte> buffer) => ReusableTask.FromResult(0);
        public ReusableTask<int> SendAsync(Memory<byte> buffer) => ReusableTask.FromResult(buffer.Length);
        public ReadOnlyMemory<byte> AddressBytes => EndPoint.Address.GetAddressBytes();
        public bool CanReconnect => false;
        public IPEndPoint EndPoint { get; }
        public bool IsIncoming => false;
        public Uri Uri => new Uri($"ipv4://{EndPoint}");

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeListener : IPeerConnectionListener
    {
        public event EventHandler<PeerConnectionEventArgs>? ConnectionReceived;
        public event EventHandler<EventArgs>? StatusChanged { add { } remove { } }

        public IPEndPoint LocalEndPoint => new(IPAddress.Loopback, 6881);
        public IPEndPoint PreferredLocalEndPoint => LocalEndPoint;
        public ListenerStatus Status => ListenerStatus.Listening;

        public void Start() { }
        public void Stop() { }

        public void Raise(IPeerConnection connection) =>
            ConnectionReceived?.Invoke(this, new PeerConnectionEventArgs(connection, new InfoHash(new byte[20])));
    }
}
