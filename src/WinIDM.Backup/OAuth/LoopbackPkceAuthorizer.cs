using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WinIDM.Backup;

/// <summary>
/// OAuth2 Authorization Code + PKCE over loopback (RFC 8252). Uses only HttpClient and HttpListener.
/// The app supplies <c>openBrowser</c> (for example Process.Start with UseShellExecute).
/// If <see cref="ITokenStore"/> and a key are supplied, the token is loaded on construction and saved every time it changes.
/// With <c>allowInteractiveFallback</c> = false, a refresh failure throws and does not open the browser
/// (suitable for scheduled background backups).
/// </summary>
public sealed class LoopbackPkceAuthorizer : IOAuthTokenProvider
{
    private const string SuccessPage =
        "<html><head><meta charset=\"utf-8\"></head><body><p>Authorization succeeded. You can close this tab and return to WinIDM.</p></body></html>";

    private const string FailurePage =
        "<html><head><meta charset=\"utf-8\"></head><body><p>Authorization failed. Please try again from WinIDM.</p></body></html>";

    private readonly OAuthClientOptions _options;
    private readonly HttpClient _http;
    private readonly Func<Uri, CancellationToken, Task> _openBrowser;
    private readonly ISystemClock _clock;
    private readonly TimeSpan _timeout;
    private readonly ITokenStore? _store;
    private readonly string? _storeKey;
    private readonly bool _allowInteractive;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private OAuthToken? _token;

    public LoopbackPkceAuthorizer(
        OAuthClientOptions options,
        HttpClient http,
        Func<Uri, CancellationToken, Task> openBrowser,
        ISystemClock? clock = null,
        TimeSpan? timeout = null,
        ITokenStore? tokenStore = null,
        string? tokenKey = null,
        bool allowInteractiveFallback = true)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _openBrowser = openBrowser ?? throw new ArgumentNullException(nameof(openBrowser));
        _clock = clock ?? SystemClock.Instance;
        _timeout = timeout ?? TimeSpan.FromMinutes(5);
        _allowInteractive = allowInteractiveFallback;

        if (tokenStore is not null && !string.IsNullOrEmpty(tokenKey))
        {
            _store = tokenStore;
            _storeKey = tokenKey;
            _token = tokenStore.Load(tokenKey);
        }
    }

    public OAuthToken? CurrentToken => _token;

    /// <summary>Load a token previously saved by App (does not write to the store).</summary>
    public void LoadToken(OAuthToken token) => _token = token ?? throw new ArgumentNullException(nameof(token));

    /// <summary>Current access token. Refreshes automatically when it expires.</summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_token is { } current && !current.IsExpired(_clock.UtcNow))
            {
                return current.AccessToken;
            }

            return (await RefreshOrAuthorizeLockedAsync(ct)).AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Force a token refresh (called after the server rejects the token with 401).</summary>
    public async Task<string> RefreshAccessTokenAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return (await RefreshOrAuthorizeLockedAsync(ct)).AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Run the full authorization flow: open the browser, wait for the loopback callback, exchange the code.</summary>
    public async Task<OAuthToken> AuthorizeAsync(CancellationToken ct = default)
    {
        var port = GetFreeLoopbackPort();
        var redirectUri = $"http://127.0.0.1:{port}/";
        var (verifier, challenge) = Pkce.Create();
        var state = Pkce.Base64UrlEncode(RandomNumberGenerator.GetBytes(16));
        var authorizationUrl = BuildAuthorizationUrl(redirectUri, state, challenge);

        using var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri);
        listener.Start();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_timeout);

        await _openBrowser(authorizationUrl, timeoutCts.Token);
        var code = await WaitForCallbackAsync(listener, state, timeoutCts.Token, ct);
        var token = await ExchangeCodeAsync(code, verifier, redirectUri, ct);
        SetToken(token);
        return token;
    }

    /// <summary>Build the authorization URL with PKCE parameters (S256).</summary>
    public Uri BuildAuthorizationUrl(string redirectUri, string state, string codeChallenge)
    {
        var parts = new List<string>
        {
            "response_type=code",
            "client_id=" + Escape(_options.ClientId),
            "redirect_uri=" + Escape(redirectUri),
            "state=" + Escape(state),
            "code_challenge=" + Escape(codeChallenge),
            "code_challenge_method=S256",
        };

        if (!string.IsNullOrEmpty(_options.Scope))
        {
            parts.Add("scope=" + Escape(_options.Scope));
        }

        foreach (var (key, value) in _options.ExtraAuthorizeParameters)
        {
            parts.Add(Escape(key) + "=" + Escape(value));
        }

        return new UriBuilder(_options.AuthorizationEndpoint) { Query = string.Join("&", parts) }.Uri;
    }

    /// <summary>Validate the callback URL: state must match, and there must be a code or an error.</summary>
    public static bool TryParseCallback(Uri callback, string expectedState, out string? code, out string? error)
    {
        ArgumentNullException.ThrowIfNull(callback);
        code = null;
        error = null;

        var query = ParseQuery(callback.Query);
        if (!query.TryGetValue("state", out var state)
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(expectedState)))
        {
            error = "OAuth state does not match.";
            return false;
        }

        if (query.TryGetValue("error", out var providerError))
        {
            error = $"Authorization denied: {providerError}";
            return false;
        }

        if (!query.TryGetValue("code", out var authCode) || string.IsNullOrEmpty(authCode))
        {
            error = "The authorization code is missing from the callback.";
            return false;
        }

        code = authCode;
        return true;
    }

    public async Task<OAuthToken> ExchangeCodeAsync(string code, string verifier, string redirectUri, CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = _options.ClientId,
            ["code_verifier"] = verifier,
        };
        AddSecret(form);
        return await PostTokenAsync(form, keepRefreshToken: null, ct);
    }

    public async Task<OAuthToken> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = _options.ClientId,
        };
        AddSecret(form);
        return await PostTokenAsync(form, keepRefreshToken: refreshToken, ct);
    }

    /// <summary>Called with the gate held.</summary>
    private async Task<OAuthToken> RefreshOrAuthorizeLockedAsync(CancellationToken ct)
    {
        var refresh = _token?.RefreshToken;
        if (!string.IsNullOrEmpty(refresh))
        {
            try
            {
                var renewed = await RefreshAsync(refresh, ct);
                SetToken(renewed);
                return renewed;
            }
            catch (BackupTransportException ex) when (!_allowInteractive)
            {
                throw new BackupTransportException(
                    "Token refresh failed. Reconnect the account in the backup window.", ex.StatusCode);
            }
            catch (BackupTransportException)
            {
                // Refresh failed: fall back to interactive authorization.
            }
        }

        if (!_allowInteractive)
        {
            throw new BackupTransportException("The account session expired and there is no refresh token. Reconnect the account.");
        }

        return await AuthorizeAsync(ct);
    }

    private void SetToken(OAuthToken token)
    {
        _token = token;
        if (_store is not null && _storeKey is not null)
        {
            _store.Save(_storeKey, token);
        }
    }

    private void AddSecret(Dictionary<string, string> form)
    {
        if (!string.IsNullOrEmpty(_options.ClientSecret))
        {
            form["client_secret"] = _options.ClientSecret;
        }
    }

    private async Task<OAuthToken> PostTokenAsync(Dictionary<string, string> form, string? keepRefreshToken, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await SendTokenRequestAsync(content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            var reason = TryReadErrorCode(body);
            throw new BackupTransportException(
                reason is null ? $"Token exchange failed (HTTP {status})." : $"Token exchange failed (HTTP {status}, {reason}).",
                status);
        }

        using var doc = ParseTokenJson(body);
        var root = doc.RootElement;
        var access = root.TryGetProperty("access_token", out var accessElement) ? accessElement.GetString() : null;
        if (string.IsNullOrEmpty(access))
        {
            throw new BackupTransportException("The token response has no access_token.");
        }

        var refresh = root.TryGetProperty("refresh_token", out var refreshElement) ? refreshElement.GetString() : null;
        var expiresIn = root.TryGetProperty("expires_in", out var expiresElement) && expiresElement.TryGetInt64(out var seconds)
            ? seconds
            : 3600;

        return new OAuthToken(access, string.IsNullOrEmpty(refresh) ? keepRefreshToken : refresh,
            _clock.UtcNow.AddSeconds(expiresIn));
    }

    private async Task<HttpResponseMessage> SendTokenRequestAsync(HttpContent content, CancellationToken ct)
    {
        try
        {
            return await _http.PostAsync(_options.TokenEndpoint, content, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new BackupTransportException("Cannot reach the OAuth token server.", ex);
        }
    }

    private static async Task<string> WaitForCallbackAsync(
        HttpListener listener, string expectedState, CancellationToken timeoutToken, CancellationToken callerToken)
    {
        using var stop = timeoutToken.Register(() =>
        {
            try
            {
                listener.Stop();
            }
            catch (ObjectDisposedException)
            {
                // Already closed.
            }
        });

        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                callerToken.ThrowIfCancellationRequested();
                throw new BackupException("OAuth authorization was canceled or timed out.", ex);
            }

            var uri = context.Request.Url;
            if (uri is null || !HasOAuthParameters(uri))
            {
                Respond(context.Response, 404, "<html><body>Not found.</body></html>");
                continue;
            }

            var ok = TryParseCallback(uri, expectedState, out var code, out var error);
            Respond(context.Response, ok ? 200 : 400, ok ? SuccessPage : FailurePage);
            if (!ok)
            {
                throw new BackupException(error ?? "Invalid OAuth response.");
            }

            return code!;
        }
    }

    private static bool HasOAuthParameters(Uri uri)
    {
        var query = ParseQuery(uri.Query);
        return query.ContainsKey("state") || query.ContainsKey("code") || query.ContainsKey("error");
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var trimmed = query.StartsWith('?') ? query[1..] : query;
        foreach (var pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var key = Uri.UnescapeDataString(separator < 0 ? pair : pair[..separator]);
            var value = separator < 0 ? string.Empty : Uri.UnescapeDataString(pair[(separator + 1)..]);
            result.TryAdd(key, value);
        }

        return result;
    }

    private static void Respond(HttpListenerResponse response, int status, string html)
    {
        var bytes = Encoding.UTF8.GetBytes(html);
        response.StatusCode = status;
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        response.OutputStream.Write(bytes, 0, bytes.Length);
        response.Close();
    }

    private static int GetFreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static JsonDocument ParseTokenJson(string body)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new BackupTransportException("The OAuth token response is not valid JSON.", ex);
        }
    }

    private static string? TryReadErrorCode(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                ? error.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
