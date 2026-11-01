using System.Text;
using WinIDM.Core.Contracts;

namespace WinIDM.Browser;

/// <summary>Compact filter engine statistics.</summary>
public sealed record AdBlockerStats(
    int NetworkRules,
    int ExceptionRules,
    int HostRules,
    int CosmeticRules,
    int UnsupportedRules,
    int BadFilteredRules,
    int WhitelistedPages);

/// <summary>
/// Ad/tracker blocking engine. Supports basic ABP/uBO filters (||domain^, @@, $third-party,
/// $script/$image/$xmlhttprequest/$domain=, wildcards, regex, $badfilter), hosts lists, site
/// allowlists, and ##selector cosmetic filters. No scriptlets or redirects are loaded.
/// All matching happens locally; the engine never contacts the network.
/// </summary>
public sealed class AdBlocker : IAdBlocker
{
    private static readonly HashSet<string> IgnoredHostNames = new(StringComparer.Ordinal)
    {
        "localhost", "localhost.localdomain", "local", "broadcasthost", "ip6-localhost",
        "ip6-loopback", "ip6-localnet", "ip6-mcastprefix", "ip6-allnodes", "ip6-allrouters", "ip6-allhosts",
        "0.0.0.0",
    };

    private readonly ReaderWriterLockSlim _gate = new(LockRecursionPolicy.NoRecursion);
    private readonly RuleIndex _block = new();
    private readonly RuleIndex _exceptions = new();
    private readonly CosmeticIndex _cosmetic = new();
    private readonly Dictionary<string, List<FilterRule>> _rulesByText = new(StringComparer.Ordinal);
    private readonly HashSet<string> _badFilters = new(StringComparer.Ordinal);
    private readonly HashSet<string> _hosts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pageWhitelist = new(StringComparer.Ordinal);
    private readonly List<string> _sources = new();

    private int _hostRules;
    private int _unsupported;
    private int _badFiltered;
    private int _documentExceptions;
    private int _importantBlocks;

    /// <summary>Names of sources loaded so far (for display and diagnostics).</summary>
    public IReadOnlyList<string> Sources
    {
        get
        {
            _gate.EnterReadLock();
            try { return _sources.ToArray(); }
            finally { _gate.ExitReadLock(); }
        }
    }

    public AdBlockerStats Stats
    {
        get
        {
            _gate.EnterReadLock();
            try
            {
                return new AdBlockerStats(
                    _block.Count, _exceptions.Count, _hostRules, _cosmetic.Count,
                    _unsupported, _badFiltered, _pageWhitelist.Count);
            }
            finally { _gate.ExitReadLock(); }
        }
    }

    /// <summary>Total active rules (network, exceptions, hosts).</summary>
    public int RuleCount
    {
        get
        {
            var s = Stats;
            return s.NetworkRules + s.ExceptionRules + s.HostRules;
        }
    }

    /// <summary>Load an ABP/uBO-style list. Hosts lines (0.0.0.0 domain) are accepted too.</summary>
    public void LoadFilterList(string name, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _gate.EnterWriteLock();
        try
        {
            _sources.Add(name);
            foreach (string raw in text.Split('\n'))
                ProcessFilterLine(raw);
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    /// <summary>Load a hosts list (hosts format or one domain per line).</summary>
    public void LoadHostsList(string name, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _gate.EnterWriteLock();
        try
        {
            _sources.Add(name);
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
                    for (int i = 1; i < parts.Length; i++) AddHostInternal(parts[i]);
                }
                else if (parts.Length == 1)
                {
                    AddHostInternal(parts[0]);
                }
            }
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    /// <summary>Page allowlist: all requests from this host bypass filtering.</summary>
    public void AddWhitelistHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return;
        _gate.EnterWriteLock();
        try { _pageWhitelist.Add(HostUtil.NormalizeHost(host)); }
        finally { _gate.ExitWriteLock(); }
    }

    public void RemoveWhitelistHost(string host)
    {
        _gate.EnterWriteLock();
        try { _pageWhitelist.Remove(HostUtil.NormalizeHost(host)); }
        finally { _gate.ExitWriteLock(); }
    }

    /// <summary>
    /// Mark one rule as bad (false positive). Matching rules are disabled and will not be
    /// loaded again on the next load.
    /// </summary>
    public void MarkBadFilter(string ruleText)
    {
        if (string.IsNullOrWhiteSpace(ruleText)) return;
        var outcome = FilterParser.Parse(ruleText);
        string key = outcome.Rule?.RawText ?? outcome.BadFilterTarget ?? ruleText.Trim();
        _gate.EnterWriteLock();
        try { DisableByText(key); }
        finally { _gate.ExitWriteLock(); }
    }

    /// <summary>Build CSS that hides elements matching the host.</summary>
    public string BuildCosmeticStyleSheet(string pageHost)
    {
        var selectors = CosmeticSelectors(pageHost);
        if (selectors.Count == 0) return string.Empty;
        var sb = new StringBuilder(selectors.Count * 32);
        foreach (string s in selectors)
            sb.Append(s).Append("{display:none!important;}\n");
        return sb.ToString();
    }

    /// <summary>Check whether a request should be blocked.</summary>
    public bool ShouldBlock(string requestUrl, string pageUrl, ResourceType type)
    {
        if (!HostUtil.TryGetHttpUri(requestUrl, out var reqUri)) return false;

        string? pageHost = HostUtil.TryGetHttpUri(pageUrl, out var pageUri) ? pageUri.Host : null;

        _gate.EnterReadLock();
        try
        {
            if (pageHost is not null && IsWhitelistedHost(pageHost)) return false;

            var ctx = BuildContext(reqUri, pageHost, type);

            // $important: important blocks override plain exceptions (@@). An important exception
            // (also marked $important) can cancel them.
            if (_importantBlocks > 0 && _block.Matches(in ctx, importantOnly: true) &&
                !ExceptionMatches(in ctx, pageUri, pageHost, importantOnly: true))
            {
                return true;
            }

            if (ExceptionMatches(in ctx, pageUri, pageHost, importantOnly: false)) return false;

            if (IsBlockedHost(reqUri.Host)) return true;

            return _block.Matches(in ctx);
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    private bool ExceptionMatches(in RequestContext ctx, Uri pageUri, string? pageHost, bool importantOnly)
    {
        if (_exceptions.Matches(in ctx, importantOnly)) return true;
        if (_documentExceptions > 0 && pageHost is not null)
        {
            var pageCtx = BuildContext(pageUri, pageHost, ResourceType.Document);
            if (_exceptions.Matches(in pageCtx, importantOnly)) return true;
        }
        return false;
    }

    /// <summary>Cosmetic selectors for the page host (including generic rules and exceptions).</summary>
    public IReadOnlyList<string> CosmeticSelectors(string pageHost)
    {
        if (string.IsNullOrWhiteSpace(pageHost)) return Array.Empty<string>();
        string host = HostUtil.NormalizeHost(pageHost);
        _gate.EnterReadLock();
        try { return _cosmetic.SelectorsFor(host); }
        finally { _gate.ExitReadLock(); }
    }

    // ---------------------------------------------------------------------
    // Internals

    private static RequestContext BuildContext(Uri uri, string? pageHost, ResourceType type)
    {
        string raw = uri.AbsoluteUri;
        string lower = raw.ToLowerInvariant();
        int hostStart = raw.IndexOf("://", StringComparison.Ordinal);
        hostStart = hostStart < 0 ? 0 : hostStart + 3;
        int hostEnd = raw.IndexOfAny(new[] { '/', '?', '#' }, hostStart);
        if (hostEnd < 0) hostEnd = raw.Length;

        string host = uri.Host;
        bool third = pageHost is null || !HostUtil.SameSite(host, pageHost);
        return new RequestContext(raw, lower, host, hostStart, hostEnd, type, pageHost, third);
    }

    private bool IsWhitelistedHost(string host)
    {
        if (_pageWhitelist.Count == 0) return false;
        return HostUtil.MatchesAnyDomain(host, _pageWhitelist);
    }

    private bool IsBlockedHost(string host)
    {
        if (_hosts.Count == 0) return false;
        return HostUtil.MatchesAnyDomain(host, _hosts);
    }

    private void AddHostInternal(string raw)
    {
        string h = raw.Trim().TrimEnd('.').ToLowerInvariant();
        if (h.Length == 0 || IgnoredHostNames.Contains(h)) return;
        if (h.StartsWith("ip6-", StringComparison.Ordinal)) return;
        if (System.Net.IPAddress.TryParse(h, out _)) return;
        if (h.StartsWith("www.", StringComparison.Ordinal)) h = h[4..];
        string normalized = HostUtil.NormalizeHost(h);
        if (normalized.Length == 0) return;
        if (_hosts.Add(normalized)) _hostRules++;
    }

    private void ProcessFilterLine(string raw)
    {
        var outcome = FilterParser.Parse(raw);
        if (outcome.HostsEntry is not null)
        {
            AddHostInternal(outcome.HostsEntry);
            return;
        }
        if (outcome.Unsupported)
        {
            _unsupported++;
            return;
        }
        if (outcome.Cosmetic is not null)
        {
            _cosmetic.Add(outcome.Cosmetic);
            return;
        }
        if (outcome.BadFilterTarget is not null)
        {
            _badFilters.Add(outcome.BadFilterTarget);
            DisableByText(outcome.BadFilterTarget);
            return;
        }
        if (outcome.Rule is null) return;

        var rule = outcome.Rule;
        if (_badFilters.Contains(rule.RawText))
        {
            _badFiltered++;
            return;
        }

        if (rule.IsException)
        {
            _exceptions.Add(rule, outcome.DomainKey, outcome.Token);
            if ((rule.TypeMask & ResourceMask.Bit(ResourceType.Document)) != 0) _documentExceptions++;
        }
        else
        {
            _block.Add(rule, outcome.DomainKey, outcome.Token);
            if (rule.Important) _importantBlocks++;
        }

        if (!_rulesByText.TryGetValue(rule.RawText, out var list))
        {
            list = new List<FilterRule>(1);
            _rulesByText[rule.RawText] = list;
        }
        list.Add(rule);
    }

    private void DisableByText(string text)
    {
        _badFilters.Add(text);
        if (!_rulesByText.TryGetValue(text, out var list)) return;
        foreach (var rule in list)
        {
            if (rule.Disabled) continue;
            rule.Disabled = true;
            _badFiltered++;
        }
    }
}
