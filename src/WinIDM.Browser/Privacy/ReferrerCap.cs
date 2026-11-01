namespace WinIDM.Browser;

/// <summary>
/// Cross-site referrer cap (Brave): a request to another site carries only the origin of the page, whatever
/// Referrer-Policy the page asked for. Same-site requests keep the full referrer.
/// </summary>
public static class ReferrerCap
{
    /// <summary>The capped referrer (<c>scheme://host[:port]/</c>), or <c>null</c> when the header can stay.</summary>
    public static string? Cap(string referer, string requestUrl)
    {
        if (!HostUtil.TryGetHttpUri(referer, out var from) || !HostUtil.TryGetHttpUri(requestUrl, out var to)) return null;
        if (HostUtil.SameSite(HostUtil.NormalizeHost(from.Host), HostUtil.NormalizeHost(to.Host))) return null;

        var origin = from.GetLeftPart(UriPartial.Authority) + "/";
        return string.Equals(origin, referer, StringComparison.Ordinal) ? null : origin;
    }
}
