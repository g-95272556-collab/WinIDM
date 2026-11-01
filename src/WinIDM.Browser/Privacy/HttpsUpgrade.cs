using System.Net;

namespace WinIDM.Browser;

/// <summary>
/// HTTPS by default (Brave "HTTPS upgrade", standard mode): a top-level <c>http://</c> navigation is tried as
/// <c>https://</c> first, and the caller falls back to HTTP for the session when the secure attempt fails.
/// Hosts where HTTPS cannot work are never tried: IP literals, single-label and local names, Tor/I2P addresses
/// and URLs with an explicit non-default port.
/// </summary>
public static class HttpsUpgrade
{
    private static readonly string[] LocalSuffixes =
    {
        ".localhost", ".local", ".lan", ".home", ".internal", ".intranet", ".corp", ".test", ".example",
        ".invalid", ".onion", ".i2p", ".arpa",
    };

    /// <summary>The <c>https://</c> form of <paramref name="url"/>, or <c>null</c> when it should stay as it is.</summary>
    /// <param name="httpOnlyHosts">Hosts that already failed over HTTPS in this session.</param>
    public static string? TryUpgrade(string url, ICollection<string> httpOnlyHosts)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp) return null;
        if (!uri.IsDefaultPort) return null;
        var host = HostUtil.NormalizeHost(uri.Host);
        if (!CanUpgradeHost(host) || httpOnlyHosts.Contains(host)) return null;

        var b = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 };
        return b.Uri.AbsoluteUri;
    }

    /// <summary>The <c>http://</c> form of an upgraded URL (used for the fallback).</summary>
    public static string Downgrade(string httpsUrl)
    {
        var uri = new Uri(httpsUrl);
        return new UriBuilder(uri) { Scheme = Uri.UriSchemeHttp, Port = -1 }.Uri.AbsoluteUri;
    }

    public static bool CanUpgradeHost(string host)
    {
        if (host.Length == 0 || !host.Contains('.')) return false;
        if (host.StartsWith('[') || IPAddress.TryParse(host, out _)) return false;
        return !LocalSuffixes.Any(s => host.EndsWith(s, StringComparison.Ordinal));
    }
}
