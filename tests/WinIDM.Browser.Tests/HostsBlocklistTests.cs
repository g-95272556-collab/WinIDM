namespace WinIDM.Browser.Tests;

public class HostsBlocklistTests
{
    [Fact]
    public void Sources_accept_hosts_and_plain_lines_and_ignore_local_entries()
    {
        var h = new HostsBlocklist();
        int count = h.AddOrReplaceSource("mixed", "# comment\n0.0.0.0 tracker.example\n127.0.0.1 localhost\nads.example.net\n0.0.0.0 www.www-site.test\n");

        Assert.Equal(3, count);
        Assert.True(h.TryBlock("https://sub.tracker.example/x"));
        Assert.True(h.TryBlock("https://ads.example.net/x"));
        Assert.True(h.TryBlock("https://www-site.test/x"));
        Assert.False(h.TryBlock("https://localhost/x"));
        Assert.False(h.TryBlock("https://news.test/"));
    }

    [Fact]
    public void Whitelist_wins_over_blocking_for_host_and_subdomains_and_page_origin()
    {
        var h = new HostsBlocklist();
        h.AddOrReplaceSource("a", "tracker.example\nads.example.net\n");

        h.AddWhitelist("tracker.example");
        Assert.False(h.TryBlock("https://tracker.example/x"));
        Assert.False(h.TryBlock("https://sub.tracker.example/x"));
        Assert.True(h.TryBlock("https://ads.example.net/x"));

        Assert.False(h.TryBlock("https://ads.example.net/x", "https://tracker.example/page"));
        Assert.True(h.IsWhitelisted("cdn.tracker.example"));
        Assert.Equal(new[] { "tracker.example" }, h.Whitelist());

        h.RemoveWhitelist("tracker.example");
        Assert.True(h.TryBlock("https://tracker.example/x"));
    }

    [Fact]
    public void Disabled_sources_do_not_block_and_can_be_toggled_or_removed()
    {
        var h = new HostsBlocklist();
        h.AddOrReplaceSource("b", "b.test\n", enabled: false);
        Assert.False(h.TryBlock("https://b.test/x"));
        Assert.Equal(0, h.ActiveDomainCount);

        h.SetEnabled("b", true);
        Assert.True(h.TryBlock("https://b.test/x"));
        Assert.Equal(1, h.ActiveDomainCount);

        h.RemoveSource("b");
        Assert.False(h.TryBlock("https://b.test/x"));
        Assert.Throws<KeyNotFoundException>(() => h.SetEnabled("missing", true));
    }

    [Fact]
    public void Replacing_a_source_swaps_its_domains()
    {
        var h = new HostsBlocklist();
        h.AddOrReplaceSource("a", "old.test\n");
        Assert.True(h.TryBlock("https://old.test/"));

        h.AddOrReplaceSource("a", "new.test\n");
        Assert.False(h.TryBlock("https://old.test/"));
        Assert.True(h.TryBlock("https://new.test/"));
        var info = Assert.Single(h.Sources());
        Assert.Equal("a", info.Name);
        Assert.Equal(1, info.DomainCount);
    }

    [Fact]
    public void Statistics_count_blocks_per_matched_domain_and_reset()
    {
        var h = new HostsBlocklist();
        h.AddOrReplaceSource("s", "tracker.example\nads.example.net\n");

        Assert.True(h.TryBlock("https://a.tracker.example/1"));
        Assert.True(h.TryBlock("https://tracker.example/2"));
        Assert.True(h.TryBlock("https://a.tracker.example/3"));
        Assert.True(h.TryBlock("https://ads.example.net/4"));
        Assert.False(h.TryBlock("not a url"));

        var stats = h.GetStats(top: 5);
        Assert.Equal(4, stats.TotalBlocked);
        Assert.NotNull(stats.SinceUtc);
        Assert.Equal(new[] { ("tracker.example", 3L), ("ads.example.net", 1L) },
            stats.TopDomains.Select(t => (t.Domain, t.Count)).ToArray());

        Assert.Single(h.GetStats(top: 1).TopDomains);

        h.ResetStats();
        var cleared = h.GetStats();
        Assert.Equal(0, cleared.TotalBlocked);
        Assert.Null(cleared.SinceUtc);
        Assert.Empty(cleared.TopDomains);
    }

    [Fact]
    public void Whitelisted_requests_are_not_counted()
    {
        var h = new HostsBlocklist();
        h.AddOrReplaceSource("s", "tracker.example\n");
        h.AddWhitelist("tracker.example");
        Assert.False(h.TryBlock("https://tracker.example/x"));
        Assert.Equal(0, h.GetStats().TotalBlocked);
    }
}
