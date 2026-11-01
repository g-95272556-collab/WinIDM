using System.Text;

namespace WinIDM.Browser.Tests;

public class BrowserDataStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "winidm-data-" + Guid.NewGuid().ToString("N"));

    public BrowserDataStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Db => Path.Combine(_dir, "browser.db");

    [Fact]
    public void Bookmark_folders_tree_and_move_rules()
    {
        using var store = new BrowserDataStore(Db);
        long work = store.AddFolder("Work");
        long nested = store.AddFolder("Project", work);
        long link = store.AddBookmark("Docs", "https://docs.test/a", nested);
        long root = store.AddBookmark("News", "https://news.test/");

        var tree = store.GetBookmarkTree();
        Assert.Equal(2, tree.Count);
        var folder = tree.Single(n => n.Id == work);
        Assert.True(folder.IsFolder);
        Assert.Equal("Project", folder.Children.Single().Title);
        Assert.Equal("https://docs.test/a", folder.Children.Single().Children.Single().Url);
        Assert.Equal(link, folder.Children.Single().Children.Single().Id);

        store.UpdateBookmark(root, "Latest News", "https://news.test/home");
        var updated = store.GetBookmarkTree().Single(n => n.Id == root);
        Assert.Equal("Latest News", updated.Title);
        Assert.Equal("https://news.test/home", updated.Url);

        store.MoveBookmark(root, nested);
        Assert.Single(store.GetBookmarkTree());
        store.MoveBookmark(root, null);
        Assert.Equal(2, store.GetBookmarkTree().Count);

        Assert.Throws<InvalidOperationException>(() => store.MoveBookmark(work, work));
        Assert.Throws<InvalidOperationException>(() => store.MoveBookmark(work, nested));

        store.RemoveBookmark(work);
        Assert.Single(store.GetBookmarkTree());
        Assert.Throws<ArgumentException>(() => store.AddBookmark("x", "javascript:alert(1)"));
        Assert.Throws<ArgumentException>(() => store.UpdateBookmark(root, "x", "file:///C:/x"));
        Assert.Throws<ArgumentException>(() => store.AddFolder(" "));
    }

    [Fact]
    public void Netscape_export_and_import_round_trip_preserves_titles_and_urls()
    {
        using var source = new BrowserDataStore(Db);
        long folder = source.AddFolder("Tom & Jerry <cartoon>");
        source.AddBookmark("Alpha \"quoted\"", "https://a.test/path?x=1&y=2", folder);
        source.AddBookmark("Beta", "http://b.test/");
        string html = source.ExportBookmarksHtml();
        Assert.StartsWith("<!DOCTYPE NETSCAPE-Bookmark-file-1>", html);
        Assert.Contains("&amp;", html);

        using var target = new BrowserDataStore(Path.Combine(_dir, "other.db"));
        int imported = target.ImportBookmarksHtml(html);
        Assert.Equal(2, imported); // two bookmarks; folders are not counted

        var tree = target.GetBookmarkTree();
        var f = tree.Single(n => n.IsFolder);
        Assert.Equal("Tom & Jerry <cartoon>", f.Title);
        Assert.Equal("Alpha \"quoted\"", f.Children.Single().Title);
        Assert.Equal("https://a.test/path?x=1&y=2", f.Children.Single().Url);
        Assert.Equal("Beta", tree.Single(n => !n.IsFolder).Title);
    }

    [Fact]
    public void Netscape_import_handles_chrome_style_nesting_and_skips_unsafe_links()
    {
        const string html = """
<!DOCTYPE NETSCAPE-Bookmark-file-1>
<META HTTP-EQUIV="Content-Type" CONTENT="text/html; charset=UTF-8">
<TITLE>Bookmarks</TITLE>
<H1>Bookmarks</H1>
<DL><p>
    <DT><H3 ADD_DATE="1700000000">Bar</H3>
    <DL><p>
        <DT><A HREF="https://one.test/" ADD_DATE="1">One</A>
        <DT><H3>Inside</H3>
        <DL><p>
            <DT><A HREF='https://two.test/'>Two</A>
            <DT><A HREF="javascript:alert(1)">Dangerous</A>
            <DT><A>No href</A>
        </DL><p>
    </DL><p>
    <DT><A HREF="https://three.test/">Three</A>
</DL><p>
""";
        using var store = new BrowserDataStore(Db);
        int imported = store.ImportBookmarksHtml(html);
        Assert.Equal(3, imported);

        // "Three" is at the root level because the Bar DL was closed before that link.
        var tree = store.GetBookmarkTree();
        Assert.Equal(2, tree.Count);
        var bar = tree.Single(n => n.IsFolder);
        Assert.Equal("Bar", bar.Title);
        Assert.Equal(2, bar.Children.Count);
        var inner = bar.Children.Single(c => c.IsFolder);
        Assert.Equal("Inside", inner.Title);
        Assert.Equal("Two", inner.Children.Single().Title);
        Assert.Equal("https://three.test/", tree.Single(n => !n.IsFolder).Url);

        Assert.Equal(0, store.ImportBookmarksHtml("<p>no bookmarks</p>"));
        long target = store.AddFolder("Target");
        Assert.Equal(1, store.ImportBookmarksHtml("<DL><p><DT><A HREF=\"https://x.test/\">X</A></DL>", target));
        Assert.Single(store.GetBookmarkTree().Single(n => n.Id == target).Children);
    }

    [Fact]
    public void Netscape_parser_is_depth_bounded()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 200; i++) sb.Append("<DL><p><DT><H3>f").Append(i).Append("</H3>");
        sb.Append("<DT><A HREF=\"https://deep.test/\">deep</A>");
        using var store = new BrowserDataStore(Db);
        int imported = store.ImportBookmarksHtml(sb.ToString());
        Assert.True(imported <= 1);
    }

    [Fact]
    public void History_add_search_range_delete_and_wildcard_escaping()
    {
        using var store = new BrowserDataStore(Db);
        var t0 = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.True(store.AddHistory("https://news.test/a", "News 100%", t0));
        Assert.True(store.AddHistory("https://shop_site.test/", "Shop", t0.AddDays(1)));
        Assert.True(store.AddHistory("https://news.test/b", "Other", t0.AddDays(2)));
        Assert.False(store.AddHistory("data:text/html,hello", "data"));
        Assert.False(store.AddHistory("file:///C:/x", "file"));
        Assert.False(store.AddHistory("https://" + new string('a', 2050) + ".test/", "long"));

        Assert.Equal(3, store.HistoryCount);
        var all = store.SearchHistory();
        Assert.Equal("https://news.test/b", all[0].Url);

        Assert.Single(store.SearchHistory("100%"));
        Assert.Single(store.SearchHistory("shop_site"));
        Assert.Empty(store.SearchHistory("shopXsite"));
        Assert.Equal(2, store.SearchHistory("news.test", limit: 10).Count);
        Assert.Single(store.SearchHistory("news.test", limit: 1).Take(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.SearchHistory(limit: 0));

        Assert.Equal(1, store.DeleteHistory(t0, t0.AddHours(1)));
        Assert.Equal(2, store.HistoryCount);
        Assert.Throws<ArgumentException>(() => store.DeleteHistory(t0.AddDays(1), t0));
    }

    [Fact]
    public void Saved_tabs_round_trip_in_order_and_skip_invalid()
    {
        using var store = new BrowserDataStore(Db);
        store.SaveTabs(new[]
        {
            new SavedTab("https://a.test/", "A", false),
            new SavedTab("javascript:bad()", "Bad", false),
            new SavedTab("https://b.test/", null, true),
        });
        var tabs = store.LoadTabs();
        Assert.Equal(2, tabs.Count);
        Assert.Equal("https://a.test/", tabs[0].Url);
        Assert.True(tabs[1].DesktopMode);
        Assert.Null(tabs[1].Title);

        store.SaveTabs(Array.Empty<SavedTab>());
        Assert.Empty(store.LoadTabs());
    }

    [Fact]
    public void Site_permissions_user_agent_mode_and_generic_settings()
    {
        using var store = new BrowserDataStore(Db);
        Assert.Equal(PermissionState.Ask, store.GetPermission("www.bank.test", SitePermissionKind.Camera));
        store.SetPermission("www.bank.test", SitePermissionKind.Camera, PermissionState.Allow);
        store.SetPermission("bank.test", SitePermissionKind.Microphone, PermissionState.Block);
        Assert.Equal(PermissionState.Allow, store.GetPermission("WWW.BANK.TEST", SitePermissionKind.Camera));
        Assert.Equal(PermissionState.Block, store.GetPermission("bank.test", SitePermissionKind.Microphone));
        Assert.Equal(PermissionState.Ask, store.GetPermission("bank.test", SitePermissionKind.Location));
        store.SetPermission("bank.test", SitePermissionKind.Microphone, PermissionState.Ask);
        Assert.Equal(PermissionState.Ask, store.GetPermission("bank.test", SitePermissionKind.Microphone));

        Assert.Equal(UserAgentMode.Default, store.GetUserAgentMode("site.test"));
        store.SetUserAgentMode("site.test", UserAgentMode.Desktop);
        Assert.Equal(UserAgentMode.Desktop, store.GetUserAgentMode("site.test"));
        store.SetUserAgentMode("site.test", UserAgentMode.Default);
        Assert.Equal(UserAgentMode.Default, store.GetUserAgentMode("site.test"));

        store.SetSiteSetting("site.test", "ignore_ssl", "1");
        Assert.Equal("1", store.GetSiteSetting("site.test", "ignore_ssl"));
        store.SetSiteSetting("site.test", "ignore_ssl", null);
        Assert.Null(store.GetSiteSetting("site.test", "ignore_ssl"));
        Assert.Throws<ArgumentException>(() => store.SetSiteSetting("site.test", " ", "x"));
    }

    [Fact]
    public void Clear_browsing_data_by_category_and_persistence_across_instances()
    {
        using (var store = new BrowserDataStore(Db))
        {
            store.AddBookmark("Keep", "https://keep.test/");
            store.AddHistory("https://h.test/", "H");
            store.SaveTabs(new[] { new SavedTab("https://t.test/", "T", false) });
            store.SetSiteSetting("s.test", "k", "v");
        }

        using (var store = new BrowserDataStore(Db))
        {
            Assert.Single(store.GetBookmarkTree());
            Assert.Equal(1, store.HistoryCount);
            store.ClearBrowsingData(BrowsingDataKinds.History | BrowsingDataKinds.SavedTabs);
            Assert.Equal(0, store.HistoryCount);
            Assert.Empty(store.LoadTabs());
            Assert.Equal("v", store.GetSiteSetting("s.test", "k"));
            Assert.Single(store.GetBookmarkTree());

            store.ClearBrowsingData(BrowsingDataKinds.All);
            Assert.Empty(store.GetBookmarkTree());
            Assert.Null(store.GetSiteSetting("s.test", "k"));
            store.ClearBrowsingData(BrowsingDataKinds.None);
        }
    }

    [Fact]
    public void Search_engines_list_templates_and_custom_validation()
    {
        Assert.Equal(14, SearchEngines.All.Count);
        Assert.Equal("google", SearchEngines.DefaultId);
        Assert.Equal("google", SearchEngines.Get("nope").Id);
        Assert.Equal("Bing", SearchEngines.Get("bing").Name);

        Assert.Equal("https://www.google.com/search?q=hello%20world%26x", SearchEngines.BuildSearchUrl("google", "hello world&x"));
        Assert.Equal("https://duckduckgo.com/?q=a%2Bb", SearchEngines.BuildSearchUrl("duckduckgo", "a+b"));
        Assert.Equal("https://example.test/find?term=word", SearchEngines.BuildSearchUrl("custom", "word", "https://example.test/find?term={q}"));
        Assert.Throws<ArgumentException>(() => SearchEngines.BuildSearchUrl("custom", "x", "https://example.test/find"));
        Assert.Throws<ArgumentException>(() => SearchEngines.BuildSearchUrl("custom", "x", "ftp://example.test/?q={q}"));
        Assert.Throws<ArgumentException>(() => SearchEngines.BuildSearchUrl("custom", "x"));
        Assert.False(SearchEngines.IsValidTemplate(null));
        Assert.True(SearchEngines.IsValidTemplate("https://x.test/?q={q}"));

        foreach (var engine in SearchEngines.All.Where(e => e.Id != SearchEngines.CustomId))
            Assert.Contains("{q}", engine.Template);
    }

    [Fact]
    public void User_agent_profiles_and_consistent_client_hints()
    {
        string system = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/140.0.0.0 Safari/537.36";
        Assert.Equal(system, UserAgentProfile.Resolve(UserAgentMode.Default, system));
        Assert.Equal(UserAgentProfile.DesktopUserAgent("150.0.1.0"), UserAgentProfile.Resolve(UserAgentMode.Desktop, system, "150.0.1.0"));
        Assert.Contains("Android", UserAgentProfile.Resolve(UserAgentMode.CustomMobile, system));
        Assert.Equal("ua-custom/1.0", UserAgentProfile.Resolve(UserAgentMode.Custom, system, customText: "ua-custom/1.0"));
        Assert.Equal(system, UserAgentProfile.Resolve(UserAgentMode.Custom, system, customText: "bad\nvalue"));
        Assert.Equal(system, UserAgentProfile.Resolve(UserAgentMode.Custom, system, customText: null));
        Assert.Equal(UserAgentProfile.DesktopUserAgent(), UserAgentProfile.Resolve(UserAgentMode.Default, ""));
        Assert.False(UserAgentProfile.IsValidCustomUserAgent(new string('a', 600)));

        Assert.True(UserAgentProfile.IsDesktopUserAgent(UserAgentProfile.DesktopUserAgent()));
        Assert.True(UserAgentProfile.IsDesktopUserAgent("Mozilla/5.0 (X11; Linux x86_64)"));
        Assert.False(UserAgentProfile.IsDesktopUserAgent(UserAgentProfile.MobileUserAgent()));
        Assert.False(UserAgentProfile.IsDesktopUserAgent(""));

        var desktop = UserAgentProfile.ClientHintsFor(UserAgentProfile.DesktopUserAgent("149.0.7.1"));
        Assert.Equal("\"Windows\"", desktop.SecChUaPlatform);
        Assert.Equal("?0", desktop.SecChUaMobile);
        Assert.Contains("\"Google Chrome\";v=\"149\"", desktop.SecChUa);

        var mobile = UserAgentProfile.ClientHintsFor(UserAgentProfile.MobileUserAgent("149.0.0.0"));
        Assert.Equal("\"Android\"", mobile.SecChUaPlatform);
        Assert.Equal("?1", mobile.SecChUaMobile);

        var mac = UserAgentProfile.ClientHintsFor("Mozilla/5.0 (Macintosh; Intel Mac OS X 14_0)");
        Assert.Equal("\"macOS\"", mac.SecChUaPlatform);
        Assert.Equal("\"Not.A/Brand\";v=\"99\"", mac.SecChUa);
        var linux = UserAgentProfile.ClientHintsFor("Mozilla/5.0 (X11; Linux x86_64)");
        Assert.Equal("\"Linux\"", linux.SecChUaPlatform);
        Assert.Equal("\"Unknown\"", UserAgentProfile.ClientHintsFor("Custom").SecChUaPlatform);
    }

    [Fact]
    public void Address_bar_distinguishes_urls_searches_and_schemes()
    {
        const string tpl = "https://www.google.com/search?q={q}";

        Assert.Equal(AddressKind.Empty, AddressBarResolver.Resolve("   ", tpl).Kind);
        Assert.Equal(new AddressResolution(AddressKind.Url, "https://example.com/"), AddressBarResolver.Resolve("example.com", tpl));
        Assert.Equal("https://example.com/path?q=1", AddressBarResolver.Resolve("example.com/path?q=1", tpl).Target);
        Assert.Equal("https://localhost:8080/", AddressBarResolver.Resolve("localhost:8080", tpl).Target);
        Assert.Equal("https://192.168.0.1/", AddressBarResolver.Resolve("192.168.0.1", tpl).Target);
        Assert.Equal("https://[::1]/", AddressBarResolver.Resolve("[::1]", tpl).Target);
        Assert.Equal("http://plain.test/x", AddressBarResolver.Resolve("http://plain.test/x", tpl).Target);
        Assert.Equal("about:blank", AddressBarResolver.Resolve("about:blank", tpl).Target);

        var search = AddressBarResolver.Resolve("hello world", tpl);
        Assert.Equal(AddressKind.Search, search.Kind);
        Assert.Equal("https://www.google.com/search?q=hello%20world", search.Target);
        Assert.Equal(AddressKind.Search, AddressBarResolver.Resolve("1234", tpl).Kind);
        Assert.Equal(AddressKind.Search, AddressBarResolver.Resolve("1.5", tpl).Kind);
        Assert.Equal(AddressKind.Search, AddressBarResolver.Resolve("single", tpl).Kind);
        Assert.Equal(AddressKind.Search, AddressBarResolver.Resolve("https://", tpl).Kind);

        Assert.Equal(AddressKind.Blocked, AddressBarResolver.Resolve("javascript:alert(1)", tpl).Kind);
        Assert.Equal(AddressKind.Blocked, AddressBarResolver.Resolve("data:text/html,x", tpl).Kind);
        Assert.Equal(AddressKind.Blocked, AddressBarResolver.Resolve("file:///C:/secret.txt", tpl).Kind);
        Assert.Equal(AddressKind.Blocked, AddressBarResolver.Resolve("bad\u0007input", tpl).Kind);

        var mail = AddressBarResolver.Resolve("mailto:someone@example.com", tpl);
        Assert.Equal(AddressKind.ExternalScheme, mail.Kind);
        Assert.Equal("mailto:someone@example.com", mail.Target);

        Assert.Throws<ArgumentException>(() => AddressBarResolver.Resolve("x", "https://no-placeholder.test/"));
    }
}
