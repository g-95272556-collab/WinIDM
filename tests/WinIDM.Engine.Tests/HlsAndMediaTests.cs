using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using WinIDM.Engine;
using WinIDM.Engine.Tests.Support;

namespace WinIDM.Engine.Tests;

public sealed class HlsDownloaderTests
{
    private static byte[] Encrypt(byte[] plain, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.KeySize = 128;
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(plain, 0, plain.Length);
    }

    private static byte[] SequenceIv(long sequence)
    {
        var iv = new byte[16];
        for (var i = 0; i < 8; i++) iv[15 - i] = (byte)(sequence >> (8 * i));
        return iv;
    }

    [Fact]
    public async Task Aes128Segments_AreDecryptedAndConcatenated()
    {
        await using var server = new LocalHttpServer();
        using var temp = new TempDirectory();
        var key = TestData.Random(16, seed: 40);
        var explicitIv = TestData.Random(16, seed: 41);
        var plain = new[]
        {
            TestData.Random(50_000, seed: 42),
            TestData.Random(70_001, seed: 43),
            TestData.Random(12_345, seed: 44),
        };

        // Segment 0 uses an explicit IV; segments 1 and 2 use IVs based on the sequence number (1 and 2).
        var encrypted = new[]
        {
            Encrypt(plain[0], key, explicitIv),
            Encrypt(plain[1], key, SequenceIv(1)),
            Encrypt(plain[2], key, SequenceIv(2)),
        };

        var playlist = $"""
            #EXTM3U
            #EXT-X-VERSION:3
            #EXT-X-TARGETDURATION:4
            #EXT-X-MEDIA-SEQUENCE:0
            #EXT-X-KEY:METHOD=AES-128,URI="/key.bin",IV=0x{Convert.ToHexString(explicitIv)}
            #EXTINF:4.0,
            /s0.ts
            #EXT-X-KEY:METHOD=AES-128,URI="/key.bin"
            #EXTINF:4.0,
            /s1.ts
            #EXTINF:4.0,
            /s2.ts
            #EXT-X-ENDLIST
            """;

        server.Handler = req => req.Target switch
        {
            "/media.m3u8" => Text(playlist),
            "/key.bin" => Bytes(key),
            "/s0.ts" => Bytes(encrypted[0]),
            "/s1.ts" => Bytes(encrypted[1]),
            "/s2.ts" => Bytes(encrypted[2]),
            _ => new ServerResponse { Status = 404, Reason = "Not Found" },
        };

        using var downloader = new HlsDownloader();
        var progress = new List<long>();
        var output = await downloader.DownloadAsync(new Uri(server.BaseUrl + "/media.m3u8"), temp.Path, "stream",
            new HlsDownloadOptions { ConvertToMp4 = false }, bytes => progress.Add(bytes), CancellationToken.None);

        Assert.EndsWith("stream.ts", output);
        var expected = plain.SelectMany(p => p).ToArray();
        Assert.Equal(expected, File.ReadAllBytes(output));
        Assert.Equal(expected.Length, progress[^1]);
    }

    [Fact]
    public async Task MasterPlaylist_PicksHighestBandwidthByDefault()
    {
        await using var server = new LocalHttpServer();
        using var temp = new TempDirectory();
        var low = TestData.Random(20_000, seed: 50);
        var high = TestData.Random(30_000, seed: 51);

        var master = """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=300000,RESOLUTION=640x360
            /low.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=1200000,RESOLUTION=1280x720
            /high.m3u8
            """;

        server.Handler = req => req.Target switch
        {
            "/master.m3u8" => Text(master),
            "/low.m3u8" => Text("#EXTM3U\n#EXTINF:2,\n/r0.ts\n#EXT-X-ENDLIST\n"),
            "/high.m3u8" => Text("#EXTM3U\n#EXTINF:2,\n/t0.ts\n#EXT-X-ENDLIST\n"),
            "/r0.ts" => Bytes(low),
            "/t0.ts" => Bytes(high),
            _ => new ServerResponse { Status = 404, Reason = "Not Found" },
        };

        using var downloader = new HlsDownloader();
        var output = await downloader.DownloadAsync(new Uri(server.BaseUrl + "/master.m3u8"), temp.Path, "video",
            new HlsDownloadOptions { ConvertToMp4 = false }, null, CancellationToken.None);

        Assert.Equal(high, File.ReadAllBytes(output));
    }

    [Fact]
    public async Task LowestPreference_PicksLowestBandwidth()
    {
        await using var server = new LocalHttpServer();
        using var temp = new TempDirectory();
        var low = TestData.Random(8_000, seed: 52);

        server.Handler = req => req.Target switch
        {
            "/master.m3u8" => Text("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=300000\n/low.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=900000\n/high.m3u8\n"),
            "/low.m3u8" => Text("#EXTM3U\n#EXTINF:2,\n/r0.ts\n#EXT-X-ENDLIST\n"),
            "/r0.ts" => Bytes(low),
            _ => new ServerResponse { Status = 404, Reason = "Not Found" },
        };

        using var downloader = new HlsDownloader();
        var output = await downloader.DownloadAsync(new Uri(server.BaseUrl + "/master.m3u8"), temp.Path, "video",
            new HlsDownloadOptions { ConvertToMp4 = false, VariantPreference = HlsVariantPreference.Lowest },
            null, CancellationToken.None);

        Assert.Equal(low, File.ReadAllBytes(output));
    }

    [Fact]
    public async Task SampleAesFmp4_IsRejectedAsUnsupported()
    {
        await using var server = new LocalHttpServer();
        using var temp = new TempDirectory();
        server.Handler = _ => Text("#EXTM3U\n#EXT-X-MAP:URI=\"/init.mp4\"\n#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"/k\"\n#EXTINF:2,\n/s.m4s\n#EXT-X-ENDLIST\n");

        using var downloader = new HlsDownloader();
        var ex = await Assert.ThrowsAsync<TransferException>(() =>
            downloader.DownloadAsync(new Uri(server.BaseUrl + "/a.m3u8"), temp.Path, "x",
                new HlsDownloadOptions { ConvertToMp4 = false }, null, CancellationToken.None));

        Assert.Equal(184, ex.Code);
        Assert.False(ex.Retryable);
    }

    [Fact]
    public async Task HttpErrorOnSegment_IsReportedWithStatusCode()
    {
        await using var server = new LocalHttpServer();
        using var temp = new TempDirectory();
        server.Handler = req => req.Target == "/a.m3u8"
            ? Text("#EXTM3U\n#EXTINF:2,\n/missing.ts\n#EXT-X-ENDLIST\n")
            : new ServerResponse { Status = 404, Reason = "Not Found" };

        using var downloader = new HlsDownloader();
        var ex = await Assert.ThrowsAsync<TransferException>(() =>
            downloader.DownloadAsync(new Uri(server.BaseUrl + "/a.m3u8"), temp.Path, "x",
                new HlsDownloadOptions { ConvertToMp4 = false }, null, CancellationToken.None));

        Assert.Equal(404, ex.Code);
    }

    [Fact]
    public async Task ConvertToMp4_WithFfmpeg_ProducesMp4()
    {
        if (!new MediaMerger().IsAvailable) return; // ffmpeg not on PATH: conversion test skipped.

        using var temp = new TempDirectory();
        var tsPath = Path.Combine(temp.Path, "src.ts");
        RunFfmpeg("-y", "-f", "lavfi", "-i", "testsrc=duration=2:size=160x120:rate=10",
            "-c:v", "mpeg2video", "-f", "mpegts", tsPath);
        var tsBytes = File.ReadAllBytes(tsPath);

        await using var server = new LocalHttpServer();
        server.Handler = req => req.Target switch
        {
            "/media.m3u8" => Text("#EXTM3U\n#EXTINF:2,\n/seg.ts\n#EXT-X-ENDLIST\n"),
            "/seg.ts" => Bytes(tsBytes),
            _ => new ServerResponse { Status = 404, Reason = "Not Found" },
        };

        using var downloader = new HlsDownloader();
        var output = await downloader.DownloadAsync(new Uri(server.BaseUrl + "/media.m3u8"), temp.Path, "result",
            new HlsDownloadOptions { ConvertToMp4 = true }, null, CancellationToken.None);

        Assert.EndsWith("result.mp4", output);
        Assert.False(File.Exists(Path.Combine(temp.Path, "result.ts")));
        var header = File.ReadAllBytes(output).AsSpan(4, 4).ToArray();
        Assert.Equal("ftyp", Encoding.ASCII.GetString(header));
    }

    private static ServerResponse Text(string text) => new()
    {
        Status = 200,
        Reason = "OK",
        Body = Encoding.UTF8.GetBytes(text),
    };

    private static ServerResponse Bytes(byte[] data) => new()
    {
        Status = 200,
        Reason = "OK",
        Body = data,
    };

    internal static void RunFfmpeg(params string[] args)
    {
        var startInfo = new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var arg in args) startInfo.ArgumentList.Add(arg);

        using var process = Process.Start(startInfo)!;
        var stderr = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(60_000), "ffmpeg timed out.");
        Assert.True(process.ExitCode == 0, $"ffmpeg failed: {stderr}");
    }
}

public sealed class MediaMergerTests
{
    [Fact]
    public void MergeArguments_MapVideoAndAudioWithoutReencoding()
    {
        var args = MediaMerger.BuildMergeArguments("v.ts", "a.aac", "out.mp4");

        Assert.Equal(new[] { "0:v:0", "1:a:0" }, args.Where(a => a.Contains(":0")).ToArray());
        var array = args.ToArray();
        Assert.Equal("copy", array[Array.IndexOf(array, "-c") + 1]);
        Assert.Equal("out.mp4", array[^1]);
        Assert.Equal(2, array.Count(a => a == "-i"));
    }

    [Fact]
    public void ConvertArguments_CopyStreamsIntoMp4()
    {
        var args = MediaMerger.BuildConvertArguments("in.ts", "out.mp4");

        var array = args.ToArray();
        Assert.Contains("in.ts", array);
        Assert.Equal("copy", array[Array.IndexOf(array, "-c") + 1]);
        Assert.Equal("out.mp4", array[^1]);
    }

    [Fact]
    public async Task ExplicitMissingFfmpegPath_SurfacesProcessError()
    {
        var merger = new MediaMerger(Path.Combine(Path.GetTempPath(), "missing-ffmpeg-" + Guid.NewGuid().ToString("N") + ".exe"));

        Assert.True(merger.IsAvailable);
        await Assert.ThrowsAnyAsync<System.ComponentModel.Win32Exception>(() =>
            merger.ConvertTsToMp4Async("a.ts", "b.mp4"));
    }

    [Fact]
    public async Task MergeAudioAndVideo_WithFfmpeg_ProducesMp4()
    {
        if (!new MediaMerger().IsAvailable) return; // ffmpeg not on PATH: merge test skipped.

        using var temp = new TempDirectory();
        var video = Path.Combine(temp.Path, "v.ts");
        var audio = Path.Combine(temp.Path, "a.aac");
        var output = Path.Combine(temp.Path, "merged.mp4");
        HlsDownloaderTests.RunFfmpeg("-y", "-f", "lavfi", "-i", "testsrc=duration=2:size=160x120:rate=10",
            "-c:v", "mpeg2video", "-f", "mpegts", video);
        HlsDownloaderTests.RunFfmpeg("-y", "-f", "lavfi", "-i", "sine=frequency=440:duration=2",
            "-c:a", "aac", "-f", "adts", audio);

        await new MediaMerger().MergeAsync(video, audio, output);

        Assert.True(new FileInfo(output).Length > 0);
    }
}
