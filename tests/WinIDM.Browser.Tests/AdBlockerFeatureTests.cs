using System.Diagnostics;
using System.Text;
using WinIDM.Core.Contracts;

namespace WinIDM.Browser.Tests;

public class AdBlockerFeatureTests
{
    private static AdBlocker Make(string list)
    {
        var blocker = new AdBlocker();
        blocker.LoadFilterList("test", list);
        return blocker;
    }

    [Fact]
    public void Subdocument_option_targets_frames_not_top_level_or_scripts()
    {
        var b = Make("||ads.test^$subdocument");
        Assert.True(b.ShouldBlock("https://ads.test/frame.html", "https://news.test/", ResourceType.Subdocument));
        Assert.False(b.ShouldBlock("https://ads.test/x.js", "https://news.test/", ResourceType.Script));
        Assert.False(b.ShouldBlock("https://ads.test/", "https://news.test/", ResourceType.Document));
    }

    [Fact]
    public void Frame_alias_and_negated_subdocument_are_mapped_correctly()
    {
        var alias = Make("||ads.test^$frame");
        Assert.True(alias.ShouldBlock("https://ads.test/f", "https://news.test/", ResourceType.Subdocument));

        var negated = Make("||ads.test^$~subdocument");
        Assert.False(negated.ShouldBlock("https://ads.test/f", "https://news.test/", ResourceType.Subdocument));
        Assert.True(negated.ShouldBlock("https://ads.test/x.js", "https://news.test/", ResourceType.Script));
    }

    [Fact]
    public void Default_rules_block_subdocuments_but_document_option_stays_separate()
    {
        var defaults = Make("||ads.test^");
        Assert.True(defaults.ShouldBlock("https://ads.test/f", "https://news.test/", ResourceType.Subdocument));

        var docRule = Make("||news.test^$document");
        Assert.True(docRule.ShouldBlock("https://news.test/", "https://news.test/", ResourceType.Document));
        Assert.False(docRule.ShouldBlock("https://news.test/f", "https://news.test/", ResourceType.Subdocument));
    }

    [Fact]
    public void Important_block_overrides_ordinary_exception()
    {
        var b = Make("||ads.test^$important\n@@||ads.test^");
        Assert.True(b.ShouldBlock("https://ads.test/a.js", "https://news.test/", ResourceType.Script));
    }

    [Fact]
    public void Ordinary_block_still_yields_to_exception_and_important_exception_cancels_important_block()
    {
        var ordinary = Make("||ads.test^\n@@||ads.test^");
        Assert.False(ordinary.ShouldBlock("https://ads.test/a.js", "https://news.test/", ResourceType.Script));

        var cancelled = Make("||ads.test^$important\n@@||ads.test^$important");
        Assert.False(cancelled.ShouldBlock("https://ads.test/a.js", "https://news.test/", ResourceType.Script));
    }

    [Fact]
    public void Denyallow_excludes_listed_hosts_from_block_rule()
    {
        var b = Make("||ads.test^$denyallow=safe.ads.test");
        Assert.True(b.ShouldBlock("https://ads.test/x", "https://news.test/", ResourceType.Script));
        Assert.True(b.ShouldBlock("https://tracker.ads.test/x", "https://news.test/", ResourceType.Script));
        Assert.False(b.ShouldBlock("https://safe.ads.test/x", "https://news.test/", ResourceType.Script));
        Assert.False(b.ShouldBlock("https://deep.safe.ads.test/x", "https://news.test/", ResourceType.Script));
    }

    [Fact]
    public void Denyallow_with_wildcard_is_unsupported()
    {
        // Unsupported rules are dropped (counted in the stats), like other unknown options.
        var b = Make("||ads.test^$denyallow=*.safe.test");
        Assert.Equal(1, b.Stats.UnsupportedRules);
        Assert.False(b.ShouldBlock("https://ads.test/x", "https://news.test/", ResourceType.Script));
    }

    [Fact]
    public void Element_hiding_exceptions_remove_generic_and_domain_selectors()
    {
        var b = new AdBlocker();
        b.LoadFilterList("cosmetic", "##.banner\nexample.test##.promo\nexample.test#@#.banner\nother.test#@#.promo");

        var example = b.CosmeticSelectors("example.test");
        Assert.DoesNotContain(".banner", example);
        Assert.Contains(".promo", example);

        var other = b.CosmeticSelectors("other.test");
        Assert.Contains(".banner", other);
        Assert.DoesNotContain(".promo", other);

        // A spoofed subdomain does not inherit the example.test domain selector.
        Assert.DoesNotContain(".promo", b.CosmeticSelectors("example.test.evil.test"));
    }

    [Fact]
    public void Hundred_thousand_rules_load_and_match_within_budget()
    {
        var sb = new StringBuilder(4 * 1024 * 1024);
        for (int i = 0; i < 100_000; i++)
        {
            sb.Append("||host").Append(i).Append(".perf.test^\n");
        }
        sb.Append("/banner-token-x/*.gif\n");

        var b = new AdBlocker();
        var sw = Stopwatch.StartNew();
        b.LoadFilterList("perf", sb.ToString());
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 10000,$"Loading 100k rules took {sw.ElapsedMilliseconds} ms.");
        Assert.Equal(100_001, b.Stats.NetworkRules);

        var match = Stopwatch.StartNew();
        int blocked = 0;
        for (int i = 0; i < 10_000; i++)
        {
            if (b.ShouldBlock($"https://host{i * 7}.perf.test/x.js", "https://news.test/", ResourceType.Script)) blocked++;
            if (b.ShouldBlock($"https://clean{i}.example/x.js", "https://news.test/", ResourceType.Script)) blocked++;
        }
        match.Stop();
        Assert.True(match.ElapsedMilliseconds < 10000,$"10k checks took {match.ElapsedMilliseconds} ms.");
        Assert.True(blocked > 0);
    }
}
