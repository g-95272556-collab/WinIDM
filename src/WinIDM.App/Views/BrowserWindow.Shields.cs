using Microsoft.Web.WebView2.Core;
using WinIDM.App.Services;
using WinIDM.App.ViewModels;
using WinIDM.Browser;

namespace WinIDM.App.Views;

/// <summary>
/// Brave-style privacy shields on top of WebView2: Global Privacy Control, tracking-parameter removal,
/// HTTPS by default and fingerprinting protection. The request-header half of each shield lives in <see cref="ApplyRequestPrivacy"/>.
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
        int removed = 0;
        bool upgraded = false;
        if (_c.Settings.Bool(Keys.StripTrackingParams) && TrackingParams.Strip(target, out removed) is { } clean) target = clean;
        if (_c.Settings.Bool(Keys.HttpsUpgrade) && HttpsUpgrade.TryUpgrade(target, _httpOnlyHosts) is { } secure)
        {
            _pendingUpgrades[tab] = new PendingUpgrade { HttpsUrl = secure, Host = new Uri(secure).Host };
            target = secure;
            upgraded = true;
        }
        if (target == e.Uri) return false;

        tab.ShieldsBlocked = 0;
        tab.ShieldsParamsRemoved = removed;
        tab.ShieldsUpgraded = upgraded;
        tab.ShieldsRewriteTarget = target;
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

    /// <summary>A closed tab leaves nothing behind.</summary>
    private void ForgetShields(BrowserTabViewModel tab)
    {
        _pendingUpgrades.Remove(tab);
        _fingerprintScriptIds.Remove(tab);
    }

    private void OnShieldsNavigationCompleted(BrowserTabViewModel tab, CoreWebView2 core, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!_pendingUpgrades.TryGetValue(tab, out var p) || p.NavigationId != e.NavigationId) return;
        _pendingUpgrades.Remove(tab);
        if (e.IsSuccess || !UpgradeFallbackErrors.Contains(e.WebErrorStatus)) return;

        _httpOnlyHosts.Add(p.Host);
        tab.ShieldsUpgraded = false;
        core.Navigate(HttpsUpgrade.Downgrade(p.HttpsUrl));
    }

    // ---------- Per-site switch

    /// <summary>
    /// Incognito changes stay in memory: switching a site off or on there must not write the host to disk.
    /// </summary>
    private readonly HashSet<string> _sessionShieldsOff = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _sessionShieldsOn = new(StringComparer.OrdinalIgnoreCase);

    private HashSet<string> SavedShieldsOffSites() =>
        new(_c.Settings.Text(Keys.ShieldsOffSites)
                .Split(new[] { '\n', '\r', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>Sites where the user switched the shields off (a host also covers its subdomains).</summary>
    private IReadOnlyCollection<string> ShieldExemptSites()
    {
        var sites = SavedShieldsOffSites();
        sites.UnionWith(_sessionShieldsOff);
        sites.ExceptWith(_sessionShieldsOn);
        return sites;
    }

    private bool IsShieldsOff(string? host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        foreach (var site in ShieldExemptSites())
        {
            if (host.Equals(site, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + site, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Whether the shields apply to a page.</summary>
    private bool ShieldsActive(string? pageUrl) =>
        Uri.TryCreate(pageUrl, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" && !IsShieldsOff(u.Host);

    private void SetShieldsOff(string host, bool off)
    {
        host = host.ToLowerInvariant();
        if (_incognito)
        {
            (off ? _sessionShieldsOff : _sessionShieldsOn).Add(host);
            (off ? _sessionShieldsOn : _sessionShieldsOff).Remove(host);
            return;
        }
        var saved = SavedShieldsOffSites();
        if (off) saved.Add(host); else saved.Remove(host);
        _c.Settings.Set(Keys.ShieldsOffSites, string.Join('\n', saved.OrderBy(s => s, StringComparer.Ordinal)));
    }

    private void ResetShieldsCounters(BrowserTabViewModel tab, CoreWebView2NavigationStartingEventArgs e)
    {
        if (e.IsRedirected) return;
        if (tab.ShieldsRewriteTarget is { } t && t == e.Uri)
        {
            tab.ShieldsRewriteTarget = null;
            return;
        }
        tab.ShieldsRewriteTarget = null;
        tab.ShieldsBlocked = 0;
        tab.ShieldsParamsRemoved = 0;
        tab.ShieldsUpgraded = false;
    }

    private void RefreshShieldsButton(BrowserTabViewModel tab)
    {
        var page = tab.View?.Source?.ToString();
        if (!UrlHelper.IsHttp(page)) ShieldsButton.Content = "Shields";
        else if (!ShieldsActive(page)) ShieldsButton.Content = "Shields off";
        else ShieldsButton.Content = $"Shields ({tab.ShieldsBlocked + tab.ShieldsParamsRemoved + (tab.ShieldsUpgraded ? 1 : 0)})";
    }

    private void ShieldsMenu_Opened(object sender, System.Windows.RoutedEventArgs e)
    {
        var tab = _current;
        var src = tab?.View?.Source;
        bool web = tab is not null && src is not null && UrlHelper.IsHttp(src.ToString());
        bool on = web && ShieldsActive(src!.ToString());

        ShieldsSiteItem.IsEnabled = web;
        ShieldsSiteItem.IsChecked = on;
        ShieldsSiteItem.Header = web ? $"Shields for {src!.Host}" : "Shields for this site";
        ShieldsBlockedItem.Header = $"Ads and trackers blocked: {(on && IsAdblockOnFor(tab!) ? tab!.ShieldsBlocked.ToString() : "off")}";
        ShieldsParamsItem.Header = $"Tracking parameters removed: {(on && _c.Settings.Bool(Keys.StripTrackingParams) ? tab!.ShieldsParamsRemoved.ToString() : "off")}";
        ShieldsHttpsItem.Header = "Connection: " + (src?.Scheme == "https"
            ? (tab!.ShieldsUpgraded ? "upgraded to HTTPS" : "HTTPS")
            : web ? "HTTP (not secure)" : "-");
        ShieldsFingerprintItem.Header = "Fingerprinting protection: " +
            (on && _c.Settings.Bool(Keys.FingerprintProtection) ? "on" : "off");
    }

    /// <summary>
    /// Switches every shield for the site (ad blocker, cosmetic rules, tracking parameters, HTTPS upgrade, referrer
    /// cap, fingerprinting) and reloads the page. The ad blocker's own per-site allowlist is left as it is.
    /// </summary>
    private async void ShieldsSite_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_current?.View?.CoreWebView2 is not { } core || _current.View.Source is not { } src || !UrlHelper.IsHttp(src.ToString()))
            return;
        bool on = ShieldsSiteItem.IsChecked;
        SetShieldsOff(src.Host, off: !on);
        try
        {
            foreach (var t in _tabs)
                if (t.View?.CoreWebView2 is { } c) await RegisterFingerprintShieldAsync(t, c);
        }
        catch (Exception ex)
        {
            AppLog.Error("Browser shields", ex);
        }
        StatusText.Text = on ? $"Shields are on for {src.Host}." : $"Shields are off for {src.Host}.";
        core.Reload();
    }

    /// <summary>Fingerprint seed of this window: every site gets new values in a new window (and in incognito).</summary>
    private readonly string _fingerprintKey = System.Security.Cryptography.RandomNumberGenerator.GetHexString(32);

    /// <summary>The fingerprint script registered on each tab, replaced when the per-site exemptions change.</summary>
    private readonly Dictionary<BrowserTabViewModel, string> _fingerprintScriptIds = new();

    private async Task RegisterFingerprintShieldAsync(BrowserTabViewModel tab, CoreWebView2 core)
    {
        if (_fingerprintScriptIds.Remove(tab, out var old)) core.RemoveScriptToExecuteOnDocumentCreated(old);
        if (!_c.Settings.Bool(Keys.FingerprintProtection)) return;
        _fingerprintScriptIds[tab] = await core.AddScriptToExecuteOnDocumentCreatedAsync(
            FingerprintShield.BuildScript(_fingerprintKey, ShieldExemptSites()));
    }

    private async Task WireShieldsAsync(BrowserTabViewModel tab, CoreWebView2 core)
    {
        core.NavigationCompleted += (_, e) => OnShieldsNavigationCompleted(tab, core, e);
        try
        {
            if (_c.Settings.Bool(Keys.GlobalPrivacyControl))
                await core.AddScriptToExecuteOnDocumentCreatedAsync(GpcScript);
            await RegisterFingerprintShieldAsync(tab, core);
        }
        catch (Exception ex)
        {
            AppLog.Error("Browser shields", ex);
        }
    }
}
