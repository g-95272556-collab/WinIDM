namespace WinIDM.Browser.Tests;

/// <summary>Rules used when a media response is captured from the browser (YouTube ranges, file names, de-duplication).</summary>
public class MediaCaptureTests
{
    private const string YouTubeStream =
        "https://rr3---sn-a5meknsd.googlevideo.com/videoplayback?expire=1762000000" +
        "&ei=abc123&ip=203.0.113.9&id=o-AHxyz&itag=22&source=youtube&requiressl=yes" +
        "&mime=video%2Fmp4&ns=xyz&sparams=expire%2Cei%2Cip%2Cid%2Citag%2Csource%2Cmime" +
        "&sig=AFi7abcdef&n=Mn8vQ2&range=0-1048575&rn=4&rbuf=65536";

    [Fact]
    public void NormalizeForDownload_RemovesOnlyRangeParameters()
    {
        var normalized = MediaCapture.NormalizeForDownload(YouTubeStream);

        Assert.DoesNotContain("range=", normalized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rn=", normalized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rbuf=", normalized, StringComparison.OrdinalIgnoreCase);

        // Signature, token and stream identity must survive: without them the server rejects the request.
        Assert.Contains("sig=AFi7abcdef", normalized);
        Assert.Contains("n=Mn8vQ2", normalized);
        Assert.Contains("itag=22", normalized);
        Assert.Contains("mime=video%2Fmp4", normalized, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("https://rr3---sn-a5meknsd.googlevideo.com/videoplayback?", normalized);
    }

    [Fact]
    public void NormalizeForDownload_StripsByteRangesOnOtherHostsToo()
    {
        const string fb = "https://video.xx.fbcdn.net/v/t42.1790-2/abc.mp4?_nc_cat=1&range=0-999&oh=deadbeef&oe=FFFF";
        var normalized = MediaCapture.NormalizeForDownload(fb);

        // IDM+ strips a byte range before handing a capture to the downloader, on every host.
        Assert.DoesNotContain("range=", normalized, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("_nc_cat=1", normalized);
        Assert.Contains("oh=deadbeef", normalized);
        Assert.Contains("oe=FFFF", normalized);
    }

    [Fact]
    public void NormalizeForDownload_StripsByteStartAndByteEnd()
    {
        const string url = "https://cdn.example.test/clip.mp4?bytestart=1024&byteend=2047&token=t1";
        var normalized = MediaCapture.NormalizeForDownload(url);
        Assert.DoesNotContain("bytestart", normalized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("byteend", normalized, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("token=t1", normalized);
    }

    [Fact]
    public void NormalizeForDownload_KeepsParametersThatOnlyLookLikeRanges()
    {
        // A signed parameter that merely reuses the name must survive, otherwise the signature breaks.
        const string url = "https://cdn.example.test/clip.mp4?range=full&token=abc";
        Assert.Equal(url, MediaCapture.NormalizeForDownload(url));
    }

    [Fact]
    public void NormalizeForDownload_LeavesGooglevideoNonStreamPathsUntouched()
    {
        const string other = "https://rr1---sn-x.googlevideo.com/initplayback?source=youtube&token=abc";
        Assert.Equal(other, MediaCapture.NormalizeForDownload(other));
    }

    [Fact]
    public void NormalizeForDownload_LeavesUrlWithoutRangeUnchanged()
    {
        const string noRange = "https://rr1---sn-x.googlevideo.com/videoplayback?id=o-1&itag=18&sig=abc";
        Assert.Equal(noRange, MediaCapture.NormalizeForDownload(noRange));
    }

    [Fact]
    public void NormalizeForDownload_IgnoresNonHttpAndMalformedInput()
    {
        Assert.Equal("magnet:?xt=urn:btih:abc", MediaCapture.NormalizeForDownload("magnet:?xt=urn:btih:abc"));
        Assert.Equal("not a url", MediaCapture.NormalizeForDownload("not a url"));
        Assert.Equal("", MediaCapture.NormalizeForDownload(""));
    }

    [Fact]
    public void DedupeKey_CollapsesRangeVariantsOfTheSameStream()
    {
        var second = YouTubeStream.Replace("range=0-1048575", "range=1048576-2097151");
        Assert.NotEqual(YouTubeStream, second);
        Assert.Equal(MediaCapture.DedupeKey(YouTubeStream), MediaCapture.DedupeKey(second));
    }

    [Fact]
    public void IsYouTubeSource_OnlyForGooglevideoStreams()
    {
        Assert.True(MediaCapture.IsYouTubeSource(YouTubeStream));
        Assert.False(MediaCapture.IsYouTubeSource("https://www.youtube.com/watch?v=abc"));
        Assert.False(MediaCapture.IsYouTubeSource("https://video.xx.fbcdn.net/v/abc.mp4"));
    }

    [Fact]
    public void SuggestedFileName_YouTubeUsesThePageTitleAndMimeExtension()
    {
        var name = MediaCapture.SuggestedFileName(YouTubeStream, "My Video: Part 1?", null, "video/mp4");
        Assert.Equal("My Video Part 1.mp4", name);
    }

    [Fact]
    public void SuggestedFileName_AudioOnlyStreamKeepsItsAudioExtension()
    {
        var audioUrl = YouTubeStream.Replace("itag=22", "itag=140").Replace("mime=video%2Fmp4", "mime=audio%2Fmp4");
        var name = MediaCapture.SuggestedFileName(audioUrl, null, null, "audio/mp4");
        Assert.Equal("video-140.m4a", name);
    }

    [Fact]
    public void SuggestedFileName_PrefersARealFileNameInTheUrl()
    {
        const string fb = "https://video.xx.fbcdn.net/v/t42.1790-2/clip%20final.mp4?_nc_cat=1&oh=deadbeef";
        Assert.Equal("clip final.mp4", MediaCapture.SuggestedFileName(fb, "A post", null, "video/mp4"));
    }

    [Fact]
    public void SuggestedFileName_SanitizesInvalidCharactersAndStaysShort()
    {
        var title = new string('x', 200) + "<>:\"/\\|?*";
        var name = MediaCapture.SuggestedFileName(YouTubeStream, title, null, "video/mp4");
        Assert.True(name.Length <= 124, $"name is too long: {name.Length}");
        foreach (var ch in Path.GetInvalidFileNameChars())
            Assert.DoesNotContain(ch, name);
        Assert.EndsWith(".mp4", name);
    }

    [Fact]
    public void SuggestedFileName_FallsBackToAStableHash()
    {
        const string url = "https://cdn.example.test/stream/play";
        var first = MediaCapture.SuggestedFileName(url, null, null, null);
        var second = MediaCapture.SuggestedFileName(url, null, null, null);
        Assert.Equal(first, second);
        Assert.StartsWith("media-", first);
        Assert.EndsWith(".mp4", first);
    }

    [Fact]
    public void SuggestedFileName_UsesHlsAndDashExtensionsForPlaylists()
    {
        Assert.EndsWith(".m3u8", MediaCapture.SuggestedFileName("https://site.test/master.m3u8", "Show", null, "application/vnd.apple.mpegurl"));
        Assert.EndsWith(".mpd", MediaCapture.SuggestedFileName("https://site.test/manifest", "Show", null, "application/dash+xml"));
    }

    [Fact]
    public void FileNameFromContentDisposition_ParsesExtendedAndPlainForms()
    {
        Assert.Equal("My Film.mp4", MediaCapture.FileNameFromContentDisposition("attachment; filename*=UTF-8''My%20Film.mp4"));
        Assert.Equal("clip.mp4", MediaCapture.FileNameFromContentDisposition("attachment; filename=\"clip.mp4\""));
        Assert.Equal("song.m4a", MediaCapture.FileNameFromContentDisposition("inline; filename=song.m4a"));
        Assert.Null(MediaCapture.FileNameFromContentDisposition("attachment"));
        Assert.Null(MediaCapture.FileNameFromContentDisposition(null));
        Assert.Null(MediaCapture.FileNameFromContentDisposition("attachment; filename=\"\""));
    }

    [Fact]
    public void FileNameFromContentDisposition_KeepsOnlyTheFileName()
    {
        Assert.Equal("clip.mp4", MediaCapture.FileNameFromContentDisposition("attachment; filename=\"C:\\\\temp\\\\clip.mp4\""));
    }

    [Fact]
    public void SuggestedFileName_PrefersContentDispositionThenQueryTitle()
    {
        var withHeader = MediaCapture.SuggestedFileName(
            "https://cdn.test/stream?id=7", "Page title", null, "video/mp4",
            "attachment; filename*=UTF-8''Holiday%20clip");
        Assert.Equal("Holiday clip.mp4", withHeader);

        var withQuery = MediaCapture.SuggestedFileName(
            "https://cdn.test/stream?title=Live%20set&id=7", "Page title", null, "video/mp4");
        Assert.Equal("Live set.mp4", withQuery);

        var withQueryFile = MediaCapture.SuggestedFileName(
            "https://cdn.test/stream?filename=track", "Page title", null, "audio/mp4");
        Assert.Equal("track.m4a", withQueryFile);
    }

    [Fact]
    public void SuggestedFileName_ContentDispositionWithExtensionIsNotDoubled()
    {
        var name = MediaCapture.SuggestedFileName("https://cdn.test/stream", "Page", null, "video/mp4",
            "attachment; filename=\"already.webm\"");
        Assert.Equal("already.webm", name);
    }

    [Fact]
    public void SuggestedFileName_UsesANameAlreadyParsedFromContentDisposition()
    {
        // The browser stores the parsed header name on DetectedMedia.SourceFileName; it must win over the page title.
        var name = MediaCapture.SuggestedFileName("https://cdn.test/stream?id=7", "Page title", null, "video/mp4",
            sourceFileName: "Holiday clip");
        Assert.Equal("Holiday clip.mp4", name);

        // A parsed name is not percent-decoded a second time.
        Assert.Equal("100%25 done.mp4", MediaCapture.SuggestedFileName("https://cdn.test/stream", null, null, "video/mp4",
            sourceFileName: "100%25 done.mp4"));
    }

    [Fact]
    public void FileNameFromContentDisposition_KeepsSemicolonsInsideQuotes()
    {
        Assert.Equal("Tom; Jerry.mp4", MediaCapture.FileNameFromContentDisposition("attachment; filename=\"Tom; Jerry.mp4\""));
    }

    [Fact]
    public void NormalizeForDownload_KeepsEveryOtherParameterByteForByte()
    {
        const string signed = "https://cdn.example.test/v/%7Euser/clip.mp4?a=%7E1&empty=&flag&range=0-99&sig=x%2By#t=5";
        Assert.Equal("https://cdn.example.test/v/%7Euser/clip.mp4?a=%7E1&empty=&flag&sig=x%2By#t=5",
            MediaCapture.NormalizeForDownload(signed));

        // Exactly the range parameters go; the YouTube URL keeps every other parameter in order.
        Assert.Equal(
            "https://rr3---sn-a5meknsd.googlevideo.com/videoplayback?expire=1762000000" +
            "&ei=abc123&ip=203.0.113.9&id=o-AHxyz&itag=22&source=youtube&requiressl=yes" +
            "&mime=video%2Fmp4&ns=xyz&sparams=expire%2Cei%2Cip%2Cid%2Citag%2Csource%2Cmime" +
            "&sig=AFi7abcdef&n=Mn8vQ2",
            MediaCapture.NormalizeForDownload(YouTubeStream));
    }

    [Fact]
    public void NormalizeForDownload_DropsTheQuestionMarkWhenOnlyARangeWasPresent()
    {
        Assert.Equal("https://cdn.example.test/clip.mp4", MediaCapture.NormalizeForDownload("https://cdn.example.test/clip.mp4?range=0-99"));
    }

    [Fact]
    public void SuggestedFileName_LongNameKeepsItsExtension()
    {
        var url = "https://cdn.example.test/" + new string('a', 200) + ".webm";
        var name = MediaCapture.SuggestedFileName(url, null, null, "video/webm");
        Assert.EndsWith(".webm", name);
        Assert.True(name.Length <= 120, $"name is too long: {name.Length}");
    }

    [Theory]
    [InlineData("audio/x-wav", "wav")]
    [InlineData("video/x-flv", "flv")]
    [InlineData("audio/x-unknown", "m4a")]
    public void SuggestedFileName_MapsMoreMimeTypesAndDefaultsAudioToM4a(string mime, string extension)
    {
        Assert.EndsWith("." + extension, MediaCapture.SuggestedFileName("https://cdn.test/stream", "Track", null, mime));
    }
}
