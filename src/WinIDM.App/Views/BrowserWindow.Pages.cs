using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using WinIDM.App.Services;
using WinIDM.App.ViewModels;
using WinIDM.Browser;
using WinIDM.Core.Models;

namespace WinIDM.App.Views;

/// <summary>
/// Request privacy (DNT, Save-Data, third-party cookie blocking), saved pages (MHTML/PDF),
/// and HTML bookmark import/export.
/// </summary>
public partial class BrowserWindow
{
    private const long MaxBookmarkImportBytes = 20 * 1024 * 1024;

    /// <summary>Set privacy headers and strip cookies for third-party sources (not navigation documents).</summary>
    private void ApplyRequestPrivacy(BrowserTabViewModel tab, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var headers = e.Request.Headers;
        if (_c.Settings.Bool(Keys.DoNotTrack)) headers.SetHeader("DNT", "1");
        if (_c.Settings.Bool(Keys.SaveData)) headers.SetHeader("Save-Data", "on");
        if (_c.Settings.Bool(Keys.GlobalPrivacyControl)) headers.SetHeader("Sec-GPC", "1");

        // Cross-site referrer cap. Media, XHR and fetch are left alone: players fetch HLS/DASH playlists and
        // segments that way, and some media CDNs check the full Referer before they serve anything.
        if (_c.Settings.Bool(Keys.CapReferrer) && ShieldsActive(tab.View?.Source?.ToString())
            && e.ResourceContext is not (CoreWebView2WebResourceContext.Media or CoreWebView2WebResourceContext.XmlHttpRequest
                or CoreWebView2WebResourceContext.Fetch)
            && ReferrerCap.Cap(BrowserWindow.HeaderOrEmpty(headers, "Referer"), e.Request.Uri) is { } capped)
        {
            headers.SetHeader("Referer", capped);
        }

        // Navigation documents are not cookie-blocked (that would break sign-in). Blocking applies to subresources only.
        if (_c.Settings.Bool(Keys.BlockThirdPartyCookies)
            && e.ResourceContext != CoreWebView2WebResourceContext.Document
            && IsThirdPartyRequest(tab, e.Request.Uri))
        {
            headers.RemoveHeader("Cookie");
        }
    }

    private static bool IsThirdPartyRequest(BrowserTabViewModel tab, string requestUrl)
    {
        var page = tab.View?.Source;
        if (page is null || !Uri.TryCreate(requestUrl, UriKind.Absolute, out var req)) return false;
        return !string.Equals(RegistrableDomain(page.Host), RegistrableDomain(req.Host), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Approximate registrable domain (last two labels). Does not use the full Public Suffix List.</summary>
    private static string RegistrableDomain(string host)
    {
        var parts = host.Split('.');
        return parts.Length <= 2 ? host : string.Join('.', parts[^2..]);
    }

    // ---------------------------------------------------------- saved pages

    private async void SavePage_Click(object sender, RoutedEventArgs e)
    {
        if (_current?.View?.CoreWebView2 is not { } core || !UrlHelper.IsHttp(core.Source))
        {
            StatusText.Text = "Only HTTP(S) pages can be saved.";
            return;
        }

        // IDM+ "If possible use webpage title as file name while saving webpages" (uwtaffsp).
        var title = string.IsNullOrWhiteSpace(core.DocumentTitle) || !_c.Settings.Bool(Keys.SavePageTitleName)
            ? "page" : core.DocumentTitle;
        var invalid = Path.GetInvalidFileNameChars();
        var baseName = new string(title.Select(ch => invalid.Contains(ch) ? '_' : ch).Take(120).ToArray());
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save page",
            Filter = "MHTML web archive (*.mhtml)|*.mhtml|PDF document (*.pdf)|*.pdf",
            FileName = baseName + ".mhtml",
        };
        // IDM+ "Set directory for saving web pages" (wbpsf); empty = the dialog's own last folder.
        var folder = _c.Settings.Text(Keys.SavedPagesFolder).Trim();
        if (folder.Length > 0 && Directory.Exists(folder)) dlg.InitialDirectory = folder;
        if (dlg.ShowDialog(this) != true) return;

        var isPdf = dlg.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
        try
        {
            if (isPdf)
            {
                await core.PrintToPdfAsync(dlg.FileName);
            }
            else
            {
                await SaveMhtmlAsync(core, dlg.FileName);
            }
            _c.SavedPages.Add(new SavedPage(title, core.Source, dlg.FileName, isPdf ? "PDF" : "MHTML", DateTimeOffset.Now));
            StatusText.Text = "Page saved and registered in Saved pages.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException or InvalidDataException)
        {
            AppLog.Error("Save page", ex);
            StatusText.Text = "Failed to save the page. See the log for details.";
        }
    }

    /// <summary>MHTML through the DevTools Protocol (Page.captureSnapshot). The output is MHTML text.</summary>
    private static async Task SaveMhtmlAsync(CoreWebView2 core, string path)
    {
        var json = await core.CallDevToolsProtocolMethodAsync("Page.captureSnapshot", "{\"format\":\"mhtml\"}");
        using var doc = JsonDocument.Parse(json);
        var data = doc.RootElement.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String
            ? d.GetString()
            : null;
        if (string.IsNullOrEmpty(data)) throw new InvalidDataException("CDP did not return MHTML data.");
        await File.WriteAllTextAsync(path, data, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private void SavedPages_Click(object sender, RoutedEventArgs e) => _shell.OpenSavedPages();

    // ---------------------------------------------------------- HTML bookmarks

    private void ImportBookmarks_Click(object sender, RoutedEventArgs e)
    {
        if (_bookmarks is null)
        {
            StatusText.Text = "Bookmarks are not available in incognito mode.";
            return;
        }
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import bookmarks (HTML)",
            Filter = "HTML bookmark files (*.html;*.htm)|*.html;*.htm|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            if (new FileInfo(dlg.FileName).Length > MaxBookmarkImportBytes)
            {
                StatusText.Text = "The bookmark file is too large (20 MB limit).";
                return;
            }
            var html = File.ReadAllText(dlg.FileName);
            int added = 0, skipped = 0;
            foreach (var (title, url) in ExtractBookmarks(html))
            {
                if (url is null) { skipped++; continue; }
                if (_bookmarks.Contains(url)) continue;
                _bookmarks.Add(string.IsNullOrWhiteSpace(title) ? url : title, url);
                added++;
            }
            StatusText.Text = $"Bookmark import: {added} added, {skipped} skipped.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            AppLog.Error("Import bookmarks", ex);
            StatusText.Text = "Import failed. See the log for details.";
        }
    }

    /// <summary>
    /// Collect every <c>&lt;A HREF&gt;</c> link from the bookmark file (folder structure is flattened because
    /// <see cref="BookmarkStore"/> is a flat list). Only HTTP(S) is accepted; anything else counts as skipped (null url).
    /// </summary>
    private static IEnumerable<(string Title, string? Url)> ExtractBookmarks(string html)
    {
        int count = 0;
        foreach (Match m in BookmarkAnchor.Matches(html))
        {
            if (++count > MaxBookmarkItems) yield break;
            var href = WebUtility.HtmlDecode(
                m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value).Trim();
            if (!UrlHelper.IsHttp(href))
            {
                yield return ("", null);
                continue;
            }
            var title = WebUtility.HtmlDecode(StripTags.Replace(m.Groups[4].Value, "")).Trim();
            yield return (title, href);
        }
    }

    private const int MaxBookmarkItems = 100_000;
    private static readonly Regex BookmarkAnchor = new(
        @"<A\b[^>]*?\bHREF\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))[^>]*>(.*?)</A>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex StripTags = new(@"<[^>]+>", RegexOptions.Compiled);

    private void ExportBookmarks_Click(object sender, RoutedEventArgs e)
    {
        if (_bookmarks is null)
        {
            StatusText.Text = "Bookmarks are not available in incognito mode.";
            return;
        }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export bookmarks (HTML)",
            Filter = "HTML bookmark files (*.html)|*.html",
            FileName = "winidm-bookmarks.html",
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var nodes = _bookmarks.All
                .Select((b, i) => new BookmarkNode(i + 1, b.Title, b.Url, IsFolder: false, Children: Array.Empty<BookmarkNode>()))
                .ToList();
            File.WriteAllText(dlg.FileName, NetscapeBookmarks.Export(nodes), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            StatusText.Text = $"Export complete: {nodes.Count} bookmarks.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Export bookmarks", ex);
            StatusText.Text = "Export failed. See the log for details.";
        }
    }
}
