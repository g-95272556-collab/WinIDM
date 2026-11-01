using WinIDM.Core.Contracts;
using WinIDM.Engine;

namespace WinIDM.App.Services;

/// <summary>Setting key constants used by the UI. The source of truth is <see cref="SettingsCatalog"/>.</summary>
public static class Keys
{
    public const string DnsMode = "dns_mode";
    public const string DnsDohUrl = "dns_doh_url";
    public const string DnsDotHost = "dns_dot_host";
    public const string DoNotTrack = "do_not_track";
    public const string SaveData = "save_data";
    public const string GlobalPrivacyControl = "browser.gpc";
    public const string StripTrackingParams = "browser.strip_tracking_params";
    public const string HttpsUpgrade = "browser.https_upgrade";
    public const string CapReferrer = "browser.cap_referrer";
    public const string BlockThirdPartyCookies = "third_party";
    public const string SkipEditor = "idm_pref_skip_editor";
    public const string AutoStart = "skip_editor_auto_start";
    public const string SingleFolder = "idm_pref_disable_cataloguing";
    public const string SmartDownload = "idm_pref_smart_download";
    public const string SmartDownloadAll = "idm_pref_smart_download_all";
    public const string SmartExcludeExt = "idm_pref_smart_download_exclude_extensions";
    public const string WifiOnly = "idm_pref_wifi";
    public const string Theme = "idm_pref_theme";
    public const string CustomTheme = "app.theme.custom";
    /// <summary>Pause on metered network. Single switch: the engine key <c>winidm_pause_on_metered</c> read by <see cref="SettingKeys.PauseOnMetered"/>.</summary>
    public const string PauseOnMetered = SettingKeys.PauseOnMetered;
    public const string WindowsHello = "vault.windows_hello";
    public const string UpdateUrl = "app.update_url";
    public const string CaptureLog = "app.capture_log";
    public const string DownloadDiagnostics = "download_diagnostics";
    public const string Parts = "idm_pref_download_part";
    public const string Concurrent = "idm_pref_download_count";
    public const string RemoveFinished = "idm_pref_remove_link_download_finish";
    public const string ToastComplete = "idm_pref_show_toast_complete";
    public const string ToastError = "idm_pref_show_toast_error";
    public const string DisableAllToasts = "ddasetm";
    public const string Sound = "idm_pref_sound_options";
    public const string WakeLock = "idm_pref_wake_lock";
    public const string KeepScreenOn = "ksowiu";
    public const string ShowBits = "sdusbps";
    public const string EtaActiveTime = "show_active_time_eta";
    public const string MinimizeToTray = "app.minimize_to_tray";
    public const string StartWithWindows = "app.start_with_windows";
    public const string RegisterProtocols = "app.register_protocols";
    public const string DefaultFolder = "app.default_folder";
    /// <summary>Engine key <c>winidm_after_downloads_action</c> (the <c>SystemAction</c> enum name).</summary>
    public const string AfterComplete = "winidm_after_downloads_action";
    public const string SpeedLimitEnabled = "idm_pref_speed_limiter";
    public const string SpeedLimitKbps = "idm_pref_speed_limit";

    // Download list (03a: General)
    public const string ShowMultiPart = "idm_pref_show_multi_part";
    public const string ShowTabCounts = "sdcwtn";
    public const string ShowFileTypeFilter = "idm_pref_show_file_types_filter";
    public const string ShowTimeFilter = "idm_pref_show_time_filter";
    public const string AutoOpenWebPages = "idm_pref_auto_open_webpage_browser";

    // Notifications, sounds and the overlay window (03a: Notification, Overlay)
    public const string Notifications = "idm_pref_notifications";
    public const string NoAddedToast = "idm_pref_disable_toast_download_added_started";
    public const string SoundStart = "idm_pref_sound_ringtone_start";
    public const string SoundError = "idm_pref_sound_ringtone_error";
    public const string SoundComplete = "idm_pref_sound_ringtone_complete";
    public const string SoundCompleteAll = "idm_pref_sound_ringtone_complete_all";
    public const string Overlay = "idm_pref_show_window_overlay";
    public const string OverlayCombine = "idm_pref_combine_notifications_overlay";
    public const string OverlayWidth = "idm_pref_window_width_overlay";
    public const string OverlayHeight = "idm_pref_window_height_overlay";
    public const string OverlayTextSize = "idm_pref_text_size_overlay";
    public const string OverlayTextColor = "idm_pref_overlay_text_color";
    public const string OverlayBackground = "idm_pref_background_color_overlay";
    public const string OverlayProgressColor = "idm_pref_progressbar_color_overlay";
    public const string OverlayHAlign = "idm_pref_horizontal_alignment_overlay";
    public const string OverlayVAlign = "idm_pref_vertical_alignment_overlay";
    public const string OverlayHOffset = "idm_pref_horizontal_offset_overlay";
    public const string OverlayVOffset = "idm_pref_vertical_offset_overlay";

    // Browser
    public const string BrowserJs = "cb_javascript";
    public const string BrowserBlockImages = "cb_images";
    public const string SearchEngine = "search";
    public const string SearchCustom = "searchurl";
    public const string Home = "home";
    public const string SdeBrowserDownload = "sdebdl";
    public const string DarkMode = "webview_dark_mode";
    public const string DevTools = "webview_debugging_enabled";
    public const string AdBlock = "browser.adblock";
    public const string WebRtcBlock = "block_webrtc_leak";
    public const string IgnoreSsl = "issle";
    public const string ClearCacheExit = "clear_cache_exit";
    public const string ClearHistoryExit = "clear_history_exit";
    public const string ClearCookiesExit = "clear_cookies_exit";
    public const string ClearWebStorageExit = "clear_webstorage_exit";
    public const string SecureIncognito = "enable_secure_incognito_mode";
    public const string RestoreTabs = "restore_tabs";
    public const string SavedTabs = "browser.saved_tabs";
    public const string IncognitoDefault = "incognito_default";
    public const string AllowNewWindow = "allow_new_window";
    public const string PopupHandling = "popup_handling";
    public const string OpenTabsBackground = "open_new_tabs_background";
    public const string AskNewTabs = "ask_new_tabs_open";
    public const string DisableSubtitleCapture = "idm_pref_disable_subtitle_capture";
    public const string SavedPagesFolder = "wbpsf";
    public const string SavePageTitleName = "uwtaffsp";
    public const string SubtitlesInVideoFolder = "idm_pref_download_subtitles_videos_folder";
    public const string TabClosedUndo = "show_tab_closed";
    public const string TabClosedDurationMs = "tab_closed_snackbar_duration_ms";
    public const string PageZoom = "text_size";
    public const string RenderingMode = "rendering_mode";
    public const string ShowTargetUrl = "stupm";
    public const string TargetUrlMaxChars = "tumnoc";
    public const string ForceSelection = "enable_selection_copy_paste";
    public const string CapturePageTitle = "capture_page_title";
    public const string TranslateTo = "browser.translate_to";
    public const string AllowCookies = "allow_cookies";
    public const string IncognitoCookies = "incognito_cookies";
    public const string SafeBrowsing = "wbsbe";
    public const string CameraPolicy = "wbcmara";
    public const string MicrophonePolicy = "wbmpara";
    public const string LocationPolicy = "wbloara";

    // Scheduler (shown in the Scheduler tab)
    public const string SchedEnabled = "scheduler.enabled";
    public const string SchedStart = "scheduler.start";
    public const string SchedStop = "scheduler.stop";
    public const string SchedDays = "scheduler.days";
    public const string SchedSpeed = "scheduler.speed_kbps";

    // Proxy
    public const string ProxyEnabled = "idm_pref_proxy";
    public const string ProxyType = "idm_pref_proxy_type";
    public const string ProxyHost = "idm_pref_proxy_host";
    public const string ProxyPort = "idm_pref_proxy_port";
    public const string ProxyDisableWebRtc = "idm_pref_proxy_disable_webrtc";
    public const string DisplayPeerInfo = "idm_pref_display_peerinfo";
    public const string BookmarkFoldersFirst = "bookmark_show_folders_first";
}

/// <summary>
/// Typed layer over <see cref="ISettingsStore"/>. Defaults come from <see cref="SettingsCatalog"/>
/// so the UI and code never disagree on a default.
/// </summary>
public sealed class AppSettings
{
    private readonly ISettingsStore _store;

    public AppSettings(ISettingsStore store) => _store = store;

    public event Action<string>? Changed
    {
        add => _store.Changed += value;
        remove => _store.Changed -= value;
    }

    public bool Bool(string key) => _store.Get(key, DefaultOf<bool>(key));
    public int Int(string key) => _store.Get(key, DefaultOf<int>(key));
    public string Text(string key) => _store.Get(key, DefaultOf<string>(key));

    public void Set<T>(string key, T value) => _store.Set(key, value);

    public IReadOnlyDictionary<string, string> Snapshot() => _store.Snapshot();

    private static T DefaultOf<T>(string key)
    {
        if (!SettingsCatalog.TryGet(key, out var def))
            throw new KeyNotFoundException($"Unknown setting key: {key}");
        return (T)Convert.ChangeType(def.Default, typeof(T));
    }
}
