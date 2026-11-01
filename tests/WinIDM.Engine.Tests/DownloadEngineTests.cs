using System.Diagnostics;
using System.Text;
using WinIDM.Core.Models;
using WinIDM.Engine;
using WinIDM.Engine.Tests.Support;

namespace WinIDM.Engine.Tests;

/// <summary>
/// Download engine integration tests against a local HTTP server (127.0.0.1). No internet access.
/// </summary>
public sealed class DownloadEngineTests
{
    private const int Mib = 1024 * 1024;

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static DownloadRequest RequestFor(LocalHttpServer server, string path = "/file.bin", bool start = true) => new()
    {
        Url = server.BaseUrl + path,
        StartImmediately = start,
    };

    private static ServerResponse Copy(ServerResponse source, int? abortAfter = null, int chunkDelayMs = 0)
    {
        var copy = new ServerResponse
        {
            Status = source.Status,
            Reason = source.Reason,
            Body = source.Body,
            AbortAfterBytes = abortAfter,
            ChunkDelayMs = chunkDelayMs,
        };
        foreach (var (name, value) in source.Headers) copy.Headers[name] = value;
        return copy;
    }

    private static ServerResponse Plain(int status, string reason, params (string Name, string Value)[] headers)
    {
        var response = new ServerResponse { Status = status, Reason = reason };
        foreach (var (name, value) in headers) response.Headers[name] = value;
        return response;
    }

    private static RecordedRequest NoRangeRequest() => new("GET", "/", new Dictionary<string, string>());

    /// <summary>Segment request: a range other than the 0-0 probe.</summary>
    private static bool IsSegmentRequest(RecordedRequest r) => r.Range is { } range && range.To != 0;

    private static bool IsProbe(RecordedRequest r) => r.Range is { } range && range.From == 0 && range.To == 0;

    private static async Task WaitForProgressAsync(EngineHarness h, long id, long bytes) =>
        await EngineHarness.WaitUntilAsync(async () =>
            (await h.Repository.GetAsync(id))?.DownloadedBytes >= bytes, 20000, $"progress {bytes} bytes");

    private static string FinalPath(DownloadItem item) => Path.Combine(item.SaveDirectory, item.FileName);

    // ------------------------------------------------------------------
    // Basic downloads and segments
    // ------------------------------------------------------------------

    [Fact]
    public async Task RangeServer_SplitsIntoSegments_AndWritesCorrectBytes()
    {
        await using var h = new EngineHarness();
        await using var server = new LocalHttpServer();
        var content = TestData.Random(5 * Mib, seed: 1);
        var file = new VirtualFile(content);
        server.Handler = file.Serve;

        var id = await h.Engine.AddAsync(RequestFor(server));
        var item = await h.WaitForStateAsync(id, DownloadState.Completed);

        Assert.Equal(content.Length, item.TotalBytes);
        Assert.Equal(content.Length, item.DownloadedBytes);
        Assert.True(item.AcceptsRanges);
        Assert.Equal(TestData.Sha256Hex(content), TestData.Sha256Hex(FinalPath(item)));

        // 5 MiB is split into min(8, ceil(5/2)) = 3 segments.
        var segmentStarts = server.Requests.Where(IsSegmentRequest)
            .Select(r => r.Range!.Value.From).Distinct().ToList();
        Assert.Equal(3, segmentStarts.Count);
        Assert.Contains(0L, segmentStarts);
    }

    [Fact]
    public async Task ServerWithoutRange_UsesSingleStreamWithoutRangeHeaders()
    {
        await using var h = new EngineHarness();
        await using var server = new LocalHttpServer();
        var content = TestData.Random(3 * Mib, seed: 2);
        var file = new VirtualFile(content) { SupportRanges = false };
        server.Handler = file.Serve;

        var id = await h.Engine.AddAsync(RequestFor(server));
        var item = await h.WaitForStateAsync(id, DownloadState.Completed);

        Assert.False(item.AcceptsRanges);
        Assert.Equal(TestData.Sha256Hex(content), TestData.Sha256Hex(FinalPath(item)));
        // Only the probe (0-0) may carry a Range header; the rest carry no range.
        Assert.All(server.Requests.Where(r => r.Range is not null), r => Assert.True(IsProbe(r)));
    }

    [Fact]
    public async Task SmallFile_UsesSingleSegment()
    {
        await using var h = new EngineHarness();
        await using var server = new LocalHttpServer();
        var content = TestData.Random(1 * Mib, seed: 3);
        server.Handler = new VirtualFile(content).Serve;

        var id = await h.Engine.AddAsync(RequestFor(server));
        var item = await h.WaitForStateAsync(id, DownloadState.Completed);

        Assert.Single(server.Requests, IsSegmentRequest);
        Assert.Equal(TestData.Sha256Hex(content), TestData.Sha256Hex(FinalPath(item)));
    }

    [Fact]
    public async Task ExplicitPartSetting_LimitsSegmentCount()
    {
        await using var h = new EngineHarness(s => s.Set(SettingKeys.DownloadPart, 1));
        await using var server = new LocalHttpServer();
        var content = TestData.Random(8 * Mib, seed: 4);
        server.Handler = new VirtualFile(content).Serve;

        var id = await h.Engine.AddAsync(RequestFor(server));
        await h.WaitForStateAsync(id, DownloadState.Completed);

        // A setting of 1 means 2 parts (value + 1).
        var starts = server.Requests.Where(IsSegmentRequest).Select(r => r.Range!.Value.From).Distinct().ToList();
        Assert.Equal(2, starts.Count);
    }

    // ------------------------------------------------------------------
    // Resume, recovery, and file changes
    // ------------------------------------------------------------------

    [Fact]
    public async Task ConnectionDrop_ResumesFromLastWrittenByte()
    {
        await using var h = new EngineHarness(s => s.Set(SettingKeys.DownloadPart, 0));
        await using var server = new LocalHttpServer();
        var content = TestData.Random(5 * Mib, seed: 5);
        var file = new VirtualFile(content);
        var dropped = false;
        server.Handler = req =>
        {
            if (IsSegmentRequest(req) && !dropped)
            {
                dropped = true;
                return Copy(file.Serve(req), abortAfter: 300_000);
            }

            return file.Serve(req);
        };

        var id = await h.Engine.AddAsync(RequestFor(server));
        var item = await h.WaitForStateAsync(id, DownloadState.Completed);

        Assert.Equal(TestData.Sha256Hex(content), TestData.Sha256Hex(FinalPath(item)));
        var segments = server.Requests.Where(IsSegmentRequest).ToList();
        Assert.Equal(2, segments.Count);
        Assert.Equal(0, segments[0].Range!.Value.From);
        // The second request starts exactly at the last byte written successfully.
        Assert.Equal(300_000, segments[1].Range!.Value.From);
    }

    [Fact]
    public async Task Pause_ThenResume_RequestsRangeFromSavedProgressWithIfRange()
    {
        await using var h = new EngineHarness(s => s.Set(SettingKeys.DownloadPart, 0));
        await using var server = new LocalHttpServer();
        var content = TestData.Random(5 * Mib, seed: 6);
        var file = new VirtualFile(content, etag: "\"valid-1\"");
        server.Handler = req => IsSegmentRequest(req) ? Copy(file.Serve(req), chunkDelayMs: 5) : file.Serve(req);

        var id = await h.Engine.AddAsync(RequestFor(server));
        await WaitForProgressAsync(h, id, 1 * Mib);

        await h.Engine.PauseAsync(id);
        var paused = await h.WaitForStateAsync(id, DownloadState.Paused);
        var savedOffset = paused.DownloadedBytes;
        Assert.InRange(savedOffset, 1 * Mib, content.Length - 1);

        await h.Engine.ResumeAsync(id);
        var completed = await h.WaitForStateAsync(id, DownloadState.Completed);

        Assert.Equal(TestData.Sha256Hex(content), TestData.Sha256Hex(FinalPath(completed)));
        var resumed = server.Requests.Where(IsSegmentRequest).Last();
        Assert.Equal(savedOffset, resumed.Range!.Value.From);
        Assert.Equal("\"valid-1\"", resumed.Get("If-Range"));
    }

    [Fact]
    public async Task Restart_ReopenedRepository_ResumesFromPersistedProgress()
    {
        await using var h = new EngineHarness(s => s.Set(SettingKeys.DownloadPart, 0));
        await using var server = new LocalHttpServer();
        var content = TestData.Random(5 * Mib, seed: 7);
        var file = new VirtualFile(content);
        server.Handler = req => IsSegmentRequest(req) ? Copy(file.Serve(req), chunkDelayMs: 5) : file.Serve(req);

        var id = await h.Engine.AddAsync(RequestFor(server));
        await WaitForProgressAsync(h, id, 1 * Mib);
        await h.Engine.PauseAsync(id);
        var paused = await h.WaitForStateAsync(id, DownloadState.Paused);

        // Simulate the app being closed and reopened.
        h.Reopen();
        var reloaded = await h.GetItemAsync(id);
        Assert.Equal(DownloadState.Paused, reloaded.State);
        Assert.Equal(paused.DownloadedBytes, reloaded.DownloadedBytes);

        await h.Engine.StartAllAsync();
        var completed = await h.WaitForStateAsync(id, DownloadState.Completed);

        Assert.Equal(TestData.Sha256Hex(content), TestData.Sha256Hex(FinalPath(completed)));
        Assert.Equal(paused.DownloadedBytes, server.Requests.Where(IsSegmentRequest).Last().Range!.Value.From);
    }

    [Fact]
    public async Task InFlightAtStartup_BecomesPaused_AndCanBeStarted()
    {
        await using var h = new EngineHarness();
        await using var server = new LocalHttpServer();
        var content = TestData.Random(1 * Mib, seed: 8);
        server.Handler = new VirtualFile(content).Serve;

        // A record left in the Downloading state (for example a crash during a download).
        var id = await h.Repository.InsertAsync(new DownloadItem
        {
            Url = server.BaseUrl + "/crash.bin",
            FileName = "crash.bin",
            SaveDirectory = h.SaveDirectory,
            State = DownloadState.Downloading,
        });

        h.Reopen();
        await h.Engine.StartAllAsync();
        var item = await h.WaitForStateAsync(id, DownloadState.Completed);

        Assert.Equal(TestData.Sha256Hex(content), TestData.Sha256Hex(FinalPath(item)));
    }

    [Fact]
    public async Task ChangedEtag_RestartsFromZero_AndDownloadsNewContent()
    {
        await using var h = new EngineHarness(s => s.Set(SettingKeys.DownloadPart, 0));
        await using var server = new LocalHttpServer();
        var original = TestData.Random(5 * Mib, seed: 9);
        var updated = TestData.Random(5 * Mib, seed: 10);
        var file = new VirtualFile(original, etag: "\"v1\"");
        server.Handler = req => IsSegmentRequest(req) ? Copy(file.Serve(req), chunkDelayMs: 5) : file.Serve(req);

        var id = await h.Engine.AddAsync(RequestFor(server));
        await WaitForProgressAsync(h, id, 1 * Mib);
        await h.Engine.PauseAsync(id);
        await h.WaitForStateAsync(id, DownloadState.Paused);

        // The file on the server changes after the pause.
        file.Content = updated;
        file.ETag = "\"v2\"";
        var requestsBefore = server.Requests.Count;

        await h.Engine.ResumeAsync(id);
        var completed = await h.WaitForStateAsync(id, DownloadState.Completed);

        Assert.Equal(TestData.Sha256Hex(updated), TestData.Sha256Hex(FinalPath(completed)));
        var afterResume = server.Requests.Skip(requestsBefore).Where(IsSegmentRequest).ToList();
        Assert.Equal(0, afterResume.First().Range!.Value.From);
    }

    [Fact]
    public async Task ServerIgnoresRangeMidDownload_RestartsAndCompletesWithoutRanges()
    {
        await using var h = new EngineHarness();
        await using var server = new LocalHttpServer();
        var content = TestData.Random(3 * Mib, seed: 11);
        var file = new VirtualFile(content);
        var firstProbe = true;
        server.Handler = req =>
        {
            if (req.Range is null) return file.Serve(req);
            if (firstProbe && IsProbe(req))
            {
                firstProbe = false;
                return file.Serve(req);
            }

            // After the first probe, the server ignores ranges and replies with a full 200.
            return file.Serve(NoRangeRequest());
        };

        var id = await h.Engine.AddAsync(RequestFor(server));
        var item = await h.WaitForStateAsync(id, DownloadState.Completed);

        Assert.Equal(TestData.Sha256Hex(content), TestData.Sha256Hex(FinalPath(item)));
        Assert.True(server.Requests.Count(IsProbe) >= 2);
    }

    // ------------------------------------------------------------------
    // Retries and errors
    // ------------------------------------------------------------------

    [Fact]
    public async Task TooManyRequests_IsRetriedAfterRetryAfterDelay()
    {
        await using var h = new EngineHarness();
        await using var server = new LocalHttpServer();
        var content = TestData.Random(1 * Mib, seed: 12);
        var file = new VirtualFile(content);
        var throttled = false;
        server.Handler = req =>
        {
            if (!throttled && IsProbe(req))
            {
                throttled = true;
                return Plain(429, "Too Many Requests", ("Retry-After", "0"));
            }

            return file.Serve(req);
        };

        var id = await h.Engine.AddAsync(RequestFor(server));
        var item = await h.WaitForStateAsync(id, DownloadState.Completed);

        Assert.Equal(TestData.Sha256Hex(content), TestData.Sha256Hex(FinalPath(item)));
        Assert.Equal(2, server.Requests.Count(IsProbe));
    }

    [Fact]
    public async Task RangeNotSatisfiable_IsNotRetried_AndFailsDownload()
    {
        await using var h = new EngineHarness(s => s.Set(SettingKeys.DownloadPart, 0));
        await using var server = new LocalHttpServer();
        var file = new VirtualFile(TestData.Random(3 * Mib, seed: 13));
        server.Handler = req => IsSegmentRequest(req)
            ? Plain(416, "Range Not Satisfiable", ("Content-Range", "bytes */3145728"))
            : file.Serve(req);

        var id = await h.Engine.AddAsync(RequestFor(server));
        var item = await h.WaitForStateAsync(id, DownloadState.Error);

        Assert.Equal(416, item.ErrorCode);
        Assert.Single(server.Requests, IsSegmentRequest);
    }

    [Fact]
    public async Task SegmentServerError_IsRetriedTwiceThenSucceeds()
    {
        await using var h = new EngineHarness(s => s.Set(SettingKeys.DownloadPart, 0));
        await using var server = new LocalHttpServer();
        var content = TestData.Random(2 * Mib, seed: 14);
        var file = new VirtualFile(content);
        var failures = 0;
        server.Handler = req =>
        {
            if (IsSegmentRequest(req) && failures < 2)
            {
                failures++;
                return Plain(503, "Service Unavailable");
            }

            return file.Serve(req);
        };

        var id = await h.Engine.AddAsync(RequestFor(server));
        var item = await h.WaitForStateAsync(id, DownloadState.Completed);

        Assert.Equal(TestData.Sha256Hex(content), TestData.Sha256Hex(FinalPath(item)));
        Assert.Equal(3, server.Requests.Count(IsSegmentRequest));
    }

    [Fact]
    public async Task PersistentServerError_ExhaustsRetryCount_AndSetsError()
    {
        await using var h = new EngineHarness(s => s.Set(SettingKeys.RetryCount, 2));
        await using var server = new LocalHttpServer();
        server.Handler = _ => Plain(503, "Service Unavailable");

        var id = await h.Engine.AddAsync(RequestFor(server));
        var item = await h.WaitForStateAsync(id, DownloadState.Error);

        Assert.Equal(503, item.ErrorCode);
        Assert.Equal(3, server.Requests.Count(IsProbe));
    }

    [Fact]
    public async Task Unauthorized_IsFatal_NoRetry()
    {
        await using var h = new EngineHarness();
        await using var server = new LocalHttpServer();
        server.Handler = _ => Plain(401, "Unauthorized", ("WWW-Authenticate", "Basic"));

        var id = await h.Engine.AddAsync(RequestFor(server));
        var item = await h.WaitForStateAsync(id, DownloadState.Error);

        Assert.Equal(401, item.ErrorCode);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task Redirect_IsFollowed_AndFinalUrlStored()
    {
        await using var h = new EngineHarness();
        await using var server = new LocalHttpServer();
        var content = TestData.Random(1 * Mib, seed: 15);
        var file = new VirtualFile(content);
        server.Handler = req => req.Target == "/start"
            ? Plain(302, "Found", ("Location", "/actual/report.bin"))
            : file.Serve(req);

        var id = await h.Engine.AddAsync(RequestFor(server, "/start"));
        var item = await h.WaitForStateAsync(id, DownloadState.Completed);

        Assert.EndsWith("/actual/report.bin", item.FinalUrl);
        Assert.Equal("report.bin", item.FileName);
        Assert.Equal(TestData.Sha256Hex(content), TestData.Sha256Hex(FinalPath(item)));
    }

    [Fact]
    public async Task ProxyEnabled_RoutesRequestThroughConfiguredProxy()
    {
        // The local server acts as an HTTP proxy: it receives an absolute URL in the request line.
        await using var proxy = new LocalHttpServer();
        var content = TestData.Random(1 * Mib, seed: 31);
        var file = new VirtualFile(content);
        proxy.Handler = req => req.Target.StartsWith("http://example.invalid/", StringComparison.Ordinal)
            ? file.Serve(req)
            : new ServerResponse { Status = 502, Reason = "Bad Gateway" };

        await using var h = new EngineHarness(s =>
        {
            s.Set(SettingKeys.ProxyEnabled, true);
            s.Set(SettingKeys.ProxyHost, "127.0.0.1");
            s.Set(SettingKeys.ProxyPort, proxy.Port);
        });

        var id = await h.Engine.AddAsync(new DownloadRequest { Url = "http://example.invalid/file.bin" });
        var item = await h.WaitForStateAsync(id, DownloadState.Completed);

        Assert.Equal(TestData.Sha256Hex(content), TestData.Sha256Hex(FinalPath(item)));
        Assert.All(proxy.Requests, r => Assert.StartsWith("http://example.invalid/", r.Target));
    }

    [Fact]
    public async Task InvalidProxyPort_FailsWithProxyError()
    {
        await using var h = new EngineHarness(s =>
        {
            s.Set(SettingKeys.ProxyEnabled, true);
            s.Set(SettingKeys.ProxyHost, "127.0.0.1");
            s.Set(SettingKeys.ProxyPort, 0);
        });
        await using var server = new LocalHttpServer();

        var id = await h.Engine.AddAsync(RequestFor(server));
        var item = await h.WaitForStateAsync(id, DownloadState.Error);

        Assert.Equal(182, item.ErrorCode);
        Assert.Empty(server.Requests);
    }

    // ------------------------------------------------------------------
    // Request headers
    // ------------------------------------------------------------------

    [Fact]
    public async Task AuthCookieRefererUserAgentAndCustomHeaders_AreSent()
    {
        await using var h = new EngineHarness();
        await using var server = new LocalHttpServer();
        var content = TestData.Random(1 * Mib, seed: 16);
        server.Handler = new VirtualFile(content).Serve;

        var id = await h.Engine.AddAsync(new DownloadRequest
        {
            Url = server.BaseUrl + "/secret.bin",
            Username = "ali",
            Password = "secret",
            CookieHeader = "sid=abc123",
            Referer = "https://referrer.test/",
            UserAgent = "WinIDM-Test",
            Headers = new Dictionary<string, string> { ["X-Test"] = "yes" },
        });
        var item = await h.WaitForStateAsync(id, DownloadState.Completed);

        Assert.Equal(TestData.Sha256Hex(content), TestData.Sha256Hex(FinalPath(item)));
        var first = server.Requests[0];
        Assert.Equal("Basic " + Convert.ToBase64String("ali:secret"u8.ToArray()), first.Get("Authorization"));
        Assert.Equal("sid=abc123", first.Get("Cookie"));
        Assert.Equal("https://referrer.test/", first.Get("Referer"));
        Assert.Equal("WinIDM-Test", first.Get("User-Agent"));
        Assert.Equal("yes", first.Get("X-Test"));
        Assert.Equal("identity", first.Get("Accept-Encoding"));
    }

    // ------------------------------------------------------------------
    // Speed limit, checksum, scheduler, Wi-Fi, and queue
    // ------------------------------------------------------------------

    [Fact]
    public async Task PerDownloadSpeedLimit_ThrottlesTransfer()
    {
        await using var h = new EngineHarness();
        await using var server = new LocalHttpServer();
        server.Handler = new VirtualFile(TestData.Random(512 * 1024, seed: 17)).Serve;

        var id = await h.Engine.AddAsync(RequestFor(server, start: false));
        await h.Engine.SetSpeedLimitAsync(id, 256 * 1024);

        var watch = Stopwatch.StartNew();
        await h.Engine.ResumeAsync(id);
        await h.WaitForStateAsync(id, DownloadState.Completed);

        // 512 KiB at 256 KiB/s takes about 2 seconds.
        Assert.True(watch.Elapsed.TotalSeconds >= 1.2, $"Too fast: {watch.Elapsed.TotalSeconds:0.00} s");
    }

    [Fact]
    public async Task GlobalSpeedLimit_FromSettings_ThrottlesTransfer()
    {
        await using var h = new EngineHarness(s =>
        {
            s.Set(SettingKeys.SpeedLimiter, true);
            s.Set(SettingKeys.SpeedLimit, 256);
        });
        await using var server = new LocalHttpServer();
        server.Handler = new VirtualFile(TestData.Random(512 * 1024, seed: 18)).Serve;

        var watch = Stopwatch.StartNew();
        var id = await h.Engine.AddAsync(RequestFor(server));
        await h.WaitForStateAsync(id, DownloadState.Completed);

        Assert.True(watch.Elapsed.TotalSeconds >= 1.2, $"Too fast: {watch.Elapsed.TotalSeconds:0.00} s");
    }

    [Fact]
    public async Task Checksum_Match_Completes()
    {
        await using var h = new EngineHarness();
        await using var server = new LocalHttpServer();
        var content = TestData.Random(1 * Mib, seed: 19);
        server.Handler = new VirtualFile(content).Serve;

        var id = await h.Engine.AddAsync(RequestFor(server, start: false));
        await h.Engine.SetChecksumAsync(id, TestData.Sha256Hex(content).ToUpperInvariant(), null);
        await h.Engine.ResumeAsync(id);

        await h.WaitForStateAsync(id, DownloadState.Completed);
    }

    [Fact]
    public async Task Checksum_Mismatch_SetsErrorAndKeepsFile()
    {
        await using var h = new EngineHarness();
        await using var server = new LocalHttpServer();
        var content = TestData.Random(1 * Mib, seed: 20);
        server.Handler = new VirtualFile(content).Serve;

        var id = await h.Engine.AddAsync(RequestFor(server, start: false));
        await h.Engine.SetChecksumAsync(id, new string('0', 64), null);
        await h.Engine.ResumeAsync(id);

        var item = await h.WaitForStateAsync(id, DownloadState.Error);

        Assert.Equal(180, item.ErrorCode);
        Assert.True(File.Exists(FinalPath(item)));
    }

    [Fact]
    public async Task Md5Checksum_IsVerified()
    {
        await using var h = new EngineHarness();
        await using var server = new LocalHttpServer();
        var content = TestData.Random(1 * Mib, seed: 21);
        server.Handler = new VirtualFile(content).Serve;
        var md5 = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(content)).ToLowerInvariant();

        var id = await h.Engine.AddAsync(RequestFor(server, start: false));
        await h.Engine.SetChecksumAsync(id, null, md5);
        await h.Engine.ResumeAsync(id);

        await h.WaitForStateAsync(id, DownloadState.Completed);
    }

    [Fact]
    public async Task ScheduledDownload_WaitsUntilScheduledTime()
    {
        await using var h = new EngineHarness();
        await using var server = new LocalHttpServer();
        server.Handler = new VirtualFile(TestData.Random(1 * Mib, seed: 22)).Serve;
        var scheduled = DateTimeOffset.UtcNow.AddSeconds(2);

        var id = await h.Engine.AddAsync(new DownloadRequest
        {
            Url = server.BaseUrl + "/schedule.bin",
            ScheduledAt = scheduled,
        });

        Assert.Equal(DownloadState.Scheduled, (await h.GetItemAsync(id)).State);
        Assert.Empty(server.Requests);

        var item = await h.WaitForStateAsync(id, DownloadState.Completed, timeoutMs: 20000);
        Assert.True(item.CompletedAt >= scheduled.AddMilliseconds(-100));
    }

    [Fact]
    public async Task WifiOnly_BlocksUntilWifiAvailable()
    {
        await using var h = new EngineHarness(s => s.Set(SettingKeys.WifiOnly, true));
        await using var server = new LocalHttpServer();
        server.Handler = new VirtualFile(TestData.Random(1 * Mib, seed: 23)).Serve;
        var wifi = false;
        h.Engine.WifiAvailability = () => wifi;

        var id = await h.Engine.AddAsync(RequestFor(server));
        await Task.Delay(1500);
        Assert.Equal(DownloadState.Queued, (await h.GetItemAsync(id)).State);
        Assert.Empty(server.Requests);

        wifi = true;
        await h.WaitForStateAsync(id, DownloadState.Completed);
    }

    [Fact]
    public async Task ConcurrencyLimit_KeepsExtraDownloadsQueued()
    {
        // idm_pref_download_count = 0 means 1 concurrent download (value + 1).
        await using var h = new EngineHarness(s => s.Set(SettingKeys.DownloadCount, 0));
        await using var server = new LocalHttpServer();
        var content = TestData.Random(4 * Mib, seed: 24);
        var file = new VirtualFile(content);
        server.Handler = req => IsSegmentRequest(req) ? Copy(file.Serve(req), chunkDelayMs: 5) : file.Serve(req);

        var first = await h.Engine.AddAsync(RequestFor(server, "/a.bin"));
        var second = await h.Engine.AddAsync(RequestFor(server, "/b.bin"));

        await EngineHarness.WaitUntilAsync(async () =>
        {
            var a = await h.GetItemAsync(first);
            var b = await h.GetItemAsync(second);
            return (a.State == DownloadState.Downloading && b.State == DownloadState.Queued)
                   || (b.State == DownloadState.Downloading && a.State == DownloadState.Queued);
        }, 15000, "one download running and one queued");

        await h.WaitForStateAsync(first, DownloadState.Completed);
        await h.WaitForStateAsync(second, DownloadState.Completed);
    }

    [Fact]
    public async Task PauseAll_ThenStartAll_CompletesEverything()
    {
        await using var h = new EngineHarness();
        await using var server = new LocalHttpServer();
        var content = TestData.Random(8 * Mib, seed: 25);
        var file = new VirtualFile(content);
        server.Handler = req => IsSegmentRequest(req) ? Copy(file.Serve(req), chunkDelayMs: 5) : file.Serve(req);

        var a = await h.Engine.AddAsync(RequestFor(server, "/one.bin"));
        var b = await h.Engine.AddAsync(RequestFor(server, "/two.bin"));
        await EngineHarness.WaitUntilAsync(async () =>
            (await h.GetItemAsync(a)).State == DownloadState.Downloading &&
            (await h.GetItemAsync(b)).State == DownloadState.Downloading, 15000, "both downloads running");

        await h.Engine.PauseAllAsync();
        await h.WaitForStateAsync(a, DownloadState.Paused);
        await h.WaitForStateAsync(b, DownloadState.Paused);

        await h.Engine.StartAllAsync();
        var first = await h.WaitForStateAsync(a, DownloadState.Completed);
        var second = await h.WaitForStateAsync(b, DownloadState.Completed);

        Assert.Equal(TestData.Sha256Hex(content), TestData.Sha256Hex(FinalPath(first)));
        Assert.Equal(TestData.Sha256Hex(content), TestData.Sha256Hex(FinalPath(second)));
    }

    // ------------------------------------------------------------------
    // Naming, categories, cleanup, and events
    // ------------------------------------------------------------------

    [Fact]
    public async Task ExistingFile_IsRenamedUniquely_AndOriginalKept()
    {
        await using var h = new EngineHarness();
        await using var server = new LocalHttpServer();
        Directory.CreateDirectory(h.SaveDirectory);
        var existing = Path.Combine(h.SaveDirectory, "file.bin");
        File.WriteAllText(existing, "original");
        server.Handler = new VirtualFile(TestData.Random(1 * Mib, seed: 26)).Serve;

        var id = await h.Engine.AddAsync(RequestFor(server));
        var item = await h.WaitForStateAsync(id, DownloadState.Completed);

        Assert.Equal("file (1).bin", item.FileName);
        Assert.Equal("original", File.ReadAllText(existing));
    }

    [Fact]
    public async Task ContentDisposition_NamesFile_AndCataloguesByCategory()
    {
        await using var h = new EngineHarness(s => s.Set(SettingKeys.DisableCataloguing, false));
        await using var server = new LocalHttpServer();
        var content = TestData.Random(1 * Mib, seed: 27);
        var file = new VirtualFile(content) { ContentDisposition = "attachment; filename=\"annual report.pdf\"" };
        server.Handler = file.Serve;

        var id = await h.Engine.AddAsync(RequestFor(server, "/download"));
        var item = await h.WaitForStateAsync(id, DownloadState.Completed);

        Assert.Equal("annual report.pdf", item.FileName);
        Assert.Equal("Document", item.Category);
        Assert.Equal(Path.Combine(h.SaveDirectory, "Document"), item.SaveDirectory);
        Assert.Equal(TestData.Sha256Hex(content), TestData.Sha256Hex(FinalPath(item)));
    }

    [Fact]
    public async Task RemoveWithDeleteFile_RemovesRecordAndFile()
    {
        await using var h = new EngineHarness();
        await using var server = new LocalHttpServer();
        server.Handler = new VirtualFile(TestData.Random(1 * Mib, seed: 28)).Serve;

        var id = await h.Engine.AddAsync(RequestFor(server));
        var item = await h.WaitForStateAsync(id, DownloadState.Completed);
        var path = FinalPath(item);
        Assert.True(File.Exists(path));

        await h.Engine.RemoveAsync(id, deleteFile: true);

        Assert.False(File.Exists(path));
        Assert.Null(await h.Repository.GetAsync(id));
    }

    [Fact]
    public async Task RemoveWithoutDeleteFile_KeepsFile()
    {
        await using var h = new EngineHarness();
        await using var server = new LocalHttpServer();
        server.Handler = new VirtualFile(TestData.Random(1 * Mib, seed: 29)).Serve;

        var id = await h.Engine.AddAsync(RequestFor(server));
        var item = await h.WaitForStateAsync(id, DownloadState.Completed);

        await h.Engine.RemoveAsync(id, deleteFile: false);

        Assert.True(File.Exists(FinalPath(item)));
    }

    [Fact]
    public async Task ProgressAndItemEvents_AreRaised()
    {
        await using var h = new EngineHarness();
        await using var server = new LocalHttpServer();
        var content = TestData.Random(12 * Mib, seed: 30);
        var file = new VirtualFile(content);
        server.Handler = req => IsSegmentRequest(req) ? Copy(file.Serve(req), chunkDelayMs: 5) : file.Serve(req);

        var progress = new List<DownloadProgress>();
        var states = new List<DownloadState>();
        h.Engine.ProgressChanged += p => { lock (progress) progress.Add(p); };
        h.Engine.ItemChanged += i => { lock (states) states.Add(i.State); };

        var id = await h.Engine.AddAsync(RequestFor(server));
        await h.WaitForStateAsync(id, DownloadState.Completed);

        List<DownloadProgress> snapshot;
        lock (progress) snapshot = progress.Where(p => p.Id == id).ToList();
        Assert.NotEmpty(snapshot);
        Assert.Contains(snapshot, p => p.DownloadedBytes > 0 && p.DownloadedBytes < content.Length);
        Assert.True(snapshot.Max(p => p.DownloadedBytes) <= content.Length);
        lock (states)
        {
            Assert.Contains(DownloadState.Downloading, states);
            Assert.Contains(DownloadState.Completed, states);
        }
    }

    [Fact]
    public async Task RedirectLoop_FailsWithTooManyRedirects()
    {
        await using var h = new EngineHarness();
        await using var server = new LocalHttpServer();
        server.Handler = _ => Plain(302, "Found", ("Location", "/loop"));

        var id = await h.Engine.AddAsync(RequestFor(server, "/loop"));
        var item = await h.WaitForStateAsync(id, DownloadState.Error);

        Assert.Equal(183, item.ErrorCode);
    }

    [Fact]
    public async Task ServerWithoutRange_RetriesTransientErrorOnSingleStream()
    {
        await using var h = new EngineHarness();
        await using var server = new LocalHttpServer();
        var content = TestData.Random(2 * Mib, seed: 32);
        var file = new VirtualFile(content) { SupportRanges = false };
        var requests = 0;
        server.Handler = req =>
        {
            // The second request (a full stream without a range) fails once with 503.
            if (Interlocked.Increment(ref requests) == 2) return Plain(503, "Service Unavailable");
            return file.Serve(req);
        };

        var id = await h.Engine.AddAsync(RequestFor(server));
        var item = await h.WaitForStateAsync(id, DownloadState.Completed);

        Assert.Equal(TestData.Sha256Hex(content), TestData.Sha256Hex(FinalPath(item)));
        Assert.Equal(3, server.Requests.Count);
    }

    [Fact]
    public async Task PauseQueuedItem_BecomesPausedWithoutStarting()
    {
        await using var h = new EngineHarness(s => s.Set(SettingKeys.WifiOnly, true));
        await using var server = new LocalHttpServer();
        server.Handler = new VirtualFile(TestData.Random(1 * Mib, seed: 33)).Serve;
        h.Engine.WifiAvailability = () => false;

        var queued = await h.Engine.AddAsync(RequestFor(server, "/queued.bin"));
        var scheduled = await h.Engine.AddAsync(new DownloadRequest
        {
            Url = server.BaseUrl + "/schedule.bin",
            ScheduledAt = DateTimeOffset.UtcNow.AddHours(1),
        });

        await h.Engine.PauseAsync(queued);
        await h.Engine.PauseAsync(scheduled);

        Assert.Equal(DownloadState.Paused, (await h.GetItemAsync(queued)).State);
        Assert.Equal(DownloadState.Paused, (await h.GetItemAsync(scheduled)).State);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task HlsPlaylistUrl_IsDownloadedAsStreamThroughEngine()
    {
        await using var h = new EngineHarness(s => s.Set(SettingKeys.ConvertTsToMp4, false));
        await using var server = new LocalHttpServer();
        var segment = TestData.Random(30_000, seed: 34);
        server.Handler = req => req.Target switch
        {
            "/stream/media.m3u8" => new ServerResponse
            {
                Status = 200,
                Reason = "OK",
                Body = Encoding.UTF8.GetBytes("#EXTM3U\n#EXTINF:2,\n/stream/s0.ts\n#EXT-X-ENDLIST\n"),
            },
            "/stream/s0.ts" => new ServerResponse { Status = 200, Reason = "OK", Body = segment },
            _ => new ServerResponse { Status = 404, Reason = "Not Found" },
        };

        var id = await h.Engine.AddAsync(RequestFor(server, "/stream/media.m3u8"));
        var item = await h.WaitForStateAsync(id, DownloadState.Completed);

        Assert.Equal(DownloadKind.Hls, item.Kind);
        Assert.Equal("media.ts", item.FileName);
        Assert.Equal(segment, File.ReadAllBytes(FinalPath(item)));
    }

    [Fact]
    public async Task InvalidUrl_IsRejected()
    {
        await using var h = new EngineHarness();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            h.Engine.AddAsync(new DownloadRequest { Url = "ftp://example.test/fail" }));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            h.Engine.AddAsync(new DownloadRequest { Url = "not-a-url" }));
    }

    [Fact]
    public async Task UnknownId_ThrowsKeyNotFound()
    {
        await using var h = new EngineHarness();

        await Assert.ThrowsAsync<KeyNotFoundException>(() => h.Engine.PauseAsync(999_999));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => h.Engine.ResumeAsync(999_999));
    }
}
