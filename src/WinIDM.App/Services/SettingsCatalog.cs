using SettingDefaults = WinIDM.Engine.SettingDefaults;

namespace WinIDM.App.Services;

public enum SettingKind { Bool, Int, Text, Choice }

/// <summary>
/// Implementation status of each setting. Shown as a badge in the UI so the user knows
/// which settings actually take effect.
/// </summary>
public enum SettingStatus
{
    /// <summary>Implemented by this WPF application.</summary>
    Active,
    /// <summary>Stored; the engine/torrent/browser modules must read it.</summary>
    EngineHook,
    /// <summary>Not implemented yet (no effect so far).</summary>
    Pending,
}

/// <summary>
/// Definition of one setting. Keys and defaults are taken from 03a-jadual-tetapan.md where a match exists.
/// Keys without an Android counterpart (the <c>app.</c>, <c>browser.</c>, <c>scheduler.</c> prefixes) are Windows clone decisions.
/// </summary>
public sealed record SettingDef(
    string Key,
    string Label,
    SettingKind Kind,
    object Default,
    string Category,
    string Group,
    SettingStatus Status = SettingStatus.Active,
    string[]? Choices = null,
    bool Visible = true,
    int DisplayOffset = 0);

public static class SettingsCatalog
{
    public const string CatGeneral = "General";
    public const string CatAutomation = "Automation";
    public const string CatNotification = "Notifications";
    public const string CatRetry = "Retry";
    public const string CatProxy = "Proxy";
    public const string CatTorrent = "Torrent";
    public const string CatAdvanced = "Advanced";
    public const string CatOverlay = "Overlay";
    public const string CatBrowser = "Browser";

    /// <summary>Nine setting categories per 03a (display order).</summary>
    public static readonly string[] Categories =
    {
        CatGeneral, CatAutomation, CatNotification, CatRetry, CatProxy,
        CatTorrent, CatAdvanced, CatOverlay, CatBrowser,
    };

    private static readonly string[] ThemeChoices = { "Light", "Dark", "System", "Custom" };

    public static readonly IReadOnlyList<SettingDef> All = Build();

    private static List<SettingDef> Build()
    {
        var l = new List<SettingDef>();
        void B(string k, string label, bool d, string cat, string grp, SettingStatus s = SettingStatus.Active) =>
            l.Add(new SettingDef(k, label, SettingKind.Bool, d, cat, grp, s));
        void I(string k, string label, int d, string cat, string grp, SettingStatus s = SettingStatus.Active, bool vis = true) =>
            l.Add(new SettingDef(k, label, SettingKind.Int, d, cat, grp, s, null, vis));
        void T(string k, string label, string d, string cat, string grp, SettingStatus s = SettingStatus.Active) =>
            l.Add(new SettingDef(k, label, SettingKind.Text, d, cat, grp, s));
        void C(string k, string label, string d, string[] ch, string cat, string grp, SettingStatus s = SettingStatus.Active, bool vis = true) =>
            l.Add(new SettingDef(k, label, SettingKind.Choice, d, cat, grp, s, ch, vis));

        const string G = "Downloads", W = "Windows";
        // --- General ---
        B("idm_pref_skip_editor", "Skip the download editor (add straight to the queue)", false, CatGeneral, G);
        B("skip_editor_auto_start", "Start downloads automatically once added", true, CatGeneral, G);
        B("idm_pref_disable_cataloguing", "Save all files in one folder", false, CatGeneral, G);
        B("idm_pref_smart_download", "Watch the clipboard for download links", false, CatGeneral, G);
        B("idm_pref_smart_download_all", "Catch every valid URL, including web pages", false, CatGeneral, G);
        T("idm_pref_smart_download_exclude_extensions", "Exclude extensions (comma separated)", "", CatGeneral, G);
        B("idm_pref_wifi", "Wi-Fi only (ignore mobile data and metered networks)", false, CatGeneral, G);
        B("idm_pref_auto_open_webpage_browser", "Open web pages in the browser automatically", false, CatGeneral, G);
        // Stored as IDM+ does (count - 1, which the engine reads); the page shows and accepts the real count.
        l.Add(new SettingDef("idm_pref_download_count", "Number of simultaneous downloads (1-9)", SettingKind.Int,
            SettingDefaults.DownloadCount, CatGeneral, G, DisplayOffset: 1));
        l.Add(new SettingDef("idm_pref_download_part", "Number of parts per download (1-32)", SettingKind.Int,
            SettingDefaults.DownloadPart, CatGeneral, G, DisplayOffset: 1));
        B("show_active_time_eta", "Calculate ETA from active time", true, CatGeneral, G);
        B("sdusbps", "Show speed in bits per second", false, CatGeneral, G);
        B("idm_pref_show_multi_part", "Show progress for each segment", true, CatGeneral, G);
        B("idm_pref_show_file_types_filter", "Show the file type filter", true, CatGeneral, G);
        B("idm_pref_show_time_filter", "Show the time filter", false, CatGeneral, G);
        B("app.minimize_to_tray", "Minimize to the system tray when the window is closed", true, CatGeneral, W);
        B("app.start_with_windows", "Start with Windows", false, CatGeneral, W);
        B("app.register_protocols", "Register the magnet:, .torrent, 1dmdownload and idmdownload protocols", false, CatGeneral, W);
        T("app.default_folder", "Default download folder (empty = the Downloads folder)", "", CatGeneral, W);
        // Choice values are SystemAction enum names (the engine reads them directly). Confirmation is requested before the action.
        C("winidm_after_downloads_action", "Action when all downloads finish (confirmed first)", "None",
          new[] { "None", "Shutdown", "Restart", "Sleep", "Hibernate", "LockWorkstation", "CloseApplication" }, CatGeneral, W);
        C("idm_pref_theme", "Theme", "System", ThemeChoices, CatGeneral, "Theme");

        // --- Automation ---
        const string Sel = "After download";
        B("idm_pref_remove_link_download_finish", "Remove finished links from the list (the file is kept)", false, CatAutomation, Sel);
        B("idm_pref_move_files_download_finish", "Move files after a download finishes", false, CatAutomation, Sel);
        T("idm_pref_move_files_download_finish_path", "Move destination folder", "", CatAutomation, Sel);
        B("winidm_move_files_by_category", "Move by category (subfolder)", false, CatAutomation, Sel);
        I("idm_pref_delete_links_after_days", "Remove old links after (n) days (0 = never)", 0, CatAutomation, Sel, SettingStatus.EngineHook);

        // --- Notifications ---
        const string Nt = "Tray notifications";
        B("idm_pref_notifications", "Show download progress in the tray icon while downloading", true, CatNotification, Nt);
        B("idm_pref_disable_toast_download_added_started", "Do not show a notification when a download is added", false, CatNotification, Nt);
        B("idm_pref_show_toast_complete", "Show a notification when a download completes", false, CatNotification, Nt);
        B("idm_pref_show_toast_error", "Show a notification when a download fails", false, CatNotification, Nt);
        B("ddasetm", "Disable all tray notifications (including errors)", false, CatNotification, Nt);
        const string Snd = "Sounds";
        B("idm_pref_sound_options", "Play sounds for download events", false, CatNotification, Snd);
        const string SoundHint = " (empty = none, default = Windows sound, or a .wav file path)";
        T("idm_pref_sound_ringtone_start", "Download starts" + SoundHint, "", CatNotification, Snd);
        T("idm_pref_sound_ringtone_error", "Download errors" + SoundHint, SoundService.Default, CatNotification, Snd);
        T("idm_pref_sound_ringtone_complete", "Download completes" + SoundHint, SoundService.Default, CatNotification, Snd);
        T("idm_pref_sound_ringtone_complete_all", "All downloads complete" + SoundHint, SoundService.Default, CatNotification, Snd);

        // --- Retry ---
        const string Rt = "Retries";
        B("idm_pref_retry", "Retry on connection errors", true, CatRetry, Rt, SettingStatus.EngineHook);
        I("idm_pref_retry_count", "Number of attempts (0 = unlimited)", 999999, CatRetry, Rt, SettingStatus.EngineHook);
        I("idm_pref_retry_interval", "Retry interval (seconds)", 5, CatRetry, Rt, SettingStatus.EngineHook);
        B("always_retry_download", "Retry even when the server error is unrecoverable", false, CatRetry, Rt, SettingStatus.EngineHook);

        // --- Proxy ---
        const string Px = "Proxy server";
        B("idm_pref_proxy", "Use a proxy for downloads", false, CatProxy, Px, SettingStatus.EngineHook);
        C("idm_pref_proxy_provider", "Proxy provider (Orbot = Tor SOCKS5 127.0.0.1:9050, I2P = HTTP 127.0.0.1:4444)", "Manual",
          new[] { "Manual", "Orbot (Tor)", "I2P" }, CatProxy, Px, SettingStatus.EngineHook);
        C("idm_pref_proxy_type", "Proxy type", "HTTP", new[] { "HTTP", "SOCKS5", "HTTPS" }, CatProxy, Px, SettingStatus.EngineHook);
        B("idm_pref_proxy_disable_webrtc", "Disable WebRTC in the browser while a proxy is used", false, CatProxy, Px);
        T("idm_pref_proxy_host", "Proxy host", "", CatProxy, Px, SettingStatus.EngineHook);
        I("idm_pref_proxy_port", "Proxy port (1-65535)", 0, CatProxy, Px, SettingStatus.EngineHook);
        T("idm_pref_proxy_user", "Proxy user name (the password is not stored here)", "", CatProxy, Px, SettingStatus.EngineHook);
        I("dns_mode", "Download DNS resolver (0 = system, 1 = DoH, 2 = DoT)", 0, CatProxy, Px, SettingStatus.EngineHook);
        T("dns_doh_url", "DoH URL (must be https)", "https://cloudflare-dns.com/dns-query", CatProxy, Px, SettingStatus.EngineHook);
        T("dns_dot_host", "DoT host (port 853)", "", CatProxy, Px, SettingStatus.EngineHook);

        // --- Torrent ---
        const string Tr = "Network and torrent";
        B("disable_torrent", "Disable torrent support", false, CatTorrent, Tr, SettingStatus.EngineHook);
        B("use_random_port", "Use a random port", true, CatTorrent, Tr, SettingStatus.EngineHook);
        I("custom_port_number", "Custom port (used when the random port is off)", 6881, CatTorrent, Tr, SettingStatus.EngineHook);
        B("enable_dht", "Enable DHT", true, CatTorrent, Tr, SettingStatus.EngineHook);
        B("enable_lsd", "Enable LSD", true, CatTorrent, Tr, SettingStatus.EngineHook);
        B("enable_pex", "Enable PeX", true, CatTorrent, Tr, SettingStatus.EngineHook);
        B("enable_utp", "Enable μTP", false, CatTorrent, Tr, SettingStatus.EngineHook);
        B("enable_upnp", "Enable UPnP", true, CatTorrent, Tr, SettingStatus.EngineHook);
        B("enable_nat_pmp", "Enable NAT-PMP", true, CatTorrent, Tr, SettingStatus.EngineHook);
        I("max_connection", "Maximum connections", 500, CatTorrent, Tr, SettingStatus.EngineHook);
        I("max_peers", "Maximum peers", 500, CatTorrent, Tr, SettingStatus.EngineHook);
        C("encrypt_incoming_connection", "Incoming connection encryption", "Prefer encryption",
          new[] { "Prefer encryption", "Require", "Disable" }, CatTorrent, Tr, SettingStatus.EngineHook);
        B("sequential_download", "Sequential download", false, CatTorrent, Tr, SettingStatus.EngineHook);
        B("seed_finished_torrent", "Seed torrents after they finish", false, CatTorrent, Tr, SettingStatus.EngineHook);
        B("idm_pref_fetch_metadata", "Fetch metadata from magnet links first", true, CatTorrent, Tr, SettingStatus.EngineHook);
        B("enable_ip_filtering", "Enable IP filtering", false, CatTorrent, Tr, SettingStatus.EngineHook);
        T("ip_filter_path", "IP filter file path (.dat, .p2p)", "", CatTorrent, Tr, SettingStatus.EngineHook);
        B("validate_https_trackers", "Validate tracker HTTPS certificates", false, CatTorrent, Tr, SettingStatus.EngineHook);
        B("acatracker", "Always contact all trackers", true, CatTorrent, Tr, SettingStatus.EngineHook);
        B("default_trackers", "Add the default public trackers to new torrents", false, CatTorrent, Tr, SettingStatus.EngineHook);
        T("dturl", "Tracker list URL added to new torrents (plain text, one tracker per line)", "", CatTorrent, Tr, SettingStatus.EngineHook);
        B("idm_pref_add_tracker_auto", "Automatically add trackers to duplicate torrents", false, CatTorrent, Tr, SettingStatus.EngineHook);
        B("idm_pref_display_peerinfo", "Display peer info (Peers... in the torrent window)", false, CatTorrent, Tr);
        C("torrent_merge_buffer_size", "Merge disk writes: buffer size", "1 MB",
          new[] { "256 KB", "512 KB", "1 MB", "2 MB", "4 MB", "8 MB", "16 MB" }, CatTorrent, Tr, SettingStatus.EngineHook);

        // --- Advanced ---
        const string Ad = "System and performance";
        B("idm_pref_wake_lock", "Keep the computer awake while downloads are active", true, CatAdvanced, Ad);
        B("ksowiu", "Keep the display on while downloads are active", false, CatAdvanced, Ad);
        B("idm_pref_create_empty_file", "Create an empty file first to speed up the download", true, CatAdvanced, Ad, SettingStatus.EngineHook);
        B("idm_pref_auto_restart_non_resumable_links", "Restart from zero if a link cannot be resumed", false, CatAdvanced, Ad, SettingStatus.EngineHook);
        I("idm_pref_timeout", "Connection timeout (seconds)", 60, CatAdvanced, Ad, SettingStatus.EngineHook);
        T("idm_pref_user_agent", "Custom User-Agent (empty = default)", "", CatAdvanced, Ad, SettingStatus.EngineHook);
        B("idm_pref_speed_limiter", "Enable the download speed limit", false, CatAdvanced, Ad, SettingStatus.EngineHook);
        B("idm_pref_speed_limiter_cascade", "Apply the speed limit to all downloads", false, CatAdvanced, Ad, SettingStatus.EngineHook);
        I("idm_pref_speed_limit", "Speed limit (KB/s)", 10240, CatAdvanced, Ad, SettingStatus.EngineHook);
        B("sdcwtn", "Show the download count in the tab name", true, CatAdvanced, Ad);
        I("idm_pref_battery_limit", "Stop downloads when the battery drops below (%) (0 = never)", 0, CatAdvanced, Ad, SettingStatus.EngineHook);
        B("idm_convert_ts_files_ffmpeg", "Convert .ts files to MP4 (requires FFmpeg)", true, CatAdvanced, Ad, SettingStatus.EngineHook);
        B("idm_pref_advance_download_method", "Use the advanced download method (large files get extra parts that start as other parts finish; may use a little more data)", true, CatAdvanced, Ad, SettingStatus.EngineHook);
        B("download_diagnostics", "Download diagnostics (logs without URLs or file names)", false, CatAdvanced, Ad);
        B("app.capture_log", "Capture the application log (off by default; errors are always logged)", false, CatAdvanced, Ad);
        T("app.update_url", "Custom update check URL (https; empty = off)", "", CatAdvanced, Ad);

        // --- Overlay: a progress window that floats above every other window while downloads run ---
        const string Ov = "Overlay window";
        B("idm_pref_show_window_overlay", "Show the progress window above all windows while downloading", false, CatOverlay, Ov);
        B("idm_pref_combine_notifications_overlay", "Combine all downloads into one line", false, CatOverlay, Ov);
        // Percent of the screen work area (IDM+ uses 75 x 30 of a phone screen; a desktop needs a smaller default).
        I("idm_pref_window_width_overlay", "Window width (% of the screen)", 25, CatOverlay, Ov);
        I("idm_pref_window_height_overlay", "Window height (% of the screen)", 18, CatOverlay, Ov);
        I("idm_pref_text_size_overlay", "Text size", 11, CatOverlay, Ov);
        T("idm_pref_overlay_text_color", "Text color", "#FFFFFF", CatOverlay, Ov);
        T("idm_pref_background_color_overlay", "Background color", "#CCF46C36", CatOverlay, Ov);
        T("idm_pref_progressbar_color_overlay", "Progress bar color", "#CC8CA638", CatOverlay, Ov);
        C("idm_pref_horizontal_alignment_overlay", "Horizontal alignment", "Right", new[] { "Left", "Center", "Right" }, CatOverlay, Ov);
        C("idm_pref_vertical_alignment_overlay", "Vertical alignment", "Bottom", new[] { "Top", "Center", "Bottom" }, CatOverlay, Ov);
        I("idm_pref_horizontal_offset_overlay", "Horizontal offset (px)", 16, CatOverlay, Ov);
        I("idm_pref_vertical_offset_overlay", "Vertical offset (px)", 16, CatOverlay, Ov);

        // --- Browser ---
        const string BU = "General", BP = "Display", BPr = "Privacy and security", BT = "Tabs and windows";
        B("cb_javascript", "Enable JavaScript", true, CatBrowser, BU);
        B("cb_images", "Block images", false, CatBrowser, BU);
        C("search", "Search engine", "Google",
          new[] { "Google", "Bing", "DuckDuckGo", "Yahoo", "Brave", "Startpage", "Baidu", "Yandex", "Custom" }, CatBrowser, BU);
        T("searchurl", "Custom search URL (use {q} in place of the search term)", "", CatBrowser, BU);
        T("home", "Home page (empty = start page)", "", CatBrowser, BU);
        B("sdebdl", "Skip the editor when a page starts a download", false, CatBrowser, BU);
        C("webview_dark_mode", "Dark mode in the browser", "Follow theme",
          new[] { "Follow theme", "Always on", "Always off" }, CatBrowser, BP);
        B("webview_debugging_enabled", "Enable WebView debugging", false, CatBrowser, BP);
        I("text_size", "Default page zoom (%)", 100, CatBrowser, BP);
        C("rendering_mode", "Rendering mode", "Normal",
          new[] { "Normal", "Inverted", "Grayscale", "Inverted grayscale", "Increase contrast" }, CatBrowser, BP);
        B("stupm", "Show the target URL of a link in the status bar", true, CatBrowser, BP);
        I("tumnoc", "Maximum characters of the target URL shown", 128, CatBrowser, BP);
        B("enable_selection_copy_paste", "Force text selection, copy and paste on pages that block it", false, CatBrowser, BU);
        B("capture_page_title", "Use the web page title as the file name of captured media", true, CatBrowser, BU);
        B("idm_pref_disable_subtitle_capture", "Disable automatic subtitle capture in the browser", false, CatBrowser, BU);
        B("idm_pref_download_subtitles_videos_folder", "Download subtitles into the videos folder", true, CatBrowser, BU);
        T("wbpsf", "Folder for saved web pages (empty = choose each time)", "", CatBrowser, BU);
        B("uwtaffsp", "Use the page title as the file name when saving a page", true, CatBrowser, BU);
        B("bookmark_show_folders_first", "Show bookmark folders first", false, CatBrowser, BU);
        T("browser.translate_to", "Translate pages to (language code, for example en, ms)", "en", CatBrowser, BU);
        B("browser.adblock", "Block ads and trackers", true, CatBrowser, BPr);
        B("block_webrtc_leak", "Prevent WebRTC leaks", false, CatBrowser, BPr);
        B("issle", "Ignore SSL errors (not recommended)", false, CatBrowser, BPr);
        B("do_not_track", "Send the DNT: 1 header", false, CatBrowser, BPr);
        B("save_data", "Send the Save-Data header", false, CatBrowser, BPr);
        B("third_party", "Block third-party cookies (default: blocked)", true, CatBrowser, BPr);
        B("allow_cookies", "Enable cookies", true, CatBrowser, BPr);
        B("incognito_cookies", "Enable cookies in incognito mode", true, CatBrowser, BPr);
        B("wbsbe", "Enable safe browsing (Microsoft Defender SmartScreen)", true, CatBrowser, BPr);
        var permissionChoices = new[] { "Always Ask", "Allow", "Block" };
        C("wbcmara", "Camera", "Always Ask", permissionChoices, CatBrowser, BPr);
        C("wbmpara", "Microphone", "Always Ask", permissionChoices, CatBrowser, BPr);
        C("wbloara", "Location", "Always Ask", permissionChoices, CatBrowser, BPr);
        C("wbcmaraig", "Camera (incognito)", "Block", permissionChoices, CatBrowser, BPr);
        C("wbmparaig", "Microphone (incognito)", "Block", permissionChoices, CatBrowser, BPr);
        C("wbloaraig", "Location (incognito)", "Block", permissionChoices, CatBrowser, BPr);
        B("enable_secure_incognito_mode", "Secure incognito mode (block screenshots)", false, CatBrowser, BPr);
        B("clear_cache_exit", "Clear the cache on exit", false, CatBrowser, BPr);
        B("clear_history_exit", "Clear history on exit", false, CatBrowser, BPr);
        B("clear_cookies_exit", "Clear cookies on exit", false, CatBrowser, BPr);
        B("clear_webstorage_exit", "Clear web storage on exit", false, CatBrowser, BPr);
        B("restore_tabs", "Restore tabs on startup (incognito tabs are not restored)", true, CatBrowser, BT);
        B("incognito_default", "Open the browser in incognito mode by default", false, CatBrowser, BT);
        B("allow_new_window", "Allow sites to open new tabs", true, CatBrowser, BT);
        C("popup_handling", "Popup and new tab handling", "Ask", new[] { "Ask", "Allow", "Block" }, CatBrowser, BT);
        B("open_new_tabs_background", "Open new tabs in the background", false, CatBrowser, BT);
        B("ask_new_tabs_open", "Ask before opening new tabs", false, CatBrowser, BT);
        B("show_tab_closed", "Show a message with Undo when a tab is closed", false, CatBrowser, BT);
        I("tab_closed_snackbar_duration_ms", "Tab closed message duration (ms)", 4000, CatBrowser, BT);

        // --- Internal keys (not shown in Settings) ---
        const string Hid = "Internal";
        l.Add(new SettingDef("browser.saved_tabs", "Browser tab list (JSON)", SettingKind.Text, "", CatBrowser, Hid,
            SettingStatus.Active, null, Visible: false));
        C("scheduler.days", "Repeat", "Every day", new[] { "Every day", "Weekdays", "Weekends" }, CatGeneral, Hid, SettingStatus.EngineHook, vis: false);
        B("scheduler.enabled", "Scheduler enabled", false, CatGeneral, Hid, SettingStatus.EngineHook);
        T("scheduler.start", "Start time (HH:mm)", "00:00", CatGeneral, Hid, SettingStatus.EngineHook);
        T("scheduler.stop", "Stop time (HH:mm)", "06:00", CatGeneral, Hid, SettingStatus.EngineHook);
        I("scheduler.speed_kbps", "Scheduler speed limit (KB/s, 0 = none)", 0, CatGeneral, Hid, SettingStatus.EngineHook);
        B("winidm_pause_on_metered", "Pause downloads on a metered network", false, CatAutomation, Sel);
        B("vault.windows_hello", "Request Windows Hello before opening the password vault", false, CatBrowser, BPr);

        // --- Internal: backup and theme (not shown; managed by BackupWindow, the scheduler and ThemeEditor) ---
        const string Bk = "Backup";
        void H(string k, SettingKind kind, object d) =>
            l.Add(new SettingDef(k, k, kind, d, CatGeneral, Bk, SettingStatus.Active, null, Visible: false));
        H("backup.destination", SettingKind.Text, "Local");
        H("backup.local_folder", SettingKind.Text, "");
        H("backup.rest_url", SettingKind.Text, "");
        H("backup.google_client_id", SettingKind.Text, "");
        H("backup.dropbox_client_id", SettingKind.Text, "");
        H("backup.google_secret", SettingKind.Text, "");
        H("backup.dropbox_secret", SettingKind.Text, "");
        H("backup.rest_token_secret", SettingKind.Text, "");
        H("backup.schedule_password_secret", SettingKind.Text, "");
        H("backup.schedule.enabled", SettingKind.Bool, false);
        H("backup.schedule.interval_hours", SettingKind.Int, 0);
        H("backup.schedule.on_open", SettingKind.Bool, false);
        H("backup.schedule.on_close", SettingKind.Bool, false);
        H("backup.schedule.retention", SettingKind.Int, 0);
        H("backup.schedule.categories", SettingKind.Int, 463);
        H("backup.last_status", SettingKind.Text, "");
        H("app.theme.custom", SettingKind.Text, "");
        return l;
    }

    private static readonly Dictionary<string, SettingDef> ByKey =
        All.GroupBy(d => d.Key).ToDictionary(g => g.Key, g => g.First());

    public static bool TryGet(string key, out SettingDef def) => ByKey.TryGetValue(key, out def!);
}
