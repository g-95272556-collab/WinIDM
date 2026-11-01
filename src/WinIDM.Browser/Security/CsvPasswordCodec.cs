using System.Text;

namespace WinIDM.Browser;

/// <summary>Result of parsing a password CSV.</summary>
public sealed record CsvParseResult(string Format, IReadOnlyList<ImportedCredential> Credentials, int SkippedRows);

/// <summary>
/// Password CSV codec. Detects the format from the headers: Bitwarden, 1Password, Chrome,
/// Safari, and Firefox. Rows without an http(s) URL, without a password, or not of the login type
/// are skipped and counted. Parsing follows RFC 4180 (quotes and commas in fields).
/// </summary>
public static class CsvPasswordCodec
{
    public static CsvParseResult Parse(string csv)
    {
        ArgumentNullException.ThrowIfNull(csv);
        var rows = ReadRows(csv.TrimStart('﻿'));
        if (rows.Count == 0) throw new FormatException("Empty CSV.");

        var headers = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Col(string name) => headers.IndexOf(name);
        bool Has(string name) => headers.Contains(name);

        string format;
        if (Has("login_uri") && Has("login_password")) format = "Bitwarden";
        else if (Has("title") && Has("website")) format = "1Password";
        else if (Has("name") && Has("url") && Has("username") && Has("password")) format = "Chrome";
        else if (Has("title") && Has("url") && Has("username") && Has("password")) format = "Safari";
        else if (Has("url") && Has("username") && Has("password")) format = "Firefox";
        else throw new FormatException("Unrecognized password CSV format.");

        var creds = new List<ImportedCredential>();
        int skipped = 0;

        for (int r = 1; r < rows.Count; r++)
        {
            var row = rows[r];
            if (row.Count == 1 && row[0].Length == 0) continue; // empty row

            string Field(int index) => index >= 0 && index < row.Count ? row[index] : "";

            string urlField, userField, passField;
            if (format == "Bitwarden")
            {
                if (Has("type") && !string.Equals(Field(Col("type")), "login", StringComparison.OrdinalIgnoreCase))
                {
                    skipped++;
                    continue;
                }
                urlField = Field(Col("login_uri"));
                userField = Field(Col("login_username"));
                passField = Field(Col("login_password"));
            }
            else if (format == "1Password")
            {
                urlField = Field(Col("website"));
                userField = Field(Col("username"));
                passField = Field(Col("password"));
            }
            else
            {
                urlField = Field(Col("url"));
                userField = Field(Col("username"));
                passField = Field(Col("password"));
            }

            if (passField.Length == 0)
            {
                skipped++;
                continue;
            }

            bool any = false;
            foreach (string candidate in urlField.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!OriginNormalizer.TryNormalize(candidate, out var origin)) continue;
                creds.Add(new ImportedCredential(origin.Origin, userField, passField));
                any = true;
            }
            if (!any) skipped++;
        }

        return new CsvParseResult(format, creds, skipped);
    }

    /// <summary>Chrome-style export: name,url,username,password. Values are quoted per RFC 4180.</summary>
    public static string Export(IEnumerable<(string Origin, string Username, string Password)> rows)
    {
        var sb = new StringBuilder();
        sb.Append("name,url,username,password\r\n");
        foreach (var (origin, username, password) in rows)
        {
            string name = Uri.TryCreate(origin, UriKind.Absolute, out var u) ? u.Host : origin;
            sb.Append(Quote(name)).Append(',')
              .Append(Quote(origin)).Append(',')
              .Append(Quote(username)).Append(',')
              .Append(Quote(password)).Append("\r\n");
        }
        return sb.ToString();
    }

    private static string Quote(string value)
    {
        bool needs = value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0;
        return needs ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    /// <summary>Simple RFC 4180 CSV parser. Supports quoted fields with commas, newlines, and "".</summary>
    internal static List<List<string>> ReadRows(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
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

        if (inQuotes) throw new FormatException("Unclosed quote in CSV.");
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows;
    }
}
