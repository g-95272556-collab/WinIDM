using System.Text.RegularExpressions;

namespace WinIDM.Browser;

/// <summary>
/// robots.txt parser (RFC 9309, practical subset). Selects the most specific User-agent group
/// for our product token, or the '*' group when none matches. Path matching: prefix, '*' as any
/// sequence, and '$' as the end marker. The longest rule wins; if equal length, Allow wins.
/// </summary>
public sealed class RobotsTxt
{
    private const string ProductToken = "winidm";

    private readonly List<(Regex Pattern, int Length, bool Allow)> _rules;

    private RobotsTxt(List<(Regex, int, bool)> rules, TimeSpan? crawlDelay)
    {
        _rules = rules;
        CrawlDelay = crawlDelay;
    }

    /// <summary>Delay requested by Crawl-delay (if any).</summary>
    public TimeSpan? CrawlDelay { get; }

    /// <summary>All paths allowed (for example: robots.txt does not exist).</summary>
    public static RobotsTxt AllowAll { get; } = new(new List<(Regex, int, bool)>(), null);

    /// <summary>All paths denied (for example: robots.txt cannot be reached with a server error).</summary>
    public static RobotsTxt DisallowAll { get; } = new(
        new List<(Regex, int, bool)> { (new Regex("^/", RegexOptions.CultureInvariant), 1, false) }, null);

    public static RobotsTxt Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var groups = new List<Group>();
        Group? current = null;
        bool lastWasAgent = false;

        foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw;
            int hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            line = line.Trim();
            if (line.Length == 0) { lastWasAgent = false; continue; }

            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            string field = line[..colon].Trim().ToLowerInvariant();
            string value = line[(colon + 1)..].Trim();

            switch (field)
            {
                case "user-agent":
                    if (current is null || !lastWasAgent)
                    {
                        current = new Group();
                        groups.Add(current);
                    }
                    current.Agents.Add(value.ToLowerInvariant());
                    lastWasAgent = true;
                    break;
                case "allow":
                case "disallow":
                    lastWasAgent = false;
                    if (current is null) break;
                    // An empty Disallow means no restriction.
                    if (value.Length == 0) break;
                    current.Rules.Add((field == "allow", value));
                    break;
                case "crawl-delay":
                    lastWasAgent = false;
                    if (current is not null && double.TryParse(value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double secs) && secs >= 0 && secs <= 3600)
                    {
                        current.CrawlDelay = TimeSpan.FromSeconds(secs);
                    }
                    break;
                default:
                    lastWasAgent = false;
                    break;
            }
        }

        Group? chosen = groups.FirstOrDefault(g => g.Agents.Any(a => a != "*" && ProductToken.StartsWith(a, StringComparison.Ordinal)))
                        ?? groups.FirstOrDefault(g => g.Agents.Contains("*"));
        if (chosen is null) return AllowAll;

        var rules = new List<(Regex, int, bool)>();
        foreach (var (allow, path) in chosen.Rules)
        {
            rules.Add((PathRegex(path), path.Length, allow));
        }
        return new RobotsTxt(rules, chosen.CrawlDelay);
    }

    /// <summary>True if the path (including the query, without the host) is allowed.</summary>
    public bool IsAllowed(string pathAndQuery)
    {
        string path = string.IsNullOrEmpty(pathAndQuery) ? "/" : pathAndQuery;
        bool? best = null;
        int bestLen = -1;
        foreach (var (pattern, length, allow) in _rules)
        {
            if (!pattern.IsMatch(path)) continue;
            if (length > bestLen || (length == bestLen && allow))
            {
                bestLen = length;
                best = allow;
            }
        }
        return best ?? true;
    }

    private static Regex PathRegex(string path)
    {
        bool endAnchor = path.EndsWith('$');
        string body = endAnchor ? path[..^1] : path;
        string pattern = "^" + string.Join(".*", body.Split('*').Select(Regex.Escape)) + (endAnchor ? "$" : "");
        return new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }

    private sealed class Group
    {
        public List<string> Agents { get; } = new();
        public List<(bool Allow, string Path)> Rules { get; } = new();
        public TimeSpan? CrawlDelay { get; set; }
    }
}
