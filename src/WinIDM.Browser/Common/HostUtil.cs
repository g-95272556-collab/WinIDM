using System.Globalization;
using System.Net;

namespace WinIDM.Browser;

/// <summary>Host and URL helpers shared by the browser modules.</summary>
internal static class HostUtil
{
    // Common two-label suffixes. A small, deliberately short list; not a PSL copy.
    private static readonly HashSet<string> TwoLabelSuffixes = new(StringComparer.Ordinal)
    {
        "co.uk", "org.uk", "ac.uk", "gov.uk", "com.au", "net.au", "org.au", "co.jp", "ne.jp",
        "or.jp", "com.my", "edu.my", "gov.my", "org.my", "net.my", "com.sg", "co.nz", "com.br",
        "com.cn", "com.tw", "com.hk", "co.in", "co.kr", "com.mx", "co.za", "com.tr", "com.ar",
    };

    private static readonly IdnMapping Idn = new() { AllowUnassigned = true };

    /// <summary>Normalize a host: lowercase, strip the trailing dot, convert IDN to punycode.</summary>
    public static string NormalizeHost(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        string h = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (h.Length == 0) return h;
        if (h.Any(c => c > 127))
        {
            try { h = Idn.GetAscii(h); }
            catch (ArgumentException) { /* leave as is */ }
        }
        return h;
    }

    public static bool TryGetHttpUri(string? url, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return false;
        uri = parsed;
        return true;
    }

    /// <summary>Approximate eTLD+1. Used to determine third-party status.</summary>
    public static string RegistrableDomain(string host)
    {
        if (IPAddress.TryParse(host, out _)) return host;
        string[] labels = host.Split('.');
        if (labels.Length <= 2) return host;
        string last2 = labels[^2] + "." + labels[^1];
        if (TwoLabelSuffixes.Contains(last2) && labels.Length >= 3)
            return labels[^3] + "." + last2;
        return last2;
    }

    public static bool SameSite(string hostA, string hostB) =>
        string.Equals(RegistrableDomain(hostA), RegistrableDomain(hostB), StringComparison.Ordinal);

    /// <summary>True if <paramref name="host"/> equals or is a subdomain of any entry in the set.</summary>
    public static bool MatchesAnyDomain(string host, IReadOnlyCollection<string> domains)
    {
        if (domains.Count == 0) return false;
        int start = 0;
        while (true)
        {
            string suffix = start == 0 ? host : host[start..];
            if (domains.Contains(suffix)) return true;
            int dot = host.IndexOf('.', start);
            if (dot < 0) return false;
            start = dot + 1;
        }
    }
}
