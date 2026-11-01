using System.Globalization;
using System.Text;

namespace WinIDM.Engine;

/// <summary>
/// URL patterns for batch download (IDM+ "Batch download"). Each <c>[from-to]</c> or <c>[from-to:step]</c> group is
/// replaced by every value in the range; several groups give every combination, left to right.
/// <list type="bullet">
/// <item><c>[1-10]</c> 1, 2, ... 10</item>
/// <item><c>[01-10]</c> 01, 02, ... 10 (a leading zero keeps the width)</item>
/// <item><c>[a-e]</c>, <c>[A-E]</c> letters</item>
/// <item><c>[0-100:25]</c> 0, 25, 50, 75, 100; a range may also count down (<c>[5-1]</c>)</item>
/// </list>
/// Text outside the brackets is kept as is. A pattern without groups expands to itself.
/// </summary>
public static class BatchPattern
{
    /// <summary>Upper bound on generated links, so a typo cannot add millions of downloads.</summary>
    public const int MaxLinks = 10_000;

    /// <exception cref="FormatException">A group is malformed or the pattern yields more than <paramref name="max"/> links.</exception>
    public static IReadOnlyList<string> Expand(string pattern, int max = MaxLinks)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var parts = Parse(pattern.Trim());

        long total = 1;
        foreach (var part in parts)
        {
            total *= part.Count;
            if (total > max) throw new FormatException($"The pattern produces more than {max} links.");
        }

        var results = new List<string>((int)total);
        Combine(parts, 0, new StringBuilder(), results);
        return results;
    }

    private static void Combine(List<IReadOnlyList<string>> parts, int index, StringBuilder prefix, List<string> results)
    {
        if (index == parts.Count)
        {
            results.Add(prefix.ToString());
            return;
        }

        var length = prefix.Length;
        foreach (var value in parts[index])
        {
            prefix.Append(value);
            Combine(parts, index + 1, prefix, results);
            prefix.Length = length;
        }
    }

    private static List<IReadOnlyList<string>> Parse(string pattern)
    {
        var parts = new List<IReadOnlyList<string>>();
        var literal = new StringBuilder();
        var i = 0;
        while (i < pattern.Length)
        {
            var open = pattern.IndexOf('[', i);
            var close = open < 0 ? -1 : pattern.IndexOf(']', open);
            if (open < 0 || close < 0)
            {
                literal.Append(pattern, i, pattern.Length - i);
                break;
            }

            var range = ParseGroup(pattern[(open + 1)..close]);
            if (range is null)
            {
                // Not a range (for example an IPv6 host "[::1]"): keep the brackets as literal text.
                literal.Append(pattern, i, close + 1 - i);
                i = close + 1;
                continue;
            }

            literal.Append(pattern, i, open - i);
            if (literal.Length > 0)
            {
                parts.Add([literal.ToString()]);
                literal.Clear();
            }

            parts.Add(range);
            i = close + 1;
        }

        if (literal.Length > 0) parts.Add([literal.ToString()]);
        if (parts.Count == 0) parts.Add([string.Empty]);
        return parts;
    }

    /// <summary>Values of one group, or null if the text is not a range group.</summary>
    private static IReadOnlyList<string>? ParseGroup(string body)
    {
        var step = 1;
        var colon = body.IndexOf(':');
        if (colon >= 0)
        {
            if (!int.TryParse(body[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out step) || step < 1)
                return null;
            body = body[..colon];
        }

        var dash = body.Length > 1 ? body.IndexOf('-', 1) : -1;
        if (dash <= 0 || dash == body.Length - 1) return null;
        var from = body[..dash];
        var to = body[(dash + 1)..];

        if (IsDigits(from) && IsDigits(to))
        {
            if (from.Length > 9 || to.Length > 9) throw new FormatException($"The range [{body}] is too large.");
            var width = from.Length > 1 && from[0] == '0' ? from.Length : 0;
            return Range(int.Parse(from, CultureInfo.InvariantCulture), int.Parse(to, CultureInfo.InvariantCulture), step,
                n => width > 0 ? n.ToString(new string('0', width), CultureInfo.InvariantCulture) : n.ToString(CultureInfo.InvariantCulture));
        }

        if (from.Length == 1 && to.Length == 1 && char.IsAsciiLetter(from[0]) && char.IsAsciiLetter(to[0]) &&
            char.IsUpper(from[0]) == char.IsUpper(to[0]))
        {
            return Range(from[0], to[0], step, n => ((char)n).ToString());
        }

        return null;
    }

    private static List<string> Range(int from, int to, int step, Func<int, string> format)
    {
        var values = new List<string>();
        var direction = to >= from ? 1 : -1;
        for (long n = from; direction > 0 ? n <= to : n >= to; n += direction * step)
        {
            values.Add(format((int)n));
            if (values.Count > MaxLinks) throw new FormatException($"The pattern produces more than {MaxLinks} links.");
        }

        return values;
    }

    private static bool IsDigits(string s) => s.Length > 0 && s.All(char.IsAsciiDigit);
}
