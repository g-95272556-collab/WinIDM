using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using WinIDM.App.Services;
using WinIDM.App.ViewModels;
using WinIDM.Browser;
using WinIDM.Core.Models;

namespace WinIDM.App.Views;

/// <summary>
/// Side panel next to the page: an AI provider's own site (Claude, ChatGPT, DeepSeek, Kimi) or the side panel of
/// an installed browser extension (the official Claude and ChatGPT extensions from the Chrome Web Store).
/// <para>
/// Sign-in always happens on the provider's own pages or in the extension's own code, so a paid account works
/// the same way as in Chrome and WinIDM never handles the provider's tokens. The panel shares the window's
/// WebView2 profile: a sign-in made in a browser tab is seen by the panel and the other way round.
/// </para>
/// </summary>
public partial class BrowserWindow
{
    /// <summary>One entry of the provider list.</summary>
    /// <param name="Key"><c>web:…</c>, <c>ext:&lt;id&gt;</c> or <c>install:&lt;id&gt;</c>.</param>
    private sealed record SidePanelProvider(string Key, string Title, string? Url = null, string? ExtensionId = null)
    {
        /// <summary>The list item's accessible name (screen readers, UI Automation).</summary>
        public override string ToString() => Title;
    }

    private static readonly SidePanelProvider[] WebProviders =
    {
        new("web:claude", "Claude (claude.ai)", "https://claude.ai/new"),
        new("web:chatgpt", "ChatGPT (chatgpt.com)", "https://chatgpt.com/"),
        new("web:deepseek", "DeepSeek (chat.deepseek.com)", "https://chat.deepseek.com/"),
        new("web:kimi", "Kimi (kimi.com)", "https://www.kimi.com/"),
    };

    /// <summary>Official extensions offered before they are installed (Chrome Web Store ID, name, publisher).</summary>
    internal static readonly (string Id, string Name, string Publisher)[] OfficialExtensions =
    {
        ("fcoeoabgfenejglbffodgkkbkcdhcgfn", "Claude", "Anthropic"),
        ("hehggadaopoacecdllhhajmbjkdcmajg", "ChatGPT", "OpenAI"),
    };

    private WebView2? _sidePanelView;
    private Task? _sidePanelViewReady;
    private bool _extensionsEnabled;
    private Task? _extensionSync;
    private bool _suppressProviderChange;
    private int _panelLoadSeq;

    private bool ExtensionsAvailable => !_incognito && _extensionsEnabled;

    private bool SidePanelIsOpen => SidePanel.Visibility == Visibility.Visible;

    private SidePanelProvider? SelectedProvider => SidePanelProviderBox.SelectedItem as SidePanelProvider;

    private void InitSidePanel()
    {
        RefreshProviderList();
        if (_c.Settings.Bool(Keys.SidePanelOpen)) SetSidePanelOpen(true);
    }

    // ---------- Open, close, resize

    private void ToggleSidePanel_Click(object sender, RoutedEventArgs e) => SetSidePanelOpen(!SidePanelIsOpen);

    private void SetSidePanelOpen(bool open)
    {
        SidePanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        SidePanelSplitter.Visibility = SidePanel.Visibility;
        SidePanelColumn.MinWidth = open ? 280 : 0;
        SidePanelColumn.Width = open ? new GridLength(Math.Clamp(_c.Settings.Int(Keys.SidePanelWidth), 280, 900)) : new GridLength(0);
        if (!_incognito) _c.Settings.Set(Keys.SidePanelOpen, open);
        if (open && SelectedProvider is { } p) _ = ShowProviderAsync(p);
    }

    private void SidePanelSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (SidePanelIsOpen) _c.Settings.Set(Keys.SidePanelWidth, (int)SidePanelColumn.ActualWidth);
    }

    private void SidePanelReload_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProvider is { } p) _ = ShowProviderAsync(p);
    }

    private void SidePanelOpenInTab_Click(object sender, RoutedEventArgs e)
    {
        var url = _sidePanelView?.CoreWebView2?.Source;
        if (!UrlHelper.IsHttp(url)) url = SelectedProvider?.Url;
        if (!string.IsNullOrEmpty(url)) _ = NewTabAsync(url, select: true);
    }

    // ---------- Provider list

    private void RefreshProviderList(string? selectKey = null)
    {
        var items = new List<SidePanelProvider>();
        if (ExtensionsAvailable)
        {
            var installed = _c.Extensions.All;
            foreach (var ext in installed.Where(x => x.Enabled && x.PanelPage is not null))
                items.Add(new SidePanelProvider("ext:" + ext.Id, ext.Name + " (extension)", ExtensionId: ext.Id));
            foreach (var (id, name, publisher) in OfficialExtensions.Where(o => installed.All(x => x.Id != o.Id)))
                items.Add(new SidePanelProvider("install:" + id, $"Install the {name} extension ({publisher})…", ExtensionId: id));
        }
        items.AddRange(WebProviders);

        var key = selectKey ?? SelectedProvider?.Key ?? _c.Settings.Text(Keys.SidePanelProvider);
        _suppressProviderChange = true;
        SidePanelProviderBox.ItemsSource = items;
        SidePanelProviderBox.SelectedItem = items.FirstOrDefault(p => p.Key == key && !p.Key.StartsWith("install:", StringComparison.Ordinal))
                                            ?? items.FirstOrDefault(p => p.Key.StartsWith("ext:", StringComparison.Ordinal))
                                            ?? items.First(p => p.Key.StartsWith("web:", StringComparison.Ordinal));
        _suppressProviderChange = false;
    }

    private void SidePanelProvider_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressProviderChange || SelectedProvider is not { } p) return;
        bool install = p.Key.StartsWith("install:", StringComparison.Ordinal);
        if (!install) _c.Settings.Set(Keys.SidePanelProvider, p.Key);
        // After the selection change completes: the install question is a modal dialog.
        if (SidePanelIsOpen || install) Dispatcher.BeginInvoke(() => _ = ShowProviderAsync(p));
    }

    // ---------- Panel view

    private Task EnsureSidePanelViewAsync() => _sidePanelViewReady ??= CreateSidePanelViewAsync();

    private async Task CreateSidePanelViewAsync()
    {
        var view = new WebView2();
        SidePanelHost.Children.Add(view);
        await view.EnsureCoreWebView2Async(await _env!);
        var core = view.CoreWebView2;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.NewWindowRequested += OnSidePanelNewWindow;
        core.DownloadStarting += (_, e) => _ = OnSidePanelDownloadAsync(core, e);
        core.NavigationCompleted += (_, e) =>
        {
            if (!e.IsSuccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                SidePanelStatus.Text = "The page did not load: " + e.WebErrorStatus;
        };
        _sidePanelView = view;
        _ = SyncExtensionsAsync(core);
    }

    private async Task ShowProviderAsync(SidePanelProvider provider)
    {
        int seq = ++_panelLoadSeq;
        try
        {
            if (provider.Key.StartsWith("install:", StringComparison.Ordinal))
            {
                var installed = await InstallExtensionAsync(provider.ExtensionId!, confirm: true);
                RefreshProviderList(installed is null ? _c.Settings.Text(Keys.SidePanelProvider) : "ext:" + installed.Id);
                if (installed is not null && SelectedProvider is { } next)
                {
                    _c.Settings.Set(Keys.SidePanelProvider, next.Key);
                    if (!SidePanelIsOpen) SetSidePanelOpen(true);
                    else await ShowProviderAsync(next);
                }
                return;
            }

            await EnsureSidePanelViewAsync();
            var core = _sidePanelView!.CoreWebView2;
            SidePanelOpenTabButton.IsEnabled = provider.Url is not null;

            if (provider.ExtensionId is { } id)
            {
                await SyncExtensionsAsync(core);
                var ext = _c.Extensions.Find(id);
                if (ext is null) { SidePanelStatus.Text = "The extension is no longer installed."; return; }
                IReadOnlyList<string> pages = ext.PanelPages.Count > 0 ? ext.PanelPages
                    : ext.PopupPage is { } popup ? new[] { popup } : Array.Empty<string>();
                SidePanelStatus.Text = $"{ext.Name} {ext.Version}: sign in from the panel; its sign-in page opens as a tab.";
                for (int i = 0; i < pages.Count; i++)
                {
                    if (seq != _panelLoadSeq) return;
                    await NavigateAndWaitAsync(core, $"chrome-extension://{id}/{pages[i]}");
                    // Some extensions start on a placeholder that waits for Chrome's toolbar button; try the next page.
                    if (seq != _panelLoadSeq || await PageHasContentAsync(core, TimeSpan.FromSeconds(i == 0 ? 20 : 8))) return;
                }
                // Nothing rendered text (it may still be signing in): stay on the most likely page.
                if (pages.Count > 1 && seq == _panelLoadSeq) core.Navigate($"chrome-extension://{id}/{pages[0]}");
                return;
            }

            SidePanelStatus.Text = "Sign in on the provider's own page. Your account stays with the provider.";
            core.Navigate(provider.Url!);
        }
        catch (Exception ex)
        {
            AppLog.Error("Side panel", ex);
            SidePanelStatus.Text = "Side panel error: " + ex.Message;
        }
    }

    private static async Task NavigateAndWaitAsync(CoreWebView2 core, string url)
    {
        var done = new TaskCompletionSource();
        void OnDone(object? s, CoreWebView2NavigationCompletedEventArgs e) => done.TrySetResult();
        core.NavigationCompleted += OnDone;
        try
        {
            core.Navigate(url);
            await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(20)));
        }
        finally
        {
            core.NavigationCompleted -= OnDone;
        }
    }

    /// <summary>True once the page shows any text within <paramref name="wait"/> (script-built pages render late).</summary>
    private static async Task<bool> PageHasContentAsync(CoreWebView2 core, TimeSpan wait)
    {
        for (var until = DateTime.UtcNow + wait; DateTime.UtcNow < until;)
        {
            await Task.Delay(750);
            var json = await core.ExecuteScriptAsync("(document.body&&document.body.innerText||'').trim().length");
            if (int.TryParse(json, out var n) && n > 0) return true;
        }
        return false;
    }

    /// <summary>
    /// Sign-in popups (a window with a size, as OAuth flows open) keep WebView2's own popup so the page that
    /// opened them gets the result back. Plain links open as WinIDM tabs.
    /// </summary>
    private void OnSidePanelNewWindow(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        if (IsExtensionWindowRequest(e))
        {
            _ = OpenExtensionTabAsync(e);
            return;
        }
        if (e.WindowFeatures.HasSize || !UrlHelper.IsHttp(e.Uri)) return;
        e.Handled = true;
        _ = NewTabAsync(e.Uri, select: true);
    }

    /// <summary>Files from the panel go to the download engine; blob/data downloads stay with WebView2.</summary>
    private async Task OnSidePanelDownloadAsync(CoreWebView2 core, CoreWebView2DownloadStartingEventArgs e)
    {
        var url = e.DownloadOperation.Uri;
        if (!UrlHelper.IsHttp(url)) return;
        e.Cancel = true;
        var deferral = e.GetDeferral();
        try
        {
            var req = new DownloadRequest
            {
                Url = url,
                FileName = UrlHelper.FileNameFromUrl(url),
                SaveDirectory = _c.DefaultDownloadFolder(),
                Referer = UrlHelper.IsHttp(core.Source) ? core.Source : null,
                UserAgent = core.Settings.UserAgent,
                CookieHeader = await GetCookieHeaderAsync(core, url),
            };
            if (_c.Settings.Bool(Keys.SdeBrowserDownload)) await _c.Engine.AddAsync(req);
            else await _shell.AddDownloadAsync(req);
        }
        catch (Exception ex)
        {
            AppLog.Error("Download from side panel", ex);
        }
        finally
        {
            deferral.Complete();
        }
    }

    // ---------- Tabs opened by extensions

    /// <summary>When an extension was last installed or switched on; its welcome or sign-in tab follows shortly.</summary>
    private DateTime _extensionChangedUtc = DateTime.MinValue;

    /// <summary>
    /// A tab an extension asked for (<c>chrome.tabs.create</c>, e.g. its sign-in page). WebView2 reports such a
    /// request as coming from the active tab's page, with nothing that tells it apart from a link the user
    /// clicked, so it is recognised by where it can only have come from: an extension page, WinIDM's own start
    /// page (which has no script that opens windows), or the moments right after an extension was installed.
    /// </summary>
    private bool IsExtensionWindowRequest(CoreWebView2NewWindowRequestedEventArgs e, BrowserTabViewModel? tab = null)
    {
        if (!ExtensionsAvailable || !_c.Extensions.All.Any(x => x.Enabled)) return false;
        try
        {
            var source = e.OriginalSourceFrameInfo?.Source;
            return source is null
                   || source.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)
                   || tab is { IsStartPage: true } && source.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                   || DateTime.UtcNow - _extensionChangedUtc < TimeSpan.FromSeconds(30);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or NotImplementedException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Open the extension's tab as a WinIDM tab and hand it to WebView2 as the new window, so the extension gets
    /// that tab back from its call (its sign-in flow watches it). No popup question: the user installed the extension.
    /// </summary>
    private async Task OpenExtensionTabAsync(CoreWebView2NewWindowRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            var tab = await NewTabAsync(null, select: true, blank: true);
            if (tab.View?.CoreWebView2 is { } core)
            {
                e.NewWindow = core;
                e.Handled = true;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Extension tab", ex);
        }
        finally
        {
            deferral.Complete();
        }
    }

    // ---------- Extensions in the WebView2 profile

    private async Task<CoreWebView2Profile?> ProfileAsync()
    {
        if (!ExtensionsAvailable) return null;
        if (_current?.View?.CoreWebView2 is { } tabCore) return tabCore.Profile;
        await EnsureSidePanelViewAsync();
        return _sidePanelView?.CoreWebView2?.Profile;
    }

    /// <summary>Once per window: add installed extensions the profile does not have yet, and match enabled states.</summary>
    private Task SyncExtensionsAsync(CoreWebView2 core) => _extensionSync ??= SyncExtensionsCoreAsync(core.Profile);

    private async Task SyncExtensionsCoreAsync(CoreWebView2Profile profile)
    {
        if (!ExtensionsAvailable) return;
        try
        {
            var loaded = (await profile.GetBrowserExtensionsAsync()).ToDictionary(x => x.Id);
            foreach (var ext in _c.Extensions.All)
            {
                if (!loaded.TryGetValue(ext.Id, out var live))
                {
                    if (!ext.Enabled) continue;
                    live = await profile.AddBrowserExtensionAsync(ext.Folder);
                }
                if (live.IsEnabled != ext.Enabled) await live.EnableAsync(ext.Enabled);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Load browser extensions", ex);
        }
    }

    /// <summary>
    /// Download the extension from the Chrome Web Store, verify the publisher's signature, unpack it and add it to
    /// the profile. An installed extension is replaced by the new version (its stored data may be reset).
    /// </summary>
    internal async Task<ExtensionInfo?> InstallExtensionAsync(string id, bool confirm)
    {
        if (!ExtensionsAvailable)
        {
            MessageBox.Show(this, "Extensions are available in normal browser windows only.", "Extensions",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }
        if (confirm)
        {
            var official = OfficialExtensions.FirstOrDefault(o => o.Id == id);
            var what = official.Id is null ? $"extension {id}" : $"the official {official.Name} extension by {official.Publisher}";
            var answer = MessageBox.Show(this,
                $"Download {what} from the Chrome Web Store?\n\n" +
                "The package is checked against the publisher's signature before it is installed. " +
                "Extensions like this can read and change the pages you open and act in your tabs.",
                "Install extension", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK) return null;
        }

        try
        {
            SidePanelStatus.Text = "Downloading the extension…";
            StatusText.Text = SidePanelStatus.Text;
            var env = await _env!;
            var info = await _c.Extensions.InstallFromStoreAsync(id, env.BrowserVersionString);

            if (await ProfileAsync() is { } profile)
            {
                foreach (var old in (await profile.GetBrowserExtensionsAsync()).Where(x => x.Id == id))
                    await old.RemoveAsync();
                _extensionChangedUtc = DateTime.UtcNow;
                await profile.AddBrowserExtensionAsync(info.Folder);
                _c.Extensions.PruneOldVersions(id);
            }
            if (confirm)
                MessageBox.Show(this, BuildPermissionSummary(info), "Extension installed", MessageBoxButton.OK, MessageBoxImage.Information);
            SidePanelStatus.Text = $"{info.Name} {info.Version} installed.";
            StatusText.Text = SidePanelStatus.Text;
            RefreshProviderList();
            return info;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException
                                       or TaskCanceledException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            AppLog.Error("Install extension", ex);
            SidePanelStatus.Text = "The extension was not installed: " + ex.Message;
            StatusText.Text = SidePanelStatus.Text;
            MessageBox.Show(this, "The extension was not installed.\n\n" + ex.Message, "Install extension",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
    }

    internal static string BuildPermissionSummary(ExtensionInfo info)
    {
        var notes = new List<string>();
        if (info.HostPermissions.Any(h => h is "<all_urls>" or "*://*/*" or "https://*/*"))
            notes.Add("read and change every page you open");
        if (info.Permissions.Contains("debugger")) notes.Add("control pages through the debugger (click, type, navigate)");
        if (info.Permissions.Contains("tabs") || info.Permissions.Contains("tabGroups")) notes.Add("see and arrange your tabs");
        if (info.Permissions.Contains("downloads")) notes.Add("start downloads");
        if (info.Permissions.Contains("history")) notes.Add("read your browsing history");
        if (info.Permissions.Contains("bookmarks")) notes.Add("read your bookmarks");
        var text = $"{info.Name} {info.Version} ({info.Id})";
        return notes.Count == 0 ? text : text + "\n\nIt can:\n• " + string.Join("\n• ", notes);
    }

    internal async Task SetExtensionEnabledAsync(string id, bool enabled)
    {
        _c.Extensions.SetEnabled(id, enabled);
        if (enabled) _extensionChangedUtc = DateTime.UtcNow;
        if (await ProfileAsync() is { } profile)
        {
            var live = (await profile.GetBrowserExtensionsAsync()).FirstOrDefault(x => x.Id == id);
            if (live is null && enabled && _c.Extensions.Find(id) is { } ext) await profile.AddBrowserExtensionAsync(ext.Folder);
            else if (live is not null && live.IsEnabled != enabled) await live.EnableAsync(enabled);
        }
        RefreshProviderList();
    }

    internal async Task RemoveExtensionAsync(string id)
    {
        if (await ProfileAsync() is { } profile)
        {
            foreach (var live in (await profile.GetBrowserExtensionsAsync()).Where(x => x.Id == id))
                await live.RemoveAsync();
        }
        _c.Extensions.Remove(id);
        if (SelectedProvider?.ExtensionId == id) _sidePanelView?.CoreWebView2?.Navigate("about:blank");
        RefreshProviderList();
    }

    /// <summary>Show an installed extension in the side panel.</summary>
    internal void ShowExtensionInPanel(string id)
    {
        RefreshProviderList("ext:" + id);
        if (!SidePanelIsOpen) SetSidePanelOpen(true);
        else if (SelectedProvider is { } p) _ = ShowProviderAsync(p);
    }

    internal void OpenExtensionPageInTab(string id, string page) => _ = NewTabAsync($"chrome-extension://{id}/{page}", select: true);

    private void OpenExtensions_Click(object sender, RoutedEventArgs e) =>
        new ExtensionsWindow(_c, this) { Owner = this }.Show();
}
