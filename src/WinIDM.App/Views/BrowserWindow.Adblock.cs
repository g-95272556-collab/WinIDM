using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using WinIDM.App.Services;
using WinIDM.App.ViewModels;
using WinIDM.Core.Contracts;
using WinIDM.Core.Models;

namespace WinIDM.App.Views;

/// <summary>
/// In-browser ad blocker: request filtering (ABP/uBO engine and hosts), per-site allowlist,
/// cosmetic filters, and the manual element picker.
/// </summary>
public partial class BrowserWindow
{
    private BrowserServices BrowserSvc => BrowserServices.For(_c);

    private bool IsAdblockOnFor(BrowserTabViewModel tab)
    {
        if (!_c.Settings.Bool(Keys.AdBlock)) return false;
        var host = tab.View?.Source?.Host;
        return string.IsNullOrEmpty(host) || !BrowserSvc.Rules.IsSiteAdblockOff(host);
    }

    /// <summary>
    /// Resource type for the filters. Iframes are identified through <c>Sec-Fetch-Dest</c> (iframe or frame),
    /// which is mapped to <see cref="ResourceType.Subdocument"/>. Other documents stay as Document.
    /// </summary>
    private static ResourceType ResourceTypeFor(CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (e.ResourceContext == CoreWebView2WebResourceContext.Document)
        {
            var dest = HeaderOrEmpty(e.Request.Headers, "Sec-Fetch-Dest");
            return dest is "iframe" or "frame" ? ResourceType.Subdocument : ResourceType.Document;
        }
        return ResourceTypeMap.From(e.ResourceContext);
    }

    /// <summary>Block the request through hosts or the filter engine. Returns true if a 403 response has been set.</summary>
    private bool TryBlockByAdblock(BrowserTabViewModel tab, CoreWebView2 core, CoreWebView2WebResourceRequestedEventArgs e, string url)
    {
        if (!IsAdblockOnFor(tab)) return false;
        var pageUrl = tab.View?.Source?.ToString() ?? url;
        var type = ResourceTypeFor(e);
        var svc = BrowserSvc;
        bool blocked = svc.Hosts.TryBlock(url, pageUrl) || svc.Engine.ShouldBlock(url, pageUrl, type);
        if (!blocked) return false;
        e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
        return true;
    }

    /// <summary>
    /// Document-start style script for the page. Wrapped so it runs only in the top frame.
    /// Returns null if the blocker is off for the site or there are no rules.
    /// </summary>
    private string? BuildCosmeticDocumentScript(string url)
    {
        if (!_c.Settings.Bool(Keys.AdBlock) || !UrlHelper.IsHttp(url)) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
        if (BrowserSvc.Rules.IsSiteAdblockOff(u.Host)) return null;
        var css = BrowserCosmetics.BuildStyleSheet(
            BrowserSvc.Engine.BuildCosmeticStyleSheet(u.Host),
            BrowserSvc.Rules.ManualSelectors(u.Host));
        return css.Length == 0 ? null : BrowserCosmetics.DocumentStyleScript(css);
    }

    // ---------- Menu

    private void MainMenu_Opened(object sender, RoutedEventArgs e)
    {
        RebuildSearchEngineMenu();
        var host = _current?.View?.Source?.Host;
        SiteAdblockItem.IsEnabled = !string.IsNullOrEmpty(host);
        SiteAdblockItem.IsChecked = !string.IsNullOrEmpty(host) && _c.Settings.Bool(Keys.AdBlock)
                                    && !BrowserSvc.Rules.IsSiteAdblockOff(host);
    }

    /// <summary>"Ad blocker for this site" checkbox: checked = on. The setting is stored per host and the tab is reloaded.</summary>
    private void SiteAdblock_Click(object sender, RoutedEventArgs e)
    {
        if (_current?.View is not { } view || view.Source is not { } src) return;
        bool enabled = SiteAdblockItem.IsChecked;
        BrowserSvc.Rules.SetSiteAdblockOff(src.Host, off: !enabled);
        StatusText.Text = enabled
            ? $"Ad blocker re-enabled for {src.Host}."
            : $"Ad blocker disabled for {src.Host} (site allowlist).";
        view.CoreWebView2?.Reload();
    }

    private void OpenBlocker_Click(object sender, RoutedEventArgs e) =>
        new BrowserBlockerWindow(_c) { Owner = this }.Show();

    // ---------- Manual element picker

    private async void StartElementPicker_Click(object sender, RoutedEventArgs e)
    {
        if (_current?.View?.CoreWebView2 is not { } core || !UrlHelper.IsHttp(core.Source))
        {
            StatusText.Text = "The element picker works only on HTTP(S) pages.";
            return;
        }
        if (await RunPageScriptAsync(core, BrowserCosmetics.PickerScript))
            StatusText.Text = "Click an element to hide it. Press Esc to cancel.";
    }

    /// <summary>Message from the page for the element picker. Main frame of the tab only; other messages are ignored.</summary>
    private void OnPageMessageForAdblock(BrowserTabViewModel tab, string webMessageJson)
    {
        string? kind;
        string? selector;
        try
        {
            using var doc = JsonDocument.Parse(webMessageJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            kind = StringField(doc.RootElement, "k");
            selector = StringField(doc.RootElement, "selector");
        }
        catch (JsonException)
        {
            return;
        }

        switch (kind)
        {
            case "hide-pick-cancel":
                StatusText.Text = "Element selection canceled.";
                break;
            case "hide-pick":
                ConfirmManualRule(tab, selector);
                break;
        }
    }

    private void ConfirmManualRule(BrowserTabViewModel tab, string? selector)
    {
        if (!BrowserCosmetics.IsSafeSelector(selector) || tab.View?.Source is not { } src)
        {
            StatusText.Text = "The selector is unsafe or invalid. Pick another element.";
            return;
        }
        var host = src.Host;
        var answer = MessageBox.Show(this,
            $"Save a hide rule for {host}?\n\n{selector}\n\n" +
            "Matching elements will be hidden on this site. You can delete the rule in Ad blocker.",
            "Hide element", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        BrowserSvc.Rules.AddManualSelector(host, selector!);
        if (tab.View?.CoreWebView2 is { } core)
            _ = RunPageScriptAsync(core, BrowserCosmetics.ImmediateStyleScript(selector!));
        StatusText.Text = "Hide rule saved.";
    }

    /// <summary>Run a script on the page safely. Returns false if WebView2 rejects it (e.g. the tab was closed).</summary>
    private static async Task<bool> RunPageScriptAsync(CoreWebView2 core, string script)
    {
        try
        {
            await core.ExecuteScriptAsync(script);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or ExternalException)
        {
            AppLog.Error("Page script", ex);
            return false;
        }
    }
}
