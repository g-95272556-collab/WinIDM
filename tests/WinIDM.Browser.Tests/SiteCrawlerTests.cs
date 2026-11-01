using System.Text;

namespace WinIDM.Browser.Tests;

public class SiteCrawlerTests
{
    private sealed class SyncProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }

    private static HttpReply Html(string body) =>
        HttpReply.Text("<!doctype html><html><body>" + body + "</body></html>", 200, "text/html; charset=utf-8");

    private static string PortOf(HttpCall call) => call.Headers["Host"].Split(':')[1];

    private static Dictionary<string, Func<HttpCall, HttpReply>> SiteRoutes() => new()
    {
        ["/"] = call => Html(
            "<a href=\"/p/a.html\">A</a>" +
            "<a href=\"/files/one.zip\">one</a>" +
            "<img src=\"/img/pic.png\">" +
            "<a href=\"http://localhost:" + PortOf(call) + "/files/outside.zip\">outside</a>"),
        ["/p/a.html"] = _ => Html("<a href=\"/files/deep.zip\">inside</a><a href=\"/p/c.html\">c</a>"),
        ["/p/c.html"] = _ => Html("<a href=\"/files/deeper.zip\">deeper</a>"),
    };

    private static CrawlOptions Opts(string url, int depth = 1, bool robots = false) => new()
    {
        StartUrl = url,
        MaxDepth = depth,
        PoliteDelay = TimeSpan.Zero,
        RespectRobotsTxt = robots,
    };

    [Fact]
    public async Task Depth_one_collects_files_and_pages_within_depth_and_host_scope()
    {
        using var server = new TestHttpServer(SiteRoutes());
        using var crawler = new SiteCrawler();

        var result = await crawler.RunAsync(Opts(server.BaseUrl + "/", depth: 1));
        var urls = result.Candidates.Select(c => c.Url).ToHashSet(StringComparer.Ordinal);

        Assert.Contains(server.BaseUrl + "/files/one.zip", urls);
        Assert.Contains(server.BaseUrl + "/img/pic.png", urls);
        Assert.Contains(server.BaseUrl + "/files/deep.zip", urls);
        Assert.DoesNotContain(server.BaseUrl + "/files/deeper.zip", urls);
        Assert.DoesNotContain(urls, u => u.Contains("outside.zip", StringComparison.Ordinal));
        Assert.Equal(2, result.Pages.Count);
        Assert.True(result.SkippedByScope >= 1);
        Assert.False(result.Cancelled);
    }

    [Fact]
    public async Task Depth_two_reaches_deeper_pages()
    {
        using var server = new TestHttpServer(SiteRoutes());
        using var crawler = new SiteCrawler();

        var result = await crawler.RunAsync(Opts(server.BaseUrl + "/", depth: 2));
        Assert.Contains(result.Candidates, c => c.Url == server.BaseUrl + "/files/deeper.zip" && c.Depth == 2);
        Assert.Equal(3, result.Pages.Count);
    }

    [Fact]
    public async Task Max_pages_limits_number_of_fetched_pages()
    {
        using var server = new TestHttpServer(SiteRoutes());
        using var crawler = new SiteCrawler();

        var opts = new CrawlOptions { StartUrl = server.BaseUrl + "/", MaxDepth = 5, MaxPages = 1, PoliteDelay = TimeSpan.Zero, RespectRobotsTxt = false };
        var result = await crawler.RunAsync(opts);
        Assert.Single(result.Pages);
        Assert.Contains(result.Candidates, c => c.Url.EndsWith("/files/one.zip", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Candidates, c => c.Url.EndsWith("/files/deep.zip", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Extension_filter_keeps_only_listed_types()
    {
        using var server = new TestHttpServer(SiteRoutes());
        using var crawler = new SiteCrawler();

        var opts = new CrawlOptions
        {
            StartUrl = server.BaseUrl + "/", MaxDepth = 1, PoliteDelay = TimeSpan.Zero, RespectRobotsTxt = false,
            Extensions = new HashSet<string>(StringComparer.Ordinal) { ".ZIP" },
        };
        var result = await crawler.RunAsync(opts);
        Assert.NotEmpty(result.Candidates);
        Assert.All(result.Candidates, c => Assert.Equal("zip", c.Extension));
        Assert.DoesNotContain(result.Candidates, c => c.Url.EndsWith("pic.png", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Exclude_pattern_prunes_pages_and_files()
    {
        using var server = new TestHttpServer(SiteRoutes());
        using var crawler = new SiteCrawler();

        var opts = new CrawlOptions
        {
            StartUrl = server.BaseUrl + "/", MaxDepth = 2, PoliteDelay = TimeSpan.Zero, RespectRobotsTxt = false,
            ExcludeRegex = "/p/a",
        };
        var result = await crawler.RunAsync(opts);
        Assert.Single(result.Pages);
        Assert.DoesNotContain(result.Candidates, c => c.Url.EndsWith("/files/deep.zip", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Any_host_scope_follows_links_to_other_hosts()
    {
        using var server = new TestHttpServer(SiteRoutes());
        using var crawler = new SiteCrawler();

        var opts = new CrawlOptions
        {
            StartUrl = server.BaseUrl + "/", MaxDepth = 1, PoliteDelay = TimeSpan.Zero, RespectRobotsTxt = false,
            HostScope = CrawlHostScope.AnyHost,
        };
        var result = await crawler.RunAsync(opts);
        Assert.Contains(result.Candidates, c => c.Url.EndsWith("/files/outside.zip", StringComparison.Ordinal));
        Assert.Equal(0, result.SkippedByScope);
    }

    [Fact]
    public async Task Robots_disallow_stops_fetching_of_disallowed_pages()
    {
        var routes = SiteRoutes();
        routes["/robots.txt"] = _ => HttpReply.Text("User-agent: *\nDisallow: /p/\n");
        using var server = new TestHttpServer(routes);
        using var crawler = new SiteCrawler();

        var result = await crawler.RunAsync(new CrawlOptions
        {
            StartUrl = server.BaseUrl + "/", MaxDepth = 2, PoliteDelay = TimeSpan.Zero, RespectRobotsTxt = true,
        });
        Assert.Single(result.Pages);
        Assert.True(result.SkippedByRobots >= 1);
        Assert.DoesNotContain(result.Candidates, c => c.Url.EndsWith("/files/deep.zip", StringComparison.Ordinal));
        Assert.Equal(0, server.CallCount("/p/a.html"));
    }

    [Fact]
    public async Task Robots_server_error_disallows_everything()
    {
        var routes = SiteRoutes();
        routes["/robots.txt"] = _ => HttpReply.Text("error", 500);
        using var server = new TestHttpServer(routes);
        using var crawler = new SiteCrawler();

        var result = await crawler.RunAsync(new CrawlOptions
        {
            StartUrl = server.BaseUrl + "/", MaxDepth = 2, PoliteDelay = TimeSpan.Zero, RespectRobotsTxt = true,
        });
        Assert.Empty(result.Pages);
        Assert.Empty(result.Candidates);
        Assert.True(result.SkippedByRobots >= 1);
        Assert.Equal(0, server.CallCount("/"));
    }

    [Fact]
    public async Task Size_filter_uses_head_metadata_of_candidates()
    {
        using var server = new TestHttpServer(new Dictionary<string, Func<HttpCall, HttpReply>>
        {
            ["/"] = _ => Html("<a href=\"/files/big.bin\">big</a><a href=\"/files/small.bin\">small</a>"),
            ["/files/big.bin"] = _ => HttpReply.Bytes(new byte[5000]),
            ["/files/small.bin"] = _ => HttpReply.Bytes(new byte[100]),
        });
        using var crawler = new SiteCrawler();

        var result = await crawler.RunAsync(new CrawlOptions
        {
            StartUrl = server.BaseUrl + "/", MaxDepth = 0, PoliteDelay = TimeSpan.Zero, RespectRobotsTxt = false,
            MinSizeBytes = 1000,
        });
        var candidate = Assert.Single(result.Candidates);
        Assert.EndsWith("/files/big.bin", candidate.Url);
        Assert.Equal(5000, candidate.SizeBytes);
    }

    [Fact]
    public async Task Modified_date_filter_uses_last_modified_header()
    {
        using var server = new TestHttpServer(new Dictionary<string, Func<HttpCall, HttpReply>>
        {
            ["/"] = _ => Html("<a href=\"/files/old.pdf\">old</a><a href=\"/files/new.pdf\">new</a>"),
            ["/files/old.pdf"] = _ => new HttpReply(200, new byte[10], "application/pdf",
                new Dictionary<string, string> { ["Last-Modified"] = "Wed, 01 Jan 2020 00:00:00 GMT" }),
            ["/files/new.pdf"] = _ => new HttpReply(200, new byte[10], "application/pdf",
                new Dictionary<string, string> { ["Last-Modified"] = "Sat, 01 Jan 2028 00:00:00 GMT" }),
        });
        using var crawler = new SiteCrawler();

        var result = await crawler.RunAsync(new CrawlOptions
        {
            StartUrl = server.BaseUrl + "/", MaxDepth = 0, PoliteDelay = TimeSpan.Zero, RespectRobotsTxt = false,
            ModifiedAfter = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
        });
        var candidate = Assert.Single(result.Candidates);
        Assert.EndsWith("/files/new.pdf", candidate.Url);
        Assert.NotNull(candidate.LastModifiedUtc);
    }

    [Fact]
    public async Task Cancellation_returns_partial_result_with_flag()
    {
        using var server = new TestHttpServer(SiteRoutes());
        using var crawler = new SiteCrawler();
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress<CrawlProgress>(_ => cts.Cancel());

        var result = await crawler.RunAsync(Opts(server.BaseUrl + "/", depth: 2), progress, cts.Token);
        Assert.True(result.Cancelled);
        Assert.Single(result.Pages);
        Assert.Equal(1, server.CallCount("/"));
        Assert.Equal(0, server.CallCount("/p/a.html"));
    }

    [Fact]
    public async Task Progress_reports_pages_and_candidates()
    {
        using var server = new TestHttpServer(SiteRoutes());
        using var crawler = new SiteCrawler();
        var reports = new List<CrawlProgress>();
        var progress = new SyncProgress<CrawlProgress>(reports.Add);

        var result = await crawler.RunAsync(Opts(server.BaseUrl + "/", depth: 1), progress);
        Assert.NotEmpty(reports);
        Assert.Equal(result.Pages.Count, reports.Max(r => r.PagesVisited));
        Assert.Equal(result.Candidates.Count, reports.Max(r => r.CandidatesFound));
    }

    [Fact]
    public async Task Only_one_session_runs_at_a_time()
    {
        using var gate = new ManualResetEventSlim(false);
        var routes = new Dictionary<string, Func<HttpCall, HttpReply>>
        {
            ["/"] = _ => { gate.Wait(TimeSpan.FromSeconds(15)); return Html("<a href=\"/x.zip\">x</a>"); },
        };
        using var server = new TestHttpServer(routes);
        using var crawler = new SiteCrawler();

        var first = crawler.RunAsync(Opts(server.BaseUrl + "/"));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!crawler.IsRunning && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(crawler.IsRunning);

        await Assert.ThrowsAsync<InvalidOperationException>(() => crawler.RunAsync(Opts(server.BaseUrl + "/")));

        gate.Set();
        var result = await first;
        Assert.Single(result.Candidates);
        Assert.False(crawler.IsRunning);
    }

    [Fact]
    public async Task Invalid_options_are_rejected()
    {
        using var crawler = new SiteCrawler();
        await Assert.ThrowsAsync<ArgumentException>(() => crawler.RunAsync(new CrawlOptions { StartUrl = "ftp://127.0.0.1/" }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => crawler.RunAsync(new CrawlOptions { StartUrl = "http://127.0.0.1:1/", MaxDepth = 11 }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => crawler.RunAsync(new CrawlOptions { StartUrl = "http://127.0.0.1:1/", MaxPages = 0 }));
        await Assert.ThrowsAsync<ArgumentException>(() => crawler.RunAsync(new CrawlOptions { StartUrl = "http://127.0.0.1:1/", IncludeRegex = "([" }));
        await Assert.ThrowsAsync<ArgumentException>(() => crawler.RunAsync(new CrawlOptions { StartUrl = "http://127.0.0.1:1/", MinSizeBytes = 10, MaxSizeBytes = 5 }));
    }

    [Fact]
    public void Host_scope_rules()
    {
        Assert.True(SiteCrawler.IsInScope("127.0.0.1", "127.0.0.1", CrawlHostScope.ExactHost));
        Assert.False(SiteCrawler.IsInScope("www.example.com", "img.example.com", CrawlHostScope.ExactHost));
        Assert.True(SiteCrawler.IsInScope("www.example.com", "img.example.com", CrawlHostScope.IncludeSubdomains));
        Assert.True(SiteCrawler.IsInScope("www.example.com", "example.com", CrawlHostScope.IncludeSubdomains));
        Assert.False(SiteCrawler.IsInScope("www.example.com", "evilexample.com", CrawlHostScope.IncludeSubdomains));
        Assert.False(SiteCrawler.IsInScope("www.example.com", "example.org", CrawlHostScope.IncludeSubdomains));
        Assert.True(SiteCrawler.IsInScope("www.example.com", "anything.test", CrawlHostScope.AnyHost));
    }

    [Fact]
    public void Robots_longest_rule_wins_and_crawl_delay_is_parsed()
    {
        var r = RobotsTxt.Parse("User-agent: *\nDisallow: /a\nAllow: /a/b\nCrawl-delay: 2\n");
        Assert.False(r.IsAllowed("/a/x"));
        Assert.True(r.IsAllowed("/a/b/c"));
        Assert.True(r.IsAllowed("/z"));
        Assert.Equal(TimeSpan.FromSeconds(2), r.CrawlDelay);
    }

    [Fact]
    public void Specific_agent_group_overrides_wildcard_group()
    {
        var r = RobotsTxt.Parse("User-agent: *\nAllow: /\n\nUser-agent: WinIDM\nDisallow: /\n");
        Assert.False(r.IsAllowed("/x"));

        var other = RobotsTxt.Parse("User-agent: GoogleBot\nDisallow: /\n\nUser-agent: *\nAllow: /\n");
        Assert.True(other.IsAllowed("/x"));
    }

    [Fact]
    public void Robots_wildcards_and_end_anchor()
    {
        var r = RobotsTxt.Parse("User-agent: *\nDisallow: /*.pdf$\n");
        Assert.False(r.IsAllowed("/doc/a.pdf"));
        Assert.True(r.IsAllowed("/doc/a.pdfx"));
    }

    [Fact]
    public void Empty_or_missing_rules_allow_everything()
    {
        Assert.True(RobotsTxt.Parse("").IsAllowed("/x"));
        Assert.True(RobotsTxt.Parse("User-agent: *\nDisallow:\n").IsAllowed("/x"));
        Assert.True(RobotsTxt.AllowAll.IsAllowed("/x"));
        Assert.False(RobotsTxt.DisallowAll.IsAllowed("/x"));
    }
}
