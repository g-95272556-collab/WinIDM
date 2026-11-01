namespace WinIDM.Browser;

/// <summary>
/// Removes known click and campaign tracking parameters from a top-level page URL (Brave "query filter",
/// uBO <c>removeparam</c>). Meant for main-frame navigations only: media, segment and API requests keep every
/// parameter, and a URL that carries a signature is never touched because removing anything would invalidate it.
/// </summary>
public static class TrackingParams
{
    private static readonly HashSet<string> Global = new(StringComparer.OrdinalIgnoreCase)
    {
        "fbclid", "gclid", "gclsrc", "dclid", "gbraid", "wbraid", "msclkid", "yclid", "twclid", "ttclid",
        "li_fat_id", "mc_eid", "mc_cid", "_hsenc", "_hsmi", "__hssc", "__hstc", "__hsfp", "hsCtaTracking",
        "_openstat", "oly_anon_id", "oly_enc_id", "rb_clickid", "s_cid", "vero_conv", "vero_id", "wickedid",
        "ml_subscriber", "ml_subscriber_hash", "_branch_match_id", "_branch_referrer", "srsltid", "mkt_tok",
        "epik", "ef_id", "irclickid",
    };

    /// <summary>Parameters that are tracking on one site and may be meaningful elsewhere.</summary>
    private static readonly (string Host, string Name)[] Scoped =
    {
        ("instagram.com", "igshid"), ("instagram.com", "igsh"),
        ("youtube.com", "si"), ("youtu.be", "si"), ("spotify.com", "si"),
        ("twitter.com", "s"), ("twitter.com", "t"), ("x.com", "s"), ("x.com", "t"),
        ("tiktok.com", "_r"), ("tiktok.com", "_t"),
    };

    /// <summary>Any of these means the query is signed (S3, CloudFront, Azure SAS, Google Cloud, generic tokens).</summary>
    private static readonly HashSet<string> SignatureNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "signature", "sig", "x-amz-signature", "x-goog-signature", "hmac", "token", "expires", "policy",
        "key-pair-id", "se", "sv", "hdnts", "hdnea", "auth", "md5",
    };

    /// <summary>The URL without tracking parameters, or <c>null</c> when nothing would change.</summary>
    public static string? Strip(string url) => Strip(url, out _);

    /// <inheritdoc cref="Strip(string)"/>
    /// <param name="removed">How many parameters were removed.</param>
    public static string? Strip(string url, out int removed)
    {
        removed = 0;
        if (!HostUtil.TryGetHttpUri(url, out var uri) || uri.Query.Length <= 1) return null;
        var host = HostUtil.NormalizeHost(uri.Host);

        var pairs = uri.Query[1..].Split('&');
        var names = pairs.Select(NameOf).ToArray();
        if (names.Any(SignatureNames.Contains)) return null;

        var kept = new List<string>(pairs.Length);
        for (int i = 0; i < pairs.Length; i++)
        {
            if (pairs[i].Length == 0) continue;
            if (IsTracking(names[i], host)) { removed++; continue; }
            kept.Add(pairs[i]);
        }
        if (removed == 0) return null;

        // Rebuild from the original text so the kept parameters keep their exact encoding.
        var b = new UriBuilder(uri) { Query = string.Join('&', kept) };
        if (uri.IsDefaultPort) b.Port = -1;
        return b.Uri.AbsoluteUri;
    }

    private static string NameOf(string pair)
    {
        int eq = pair.IndexOf('=');
        var raw = eq < 0 ? pair : pair[..eq];
        try { return Uri.UnescapeDataString(raw.Replace('+', ' ')); }
        catch (UriFormatException) { return raw; }
    }

    private static bool IsTracking(string name, string host)
    {
        if (name.StartsWith("utm_", StringComparison.OrdinalIgnoreCase)) return true;
        if (Global.Contains(name)) return true;
        foreach (var (h, n) in Scoped)
        {
            if (string.Equals(n, name, StringComparison.Ordinal) && (host == h || host.EndsWith("." + h, StringComparison.Ordinal)))
                return true;
        }
        return false;
    }
}
