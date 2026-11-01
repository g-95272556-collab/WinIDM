using System.Text;
using WinIDM.Engine;

namespace WinIDM.Engine.Tests;

/// <summary>Link import parser and clipboard extraction. No network requests are made.</summary>
public sealed class ImportExportTests
{
    [Fact]
    public void PlainText_KeepsValidUrls_SkipsBlankAndComments_AndRejectsRest()
    {
        const string text = "# list\n\nhttps://example.test/a.zip\r\n  http://example.test/b.bin  \nftp://unsupported.test/c\nnot a url\nhttps://example.test/a.zip\n";

        var result = ImportExport.Parse(text);

        Assert.Equal(ImportFormat.PlainText, result.Format);
        Assert.Equal(new[] { "https://example.test/a.zip", "http://example.test/b.bin" }, result.Links.Select(l => l.Url).ToArray());
        Assert.Equal(new[] { 3, 4 }, result.Links.Select(l => l.Line).ToArray());
        Assert.Contains(result.Rejected, r => r.Line == 5 && r.Reason.Contains("http"));
        Assert.Contains(result.Rejected, r => r.Line == 6);
        Assert.Contains(result.Rejected, r => r.Line == 7 && r.Reason.Contains("Duplicate"));
        Assert.False(result.Truncated);
    }

    [Fact]
    public void PlainText_RejectsUrlsWithSpacesOrControlCharacters()
    {
        var result = ImportExport.Parse("https://example.test/file with space.zip\nhttps://example.test/\u0001x\njavascript:alert(1)\n");

        Assert.Empty(result.Links);
        Assert.Equal(3, result.Rejected.Count);
    }

    [Fact]
    public void Json_AcceptsStringsAndObjects_WithFileName()
    {
        const string json = """
            [
              "https://example.test/one.zip",
              { "url": "https://example.test/two.bin", "fileName": "new-name.bin" },
              { "URL": "https://example.test/three.mp4", "name": "three.mp4" },
              { "size": 12 },
              42
            ]
            """;

        var result = ImportExport.Parse(json);

        Assert.Equal(ImportFormat.Json, result.Format);
        Assert.Equal(3, result.Links.Count);
        Assert.Equal("new-name.bin", result.Links[1].FileName);
        Assert.Equal("three.mp4", result.Links[2].FileName);
        Assert.Equal(2, result.Rejected.Count);
        Assert.Equal(4, result.Rejected[0].Line);
        Assert.Equal(5, result.Rejected[1].Line);
    }

    [Fact]
    public void Json_MalformedOrNotArray_ThrowsFormatException()
    {
        Assert.ThrowsAny<FormatException>(() => ImportExport.Parse("[\"https://example.test/x\"", ImportFormat.Json));
        Assert.Throws<FormatException>(() => ImportExport.Parse("{\"url\":\"https://example.test/x\"}", ImportFormat.Json));
    }

    [Fact]
    public void Csv_WithHeader_ReadsQuotedFieldsAndNames()
    {
        const string csv = "url,fileName\n\"https://example.test/a.zip\",\"name, with comma.zip\"\nhttps://example.test/b.bin,\n,empty\n";

        var result = ImportExport.Parse(csv, ImportFormat.Csv);

        Assert.Equal(["https://example.test/a.zip", "https://example.test/b.bin"], result.Links.Select(l => l.Url));
        Assert.Equal("name, with comma.zip", result.Links[0].FileName);
        Assert.Null(result.Links[1].FileName);
        Assert.Contains(result.Rejected, r => r.Line == 4);
    }

    [Fact]
    public void Csv_WithoutHeader_UsesFirstColumnWhenFormatIsExplicit()
    {
        var result = ImportExport.Parse("https://example.test/x.bin,ignore\nhttps://example.test/y.bin,ignore\n", ImportFormat.Csv);

        Assert.Equal(2, result.Links.Count);
    }

    [Fact]
    public void Csv_WithoutHeader_IsNotAutoDetected()
    {
        // Without a header, CSV is not auto-detected: lines are treated as plain text (the comma stays part of the URL).
        var result = ImportExport.Parse("https://example.test/x.bin,ignore\n");

        Assert.Equal(ImportFormat.PlainText, result.Format);
        Assert.Equal("https://example.test/x.bin,ignore", result.Links[0].Url);
    }

    [Fact]
    public void NetscapeBookmarks_ExtractsHrefs_DecodesEntities_AndRejectsNonWeb()
    {
        const string html = """
            <!DOCTYPE NETSCAPE-Bookmark-file-1>
            <DL><p>
              <DT><A HREF="https://example.test/fail?a=1&amp;b=2" ADD_DATE="1">One</A>
              <DT><A HREF='http://example.test/two.zip'>Two</A>
              <DT><A HREF="place:sort=8">Place</A>
              <DT><A HREF="javascript:void(0)">Script</A>
            </DL><p>
            """;

        var result = ImportExport.Parse(html);

        Assert.Equal(ImportFormat.NetscapeHtml, result.Format);
        Assert.Equal(["https://example.test/fail?a=1&b=2", "http://example.test/two.zip"], result.Links.Select(l => l.Url));
        Assert.Equal(2, result.Rejected.Count);
        Assert.Equal(3, result.Links[0].Line);
    }

    [Fact]
    public void Auto_DetectsJsonAndNetscape()
    {
        Assert.Equal(ImportFormat.Json, ImportExport.Parse("  [\"https://example.test/x\"]").Format);
        Assert.Equal(ImportFormat.NetscapeHtml, ImportExport.Parse("<A HREF=\"https://example.test/x\">x</A>").Format);
    }

    [Fact]
    public void OneDmText_IsNotSupported_AndNotGuessed()
    {
        var ex = Assert.Throws<NotSupportedException>(() => ImportExport.Parse("1DM\nhttps://example.test/x", ImportFormat.OneDmText));

        Assert.Contains("1DM", ex.Message);
    }

    [Fact]
    public void Limit_TruncatesAfterMaxLinks()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < ImportExport.MaxLinks + 5; i++) sb.Append("https://example.test/fail-").Append(i).Append(".bin\n");

        var result = ImportExport.Parse(sb.ToString(), ImportFormat.PlainText);

        Assert.Equal(ImportExport.MaxLinks, result.Links.Count);
        Assert.True(result.Truncated);
    }

    [Fact]
    public void ExtractUrls_FindsUrlsInFreeText_TrimsPunctuation_AndDeduplicates()
    {
        const string text = "Watch at https://example.test/v.mp4, or (http://example.test/w.ts). Repeat https://example.test/v.mp4 and ftp://not.test/x";

        var urls = ImportExport.ExtractUrls(text);

        Assert.Equal(["https://example.test/v.mp4", "http://example.test/w.ts"], urls);
    }
}
