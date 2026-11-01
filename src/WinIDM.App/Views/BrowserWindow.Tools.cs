using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using WinIDM.App.Services;
using WinIDM.Browser;

namespace WinIDM.App.Views;

/// <summary>
/// Browser tools: reader mode (TTS), home page shortcuts, search engines, and reverse image search as links.
/// </summary>
public partial class BrowserWindow
{
    /// <summary>Connect the WebView2 context menu for reverse image search. No upload is performed.</summary>
    private void WireBrowserTools(CoreWebView2 core)
    {
        core.ContextMenuRequested += (_, e) => OnContextMenuRequested(core, e);
    }

    private void OnContextMenuRequested(CoreWebView2 core, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        var target = e.ContextMenuTarget;
        if (target.Kind != CoreWebView2ContextMenuTargetKind.Image || !UrlHelper.IsHttp(target.SourceUri)) return;

        e.MenuItems.Add(core.Environment.CreateContextMenuItem("-", null, CoreWebView2ContextMenuItemKind.Separator));
        foreach (var (provider, url) in BrowserImageSearch.Build(target.SourceUri))
        {
            var item = core.Environment.CreateContextMenuItem(
                "Search this image with " + provider, null, CoreWebView2ContextMenuItemKind.Command);
            // The link is opened in a new tab by the user. The image is not uploaded silently.
            item.CustomItemSelected += (_, _) => _ = NewTabAsync(url, select: true);
            e.MenuItems.Add(item);
        }
    }

    // ---------- Reader mode

    private async void OpenReader_Click(object sender, RoutedEventArgs e)
    {
        if (_current?.View?.CoreWebView2 is not { } core || !UrlHelper.IsHttp(core.Source))
        {
            StatusText.Text = "Reader mode works only on HTTP(S) pages.";
            return;
        }
        try
        {
            var json = await core.ExecuteScriptAsync("document.documentElement ? document.documentElement.outerHTML : ''");
            var html = JsonSerializer.Deserialize<string>(json) ?? "";
            var services = BrowserServices.For(_c);
            var article = services.PageTools.ExtractReadableArticle(html, core.Source);
            if (article.Text.Length == 0)
            {
                StatusText.Text = "No article content detected on this page.";
                return;
            }
            new BrowserReaderWindow(article, services.Prefs) { Owner = this }.Show();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or COMException or ExternalException)
        {
            AppLog.Error("Reader mode", ex);
            StatusText.Text = "Failed to open reader mode. See the log.";
        }
    }

    // ---------- Home page shortcuts

    private void OpenShortcuts_Click(object sender, RoutedEventArgs e) =>
        new BrowserShortcutsWindow(BrowserServices.For(_c).Shortcuts, RefreshStartPageIfShown) { Owner = this }.Show();

    private void RefreshStartPageIfShown()
    {
        if (_current is { IsStartPage: true } tab) ShowStartPage(tab);
    }

    // ---------- Search engine

    /// <summary>Populate the search engine submenu with a check mark on the current engine. Called when the menu opens.</summary>
    private void RebuildSearchEngineMenu()
    {
        SearchEngineMenu.Items.Clear();
        var current = _c.Settings.Text(Keys.SearchEngine);
        foreach (var engine in BrowserSearchEngines.All)
        {
            var item = new MenuItem
            {
                Header = engine.Name,
                IsCheckable = true,
                IsChecked = string.Equals(engine.Name, current, StringComparison.OrdinalIgnoreCase),
            };
            var name = engine.Name;
            item.Click += (_, _) =>
            {
                _c.Settings.Set(Keys.SearchEngine, name);
                RebuildSearchEngineMenu();
                StatusText.Text = $"Search engine changed to {name}.";
            };
            SearchEngineMenu.Items.Add(item);
        }
    }
}
