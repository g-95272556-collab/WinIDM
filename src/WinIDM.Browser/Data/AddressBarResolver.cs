using System.Net;
using System.Text.RegularExpressions;

namespace WinIDM.Browser;

/// <summary>Kind of address bar result.</summary>
public enum AddressKind
{
    Empty,

    /// <summary>An http, https, or about: URL that can be loaded directly.</summary>
    Url,

    /// <summary>A search using the selected engine.</summary>
    Search,

    /// <summary>Other schemes (mailto:, intent:, etc.). The app must ask the user to confirm.</summary>
    ExternalScheme,

    /// <summary>Dangerous schemes (javascript:, vbscript:, data:, file:). Not opened.</summary>
    Blocked,
}

public sealed record AddressResolution(AddressKind Kind, string? Target);

/// <summary>
/// Tells URLs apart from search text (BRW-02). Input that looks like a host or an IP address
/// is opened as https; anything else goes to the search engine.
/// </summary>
public static partial class AddressBarResolver
{
    private static readonly HashSet<string> BlockedSchemes = new(StringComparer.Ordinal)
    {
        "javascript", "vbscript", "data", "file",
    };

    [GeneratedRegex(@"^([a-zA-Z][a-zA-Z0-9+.\-]*):", RegexOptions.CultureInvariant)]
    private static partial Regex SchemePattern();

    [GeneratedRegex(@"^[^/\s]+:\d+(?:[/?#].*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex HostPortPattern();

    [GeneratedRegex(@"^[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(?:\.[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)*\.(?:[a-zA-Z]{2,}|xn--[a-zA-Z0-9-]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex DomainPattern();

    /// <summary>Decide the target for address bar input. <paramref name="searchTemplate"/> is a {q} template.</summary>
    public static AddressResolution Resolve(string? input, string searchTemplate)
    {
        string text = (input ?? "").Trim();
        if (text.Length == 0) return new AddressResolution(AddressKind.Empty, null);
        if (text.Any(char.IsControl)) return new AddressResolution(AddressKind.Blocked, null);

        var scheme = SchemePattern().Match(text);
        if (scheme.Success && !HostPortPattern().IsMatch(text))
        {
            string name = scheme.Groups[1].Value.ToLowerInvariant();
            if (name is "http" or "https")
            {
                if (HostUtil.TryGetHttpUri(text, out var uri)) return new AddressResolution(AddressKind.Url, uri.AbsoluteUri);
                return Search(text, searchTemplate);
            }
            if (name == "about") return new AddressResolution(AddressKind.Url, text);
            if (BlockedSchemes.Contains(name)) return new AddressResolution(AddressKind.Blocked, null);
            return new AddressResolution(AddressKind.ExternalScheme, text);
        }

        if (!text.Any(char.IsWhiteSpace) && LooksLikeHost(text))
        {
            string candidate = "https://" + text;
            if (HostUtil.TryGetHttpUri(candidate, out var uri)) return new AddressResolution(AddressKind.Url, uri.AbsoluteUri);
        }

        return Search(text, searchTemplate);
    }

    private static AddressResolution Search(string text, string template)
    {
        if (!SearchEngines.IsValidTemplate(template))
            throw new ArgumentException("Invalid search template.", nameof(template));
        return new AddressResolution(AddressKind.Search,
            template.Replace(SearchEngines.QueryPlaceholder, Uri.EscapeDataString(text), StringComparison.Ordinal));
    }

    private static bool LooksLikeHost(string text)
    {
        int stop = text.IndexOfAny(new[] { '/', '?', '#' });
        string authority = stop < 0 ? text : text[..stop];
        if (authority.Length == 0) return false;

        // Strip the port if present.
        string host = authority;
        int colon = authority.LastIndexOf(':');
        if (colon > 0 && authority.IndexOf(':') == colon)
        {
            string port = authority[(colon + 1)..];
            if (port.Length == 0 || !port.All(char.IsDigit)) return false;
            host = authority[..colon];
        }

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;

        // Accept IPv6 (contains ':') or IPv4 with exactly three dots. This keeps plain numbers
        // like "1234" or "1.5" from being mistaken for IP addresses.
        string bare = host.Trim('[', ']');
        if (bare.Contains(':') && IPAddress.TryParse(bare, out _)) return true;
        if (bare.Count(c => c == '.') == 3 && bare.All(c => char.IsDigit(c) || c == '.') &&
            IPAddress.TryParse(bare, out _))
            return true;

        return DomainPattern().IsMatch(host);
    }
}
