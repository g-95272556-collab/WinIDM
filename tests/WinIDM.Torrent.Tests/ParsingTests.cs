using MonoTorrent;
using MonoTorrent.Client;
using WinIDM.Core.Models;
using WinIDM.Torrent.Tests.Support;
using MTorrent = MonoTorrent.Torrent;

namespace WinIDM.Torrent.Tests;

public class ParsingTests : IDisposable
{
    private const string Hash = "0123456789abcdef0123456789abcdef01234567";

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Magnet_ParsesHexInfoHashNameAndTrackers()
    {
        var uri = $"magnet:?xt=urn:btih:{Hash}&dn=Test+Name&tr=udp%3A%2F%2Ftracker.invalid%3A80";

        var link = MagnetLink.Parse(uri);

        Assert.Equal(Hash, link.InfoHashes.V1OrV2.ToHex(), ignoreCase: true);
        Assert.Equal("Test Name", link.Name);
        Assert.Contains(link.AnnounceUrls, u => u.Contains("tracker.invalid"));
    }

    [Fact]
    public void Magnet_ParsesBase32InfoHash()
    {
        var hex = Hash;
        var bytes = Convert.FromHexString(hex);
        var base32 = Base32(bytes);

        var link = MagnetLink.Parse($"magnet:?xt=urn:btih:{base32}");

        Assert.Equal(hex, link.InfoHashes.V1OrV2.ToHex(), ignoreCase: true);
    }

    [Fact]
    public void Magnet_TryParseRejectsGarbage()
    {
        Assert.False(MagnetLink.TryParse("not-a-magnet", out _));
    }

    [Fact]
    public void Magnet_WithoutInfoHash_IsRejected()
    {
        Assert.False(MagnetLink.TryParse("magnet:?dn=No+hash", out _));
    }

    [Fact]
    public async Task GeneratedTorrent_ParsesNameFilesAndSize()
    {
        var files = new[]
        {
            new TestFile("data/alpha.bin", TestTorrent.Bytes32(1, 40_000)),
            new TestFile("data/beta.bin", TestTorrent.Bytes32(2, 5_000)),
            new TestFile("readme.txt", TestTorrent.Bytes32(3, 123)),
        };
        var torrent = TestTorrent.Create(_temp.Path, "Example Torrent", files);

        var parsed = await MTorrent.LoadAsync(torrent.Path);

        Assert.Equal("Example Torrent", parsed.Name);
        Assert.Equal(3, parsed.Files.Count);
        Assert.Equal(torrent.TotalLength, parsed.Size);
        Assert.Equal(torrent.InfoHash, parsed.InfoHashes.V1OrV2.ToHex(), ignoreCase: true);
        Assert.Equal(3, parsed.PieceCount); // 40123 bytes / 16384 = 3 pieces
    }

    [Fact]
    public void DifferentContent_ProducesDifferentInfoHash()
    {
        var files = new[] { new TestFile("a.bin", TestTorrent.Bytes32(9, 20_000)) };
        var torrent = TestTorrent.Create(_temp.Path, "tamper", files);

        var parsedOriginal = MTorrent.Load(torrent.Bytes);
        var altered = TestTorrent.Create(_temp.Path, "tamper2", new[] { new TestFile("a.bin", TestTorrent.Bytes32(10, 20_000)) });
        var parsedAltered = MTorrent.Load(altered.Bytes);

        Assert.NotEqual(parsedOriginal.InfoHashes.V1OrV2.ToHex(), parsedAltered.InfoHashes.V1OrV2.ToHex());
    }

    [Fact]
    public void Priority_MappingRoundTripsIgnoreNormalHigh()
    {
        Assert.Equal(Priority.DoNotDownload, TorrentMapping.ToMonoPriority(TorrentFilePriority.Ignore));
        Assert.Equal(Priority.Normal, TorrentMapping.ToMonoPriority(TorrentFilePriority.Normal));
        Assert.Equal(Priority.High, TorrentMapping.ToMonoPriority(TorrentFilePriority.High));

        Assert.Equal(TorrentFilePriority.Ignore, TorrentMapping.FromMonoPriority(Priority.DoNotDownload));
        Assert.Equal(TorrentFilePriority.Normal, TorrentMapping.FromMonoPriority(Priority.Low));
        Assert.Equal(TorrentFilePriority.High, TorrentMapping.FromMonoPriority(Priority.Immediate));
    }

    [Theory]
    [InlineData(MonoTorrent.Client.TorrentState.Downloading, false, 40.0, DownloadState.Downloading)]
    [InlineData(MonoTorrent.Client.TorrentState.Seeding, false, 100.0, DownloadState.Completed)]
    [InlineData(MonoTorrent.Client.TorrentState.Stopped, false, 100.0, DownloadState.Completed)]
    [InlineData(MonoTorrent.Client.TorrentState.Stopped, false, 10.0, DownloadState.Paused)]
    [InlineData(MonoTorrent.Client.TorrentState.Downloading, true, 10.0, DownloadState.Paused)]
    [InlineData(MonoTorrent.Client.TorrentState.Metadata, false, 0.0, DownloadState.Connecting)]
    [InlineData(MonoTorrent.Client.TorrentState.Error, false, 0.0, DownloadState.Error)]
    public void TorrentState_MapsToDownloadState(MonoTorrent.Client.TorrentState state, bool paused, double progress, DownloadState expected)
    {
        Assert.Equal(expected, TorrentMapping.ToDownloadState(state, paused, progress, hasError: false));
    }

    [Fact]
    public void TorrentState_WithErrorFlag_IsError()
    {
        Assert.Equal(DownloadState.Error, TorrentMapping.ToDownloadState(MonoTorrent.Client.TorrentState.Downloading, false, 50, hasError: true));
    }

    private static string Base32(byte[] data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var sb = new System.Text.StringBuilder();
        var buffer = 0;
        var bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }
}
