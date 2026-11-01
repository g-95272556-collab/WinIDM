using System.Net;
using System.Text;
using MonoTorrent.Client;

namespace WinIDM.Torrent;

/// <summary>
/// List of blocked IPs (.dat format such as "000.000.000.000 - 000.255.255.255 , 000 , text"
/// and .p2p such as "Name:1.2.3.4-1.2.3.9"). The object is immutable after loading.
/// </summary>
public sealed class IpFilter
{
    private readonly BanList _banList = new();

    private IpFilter(IReadOnlyList<AddressRange> ranges)
    {
        RangeCount = ranges.Count;
        _banList.AddRange(ranges);
    }

    /// <summary>Empty filter (no IPs blocked).</summary>
    public static IpFilter Empty { get; } = new(Array.Empty<AddressRange>());

    /// <summary>Number of loaded ranges.</summary>
    public int RangeCount { get; }

    /// <summary>Returns true if the address falls within any blocked range.</summary>
    public bool IsBlocked(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (RangeCount == 0) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        return _banList.IsBanned(address);
    }

    public static IpFilter Parse(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var ranges = new BanListParser().Parse(stream).ToList();
        return new IpFilter(ranges);
    }

    public static IpFilter FromText(string text)
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(text ?? ""));
        return Parse(ms);
    }

    public static IpFilter FromFile(string path)
    {
        using var fs = File.OpenRead(path);
        return Parse(fs);
    }
}
