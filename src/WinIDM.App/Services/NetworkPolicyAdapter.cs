using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using WinConnectivity = Windows.Networking.Connectivity;
using WinIDM.Engine;

namespace WinIDM.App.Services;

/// <summary>
/// Adapter from <see cref="INetworkPolicy"/> to <c>Windows.Networking.Connectivity</c> (primary Internet connection profile).
/// State is cached for 1 second and invalidated when the <c>NetworkStatusChanged</c> event arrives.
/// If Windows has no Internet profile (for example some VPNs), state is read from the network interfaces.
/// </summary>
public sealed class NetworkPolicyAdapter : INetworkPolicy, IDisposable
{
    /// <summary>IANA ifType 6 = Ethernet (ethernetCsmacd).</summary>
    private const uint IanaEthernet = 6;

    /// <summary>IANA ifType 71 = Wi-Fi (ieee80211).</summary>
    private const uint IanaWifi = 71;

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private EventHandler? _changed;
    private bool _subscribed;
    private Snapshot? _cache;
    private long _cacheTicks;

    private sealed record Snapshot(bool Online, bool Metered, bool WifiOrEthernet);

    /// <summary>Internet is available. Constrained (ConstrainedInternetAccess) is treated as online so downloads are not blocked incorrectly.</summary>
    public bool IsOnline => Current().Online;

    /// <summary>Wi-Fi or Ethernet (not mobile data).</summary>
    public bool IsWifiOrEthernet => Current().WifiOrEthernet;

    /// <summary>Fixed or variable cost, or a data limit that is near or reached. Unknown cost is treated as unmetered.</summary>
    public bool IsMetered => Current().Metered;

    public event EventHandler? Changed
    {
        add
        {
            lock (_gate)
            {
                _changed += value;
                if (_subscribed) return;

                WinConnectivity.NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
                _subscribed = true;
            }
        }
        remove
        {
            lock (_gate) _changed -= value;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (!_subscribed) return;

            WinConnectivity.NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;
            _subscribed = false;
        }
    }

    private void OnNetworkStatusChanged(object? sender)
    {
        lock (_gate) _cache = null;
        _changed?.Invoke(this, EventArgs.Empty);
    }

    private Snapshot Current()
    {
        lock (_gate)
        {
            if (_cache is not null && Environment.TickCount64 - _cacheTicks < CacheLifetime.TotalMilliseconds)
            {
                return _cache;
            }
        }

        var snapshot = Read();
        lock (_gate)
        {
            _cache = snapshot;
            _cacheTicks = Environment.TickCount64;
        }

        return snapshot;
    }

    private static Snapshot Read()
    {
        WinConnectivity.ConnectionProfile? profile;
        try
        {
            profile = WinConnectivity.NetworkInformation.GetInternetConnectionProfile();
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            profile = null;
        }

        if (profile is null) return ReadFromInterfaces();

        var level = profile.GetNetworkConnectivityLevel();
        var online = level is WinConnectivity.NetworkConnectivityLevel.InternetAccess
            or WinConnectivity.NetworkConnectivityLevel.ConstrainedInternetAccess;

        var cost = profile.GetConnectionCost();
        var metered = cost.NetworkCostType is WinConnectivity.NetworkCostType.Fixed or WinConnectivity.NetworkCostType.Variable
                      || cost.ApproachingDataLimit
                      || cost.OverDataLimit;

        var ifType = profile.NetworkAdapter?.IanaInterfaceType;
        var wifiOrEthernet = profile.IsWlanConnectionProfile || ifType == IanaWifi || ifType == IanaEthernet;

        return new Snapshot(online, metered, wifiOrEthernet);
    }

    /// <summary>Fallback when there is no Internet profile: online if any interface is active; metered state unknown.</summary>
    private static Snapshot ReadFromInterfaces()
    {
        try
        {
            var active = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .ToList();

            var wifiOrEthernet = active.Any(n =>
                n.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet);
            return new Snapshot(NetworkInterface.GetIsNetworkAvailable(), Metered: false, wifiOrEthernet);
        }
        catch (NetworkInformationException)
        {
            return new Snapshot(false, Metered: false, WifiOrEthernet: false);
        }
    }
}
