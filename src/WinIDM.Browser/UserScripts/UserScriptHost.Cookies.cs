using System.Text.Json;

namespace WinIDM.Browser;

/// <summary>A cookie as exposed to GM_cookie.</summary>
public sealed record ScriptCookie(
    string Name,
    string Value,
    string Domain,
    string Path,
    bool Secure,
    bool HttpOnly,
    bool Session,
    double? ExpirationDate);

/// <summary>
/// Cookie storage for GM_cookie. The app provides a WebView2-based implementation
/// (CoreWebView2CookieManager). The in-memory implementation is provided for tests and browser-less mode.
/// All boundary checks are done by <see cref="UserScriptHost"/> before calling the store.
/// </summary>
public interface IUserScriptCookieStore
{
    /// <summary>Cookies that apply to the URL (domain, path, and HTTPS security matching).</summary>
    Task<IReadOnlyList<ScriptCookie>> ListAsync(Uri url, CancellationToken ct = default);

    Task SetAsync(ScriptCookie cookie, CancellationToken ct = default);

    /// <summary>Delete the named cookies that apply to the URL. Returns true if any were deleted.</summary>
    Task<bool> DeleteAsync(Uri url, string name, CancellationToken ct = default);
}

/// <summary>In-memory cookie store. Lost when the process exits.</summary>
public sealed class InMemoryUserScriptCookieStore : IUserScriptCookieStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ScriptCookie> _cookies = new(StringComparer.Ordinal);

    public Task<IReadOnlyList<ScriptCookie>> ListAsync(Uri url, CancellationToken ct = default)
    {
        double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        lock (_gate)
        {
            foreach (var key in _cookies.Where(kv => kv.Value.ExpirationDate is double e && e <= now).Select(kv => kv.Key).ToList())
                _cookies.Remove(key);

            IReadOnlyList<ScriptCookie> result = _cookies.Values
                .Where(c => Applies(c, url))
                .OrderBy(c => c.Name, StringComparer.Ordinal)
                .ThenBy(c => c.Path, StringComparer.Ordinal)
                .ToList();
            return Task.FromResult(result);
        }
    }

    public Task SetAsync(ScriptCookie cookie, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cookie);
        lock (_gate) _cookies[Key(cookie.Domain, cookie.Path, cookie.Name)] = cookie;
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(Uri url, string name, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var keys = _cookies.Values
                .Where(c => c.Name == name && Applies(c, url))
                .Select(c => Key(c.Domain, c.Path, c.Name))
                .ToList();
            foreach (var k in keys) _cookies.Remove(k);
            return Task.FromResult(keys.Count > 0);
        }
    }

    private static string Key(string domain, string path, string name) => domain + "\n" + path + "\n" + name;

    private static bool Applies(ScriptCookie c, Uri url)
    {
        string host = HostUtil.NormalizeHost(url.Host);
        string domain = HostUtil.NormalizeHost(c.Domain.TrimStart('.'));
        bool domainOk = host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);
        if (!domainOk) return false;
        if (c.Secure && url.Scheme != Uri.UriSchemeHttps) return false;
        return url.AbsolutePath.StartsWith(c.Path, StringComparison.Ordinal);
    }
}

/// <summary>
/// GM_cookie support (list, set, delete). Boundaries:
/// (1) the script must declare @grant GM_cookie and the user must approve it;
/// (2) cookies can only be read and written for the same site (same registrable domain) as the script page;
/// (3) the Domain attribute must match the target host or be a suffix of it; secure cookies require HTTPS;
/// (4) name and value are validated before storing.
/// </summary>
public sealed partial class UserScriptHost
{
    private const int MaxCookieNameLength = 256;
    private const int MaxCookieValueLength = 4096;
    private const int MaxCookiePathLength = 1024;
    private static readonly TimeSpan MaxCookieLifetime = TimeSpan.FromDays(400);

    private IUserScriptCookieStore _cookieStore = new InMemoryUserScriptCookieStore();

    /// <summary>Cookie store used by GM_cookie. The default is the in-memory store.</summary>
    public IUserScriptCookieStore CookieStore
    {
        get { lock (_gate) return _cookieStore; }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_gate) _cookieStore = value;
        }
    }

    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement;

    private async Task<string> HandleCookieAsync(string api, string scriptId, string pageUrl, JsonElement args, CancellationToken ct)
    {
        if (args.GetArrayLength() > 0 && args[0].ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Invalid cookie details.");
        var details = args.GetArrayLength() > 0 ? args[0] : EmptyObject;

        if (!HostUtil.TryGetHttpUri(pageUrl, out var page)) throw new ArgumentException("Invalid page.");
        Uri target = page;
        if (details.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String)
        {
            if (!HostUtil.TryGetHttpUri(urlEl.GetString(), out var parsed)) throw new ArgumentException("Invalid cookie URL.");
            target = parsed;
        }

        if (!HostUtil.SameSite(HostUtil.NormalizeHost(target.Host), HostUtil.NormalizeHost(page.Host)))
        {
            Log("PERMISSION DENIED", scriptId, api, $"Cookie for {target.Host} is outside the page's site.");
            throw new InvalidOperationException("Cross-site cookies are not allowed.");
        }

        var store = CookieStore;
        switch (api)
        {
            case "cookieList":
            {
                string? nameFilter = OptionalString(details, "name", MaxCookieNameLength);
                var cookies = await store.ListAsync(target, ct).ConfigureAwait(false);
                var filtered = cookies
                    .Where(c => nameFilter is null || c.Name == nameFilter)
                    .Select(c => new
                    {
                        name = c.Name,
                        value = c.Value,
                        domain = c.Domain,
                        path = c.Path,
                        secure = c.Secure,
                        httpOnly = c.HttpOnly,
                        session = c.Session,
                        expirationDate = c.ExpirationDate,
                    });
                return JsonSerializer.Serialize(filtered);
            }
            case "cookieSet":
            {
                string name = RequireCookieName(details);
                string value = RequireString(details, "value", MaxCookieValueLength, allowEmpty: true);
                if (value.Any(c => c < 0x20 || c == 0x7F || c == ';'))
                    throw new ArgumentException("Cookie value contains disallowed characters.");

                string targetHost = HostUtil.NormalizeHost(target.Host);
                string domain = targetHost;
                if (OptionalString(details, "domain", 253) is string d)
                {
                    string candidate = HostUtil.NormalizeHost(d.TrimStart('.'));
                    if (candidate != targetHost && !targetHost.EndsWith("." + candidate, StringComparison.Ordinal))
                        throw new ArgumentException("Domain attribute does not match the target host.");
                    domain = candidate;
                }

                string path = OptionalString(details, "path", MaxCookiePathLength) ?? "/";
                if (!path.StartsWith('/')) throw new ArgumentException("Cookie path must start with '/'.");

                bool secure = OptionalBool(details, "secure");
                if (secure && target.Scheme != Uri.UriSchemeHttps)
                    throw new ArgumentException("Secure cookies require HTTPS.");

                bool httpOnly = OptionalBool(details, "httpOnly");
                double? expiry = null;
                bool session = OptionalBool(details, "session");
                if (!session && details.TryGetProperty("expirationDate", out var expEl) && expEl.ValueKind == JsonValueKind.Number)
                {
                    double exp = expEl.GetDouble();
                    double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
                    double max = now + MaxCookieLifetime.TotalSeconds;
                    if (exp > now && exp <= max) expiry = exp;
                    else if (exp > max) throw new ArgumentException("Cookie expiration date is too far in the future.");
                }
                if (expiry is null) session = true;

                await store.SetAsync(new ScriptCookie(name, value, domain, path, secure, httpOnly, session, expiry), ct).ConfigureAwait(false);
                return "null";
            }
            case "cookieDelete":
            {
                string name = RequireCookieName(details);
                await store.DeleteAsync(target, name, ct).ConfigureAwait(false);
                return "null";
            }
            default:
                throw new ArgumentException("Unknown cookie API.");
        }
    }

    private static string RequireCookieName(JsonElement details)
    {
        string name = RequireString(details, "name", MaxCookieNameLength, allowEmpty: false);
        if (!name.All(c => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c)))
            throw new ArgumentException("Cookie name contains invalid characters.");
        return name;
    }

    private static string RequireString(JsonElement obj, string prop, int maxLength, bool allowEmpty)
    {
        if (!obj.TryGetProperty(prop, out var el) || el.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"'{prop}' is required.");
        string s = el.GetString() ?? "";
        if (s.Length > maxLength) throw new ArgumentException($"'{prop}' is too long.");
        if (!allowEmpty && s.Length == 0) throw new ArgumentException($"'{prop}' cannot be empty.");
        return s;
    }

    private static string? OptionalString(JsonElement obj, string prop, int maxLength)
    {
        if (!obj.TryGetProperty(prop, out var el) || el.ValueKind != JsonValueKind.String) return null;
        string s = el.GetString() ?? "";
        if (s.Length > maxLength) throw new ArgumentException($"'{prop}' is too long.");
        return s.Length == 0 ? null : s;
    }

    private static bool OptionalBool(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.True;
}
