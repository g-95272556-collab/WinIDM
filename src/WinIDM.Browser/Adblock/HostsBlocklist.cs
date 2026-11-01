namespace WinIDM.Browser;

/// <summary>Details of one managed hosts source.</summary>
public sealed record HostsSourceInfo(string Name, bool Enabled, int DomainCount);

/// <summary>One statistics entry: a listed domain that blocks requests most often.</summary>
public sealed record HostsBlockCount(string Domain, long Count);

/// <summary>Summary of hosts blocking statistics.</summary>
public sealed record HostsBlockStats(long TotalBlocked, DateTimeOffset? SinceUtc, IReadOnlyList<HostsBlockCount> TopDomains);

/// <summary>
/// Hosts source manager: several named sources (which can be enabled or disabled), a domain
/// allowlist, and blocking statistics (total and per matched domain).
/// A match is an exact domain or one of its subdomains. The allowlist always wins over blocking.
/// This class never contacts the network; the caller supplies each source's contents.
/// </summary>
public sealed class HostsBlocklist
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SourceEntry> _sources = new(StringComparer.Ordinal);
    private readonly HashSet<string> _whitelist = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _counts = new(StringComparer.Ordinal);
    private HashSet<string> _merged = new(StringComparer.Ordinal);
    private long _total;
    private DateTimeOffset? _since;

    private sealed record SourceEntry(bool Enabled, HashSet<string> Domains);

    /// <summary>Total unique domains from enabled sources.</summary>
    public int ActiveDomainCount
    {
        get { lock (_gate) return _merged.Count; }
    }

    /// <summary>Add or replace a named source with hosts text (hosts format or one domain per line).</summary>
    public int AddOrReplaceSource(string name, string text, bool enabled = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(text);
        var domains = HostsParser.Parse(text);
        lock (_gate)
        {
            _sources[name] = new SourceEntry(enabled, domains);
            Rebuild();
            return domains.Count;
        }
    }

    public void SetEnabled(string name, bool enabled)
    {
        lock (_gate)
        {
            if (!_sources.TryGetValue(name, out var entry)) throw new KeyNotFoundException("Hosts source not found.");
            _sources[name] = entry with { Enabled = enabled };
            Rebuild();
        }
    }

    public void RemoveSource(string name)
    {
        lock (_gate)
        {
            if (_sources.Remove(name)) Rebuild();
        }
    }

    public IReadOnlyList<HostsSourceInfo> Sources()
    {
        lock (_gate)
        {
            return _sources
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new HostsSourceInfo(kv.Key, kv.Value.Enabled, kv.Value.Domains.Count))
                .ToList();
        }
    }

    /// <summary>Add a domain to the allowlist. Subdomains are exempt too.</summary>
    public void AddWhitelist(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return;
        lock (_gate) _whitelist.Add(HostUtil.NormalizeHost(domain));
    }

    public void RemoveWhitelist(string domain)
    {
        lock (_gate) _whitelist.Remove(HostUtil.NormalizeHost(domain));
    }

    public IReadOnlyList<string> Whitelist()
    {
        lock (_gate) return _whitelist.OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    /// <summary>True if the host is exempted by the allowlist (domain or subdomain match).</summary>
    public bool IsWhitelisted(string host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        lock (_gate) return HostUtil.MatchesAnyDomain(HostUtil.NormalizeHost(host), _whitelist);
    }

    /// <summary>
    /// Check a request. If it is blocked (and the page host is not allowlisted), the count is increased
    /// and this returns true. The caller is responsible for blocking the request.
    /// </summary>
    public bool TryBlock(string requestUrl, string? pageUrl = null)
    {
        if (!HostUtil.TryGetHttpUri(requestUrl, out var reqUri)) return false;
        string host = HostUtil.NormalizeHost(reqUri.Host);

        lock (_gate)
        {
            if (_merged.Count == 0) return false;
            if (HostUtil.MatchesAnyDomain(host, _whitelist)) return false;
            if (HostUtil.TryGetHttpUri(pageUrl, out var pageUri) &&
                HostUtil.MatchesAnyDomain(HostUtil.NormalizeHost(pageUri.Host), _whitelist))
            {
                return false;
            }

            string? matched = MatchDomain(host);
            if (matched is null) return false;

            _total++;
            _since ??= DateTimeOffset.UtcNow;
            _counts[matched] = _counts.GetValueOrDefault(matched) + 1;
            return true;
        }
    }

    /// <summary>Current statistics. <paramref name="top"/> is the number of top domains returned.</summary>
    public HostsBlockStats GetStats(int top = 20)
    {
        if (top < 0) throw new ArgumentOutOfRangeException(nameof(top));
        lock (_gate)
        {
            var list = _counts
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Take(top)
                .Select(kv => new HostsBlockCount(kv.Key, kv.Value))
                .ToList();
            return new HostsBlockStats(_total, _since, list);
        }
    }

    public void ResetStats()
    {
        lock (_gate)
        {
            _counts.Clear();
            _total = 0;
            _since = null;
        }
    }

    // ---------------------------------------------------------------------

    private string? MatchDomain(string host)
    {
        int start = 0;
        while (true)
        {
            string suffix = start == 0 ? host : host[start..];
            if (_merged.Contains(suffix)) return suffix;
            int dot = host.IndexOf('.', start);
            if (dot < 0) return null;
            start = dot + 1;
        }
    }

    private void Rebuild()
    {
        var merged = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in _sources.Values)
        {
            if (!entry.Enabled) continue;
            merged.UnionWith(entry.Domains);
        }
        _merged = merged;
    }
}

/// <summary>Hosts text parser. Same rules as AdBlocker.LoadHostsList.</summary>
internal static class HostsParser
{
    private static readonly HashSet<string> IgnoredHostNames = new(StringComparer.Ordinal)
    {
        "localhost", "localhost.localdomain", "local", "broadcasthost", "ip6-localhost",
        "ip6-loopback", "ip6-localnet", "ip6-mcastprefix", "ip6-allnodes", "ip6-allrouters", "ip6-allhosts",
        "0.0.0.0",
    };

    public static HashSet<string> Parse(string text)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (string raw in text.Split('\n'))
        {
            string line = raw;
            int hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            line = line.Trim();
            if (line.Length == 0) continue;

            string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && System.Net.IPAddress.TryParse(parts[0], out _))
            {
                for (int i = 1; i < parts.Length; i++) Add(result, parts[i]);
            }
            else if (parts.Length == 1)
            {
                Add(result, parts[0]);
            }
        }
        return result;
    }

    private static void Add(HashSet<string> set, string raw)
    {
        string h = raw.Trim().TrimEnd('.').ToLowerInvariant();
        if (h.Length == 0 || IgnoredHostNames.Contains(h)) return;
        if (h.StartsWith("ip6-", StringComparison.Ordinal)) return;
        if (System.Net.IPAddress.TryParse(h, out _)) return;
        if (h.StartsWith("www.", StringComparison.Ordinal)) h = h[4..];
        string normalized = HostUtil.NormalizeHost(h);
        if (normalized.Length > 0) set.Add(normalized);
    }
}
