using Microsoft.Web.WebView2.Core;
using WinIDM.Browser;

namespace WinIDM.App.Services;

/// <summary>
/// GM_cookie adapter: maps <see cref="IUserScriptCookieStore"/> onto a <see cref="CoreWebView2CookieManager"/>
/// of one WebView2 profile. Boundary checks (HTTPS, domain, path) are done by <see cref="UserScriptHost"/>
/// before this class is called. Only used for the normal profile, never for incognito.
/// </summary>
public sealed class BrowserCookieStore : IUserScriptCookieStore
{
    private readonly CoreWebView2CookieManager _manager;

    public BrowserCookieStore(CoreWebView2CookieManager manager)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
    }

    public async Task<IReadOnlyList<ScriptCookie>> ListAsync(Uri url, CancellationToken ct = default)
    {
        var cookies = await _manager.GetCookiesAsync(url.AbsoluteUri);
        return cookies.Select(ToScript).ToList();
    }

    public Task SetAsync(ScriptCookie cookie, CancellationToken ct = default)
    {
        var native = _manager.CreateCookie(cookie.Name, cookie.Value, cookie.Domain, cookie.Path);
        native.IsSecure = cookie.Secure;
        native.IsHttpOnly = cookie.HttpOnly;
        if (!cookie.Session && cookie.ExpirationDate is { } expires)
            native.Expires = DateTimeOffset.FromUnixTimeSeconds((long)expires).UtcDateTime;
        _manager.AddOrUpdateCookie(native);
        return Task.CompletedTask;
    }

    public async Task<bool> DeleteAsync(Uri url, string name, CancellationToken ct = default)
    {
        var cookies = await _manager.GetCookiesAsync(url.AbsoluteUri);
        var matches = cookies.Where(c => string.Equals(c.Name, name, StringComparison.Ordinal)).ToList();
        foreach (var c in matches) _manager.DeleteCookie(c);
        return matches.Count > 0;
    }

    private static ScriptCookie ToScript(CoreWebView2Cookie c) =>
        new(c.Name, c.Value, c.Domain, c.Path, c.IsSecure, c.IsHttpOnly, c.IsSession,
            c.IsSession ? null : (double)new DateTimeOffset(c.Expires.ToUniversalTime()).ToUnixTimeSeconds());
}
