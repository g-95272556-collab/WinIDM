using MonoTorrent.Connections;
using WinIDM.Core.Contracts;

namespace WinIDM.Torrent;

/// <summary>Torrent setting keys (see 03a-jadual-tetapan.md, section Downloads - Torrent).</summary>
public static class TorrentSettingKeys
{
    public const string DisableTorrent = "disable_torrent";
    public const string UserAgent = "idm_pref_torrent_user_agent";
    public const string UseRandomPort = "use_random_port";
    public const string CustomPortNumber = "custom_port_number";
    public const string FetchMetadata = "idm_pref_fetch_metadata";
    public const string EnableDht = "enable_dht";
    public const string EnableLsd = "enable_lsd";
    public const string EnablePex = "enable_pex";
    public const string EnableUtp = "enable_utp";
    public const string EnableUpnp = "enable_upnp";
    public const string EnableNatPmp = "enable_nat_pmp";
    public const string SequentialDownload = "sequential_download";
    public const string SeedFinishedTorrents = "seed_finished_torrent";
    public const string EncryptIncoming = "encrypt_incoming_connection";
    public const string EncryptOutgoing = "encrypt_outgoing_connection";
    public const string EncryptLevel = "encrypt_level_connection";
    public const string MaxConnection = "max_connection";
    public const string MaxPeers = "max_peers";
    public const string EnableIpFiltering = "enable_ip_filtering";
    public const string IpFilterPath = "ip_filter_path";
    public const string ValidateHttpsTrackers = "validate_https_trackers";
    public const string AlwaysContactAllTrackers = "acatracker";
    public const string AddTrackersToDuplicates = "idm_pref_add_tracker_auto";
    public const string UseDefaultTrackers = "default_trackers";
    public const string TrackerListUrl = "dturl";

    // Global speed-limit keys (general Downloads section). Unit: KB/s.
    public const string SpeedLimiterEnabled = "idm_pref_speed_limiter";
    public const string SpeedLimitKilobytes = "idm_pref_speed_limit";
    public const string UploadSpeedLimitKilobytes = "idm_pref_up_speed_limit";
}

/// <summary>Peer connection encryption policy.</summary>
public enum EncryptionPolicy { Prefer, Require, Disable }

/// <summary>RC4 encryption levels allowed when encryption is enabled.</summary>
public enum EncryptionLevel { Both, HeaderOnly, FullOnly }

/// <summary>
/// Torrent configuration read from <see cref="ISettingsStore"/>.
/// Defaults follow 03a-jadual-tetapan.md.
/// </summary>
public sealed class TorrentConfiguration
{
    public const int DefaultPort = 6881;
    public const int DefaultMaxConnections = 500;

    public bool Disabled { get; private init; }
    public string UserAgent { get; private init; } = "";
    public bool UseRandomPort { get; private init; } = true;
    public int CustomPort { get; private init; } = DefaultPort;
    public bool EnableDht { get; private init; } = true;
    public bool EnableLsd { get; private init; } = true;
    public bool EnablePex { get; private init; } = true;

    /// <summary>Setting is stored, but MonoTorrent 3.0.2 does not support uTP.</summary>
    public bool EnableUtp { get; private init; }
    public bool EnableUpnp { get; private init; } = true;
    public bool EnableNatPmp { get; private init; } = true;
    public EncryptionPolicy IncomingEncryption { get; private init; } = EncryptionPolicy.Prefer;
    public EncryptionPolicy OutgoingEncryption { get; private init; } = EncryptionPolicy.Prefer;
    public EncryptionLevel EncryptionLevel { get; private init; } = EncryptionLevel.Both;
    public int MaxConnections { get; private init; } = DefaultMaxConnections;
    public int MaxPeersPerTorrent { get; private init; } = DefaultMaxConnections;
    public bool FetchMetadata { get; private init; } = true;
    public bool SequentialDownload { get; private init; }
    public bool SeedFinishedTorrents { get; private init; }
    public bool EnableIpFiltering { get; private init; }
    public string IpFilterPath { get; private init; } = "";

    /// <summary>Setting is stored, but MonoTorrent 3.0.2 has no HTTPS tracker certificate validation switch.</summary>
    public bool ValidateHttpsTrackers { get; private init; }

    /// <summary>Announce to every tracker, not only the first working one of each tier (03a: acatracker, default on).</summary>
    public bool AlwaysContactAllTrackers { get; private init; } = true;

    /// <summary>Adding a torrent that already exists merges its trackers into the existing one instead of failing.</summary>
    public bool AddTrackersToDuplicates { get; private init; }

    /// <summary>Add the built-in public tracker list to new torrents.</summary>
    public bool UseDefaultTrackers { get; private init; }

    /// <summary>Plain-text tracker list (one URL per line) added to new torrents; empty = none.</summary>
    public string TrackerListUrl { get; private init; } = "";

    /// <summary>Global download limit in bytes/second. 0 means unlimited.</summary>
    public int GlobalDownloadLimit { get; private init; }

    /// <summary>Global upload limit in bytes/second. 0 means unlimited.</summary>
    public int GlobalUploadLimit { get; private init; }

    /// <summary>Read all torrent keys from settings. Missing keys use the defaults.</summary>
    public static TorrentConfiguration FromSettings(ISettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        var port = store.Get(TorrentSettingKeys.CustomPortNumber, DefaultPort);
        if (port < 1024 || port > 65535) port = DefaultPort;

        var maxConnections = store.Get(TorrentSettingKeys.MaxConnection, DefaultMaxConnections);
        var maxPeers = store.Get(TorrentSettingKeys.MaxPeers, DefaultMaxConnections);

        var speedLimiter = store.Get(TorrentSettingKeys.SpeedLimiterEnabled, false);
        var downKb = store.Get(TorrentSettingKeys.SpeedLimitKilobytes, 10240);
        var upKb = store.Get(TorrentSettingKeys.UploadSpeedLimitKilobytes, 10240);

        return new TorrentConfiguration
        {
            Disabled = store.Get(TorrentSettingKeys.DisableTorrent, false),
            UserAgent = (store.Get<string?>(TorrentSettingKeys.UserAgent, null) ?? "").Trim(),
            UseRandomPort = store.Get(TorrentSettingKeys.UseRandomPort, true),
            CustomPort = port,
            EnableDht = store.Get(TorrentSettingKeys.EnableDht, true),
            EnableLsd = store.Get(TorrentSettingKeys.EnableLsd, true),
            EnablePex = store.Get(TorrentSettingKeys.EnablePex, true),
            EnableUtp = store.Get(TorrentSettingKeys.EnableUtp, false),
            EnableUpnp = store.Get(TorrentSettingKeys.EnableUpnp, true),
            EnableNatPmp = store.Get(TorrentSettingKeys.EnableNatPmp, true),
            IncomingEncryption = ParseEncryptionPolicy(store.Get<string?>(TorrentSettingKeys.EncryptIncoming, null), EncryptionPolicy.Prefer),
            OutgoingEncryption = ParseEncryptionPolicy(store.Get<string?>(TorrentSettingKeys.EncryptOutgoing, null), EncryptionPolicy.Prefer),
            EncryptionLevel = ParseEncryptionLevel(store.Get<string?>(TorrentSettingKeys.EncryptLevel, null)),
            MaxConnections = Math.Max(1, maxConnections),
            MaxPeersPerTorrent = Math.Max(1, maxPeers),
            FetchMetadata = store.Get(TorrentSettingKeys.FetchMetadata, true),
            SequentialDownload = store.Get(TorrentSettingKeys.SequentialDownload, false),
            SeedFinishedTorrents = store.Get(TorrentSettingKeys.SeedFinishedTorrents, false),
            EnableIpFiltering = store.Get(TorrentSettingKeys.EnableIpFiltering, false),
            IpFilterPath = (store.Get<string?>(TorrentSettingKeys.IpFilterPath, null) ?? "").Trim(),
            ValidateHttpsTrackers = store.Get(TorrentSettingKeys.ValidateHttpsTrackers, false),
            AlwaysContactAllTrackers = store.Get(TorrentSettingKeys.AlwaysContactAllTrackers, true),
            AddTrackersToDuplicates = store.Get(TorrentSettingKeys.AddTrackersToDuplicates, false),
            UseDefaultTrackers = store.Get(TorrentSettingKeys.UseDefaultTrackers, false),
            TrackerListUrl = (store.Get<string?>(TorrentSettingKeys.TrackerListUrl, null) ?? "").Trim(),
            GlobalDownloadLimit = speedLimiter ? KilobytesToBytesPerSecond(downKb) : 0,
            GlobalUploadLimit = KilobytesToBytesPerSecond(upKb),
        };
    }

    /// <summary>
    /// Encryption list allowed for MonoTorrent. Encryption and RC4 level are combined
    /// because MonoTorrent 3.0.2 does not separate incoming and outgoing policy.
    /// </summary>
    public IReadOnlyList<EncryptionType> AllowedEncryption()
    {
        var anyRequire = IncomingEncryption == EncryptionPolicy.Require || OutgoingEncryption == EncryptionPolicy.Require;
        var bothDisable = IncomingEncryption == EncryptionPolicy.Disable && OutgoingEncryption == EncryptionPolicy.Disable;

        var allowPlain = !anyRequire;
        var allowEncrypted = !bothDisable;

        var list = new List<EncryptionType>(3);
        if (allowEncrypted)
        {
            if (EncryptionLevel != EncryptionLevel.FullOnly) list.Add(EncryptionType.RC4Header);
            if (EncryptionLevel != EncryptionLevel.HeaderOnly) list.Add(EncryptionType.RC4Full);
        }
        if (allowPlain) list.Add(EncryptionType.PlainText);

        return list;
    }

    /// <summary>Translate encryption policy text (English or legacy Malay) to the enum. Unknown text uses <paramref name="fallback"/>.</summary>
    public static EncryptionPolicy ParseEncryptionPolicy(string? text, EncryptionPolicy fallback)
    {
        var value = (text ?? "").Trim().ToLowerInvariant();
        if (value.Length == 0) return fallback;
        // Legacy Malay values are still accepted so settings saved by earlier builds keep parsing.
        if (value.Contains("require") || value.Contains("wajib")) return EncryptionPolicy.Require;
        if (value.Contains("disable") || value.Contains("lumpuh")) return EncryptionPolicy.Disable;
        if (value.Contains("prefer") || value.Contains("utamakan")) return EncryptionPolicy.Prefer;
        return fallback;
    }

    /// <summary>Translate the encryption level. "Full" means full RC4 only; "Header" means header only; anything else means both. Legacy Malay values are still accepted.</summary>
    public static EncryptionLevel ParseEncryptionLevel(string? text)
    {
        var value = (text ?? "").Trim().ToLowerInvariant();
        if (value.Contains("full") || value.Contains("penuh")) return EncryptionLevel.FullOnly;
        if (value.Contains("header")) return EncryptionLevel.HeaderOnly;
        return EncryptionLevel.Both;
    }

    /// <summary>Convert KB/s (setting unit) to bytes/second, clamped to Int32.</summary>
    public static int KilobytesToBytesPerSecond(long kilobytes)
    {
        if (kilobytes <= 0) return 0;
        if (kilobytes > int.MaxValue / 1024) return int.MaxValue;
        return (int)(kilobytes * 1024L);
    }
}
