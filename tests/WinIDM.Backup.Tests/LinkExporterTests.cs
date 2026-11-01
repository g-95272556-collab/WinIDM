using WinIDM.Backup;
using WinIDM.Core.Models;

namespace WinIDM.Backup.Tests;

public class LinkExporterTests
{
    private static DownloadItem Item(string url, string fileName, string dir = @"C:\Downloads") => new()
    {
        Url = url,
        FileName = fileName,
        SaveDirectory = dir,
        Category = "Document",
        State = DownloadState.Queued,
        Referer = "https://referrer.example",
        Password = "secret-password",
        CookieHeader = "sid=secret-cookie",
    };

    [Fact]
    public void Csv_round_trip_keeps_commas_and_quotes_in_fields()
    {
        var items = new[] { Item("https://sample.example/file?x=1,2&y=3", "report, final.pdf") };

        var csv = LinkExporter.ExportCsv(items);
        var result = LinkExporter.ImportCsv(csv);

        Assert.Empty(result.Errors);
        var request = Assert.Single(result.Requests);
        Assert.Equal("https://sample.example/file?x=1,2&y=3", request.Url);
        Assert.Equal("report, final.pdf", request.FileName);
        Assert.Equal(@"C:\Downloads", request.SaveDirectory);
        Assert.Equal("https://referrer.example", request.Referer);
    }

    [Fact]
    public void Exports_never_contain_credentials()
    {
        var items = new[] { Item("https://user:pass@sample.example/a.bin", "a.bin") };

        var json = LinkExporter.ExportJson(items);
        var csv = LinkExporter.ExportCsv(items);

        Assert.DoesNotContain("secret", json);
        Assert.DoesNotContain("secret", csv);
        Assert.DoesNotContain("user:pass", json);
        Assert.Contains("https://sample.example/a.bin", json);
    }

    [Fact]
    public void Formula_like_cells_are_neutralised_on_export_and_restored_on_import()
    {
        var items = new[] { Item("https://sample.example/x", "=cmd.txt") };

        var csv = LinkExporter.ExportCsv(items);
        var result = LinkExporter.ImportCsv(csv);

        Assert.Contains("'=cmd.txt", csv);
        Assert.Equal("=cmd.txt", Assert.Single(result.Requests).FileName);
    }

    [Fact]
    public void Json_import_rejects_non_http_urls_and_sanitises_names()
    {
        const string json = """
            [
              "https://ok.example/a",
              "ftp://unsupported.example/b",
              "not a url",
              { "url": "http://ok.example/c", "fileName": "..\\..\\evil.bin" }
            ]
            """;

        var result = LinkExporter.ImportJson(json);

        Assert.Equal(2, result.Requests.Count);
        Assert.Equal(2, result.Errors.Count);
        Assert.Equal("https://ok.example/a", result.Requests[0].Url);
        Assert.Equal("evil.bin", result.Requests[1].FileName);
    }

    [Fact]
    public void Json_export_then_import_round_trips_urls()
    {
        var items = new[] { Item("https://a.example/1", "one.bin"), Item("https://b.example/2", "two.bin") };

        var result = LinkExporter.ImportJson(LinkExporter.ExportJson(items));

        Assert.Empty(result.Errors);
        Assert.Equal(new[] { "https://a.example/1", "https://b.example/2" }, result.Requests.Select(r => r.Url));
    }

    [Fact]
    public void Csv_without_url_column_is_refused()
    {
        Assert.Throws<BackupFormatException>(() => LinkExporter.ImportCsv("name,path\nfile,C:\\x"));
    }

    [Fact]
    public void Csv_with_unterminated_quote_is_refused()
    {
        Assert.Throws<BackupFormatException>(() => LinkExporter.ImportCsv("url\n\"https://a.example/x"));
    }

    [Fact]
    public void Invalid_csv_rows_are_reported_with_line_numbers()
    {
        var result = LinkExporter.ImportCsv("url,fileName\nhttps://ok.example/1,a.bin\nnot-a-url,b.bin\n");

        Assert.Single(result.Requests);
        var error = Assert.Single(result.Errors);
        Assert.Contains("Row 3", error);
    }
}
