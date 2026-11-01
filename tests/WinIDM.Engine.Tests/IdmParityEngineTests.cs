using WinIDM.Core.Models;
using WinIDM.Engine;
using WinIDM.Engine.Tests.Support;

namespace WinIDM.Engine.Tests;

/// <summary>
/// IDM+ download behaviours that are settings-driven: the advanced method's queued parts, auto restart of
/// non-resumable links, the battery limit, and "remove old links after 0 days" meaning off.
/// </summary>
public sealed class IdmParityEngineTests
{
    private const int Mib = 1024 * 1024;

    private static DownloadRequest RequestFor(LocalHttpServer server, string path) => new()
    {
        Url = server.BaseUrl + path,
        StartImmediately = true,
    };

    private static string FinalPath(DownloadItem item) => Path.Combine(item.SaveDirectory, item.FileName);

    /// <summary>Same response, but the connection drops after <paramref name="bytes"/> body bytes.</summary>
    private static ServerResponse Truncated(ServerResponse response, int bytes) =>
        new ServerResponse { Status = response.Status, Reason = response.Reason, Body = response.Body, AbortAfterBytes = bytes }
            .WithHeaders(response.Headers);

    [Fact]
    public void WorkerCount_IsThePartCountBeforeTheAdvancedDoubling()
    {
        Assert.Equal(16, SegmentPlanner.CountSegments(64L * Mib, 8, 0, advanced: true));
        Assert.Equal(8, SegmentPlanner.WorkerCount(64L * Mib, 8, 0));
        Assert.Equal(4, SegmentPlanner.WorkerCount(64L * Mib, 8, domainPartLimit: 4));
        Assert.Equal(1, SegmentPlanner.WorkerCount(1 * Mib, 8, 0));
    }

    [Fact]
    public async Task AdvancedMethod_ExtraPartsWaitForAFreeConnection()
    {
        var content = TestData.Random(34 * Mib, seed: 4101);
        var file = new VirtualFile(content);
        int inFlight = 0, maxInFlight = 0;

        await using var server = new LocalHttpServer();
        server.Handler = req =>
        {
            if (req.Range is { From: 0, To: 0 }) return file.Serve(req); // probe
            var now = Interlocked.Increment(ref inFlight);
            int seen;
            while ((seen = Volatile.Read(ref maxInFlight)) < now && Interlocked.CompareExchange(ref maxInFlight, now, seen) != seen) { }
            Thread.Sleep(150);
            Interlocked.Decrement(ref inFlight);
            return file.Serve(req);
        };

        // Raw 3 = 4 parts; the advanced method doubles that to 8 parts for a file of 32 MiB or more.
        await using var h = new EngineHarness(s =>
        {
            s.Set(SettingKeys.DownloadPart, 3);
            s.Set(SettingKeys.AdvanceDownloadMethod, true);
        });

        var id = await h.Engine.AddAsync(RequestFor(server, "/advanced.bin"));
        var done = await h.WaitForStateAsync(id, DownloadState.Completed, timeoutMs: 60000);

        Assert.Equal(TestData.Sha256Hex(content), TestData.Sha256Hex(FinalPath(done)));
        // Eight distinct parts were fetched, never more than four at once.
        var partStarts = server.Requests.Select(r => r.Range).Where(r => r is not { From: 0, To: 0 } && r is not null)
            .Select(r => r!.Value.From).Distinct().Count();
        Assert.Equal(8, partStarts);
        Assert.InRange(maxInFlight, 1, 4);
    }

    [Fact]
    public async Task NonResumable_Interrupted_StopsWith186_WhenAutoRestartIsOff()
    {
        var file = new VirtualFile(TestData.Random(512 * 1024, seed: 4102)) { SupportRanges = false };

        await using var server = new LocalHttpServer();
        server.Handler = req =>
        {
            var response = file.Serve(req);
            return req.Range is null ? Truncated(response, 64 * 1024) : response;
        };

        await using var h = new EngineHarness(s => s.Set(SettingKeys.AutoRestartNonResumable, false));
        var id = await h.Engine.AddAsync(RequestFor(server, "/no-resume.bin"));
        var failed = await h.WaitForStateAsync(id, DownloadState.Error, timeoutMs: 30000);

        Assert.Equal(EngineErrorCodes.NotResumable, failed.ErrorCode);
    }

    [Fact]
    public async Task NonResumable_Interrupted_RestartsFromZero_WhenAutoRestartIsOn()
    {
        var content = TestData.Random(512 * 1024, seed: 4103);
        var file = new VirtualFile(content) { SupportRanges = false };
        var aborted = 0;

        await using var server = new LocalHttpServer();
        server.Handler = req =>
        {
            var response = file.Serve(req);
            return req.Range is null && Interlocked.Exchange(ref aborted, 1) == 0 ? Truncated(response, 64 * 1024) : response;
        };

        await using var h = new EngineHarness(s => s.Set(SettingKeys.AutoRestartNonResumable, true));
        var id = await h.Engine.AddAsync(RequestFor(server, "/restart.bin"));
        var done = await h.WaitForStateAsync(id, DownloadState.Completed, timeoutMs: 30000);

        Assert.Equal(TestData.Sha256Hex(content), TestData.Sha256Hex(FinalPath(done)));
    }

    [Fact]
    public async Task BatteryLimit_HoldsDownloadsOnLowBattery_AndStartsWhenPluggedIn()
    {
        var content = TestData.Random(256 * 1024, seed: 4104);
        var file = new VirtualFile(content);
        await using var server = new LocalHttpServer { Handler = file.Serve };

        var power = new PowerReading(OnBattery: true, Percent: 10);
        await using var h = new EngineHarness(s => s.Set(SettingKeys.BatteryLimit, 20));
        h.Engine.PowerStatus = () => power;

        var id = await h.Engine.AddAsync(RequestFor(server, "/battery.bin"));
        await Task.Delay(1500);
        Assert.Equal(DownloadState.Queued, (await h.GetItemAsync(id)).State);
        Assert.Empty(server.Requests);

        power = new PowerReading(OnBattery: false, Percent: 10); // plugged in: the limit no longer applies
        var done = await h.WaitForStateAsync(id, DownloadState.Completed, timeoutMs: 30000);
        Assert.Equal(TestData.Sha256Hex(content), TestData.Sha256Hex(FinalPath(done)));
    }

    /// <summary>The Settings page stores the proxy type by name; it must not fall back to HTTP for "SOCKS5".</summary>
    [Theory]
    [InlineData("SOCKS5", "socks5")]
    [InlineData("HTTPS", "https")]
    [InlineData("HTTP", "http")]
    [InlineData("1", "socks5")]
    public void ProxyType_ByNameOrNumber(string stored, string scheme)
    {
        using var temp = new TempDirectory();
        var s = new JsonSettingsStore(Path.Combine(temp.Path, "settings.json"));
        s.Set(SettingKeys.ProxyEnabled, true);
        s.Set(SettingKeys.ProxyType, stored);
        s.Set(SettingKeys.ProxyHost, "proxy.example");
        s.Set(SettingKeys.ProxyPort, 1080);

        Assert.Equal(scheme, EngineTransport.BuildProxy(s).Address!.Scheme);
    }

    [Theory]
    [InlineData("Orbot (Tor)", "socks5://127.0.0.1:9050/")]
    [InlineData("I2P", "http://127.0.0.1:4444/")]
    public void ProxyProvider_PresetsLocalClients(string provider, string expected)
    {
        using var temp = new TempDirectory();
        var s = new JsonSettingsStore(Path.Combine(temp.Path, "settings.json"));
        s.Set(SettingKeys.ProxyEnabled, true);
        s.Set(SettingKeys.ProxyProvider, provider);

        Assert.Equal(expected, EngineTransport.BuildProxy(s).Address!.ToString());
    }

    [Fact]
    public async Task RemoveOldLinks_ZeroDaysMeansOff()
    {
        var file = new VirtualFile(TestData.Random(64 * 1024, seed: 4105));
        await using var server = new LocalHttpServer { Handler = file.Serve };
        await using var h = new EngineHarness(s => s.Set(SettingKeys.DeleteLinksAfterDays, 0));

        var id = await h.Engine.AddAsync(RequestFor(server, "/keep.bin"));
        await h.WaitForStateAsync(id, DownloadState.Completed);

        Assert.Equal(0, await h.Engine.RemoveOldLinksAsync(DateTimeOffset.UtcNow.AddDays(30)));
        Assert.NotNull(await h.Repository.GetAsync(id));
    }
}
