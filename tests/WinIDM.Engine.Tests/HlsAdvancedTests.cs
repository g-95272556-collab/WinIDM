using System.Diagnostics;
using System.Text;
using WinIDM.Core.Models;
using WinIDM.Engine;
using WinIDM.Engine.Tests.Support;

namespace WinIDM.Engine.Tests;

/// <summary>Advanced HLS parser: BYTERANGE, EXT-X-MAP, EXT-X-MEDIA, live streams, and playlist fingerprints.</summary>
public sealed class HlsParserTests
{
    [Fact]
    public void ByteRange_WithoutOffset_ContinuesFromPreviousRangeOfSameUri()
    {
        var text = "#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXTINF:2,\n#EXT-X-BYTERANGE:100@0\n/a.ts\n" +
                   "#EXTINF:2,\n#EXT-X-BYTERANGE:50\n/a.ts\n#EXT-X-ENDLIST\n";

        var playlist = M3u8Playlist.Parse(text, new Uri("http://127.0.0.1/x/m.m3u8"));

        Assert.Equal(2, playlist.Segments.Count);
        Assert.Equal(new HlsByteRange(100, 0), playlist.Segments[0].ByteRange);
        Assert.Equal(new HlsByteRange(50, 100), playlist.Segments[1].ByteRange);
    }

    [Fact]
    public void MasterPlaylist_ParsesRenditionsAndAudioGroup()
    {
        var text = "#EXTM3U\n" +
                   "#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"aud\",NAME=\"en\",DEFAULT=YES,URI=\"audio.m3u8\"\n" +
                   "#EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360,AUDIO=\"aud\"\nvideo.m3u8\n";

        var playlist = M3u8Playlist.Parse(text, new Uri("http://127.0.0.1/x/m.m3u8"));

        Assert.True(playlist.IsMaster);
        Assert.Equal("aud", playlist.Variants[0].AudioGroup);
        Assert.Equal("http://127.0.0.1/x/video.m3u8", playlist.Variants[0].Uri.AbsoluteUri);
        var rendition = Assert.Single(playlist.Renditions);
        Assert.Equal("AUDIO", rendition.Type);
        Assert.True(rendition.IsDefault);
        Assert.Equal("http://127.0.0.1/x/audio.m3u8", rendition.Uri!.AbsoluteUri);
    }

    [Fact]
    public void MediaPlaylist_ParsesMapWithByteRange_AndLiveState()
    {
        var text = "#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXT-X-MAP:URI=\"init.mp4\",BYTERANGE=\"720@0\"\n" +
                   "#EXTINF:2,\nseg0.m4s\n#EXTINF:2,\nseg1.m4s\n";

        var playlist = M3u8Playlist.Parse(text, new Uri("http://127.0.0.1/x/m.m3u8"));

        Assert.NotNull(playlist.Map);
        Assert.Equal("http://127.0.0.1/x/init.mp4", playlist.Map!.Uri.AbsoluteUri);
        Assert.Equal(new HlsByteRange(720, 0), playlist.Map.ByteRange);
        Assert.True(playlist.HasInitializationMap);
        Assert.Equal(6, playlist.TargetDuration);
        Assert.True(playlist.IsLive, "No ENDLIST means a live stream.");
        Assert.Equal(0, playlist.MediaSequence);
    }

    [Fact]
    public void ByteRange_Invalid_Throws()
    {
        Assert.Throws<FormatException>(() => M3u8Playlist.ParseByteRange("abc"));
        Assert.Throws<FormatException>(() => M3u8Playlist.ParseByteRange("0"));
        Assert.Throws<FormatException>(() => M3u8Playlist.ParseByteRange("10@-1"));
    }

    [Fact]
    public void Fingerprint_IgnoresQuery_ButChangesWithPath()
    {
        var a = HlsDownloader.Fingerprint(new Uri("http://127.0.0.1/x/m.m3u8?t=1"), null, null);
        var b = HlsDownloader.Fingerprint(new Uri("http://127.0.0.1/x/m.m3u8?t=2"), null, null);
        var c = HlsDownloader.Fingerprint(new Uri("http://127.0.0.1/y/m.m3u8?t=1"), null, null);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(40, a.Length);
    }
}

/// <summary>Advanced HLS downloads against a local server (127.0.0.1).</summary>
public sealed class HlsAdvancedDownloadTests
{
    private static ServerResponse Text(string body)
    {
        var response = new ServerResponse { Status = 200, Reason = "OK", Body = Encoding.UTF8.GetBytes(body) };
        response.Headers["Content-Type"] = "application/vnd.apple.mpegurl";
        return response;
    }

    private static ServerResponse Bytes(byte[] body) => new() { Status = 200, Reason = "OK", Body = body };

    private static ServerResponse NotFound() => new() { Status = 404, Reason = "Not Found" };

    private static string Segments(int count, string prefix = "s", string extinf = "2")
    {
        var sb = new StringBuilder("#EXTM3U\n#EXT-X-TARGETDURATION:2\n");
        for (var i = 0; i < count; i++) sb.Append($"#EXTINF:{extinf},\n/{prefix}{i}.ts\n");
        return sb.ToString();
    }

    [Fact]
    public async Task FragmentedMp4_WithMap_WritesInitThenFragments_AsMp4()
    {
        await using var server = new LocalHttpServer();
        var init = TestData.Random(300, seed: 120);
        var s0 = TestData.Random(4000, seed: 121);
        var s1 = TestData.Random(3000, seed: 122);
        server.Handler = req => req.Target switch
        {
            "/a.m3u8" => Text("#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXT-X-MAP:URI=\"/init.mp4\"\n" +
                              "#EXTINF:2,\n/s0.m4s\n#EXTINF:2,\n/s1.m4s\n#EXT-X-ENDLIST\n"),
            "/init.mp4" => Bytes(init),
            "/s0.m4s" => Bytes(s0),
            "/s1.m4s" => Bytes(s1),
            _ => NotFound(),
        };

        using var temp = new TempDirectory();
        using var downloader = new HlsDownloader();
        var output = await downloader.DownloadAsync(new Uri(server.BaseUrl + "/a.m3u8"), temp.Path, "movie",
            new HlsDownloadOptions { ConvertToMp4 = false }, null, CancellationToken.None);

        Assert.EndsWith(".mp4", output);
        Assert.Equal(init.Concat(s0).Concat(s1).ToArray(), File.ReadAllBytes(output));
    }

    [Fact]
    public async Task ByteRangeSegments_AreFetchedWithRangeHeaders()
    {
        await using var server = new LocalHttpServer();
        var media = TestData.Random(5000, seed: 110);
        var file = new VirtualFile(media);
        server.Handler = req => req.Target switch
        {
            "/media.bin" => file.Serve(req),
            "/a.m3u8" => Text("#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXTINF:1,\n#EXT-X-BYTERANGE:2000@0\n/media.bin\n" +
                              "#EXTINF:1,\n#EXT-X-BYTERANGE:1500\n/media.bin\n#EXT-X-ENDLIST\n"),
            _ => NotFound(),
        };

        using var temp = new TempDirectory();
        using var downloader = new HlsDownloader();
        var output = await downloader.DownloadAsync(new Uri(server.BaseUrl + "/a.m3u8"), temp.Path, "range",
            new HlsDownloadOptions { ConvertToMp4 = false }, null, CancellationToken.None);

        Assert.Equal(media[..3500], File.ReadAllBytes(output));
        Assert.Contains(server.Requests, r => r.Target == "/media.bin" && r.Range is { From: 2000, To: 3499 });
    }

    [Fact]
    public async Task Resume_AfterFailure_ContinuesFromCheckpoint_WithoutRefetching()
    {
        await using var server = new LocalHttpServer();
        var segments = Enumerable.Range(0, 5).Select(i => TestData.Random(20_000 + i, seed: 130 + i)).ToArray();
        var failOnce = 1;
        server.Handler = req =>
        {
            if (req.Target == "/a.m3u8") return Text(Segments(segments.Length));
            if (req.Target == "/s3.ts" && Interlocked.Exchange(ref failOnce, 0) == 1)
            {
                return new ServerResponse { Status = 503, Reason = "Service Unavailable" };
            }

            var index = req.Target.StartsWith("/s", StringComparison.Ordinal) && req.Target.EndsWith(".ts", StringComparison.Ordinal)
                ? int.Parse(req.Target[2..^3])
                : -1;
            return index >= 0 && index < segments.Length ? Bytes(segments[index]) : NotFound();
        };

        using var temp = new TempDirectory();
        using var downloader = new HlsDownloader();
        var url = new Uri(server.BaseUrl + "/a.m3u8");
        var states = new List<HlsResumeState>();

        var first = new HlsDownloadOptions
        {
            ConvertToMp4 = false,
            MaxConcurrentSegments = 1,
            Checkpoint = state =>
            {
                states.Add(state);
                return Task.CompletedTask;
            },
        };
        await Assert.ThrowsAsync<TransferException>(() =>
            downloader.DownloadDetailedAsync(url, temp.Path, "x", first, null, CancellationToken.None));

        var last = states[^1];
        Assert.Equal(3, last.Video.NextSequence);
        Assert.Equal(3, last.Video.CompletedSegments);

        var resumed = new HlsDownloadOptions { ConvertToMp4 = false, MaxConcurrentSegments = 1, Resume = last };
        var result = await downloader.DownloadDetailedAsync(url, temp.Path, "x", resumed, null, CancellationToken.None);

        Assert.Equal(segments.SelectMany(b => b).ToArray(), File.ReadAllBytes(result.OutputPath));
        Assert.Equal(1, server.Requests.Count(r => r.Target == "/s0.ts"));
        Assert.Equal(1, server.Requests.Count(r => r.Target == "/s2.ts"));
        Assert.Equal(2, server.Requests.Count(r => r.Target == "/s3.ts"));
    }

    [Fact]
    public async Task Resume_WithChangedPlaylist_RestartsFromZero()
    {
        await using var server = new LocalHttpServer();
        var segments = Enumerable.Range(0, 2).Select(i => TestData.Random(9000, seed: 140 + i)).ToArray();
        server.Handler = req => req.Target == "/a.m3u8" ? Text(Segments(2))
            : req.Target is "/s0.ts" ? Bytes(segments[0])
            : req.Target is "/s1.ts" ? Bytes(segments[1])
            : NotFound();

        using var temp = new TempDirectory();
        using var downloader = new HlsDownloader();
        var stale = new HlsResumeState("old-fingerprint-mismatch", new HlsTrackProgress(1, 1, 9000), null);

        var result = await downloader.DownloadDetailedAsync(new Uri(server.BaseUrl + "/a.m3u8"), temp.Path, "x",
            new HlsDownloadOptions { ConvertToMp4 = false, Resume = stale }, null, CancellationToken.None);

        Assert.Equal(segments.SelectMany(b => b).ToArray(), File.ReadAllBytes(result.OutputPath));
        Assert.Contains(result.Notes, n => n.Contains("starts from scratch", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Live_Snapshot_DownloadsCurrentWindowOnly_AndReportsLive()
    {
        await using var server = new LocalHttpServer();
        var l0 = TestData.Random(5000, seed: 150);
        var l1 = TestData.Random(5000, seed: 151);
        server.Handler = req => req.Target switch
        {
            "/live.m3u8" => Text("#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXTINF:2,\n/l0.ts\n#EXTINF:2,\n/l1.ts\n"),
            "/l0.ts" => Bytes(l0),
            "/l1.ts" => Bytes(l1),
            _ => NotFound(),
        };

        using var temp = new TempDirectory();
        using var downloader = new HlsDownloader();
        var result = await downloader.DownloadDetailedAsync(new Uri(server.BaseUrl + "/live.m3u8"), temp.Path, "live",
            new HlsDownloadOptions { ConvertToMp4 = false }, null, CancellationToken.None);

        Assert.True(result.IsLive);
        Assert.Contains(result.Notes, n => n.Contains("Live stream", StringComparison.Ordinal));
        Assert.Equal(l0.Concat(l1).ToArray(), File.ReadAllBytes(result.OutputPath));
    }

    [Fact]
    public async Task Live_RecordUntilStopped_FollowsPlaylistUntilEndList()
    {
        await using var server = new LocalHttpServer();
        var segments = Enumerable.Range(0, 3).Select(i => TestData.Random(4000, seed: 160 + i)).ToArray();
        var calls = 0;
        server.Handler = req =>
        {
            if (req.Target == "/live.m3u8")
            {
                // First request: two segments without ENDLIST. After that: three segments with ENDLIST.
                return Interlocked.Increment(ref calls) == 1
                    ? Text("#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXTINF:2,\n/l0.ts\n#EXTINF:2,\n/l1.ts\n")
                    : Text("#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXTINF:2,\n/l0.ts\n#EXTINF:2,\n/l1.ts\n#EXTINF:2,\n/l2.ts\n#EXT-X-ENDLIST\n");
            }

            var index = req.Target switch { "/l0.ts" => 0, "/l1.ts" => 1, "/l2.ts" => 2, _ => -1 };
            return index >= 0 ? Bytes(segments[index]) : NotFound();
        };

        using var temp = new TempDirectory();
        using var downloader = new HlsDownloader();
        var result = await downloader.DownloadDetailedAsync(new Uri(server.BaseUrl + "/live.m3u8"), temp.Path, "record",
            new HlsDownloadOptions
            {
                ConvertToMp4 = false,
                RecordLiveUntilStopped = true,
                LivePollInterval = TimeSpan.FromMilliseconds(20),
            }, null, CancellationToken.None);

        Assert.Equal(segments.SelectMany(b => b).ToArray(), File.ReadAllBytes(result.OutputPath));
        Assert.True(calls >= 2);
    }

    [Fact]
    public async Task SampleAesFmp4_IsRejected_WithClearMessage()
    {
        // SAMPLE-AES for MPEG-TS is supported; fMP4 (EXT-X-MAP) is not supported yet and must be rejected clearly.
        await using var server = new LocalHttpServer();
        server.Handler = _ => Text("#EXTM3U\n#EXT-X-MAP:URI=\"/init.mp4\"\n#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"/k\"\n#EXTINF:2,\n/s.m4s\n#EXT-X-ENDLIST\n");

        using var temp = new TempDirectory();
        using var downloader = new HlsDownloader();
        var ex = await Assert.ThrowsAsync<TransferException>(() =>
            downloader.DownloadAsync(new Uri(server.BaseUrl + "/a.m3u8"), temp.Path, "x",
                new HlsDownloadOptions { ConvertToMp4 = false }, null, CancellationToken.None));

        Assert.Equal(EngineErrorCodes.UnsupportedPlaylist, ex.Code);
        Assert.Contains("SAMPLE-AES", ex.Message);
        Assert.False(ex.Retryable);
    }

    [Fact]
    public async Task SeparateAudioAndVideo_AreMergedWithMediaMerger()
    {
        var merger = new MediaMerger();
        if (!merger.IsAvailable) return; // ffmpeg not on PATH: merge test skipped.

        using var temp = new TempDirectory();
        var videoTs = Path.Combine(temp.Path, "src-video.ts");
        var audioTs = Path.Combine(temp.Path, "src-audio.ts");
        RunFfmpeg(merger.FfmpegPath!, "-y", "-f", "lavfi", "-i", "testsrc=duration=2:size=160x120:rate=10",
            "-c:v", "mpeg2video", "-an", "-f", "mpegts", videoTs);
        RunFfmpeg(merger.FfmpegPath!, "-y", "-f", "lavfi", "-i", "sine=frequency=440:duration=2",
            "-c:a", "aac", "-vn", "-f", "mpegts", audioTs);

        await using var server = new LocalHttpServer();
        var videoBytes = await File.ReadAllBytesAsync(videoTs);
        var audioBytes = await File.ReadAllBytesAsync(audioTs);
        server.Handler = req => req.Target switch
        {
            "/m.m3u8" => Text("#EXTM3U\n#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"aud\",NAME=\"en\",DEFAULT=YES,URI=\"/audio.m3u8\"\n" +
                              "#EXT-X-STREAM-INF:BANDWIDTH=500000,AUDIO=\"aud\"\n/video.m3u8\n"),
            "/video.m3u8" => Text("#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXTINF:2,\n/v0.ts\n#EXT-X-ENDLIST\n"),
            "/audio.m3u8" => Text("#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXTINF:2,\n/a0.ts\n#EXT-X-ENDLIST\n"),
            "/v0.ts" => Bytes(videoBytes),
            "/a0.ts" => Bytes(audioBytes),
            _ => NotFound(),
        };

        var outDir = Path.Combine(temp.Path, "out");
        using var downloader = new HlsDownloader();
        var result = await downloader.DownloadDetailedAsync(new Uri(server.BaseUrl + "/m.m3u8"), outDir, "movie",
            new HlsDownloadOptions { ConvertToMp4 = true }, null, CancellationToken.None);

        Assert.True(result.Merged);
        Assert.Null(result.AudioPath);
        Assert.EndsWith(".mp4", result.OutputPath);
        var header = new byte[12];
        using (var stream = File.OpenRead(result.OutputPath)) _ = stream.Read(header, 0, header.Length);
        Assert.Equal("ftyp", Encoding.ASCII.GetString(header, 4, 4));
        Assert.False(File.Exists(Path.Combine(outDir, "movie.video.ts")));
        Assert.False(File.Exists(Path.Combine(outDir, "movie.audio.ts")));
    }

    private static void RunFfmpeg(string ffmpeg, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)!;
        process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("ffmpeg failed to generate test media: " + error);
    }
}

/// <summary>HLS download through the engine: a transient segment error is retried and the download completes.</summary>
public sealed class HlsEngineTests
{
    [Fact]
    public async Task HlsJob_TransientSegmentError_IsRetried_AndCompletes()
    {
        await using var server = new LocalHttpServer();
        var segments = Enumerable.Range(0, 4).Select(i => TestData.Random(12_000, seed: 170 + i)).ToArray();
        var failed = 0;
        server.Handler = req =>
        {
            if (req.Target == "/a.m3u8")
            {
                return new ServerResponse { Status = 200, Reason = "OK", Body = Encoding.UTF8.GetBytes(
                    "#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXTINF:2,\n/e0.ts\n#EXTINF:2,\n/e1.ts\n#EXTINF:2,\n/e2.ts\n#EXTINF:2,\n/e3.ts\n#EXT-X-ENDLIST\n") };
            }

            if (req.Target == "/e2.ts" && Interlocked.Exchange(ref failed, 1) == 0)
            {
                return new ServerResponse { Status = 503, Reason = "Service Unavailable" };
            }

            var index = req.Target switch { "/e0.ts" => 0, "/e1.ts" => 1, "/e2.ts" => 2, "/e3.ts" => 3, _ => -1 };
            return index >= 0 ? new ServerResponse { Status = 200, Reason = "OK", Body = segments[index] } : new ServerResponse { Status = 404, Reason = "Not Found" };
        };

        // Output format .ts (no ffmpeg conversion) so the test data is not reinterpreted.
        await using var h = new EngineHarness(s => s.Set(SettingKeys.ConvertTsToMp4, false));

        var id = await h.Engine.AddAsync(new DownloadRequest { Url = server.BaseUrl + "/a.m3u8" });
        var item = await h.WaitForStateAsync(id, DownloadState.Completed);

        Assert.EndsWith(".ts", item.FileName);
        Assert.Equal(segments.SelectMany(b => b).ToArray(), File.ReadAllBytes(Path.Combine(item.SaveDirectory, item.FileName)));
        Assert.Equal(2, server.Requests.Count(r => r.Target == "/e2.ts"));
    }
}
