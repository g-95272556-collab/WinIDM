using WinIDM.App.Services;
using WinIDM.Engine;

namespace WinIDM.App.Tests;

/// <summary>
/// The status bar describes the engine's own network policy. Wired Ethernet counts as Wi-Fi for "Wi-Fi only",
/// exactly as the engine does, so the label never claims downloads are held back when the engine runs them.
/// </summary>
public class NetworkStatusTests
{
    private sealed class Policy(bool online, bool wifiOrEthernet, bool metered) : INetworkPolicy
    {
        public bool IsOnline => online;
        public bool IsWifiOrEthernet => wifiOrEthernet;
        public bool IsMetered => metered;
        public event EventHandler? Changed { add { } remove { } }
    }

    [Theory]
    [InlineData(false, false, false, "No connection")]
    [InlineData(true, true, false, "Wi-Fi / wired")]
    [InlineData(true, false, false, "Mobile data")]
    [InlineData(true, true, true, "Metered connection")]
    [InlineData(true, false, true, "Mobile data (metered)")]
    public void Label_ReflectsEnginePolicy(bool online, bool wifiOrEthernet, bool metered, string expected) =>
        Assert.Equal(expected, NetworkStatus.From(new Policy(online, wifiOrEthernet, metered)).Label);

    [Theory]
    [InlineData(true, true, false, true, false)]   // Ethernet or Wi-Fi: never blocked
    [InlineData(true, false, false, true, true)]   // mobile data with Wi-Fi only: blocked
    [InlineData(true, false, false, false, false)] // Wi-Fi only off: not blocked
    [InlineData(false, false, false, true, false)] // offline is reported as "No connection", not as Wi-Fi only
    public void BlocksWifiOnly_MatchesEngineRule(bool online, bool wifiOrEthernet, bool metered, bool wifiOnly, bool expected) =>
        Assert.Equal(expected, NetworkStatus.From(new Policy(online, wifiOrEthernet, metered)).BlocksWifiOnly(wifiOnly));
}
