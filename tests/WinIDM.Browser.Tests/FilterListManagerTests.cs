using System.Text;
using WinIDM.Core.Contracts;

namespace WinIDM.Browser.Tests;

public class FilterListManagerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "winidm-fl-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private FilterListManager Open() => new(_dir) { RequireHttps = false };

    private static HttpReply ListReply(string etag, string body) =>
        new(200, Encoding.UTF8.GetBytes(body), "text/plain", new Dictionary<string, string> { ["ETag"] = etag });

    [Fact]
    public void Catalog_is_data_only_with_unique_https_entries()
    {
        var catalog = FilterListManager.Catalog;
        Assert.NotEmpty(catalog);
        Assert.Equal(catalog.Count, catalog.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count());
        foreach (var entry in catalog)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Name));
            Assert.True(HostUtil.TryGetHttpUri(entry.Url, out var uri), entry.Id);
            Assert.Equal(Uri.UriSchemeHttps, uri.Scheme);
            Assert.Contains(entry.Format, new[] { "adblock", "hosts" });
        }
    }

    [Fact]
    public void Catalog_entries_start_disabled_and_nothing_is_downloaded_on_open()
    {
        using var m = Open();
        var easylist = m.Get("easylist");
        Assert.NotNull(easylist);
        Assert.False(easylist!.Enabled);
        Assert.Null(easylist.ETag);
        Assert.True(m.Lists().Count >= FilterListManager.Catalog.Count);
    }

    [Fact]
    public async Task Download_sends_if_none_match_and_304_keeps_cache()
    {
        using var server = new TestHttpServer(new Dictionary<string, Func<HttpCall, HttpReply>>
        {
            ["/list.txt"] = call => call.Headers.TryGetValue("If-None-Match", out var inm) && inm == "\"v1\""
                ? new HttpReply(304, Array.Empty<byte>(), "text/plain", new Dictionary<string, string> { ["ETag"] = "\"v1\"" })
                : ListReply("\"v1\"", "||ads.test^\n"),
        });

        using var m = Open();
        string id = m.AddCustomSource("Custom", server.BaseUrl + "/list.txt");
        m.SetEnabled(id, true);

        var first = await m.UpdateAsync(id);
        Assert.Equal(FilterListUpdateStatus.Updated, first.Status);
        Assert.Equal("\"v1\"", m.Get(id)!.ETag);

        var second = await m.UpdateAsync(id);
        Assert.Equal(FilterListUpdateStatus.NotModified, second.Status);
        Assert.Equal("\"v1\"", server.Calls.Last().Headers["If-None-Match"]);

        var blocker = new AdBlocker();
        Assert.Equal(1, m.ApplyTo(blocker));
        Assert.True(blocker.ShouldBlock("https://ads.test/x", "https://news.test/", ResourceType.Script));
    }

    [Fact]
    public async Task State_and_cache_survive_restart()
    {
        using var server = new TestHttpServer(new Dictionary<string, Func<HttpCall, HttpReply>>
        {
            ["/list.txt"] = _ => ListReply("\"v9\"", "||kept.test^\n"),
        });

        string id;
        using (var m = Open())
        {
            id = m.AddCustomSource("Persist", server.BaseUrl + "/list.txt");
            m.SetEnabled(id, true);
            Assert.Equal(FilterListUpdateStatus.Updated, (await m.UpdateAsync(id)).Status);
        }

        using var reopened = Open();
        var state = reopened.Get(id);
        Assert.NotNull(state);
        Assert.True(state!.Enabled);
        Assert.True(state.IsCustom);
        Assert.Equal("\"v9\"", state.ETag);
        Assert.NotNull(state.LastUpdatedUtc);

        var blocker = new AdBlocker();
        reopened.ApplyTo(blocker);
        Assert.True(blocker.ShouldBlock("https://kept.test/x", "https://news.test/", ResourceType.Script));
    }

    [Fact]
    public async Task Https_requirement_refuses_plain_http_without_network()
    {
        using var m = Open();
        string id = m.AddCustomSource("Http", "http://127.0.0.1:1/list.txt");
        m.SetEnabled(id, true);
        m.RequireHttps = true;

        var result = await m.UpdateAsync(id);
        Assert.Equal(FilterListUpdateStatus.Failed, result.Status);
        Assert.Contains("HTTPS", result.Error);
        Assert.NotNull(m.Get(id)!.LastError);
    }

    [Fact]
    public async Task Failed_update_keeps_previous_cache()
    {
        bool fail = false;
        using var server = new TestHttpServer(new Dictionary<string, Func<HttpCall, HttpReply>>
        {
            ["/list.txt"] = _ => fail ? new HttpReply(500, Encoding.UTF8.GetBytes("boom"), "text/plain") : ListReply("\"a\"", "||old.test^\n"),
        });

        using var m = Open();
        string id = m.AddCustomSource("Failed", server.BaseUrl + "/list.txt");
        m.SetEnabled(id, true);
        Assert.Equal(FilterListUpdateStatus.Updated, (await m.UpdateAsync(id)).Status);

        fail = true;
        var result = await m.UpdateAsync(id);
        Assert.Equal(FilterListUpdateStatus.Failed, result.Status);
        Assert.Contains("500", result.Error);

        var blocker = new AdBlocker();
        Assert.Equal(1, m.ApplyTo(blocker));
        Assert.True(blocker.ShouldBlock("https://old.test/x", "https://news.test/", ResourceType.Script));
    }

    [Fact]
    public async Task Oversized_list_is_rejected()
    {
        using var server = new TestHttpServer(new Dictionary<string, Func<HttpCall, HttpReply>>
        {
            ["/big.txt"] = _ => HttpReply.Bytes(new byte[FilterListManager.MaxListBytes + 1], "text/plain"),
        });

        using var m = Open();
        string id = m.AddCustomSource("Big", server.BaseUrl + "/big.txt");
        m.SetEnabled(id, true);
        var result = await m.UpdateAsync(id);
        Assert.Equal(FilterListUpdateStatus.Failed, result.Status);
        Assert.Equal(0, m.Get(id)!.CachedBytes);
    }

    [Fact]
    public async Task Scheduled_due_check_respects_interval_and_enabled_flag()
    {
        using var server = new TestHttpServer(new Dictionary<string, Func<HttpCall, HttpReply>>
        {
            ["/list.txt"] = call => call.Headers.ContainsKey("If-None-Match")
                ? new HttpReply(304, Array.Empty<byte>(), "text/plain", new Dictionary<string, string> { ["ETag"] = "\"s\"" })
                : ListReply("\"s\"", "||sched.test^\n"),
        });

        using var m = Open();
        string id = m.AddCustomSource("Schedule", server.BaseUrl + "/list.txt");

        var disabled = await m.UpdateDueAsync(DateTimeOffset.UtcNow, TimeSpan.FromHours(1));
        Assert.Empty(disabled);
        Assert.Equal(0, server.CallCount("/list.txt"));

        m.SetEnabled(id, true);
        var first = await m.UpdateDueAsync(DateTimeOffset.UtcNow, TimeSpan.FromHours(1));
        Assert.Single(first);
        Assert.Equal(FilterListUpdateStatus.Updated, first[0].Status);

        var immediate = await m.UpdateDueAsync(DateTimeOffset.UtcNow, TimeSpan.FromHours(1));
        Assert.Empty(immediate);

        var later = await m.UpdateDueAsync(DateTimeOffset.UtcNow.AddHours(2), TimeSpan.FromHours(1));
        Assert.Single(later);
        Assert.Equal(FilterListUpdateStatus.NotModified, later[0].Status);
        Assert.Equal(2, server.CallCount("/list.txt"));
    }

    [Fact]
    public async Task Schedule_loop_stops_when_cancelled()
    {
        using var m = Open();
        using var cts = new CancellationTokenSource();
        var task = m.RunScheduleAsync(TimeSpan.FromHours(1), TimeSpan.FromMilliseconds(20), cts.Token);
        cts.CancelAfter(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public void Custom_source_validation_and_removal_rules()
    {
        using var m = Open();
        Assert.Throws<ArgumentException>(() => m.AddCustomSource("Ftp", "ftp://127.0.0.1/list.txt"));

        m.RequireHttps = true;
        Assert.Throws<ArgumentException>(() => m.AddCustomSource("Http", "http://127.0.0.1/list.txt"));
        m.RequireHttps = false;

        string id = m.AddCustomSource("Remove", "http://127.0.0.1:9/remove.txt");
        Assert.Equal(id, m.AddCustomSource("Remove", "http://127.0.0.1:9/remove.txt"));
        Assert.Throws<InvalidOperationException>(() => m.RemoveCustomSource("easylist"));

        m.RemoveCustomSource(id);
        Assert.Null(m.Get(id));
    }

    [Fact]
    public void Apply_skips_disabled_lists_without_cache()
    {
        Directory.CreateDirectory(_dir);
        using var m = Open();
        string id = m.AddCustomSource("Without", "http://127.0.0.1:9/without.txt");
        m.SetEnabled(id, true);
        var blocker = new AdBlocker();
        Assert.Equal(0, m.ApplyTo(blocker));
        Assert.Empty(blocker.Sources.Where(s => s == "Without"));
    }
}
