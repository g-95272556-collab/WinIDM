using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using WinIDM.App.Services;
using WinIDM.App.ViewModels;
using WinIDM.Browser;
using WinIDM.Core.Models;

namespace WinIDM.App.Views;

/// <summary>
/// Embedded browser window (WebView2). Each tab is one WebView2 control.
/// Incognito mode uses a temporary profile that is deleted on close.
/// </summary>
public partial class BrowserWindow : Window
{
    private const string DesktopUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.0.0 Safari/537.36";

    // Blocks the WebRTC APIs in pages without disabling JavaScript (block_webrtc_leak).
    private const string WebRtcBlockScript =
        "(function(){['RTCPeerConnection','webkitRTCPeerConnection','RTCSessionDescription','RTCIceCandidate']" +
        ".forEach(function(n){try{Object.defineProperty(window,n,{value:undefined,configurable:false,writable:false});}catch(e){}});})();";

    private readonly Composition _c;
    private readonly IShell _shell;
    private readonly bool _incognito;
    private readonly string _profileDir;
    private readonly string? _initialUrl;
    private readonly ObservableCollection<BrowserTabViewModel> _tabs = new();
    private readonly HashSet<string> _sslAllowedHosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HistoryStore? _history;
    private readonly BookmarkStore? _bookmarks;
    private Task<CoreWebView2Environment>? _env;
    private BrowserTabViewModel? _current;
    private string _defaultUserAgent = "";

    public BrowserWindow(Composition composition, IShell shell, bool incognito, string? url)
    {
        InitializeComponent();
        _c = composition;
        _shell = shell;
        _incognito = incognito;
        _initialUrl = url;
        _profileDir = incognito
            ? Path.Combine(Path.GetTempPath(), "WinIDM-inkognito-" + Guid.NewGuid().ToString("N"))
            : _c.WebViewDataDir;

        // Incognito mode does not use the history or bookmark stores (no writes to disk).
        _history = incognito ? null : _c.History;
        _bookmarks = incognito ? null : _c.Bookmarks;

        TabStrip.ItemsSource = _tabs;
        IncognitoLabel.Visibility = incognito ? Visibility.Visible : Visibility.Collapsed;
        if (incognito) Title = "Incognito - WinIDM Browser";

        Loaded += OnLoaded;
        if (ScriptHost is { } scriptHost) scriptHost.UiRequested += OnScriptUiRequested;
        SourceInitialized += (_, _) =>
        {
            if (_incognito && _c.Settings.Bool(Keys.SecureIncognito))
                WindowProtection.ExcludeFromCapture(new WindowInteropHelper(this).Handle);
        };
        Closing += OnClosing;
        Closed += OnClosed;
        _c.Settings.Changed += OnSettingChanged;
    }

    // ---------- Lifecycle ----------

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _env = CreateEnvironmentAsync();
            await _env;
        }
        catch (Exception ex)
        {
            AppLog.Error("Start WebView2", ex);
            StatusText.Text = "WebView2 Runtime not found. Install the Microsoft Edge WebView2 Runtime.";
            return;
        }

        _ = RunDailyScriptUpdatesAsync();
        InitSidePanel();

        var restore = !_incognito && _c.Settings.Bool(Keys.RestoreTabs)
            ? SavedTabs.Parse(_c.Settings.Text(Keys.SavedTabs))
            : new List<string>();
        foreach (var u in restore.Where(UrlHelper.IsHttp))
            await NewTabAsync(u, select: false);

        if (!string.IsNullOrWhiteSpace(_initialUrl))
            await NewTabAsync(_initialUrl, select: true);
        else if (_tabs.Count == 0)
            await NewTabAsync(HomeUrlOrNull(), select: true);
        else
            SelectTab(_tabs[0]);
    }

    private async Task<CoreWebView2Environment> CreateEnvironmentAsync()
    {
        // Extensions run in the normal profile only; an incognito window starts without them.
        if (!_incognito)
        {
            try
            {
                var withExtensions = new CoreWebView2EnvironmentOptions(BuildBrowserArguments()) { AreBrowserExtensionsEnabled = true };
                var env = await CoreWebView2Environment.CreateAsync(null, _profileDir, withExtensions);
                _extensionsEnabled = true;
                return env;
            }
            catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException or NotImplementedException)
            {
                AppLog.Error("WebView2 extensions", ex);
            }
        }
        var options = new CoreWebView2EnvironmentOptions(BuildBrowserArguments());
        return await CoreWebView2Environment.CreateAsync(null, _profileDir, options);
    }

    /// <summary>Chromium arguments: manual proxy when enabled. The proxy password is not used here.</summary>
    private string BuildBrowserArguments()
    {
        // Same proxy as the download engine (type names or numbers, and the Orbot/I2P presets).
        if (WinIDM.Engine.ProxyTypes.Endpoint(_c.RawSettings) is not { } proxy) return "";
        return $"--proxy-server={proxy.Scheme}://{proxy.Host}:{proxy.Port}";
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_incognito) return;
        _c.Settings.Set(Keys.SavedTabs, SavedTabs.Serialize(_tabs.Select(t => t.Url).Where(UrlHelper.IsHttp)));
        var kinds = ClearKindsOnExit();
        if (kinds != (CoreWebView2BrowsingDataKinds)0 && _current?.View?.CoreWebView2 is { } core)
        {
            try { await core.Profile.ClearBrowsingDataAsync(kinds); }
            catch (Exception ex) { AppLog.Error("Clear data on exit", ex); }
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _c.Settings.Changed -= OnSettingChanged;
        if (ScriptHost is { } scriptHost) scriptHost.UiRequested -= OnScriptUiRequested;
        foreach (var t in _tabs.ToList())
        {
            ReleaseScripts(t);
            if (t.View is { } wv)
            {
                ViewHost.Children.Remove(wv);
                wv.Dispose();
            }
        }
        _tabs.Clear();
        if (_incognito) DeleteIncognitoProfile();
    }

    private void DeleteIncognitoProfile()
    {
        var dir = _profileDir;
        // WebView2 processes may still hold the files briefly after the window closes; retry a few times.
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 6; i++)
            {
                try
                {
                    if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                    return;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                await Task.Delay(1000);
            }
        });
    }

    private CoreWebView2BrowsingDataKinds ClearKindsOnExit()
    {
        var kinds = (CoreWebView2BrowsingDataKinds)0;
        if (_c.Settings.Bool(Keys.ClearCacheExit)) kinds |= CoreWebView2BrowsingDataKinds.DiskCache;
        if (_c.Settings.Bool(Keys.ClearHistoryExit)) kinds |= CoreWebView2BrowsingDataKinds.BrowsingHistory;
        if (_c.Settings.Bool(Keys.ClearCookiesExit)) kinds |= CoreWebView2BrowsingDataKinds.Cookies;
        if (_c.Settings.Bool(Keys.ClearWebStorageExit)) kinds |= CoreWebView2BrowsingDataKinds.AllDomStorage;
        return kinds;
    }

    private void OnSettingChanged(string key)
    {
        if (key is not (Keys.BrowserJs or Keys.DevTools or Keys.DarkMode or Keys.BrowserBlockImages)) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            foreach (var t in _tabs)
            {
                if (t.View?.CoreWebView2 is not { } core) continue;
                core.Settings.IsScriptEnabled = _c.Settings.Bool(Keys.BrowserJs);
                core.Settings.AreDevToolsEnabled = _c.Settings.Bool(Keys.DevTools);
                ApplyColorScheme(core);
            }
        }));
    }

    // ---------- Tab ----------

    private Task<BrowserTabViewModel> NewTabAsync(string? url, bool select = true) => NewTabAsync(url, select, blank: false);

    /// <param name="blank">Leave the new tab un-navigated (it becomes the target of a window request).</param>
    private async Task<BrowserTabViewModel> NewTabAsync(string? url, bool select, bool blank)
    {
        var tab = new BrowserTabViewModel();
        var wv = new WebView2 { Visibility = Visibility.Collapsed };
        tab.View = wv;
        _tabs.Add(tab);
        ViewHost.Children.Add(wv);
        if (select) SelectTab(tab);

        try
        {
            var env = await _env!;
            await wv.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            AppLog.Error("Start browser tab", ex);
            tab.Title = "Failed to start";
            return tab;
        }

        await WireCoreAsync(tab, wv.CoreWebView2);
        if (blank)
        {
            tab.IsStartPage = false;
            _navigatedTabs.Add(tab);
            return tab;
        }
        // The user may already have typed an address while the tab was being set up (Go works once the core exists).
        if (url is null) { if (!_navigatedTabs.Contains(tab)) ShowStartPage(tab); }
        else Navigate(tab, url);
        return tab;
    }

    private void SelectTab(BrowserTabViewModel tab)
    {
        foreach (var t in _tabs)
        {
            t.IsSelected = t == tab;
            if (t.View is { } wv) wv.Visibility = t == tab ? Visibility.Visible : Visibility.Collapsed;
        }
        _current = tab;
        MediaList.ItemsSource = tab.Media;
        DesktopMenu.IsChecked = tab.IsDesktopMode;
        AddressBox.Text = tab.Address;
        RefreshChrome();
    }

    private void RefreshChrome()
    {
        var tab = _current;
        if (tab is null) return;
        MediaButton.Content = $"Media ({tab.Media.Count})";
        RefreshShieldsButton(tab);
        BookmarkButton.Content = IsBookmarked(tab.Address) ? "★" : "☆";
        Title = $"{tab.Title} - " + (_incognito ? "Incognito" : "WinIDM Browser");
        StatusText.Text = tab.IsLoading ? "Loading…" : tab.Address;
    }

    private void CloseTab(BrowserTabViewModel tab)
    {
        int idx = _tabs.IndexOf(tab);
        _navigatedTabs.Remove(tab);
        ForgetShields(tab);
        if (!_incognito) RememberClosedTab(tab);
        _tabs.Remove(tab);
        ReleaseScripts(tab);
        if (tab.View is { } wv)
        {
            ViewHost.Children.Remove(wv);
            wv.Dispose();
        }
        if (_tabs.Count == 0)
        {
            Close();
            return;
        }
        if (_current == tab) SelectTab(_tabs[Math.Clamp(idx, 0, _tabs.Count - 1)]);
    }

    private async Task<string?> GetCookieHeaderAsync(CoreWebView2 core, string url)
    {
        try
        {
            var cookies = await core.CookieManager.GetCookiesAsync(url);
            return cookies.Count == 0 ? null : string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));
        }
        catch (Exception ex)
        {
            AppLog.Error("Read cookies", ex);
            return null;
        }
    }

    // ---------- Per-tab WebView2 wiring ----------

    private async Task WireCoreAsync(BrowserTabViewModel tab, CoreWebView2 core)
    {
        var s = core.Settings;
        if (_defaultUserAgent.Length == 0) _defaultUserAgent = s.UserAgent;
        _ = SyncExtensionsAsync(core);
        s.IsScriptEnabled = _c.Settings.Bool(Keys.BrowserJs);
        s.AreDevToolsEnabled = _c.Settings.Bool(Keys.DevTools);
        s.IsPasswordAutosaveEnabled = false;
        s.IsGeneralAutofillEnabled = false;
        s.AreDefaultContextMenusEnabled = true;
        ApplyColorScheme(core);

        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (sender, e) => OnWebResourceRequested(tab, (CoreWebView2)sender!, e);
        core.WebResourceResponseReceived += (_, e) => OnResponseReceived(tab, e);
        core.NavigationStarting += (_, e) => OnNavigationStarting(tab, e);
        core.SourceChanged += (_, _) => OnSourceChanged(tab, core);
        core.NavigationCompleted += (_, e) => OnNavigationCompleted(tab, core, e);
        core.DocumentTitleChanged += (_, _) =>
        {
            tab.Title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? tab.Address : core.DocumentTitle;
            if (_current == tab) RefreshChrome();
        };
        core.NewWindowRequested += (_, e) => OnNewWindowRequested(tab, e);
        core.DownloadStarting += (_, e) => _ = OnDownloadStartingAsync(tab, core, e);
        core.PermissionRequested += (_, e) => OnPermissionRequested(e);
        core.ServerCertificateErrorDetected += (_, e) =>
        {
            if (!TryCancelUpgradeCertificateError(tab, e)) OnCertificateError(e);
        };

        // WebRTC can reveal the real address behind a proxy (03a: idm_pref_proxy_disable_webrtc).
        bool proxyWebRtc = _c.Settings.Bool(Keys.ProxyDisableWebRtc) && WinIDM.Engine.ProxyTypes.Endpoint(_c.RawSettings) is not null;
        if (_c.Settings.Bool(Keys.WebRtcBlock) || proxyWebRtc)
            await core.AddScriptToExecuteOnDocumentCreatedAsync(WebRtcBlockScript);

        await WireShieldsAsync(tab, core);
        WireScripts(tab, core);
        WireBrowserTools(core);
        await WirePasswordAssistAsync(tab, core);
        await WireExtrasAsync(tab, core);
    }

    private void ApplyColorScheme(CoreWebView2 core)
    {
        var scheme = _c.Settings.Text(Keys.DarkMode) switch
        {
            "Always on" => CoreWebView2PreferredColorScheme.Dark,
            "Always off" => CoreWebView2PreferredColorScheme.Light,
            _ => ThemeService.IsDark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light,
        };
        core.Profile.PreferredColorScheme = scheme;
    }

    // ---------- Navigation ----------

    private void OnNavigationStarting(BrowserTabViewModel tab, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var u)) return;

        if (TryRewriteNavigation(tab, e)) return;

        if (u.Scheme is "http" or "https" or "about" or "data" or "blob" or "view-source"
            || u.Scheme == "chrome-extension" && ExtensionsAvailable)
        {
            tab.IsLoading = true;
            if (e.Uri != "about:blank") tab.IsStartPage = false;
            // Media is recalculated for every main navigation.
            tab.Media.Clear();
            ResetShieldsCounters(tab, e);
            if (tab.View?.CoreWebView2 is { } navCore) OnScriptNavigationStarting(tab, navCore, e.Uri);
            if (_current == tab) RefreshChrome();
            return;
        }

        // External schemes (e.g. mailto:, ms-settings:) are opened only after the user agrees (BRW-02).
        e.Cancel = true;
        var answer = MessageBox.Show(this,
            $"This link wants to open an external app for the \"{u.Scheme}:\" scheme.\n\n{UrlHelper.Redact(e.Uri)}\n\nContinue?",
            "External app", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Error("Open external app", ex);
        }
    }

    private void OnSourceChanged(BrowserTabViewModel tab, CoreWebView2 core)
    {
        if (tab.IsStartPage) return;
        tab.Url = core.Source ?? "";
        tab.Address = tab.Url;
        if (_current == tab && !AddressBox.IsKeyboardFocusWithin) AddressBox.Text = tab.Address;
        if (_current == tab) RefreshChrome();
    }

    private void OnNavigationCompleted(BrowserTabViewModel tab, CoreWebView2 core, CoreWebView2NavigationCompletedEventArgs e)
    {
        tab.IsLoading = false;
        var url = core.Source ?? "";
        if (e.IsSuccess && !tab.IsStartPage && UrlHelper.IsHttp(url))
        {
            _history?.Add(string.IsNullOrWhiteSpace(core.DocumentTitle) ? url : core.DocumentTitle, url);
        }
        if (_current == tab) RefreshChrome();
    }

    private void Navigate(BrowserTabViewModel tab, string input)
    {
        var resolved = BrowserSearchEngines.ResolveInput(input,
            _c.Settings.Text(Keys.SearchEngine), _c.Settings.Text(Keys.SearchCustom));
        if (resolved.Length == 0) return;
        if (tab.View?.CoreWebView2 is not { } core) return;
        _navigatedTabs.Add(tab);
        core.Navigate(resolved);
    }

    /// <summary>Tabs the user already sent somewhere, so a start page that finishes loading late never replaces it.</summary>
    private readonly HashSet<BrowserTabViewModel> _navigatedTabs = new();

    private void ShowStartPage(BrowserTabViewModel tab)
    {
        if (tab.View?.CoreWebView2 is not { } core) return;
        // Shortcut grid (up to 40). Shortcuts are edited through the Edit home page shortcuts menu.
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html><head><meta charset='utf-8'><title>New page</title><style>")
          .Append("body{font-family:'Segoe UI',sans-serif;margin:0;padding:40px;background:#F3F4F6;color:#1B1F24}")
          .Append("h1{font-size:26px;margin:0 0 6px}p{color:#5B6470}")
          .Append(".g{display:grid;grid-template-columns:repeat(auto-fill,minmax(170px,1fr));gap:10px;margin-top:20px}")
          .Append(".g a{display:block;padding:14px 12px;border-radius:8px;background:#fff;color:#1B1F24;text-decoration:none;border:1px solid #D5DAE1;overflow:hidden;white-space:nowrap;text-overflow:ellipsis}")
          .Append("@media (prefers-color-scheme: dark){body{background:#1E2126;color:#E8EAED}p{color:#A3ABB6}.g a{background:#272B31;color:#E8EAED;border-color:#3C424B}}")
          .Append("</style></head><body><h1>WinIDM</h1><p>Type a URL or search term in the address bar.</p><div class='g'>");
        foreach (var s in BrowserServices.For(_c).Shortcuts.All)
            sb.Append("<a href='").Append(WebUtility.HtmlEncode(s.Url)).Append("' title='")
              .Append(WebUtility.HtmlEncode(s.Url)).Append("'>")
              .Append(WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(s.Title) ? s.Url : s.Title)).Append("</a>");
        sb.Append("</div></body></html>");

        tab.IsStartPage = true;
        tab.Address = "";
        tab.Title = "New page";
        core.NavigateToString(sb.ToString());
    }

    private string? HomeUrlOrNull()
    {
        var home = _c.Settings.Text(Keys.Home).Trim();
        return UrlHelper.IsHttp(home) ? home : null;
    }

    // ---------- Request filtering: ads, images, media detection ----------

    private void OnWebResourceRequested(BrowserTabViewModel tab, CoreWebView2 core, CoreWebView2WebResourceRequestedEventArgs e)
    {
        try
        {
            var url = e.Request.Uri;
            ApplyRequestPrivacy(tab, e);
            if (ApplyScriptWebRequestRules(core, e, url)) return;
            if (_c.Settings.Bool(Keys.BrowserBlockImages) && e.ResourceContext == CoreWebView2WebResourceContext.Image)
            {
                e.Response = core.Environment.CreateWebResourceResponse(null, 204, "No Content", "");
                return;
            }
            if (TryBlockByAdblock(tab, core, e, url)) return;
        }
        catch (Exception ex)
        {
            AppLog.Error("Request filtering", ex);
        }
    }

    /// <summary>
    /// Read a response header without throwing. <see cref="CoreWebView2HttpResponseHeaders.GetHeader"/> raises
    /// <c>COMException 0x80070585 "Invalid index"</c> when the header is absent (it does not return an empty string),
    /// so every optional header must be probed first.
    /// </summary>
    internal static string HeaderOrEmpty(CoreWebView2HttpResponseHeaders headers, string name)
    {
        try
        {
            return headers.Contains(name) ? headers.GetHeader(name) : "";
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return "";
        }
    }

    /// <summary>Request-header variant of <see cref="HeaderOrEmpty(CoreWebView2HttpResponseHeaders, string)"/>.</summary>
    internal static string HeaderOrEmpty(CoreWebView2HttpRequestHeaders headers, string name)
    {
        try
        {
            return headers.Contains(name) ? headers.GetHeader(name) : "";
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return "";
        }
    }

    internal static long? TotalFromContentRange(string contentRange)
    {
        int slash = contentRange.LastIndexOf('/');
        return slash >= 0 && long.TryParse(contentRange[(slash + 1)..].Trim(), out var total) && total > 0 ? total : null;
    }

    private void OnResponseReceived(BrowserTabViewModel tab, CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        try
        {
            var headers = e.Response.Headers;
            var mime = HeaderOrEmpty(headers, "Content-Type");
            // A 206 reply's Content-Length is only the chunk; the full size is the total in "bytes a-b/TOTAL".
            long? length = TotalFromContentRange(HeaderOrEmpty(headers, "Content-Range"))
                           ?? (long.TryParse(HeaderOrEmpty(headers, "Content-Length"), out var len) ? len : null);
            var page = tab.View?.Source?.ToString();
            var media = _c.Media.Inspect(e.Request.Uri, mime, length, page);
            // IDM+ automatic subtitle capture (03a: idm_pref_disable_subtitle_capture turns it off).
            if (media is null && !_c.Settings.Bool(Keys.DisableSubtitleCapture) && MediaCapture.IsSubtitle(e.Request.Uri, mime))
                media = new DetectedMedia(e.Request.Uri, page, null, mime, length, DownloadKind.Http, MediaCapture.SubtitleQuality);
            if (media is null) return;

            // A Content-Disposition name is the best file name the server can offer, so keep it with the entry.
            var disclosed = MediaCapture.FileNameFromContentDisposition(HeaderOrEmpty(headers, "Content-Disposition"));
            if (disclosed is not null) media = media with { SourceFileName = disclosed };

            // Range variants of one progressive stream (YouTube) must collapse into a single panel entry.
            var key = MediaCapture.DedupeKey(media.Url);
            if (tab.Media.Any(m => MediaCapture.DedupeKey(m.Url) == key)) return;

            if (tab.Media.Count >= 200) tab.Media.RemoveAt(tab.Media.Count - 1);
            tab.Media.Insert(0, media);
            if (_current == tab) RefreshChrome();
        }
        catch (Exception ex)
        {
            AppLog.Error("Media detection", ex);
        }
    }

    // ---------- New window, downloads, permissions, SSL ----------

    private void OnNewWindowRequested(BrowserTabViewModel tab, CoreWebView2NewWindowRequestedEventArgs e)
    {
        if (IsExtensionWindowRequest(e, tab))
        {
            _ = OpenExtensionTabAsync(e);
            return;
        }
        e.Handled = true;
        var mode = _c.Settings.Text(Keys.PopupHandling);
        var pageOrigin = OriginOf(tab.View?.Source?.ToString() ?? "");
        bool allowed = _c.Settings.Bool(Keys.AllowNewWindow) && mode != "Block";
        // Per-site popup decision (remembered) overrides the default mode when one exists.
        if (_c.SitePermissions.Get(pageOrigin, PopupKind) is bool remembered)
        {
            allowed = remembered;
        }
        else if (allowed && mode == "Ask")
        {
            allowed = MessageBox.Show(this,
                $"The site wants to open a new tab:\n{UrlHelper.Redact(e.Uri)}\n\nAllow? This choice will be remembered for this site.",
                "New tab", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            if (pageOrigin.Length > 0) _c.SitePermissions.Set(pageOrigin, PopupKind, allowed);
        }
        // IDM+ "Ask before opening new tabs": a question for every new tab, on top of the popup policy.
        if (allowed && mode != "Ask" && _c.Settings.Bool(Keys.AskNewTabs))
        {
            allowed = MessageBox.Show(this, $"Open a new tab?\n{UrlHelper.Redact(e.Uri)}", "New tab",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        }
        if (!allowed)
        {
            StatusText.Text = "New tab blocked.";
            return;
        }
        _ = NewTabAsync(e.Uri, select: !_c.Settings.Bool(Keys.OpenTabsBackground));
    }

    /// <summary>
    /// WebView2 downloads are canceled and handed over to the download engine. The referer and cookies are passed
    /// along so that links requiring sign-in keep working.
    /// </summary>
    private async Task OnDownloadStartingAsync(BrowserTabViewModel tab, CoreWebView2 core, CoreWebView2DownloadStartingEventArgs e)
    {
        e.Cancel = true;
        ForgetUpgrade(tab);
        var deferral = e.GetDeferral();
        try
        {
            var url = e.DownloadOperation.Uri;
            if (!UrlHelper.IsHttp(url)) return;

            var req = new DownloadRequest
            {
                Url = url,
                FileName = UrlHelper.FileNameFromUrl(url),
                SaveDirectory = _c.DefaultDownloadFolder(),
                Referer = tab.View?.Source?.ToString(),
                UserAgent = core.Settings.UserAgent,
                CookieHeader = await GetCookieHeaderAsync(core, url),
            };

            if (_c.Settings.Bool(Keys.SdeBrowserDownload))
                await _c.Engine.AddAsync(req);
            else
                await _shell.AddDownloadAsync(req);
        }
        catch (Exception ex)
        {
            AppLog.Error("Download from browser", ex);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OnPermissionRequested(CoreWebView2PermissionRequestedEventArgs e)
    {
        var origin = OriginOf(e.Uri);
        var kind = e.PermissionKind.ToString();
        var saved = _c.SitePermissions.Get(origin, kind);
        bool allow;
        if (saved is bool remembered)
        {
            allow = remembered;
        }
        else if (DefaultPermission(e.PermissionKind) is bool policy)
        {
            allow = policy; // Settings > Browser: Allow or Block without asking
        }
        else
        {
            allow = MessageBox.Show(this,
                $"{origin} is requesting permission for {PermissionText(e.PermissionKind)}.\n\nAllow? This choice will be remembered for this site.",
                "Site permissions", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            _c.SitePermissions.Set(origin, kind, allow);
        }
        e.State = allow ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
    }

    private void OnCertificateError(CoreWebView2ServerCertificateErrorDetectedEventArgs e)
    {
        var host = e.RequestUri;
        if (Uri.TryCreate(e.RequestUri, UriKind.Absolute, out var u)) host = u.Host;

        if (_c.Settings.Bool(Keys.IgnoreSsl) || _sslAllowedHosts.Contains(host))
        {
            e.Action = CoreWebView2ServerCertificateErrorAction.AlwaysAllow;
            return;
        }
        // The default is cancel. Proceed only temporarily for this window session (not persisted to disk).
        var proceed = BrowserSslDialog.Ask(this, host, e.ErrorStatus.ToString()) == SslDecision.AllowForSession;
        if (proceed)
        {
            _sslAllowedHosts.Add(host);
            e.Action = CoreWebView2ServerCertificateErrorAction.AlwaysAllow;
        }
        else
        {
            e.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
        }
    }

    private static string OriginOf(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Authority) : uri;

    private static string OriginHost(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u) ? u.Host : uri;

    private static string PermissionText(CoreWebView2PermissionKind kind) => kind switch
    {
        CoreWebView2PermissionKind.Microphone => "microphone",
        CoreWebView2PermissionKind.Camera => "camera",
        CoreWebView2PermissionKind.Geolocation => "location",
        CoreWebView2PermissionKind.Notifications => "notifications",
        CoreWebView2PermissionKind.ClipboardRead => "reading the clipboard",
        _ => "another permission",
    };

    // ---------- Toolbar, menu, and key commands ----------

    /// <summary>Called by ShellService when the browser is already open and the user requests a new URL.</summary>
    public void OpenUrlInNewTab(string url) => _ = NewTabAsync(url, select: true);

    private void NewTab_Click(object sender, RoutedEventArgs e) => _ = NewTabAsync(HomeUrlOrNull());

    private void NewIncognito_Click(object sender, RoutedEventArgs e) => _shell.OpenBrowser(incognito: true);

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_current?.View?.CoreWebView2 is { CanGoBack: true } core) core.GoBack();
    }

    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        if (_current?.View?.CoreWebView2 is { CanGoForward: true } core) core.GoForward();
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => _current?.View?.CoreWebView2?.Reload();

    private void Home_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        var home = HomeUrlOrNull();
        if (home is null) ShowStartPage(_current);
        else Navigate(_current, home);
    }

    private void Go_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not null) Navigate(_current, AddressBox.Text);
    }

    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _current is not null)
        {
            Navigate(_current, AddressBox.Text);
            e.Handled = true;
        }
    }

    private void AddressBox_GotFocus(object sender, KeyboardFocusChangedEventArgs e) => AddressBox.SelectAll();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (HandleExtraKeys(e))
        {
            e.Handled = true;
            return;
        }
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (ctrl && e.Key == Key.T) { NewTab_Click(this, e); e.Handled = true; }
        else if (ctrl && e.Key == Key.W && _current is not null) { CloseTab(_current); e.Handled = true; }
        else if (ctrl && e.Key == Key.L) { AddressBox.Focus(); AddressBox.SelectAll(); e.Handled = true; }
        else if (e.Key == Key.F5) { Reload_Click(this, e); e.Handled = true; }
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is BrowserTabViewModel tab) CloseTab(tab);
    }

    private void SelectTab_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is BrowserTabViewModel tab) SelectTab(tab);
    }

    private void Menu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } cm } b)
        {
            cm.PlacementTarget = b;
            cm.IsOpen = true;
        }
    }

    private void ToggleMedia_Click(object sender, RoutedEventArgs e)
    {
        MediaPanel.Visibility = MediaPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Send a captured media item to the download engine with the same request identity the browser used:
    /// full-file URL (YouTube range parameters removed), cookies, user agent and referer. Without those, signed
    /// media URLs (YouTube, Facebook CDN) answer 403 instead of the file.
    /// </summary>
    private async void DownloadMedia_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not DetectedMedia media) return;
        try
        {
            await _shell.AddDownloadAsync(await BuildMediaRequestAsync(media));
        }
        catch (Exception ex)
        {
            AppLog.Error("Media download", ex);
            StatusText.Text = "Could not start the media download: " + ex.Message;
        }
    }

    /// <summary>
    /// Download request for a captured item (or one of its quality variants), shared by the media panel and the
    /// quality window so both carry the same cookies, user agent, Origin and Referer.
    /// </summary>
    private async Task<DownloadRequest> BuildMediaRequestAsync(DetectedMedia media)
    {
        var tab = _current;
        var core = tab?.View?.CoreWebView2;
        var url = MediaCapture.NormalizeForDownload(media.Url);

        // Only a real web page can be a Referer/Origin (not about:blank or data:); the fragment never travels.
        var pageUrl = media.PageUrl ?? tab?.Address;
        string? referer = Uri.TryCreate(pageUrl, UriKind.Absolute, out var page) && page.Scheme is "http" or "https"
            ? page.GetLeftPart(UriPartial.Query)
            : null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (referer is not null) headers["Origin"] = OriginOf(referer);

        return new DownloadRequest
        {
            Url = url,
            // "Use webpage title as file name" (03a: capture_page_title); off = the URL or media name decides.
            FileName = MediaCapture.SuggestedFileName(url, _c.Settings.Bool(Keys.CapturePageTitle) ? tab?.Title : null,
                media.Title, media.MimeType, sourceFileName: media.SourceFileName),
            // "Download subtitles inside the videos folder" (03a: idm_pref_download_subtitles_videos_folder).
            SaveDirectory = media.Quality == MediaCapture.SubtitleQuality && _c.Settings.Bool(Keys.SubtitlesInVideoFolder)
                ? WinIDM.Engine.DownloadCategories.GetSaveDirectory(_c.DefaultDownloadFolder(), WinIDM.Engine.DownloadCategories.Video)
                : _c.DefaultDownloadFolder(),
            Referer = referer,
            UserAgent = core?.Settings.UserAgent,
            CookieHeader = core is not null ? await GetCookieHeaderAsync(core, url) : null,
            Headers = headers,
        };
    }

    private void Desktop_Click(object sender, RoutedEventArgs e)
    {
        var tab = _current;
        if (tab?.View?.CoreWebView2 is not { } core) return;
        tab.IsDesktopMode = !tab.IsDesktopMode;
        core.Settings.UserAgent = tab.IsDesktopMode ? DesktopUserAgent : _defaultUserAgent;
        DesktopMenu.IsChecked = tab.IsDesktopMode;
        core.Reload();
    }

    private async void CopyLink_Click(object sender, RoutedEventArgs e)
    {
        var url = _current?.Address;
        if (string.IsNullOrEmpty(url)) return;
        if (!await ClipboardWriter.TryCopyLinkAsync(url))
            _shell.ShowMessage("Copy link", "The clipboard is in use by another app. Try again.", error: true);
    }

    private void Bookmark_Click(object sender, RoutedEventArgs e)
    {
        var tab = _current;
        if (tab is null) return;
        if (_incognito)
        {
            StatusText.Text = "Bookmarks are not saved in incognito mode.";
            return;
        }
        if (!UrlHelper.IsHttp(tab.Address))
        {
            StatusText.Text = "Only http or https pages can be bookmarked.";
            return;
        }
        if (IsBookmarked(tab.Address)) _bookmarks!.Remove(tab.Address);
        else _bookmarks!.Add(tab.Title, tab.Address);
        RefreshChrome();
    }

    private bool IsBookmarked(string url) => _bookmarks?.Contains(url) ?? false;

    private void Passwords_Click(object sender, RoutedEventArgs e) => _shell.OpenPasswords();

    private void Bookmarks_Click(object sender, RoutedEventArgs e)
    {
        if (_bookmarks is null)
        {
            StatusText.Text = "Bookmarks are not available in incognito mode.";
            return;
        }
        var store = _bookmarks;
        IEnumerable<PickItem> Items() => store.Ordered(_c.Settings.Bool(Keys.BookmarkFoldersFirst)).Select(b => new PickItem(
            b.Title, b.Url,
            (string.IsNullOrEmpty(b.Folder) ? "" : $"\U0001F4C1 {b.Folder}  |  ") + b.Added.LocalDateTime.ToString("yyyy-MM-dd HH:mm")));
        new ListPickerWindow("Bookmarks", "Double-click to open. Select an item to move it to a folder or remove it.", Items(),
            open: url => { if (_current is not null) Navigate(_current, url); },
            remove: url => store.Remove(url),
            // IDM+ "Delete all bookmarks"; the list window asks before clearing.
            clear: () => { store.Restore([], overwrite: true); RefreshChrome(); },
            extra: ("Move to folder…", url =>
            {
                var current = store.All.FirstOrDefault(b => b.Url == url)?.Folder;
                var folder = _shell.Prompt("Bookmark folder", "Folder name (empty = top level):", current);
                if (folder is null) return null;
                store.MoveToFolder(url, folder);
                return Items();
            })) { Owner = this }.Show();
    }

    private void History_Click(object sender, RoutedEventArgs e)
    {
        if (_history is null)
        {
            StatusText.Text = "History is not saved in incognito mode.";
            return;
        }
        var items = _history.All.Take(500).Select(h => new PickItem(h.Title, h.Url, h.Visited.LocalDateTime.ToString("yyyy-MM-dd HH:mm")));
        new ListPickerWindow("History", "Double-click to reopen a page.", items,
            open: url => { if (_current is not null) Navigate(_current, url); },
            clear: () => _history.Clear()) { Owner = this }.Show();
    }

    private async void ClearData_Click(object sender, RoutedEventArgs e)
    {
        if (_current?.View?.CoreWebView2 is not { } core) return;
        var kinds = CoreWebView2BrowsingDataKinds.DiskCache | CoreWebView2BrowsingDataKinds.Cookies |
                    CoreWebView2BrowsingDataKinds.AllDomStorage | CoreWebView2BrowsingDataKinds.BrowsingHistory;
        try
        {
            await core.Profile.ClearBrowsingDataAsync(kinds);
            _history?.Clear();
            StatusText.Text = "Browsing data cleared.";
        }
        catch (Exception ex)
        {
            AppLog.Error("Clear data", ex);
            StatusText.Text = "Failed to clear data. See the log.";
        }
    }
}
