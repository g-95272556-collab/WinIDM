using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace WinIDM.Browser;

/// <summary>
/// Converts @match patterns (Chrome style: scheme://host/path with '*') and @include/@exclude
/// ('*' glob or /.../ regex) into Regex. Results are cached by pattern text.
/// Invalid patterns yield null and are ignored.
/// </summary>
internal static class UrlPatternMatcher
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly ConcurrentDictionary<string, Regex?> Cache = new(StringComparer.Ordinal);

    public static bool MatchAny(IReadOnlyList<string> patterns, bool matchStyle, string url)
    {
        foreach (string p in patterns)
        {
            var re = Get(p, matchStyle);
            if (re is not null && re.IsMatch(url)) return true;
        }
        return false;
    }

    private static Regex? Get(string pattern, bool matchStyle)
    {
        string key = (matchStyle ? "m|" : "i|") + pattern;
        return Cache.GetOrAdd(key, _ => matchStyle ? CompileMatch(pattern) : CompileInclude(pattern));
    }

    /// <summary>Chrome-style @match pattern. Only http and https are matched.</summary>
    internal static Regex? CompileMatch(string pattern)
    {
        if (pattern == "<all_urls>") return Make("^https?://.*$");

        int idx = pattern.IndexOf("://", StringComparison.Ordinal);
        if (idx < 0) return null;
        string scheme = pattern[..idx];
        string rest = pattern[(idx + 3)..];
        int slash = rest.IndexOf('/');
        string hostPart = slash < 0 ? rest : rest[..slash];
        string path = slash < 0 ? "/" : rest[slash..];

        string schemeRe = scheme switch
        {
            "*" => "https?",
            "http" => "http",
            "https" => "https",
            _ => string.Empty,
        };
        if (schemeRe.Length == 0) return null;

        string hostRe;
        if (hostPart == "*")
        {
            hostRe = "[^/]*";
        }
        else if (hostPart.StartsWith("*.", StringComparison.Ordinal))
        {
            hostRe = "(?:[^/]*\\.)?" + Glob(hostPart[2..]);
        }
        else
        {
            hostRe = Glob(hostPart);
        }

        return Make("^" + schemeRe + "://" + hostRe + Glob(path) + "$");
    }

    /// <summary>@include/@exclude: '*' glob for the whole URL, or a regex when enclosed in /.../.</summary>
    internal static Regex? CompileInclude(string pattern)
    {
        if (pattern.Length >= 2 && pattern[0] == '/' && pattern[^1] == '/')
        {
            try
            {
                return new Regex(pattern[1..^1], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
        return Make("^" + Glob(pattern) + "$");
    }

    private static string Glob(string text) =>
        string.Join(".*", text.Split('*').Select(Regex.Escape));

    private static Regex Make(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
}
