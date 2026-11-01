using System.Net;
using MonoTorrent.Connections.Peer;
using ReusableTasks;

namespace WinIDM.Torrent;

/// <summary>
/// Peer connection gate. Used by the MonoTorrent connection creators so that outgoing
/// and incoming connections to blocked IPs are rejected. <see cref="Filter"/> can be swapped at runtime.
/// </summary>
internal sealed class PeerConnectionGate
{
    private volatile IpFilter _filter = IpFilter.Empty;

    public IpFilter Filter
    {
        get => _filter;
        set => _filter = value ?? IpFilter.Empty;
    }

    public bool IsBlocked(IPEndPoint? endPoint) => endPoint != null && _filter.IsBlocked(endPoint.Address);

    public IPeerConnection Wrap(IPeerConnection inner) => new FilteredPeerConnection(inner, this);
}

/// <summary>Outgoing connection that refuses to connect when its address is blocked.</summary>
internal sealed class FilteredPeerConnection : IPeerConnection
{
    private readonly IPeerConnection _inner;
    private readonly PeerConnectionGate _gate;

    public FilteredPeerConnection(IPeerConnection inner, PeerConnectionGate gate)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
    }

    public ReusableTask ConnectAsync()
    {
        if (_gate.IsBlocked(_inner.EndPoint))
            throw new InvalidOperationException("Peer address is blocked by the IP filter.");
        return _inner.ConnectAsync();
    }

    public ReusableTask<int> ReceiveAsync(Memory<byte> buffer) => _inner.ReceiveAsync(buffer);

    public ReusableTask<int> SendAsync(Memory<byte> buffer) => _inner.SendAsync(buffer);

    public ReadOnlyMemory<byte> AddressBytes => _inner.AddressBytes;
    public bool CanReconnect => _inner.CanReconnect;
    public bool Disposed => _inner.Disposed;
    public IPEndPoint EndPoint => _inner.EndPoint!;
    public bool IsIncoming => _inner.IsIncoming;
    public Uri Uri => _inner.Uri;

    public void Dispose() => _inner.Dispose();
}

/// <summary>Incoming listener that drops connections from blocked IPs before they reach the engine.</summary>
internal sealed class FilteredPeerConnectionListener : IPeerConnectionListener
{
    private readonly IPeerConnectionListener _inner;
    private readonly PeerConnectionGate _gate;

    public FilteredPeerConnectionListener(IPeerConnectionListener inner, PeerConnectionGate gate)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _inner.ConnectionReceived += OnConnectionReceived;
    }

    public event EventHandler<PeerConnectionEventArgs>? ConnectionReceived;

    public event EventHandler<EventArgs>? StatusChanged
    {
        add => _inner.StatusChanged += value;
        remove => _inner.StatusChanged -= value;
    }

    public IPEndPoint LocalEndPoint => _inner.LocalEndPoint!;
    public IPEndPoint PreferredLocalEndPoint => _inner.PreferredLocalEndPoint!;
    public MonoTorrent.Connections.ListenerStatus Status => _inner.Status;

    public void Start() => _inner.Start();
    public void Stop() => _inner.Stop();

    private void OnConnectionReceived(object? sender, PeerConnectionEventArgs e)
    {
        if (_gate.IsBlocked(e.Connection.EndPoint))
        {
            e.Connection.Dispose();
            return;
        }
        ConnectionReceived?.Invoke(this, e);
    }
}
