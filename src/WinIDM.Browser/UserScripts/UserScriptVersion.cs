using System.Text.RegularExpressions;

namespace WinIDM.Browser;

/// <summary>
/// @version-style script version comparison. It splits the string into segments (digits or letters).
/// Digit segments are compared numerically, letter segments are compared lexicographically, and
/// letter segments sort before digit segments (for example: 1.0.0-beta &lt; 1.0.0). Missing segments
/// are treated as 0.
/// </summary>
public static class UserScriptVersion
{
    private static readonly Regex Segment = new(@"\d+|[A-Za-z]+", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Compare two versions. A positive result means <paramref name="a"/> is newer.</summary>
    public static int Compare(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var sa = Split(a);
        var sb = Split(b);
        int n = Math.Max(sa.Count, sb.Count);
        for (int i = 0; i < n; i++)
        {
            string x = i < sa.Count ? sa[i] : "0";
            string y = i < sb.Count ? sb[i] : "0";
            int c = CompareSegment(x, y);
            if (c != 0) return c;
        }
        return 0;
    }

    private static List<string> Split(string v)
    {
        var list = new List<string>();
        foreach (Match m in Segment.Matches(v.Trim()))
        {
            list.Add(m.Value);
        }
        if (list.Count == 0) list.Add("0");
        return list;
    }

    private static int CompareSegment(string x, string y)
    {
        bool xd = char.IsDigit(x[0]);
        bool yd = char.IsDigit(y[0]);
        if (xd && yd)
        {
            // Compare numerically with no length limit: strip leading zeros, then compare length and lexicographically.
            string xs = x.TrimStart('0'), ys = y.TrimStart('0');
            if (xs.Length != ys.Length) return xs.Length.CompareTo(ys.Length);
            return string.CompareOrdinal(xs, ys) switch { < 0 => -1, > 0 => 1, _ => 0 };
        }
        if (!xd && !yd)
        {
            return string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
        }
        // Letter < digit.
        return xd ? 1 : -1;
    }
}
