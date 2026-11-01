using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinIDM.Engine;

/// <summary>Importable link list formats.</summary>
public enum ImportFormat
{
    /// <summary>Detected from the content: JSON (starts with [ or {), Netscape HTML (has &lt;a href&gt;), otherwise plain text.</summary>
    Auto,

    /// <summary>One http/https URL per line. Empty lines and lines starting with # are ignored.</summary>
    PlainText,

    /// <summary>JSON array: URL strings, or objects with a <c>url</c> field and an optional <c>fileName</c>.</summary>
    Json,

    /// <summary>CSV with a header. URL column: <c>url</c>, <c>link</c>, <c>address</c>, or the legacy alias <c>alamat</c>. Optional file name column: <c>fileName</c>, <c>name</c>, or the legacy alias <c>nama</c>.</summary>
    Csv,

    /// <summary>Netscape HTML bookmark file (<c>&lt;A HREF="..."&gt;</c>), the usual browser export format.</summary>
    NetscapeHtml,

    /// <summary>
    /// 1DM export text format. Not supported: its line specification is missing from report 03, so it is not guessed.
    /// Requesting this format throws <see cref="NotSupportedException"/>.
    /// </summary>
    OneDmText,
}

/// <summary>One accepted link. <see cref="Line"/> is the line number (text, CSV, HTML) or element number (JSON), starting at 1.</summary>
public sealed record ImportedLink(string Url, string? FileName, int Line);

/// <summary>A rejected entry and its reason. <see cref="Line"/> as in <see cref="ImportedLink.Line"/>.</summary>
public sealed record ImportRejection(int Line, string Input, string Reason);

/// <summary>
/// Import result. <see cref="Truncated"/> means the link count exceeded <see cref="ImportExport.MaxLinks"/> and the rest were ignored.
/// Duplicate links (same URL) are kept only once; the second occurrence is recorded in <see cref="Rejected"/>.
/// </summary>
public sealed record ImportResult(
    ImportFormat Format,
    IReadOnlyList<ImportedLink> Links,
    IReadOnlyList<ImportRejection> Rejected,
    bool Truncated);

/// <summary>
/// Link list parser for import and the smart clipboard. Produces valid http/https URLs only.
/// Makes no network requests.
/// </summary>
public static class ImportExport
{
    /// <summary>Maximum number of links accepted in one import.</summary>
    public const int MaxLinks = 10_000;

    /// <summary>Maximum length of a single URL.</summary>
    public const int MaxUrlLength = 2048;

    private static readonly string[] UrlHeaders = ["url", "link", "address", "alamat", "pautan"];
    private static readonly string[] NameHeaders = ["filename", "file_name", "file name", "name", "nama", "nama fail"];

    private static readonly Regex AnchorHref = new(
        @"<a\s[^>]*?href\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex UrlInText = new(
        @"https?://[^\s<>""'`]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Imports a link list from text.</summary>
    /// <exception cref="FormatException">The JSON content is invalid or is not an array.</exception>
    /// <exception cref="NotSupportedException">The <see cref="ImportFormat.OneDmText"/> format was requested (not supported yet).</exception>
    public static ImportResult Parse(string text, ImportFormat format = ImportFormat.Auto)
    {
        ArgumentNullException.ThrowIfNull(text);

        var resolved = format == ImportFormat.Auto ? Detect(text) : format;
        return resolved switch
        {
            ImportFormat.PlainText => ParsePlainText(text),
            ImportFormat.Json => ParseJson(text),
            ImportFormat.Csv => ParseCsv(text),
            ImportFormat.NetscapeHtml => ParseNetscape(text),
            ImportFormat.OneDmText => throw new NotSupportedException(
                "The 1DM text export format is not supported: its line specification is missing from report 03. " +
                "Use a text list (one URL per line), JSON, CSV or Netscape HTML."),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown import format."),
        };
    }

    /// <summary>
    /// Finds http/https URLs in free text (smart clipboard). Trailing punctuation (periods, commas, closing brackets)
    /// is stripped. The result has no duplicates, keeps the order of appearance, and is limited to <see cref="MaxLinks"/>.
    /// </summary>
    public static IReadOnlyList<string> ExtractUrls(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in UrlInText.Matches(text))
        {
            var candidate = match.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}');
            if (!TryNormalizeUrl(candidate, out var url, out _) || !seen.Add(url)) continue;

            result.Add(url);
            if (result.Count >= MaxLinks) break;
        }

        return result;
    }

    /// <summary>Detects the import format from the content. CSV without a header is not detected automatically.</summary>
    internal static ImportFormat Detect(string text)
    {
        var trimmed = text.TrimStart('﻿', ' ', '\t', '\r', '\n');
        if (trimmed.StartsWith('[') || trimmed.StartsWith('{')) return ImportFormat.Json;
        if (Regex.IsMatch(trimmed, @"<a\s[^>]*href\s*=", RegexOptions.IgnoreCase)) return ImportFormat.NetscapeHtml;

        var firstLine = SplitLines(trimmed).FirstOrDefault(l => l.Trim().Length > 0) ?? string.Empty;
        var cells = CsvReader.SplitRow(firstLine);
        if (cells.Count > 1 && cells.Any(c => UrlHeaders.Contains(c.Trim().ToLowerInvariant()))) return ImportFormat.Csv;

        return ImportFormat.PlainText;
    }

    // ------------------------------------------------------------------
    // Formats
    // ------------------------------------------------------------------

    private static ImportResult ParsePlainText(string text)
    {
        var collector = new Collector();
        var lineNumber = 0;
        foreach (var raw in SplitLines(text))
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            collector.Add(line, null, lineNumber);
        }

        return collector.Build(ImportFormat.PlainText);
    }

    private static ImportResult ParseJson(string text)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            throw new FormatException("Invalid link JSON file: " + ex.Message, ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("The link JSON file must be an array.");
            }

            var collector = new Collector();
            var index = 0;
            foreach (var element in document.RootElement.EnumerateArray())
            {
                index++;
                switch (element.ValueKind)
                {
                    case JsonValueKind.String:
                        collector.Add(element.GetString() ?? string.Empty, null, index);
                        break;

                    case JsonValueKind.Object:
                        var url = StringProperty(element, "url");
                        if (url is null)
                        {
                            collector.Reject(index, "object", "Object has no text 'url' field.");
                            break;
                        }

                        var name = StringProperty(element, "fileName") ?? StringProperty(element, "filename")
                                   ?? StringProperty(element, "file_name") ?? StringProperty(element, "name");
                        collector.Add(url, name, index);
                        break;

                    default:
                        collector.Reject(index, element.ValueKind.ToString(), "Element must be a URL string or an object.");
                        break;
                }
            }

            return collector.Build(ImportFormat.Json);
        }
    }

    private static ImportResult ParseCsv(string text)
    {
        var rows = CsvReader.ParseRecords(text);
        var collector = new Collector();
        if (rows.Count == 0) return collector.Build(ImportFormat.Csv);

        var header = rows[0].Cells.Select(c => c.Trim().ToLowerInvariant()).ToList();
        var urlColumn = header.FindIndex(h => UrlHeaders.Contains(h));
        var nameColumn = header.FindIndex(h => NameHeaders.Contains(h));
        var start = 0;
        if (urlColumn >= 0)
        {
            start = 1;
        }
        else
        {
            urlColumn = 0;
        }

        for (var i = start; i < rows.Count; i++)
        {
            var (line, cells) = rows[i];
            if (cells.All(c => c.Trim().Length == 0)) continue;

            var url = urlColumn < cells.Count ? cells[urlColumn] : string.Empty;
            var name = nameColumn >= 0 && nameColumn < cells.Count ? cells[nameColumn] : null;
            collector.Add(url, string.IsNullOrWhiteSpace(name) ? null : name.Trim(), line);
        }

        return collector.Build(ImportFormat.Csv);
    }

    private static ImportResult ParseNetscape(string text)
    {
        var collector = new Collector();
        foreach (Match match in AnchorHref.Matches(text))
        {
            var value = match.Groups[1].Success ? match.Groups[1].Value
                : match.Groups[2].Success ? match.Groups[2].Value
                : match.Groups[3].Value;

            var line = CountNewlines(text, match.Index) + 1;
            collector.Add(WebUtility.HtmlDecode(value), null, line);
        }

        return collector.Build(ImportFormat.NetscapeHtml);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>Normalizes a URL: http/https only, no spaces or control characters, length within <see cref="MaxUrlLength"/>.</summary>
    internal static bool TryNormalizeUrl(string candidate, out string url, out string? reason)
    {
        url = string.Empty;
        var value = candidate.Trim();
        if (value.Length == 0)
        {
            reason = "Empty.";
            return false;
        }

        if (value.Length > MaxUrlLength)
        {
            reason = $"URL exceeds {MaxUrlLength} characters.";
            return false;
        }

        if (value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            reason = "URL contains spaces or control characters.";
            return false;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host))
        {
            reason = "Not a valid http or https URL.";
            return false;
        }

        url = uri.AbsoluteUri;
        reason = null;
        return true;
    }

    private static string? StringProperty(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString();
            }
        }

        return null;
    }

    private static IEnumerable<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    private static int CountNewlines(string text, int end)
    {
        var count = 0;
        for (var i = 0; i < end; i++)
        {
            if (text[i] == '\n') count++;
        }

        return count;
    }

    /// <summary>Collects links and rejections, handling duplicates and limits.</summary>
    private sealed class Collector
    {
        private readonly List<ImportedLink> _links = new();
        private readonly List<ImportRejection> _rejected = new();
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private bool _truncated;

        public void Add(string candidate, string? fileName, int line)
        {
            if (!TryNormalizeUrl(candidate, out var url, out var reason))
            {
                Reject(line, candidate, reason!);
                return;
            }

            if (!_seen.Add(url))
            {
                Reject(line, candidate, "Duplicate (ignored).");
                return;
            }

            if (_links.Count >= MaxLinks)
            {
                _truncated = true;
                return;
            }

            _links.Add(new ImportedLink(url, fileName, line));
        }

        public void Reject(int line, string input, string reason) =>
            _rejected.Add(new ImportRejection(line, input, reason));

        public ImportResult Build(ImportFormat format) =>
            new(format, _links, _rejected, _truncated);
    }

    /// <summary>Simple CSV parser following RFC 4180 (quoted fields, "" as an escaped quote, newlines inside quotes).</summary>
    private static class CsvReader
    {
        /// <summary>Records with their starting line number (1-based).</summary>
        public static List<(int Line, List<string> Cells)> ParseRecords(string text)
        {
            var records = new List<(int Line, List<string> Cells)>();
            var cells = new List<string>();
            var field = new StringBuilder();
            var inQuotes = false;
            var line = 1;
            var recordLine = 1;

            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            field.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        if (c == '\n') line++;
                        field.Append(c);
                    }

                    continue;
                }

                switch (c)
                {
                    case '"':
                        inQuotes = true;
                        break;
                    case ',':
                        cells.Add(field.ToString());
                        field.Clear();
                        break;
                    case '\r':
                        break;
                    case '\n':
                        cells.Add(field.ToString());
                        field.Clear();
                        records.Add((recordLine, cells));
                        cells = new List<string>();
                        line++;
                        recordLine = line;
                        break;
                    default:
                        field.Append(c);
                        break;
                }
            }

            if (field.Length > 0 || cells.Count > 0)
            {
                cells.Add(field.ToString());
                records.Add((recordLine, cells));
            }

            return records;
        }

        /// <summary>Splits one CSV line (for header detection).</summary>
        public static List<string> SplitRow(string line) =>
            ParseRecords(line).FirstOrDefault().Cells ?? new List<string>();
    }
}
