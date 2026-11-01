using MonoTorrent.Connections;
using WinIDM.Torrent.Tests.Support;

namespace WinIDM.Torrent.Tests;

public class TorrentConfigurationTests
{
    [Fact]
    public void EmptySettings_UseDocumentedDefaults()
    {
        var config = TorrentConfiguration.FromSettings(new MemorySettingsStore());

        Assert.False(config.Disabled);
        Assert.True(config.UseRandomPort);
        Assert.True(config.EnableDht);
        Assert.True(config.EnableLsd);
        Assert.True(config.EnablePex);
        Assert.False(config.EnableUtp);
        Assert.True(config.EnableUpnp);
        Assert.True(config.EnableNatPmp);
        Assert.Equal(500, config.MaxConnections);
        Assert.Equal(500, config.MaxPeersPerTorrent);
        Assert.True(config.FetchMetadata);
        Assert.False(config.SequentialDownload);
        Assert.False(config.SeedFinishedTorrents);
        Assert.False(config.EnableIpFiltering);
        Assert.Equal(EncryptionPolicy.Prefer, config.IncomingEncryption);
        Assert.Equal(EncryptionPolicy.Prefer, config.OutgoingEncryption);
        Assert.Equal(EncryptionLevel.Both, config.EncryptionLevel);

        // Download limit is off (the speed limiter defaults to false); upload limit is 10240 KB/s.
        Assert.Equal(0, config.GlobalDownloadLimit);
        Assert.Equal(10240 * 1024, config.GlobalUploadLimit);
    }

    [Fact]
    public void Settings_AreReadFromTorrentKeys()
    {
        var store = new MemorySettingsStore()
            .Put("enable_dht", false)
            .Put("enable_lsd", false)
            .Put("enable_pex", false)
            .Put("enable_upnp", false)
            .Put("enable_nat_pmp", false)
            .Put("use_random_port", false)
            .Put("custom_port_number", 50123)
            .Put("max_connection", 42)
            .Put("max_peers", 7)
            .Put("sequential_download", true)
            .Put("seed_finished_torrent", true)
            .Put("idm_pref_fetch_metadata", false)
            .Put("idm_pref_torrent_user_agent", "WinIDM/1.0")
            .Put("idm_pref_speed_limiter", true)
            .Put("idm_pref_speed_limit", 100)
            .Put("idm_pref_up_speed_limit", 50);

        var config = TorrentConfiguration.FromSettings(store);

        Assert.False(config.EnableDht);
        Assert.False(config.EnableLsd);
        Assert.False(config.EnablePex);
        Assert.False(config.EnableUpnp);
        Assert.False(config.EnableNatPmp);
        Assert.False(config.UseRandomPort);
        Assert.Equal(50123, config.CustomPort);
        Assert.Equal(42, config.MaxConnections);
        Assert.Equal(7, config.MaxPeersPerTorrent);
        Assert.True(config.SequentialDownload);
        Assert.True(config.SeedFinishedTorrents);
        Assert.False(config.FetchMetadata);
        Assert.Equal("WinIDM/1.0", config.UserAgent);
        Assert.Equal(100 * 1024, config.GlobalDownloadLimit);
        Assert.Equal(50 * 1024, config.GlobalUploadLimit);
    }

    [Fact]
    public void DownloadLimit_IgnoredWhenSpeedLimiterDisabled()
    {
        var store = new MemorySettingsStore()
            .Put("idm_pref_speed_limiter", false)
            .Put("idm_pref_speed_limit", 500);

        var config = TorrentConfiguration.FromSettings(store);

        Assert.Equal(0, config.GlobalDownloadLimit);
    }

    [Fact]
    public void InvalidCustomPort_FallsBackToDefault()
    {
        var config = TorrentConfiguration.FromSettings(new MemorySettingsStore()
            .Put("use_random_port", false)
            .Put("custom_port_number", 80));

        Assert.Equal(TorrentConfiguration.DefaultPort, config.CustomPort);
    }

    [Fact]
    public void DisableTorrentSetting_IsExposed()
    {
        var config = TorrentConfiguration.FromSettings(new MemorySettingsStore().Put("disable_torrent", true));

        Assert.True(config.Disabled);
    }

    [Theory]
    // Legacy Malay labels stay accepted so settings saved by earlier builds keep parsing.
    [InlineData("Prefer encryption", EncryptionPolicy.Prefer)]
    [InlineData("Utamakan enkripsi", EncryptionPolicy.Prefer)]
    [InlineData("Require encryption", EncryptionPolicy.Require)]
    [InlineData("Wajib", EncryptionPolicy.Require)]
    [InlineData("Disable encryption", EncryptionPolicy.Disable)]
    [InlineData("Lumpuh", EncryptionPolicy.Disable)]
    [InlineData("unknown", EncryptionPolicy.Prefer)]
    [InlineData(null, EncryptionPolicy.Prefer)]
    public void EncryptionPolicy_ParsesEnglishAndMalayLabels(string? text, EncryptionPolicy expected)
    {
        Assert.Equal(expected, TorrentConfiguration.ParseEncryptionPolicy(text, EncryptionPolicy.Prefer));
    }

    [Fact]
    public void Prefer_AllowsEncryptedAndPlaintextInPreferenceOrder()
    {
        var config = TorrentConfiguration.FromSettings(new MemorySettingsStore());

        Assert.Equal(
            new[] { EncryptionType.RC4Header, EncryptionType.RC4Full, EncryptionType.PlainText },
            config.AllowedEncryption());
    }

    [Fact]
    public void Require_ExcludesPlaintext()
    {
        var config = TorrentConfiguration.FromSettings(new MemorySettingsStore()
            .Put("encrypt_incoming_connection", "Require encryption")
            .Put("encrypt_outgoing_connection", "Prefer encryption"));

        var allowed = config.AllowedEncryption();

        Assert.DoesNotContain(EncryptionType.PlainText, allowed);
        Assert.Contains(EncryptionType.RC4Full, allowed);
    }

    [Fact]
    public void BothDisabled_AllowsPlaintextOnly()
    {
        var config = TorrentConfiguration.FromSettings(new MemorySettingsStore()
            .Put("encrypt_incoming_connection", "Disable encryption")
            .Put("encrypt_outgoing_connection", "Disable encryption"));

        Assert.Equal(new[] { EncryptionType.PlainText }, config.AllowedEncryption());
    }

    [Theory]
    [InlineData("Full RC4", EncryptionType.RC4Full, EncryptionType.RC4Header)]
    [InlineData("Header only", EncryptionType.RC4Header, EncryptionType.RC4Full)]
    public void EncryptionLevel_RestrictsRc4Variant(string level, EncryptionType kept, EncryptionType dropped)
    {
        var config = TorrentConfiguration.FromSettings(new MemorySettingsStore()
            .Put("encrypt_level_connection", level));

        var allowed = config.AllowedEncryption();

        Assert.Contains(kept, allowed);
        Assert.DoesNotContain(dropped, allowed);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-5, 0)]
    [InlineData(1, 1024)]
    [InlineData(10240, 10240 * 1024)]
    public void KilobytesToBytes_ConvertsAndClampsAtZero(long kb, int expected)
    {
        Assert.Equal(expected, TorrentConfiguration.KilobytesToBytesPerSecond(kb));
    }

    [Fact]
    public void KilobytesToBytes_ClampsToInt32Max()
    {
        Assert.Equal(int.MaxValue, TorrentConfiguration.KilobytesToBytesPerSecond(long.MaxValue / 2));
    }
}
