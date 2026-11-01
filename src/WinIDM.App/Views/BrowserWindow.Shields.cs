using Microsoft.Web.WebView2.Core;
using WinIDM.App.Services;
using WinIDM.App.ViewModels;
using WinIDM.Browser;

namespace WinIDM.App.Views;

/// <summary>
/// Brave-style privacy shields on top of WebView2: Global Privacy Control, tracking-parameter removal and
/// HTTPS by default. The request-header half of each shield lives in <see cref="ApplyRequestPrivacy"/>.
/// <para>
/// Every rewrite here acts on main-frame navigations only. Subresources (video, audio, HLS/DASH playlists and
/// segments, XHR) are never rewritten, so media capture, preview and download see the URLs the page really uses.
/// </para>
/// </summary>
public partial class BrowserWindow
{
    // GPC (https://privacycg.github.io/gpc-spec/): the header alone is not enough, pages read the navigator property.
    private const string GpcScript =
        "(function(){try{Object.defineProperty(Navigator.prototype,'globalPrivacyControl'," +
        "{get:function(){return true;},configurable:true,enumerable:true});}catch(e){}})();";

    /// <summary>An HTTPS attempt for a page the user asked for over HTTP.</summary>
    private sealed class PendingUpgrade
    {
        public required string HttpsUrl { get; init; }
        public required string Host { get; init; }
        public ulong? NavigationId { get; set; }
    }

    private readonly Dictionary<BrowserTabViewModel, PendingUpgrade> _pendingUpgrades = new();

    /// <summary>Hosts whose HTTPS failed in this window; kept in memory only (nothing is written to disk).</summary>
    private readonly HashSet<string> _httpOnlyHosts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Errors after which the HTTP page is tried instead (bad certificate, no TLS listener, reset).</summary>
    private static readonly HashSet<CoreWebView2WebErrorStatus> UpgradeFallbackErrors = new()
    {
        CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect,
        CoreWebView2WebErrorStatus.CertificateExpired,
        CoreWebView2WebErrorStatus.ClientCertificateContainsErrors,
        CoreWebView2WebErrorStatus.CertificateRevoked,
        CoreWebView2WebErrorStatus.CertificateIsInvalid,
        CoreWebView2WebErrorStatus.ServerUnreachable,
        CoreWebView2WebErrorStatus.Timeout,
        CoreWebView2WebErrorStatus.ErrorHttpInvalidServerResponse,
        CoreWebView2WebErrorStatus.ConnectionAborted,
        CoreWebView2WebErrorStatus.ConnectionReset,
        CoreWebView2WebErrorStatus.Disconnected,
        CoreWebView2WebErrorStatus.CannotConnect,
        CoreWebView2WebErrorStatus.Unknown,
    };

    /// <summary>
    /// Main-frame navigation rewrite (tracking parameters, HTTPS upgrade). Cancels the navigation and starts the
    /// rewritten one. A navigation that carries a body (form POST) is left alone so it is not turned into a GET.
    /// </summary>
    private bool TryRewriteNavigation(BrowserTabViewModel tab, CoreWebView2NavigationStartingEventArgs e)
    {
        if (tab.View?.CoreWebView2 is not { } core) return false;
        if (!TrackUpgrade(tab, e)) return false;
        if (!ShieldsActive(e.Uri) || HeaderOrEmpty(e.RequestHeaders, "Content-Type").Length > 0) return false;

        var target = e.Uri;
        if (_c.Settings.Bool(Keys.StripTrackingParams) && TrackingParams.Strip(target) is { } clean) target = clean;
        if (_c.Settings.Bool(Keys.HttpsUpgrade) && HttpsUpgrade.TryUpgrade(target, _httpOnlyHosts) is { } secure)
        {
            _pendingUpgrades[tab] = new PendingUpgrade { HttpsUrl = secure, Host = new Uri(secure).Host };
            target = secure;
        }
        if (target == e.Uri) return false;

        e.Cancel = true;
        core.Navigate(target);
        return true;
    }

    /// <summary>
    /// Follows the HTTPS attempt of a tab across its navigation. Returns false when the server itself sent the
    /// upgraded page back to HTTP: that host wants HTTP, so it is remembered and the redirect is let through as is.
    /// </summary>
    private bool TrackUpgrade(BrowserTabViewModel tab, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!_pendingUpgrades.TryGetValue(tab, out var p)) return true;

        if (p.NavigationId is null && e.Uri == p.HttpsUrl)
        {
            p.NavigationId = e.NavigationId;
            return true;
        }
        if (e.NavigationId == p.NavigationId && e.IsRedirected)
        {
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var r) && r.Scheme == Uri.UriSchemeHttp
                && string.Equals(r.Host, p.Host, StringComparison.OrdinalIgnoreCase))
            {
                _httpOnlyHosts.Add(p.Host);
                _pendingUpgrades.Remove(tab);
                return false;
            }
            return true;
        }
        // The user went somewhere else before the secure attempt finished.
        _pendingUpgrades.Remove(tab);
        return true;
    }

    /// <summary>A certificate error on the tab's HTTPS attempt is a fallback, not a question for the user.</summary>
    private bool TryCancelUpgradeCertificateError(BrowserTabViewModel tab, CoreWebView2ServerCertificateErrorDetectedEventArgs e)
    {
        if (!_pendingUpgrades.TryGetValue(tab, out var p)) return false;
        if (!Uri.TryCreate(e.RequestUri, UriKind.Absolute, out var u)
            || !string.Equals(u.Host, p.Host, StringComparison.OrdinalIgnoreCase)) return false;
        e.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
        return true;
    }

    /// <summary>The navigation became a download: the engine takes it over, and an HTTP retry must not start a second one.</summary>
    private void ForgetUpgrade(BrowserTabViewModel tab) => _pendingUpgrades.Remove(tab);

    private void OnShieldsNavigationCompleted(BrowserTabViewModel tab, CoreWebView2 core, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!_pendingUpgrades.TryGetValue(tab, out var p) || p.NavigationId != e.NavigationId) return;
        _pendingUpgrades.Remove(tab);
        if (e.IsSuccess || !UpgradeFallbackErrors.Contains(e.WebErrorStatus)) return;

        _httpOnlyHosts.Add(p.Host);
        core.Navigate(HttpsUpgrade.Downgrade(p.HttpsUrl));
    }

    /// <summary>Whether the shields apply to a page.</summary>
    private bool ShieldsActive(string? pageUrl) => UrlHelper.IsHttp(pageUrl);

    private async Task WireShieldsAsync(BrowserTabViewModel tab, CoreWebView2 core)
    {
        core.NavigationCompleted += (_, e) => OnShieldsNavigationCompleted(tab, core, e);
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
