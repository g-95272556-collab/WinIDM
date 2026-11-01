using System.Text;
using System.Text.Json;
using WinIDM.Core.Models;

namespace WinIDM.Backup;

/// <summary>Link list import result: valid requests and the error for each rejected row.</summary>
public sealed record LinkImportResult(IReadOnlyList<DownloadRequest> Requests, IReadOnlyList<string> Errors);

/// <summary>
/// Export/import download lists as JSON or CSV. Credentials (passwords, cookies) are never exported.
/// Import only accepts absolute http/https URLs. CSV values starting with = + - @ are prefixed with "'" to
/// prevent formula injection in Excel and are stripped again during import.
/// </summary>
public static class LinkExporter
{
    private static readonly char[] FormulaTriggers = { '=', '+', '-', '@', '\t', '\r' };
    private static readonly string[] CsvHeaders = { "url", "fileName", "saveDirectory", "category", "state", "referer" };

    private static readonly JsonSerializerOptions ExportOptions = new(BackupFormat.JsonOptions)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string ExportJson(IEnumerable<DownloadItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var rows = items.Select(ToRow).ToList();
        return JsonSerializer.Serialize(rows, ExportOptions);
    }

    public static string ExportCsv(IEnumerable<DownloadItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", CsvHeaders));
        foreach (var row in items.Select(ToRow))
        {
            var fields = new[]
            {
                row.Url,
                row.FileName,
                row.SaveDirectory,
                row.Category,
                row.State,
                row.Referer,
            };
            sb.AppendLine(string.Join(",", fields.Select(f => CsvEscape(Neutralize(f)))));
        }

        return sb.ToString();
    }

    public static LinkImportResult ImportJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var requests = new List<DownloadRequest>();
        var errors = new List<string>();

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new BackupFormatException("Invalid JSON link list.", ex);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new BackupFormatException("The JSON link list must be an array.");
            }

            var index = 0;
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                index++;
                string? url;
                string? fileName = null;
                string? saveDirectory = null;
                string? referer = null;

                if (element.ValueKind == JsonValueKind.String)
                {
                    url = element.GetString();
                }
                else if (element.ValueKind == JsonValueKind.Object)
                {
                    url = ReadString(element, "url");
                    fileName = ReadString(element, "fileName");
                    saveDirectory = ReadString(element, "saveDirectory");
                    referer = ReadString(element, "referer");
                }
                else
                {
                    errors.Add($"Item {index}: unsupported type.");
                    continue;
                }

                AddRequest(requests, errors, $"Item {index}", url, fileName, saveDirectory, referer);
            }
        }

        return new LinkImportResult(requests, errors);
    }

    public static LinkImportResult ImportCsv(string csv)
    {
        ArgumentNullException.ThrowIfNull(csv);
        var rows = ParseCsv(csv);
        var requests = new List<DownloadRequest>();
        var errors = new List<string>();

        if (rows.Count == 0)
        {
            return new LinkImportResult(requests, errors);
        }

        var header = rows[0].Select(h => h.Trim()).ToList();
        var urlColumn = header.FindIndex(h => string.Equals(h, "url", StringComparison.OrdinalIgnoreCase));
        if (urlColumn < 0)
        {
            throw new BackupFormatException("The first CSV row must contain a 'url' column.");
        }

        int ColumnOf(string name) => header.FindIndex(h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase));
        var fileNameColumn = ColumnOf("fileName");
        var saveColumn = ColumnOf("saveDirectory");
        var refererColumn = ColumnOf("referer");

        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Count == 1 && string.IsNullOrWhiteSpace(row[0]))
            {
                continue;
            }

            string? Cell(int column) => column >= 0 && column < row.Count ? Unneutralize(row[column]) : null;
            AddRequest(requests, errors, $"Row {i + 1}", Cell(urlColumn), Cell(fileNameColumn), Cell(saveColumn), Cell(refererColumn));
        }

        return new LinkImportResult(requests, errors);
    }

    private static void AddRequest(
        List<DownloadRequest> requests, List<string> errors, string where,
        string? url, string? fileName, string? saveDirectory, string? referer)
    {
        var trimmed = url?.Trim();
        if (string.IsNullOrEmpty(trimmed) || !IsHttpUrl(trimmed))
        {
            errors.Add($"{where}: invalid URL ('{url}').");
            return;
        }

        requests.Add(new DownloadRequest
        {
            Url = trimmed,
            FileName = SanitizeFileName(fileName),
            SaveDirectory = string.IsNullOrWhiteSpace(saveDirectory) ? null : saveDirectory.Trim(),
            Referer = string.IsNullOrWhiteSpace(referer) ? null : referer.Trim(),
        });
    }

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && !value.Any(char.IsControl);

    private static string? SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var cleaned = Path.GetFileName(name.Trim());
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            cleaned = cleaned.Replace(c, '_');
        }

        return cleaned is "" or "." or ".." ? null : cleaned;
    }

    /// <summary>Read a string matching the key name case-insensitively.</summary>
    private static string? ReadString(JsonElement element, string property)
    {
        foreach (var candidate in element.EnumerateObject())
        {
            if (string.Equals(candidate.Name, property, StringComparison.OrdinalIgnoreCase))
            {
                return candidate.Value.ValueKind == JsonValueKind.String ? candidate.Value.GetString() : null;
            }
        }

        return null;
    }

    private static LinkRow ToRow(DownloadItem item) => new(
        StripUserInfo(item.Url),
        item.FileName,
        item.SaveDirectory,
        item.Category,
        item.State.ToString(),
        item.Referer);

    private static string StripUserInfo(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.UserInfo)
            ? new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty }.Uri.AbsoluteUri
            : url;

    private static string Neutralize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return FormulaTriggers.Contains(value[0]) ? "'" + value : value;
    }

    private static string? Unneutralize(string? value)
    {
        if (value is { Length: > 1 } && value[0] == '\'' && FormulaTriggers.Contains(value[1]))
        {
            return value[1..];
        }

        return value;
    }

    private static string CsvEscape(string value)
    {
        if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0)
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>Simple RFC 4180 CSV parser (quoted fields, "" as a quote, CRLF or LF).</summary>
    private static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

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
                    row.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    rows.Add(row);
                    row = new List<string>();
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }

        if (inQuotes)
        {
            throw new BackupFormatException("The CSV has an unterminated quote.");
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows;
    }

    internal sealed record LinkRow(
        string Url, string? FileName, string? SaveDirectory, string? Category, string? State, string? Referer);
}
