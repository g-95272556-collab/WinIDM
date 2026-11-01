using System.Text.RegularExpressions;
using WinIDM.Core.Contracts;

namespace WinIDM.Browser;

internal enum PartyRestriction : byte
{
    Any,
    ThirdPartyOnly,
    FirstPartyOnly,
}

/// <summary>Resource type bits for the $script, $image, and other options.</summary>
internal static class ResourceMask
{
    public static int Bit(ResourceType type) => 1 << (int)type;

    /// <summary>All types. Document (navigation) is excluded by default, same as ABP.</summary>
    public const int All = (1 << ((int)ResourceType.Subdocument + 1)) - 1;

    /// <summary>Default types: everything except top-level navigation (Document). Subdocument is included.</summary>
    public static readonly int DefaultBlockable = All & ~Bit(ResourceType.Document);
}

/// <summary>Context for one request, prepared for matching.</summary>
internal readonly struct RequestContext
{
    public RequestContext(string rawUrl, string lowerUrl, string host, int hostStart, int hostEnd,
        ResourceType type, string? pageHost, bool thirdParty)
    {
        RawUrl = rawUrl;
        LowerUrl = lowerUrl;
        Host = host;
        HostStart = hostStart;
        HostEnd = hostEnd;
        TypeBit = ResourceMask.Bit(type);
        PageHost = pageHost;
        ThirdParty = thirdParty;
    }

    public string RawUrl { get; }
    public string LowerUrl { get; }
    public string Host { get; }
    public int HostStart { get; }
    public int HostEnd { get; }
    public int TypeBit { get; }
    public string? PageHost { get; }
    public bool ThirdParty { get; }
}

/// <summary>One parsed ABP/uBO network rule.</summary>
internal sealed class FilterRule
{
    public required string RawText { get; init; }
    public required bool IsException { get; init; }

    /// <summary>Pattern without anchor markers. Lowercase unless MatchCase.</summary>
    public required string Body { get; init; }
    public required bool MatchCase { get; init; }
    public required bool StartAnchor { get; init; }
    public required bool DomainAnchor { get; init; }
    public required bool EndAnchor { get; init; }
    public required int TypeMask { get; init; }
    public required PartyRestriction Party { get; init; }
    public Regex? Regex { get; init; }
    public HashSet<string>? IncludeDomains { get; init; }
    public HashSet<string>? ExcludeDomains { get; init; }

    /// <summary>$denyallow option: the rule does not match when the request host is in this set.</summary>
    public HashSet<string>? DenyAllow { get; init; }

    /// <summary>$important option: this block rule overrides non-important @@ exceptions.</summary>
    public bool Important { get; init; }

    public bool Disabled { get; set; }

    /// <summary>Literal prefix (before * or ^) for fast IndexOf lookups.</summary>
    public string Literal
    {
        get
        {
            if (_literal is not null) return _literal;
            int end = 0;
            while (end < Body.Length && Body[end] != '*' && Body[end] != '^') end++;
            _literal = Body[..end];
            return _literal;
        }
    }

    private string? _literal;

    public bool DomainOptionAllows(string? pageHost)
    {
        if (IncludeDomains is { Count: > 0 })
        {
            if (pageHost is null || !HostUtil.MatchesAnyDomain(pageHost, IncludeDomains)) return false;
        }
        if (ExcludeDomains is { Count: > 0 } && pageHost is not null &&
            HostUtil.MatchesAnyDomain(pageHost, ExcludeDomains))
        {
            return false;
        }
        return true;
    }

    public bool Applies(in RequestContext ctx)
    {
        if (Disabled) return false;
        if ((TypeMask & ctx.TypeBit) == 0) return false;
        switch (Party)
        {
            case PartyRestriction.ThirdPartyOnly when !ctx.ThirdParty:
            case PartyRestriction.FirstPartyOnly when ctx.ThirdParty:
                return false;
        }
        if ((IncludeDomains is not null || ExcludeDomains is not null) && !DomainOptionAllows(ctx.PageHost))
            return false;
        if (DenyAllow is { Count: > 0 } && HostUtil.MatchesAnyDomain(ctx.Host, DenyAllow))
            return false;
        return MatchesUrl(in ctx);
    }

    public bool MatchesUrl(in RequestContext ctx)
    {
        if (Regex is not null)
        {
            try { return Regex.IsMatch(ctx.RawUrl); }
            catch (RegexMatchTimeoutException) { return false; }
        }

        string subject = MatchCase ? ctx.RawUrl : ctx.LowerUrl;

        if (StartAnchor)
            return GlobMatcher.MatchAt(subject, 0, Body, EndAnchor);

        if (DomainAnchor)
        {
            // Valid start positions: the start of the host, and after each dot inside the host.
            if (GlobMatcher.MatchAt(subject, ctx.HostStart, Body, EndAnchor)) return true;
            for (int i = ctx.HostStart; i < ctx.HostEnd && i < subject.Length - 1; i++)
            {
                if (subject[i] == '.' && GlobMatcher.MatchAt(subject, i + 1, Body, EndAnchor))
                    return true;
            }
            return false;
        }

        string lit = Literal;
        int from = 0;
        while (from <= subject.Length)
        {
            int idx = lit.Length == 0 ? from : subject.IndexOf(lit, from, StringComparison.Ordinal);
            if (idx < 0) return false;
            if (GlobMatcher.MatchAt(subject, idx, Body, EndAnchor)) return true;
            from = idx + 1;
        }
        return false;
    }
}

/// <summary>Glob matching with '*' (any sequence) and '^' (one separator or the end of the string).</summary>
internal static class GlobMatcher
{
    /// <summary>Try to match <paramref name="pat"/> starting exactly at <paramref name="start"/>.</summary>
    public static bool MatchAt(string url, int start, string pat, bool endAnchored)
    {
        int u = start, p = 0;
        int starP = -1, starU = -1;
        int n = url.Length, m = pat.Length;

        while (true)
        {
            if (p == m)
            {
                if (!endAnchored || u == n) return true;
            }
            else if (pat[p] == '*')
            {
                starP = p++;
                starU = u;
                continue;
            }
            else if (u < n && CharMatches(pat[p], url[u]))
            {
                p++;
                u++;
                continue;
            }
            else if (pat[p] == '^' && u == n)
            {
                // '^' matches the end of the string.
                p++;
                continue;
            }

            // No match: retry from the last '*' with one more character.
            if (starP < 0) return false;
            starU++;
            if (starU > n) return false;
            p = starP + 1;
            u = starU;
        }
    }

    private static bool CharMatches(char pc, char uc)
    {
        if (pc == '^') return IsSeparator(uc);
        return pc == uc;
    }

    public static bool IsSeparator(char c) =>
        !(char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' || c == '%');
}

/// <summary>Cosmetic rule: a CSS selector with include and exclude domain lists.</summary>
internal sealed record CosmeticRule(string Selector, bool IsException, string[] IncludeDomains, string[] ExcludeDomains);
