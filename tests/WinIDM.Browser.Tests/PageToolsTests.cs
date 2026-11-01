namespace WinIDM.Browser.Tests;

public class PageToolsTests
{
    private const string Article = """
<!DOCTYPE html>
<html><head><title>Title &amp; Article - Page</title>
<meta name="author" content="Test Author">
<script>var x = "<p>ignored script paragraph with many words inside the script body text</p>";</script>
<style>p{color:red}</style></head>
<body>
<nav><a href="/a">Main navigation menu</a> <a href="/b">Other</a></nav>
<div class="sidebar"><p>Long sidebar subscription ad text to ignore in reader mode.</p></div>
<article>
<h1>Article Title</h1>
<p>The first article paragraph is long with many words for density scoring.</p>
<p>The second paragraph contains <a href="/reference">a reference link</a> and ordinary interesting text.</p>
<ul><li>Item one</li><li>Item two</li></ul>
<p>Third paragraph with entity &eacute; and &amp; ampersand.<br>New line here.</p>
</article>
<footer>Copyright 2026 footer text</footer>
</body></html>
""";

    [Fact]
    public void Reader_extracts_article_text_title_and_byline()
    {
        using var tools = new PageTools();
        var article = tools.ExtractReadableArticle(Article, "https://news.test/a");

        Assert.Equal("Title & Article - Page", article.Title);
        Assert.Equal("Test Author", article.Byline);
        Assert.Contains("The first article paragraph", article.Text);
        Assert.Contains("The second paragraph contains a reference link", article.Text);
        Assert.Contains("- Item one", article.Text);
        Assert.Contains("New line here.", article.Text);
        Assert.Contains("entity é and & ampersand", article.Text);

        Assert.DoesNotContain("Main navigation", article.Text);
        Assert.DoesNotContain("sidebar subscription ad", article.Text);
        Assert.DoesNotContain("Copyright", article.Text);
        Assert.DoesNotContain("ignored script", article.Text);
        Assert.DoesNotContain("color:red", article.Text);
        Assert.False(string.IsNullOrEmpty(article.Excerpt));
        Assert.True(article.Excerpt.Length <= 200);
    }

    [Fact]
    public void Reader_falls_back_to_body_text_and_og_title()
    {
        using var tools = new PageTools();
        var shortPage = tools.ExtractReadableArticle("<html><head><meta property=\"og:title\" content=\"OG &quot;Title&quot;\"></head><body><div>Short text without paragraphs.</div></body></html>");
        Assert.Equal("OG \"Title\"", shortPage.Title);
        Assert.Equal("Short text without paragraphs.", shortPage.Text);
        Assert.Null(shortPage.Byline);

        var empty = tools.ExtractReadableArticle("");
        Assert.Equal("", empty.Title);
        Assert.Equal("", empty.Text);
    }

    [Fact]
    public void Reader_handles_unclosed_tags_and_li_p_implicit_closing()
    {
        using var tools = new PageTools();
        var a = tools.ExtractReadableArticle("<body><p>First one without a closing tag that is long enough to count<p>Second is also long enough for scoring<ul><li>one<li>two</ul></body>");
        Assert.Contains("First one without a closing tag", a.Text);
        Assert.Contains("Second is also long enough", a.Text);
        Assert.Contains("- one", a.Text);
        Assert.Contains("- two", a.Text);
    }

    [Fact]
    public void Links_resolve_against_base_href_and_skip_unsafe_schemes()
    {
        const string html = """
<html><head><base href="https://cdn.site.test/root/"></head><body>
<a href="docs/manual.pdf">Guide &amp; Manual</a>
<a href="/absolute/archive.zip">Archive</a>
<a href="https://other.test/video.MP4#t=10">Video</a>
<a href="#top">top</a>
<a href="javascript:void(0)">js</a>
<a href="mailto:a@b.c">mail</a>
<a href="ftp://x/y.txt">ftp</a>
<a href="docs/manual.pdf">dup</a>
<img src="pics/photo.jpg" alt="Picture">
<video src="clips/c.webm"></video>
<source src="audio/a.mp3">
<a href="page.html"></a>
<script>document.write('<a href="http://evil.test/x">x</a>')</script>
<a href="https://other.test/listing">List</a>
</body></html>
""";
        using var tools = new PageTools();
        var links = tools.ExtractLinks(html, "https://page.test/index.html");

        Assert.Equal(8, links.Count);
        Assert.DoesNotContain(links, l => l.Url.Contains("evil.test"));
        Assert.DoesNotContain(links, l => l.Url.StartsWith("ftp", StringComparison.Ordinal) || l.Url.StartsWith("mailto", StringComparison.Ordinal));

        var manual = links.Single(l => l.Url.EndsWith("manual.pdf", StringComparison.Ordinal));
        Assert.Equal("https://cdn.site.test/root/docs/manual.pdf", manual.Url);
        Assert.Equal("Guide & Manual", manual.Text);
        Assert.Equal(FileCategory.Document, manual.Category);

        Assert.Equal(FileCategory.Archive, links.Single(l => l.Url.EndsWith("archive.zip", StringComparison.Ordinal)).Category);
        Assert.Equal(FileCategory.Video, links.Single(l => l.Url.Contains("video.MP4", StringComparison.Ordinal) || l.Url.EndsWith("video.mp4", StringComparison.OrdinalIgnoreCase)).Category);
        Assert.Equal("Picture", links.Single(l => l.Url.EndsWith("photo.jpg", StringComparison.Ordinal)).Text);
        Assert.Equal(FileCategory.Image, links.Single(l => l.Url.EndsWith("photo.jpg", StringComparison.Ordinal)).Category);
        Assert.Equal("c.webm", links.Single(l => l.Url.EndsWith("c.webm", StringComparison.Ordinal)).Text);
        Assert.Equal(FileCategory.Audio, links.Single(l => l.Url.EndsWith("a.mp3", StringComparison.Ordinal)).Category);
        Assert.Equal(FileCategory.Page, links.Single(l => l.Url.EndsWith("page.html", StringComparison.Ordinal)).Category);
        Assert.Equal(FileCategory.Page, links.Single(l => l.Url.EndsWith("/listing", StringComparison.Ordinal)).Category);
    }

    [Fact]
    public void Script_contents_never_produce_links()
    {
        using var tools = new PageTools();
        var links = tools.ExtractLinks(
            "<script>document.write('<a href=\"http://evil.test/x\">x</a>')</script><a href='http://ok.test/x'>ok</a>",
            "https://page.test/");
        Assert.Single(links);
        Assert.Equal("http://ok.test/x", links[0].Url);
    }

    [Fact]
    public void Link_filters_by_category_pattern_and_size()
    {
        const string html = """
<a href="https://a.test/x.zip">zip</a><a href="https://b.test/y.pdf">pdf</a>
<a href="https://a.test/clip.mp4">mp4</a><a href="https://c.test/page">page</a>
""";
        using var tools = new PageTools();
        var all = tools.ExtractLinks(html, "https://page.test/");
        Assert.Equal(4, all.Count);

        var docs = tools.ExtractLinks(html, "https://page.test/",
            LinkFilter.FromPattern(".", new HashSet<FileCategory> { FileCategory.Document, FileCategory.Archive }));
        Assert.Equal(2, docs.Count);

        var onlyA = tools.ExtractLinks(html, "https://page.test/", LinkFilter.FromPattern(@"^https://a\.test/"));
        Assert.Equal(2, onlyA.Count);

        var sized = new LinkFilter { MinSizeBytes = 100, MaxSizeBytes = 1000 };
        var known = new GrabbedLink("https://z.test/1", "small", FileCategory.Other, 50);
        var mid = new GrabbedLink("https://z.test/2", "mid", FileCategory.Other, 500);
        var unknown = new GrabbedLink("https://z.test/3", "unknown", FileCategory.Other, null);
        var kept = sized.Apply(new[] { known, mid, unknown });
        Assert.Equal(new[] { "mid", "unknown" }, kept.Select(k => k.Text).ToArray());

        Assert.ThrowsAny<ArgumentException>(() => LinkFilter.FromPattern("([unclosed"));
        Assert.Throws<ArgumentException>(() => tools.ExtractLinks(html, "ftp://page.test/"));
        Assert.Throws<ArgumentNullException>(() => tools.ExtractLinks(null!, "https://page.test/"));
    }

    [Fact]
    public void Link_filter_timeouts_and_empty_urls_are_safe()
    {
        // Exponential backtracking pattern: the regex timeout must stop it without freezing the process.
        var filter = LinkFilter.FromPattern("(a+)+$");
        var link = new GrabbedLink("https://x.test/" + new string('a', 40) + "!", "t", FileCategory.Other, null);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        bool matched = filter.Matches(link);
        watch.Stop();
        Assert.False(matched);
        Assert.True(watch.ElapsedMilliseconds < 5000, $"Regex took {watch.ElapsedMilliseconds} ms");
        Assert.Equal(FileCategory.Other, PageTools.Categorize("not a url"));
        Assert.Equal(FileCategory.Other, PageTools.Categorize("https://x.test/file.unknownext"));
        Assert.Equal(FileCategory.Subtitle, PageTools.Categorize("https://x.test/movie.srt"));
        Assert.Equal(FileCategory.Application, PageTools.Categorize("https://x.test/setup.exe"));
    }

    [Fact]
    public async Task Probe_sizes_uses_head_content_length_and_tolerates_failures()
    {
        byte[] body = new byte[1234];
        using var server = new TestHttpServer(new Dictionary<string, Func<HttpCall, HttpReply>>
        {
            ["/file.zip"] = _ => HttpReply.Bytes(body, "application/zip"),
            ["/gone"] = _ => HttpReply.Text("gone", 404),
        });

        using var tools = new PageTools();
        var links = new[]
        {
            new GrabbedLink(server.BaseUrl + "/file.zip", "zip", FileCategory.Archive, null),
            new GrabbedLink(server.BaseUrl + "/gone", "gone", FileCategory.Other, null),
            new GrabbedLink("ftp://x.test/file", "ftp", FileCategory.Other, null),
        };

        var probed = await tools.ProbeSizesAsync(links, maxConcurrency: 2);
        Assert.Equal(1234, probed[0].SizeBytes);
        Assert.Null(probed[1].SizeBytes);
        Assert.Null(probed[2].SizeBytes);
        Assert.Equal(FileCategory.Archive, probed[0].Category);
        Assert.Equal(1, server.CallCount("/file.zip"));
        Assert.Equal("HEAD", server.Calls.First(c => c.Path == "/file.zip").Method);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => tools.ProbeSizesAsync(links, maxConcurrency: 0));
    }

    [Fact]
    public void Owned_and_injected_http_clients_are_supported()
    {
        using var injected = new HttpClient();
        using var tools = new PageTools(injected);
        Assert.Single(tools.ExtractLinks("<a href=\"https://q.test/x.bin\">x</a>", "https://page.test/"));
    }
}
