using System.Text.RegularExpressions;
using WinIDM.Core.Contracts;

namespace WinIDM.Browser;

/// <summary>Parse result for one filter line.</summary>
internal sealed class ParseOutcome
{
    public FilterRule? Rule { get; init; }
    public CosmeticRule? Cosmetic { get; init; }
    public string? DomainKey { get; init; }
    public string? Token { get; init; }

    /// <summary>Set when the line is a $badfilter directive; the value is the rule text being canceled.</summary>
    public string? BadFilterTarget { get; init; }
    public string? HostsEntry { get; init; }
    public bool Unsupported { get; init; }
}

/// <summary>
/// Basic ABP/uBO filter syntax parser. Implemented from the public syntax specification:
/// anchors (||, |, ^), exceptions (@@), $ options (third-party, resource types, domain=, match-case,
/// badfilter), and cosmetics (##, #@#). Unsupported options make the line 'Unsupported'.
/// </summary>
internal static class FilterParser
{
    private static readonly Regex HostsLine = new(@"^(?:0\.0\.0\.0|127\.0\.0\.1)\s+(\S+)", RegexOptions.Compiled);

    private static readonly string[] UnsupportedCosmeticMarkers =
    {
        "+js(", ":-abp-", ":has-text(", ":xpath(", ":matches-css", ":style(", ":remove(",
        ":upward(", ":others(", ":min-text-length(", ":matches-path(", ":matches-attr(", ":matches-prop(",
    };

    public static ParseOutcome Parse(string rawLine)
    {
        string line = rawLine.Trim();
        if (line.Length == 0 || line[0] == '!' || line[0] == '[')
            return new ParseOutcome();

        var hostsMatch = HostsLine.Match(line);
        if (hostsMatch.Success)
            return new ParseOutcome { HostsEntry = hostsMatch.Groups[1].Value };

        int sep = FirstCosmeticSeparator(line, out int sepLength, out bool extended);
        if (sep >= 0)
        {
            string prefix = line[..sep];
            if (prefix.IndexOfAny(new[] { '/', '|', '$', '@', '*', ' ' }) < 0)
            {
                if (extended) return new ParseOutcome { Unsupported = true };
                return ParseCosmetic(line, sep, sepLength);
            }
        }

        return ParseNetwork(line);
    }

    private static int FirstCosmeticSeparator(string line, out int length, out bool extended)
    {
        extended = false;
        length = 2;
        int best = -1;
        foreach (string marker in new[] { "##", "#@#", "#?#" })
        {
            int idx = line.IndexOf(marker, StringComparison.Ordinal);
            if (idx >= 0 && (best < 0 || idx < best))
            {
                best = idx;
                length = marker.Length;
                extended = marker == "#?#";
            }
        }
        return best;
    }

    private static ParseOutcome ParseCosmetic(string line, int sep, int sepLength)
    {
        bool isException = sepLength == 3;
        string selector = line[(sep + sepLength)..].Trim();
        if (selector.Length == 0 || !IsSafeSelector(selector))
            return new ParseOutcome { Unsupported = true };
        foreach (string marker in UnsupportedCosmeticMarkers)
        {
            if (selector.Contains(marker, StringComparison.Ordinal))
                return new ParseOutcome { Unsupported = true };
        }

        var include = new List<string>();
        var exclude = new List<string>();
        string domainPart = line[..sep];
        if (domainPart.Length > 0)
        {
            foreach (string raw in domainPart.Split(','))
            {
                string d = raw.Trim().ToLowerInvariant();
                if (d.Length == 0) continue;
                if (d[0] == '~') exclude.Add(HostUtil.NormalizeHost(d[1..]));
                else include.Add(HostUtil.NormalizeHost(d));
            }
        }

        return new ParseOutcome
        {
            Cosmetic = new CosmeticRule(selector, isException, include.ToArray(), exclude.ToArray()),
        };
    }

    /// <summary>A selector must not be able to inject a CSS block, at-rule, or markup.</summary>
    public static bool IsSafeSelector(string selector)
    {
        if (selector.StartsWith('@')) return false;
        foreach (char c in selector)
        {
            if (c == '{' || c == '}' || c == ';' || c == '<' || c == '\r' || c == '\n' || c == '\0')
                return false;
        }
        return true;
    }

    private static ParseOutcome ParseNetwork(string line)
    {
        bool isException = line.StartsWith("@@", StringComparison.Ordinal);
        string body = isException ? line[2..] : line;

        // As in ABP: the last '$' starts the options, except for /.../ regex rules, which
        // may contain '$' as a literal character.
        string? optionsText = null;
        int dollar = body.LastIndexOf('$');
        bool regexForm = body.Length >= 2 && body[0] == '/' && body[^1] == '/';
        if (dollar >= 0 && !regexForm)
        {
            optionsText = body[(dollar + 1)..];
            body = body[..dollar];
        }

        int typeMask = 0;
        int negTypes = 0;
        bool anyPositiveType = false;
        var party = PartyRestriction.Any;
        bool matchCase = false;
        bool badFilter = false;
        bool important = false;
        HashSet<string>? include = null;
        HashSet<string>? exclude = null;
        HashSet<string>? denyAllow = null;
        var kept = new List<string>();

        if (optionsText is not null)
        {
            foreach (string rawOpt in optionsText.Split(','))
            {
                string opt = rawOpt.Trim();
                if (opt.Length == 0) continue;
                string lower = opt.ToLowerInvariant();
                bool neg = lower[0] == '~';
                string name = neg ? lower[1..] : lower;

                switch (name)
                {
                    case "third-party":
                    case "3p":
                        party = neg ? PartyRestriction.FirstPartyOnly : PartyRestriction.ThirdPartyOnly;
                        kept.Add(opt);
                        continue;
                    case "first-party":
                    case "1p":
                        party = neg ? PartyRestriction.ThirdPartyOnly : PartyRestriction.FirstPartyOnly;
                        kept.Add(opt);
                        continue;
                    case "match-case":
                        if (!neg) matchCase = true;
                        kept.Add(opt);
                        continue;
                    case "important":
                        if (!neg) important = true;
                        kept.Add(opt);
                        continue;
                    case "badfilter":
                        badFilter = true;
                        continue;
                }

                if (name.StartsWith("denyallow=", StringComparison.Ordinal))
                {
                    if (neg) return new ParseOutcome { Unsupported = true };
                    denyAllow ??= new HashSet<string>(StringComparer.Ordinal);
                    foreach (string d in name["denyallow=".Length..].Split('|'))
                    {
                        if (d.Length == 0) continue;
                        // Exact hosts only; patterns and paths are not supported for denyallow.
                        if (d.IndexOfAny(new[] { '*', '/', '~', '^' }) >= 0) return new ParseOutcome { Unsupported = true };
                        denyAllow.Add(HostUtil.NormalizeHost(d));
                    }
                    kept.Add(opt);
                    continue;
                }

                if (name.StartsWith("domain=", StringComparison.Ordinal))
                {
                    if (name.Contains("/", StringComparison.Ordinal))
                        return new ParseOutcome { Unsupported = true };
                    include ??= new HashSet<string>(StringComparer.Ordinal);
                    exclude ??= new HashSet<string>(StringComparer.Ordinal);
                    foreach (string d in name["domain=".Length..].Split('|'))
                    {
                        if (d.Length == 0) continue;
                        if (d[0] == '~') exclude.Add(HostUtil.NormalizeHost(d[1..]));
                        else include.Add(HostUtil.NormalizeHost(d));
                    }
                    kept.Add(opt);
                    continue;
                }

                int bit = TypeBit(name);
                if (bit != 0)
                {
                    kept.Add(opt);
                    if (neg) negTypes |= bit;
                    else
                    {
                        anyPositiveType = true;
                        typeMask |= bit;
                    }
                    continue;
                }

                // Other options (redirect, csp, popup, ping, websocket, removeparam, etc.) are not supported.
                return new ParseOutcome { Unsupported = true };
            }
        }

        string canonical = (isException ? "@@" : "") + body +
                           (kept.Count > 0 ? "$" + string.Join(",", kept) : "");

        if (badFilter)
            return new ParseOutcome { BadFilterTarget = canonical };

        int mask = anyPositiveType ? typeMask : ResourceMask.DefaultBlockable;
        mask &= ~negTypes;
        if (mask == 0) return new ParseOutcome { Unsupported = true };

        // Regex rule: /.../
        if (body.Length >= 2 && body[0] == '/' && body[^1] == '/')
        {
            string source = body[1..^1];
            Regex regex;
            try
            {
                regex = new Regex(source,
                    (matchCase ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(50));
            }
            catch (ArgumentException)
            {
                return new ParseOutcome { Unsupported = true };
            }

            return new ParseOutcome
            {
                Rule = new FilterRule
                {
                    RawText = canonical,
                    IsException = isException,
                    Body = source,
                    MatchCase = matchCase,
                    StartAnchor = false,
                    DomainAnchor = false,
                    EndAnchor = false,
                    TypeMask = mask,
                    Party = party,
                    Regex = regex,
                    IncludeDomains = include?.Count > 0 ? include : null,
                    ExcludeDomains = exclude?.Count > 0 ? exclude : null,
                    DenyAllow = denyAllow?.Count > 0 ? denyAllow : null,
                    Important = important,
                },
            };
        }

        bool domainAnchor = false, startAnchor = false, endAnchor = false;
        string pat = body;
        if (pat.StartsWith("||", StringComparison.Ordinal)) { domainAnchor = true; pat = pat[2..]; }
        else if (pat.StartsWith('|')) { startAnchor = true; pat = pat[1..]; }
        if (pat.Length > 0 && pat[^1] == '|') { endAnchor = true; pat = pat[..^1]; }
        if (pat.Length == 0) return new ParseOutcome { Unsupported = true };

        if (!matchCase) pat = pat.ToLowerInvariant();

        string? domainKey = null;
        if (domainAnchor)
        {
            int hostLen = 0;
            while (hostLen < pat.Length && "^/:?*|$#".IndexOf(pat[hostLen]) < 0) hostLen++;
            if (hostLen > 0 && hostLen < pat.Length && "^/:?".IndexOf(pat[hostLen]) >= 0)
            {
                string hostPart = HostUtil.NormalizeHost(pat[..hostLen]);
                if (hostPart.Length > 0)
                {
                    domainKey = hostPart;
                    pat = hostPart + pat[hostLen..];
                }
            }
        }

        string? token = domainKey is null
            ? BestToken(pat.ToLowerInvariant(), startAnchor || domainAnchor, endAnchor)
            : null;

        var rule = new FilterRule
        {
            RawText = canonical,
            IsException = isException,
            Body = pat,
            MatchCase = matchCase,
            StartAnchor = startAnchor,
            DomainAnchor = domainAnchor,
            EndAnchor = endAnchor,
            TypeMask = mask,
            Party = party,
            IncludeDomains = include?.Count > 0 ? include : null,
            ExcludeDomains = exclude?.Count > 0 ? exclude : null,
            DenyAllow = denyAllow?.Count > 0 ? denyAllow : null,
            Important = important,
        };

        return new ParseOutcome { Rule = rule, DomainKey = domainKey, Token = token };
    }

    /// <summary>
    /// Pick the longest token bounded by known boundaries. Such a token always equals one
    /// maximal alphanumeric run in the URL, so it can serve as an index key.
    /// </summary>
    private static string? BestToken(string pat, bool leftAnchored, bool rightAnchored)
    {
        string? best = null;
        int i = 0;
        while (i < pat.Length)
        {
            if (!char.IsLetterOrDigit(pat[i])) { i++; continue; }
            int j = i;
            while (j < pat.Length && char.IsLetterOrDigit(pat[j])) j++;

            bool leftOk = i == 0 ? leftAnchored : pat[i - 1] != '*';
            bool rightOk = j == pat.Length ? rightAnchored : pat[j] != '*';
            if (leftOk && rightOk && j - i >= 2 && (best is null || j - i > best.Length))
                best = pat[i..j];
            i = j;
        }
        return best;
    }

    private static int TypeBit(string name) => name switch
    {
        "script" => ResourceMask.Bit(ResourceType.Script),
        "image" => ResourceMask.Bit(ResourceType.Image),
        "stylesheet" or "css" => ResourceMask.Bit(ResourceType.Stylesheet),
        "media" => ResourceMask.Bit(ResourceType.Media),
        "xmlhttprequest" or "xhr" => ResourceMask.Bit(ResourceType.XmlHttpRequest),
        "font" or "object" or "object-subrequest" or "other" => ResourceMask.Bit(ResourceType.Other),
        "document" or "doc" => ResourceMask.Bit(ResourceType.Document),
        "subdocument" or "frame" => ResourceMask.Bit(ResourceType.Subdocument),
        "all" => ResourceMask.All,
        _ => 0,
    };
}
