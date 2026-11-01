namespace WinIDM.Browser;

/// <summary>
/// Three-level rule index: (1) domain for ||host^, (2) alphanumeric tokens,
/// (3) a generic list for rules without a reliable token.
/// Domain lookups use span views with no memory allocation.
/// </summary>
internal sealed class RuleIndex
{
    private readonly Dictionary<string, List<FilterRule>> _byDomain = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<FilterRule>> _byToken = new(StringComparer.Ordinal);
    private readonly List<FilterRule> _generic = new();

    public int Count { get; private set; }

    public void Add(FilterRule rule, string? domainKey, string? token)
    {
        Count++;
        if (domainKey is not null)
        {
            Bucket(_byDomain, domainKey).Add(rule);
        }
        else if (token is not null)
        {
            Bucket(_byToken, token).Add(rule);
        }
        else
        {
            _generic.Add(rule);
        }
    }

    private static List<FilterRule> Bucket(Dictionary<string, List<FilterRule>> map, string key)
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = new List<FilterRule>(2);
            map[key] = list;
        }
        return list;
    }

    /// <summary>Match rules. When <paramref name="importantOnly"/>, only $important rules are considered.</summary>
    public bool Matches(in RequestContext ctx, bool importantOnly = false)
    {
        string host = ctx.Host;
        if (_byDomain.Count > 0 && host.Length > 0)
        {
            var domainLookup = _byDomain.GetAlternateLookup<ReadOnlySpan<char>>();
            int start = 0;
            while (true)
            {
                if (domainLookup.TryGetValue(host.AsSpan(start), out var list) && AnyApplies(list, in ctx, importantOnly))
                    return true;
                int dot = host.IndexOf('.', start);
                if (dot < 0) break;
                start = dot + 1;
            }
        }

        if (_byToken.Count > 0)
        {
            var tokenLookup = _byToken.GetAlternateLookup<ReadOnlySpan<char>>();
            string lower = ctx.LowerUrl;
            int i = 0;
            while (i < lower.Length)
            {
                if (!char.IsLetterOrDigit(lower[i]))
                {
                    i++;
                    continue;
                }
                int j = i + 1;
                while (j < lower.Length && char.IsLetterOrDigit(lower[j])) j++;
                if (tokenLookup.TryGetValue(lower.AsSpan(i, j - i), out var tokenList) && AnyApplies(tokenList, in ctx, importantOnly))
                    return true;
                i = j;
            }
        }

        foreach (var rule in _generic)
        {
            if (importantOnly && !rule.Important) continue;
            if (rule.Applies(in ctx)) return true;
        }
        return false;
    }

    private static bool AnyApplies(List<FilterRule> list, in RequestContext ctx, bool importantOnly)
    {
        foreach (var rule in list)
        {
            if (importantOnly && !rule.Important) continue;
            if (rule.Applies(in ctx)) return true;
        }
        return false;
    }
}

/// <summary>Cosmetic index: generic selectors, domain-specific selectors, and #@# exceptions.</summary>
internal sealed class CosmeticIndex
{
    private readonly List<CosmeticRule> _generic = new();
    private readonly Dictionary<string, List<CosmeticRule>> _byDomain = new(StringComparer.Ordinal);
    private readonly HashSet<string> _genericUnhide = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _unhideByDomain = new(StringComparer.Ordinal);

    public int Count { get; private set; }

    public void Add(CosmeticRule rule)
    {
        Count++;
        if (rule.IsException)
        {
            if (rule.IncludeDomains.Length == 0)
            {
                _genericUnhide.Add(rule.Selector);
                return;
            }
            foreach (string d in rule.IncludeDomains)
            {
                if (!_unhideByDomain.TryGetValue(d, out var set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    _unhideByDomain[d] = set;
                }
                set.Add(rule.Selector);
            }
            return;
        }

        if (rule.IncludeDomains.Length == 0)
        {
            _generic.Add(rule);
            return;
        }
        foreach (string d in rule.IncludeDomains)
        {
            if (!_byDomain.TryGetValue(d, out var list))
            {
                list = new List<CosmeticRule>();
                _byDomain[d] = list;
            }
            list.Add(rule);
        }
    }

    public IReadOnlyList<string> SelectorsFor(string host)
    {
        var excluded = new HashSet<string>(_genericUnhide, StringComparer.Ordinal);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Collect exceptions along the host chain.
        if (_unhideByDomain.Count > 0)
        {
            foreach (string suffix in Suffixes(host))
            {
                if (_unhideByDomain.TryGetValue(suffix, out var set)) excluded.UnionWith(set);
            }
        }

        foreach (string suffix in Suffixes(host))
        {
            if (!_byDomain.TryGetValue(suffix, out var list)) continue;
            foreach (var rule in list)
            {
                if (rule.ExcludeDomains.Length > 0 && HostUtil.MatchesAnyDomain(host, rule.ExcludeDomains)) continue;
                if (!excluded.Contains(rule.Selector) && seen.Add(rule.Selector)) result.Add(rule.Selector);
            }
        }

        foreach (var rule in _generic)
        {
            if (rule.ExcludeDomains.Length > 0 && HostUtil.MatchesAnyDomain(host, rule.ExcludeDomains)) continue;
            if (!excluded.Contains(rule.Selector) && seen.Add(rule.Selector)) result.Add(rule.Selector);
        }
        return result;
    }

    private static IEnumerable<string> Suffixes(string host)
    {
        if (host.Length == 0) yield break;
        int start = 0;
        while (true)
        {
            yield return host[start..];
            int dot = host.IndexOf('.', start);
            if (dot < 0) yield break;
            start = dot + 1;
        }
    }
}
