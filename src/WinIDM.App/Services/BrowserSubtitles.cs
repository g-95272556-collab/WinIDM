using System.Globalization;
using System.Text.RegularExpressions;

namespace WinIDM.App.Services;

/// <summary>One subtitle cue: start time, end time, and text.</summary>
public sealed record SubtitleCue(TimeSpan Start, TimeSpan End, string Text);

/// <summary>
/// SRT and WebVTT subtitle parser. Style tags are stripped. Invalid cues are skipped. Results are sorted by start time.
/// This parser does not run code or load external resources.
/// </summary>
public static class BrowserSubtitles
{
    public const int MaxBytes = 2 * 1024 * 1024;

    private static readonly Regex TimeLine = new(
        @"(?:(?<h1>\d+):)?(?<m1>\d{1,2}):(?<s1>\d{2})[.,](?<f1>\d{1,3})\s*-->\s*(?:(?<h2>\d+):)?(?<m2>\d{1,2}):(?<s2>\d{2})[.,](?<f2>\d{1,3})",
        RegexOptions.Compiled);

    private static readonly Regex Tags = new(@"<[^>]+>", RegexOptions.Compiled);

    public static IReadOnlyList<SubtitleCue> Parse(string text)
    {
        var cues = new List<SubtitleCue>();
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        foreach (var block in normalized.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            int timeIndex = Array.FindIndex(lines, l => l.Contains("-->", StringComparison.Ordinal));
            if (timeIndex < 0) continue;

            var m = TimeLine.Match(lines[timeIndex]);
            if (!m.Success) continue;
            var start = ToTime(m, "h1", "m1", "s1", "f1");
            var end = ToTime(m, "h2", "m2", "s2", "f2");
            if (end <= start) continue;

            var body = string.Join("\n", lines.Skip(timeIndex + 1).Select(l => Tags.Replace(l, "").Trim())).Trim();
            if (body.Length == 0) continue;
            cues.Add(new SubtitleCue(start, end, body));
        }
        return cues.OrderBy(c => c.Start).ToList();
    }

    /// <summary>Find the cue active at the given position, or null.</summary>
    public static string? ActiveText(IReadOnlyList<SubtitleCue> cues, TimeSpan position)
    {
        foreach (var cue in cues)
        {
            if (cue.Start > position) break;
            if (position < cue.End) return cue.Text;
        }
        return null;
    }

    private static TimeSpan ToTime(Match m, string h, string min, string s, string f)
    {
        int hours = m.Groups[h].Success ? int.Parse(m.Groups[h].Value, CultureInfo.InvariantCulture) : 0;
        int minutes = int.Parse(m.Groups[min].Value, CultureInfo.InvariantCulture);
        int seconds = int.Parse(m.Groups[s].Value, CultureInfo.InvariantCulture);
        // Fraction is padded to milliseconds (for example: "5" -> 500 ms).
        int millis = int.Parse(m.Groups[f].Value.PadRight(3, '0')[..3], CultureInfo.InvariantCulture);
        return new TimeSpan(0, hours, minutes, seconds, millis);
    }
}
