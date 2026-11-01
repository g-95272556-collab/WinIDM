namespace WinIDM.Engine;

/// <summary>
/// Setting keys read by the download engine. Key names follow the 03a-jadual-tetapan.md table
/// so WinIDM settings files can be moved over directly. Extra WinIDM keys
/// (with no IDM+ equivalent) use the <c>winidm_</c> prefix.
/// </summary>
public static class SettingKeys
{
    /// <summary>Concurrent downloads. Effective value = value + 1 (see 01 section 5.1 and the engine README).</summary>
    public const string DownloadCount = "idm_pref_download_count";

    /// <summary>Number of parts (segments) per file. Effective value = value + 1 (see 01 section 3.1 and the engine README).</summary>
    public const string DownloadPart = "idm_pref_download_part";

    public const string Retry = "idm_pref_retry";
    public const string RetryCount = "idm_pref_retry_count";
    public const string RetryInterval = "idm_pref_retry_interval";
    public const string AlwaysRetryDownload = "always_retry_download";
    public const string Timeout = "idm_pref_timeout";
    public const string WifiOnly = "idm_pref_wifi";

    /// <summary>Global speed limit enabled. The <see cref="SpeedLimit"/> value is in KiB/s.</summary>
    public const string SpeedLimiter = "idm_pref_speed_limiter";
    public const string SpeedLimiterCascade = "idm_pref_speed_limiter_cascade";

    /// <summary>Global speed limit in KiB/s (1 KiB = 1024 bytes). Only enforced while <see cref="SpeedLimiter"/> is on.</summary>
    public const string SpeedLimit = "idm_pref_speed_limit";

    public const string AdvanceDownloadMethod = "idm_pref_advance_download_method";
    public const string CreateEmptyFile = "idm_pref_create_empty_file";
    public const string DisableCataloguing = "idm_pref_disable_cataloguing";
    public const string UserAgent = "idm_pref_user_agent";
    public const string ProxyEnabled = "idm_pref_proxy";

    /// <summary>Proxy type: 0 = HTTP/HTTPS (CONNECT for HTTPS targets), 1 = SOCKS5. See <see cref="ProxyTypes"/>.</summary>
    public const string ProxyType = "idm_pref_proxy_type";

    public const string ProxyHost = "idm_pref_proxy_host";
    public const string ProxyPort = "idm_pref_proxy_port";
    public const string ProxyUser = "idm_pref_proxy_user";

    /// <summary>Proxy password. Secret: encrypted with DPAPI on disk (see <see cref="IsSecret"/>).</summary>
    public const string ProxyPass = "idm_pref_proxy_pass";

    public const string ConvertTsToMp4 = "idm_convert_ts_files_ffmpeg";

    /// <summary>Default download folder. WinIDM key (not in 03a); the default is %USERPROFILE%\Downloads.</summary>
    public const string DefaultSaveDirectory = "winidm_default_save_directory";

    /// <summary>Move files after completion (03a: idm_pref_move_files_download_finish).</summary>
    public const string MoveFilesOnFinish = "idm_pref_move_files_download_finish";

    /// <summary>Move destination path (03a: idm_pref_move_files_download_finish_path).</summary>
    public const string MoveFilesPath = "idm_pref_move_files_download_finish_path";

    /// <summary>When on, files are moved into category subfolders under <see cref="MoveFilesPath"/>. WinIDM key.</summary>
    public const string MoveFilesByCategory = "winidm_move_files_by_category";

    /// <summary>
    /// Extra move rules as a JSON array, for example <c>[{"category":"Video","folder":"D:\\Movies"}]</c>.
    /// A matching rule takes precedence over moving by category. WinIDM key.
    /// </summary>
    public const string MoveRules = "winidm_move_rules";

    /// <summary>Remove the download link (record) after completion; the file is not deleted (03a: idm_pref_remove_link_download_finish).</summary>
    public const string RemoveLinkOnFinish = "idm_pref_remove_link_download_finish";

    /// <summary>Remove old links after N days; -1 means off (03a: idm_pref_delete_links_after_days).</summary>
    public const string DeleteLinksAfterDays = "idm_pref_delete_links_after_days";

    /// <summary>If true, only completed downloads are removed after N days (03a: idm_pref_delete_links_after_days_only_finished).</summary>
    public const string DeleteLinksOnlyFinished = "idm_pref_delete_links_after_days_only_finished";

    /// <summary>
    /// System action after all downloads finish: <see cref="SystemAction"/> (enum name). The engine only raises
    /// the event; the App performs it after user confirmation. WinIDM key.
    /// </summary>
    public const string AfterDownloadsAction = "winidm_after_downloads_action";

    /// <summary>
    /// HLS live stream: if true, record until the download is stopped (or ENDLIST appears).
    /// If false, only the current segments in the playlist are recorded. WinIDM key.
    /// </summary>
    public const string HlsRecordLiveUntilStopped = "winidm_hls_record_live_until_stopped";

    /// <summary>
    /// Pause downloads while the network is marked metered (<see cref="INetworkPolicy.IsMetered"/>). WinIDM key.
    /// Downloads paused this way go back to the queue and resume once the connection is no longer metered.
    /// </summary>
    public const string PauseOnMetered = "winidm_pause_on_metered";

    /// <summary>Daily data limit (MiB) on metered networks; 0 = no limit. WinIDM key.</summary>
    public const string MeteredDailyLimitMb = "winidm_metered_daily_limit_mb";

    /// <summary>Monthly data limit (MiB) on metered networks; 0 = no limit. WinIDM key.</summary>
    public const string MeteredMonthlyLimitMb = "winidm_metered_monthly_limit_mb";

    /// <summary>Persistent count: date (yyyy-MM-dd, local time) for <see cref="MeteredUsageDayBytes"/>. Managed by the engine.</summary>
    public const string MeteredUsageDay = "winidm_metered_usage_day";

    /// <summary>Persistent count: metered bytes for the day in <see cref="MeteredUsageDay"/>. Managed by the engine.</summary>
    public const string MeteredUsageDayBytes = "winidm_metered_usage_day_bytes";

    /// <summary>Persistent count: month (yyyy-MM, local time) for <see cref="MeteredUsageMonthBytes"/>. Managed by the engine.</summary>
    public const string MeteredUsageMonth = "winidm_metered_usage_month";

    /// <summary>Persistent count: metered bytes for the month in <see cref="MeteredUsageMonth"/>. Managed by the engine.</summary>
    public const string MeteredUsageMonthBytes = "winidm_metered_usage_month_bytes";

    /// <summary>Keys whose value is secret and encrypted with DPAPI on disk by <see cref="JsonSettingsStore"/>.</summary>
    public static bool IsSecret(string key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        return key == ProxyPass ||
               key.EndsWith("_pass", StringComparison.Ordinal) ||
               key.EndsWith("_password", StringComparison.Ordinal) ||
               key.EndsWith("_token", StringComparison.Ordinal) ||
               key.EndsWith("_secret", StringComparison.Ordinal);
    }
}

/// <summary>Values for <see cref="SettingKeys.ProxyType"/>.</summary>
public static class ProxyTypes
{
    /// <summary>HTTP proxy. HTTPS targets go through CONNECT (the proxy does not decrypt traffic).</summary>
    public const int Http = 0;

    /// <summary>SOCKS5 proxy with username/password auth (RFC 1928 and RFC 1929). The host name is sent to the proxy.</summary>
    public const int Socks5 = 1;

    /// <summary>
    /// HTTPS proxy: a TLS connection to the proxy itself (<c>https://</c> scheme in <see cref="System.Net.WebProxy"/>),
    /// then CONNECT or an absolute-form request through the proxy. The proxy certificate is validated against the system certificate store.
    /// </summary>
    public const int HttpsTls = 2;
}

/// <summary>System actions the App can request after all downloads finish. The engine does not perform them.</summary>
public enum SystemAction
{
    None,
    Shutdown,
    Restart,
    Sleep,
    Hibernate,
    LockWorkstation,
    CloseApplication,
}

/// <summary>
/// Engine setting defaults, taken from 03a-jadual-tetapan.md. These are the raw values
/// that are stored (not the effective values); the engine adds 1 for concurrency and part counts.
/// </summary>
public static class SettingDefaults
{
    public const int DownloadCount = 2;
    public const int DownloadPart = 7;

    /// <summary>Effective concurrent downloads at defaults = <see cref="DownloadCount"/> + 1 = 3 (01 section 5.1).</summary>
    public const int EffectiveDownloadCount = DownloadCount + 1;

    /// <summary>Effective part count at defaults = <see cref="DownloadPart"/> + 1 = 8 (01 section 3.1).</summary>
    public const int EffectivePartCount = DownloadPart + 1;

    public const bool Retry = true;
    public const int RetryCount = 999999;
    public const int RetryIntervalSeconds = 5;
    public const bool AlwaysRetryDownload = false;
    public const int TimeoutSeconds = 60;
    public const bool WifiOnly = false;
    public const bool SpeedLimiter = false;
    public const bool SpeedLimiterCascade = false;

    /// <summary>
    /// Global speed limit in KiB/s (verified: the engine multiplies by 1024 to get bytes per second).
    /// A value of 10240 means 10 MiB/s.
    /// </summary>
    public const int SpeedLimitKibPerSecond = 10240;

    public const bool AdvanceDownloadMethod = true;
    public const bool CreateEmptyFile = true;
    public const bool DisableCataloguing = false;
    public const string UserAgent = "";
    public const bool ProxyEnabled = false;
    public const int ProxyType = ProxyTypes.Http;
    public const string ProxyHost = "";
    public const int ProxyPort = 0;
    public const string ProxyUser = "";
    public const string ProxyPass = "";
    public const bool ConvertTsToMp4 = true;
    public const string DefaultSaveDirectory = "";

    public const bool MoveFilesOnFinish = false;
    public const string MoveFilesPath = "";
    public const bool MoveFilesByCategory = false;
    public const string MoveRules = "[]";
    public const bool RemoveLinkOnFinish = false;
    public const int DeleteLinksAfterDays = -1;
    public const bool DeleteLinksOnlyFinished = true;
    public const string AfterDownloadsAction = "None";
    public const bool HlsRecordLiveUntilStopped = false;
    public const bool PauseOnMetered = false;
    public const int MeteredDailyLimitMb = 0;
    public const int MeteredMonthlyLimitMb = 0;

    /// <summary>Maximum part count allowed by the engine.</summary>
    public const int MaxParts = 32;

    /// <summary>Part limit when the advanced method is on and the file is large.</summary>
    public const int MaxPartsAdvanced = 64;
}
