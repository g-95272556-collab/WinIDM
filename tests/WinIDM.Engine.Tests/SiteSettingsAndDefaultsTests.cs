using Microsoft.Data.Sqlite;
using WinIDM.Core.Models;
using WinIDM.Engine;
using WinIDM.Engine.Tests.Support;

namespace WinIDM.Engine.Tests;

/// <summary>
/// site_settings API (read/write per site), its effect on the engine (UA, cookies, default folder),
/// the per-domain part limit at engine level, and the effective defaults (3 concurrent, 8 parts).
/// </summary>
public sealed class SiteSettingsTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task SiteSettings_RoundTrip_AndCookiesAreProtectedOnDisk()
    {
        using var repo = new SqliteDownloadRepository(Path.Combine(_temp.Path, "site.db"));
        await repo.SetSiteSettingsAsync(new SiteSettings(
            "example.test",
            UserAgent: "UA-Site/1.0",
            Cookies: "session=secret-cookie-9911; choice=1",
            SaveDirectory: Path.Combine(_temp.Path, "site"),
            UseForSubdomain: true));

        var read = await repo.GetSiteSettingsAsync("example.test");
        Assert.NotNull(read);
        Assert.Equal("UA-Site/1.0", read!.UserAgent);
        Assert.Equal("session=secret-cookie-9911; choice=1", read.Cookies);
        Assert.Equal(Path.Combine(_temp.Path, "site"), read.SaveDirectory);
        Assert.True(read.UseForSubdomain);

        // Cookies must not be visible as plain text in the database.
        using var raw = new SqliteConnection($"Data Source={Path.Combine(_temp.Path, "site.db")}");
        raw.Open();
        using var cmd = raw.CreateCommand();
        cmd.CommandText = "SELECT cookies_protected FROM site_settings WHERE domain = 'example.test';";
        var stored = (string)cmd.ExecuteScalar()!;
        Assert.StartsWith("dpapi1:", stored);
        Assert.DoesNotContain("secret-cookie-9911", stored);
    }

    [Fact]
    public async Task SiteSettings_LongestMatch_AndSubdomainFlag()
    {
        using var repo = new SqliteDownloadRepository(Path.Combine(_temp.Path, "site.db"));
        await repo.SetSiteSettingsAsync(new SiteSettings("example.test", UserAgent: "UA-A", UseForSubdomain: true));
        await repo.SetSiteSettingsAsync(new SiteSettings("video.example.test", UserAgent: "UA-B", UseForSubdomain: false));

        Assert.Equal("UA-B", (await repo.GetSiteSettingsAsync("video.example.test"))!.UserAgent);
        Assert.Equal("UA-A", (await repo.GetSiteSettingsAsync("x.video.example.test"))!.UserAgent);
        Assert.Equal("UA-A", (await repo.GetSiteSettingsAsync("example.test"))!.UserAgent);
        Assert.Null(await repo.GetSiteSettingsAsync("other.test"));
    }

    [Fact]
    public async Task SiteSettings_Upsert_List_AndDelete()
    {
        using var repo = new SqliteDownloadRepository(Path.Combine(_temp.Path, "site.db"));
        await repo.SetSiteSettingsAsync(new SiteSettings("Example.Test", UserAgent: "one"));
        await repo.SetSiteSettingsAsync(new SiteSettings("example.test", UserAgent: "two"));

        var all = await repo.ListSiteSettingsAsync();
        Assert.Single(all);
        Assert.Equal("two", all[0].UserAgent);

        await repo.DeleteSiteSettingsAsync("example.test");
        Assert.Empty(await repo.ListSiteSettingsAsync());
    }

    [Fact]
    public async Task Engine_AppliesSiteUserAgentCookieAndFolder_WhenRequestOmitsThem()
    {
        await using var server = new LocalHttpServer();
        var file = new VirtualFile(TestData.Random(64 * 1024, seed: 91));
        server.Handler = file.Serve;

        var laman = Path.Combine(_temp.Path, "site");
        await using var h = new EngineHarness();
        await h.Repository.SetSiteSettingsAsync(new SiteSettings(
            "127.0.0.1", UserAgent: "UA-Site-Test", Cookies: "session=test", SaveDirectory: laman));

        var id = await h.Engine.AddAsync(new DownloadRequest { Url = server.BaseUrl + "/file.bin" });
        var item = await h.WaitForStateAsync(id, DownloadState.Completed);

        Assert.Equal(laman, item.SaveDirectory);
        Assert.Equal("UA-Site-Test", item.UserAgent);
        Assert.Equal("session=test", item.CookieHeader);
        Assert.Contains(server.Requests, r => r.Get("User-Agent") == "UA-Site-Test" && r.Get("Cookie") == "session=test");
        Assert.True(File.Exists(Path.Combine(laman, "file.bin")));
    }

    [Fact]
    public async Task Engine_RequestValues_OverrideSiteSettings()
    {
        await using var server = new LocalHttpServer();
        var file = new VirtualFile(TestData.Random(16 * 1024, seed: 92));
        server.Handler = file.Serve;

        await using var h = new EngineHarness();
        await h.Repository.SetSiteSettingsAsync(new SiteSettings("127.0.0.1", UserAgent: "UA-Site", Cookies: "a=1"));

        var id = await h.Engine.AddAsync(new DownloadRequest
        {
            Url = server.BaseUrl + "/file.bin",
            UserAgent = "UA-Request",
            CookieHeader = "b=2",
        });
        await h.WaitForStateAsync(id, DownloadState.Completed);

        Assert.Contains(server.Requests, r => r.Get("User-Agent") == "UA-Request" && r.Get("Cookie") == "b=2");
    }

    [Fact]
    public async Task DomainPartLimit_CapsSegmentRequestsAtEngineLevel()
    {
        await using var server = new LocalHttpServer();
        var file = new VirtualFile(TestData.Random(6 * 1024 * 1024, seed: 93));
        server.Handler = file.Serve;

        await using var h = new EngineHarness();
        await h.Repository.SetDomainPartLimitAsync("127.0.0.1", 2, useForSubdomain: true);

        var id = await h.Engine.AddAsync(new DownloadRequest { Url = server.BaseUrl + "/file.bin" });
        var item = await h.WaitForStateAsync(id, DownloadState.Completed);

        var segmentRequests = server.Requests.Count(r => r.Range is { } range && range.To != 0);
        Assert.Equal(2, segmentRequests);
        Assert.Equal(TestData.Sha256Hex(file.Content), TestData.Sha256Hex(Path.Combine(item.SaveDirectory, item.FileName)));
    }
}

public sealed class EngineDefaultsTests
{
    [Fact]
    public void EffectiveDefaults_AreThreeConcurrentDownloadsAndEightParts()
    {
        // The raw values 2 and 7 are the 03a defaults; the engine adds 1, so the effective values are 3 and 8 (01 sections 3.1 and 5.1).
        Assert.Equal(2, SettingDefaults.DownloadCount);
        Assert.Equal(7, SettingDefaults.DownloadPart);
        Assert.Equal(3, SettingDefaults.EffectiveDownloadCount);
        Assert.Equal(8, SettingDefaults.EffectivePartCount);
    }

    [Fact]
    public void EffectivePartCount_YieldsEightSegments_ForLargeFile()
    {
        var segments = SegmentPlanner.CountSegments(100_000_000, SettingDefaults.EffectivePartCount, domainPartLimit: 0, advanced: false);
        Assert.Equal(8, segments);
    }

    [Fact]
    public async Task DefaultSettings_RunThreeDownloadsConcurrently()
    {
        await using var server = new LocalHttpServer();
        var file = new VirtualFile(TestData.Random(64 * 1024, seed: 94));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = 0;

        // Hold the requests until released: the number that arrive is the number of concurrent active downloads.
        server.Handler = req =>
        {
            if (req.Target == "/block.bin" && req.Range is { To: 0 })
            {
                Interlocked.Increment(ref arrived);
                gate.Task.Wait(TimeSpan.FromSeconds(30));
            }

            return file.Serve(req);
        };

        await using var h = new EngineHarness();
        try
        {
            var ids = new List<long>();
            for (var i = 0; i < 4; i++)
            {
                ids.Add(await h.Engine.AddAsync(new DownloadRequest { Url = server.BaseUrl + "/block.bin" }));
            }

            await EngineHarness.WaitUntilAsync(() => Volatile.Read(ref arrived) >= SettingDefaults.EffectiveDownloadCount,
                20000, "three concurrent downloads");
            await Task.Delay(400);

            Assert.Equal(SettingDefaults.EffectiveDownloadCount, Volatile.Read(ref arrived));

            gate.TrySetResult();
            foreach (var id in ids) await h.WaitForStateAsync(id, DownloadState.Completed);
        }
        finally
        {
            gate.TrySetResult();
        }
    }
}
