using System.Security.Cryptography;
using System.Text;
using WinIDM.Core.Models;

namespace WinIDM.Browser.Tests;

public class MediaDetectorTests
{
    private const string Page = "https://site.test/watch";

    [Fact]
    public void Direct_video_above_minimum_size_is_detected()
    {
        var d = new MediaDetector();
        var media = d.Inspect("https://cdn.test/files/clip.mp4", null, 5_000_000, Page);
        Assert.NotNull(media);
        Assert.Equal(DownloadKind.Http, media!.Kind);
        Assert.Equal("clip.mp4", media.Title);
        Assert.Equal(5_000_000, media.SizeBytes);
        Assert.Equal(Page, media.PageUrl);
    }

    [Fact]
    public void Files_below_ten_kilobytes_are_not_media()
    {
        var d = new MediaDetector();
        Assert.Null(d.Inspect("https://cdn.test/a.mp4", "video/mp4", 2048, Page));
        Assert.NotNull(d.Inspect("https://cdn.test/a.mp4", "video/mp4", 10 * 1024, Page));
        Assert.NotNull(d.Inspect("https://cdn.test/a.mp4", "video/mp4", null, Page));
    }

    [Theory]
    [InlineData("https://x.test/a.bin", "audio/mpeg")]
    [InlineData("https://x.test/a", "video/webm; codecs=vp9")]
    [InlineData("https://x.test/a.mp3", "application/octet-stream")]
    public void Mime_or_extension_identifies_audio_and_video(string url, string mime)
    {
        var d = new MediaDetector();
        Assert.NotNull(d.Inspect(url, mime, 50_000, Page));
    }

    [Theory]
    [InlineData("https://x.test/index.html", "text/html", 50_000)]
    [InlineData("https://x.test/a.png", "image/png", 50_000)]
    [InlineData("https://x.test/app.js", "application/javascript", 50_000)]
    [InlineData("https://x.test/notes.txt", null, 50_000)]
    [InlineData("data:video/mp4;base64,AAAA", "video/mp4", 50_000)]
    [InlineData("file:///C:/a.mp4", null, 50_000)]
    public void Non_media_urls_and_types_are_rejected(string url, string? mime, long size)
    {
        var d = new MediaDetector();
        Assert.Null(d.Inspect(url, mime, size, Page));
    }

    [Fact]
    public void Playlists_bypass_minimum_size_and_map_to_hls_or_dash()
    {
        var d = new MediaDetector();
        var hls = d.Inspect("https://x.test/master.m3u8", "application/vnd.apple.mpegurl", 120, Page);
        Assert.NotNull(hls);
        Assert.Equal(DownloadKind.Hls, hls!.Kind);

        var dash = d.Inspect("https://x.test/stream.mpd", null, 300, Page);
        Assert.NotNull(dash);
        Assert.Equal(DownloadKind.Dash, dash!.Kind);
    }

    [Fact]
    public void User_disabled_type_overrides_built_in_detection()
    {
        var d = new MediaDetector();
        d.SetUserTypes(new[] { "!mp4", "!image/png" });
        Assert.Null(d.Inspect("https://x.test/a.mp4", "video/mp4", 50_000, Page));
        Assert.Null(d.Inspect("https://x.test/a.bin", "image/png", 50_000, Page));
        Assert.NotNull(d.Inspect("https://x.test/a.webm", "video/webm", 50_000, Page));
        Assert.Equal(new[] { "!mp4", "!image/png" }, d.GetUserTypes());
    }

    [Fact]
    public void User_enabled_type_adds_detection_globally_and_per_page()
    {
        var d = new MediaDetector();
        d.SetUserTypes(new[] { ".xyz" });
        var global = d.Inspect("https://x.test/a.xyz", null, 50_000, "https://other.test/");
        Assert.NotNull(global);
        Assert.Equal(DownloadKind.Http, global!.Kind);

        d.SetUserTypes(new[] { "application/x-custom" }, "site.test");
        Assert.NotNull(d.Inspect("https://x.test/a.bin", "application/x-custom", 50_000, "https://www.site.test/p"));
        Assert.Null(d.Inspect("https://x.test/a.bin", "application/x-custom", 50_000, "https://other.test/"));
        Assert.Single(d.GetUserTypes("site.test"));
    }

    [Fact]
    public void Page_specific_deny_wins_over_global_allow()
    {
        var d = new MediaDetector();
        d.SetUserTypes(new[] { "bin" });
        d.SetUserTypes(new[] { "!bin" }, "blocked.test");
        Assert.NotNull(d.Inspect("https://x.test/a.bin", null, 50_000, "https://other.test/"));
        Assert.Null(d.Inspect("https://x.test/a.bin", null, 50_000, "https://blocked.test/"));
    }

    [Fact]
    public void Invalid_user_entries_are_ignored()
    {
        var d = new MediaDetector();
        d.SetUserTypes(new[] { "", "!", "bad ext", "a/b c", "averyveryverylongextension" });
        Assert.Empty(d.GetUserTypes());
    }

    [Fact]
    public void Minimum_size_is_configurable_and_validated()
    {
        var d = new MediaDetector { MinimumSizeBytes = 0 };
        Assert.NotNull(d.Inspect("https://x.test/a.mp4", null, 1, Page));
        Assert.Throws<ArgumentOutOfRangeException>(() => d.MinimumSizeBytes = -1);
        Assert.Equal(0, d.MinimumSizeBytes);
    }

    [Fact]
    public void Classify_and_normalize_helpers_cover_edge_cases()
    {
        Assert.Equal(DownloadKind.Hls, MediaDetector.Classify("", "application/x-mpegurl"));
        Assert.Equal(DownloadKind.Dash, MediaDetector.Classify("", "application/dash+xml"));
        Assert.Equal(DownloadKind.Http, MediaDetector.Classify("ts", ""));
        Assert.Null(MediaDetector.Classify("", "application/xml"));
        Assert.Equal("", MediaDetector.NormalizeMime("   "));
        Assert.Equal("video/mp4", MediaDetector.NormalizeMime("Video/MP4; codecs=avc1"));
        Assert.Equal("", MediaDetector.NormalizeMime("application/octet-stream"));
        Assert.Equal("", MediaDetector.ExtensionOf(new Uri("https://x.test/")));
    }

    // ---------------------------------------------------------------------
    // HLS

    private const string MasterPlaylist = """
#EXTM3U
#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="aud",NAME="English",LANGUAGE="en",DEFAULT=YES,URI="audio/en.m3u8"
#EXT-X-STREAM-INF:BANDWIDTH=3000000,AVERAGE-BANDWIDTH=2500000,RESOLUTION=1280x720,CODECS="avc1.64001f,mp4a.40.2",AUDIO="aud"
v720.m3u8
#EXT-X-STREAM-INF:BANDWIDTH=1000000,RESOLUTION=640x360,CODECS="avc1.42c01e,mp4a.40.2"
https://cdn.test/v360.m3u8
#EXT-X-STREAM-INF:BANDWIDTH=128000,CODECS="mp4a.40.2"
audio-only.m3u8

""";

    [Fact]
    public void Master_playlist_yields_variants_with_resolution_bandwidth_and_audio_flag()
    {
        var pl = HlsPlaylist.Parse(MasterPlaylist, new Uri("https://site.test/path/master.m3u8"));
        Assert.True(pl.IsMaster);
        Assert.Equal(3, pl.Variants.Count);
        Assert.Single(pl.Renditions);
        Assert.Equal("AUDIO", pl.Renditions[0].Type);
        Assert.Equal("https://site.test/path/audio/en.m3u8", pl.Renditions[0].Uri!.AbsoluteUri);
        Assert.True(pl.Renditions[0].IsDefault);

        var v720 = pl.Variants[0];
        Assert.Equal("https://site.test/path/v720.m3u8", v720.Uri.AbsoluteUri);
        Assert.Equal(1280, v720.Width);
        Assert.Equal(720, v720.Height);
        Assert.Equal(2_500_000, v720.EffectiveBitsPerSecond);
        Assert.False(v720.AudioOnly);

        var v360 = pl.Variants[1];
        Assert.Equal("https://cdn.test/v360.m3u8", v360.Uri.AbsoluteUri);
        Assert.Equal((long)(1_000_000 * 0.8), v360.EffectiveBitsPerSecond);

        Assert.True(pl.Variants[2].AudioOnly);
    }

    private const string MediaPlaylist = """
#EXTM3U
#EXT-X-VERSION:3
#EXT-X-TARGETDURATION:10
#EXT-X-MEDIA-SEQUENCE:7
#EXT-X-MAP:URI="init.mp4"
#EXT-X-KEY:METHOD=AES-128,URI="https://keys.test/k1.bin",IV=0x000000000000000000000000000000AB
#EXTINF:10.0,
seg7.ts
#EXTINF:9.5,
seg8.ts
#EXT-X-KEY:METHOD=NONE
#EXTINF:4.5,
#EXT-X-BYTERANGE:1000@0
range.ts
#EXT-X-BYTERANGE:500
range.ts
#EXT-X-ENDLIST
""";

    [Fact]
    public void Media_playlist_has_segments_keys_map_and_byteranges()
    {
        var pl = HlsPlaylist.Parse(MediaPlaylist, new Uri("https://site.test/a/media.m3u8"));
        Assert.False(pl.IsMaster);
        Assert.True(pl.HasEndList);
        Assert.Equal(7, pl.MediaSequence);
        Assert.Equal("https://site.test/a/init.mp4", pl.MapUri!.AbsoluteUri);
        Assert.Equal(4, pl.Segments.Count);
        Assert.Equal(7, pl.Segments[0].SequenceNumber);
        Assert.Equal(10.0, pl.Segments[0].DurationSeconds);
        Assert.Equal("AES-128", pl.Segments[0].Key!.Method);
        Assert.Equal("https://keys.test/k1.bin", pl.Segments[0].Key!.Uri!.AbsoluteUri);
        Assert.Equal(0xAB, pl.Segments[0].Key!.Iv![15]);
        Assert.Equal("NONE", pl.Segments[2].Key!.Method);

        Assert.Equal(1000, pl.Segments[2].ByteLength);
        Assert.Equal(0, pl.Segments[2].ByteOffset);
        Assert.Equal(500, pl.Segments[3].ByteLength);
        Assert.Equal(1000, pl.Segments[3].ByteOffset);

        Assert.True(pl.IsEncrypted);
        Assert.False(pl.HasUnsupportedEncryption);
        Assert.Equal(10.0 + 9.5 + 4.5 + 0, pl.TotalDurationSeconds, 3);
    }

    [Fact]
    public void Sample_aes_is_flagged_as_unsupported_encryption()
    {
        var pl = HlsPlaylist.Parse("#EXTM3U\n#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"k\"\n#EXTINF:5,\na.ts\n", new Uri("https://x.test/p.m3u8"));
        Assert.True(pl.HasUnsupportedEncryption);
    }

    [Fact]
    public void Playlist_without_header_is_rejected()
    {
        Assert.Throws<FormatException>(() => HlsPlaylist.Parse("<html></html>", new Uri("https://x.test/")));
    }

    [Fact]
    public void Aes128_round_trip_and_iv_derivation()
    {
        byte[] key = RandomNumberGenerator.GetBytes(16);
        byte[] iv = HlsAes128.DeriveIv(42);
        Assert.Equal(16, iv.Length);
        Assert.Equal(42, iv[15]);

        byte[] plain = Encoding.UTF8.GetBytes("segment payload 0123456789");
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        byte[] cipher = aes.EncryptCbc(plain, iv, System.Security.Cryptography.PaddingMode.PKCS7);

        byte[] decrypted = HlsAes128.Decrypt(cipher, key, iv);
        Assert.Equal(plain, decrypted);

        Assert.Throws<ArgumentException>(() => HlsAes128.Decrypt(cipher, new byte[8], iv));
        Assert.Throws<ArgumentException>(() => HlsAes128.Decrypt(cipher, key, new byte[4]));
        Assert.Throws<CryptographicException>(() => HlsAes128.Decrypt(new byte[7], key, iv));
    }

    [Fact]
    public void Hex_iv_parsing_handles_prefix_and_invalid_values()
    {
        Assert.Null(HlsPlaylist.ParseHex(null));
        Assert.Null(HlsPlaylist.ParseHex("0xABC"));
        Assert.Null(HlsPlaylist.ParseHex("0xZZ"));
        Assert.Equal(new byte[] { 0xAB, 0xCD }, HlsPlaylist.ParseHex("0xABCD"));
    }

    // ---------------------------------------------------------------------
    // DASH

    private const string Mpd = """
<?xml version="1.0" encoding="UTF-8"?>
<MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT1M30S">
  <Period>
    <AdaptationSet mimeType="video/mp4" codecs="avc1.4d401f" contentType="video">
      <Representation id="v1" bandwidth="4500000" width="1920" height="1080" />
      <Representation id="v2" bandwidth="1500000" width="1280" height="720" />
    </AdaptationSet>
    <AdaptationSet mimeType="audio/mp4">
      <Representation id="a1" bandwidth="128000" codecs="mp4a.40.2" />
    </AdaptationSet>
  </Period>
</MPD>
""";

    [Fact]
    public void Dash_manifest_lists_representations_and_duration()
    {
        var info = DashManifest.Parse(Mpd);
        Assert.Equal(90, info.DurationSeconds);
        Assert.Equal(3, info.Representations.Count);
        var v1 = info.Representations[0];
        Assert.Equal("v1", v1.Id);
        Assert.Equal("video", v1.ContentType);
        Assert.Equal(1920, v1.Width);
        Assert.Equal("1920x1080 (4500 kbps)", v1.Quality);
        Assert.Equal("audio", info.Representations[2].ContentType);
        Assert.Equal("audio (128 kbps)", info.Representations[2].Quality);
    }

    [Theory]
    [InlineData("PT1M30S", 90.0)]
    [InlineData("PT1H2M3.5S", 3723.5)]
    [InlineData("P1DT1S", 86401.0)]
    [InlineData("PT90.5S", 90.5)]
    [InlineData("P1Y", null)]
    [InlineData("nonsense", null)]
    [InlineData("", null)]
    public void Iso_duration_parsing(string text, double? expected)
    {
        Assert.Equal(expected, DashManifest.ParseIsoDuration(text));
    }

    [Fact]
    public void Dash_rejects_doctype_entities_and_non_mpd_roots()
    {
        const string xxe = "<?xml version=\"1.0\"?><!DOCTYPE MPD [<!ENTITY x SYSTEM \"file:///C:/secret\">]><MPD>&x;</MPD>";
        Assert.ThrowsAny<System.Xml.XmlException>(() => DashManifest.Parse(xxe));
        Assert.Throws<FormatException>(() => DashManifest.Parse("<Other/>"));
    }

    [Fact]
    public void Dash_without_namespace_and_without_duration_is_parsed()
    {
        var info = DashManifest.Parse("<MPD><Period><AdaptationSet mimeType=\"video/mp4\" width=\"640\" height=\"360\"><Representation id=\"r\" bandwidth=\"800000\"/></AdaptationSet></Period></MPD>");
        Assert.Null(info.DurationSeconds);
        Assert.Equal(640, info.Representations[0].Width);
        Assert.Equal(360, info.Representations[0].Height);
    }

    // ---------------------------------------------------------------------
    // Playlist expansion over local HTTP

    [Fact]
    public async Task ExpandPlaylist_master_estimates_sizes_from_variant_durations()
    {
        const string variant = "#EXTM3U\n#EXTINF:10,\na.ts\n#EXTINF:10,\nb.ts\n#EXTINF:10,\nc.ts\n#EXT-X-ENDLIST\n";
        using var server = new TestHttpServer(new Dictionary<string, Func<HttpCall, HttpReply>>
        {
            ["/master.m3u8"] = _ => HttpReply.Text(
                "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=800000,AVERAGE-BANDWIDTH=640000,RESOLUTION=640x360\nlow.m3u8\n" +
                "#EXT-X-STREAM-INF:BANDWIDTH=2000000,AVERAGE-BANDWIDTH=1600000,RESOLUTION=1280x720\nhigh.m3u8\n",
                contentType: "application/vnd.apple.mpegurl"),
            ["/low.m3u8"] = _ => HttpReply.Text(variant),
            ["/high.m3u8"] = _ => HttpReply.Text(variant),
        });

        var d = new MediaDetector();
        var results = await d.ExpandPlaylistAsync(server.BaseUrl + "/master.m3u8");

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(DownloadKind.Hls, r.Kind));
        Assert.Equal(server.BaseUrl + "/high.m3u8", results[0].Url);
        Assert.Equal((long)(1_600_000 / 8.0 * 30), results[0].SizeBytes);
        Assert.Equal((long)(640_000 / 8.0 * 30), results[1].SizeBytes);
        Assert.Contains("1280x720", results[0].Quality);
    }

    [Fact]
    public async Task ExpandPlaylist_media_playlist_returns_single_hls_entry_and_skips_unknown_content()
    {
        using var server = new TestHttpServer(new Dictionary<string, Func<HttpCall, HttpReply>>
        {
            ["/media.m3u8"] = _ => HttpReply.Text("#EXTM3U\n#EXTINF:5,\na.ts\n#EXTINF:5,\nb.ts\n"),
            ["/plain.txt"] = _ => HttpReply.Text("hello"),
            ["/stream.mpd"] = _ => HttpReply.Text(Mpd, contentType: "application/dash+xml"),
            ["/broken"] = _ => HttpReply.Text("missing", 500),
        });

        var d = new MediaDetector();
        var single = await d.ExpandPlaylistAsync(server.BaseUrl + "/media.m3u8");
        Assert.Single(single);
        Assert.Equal(DownloadKind.Hls, single[0].Kind);
        Assert.Contains("2 segments", single[0].Quality);

        Assert.Empty(await d.ExpandPlaylistAsync(server.BaseUrl + "/plain.txt"));
        Assert.Empty(await d.ExpandPlaylistAsync("ftp://x.test/a.m3u8"));
        await Assert.ThrowsAsync<HttpRequestException>(() => d.ExpandPlaylistAsync(server.BaseUrl + "/broken"));

        var dash = await d.ExpandPlaylistAsync(server.BaseUrl + "/stream.mpd");
        Assert.Equal(3, dash.Count);
        Assert.Equal(DownloadKind.Dash, dash[0].Kind);
        Assert.Equal((long)(4_500_000 / 8.0 * 90), dash[0].SizeBytes);
        Assert.Contains("[id v1]", dash[0].Quality);
    }

    [Fact]
    public async Task ExpandPlaylist_survives_unreachable_variant_playlist()
    {
        using var server = new TestHttpServer(new Dictionary<string, Func<HttpCall, HttpReply>>
        {
            ["/m.m3u8"] = _ => HttpReply.Text("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=900000,RESOLUTION=854x480\nmissing.m3u8\n"),
        });
        var results = await new MediaDetector().ExpandPlaylistAsync(server.BaseUrl + "/m.m3u8");
        Assert.Single(results);
        Assert.Null(results[0].SizeBytes);
    }
}
