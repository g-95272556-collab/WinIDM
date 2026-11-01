using Microsoft.Web.WebView2.Core;
using WinIDM.App.Services;
using WinIDM.App.ViewModels;
using WinIDM.Browser;

namespace WinIDM.App.Views;

/// <summary>
/// Brave-style privacy shields on top of WebView2: Global Privacy Control and tracking-parameter removal.
/// The request-header half of each shield lives in <see cref="ApplyRequestPrivacy"/>.
/// </summary>
public partial class BrowserWindow
{
    // GPC (https://privacycg.github.io/gpc-spec/): the header alone is not enough, pages read the navigator property.
    private const string GpcScript =
        "(function(){try{Object.defineProperty(Navigator.prototype,'globalPrivacyControl'," +
        "{get:function(){return true;},configurable:true,enumerable:true});}catch(e){}})();";

    /// <summary>
    /// Main-frame navigation rewrite (tracking parameters). Cancels the navigation and starts the cleaned one.
    /// Subresources (media, HLS/DASH segments, XHR) never pass through here, so captured media URLs keep every
    /// parameter. A navigation that carries a body (form POST) is left alone so it is not turned into a GET.
    /// </summary>
    private bool TryRewriteNavigation(BrowserTabViewModel tab, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!ShieldsActive(e.Uri) || HeaderOrEmpty(e.RequestHeaders, "Content-Type").Length > 0) return false;
        if (tab.View?.CoreWebView2 is not { } core) return false;

        var target = e.Uri;
        if (_c.Settings.Bool(Keys.StripTrackingParams) && TrackingParams.Strip(target) is { } clean) target = clean;
        if (target == e.Uri) return false;

        e.Cancel = true;
        core.Navigate(target);
        return true;
    }

    /// <summary>Whether the shields apply to a page.</summary>
    private bool ShieldsActive(string? pageUrl) => UrlHelper.IsHttp(pageUrl);

    private async Task WireShieldsAsync(BrowserTabViewModel tab, CoreWebView2 core)
    {
        try
        {
            if (_c.Settings.Bool(Keys.GlobalPrivacyControl))
                await core.AddScriptToExecuteOnDocumentCreatedAsync(GpcScript);
        }
        catch (Exception ex)
        {
            AppLog.Error("Browser shields", ex);
        }
    }
}
