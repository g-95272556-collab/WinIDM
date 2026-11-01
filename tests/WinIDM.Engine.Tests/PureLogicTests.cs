using WinIDM.Core.Models;
using WinIDM.Engine;
using WinIDM.Engine.Tests.Support;

namespace WinIDM.Engine.Tests;

public sealed class SegmentPlannerTests
{
    private const long Mib = 1024 * 1024;

    [Theory]
    [InlineData(1, 8, 1)]
    [InlineData(2 * Mib, 8, 1)]
    [InlineData(5 * Mib, 8, 3)]
    [InlineData(100 * Mib, 8, 8)]
    [InlineData(100 * Mib, 40, 32)]
    public void CountSegments_FollowsBlockRule(long length, int requested, int expected)
    {
        Assert.Equal(expected, SegmentPlanner.CountSegments(length, requested, domainPartLimit: 0, advanced: false));
    }

    [Fact]
    public void Plan_CoversEveryByteExactlyOnce()
    {
        const long length = 100_000_001;

        var segments = SegmentPlanner.Plan(length, 8, domainPartLimit: 0, advanced: false);

        Assert.Equal(8, segments.Count);
        Assert.Equal(0, segments[0].Start);
        Assert.Equal(length - 1, segments[^1].End);
        for (var i = 1; i < segments.Count; i++)
        {
            Assert.Equal(segments[i - 1].End + 1, segments[i].Start);
        }

        Assert.Equal(length, segments.Sum(s => s.End - s.Start + 1));
        Assert.All(segments, s => Assert.Equal(0, s.Downloaded));
    }

    [Fact]
    public void Plan_UnknownLength_ReturnsSingleOpenSegment()
    {
        var segments = SegmentPlanner.Plan(-1, 8);

        Assert.Single(segments);
        Assert.Equal(-1, segments[0].End);
    }

    [Fact]
    public void DomainLimit_CapsSegmentCount()
    {
        Assert.Equal(2, SegmentPlanner.CountSegments(100 * Mib, 8, domainPartLimit: 2, advanced: false));
    }

    [Fact]
    public void AdvancedMethod_DoublesSegmentsForLargeFiles_UpTo64()
    {
        Assert.Equal(16, SegmentPlanner.CountSegments(64 * Mib, 8, 0, advanced: true));
        Assert.Equal(8, SegmentPlanner.CountSegments(64 * Mib, 8, 0, advanced: false));
        Assert.Equal(4, SegmentPlanner.CountSegments(64 * Mib, 8, domainPartLimit: 4, advanced: true));
        Assert.Equal(64, SegmentPlanner.CountSegments(1024 * Mib, 40, 0, advanced: true));
    }

    [Fact]
    public void Plan_EmptyFile_IsSingleSegmentOfZeroLength()
    {
        var segments = SegmentPlanner.Plan(0, 8);

        Assert.Single(segments);
        Assert.Equal(-1, segments[0].End);
    }
}

public sealed class FileNameResolverTests
{
    [Fact]
    public void ContentDispositionFilenameStar_IsPreferred()
    {
        var name = FileNameResolver.FromContentDisposition("attachment; filename=\"plain.txt\"; filename*=UTF-8''annual%20report.pdf");

        Assert.Equal("annual report.pdf", name);
    }

    [Fact]
    public void ContentDispositionFilename_IsUsedWhenNoStar()
    {
        Assert.Equal("data.csv", FileNameResolver.FromContentDisposition("attachment; filename=data.csv"));
        Assert.Null(FileNameResolver.FromContentDisposition(null));
        Assert.Null(FileNameResolver.FromContentDisposition("not a header"));
    }

    [Fact]
    public void UrlSegment_IgnoresQueryAndSemicolon()
    {
        var uri = new Uri("https://example.test/download/file%20one.zip;jsid=1?x=2#frag");

        Assert.Equal("file one.zip", FileNameResolver.FromUrl(uri));
    }

    [Fact]
    public void Resolve_PrefersUserNameThenDispositionThenUrl()
    {
        var uri = new Uri("https://example.test/a/index.html");

        Assert.Equal("choice.pdf", FileNameResolver.Resolve(uri, "attachment; filename=server.pdf", "application/pdf", "choice.pdf"));
        Assert.Equal("server.pdf", FileNameResolver.Resolve(uri, "attachment; filename=server.pdf", "application/pdf"));
        Assert.Equal("index.html", FileNameResolver.Resolve(uri, null, "text/html"));
    }

    [Fact]
    public void Resolve_AddsMimeExtensionWhenMissing()
    {
        var uri = new Uri("https://example.test/download");

        Assert.Equal("download.mp4", FileNameResolver.Resolve(uri, null, "video/mp4; charset=binary"));
        Assert.Equal("name.zip", FileNameResolver.Resolve(uri, null, "application/zip", "name"));
        Assert.Equal("name.zip", FileNameResolver.Resolve(uri, null, "application/zip", "name.zip"));
        Assert.Equal("download", FileNameResolver.Resolve(new Uri("https://example.test/"), null, "application/x-unknown"));
    }

    [Fact]
    public void Resolve_UsesHtmlTitleForHtmlResponses()
    {
        var uri = new Uri("https://example.test/");

        Assert.Equal("Page Title.html", FileNameResolver.Resolve(uri, null, "text/html", null, "Page Title"));
    }

    [Fact]
    public void Sanitize_ReplacesSeparatorsAndInvalidCharacters()
    {
        Assert.Equal("a-b-c.txt", FileNameResolver.Sanitize("a/b\\c.txt"));
        Assert.Equal("x-y-z", FileNameResolver.Sanitize("x:y*z"));
        Assert.Equal("name", FileNameResolver.Sanitize("name. . "));
        Assert.Null(FileNameResolver.Sanitize("   "));
        Assert.Equal("_CON.txt", FileNameResolver.Sanitize("CON.txt"));
    }

    [Fact]
    public void Sanitize_LimitsLengthKeepingExtension()
    {
        var longName = new string('a', 400) + ".mkv";

        var result = FileNameResolver.Sanitize(longName)!;

        Assert.True(result.Length <= 200);
        Assert.EndsWith(".mkv", result);
    }

    [Fact]
    public void MakeUnique_AppendsCounterBeforeExtension()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "file.bin"), "original");
        File.WriteAllText(Path.Combine(temp.Path, "file (1).bin"), "second");

        var unique = FileNameResolver.MakeUnique(temp.Path, "file.bin");

        Assert.Equal("file (2).bin", unique);
        Assert.Equal("new.bin", FileNameResolver.MakeUnique(temp.Path, "new.bin"));
    }

    [Fact]
    public void ExtensionFromMime_UnknownReturnsNull()
    {
        Assert.Equal(".pdf", FileNameResolver.ExtensionFromMime("application/pdf"));
        Assert.Null(FileNameResolver.ExtensionFromMime("application/x-unknown"));
        Assert.Null(FileNameResolver.ExtensionFromMime(" "));
    }
}

public sealed class DownloadCategoriesTests
{
    [Theory]
    [InlineData("movie.MKV", "Video")]
    [InlineData("song.flac", "Audio")]
    [InlineData("photo.webp", "Image")]
    [InlineData("paper.pdf", "Document")]
    [InlineData("archive.7z", "Archive")]
    [InlineData("installer.msi", "Program")]
    [InlineData("no-extension", "Other")]
    [InlineData("file.xyz", "Other")]
    public void GetCategory_MapsExtensions(string fileName, string expected)
    {
        Assert.Equal(expected, DownloadCategories.GetCategory(fileName));
    }

    [Fact]
    public void SaveDirectory_UsesCategoryFolderUnderRoot()
    {
        Assert.Equal(Path.Combine("D:\\Downloads", "Video"), DownloadCategories.GetSaveDirectory("D:\\Downloads", "Video"));
        Assert.Equal(Path.Combine("D:\\Downloads", "Other"), DownloadCategories.GetSaveDirectory("D:\\Downloads", "Non-Category"));
        Assert.Equal(7, DownloadCategories.All.Count);
        Assert.Contains(".mp4", DownloadCategories.Map.Keys);
    }
}

public sealed class TokenBucketTests
{
    [Fact]
    public async Task Unlimited_ConsumesWithoutWaiting()
    {
        var bucket = new TokenBucket(0);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        await bucket.ConsumeAsync(10_000_000);

        Assert.True(watch.ElapsedMilliseconds < 200);
    }

    [Fact]
    public async Task Limited_ThrottlesToApproximateRate()
    {
        // 256 KiB/s: 512 KiB must take about 2 seconds.
        var bucket = new TokenBucket(256 * 1024);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        for (var i = 0; i < 8; i++) await bucket.ConsumeAsync(64 * 1024);

        Assert.InRange(watch.Elapsed.TotalSeconds, 1.5, 4.0);
    }

    [Fact]
    public void RateCanBeChangedAndClampsNegative()
    {
        var bucket = new TokenBucket(100);
        bucket.BytesPerSecond = -5;
        Assert.Equal(0, bucket.BytesPerSecond);
        bucket.BytesPerSecond = 2048;
        Assert.Equal(2048, bucket.BytesPerSecond);
    }
}

public sealed class M3u8PlaylistTests
{
    private static readonly Uri Base = new("https://example.test/stream/");

    [Fact]
    public void Parse_MasterPlaylist_ReadsVariants()
    {
        const string text = """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360
            low/index.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=2500000,RESOLUTION=1280x720,CODECS="avc1.4d401f,mp4a.40.2"
            high/index.m3u8
            """;

        var playlist = M3u8Playlist.Parse(text, Base);

        Assert.True(playlist.IsMaster);
        Assert.Equal(2, playlist.Variants.Count);
        Assert.Equal(2_500_000, playlist.Variants[1].Bandwidth);
        Assert.Equal("1280x720", playlist.Variants[1].Resolution);
        Assert.Equal(new Uri("https://example.test/stream/high/index.m3u8"), playlist.Variants[1].Uri);
    }

    [Fact]
    public void Parse_MediaPlaylist_ReadsKeysSequenceAndEndList()
    {
        const string text = """
            #EXTM3U
            #EXT-X-TARGETDURATION:4
            #EXT-X-MEDIA-SEQUENCE:7
            #EXT-X-KEY:METHOD=AES-128,URI="key.bin",IV=0x000102030405060708090A0B0C0D0E0F
            #EXTINF:4.0,
            seg0.ts
            #EXTINF:3.5,
            seg1.ts
            #EXT-X-ENDLIST
            """;

        var playlist = M3u8Playlist.Parse(text, Base);

        Assert.False(playlist.IsMaster);
        Assert.True(playlist.HasEndList);
        Assert.Equal(2, playlist.Segments.Count);
        Assert.Equal(7, playlist.Segments[0].Sequence);
        Assert.Equal(8, playlist.Segments[1].Sequence);
        Assert.Equal(3.5, playlist.Segments[1].Duration);
        var key = playlist.Segments[0].Key!;
        Assert.Equal("AES-128", key.Method);
        Assert.Equal(new Uri("https://example.test/stream/key.bin"), key.Uri);
        Assert.Equal(16, key.Iv!.Length);
        Assert.Equal(0x0F, key.Iv[15]);
    }

    [Fact]
    public void Parse_MethodNone_ClearsKey_AndMapIsFlagged()
    {
        const string text = """
            #EXTM3U
            #EXT-X-MAP:URI="init.mp4"
            #EXT-X-KEY:METHOD=NONE
            #EXTINF:2,
            a.m4s
            """;

        var playlist = M3u8Playlist.Parse(text, Base);

        Assert.True(playlist.HasInitializationMap);
        Assert.Null(playlist.Segments[0].Key);
    }

    [Fact]
    public void Parse_InvalidHeader_Throws()
    {
        Assert.Throws<FormatException>(() => M3u8Playlist.Parse("not m3u8", Base));
    }

    [Fact]
    public void Parse_InvalidIv_Throws()
    {
        const string text = """
            #EXTM3U
            #EXT-X-KEY:METHOD=AES-128,URI="k",IV=0x0102
            #EXTINF:1,
            s.ts
            """;

        Assert.Throws<FormatException>(() => M3u8Playlist.Parse(text, Base));
    }

    [Fact]
    public void ParseAttributes_HandlesQuotedCommas()
    {
        var attributes = M3u8Playlist.ParseAttributes("NAME=\"a,b\", BANDWIDTH=100 ,EMPTY=");

        Assert.Equal("a,b", attributes["NAME"]);
        Assert.Equal("100", attributes["BANDWIDTH"]);
        Assert.Equal(string.Empty, attributes["EMPTY"]);
    }
}
