using System.Diagnostics;
using System.Text;
using WinIDM.Core.Contracts;

namespace WinIDM.Browser.Tests;

public class AdBlockerTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public AdBlockerTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    private static AdBlocker Make(string list)
    {
        var blocker = new AdBlocker();
        blocker.LoadFilterList("test", list);
        return blocker;
    }

    [Fact]
    public void DomainAnchor_blocks_host_and_subdomains_but_not_lookalikes()
    {
        var b = Make("||ads.example.com^");
        Assert.True(b.ShouldBlock("https://ads.example.com/x.js", "https://news.test/", ResourceType.Script));
        Assert.True(b.ShouldBlock("https://sub.ads.example.com/x.js", "https://news.test/", ResourceType.Script));
        Assert.False(b.ShouldBlock("https://notads.example.com/x.js", "https://news.test/", ResourceType.Script));
        Assert.False(b.ShouldBlock("https://example.com/", "https://news.test/", ResourceType.Script));
    }

    [Fact]
    public void Exception_rule_overrides_block_rule()
    {
        var b = Make("||ads.test^\n@@||cdn.ads.test^");
        Assert.True(b.ShouldBlock("https://ads.test/a.js", "https://news.test/", ResourceType.Script));
        Assert.False(b.ShouldBlock("https://cdn.ads.test/a.js", "https://news.test/", ResourceType.Script));
    }

    [Fact]
    public void ThirdParty_option_blocks_only_cross_site_requests()
    {
        var b = Make("||tracker.test^$third-party");
        Assert.False(b.ShouldBlock("https://tracker.test/t.js", "https://tracker.test/home", ResourceType.Script));
        Assert.True(b.ShouldBlock("https://tracker.test/t.js", "https://news.test/", ResourceType.Script));

        var firstOnly = Make("||tracker.test^$~third-party");
        Assert.True(firstOnly.ShouldBlock("https://tracker.test/t.js", "https://tracker.test/home", ResourceType.Script));
        Assert.False(firstOnly.ShouldBlock("https://tracker.test/t.js", "https://news.test/", ResourceType.Script));
    }

    [Theory]
    [InlineData("$script", ResourceType.Script, true)]
    [InlineData("$script", ResourceType.Image, false)]
    [InlineData("$image", ResourceType.Image, true)]
    [InlineData("$xmlhttprequest", ResourceType.XmlHttpRequest, true)]
    [InlineData("$xmlhttprequest", ResourceType.Image, false)]
    [InlineData("$stylesheet", ResourceType.Stylesheet, true)]
    [InlineData("$~image", ResourceType.Image, false)]
    [InlineData("$~image", ResourceType.Script, true)]
    public void ResourceType_options_are_respected(string option, ResourceType type, bool expected)
    {
        var b = Make("||ads.test^" + option);
        Assert.Equal(expected, b.ShouldBlock("https://ads.test/r", "https://news.test/", type));
    }

    [Fact]
    public void Default_rules_do_not_block_top_level_documents()
    {
        var b = Make("||ads.test^");
        Assert.False(b.ShouldBlock("https://ads.test/", "https://news.test/", ResourceType.Document));
    }

    [Fact]
    public void Domain_option_includes_and_excludes_page_hosts()
    {
        var b = Make("||ads.test^$domain=news.test|~sport.news.test");
        Assert.True(b.ShouldBlock("https://ads.test/x", "https://www.news.test/", ResourceType.Script));
        Assert.False(b.ShouldBlock("https://ads.test/x", "https://other.test/", ResourceType.Script));
        Assert.False(b.ShouldBlock("https://ads.test/x", "https://sport.news.test/", ResourceType.Script));
        Assert.False(b.ShouldBlock("https://ads.test/x", "https://a.sport.news.test/", ResourceType.Script));
    }

    [Fact]
    public void Wildcard_and_separator_patterns()
    {
        var b = Make("||example.org/ads/*.gif\n||adserver.net^");
        Assert.True(b.ShouldBlock("https://example.org/ads/x/y.gif", "https://news.test/", ResourceType.Image));
        Assert.False(b.ShouldBlock("https://example.org/ads/x/y.png", "https://news.test/", ResourceType.Image));
        Assert.True(b.ShouldBlock("https://adserver.net:8080/x", "https://news.test/", ResourceType.Other));
        Assert.False(b.ShouldBlock("https://adserver.network/", "https://news.test/", ResourceType.Other));
    }

    [Fact]
    public void Plain_token_rule_matches_inside_url()
    {
        var b = Make("/track123/banner*.js$script");
        Assert.True(b.ShouldBlock("https://x.test/track123/banner1.js", "https://news.test/", ResourceType.Script));
        Assert.False(b.ShouldBlock("https://x.test/track123/banner1.js", "https://news.test/", ResourceType.Image));
        Assert.False(b.ShouldBlock("https://x.test/track124/banner1.js", "https://news.test/", ResourceType.Script));
    }

    [Fact]
    public void Regex_rule_matches_and_invalid_regex_is_unsupported()
    {
        var b = Make("/banner[0-9]+\\.gif/\n/([/");
        Assert.True(b.ShouldBlock("https://cdn.x.test/banner12.gif", "https://news.test/", ResourceType.Image));
        Assert.False(b.ShouldBlock("https://cdn.x.test/banner.gif", "https://news.test/", ResourceType.Image));
        Assert.Equal(1, b.Stats.UnsupportedRules);
    }

    [Fact]
    public void Start_and_end_anchors_are_exact()
    {
        var b = Make("|https://exact.test/page|");
        Assert.True(b.ShouldBlock("https://exact.test/page", "https://news.test/", ResourceType.Other));
        Assert.False(b.ShouldBlock("https://exact.test/page2", "https://news.test/", ResourceType.Other));
        Assert.False(b.ShouldBlock("https://other.test/https://exact.test/page", "https://news.test/", ResourceType.Other));
    }

    [Fact]
    public void Match_case_option_is_respected()
    {
        var sensitive = Make("/Ads/Banner.js$match-case");
        Assert.True(sensitive.ShouldBlock("https://x.test/Ads/Banner.js", "https://news.test/", ResourceType.Script));
        Assert.False(sensitive.ShouldBlock("https://x.test/ads/banner.js", "https://news.test/", ResourceType.Script));

        var insensitive = Make("/Ads/Banner.js");
        Assert.True(insensitive.ShouldBlock("https://x.test/ads/banner.js", "https://news.test/", ResourceType.Script));
    }

    [Fact]
    public void BadFilter_directive_disables_matching_rule_even_when_loaded_later()
    {
        var b = new AdBlocker();
        b.LoadFilterList("a", "||bad.test^\n||bad.test^$badfilter");
        Assert.False(b.ShouldBlock("https://bad.test/x", "https://news.test/", ResourceType.Script));

        var order = new AdBlocker();
        order.LoadFilterList("b", "||late.test^$badfilter\n||late.test^");
        Assert.False(order.ShouldBlock("https://late.test/x", "https://news.test/", ResourceType.Script));
    }

    [Fact]
    public void MarkBadFilter_removes_rule_at_runtime()
    {
        var b = Make("||ok.test^\n||keep.test^");
        Assert.True(b.ShouldBlock("https://ok.test/x", "https://news.test/", ResourceType.Script));
        b.MarkBadFilter("||ok.test^");
        Assert.False(b.ShouldBlock("https://ok.test/x", "https://news.test/", ResourceType.Script));
        Assert.True(b.ShouldBlock("https://keep.test/x", "https://news.test/", ResourceType.Script));
    }

    [Fact]
    public void Hosts_list_blocks_domains_and_ignores_local_entries()
    {
        var b = new AdBlocker();
        b.LoadHostsList("hosts", "# comment\n0.0.0.0 tracker.example\n127.0.0.1 localhost\nads.example.net\n0.0.0.0 www.www-site.test\n");
        Assert.True(b.ShouldBlock("https://sub.tracker.example/x", "https://news.test/", ResourceType.Other));
        Assert.True(b.ShouldBlock("https://ads.example.net/x", "https://news.test/", ResourceType.Other));
        Assert.True(b.ShouldBlock("https://www-site.test/x", "https://news.test/", ResourceType.Other));
        Assert.False(b.ShouldBlock("https://localhost/x", "https://news.test/", ResourceType.Other));
        Assert.Equal(3, b.Stats.HostRules);
    }

    [Fact]
    public void Hosts_lines_inside_filter_list_are_accepted()
    {
        var b = Make("0.0.0.0 inline.test");
        Assert.True(b.ShouldBlock("https://inline.test/x", "https://news.test/", ResourceType.Other));
    }

    [Fact]
    public void Exception_beats_hosts_entry()
    {
        var b = new AdBlocker();
        b.LoadHostsList("hosts", "0.0.0.0 blocked.test");
        b.LoadFilterList("exc", "@@||blocked.test^");
        Assert.False(b.ShouldBlock("https://blocked.test/x", "https://news.test/", ResourceType.Other));
    }

    [Fact]
    public void Whitelisted_page_host_disables_blocking()
    {
        var b = Make("||ads.test^");
        b.AddWhitelistHost("news.test");
        Assert.False(b.ShouldBlock("https://ads.test/x", "https://www.news.test/", ResourceType.Script));
        Assert.False(b.ShouldBlock("https://ads.test/x", "https://news.test/", ResourceType.Script));
        Assert.True(b.ShouldBlock("https://ads.test/x", "https://other.test/", ResourceType.Script));
        b.RemoveWhitelistHost("news.test");
        Assert.True(b.ShouldBlock("https://ads.test/x", "https://news.test/", ResourceType.Script));
    }

    [Fact]
    public void Document_exception_disables_blocking_on_that_page()
    {
        var b = Make("||ads.test^\n@@||trusted.test^$document");
        Assert.False(b.ShouldBlock("https://ads.test/x", "https://trusted.test/page", ResourceType.Script));
        Assert.True(b.ShouldBlock("https://ads.test/x", "https://news.test/page", ResourceType.Script));
    }

    [Fact]
    public void Cosmetic_rules_respect_domains_negation_and_exceptions()
    {
        var b = Make(string.Join("\n",
            "##.ad-banner",
            "example.com##.sponsored",
            "~example.com##.hide-me",
            "example.com,~sub.example.com##.scoped",
            "##.sponsored",
            "example.org#@#.sponsored",
            "##+js(set-constant, x, y)",
            "##div[ad]:has(a)"));

        var www = b.CosmeticSelectors("www.example.com");
        Assert.Contains(".ad-banner", www);
        Assert.Contains(".sponsored", www);
        Assert.Contains(".scoped", www);
        Assert.Contains("div[ad]:has(a)", www);
        Assert.DoesNotContain(".hide-me", www);

        Assert.DoesNotContain(".scoped", b.CosmeticSelectors("sub.example.com"));
        Assert.DoesNotContain(".sponsored", b.CosmeticSelectors("www.example.org"));
        Assert.Contains(".sponsored", b.CosmeticSelectors("other.test"));
        Assert.Contains(".hide-me", b.CosmeticSelectors("other.test"));

        Assert.Equal(1, b.Stats.UnsupportedRules);
        Assert.Empty(b.CosmeticSelectors(""));
    }

    [Fact]
    public void Cosmetic_style_sheet_hides_selectors()
    {
        var b = Make("##.ad-banner");
        string css = b.BuildCosmeticStyleSheet("news.test");
        Assert.Contains(".ad-banner{display:none!important;}", css);
        Assert.Equal(string.Empty, b.BuildCosmeticStyleSheet(""));
    }

    [Fact]
    public void Unsafe_selector_is_rejected()
    {
        var b = Make("##.x{background:red}\n##@import\n##.safe");
        Assert.Equal(new[] { ".safe" }, b.CosmeticSelectors("news.test"));
        Assert.Equal(2, b.Stats.UnsupportedRules);
    }

    [Fact]
    public void Unsupported_options_are_dropped_and_counted()
    {
        var b = Make("||x.test^$popup\n||y.test^$redirect=noopjs\n||z.test^$csp=script-src 'none'");
        Assert.False(b.ShouldBlock("https://x.test/", "https://news.test/", ResourceType.Document));
        Assert.Equal(3, b.Stats.UnsupportedRules);
    }

    [Fact]
    public void Non_http_request_is_never_blocked()
    {
        var b = Make("||ads.test^");
        Assert.False(b.ShouldBlock("data:text/plain,ads", "https://news.test/", ResourceType.Other));
        Assert.False(b.ShouldBlock("not a url", "https://news.test/", ResourceType.Other));
    }

    [Fact]
    public void Stats_and_sources_are_reported()
    {
        var b = new AdBlocker();
        b.LoadFilterList("list-a", "||a.test^\n@@||b.test^\n##.x\n! comment\n");
        b.LoadHostsList("hosts-1", "0.0.0.0 c.test");
        var stats = b.Stats;
        Assert.Equal(1, stats.NetworkRules);
        Assert.Equal(1, stats.ExceptionRules);
        Assert.Equal(1, stats.CosmeticRules);
        Assert.Equal(1, stats.HostRules);
        Assert.Equal(3, b.RuleCount);
        Assert.Equal(new[] { "list-a", "hosts-1" }, b.Sources);
    }

    [Fact]
    public void Concurrent_checks_during_load_do_not_throw()
    {
        var b = new AdBlocker();
        b.LoadFilterList("seed", "||seed.test^");
        Parallel.For(0, 200, i =>
        {
            if (i % 20 == 0) b.LoadFilterList("extra" + i, $"||host{i}.test^");
            _ = b.ShouldBlock($"https://host{i}.test/x", "https://news.test/", ResourceType.Script);
        });
        Assert.True(b.ShouldBlock("https://seed.test/x", "https://news.test/", ResourceType.Script));
    }

    [Fact]
    public void Hundred_thousand_rules_load_quickly_and_checks_are_fast()
    {
        var sb = new StringBuilder(4 * 1024 * 1024);
        for (int i = 0; i < 80_000; i++)
            sb.Append("||ads").Append(i).Append(".example").Append(i % 97).Append(".test^\n");
        for (int i = 0; i < 15_000; i++)
            sb.Append("/track").Append(i).Append("/banner*.js$script\n");
        for (int i = 0; i < 5_000; i++)
            sb.Append("@@||safe").Append(i).Append(".test^\n");
        string list = sb.ToString();

        var b = new AdBlocker();
        var load = Stopwatch.StartNew();
        b.LoadFilterList("big", list);
        load.Stop();
        Assert.True(b.RuleCount >= 99_000, $"Rules loaded: {b.RuleCount}");
        // Loose bound (measured ~450 ms): only detects algorithmic regressions, immune to CPU load from parallel tests.
        Assert.True(load.ElapsedMilliseconds < 10000,$"Loading 100k rules took {load.ElapsedMilliseconds} ms");

        string[] urls =
        {
            "https://news.test/article/123", "https://cdn.example.org/app.js", "https://ads5.example5.test/x.js",
            "https://www.google-analytics.test/collect?v=1", "https://track77/banner1.js", "https://safe9.test/ok.js",
            "https://images.news.test/photo.jpg", "https://ads79999.example71.test/a",
        };

        for (int i = 0; i < 2000; i++)
            b.ShouldBlock(urls[i % urls.Length], "https://news.test/", ResourceType.Script);

        const int iterations = 100_000;
        var check = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
            b.ShouldBlock(urls[i % urls.Length], "https://news.test/", ResourceType.Script);
        check.Stop();

        double perCheckUs = check.Elapsed.TotalMilliseconds * 1000.0 / iterations;
        _output.WriteLine($"Loaded {b.RuleCount} rules: {load.ElapsedMilliseconds} ms; average check: {perCheckUs:F3} us ({iterations} checks)");
        // Measured ~4.5 us; the 500 us bound is still far below a linear scan of 100k rules.
        Assert.True(perCheckUs < 500, $"Average check {perCheckUs:F2} us (limit 500 us)");
        Assert.True(b.ShouldBlock("https://ads5.example5.test/x.js", "https://news.test/", ResourceType.Script));
        Assert.False(b.ShouldBlock("https://safe9.test/ok.js", "https://news.test/", ResourceType.Script));
    }
}
