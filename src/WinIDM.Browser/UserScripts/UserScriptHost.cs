using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinIDM.Core.Contracts;

namespace WinIDM.Browser;

/// <summary>Permission names (@grant) and the extra permissions the user can approve.</summary>
public static class UserScriptPermissions
{
    public const string GetValue = "GM_getValue";
    public const string SetValue = "GM_setValue";
    public const string DeleteValue = "GM_deleteValue";
    public const string ListValues = "GM_listValues";
    public const string Xhr = "GM_xmlhttpRequest";
    public const string Notification = "GM_notification";
    public const string OpenInTab = "GM_openInTab";
    public const string SetClipboard = "GM_setClipboard";
    public const string Download = "GM_download";
    public const string RegisterMenu = "GM_registerMenuCommand";
    public const string Cookie = "GM_cookie";
    public const string WebRequest = "GM_webRequest";

    /// <summary>Permissions the user must approve before use.</summary>
    public const string ConnectWildcard = "connect:*";

    private static readonly HashSet<string> Sensitive = new(StringComparer.Ordinal)
    {
        Xhr, Notification, OpenInTab, SetClipboard, Download, Cookie, WebRequest,
    };

    public static bool IsSensitive(string grant) => Sensitive.Contains(grant);

    /// <summary>A permission can be approved only if the script declares it.</summary>
    public static bool IsApprovable(string permission, UserScriptMeta meta) =>
        permission == ConnectWildcard
            ? meta.Connects.Contains("*")
            : IsSensitive(permission) && meta.Grants.Contains(permission);
}

/// <summary>Pin for one @require or @resource: content is stored and verified by SHA-256.</summary>
public sealed record UserScriptPin(string Kind, string Name, string Url, string Sha256, string ContentBase64);

/// <summary>Install preview shown to the user before it is saved.</summary>
public sealed record UserScriptPreview(
    string? SourceUrl,
    UserScriptMeta Meta,
    string Source,
    string SourceSha256,
    IReadOnlyList<UserScriptPin> Pins);

/// <summary>Summary of an installed script.</summary>
public sealed record UserScriptSummary(
    string Id,
    string Name,
    string Version,
    bool Enabled,
    IReadOnlyList<string> Grants,
    IReadOnlyList<string> Approved,
    string? SourceUrl);

/// <summary>UI request raised by a script (notification, new tab, clipboard, download, menu).</summary>
public sealed record UserScriptUiRequest(string ScriptId, string Kind, string PayloadJson);

public sealed record UserScriptLogEntry(DateTimeOffset TimeUtc, string ScriptId, string Level, string Method, string Message);

/// <summary>Thrown when @require/@resource content does not match the expected SHA-256.</summary>
public sealed class UserScriptIntegrityException : Exception
{
    public UserScriptIntegrityException(string message) : base(message) { }
}

/// <summary>
/// User script manager (Tampermonkey style, GM_* subset). Stores scripts in a JSON file,
/// determines URL matches, verifies SHA-256 for @require/@resource, and handles messages
/// from the page with a per-script token and permission checks.
///
/// Threat model limits: WebView2 provides no isolated JS world for these scripts, so the token
/// lives in the same context as the page. The token blocks forged messages from frames
/// or pages that do not run the script, and the origin check ensures the script only
/// works on matched URLs. Extra permissions still require user approval.
/// </summary>
public sealed partial class UserScriptHost : IUserScriptHost, IDisposable
{
    public const int MaxScriptBytes = 2 * 1024 * 1024;
    public const int MaxResourceBytes = 5 * 1024 * 1024;
    public const int MaxXhrResponseBytes = 5 * 1024 * 1024;

    /// <summary>Total allowed size (keys + values) per script. Protects storage and injection.</summary>
    public const int MaxStorageBytesPerScript = 1024 * 1024;

    /// <summary>Main frame. Used when no frame ID is given.</summary>
    public const string MainFrameId = "main";

    private const int MaxMessageBytes = 8 * 1024 * 1024;
    private const int MaxLogEntries = 500;
    private const int MaxRedirects = 5;
    private const int DefaultTimeoutMs = 30_000;
    private const int MaxTimeoutMs = 60_000;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static readonly HashSet<string> ForbiddenRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cookie", "Host", "Content-Length", "Connection", "Transfer-Encoding", "Upgrade", "Referer",
        "Origin", "Proxy-Authorization", "Expect", "TE", "Trailer", "Keep-Alive",
    };

    private static readonly HashSet<string> ContentHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Type", "Content-Encoding", "Content-Language",
    };

    private static readonly HashSet<string> AllowedMethods = new(StringComparer.Ordinal)
    {
        "GET", "POST", "PUT", "DELETE", "HEAD", "PATCH", "OPTIONS",
    };

    private readonly object _gate = new();
    private readonly string? _storePath;
    private readonly HttpClient _download;
    private readonly bool _ownsDownload;
    private readonly HttpClient _xhr;
    private readonly List<ScriptRecord> _scripts = new();
    private readonly Dictionary<string, UserScriptMeta> _metas = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, string>> _incognitoValues = new(StringComparer.Ordinal);
    private readonly List<UserScriptLogEntry> _log = new();
    private readonly Dictionary<string, FrameContext> _frames = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>Context of one frame registered by the app. The URL and incognito mode come from the app, not the page.</summary>
    private sealed record FrameContext(string Url, bool Incognito, bool IsMainFrame);

    /// <summary>
    /// Create a script manager. A null <paramref name="storePath"/> means in-memory storage only.
    /// </summary>
    public UserScriptHost(string? storePath = null, HttpClient? httpClient = null)
    {
        _storePath = storePath;
        _ownsDownload = httpClient is null;
        _download = httpClient ?? new HttpClient(new SocketsHttpHandler { UseCookies = false })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        _xhr = new HttpClient(new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        Load();
    }

    /// <summary>If true (default), installing and @require/@resource must use HTTPS.</summary>
    public bool RequireHttps { get; set; } = true;

    /// <summary>Raised when a script requests a UI action. The handler must complete it.</summary>
    public event Action<UserScriptUiRequest>? UiRequested;

    // ---------------------------------------------------------------------
    // Installation

    /// <summary>Download and parse a script from a URL without saving it. For the confirmation dialog.</summary>
    public async Task<UserScriptPreview> PreviewFromUrlAsync(string url, CancellationToken ct = default)
    {
        if (!HostUtil.TryGetHttpUri(url, out var uri)) throw new ArgumentException("Script URL must be http(s).", nameof(url));
        EnsureTransportAllowed(uri);

        byte[] data = await FetchBytesAsync(_download, uri, MaxScriptBytes, ct).ConfigureAwait(false);
        string source = Encoding.UTF8.GetString(data).TrimStart('﻿');
        return await BuildPreviewAsync(source, uri.AbsoluteUri, ct).ConfigureAwait(false);
    }

    /// <summary>Parse a script from text and download the @require/@resource pins.</summary>
    public Task<UserScriptPreview> PreviewFromSourceAsync(string source, string? sourceUrl = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return BuildPreviewAsync(source, sourceUrl, ct);
    }

    /// <summary>Save a preview. If a script with the same id exists, its values and enabled state are kept.</summary>
    public string Install(UserScriptPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        string id = ComputeId(preview.Meta);
        lock (_gate)
        {
            var existing = _scripts.FirstOrDefault(s => s.Id == id);
            var record = existing ?? new ScriptRecord
            {
                Id = id,
                Token = NewToken(),
                InstalledUtc = DateTimeOffset.UtcNow,
            };
            record.Source = preview.Source;
            record.SourceSha256 = preview.SourceSha256;
            record.SourceUrl = preview.SourceUrl;
            record.Pins = preview.Pins.ToList();
            record.Enabled = existing?.Enabled ?? true;
            record.Approved = record.Approved
                .Where(p => UserScriptPermissions.IsApprovable(p, preview.Meta))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (existing is null) _scripts.Add(record);
            _metas[id] = preview.Meta;
            Save();
        }
        return id;
    }

    /// <inheritdoc />
    public async Task<string> InstallFromUrlAsync(string url, CancellationToken ct = default)
    {
        var preview = await PreviewFromUrlAsync(url, ct).ConfigureAwait(false);
        return Install(preview);
    }

    /// <summary>Install a script from text provided by the user or a test.</summary>
    public async Task<string> InstallFromSourceAsync(string source, string? sourceUrl = null, CancellationToken ct = default)
    {
        var preview = await PreviewFromSourceAsync(source, sourceUrl, ct).ConfigureAwait(false);
        return Install(preview);
    }

    // ---------------------------------------------------------------------
    // List management

    /// <inheritdoc />
    public IReadOnlyList<string> ScriptsForUrl(string pageUrl)
    {
        if (!HostUtil.TryGetHttpUri(pageUrl, out var uri)) return Array.Empty<string>();
        string url = uri.AbsoluteUri;
        var result = new List<string>();
        lock (_gate)
        {
            foreach (var rec in _scripts)
            {
                if (!rec.Enabled || !_metas.TryGetValue(rec.Id, out var meta)) continue;
                bool included = UrlPatternMatcher.MatchAny(meta.Matches, true, url)
                                || UrlPatternMatcher.MatchAny(meta.Includes, false, url);
                if (!included) continue;
                if (UrlPatternMatcher.MatchAny(meta.Excludes, false, url)
                    || UrlPatternMatcher.MatchAny(meta.ExcludeMatches, true, url))
                    continue;
                result.Add(rec.Id);
            }
        }
        return result;
    }

    public IReadOnlyList<UserScriptSummary> ListScripts()
    {
        lock (_gate)
        {
            return _scripts.Select(r =>
            {
                var meta = _metas.GetValueOrDefault(r.Id);
                return new UserScriptSummary(
                    r.Id,
                    meta?.Name ?? r.Id,
                    meta?.Version ?? "",
                    r.Enabled,
                    meta?.Grants ?? Array.Empty<string>(),
                    r.Approved.ToArray(),
                    r.SourceUrl);
            }).ToList();
        }
    }

    public UserScriptMeta? GetMeta(string id)
    {
        lock (_gate) return _metas.GetValueOrDefault(id);
    }

    public void SetEnabled(string id, bool enabled)
    {
        lock (_gate)
        {
            var rec = FindById(id) ?? throw new KeyNotFoundException("Script not found.");
            rec.Enabled = enabled;
            Save();
        }
    }

    public void Remove(string id)
    {
        lock (_gate)
        {
            var rec = FindById(id);
            if (rec is null) return;
            _scripts.Remove(rec);
            _metas.Remove(id);
            _incognitoValues.Remove(id);
            _webRules.Remove(id);
            Save();
        }
    }

    /// <summary>Approve one sensitive permission. Only declared permissions can be approved.</summary>
    public void ApprovePermission(string id, string permission)
    {
        lock (_gate)
        {
            var rec = FindById(id) ?? throw new KeyNotFoundException("Script not found.");
            var meta = _metas[id];
            if (!UserScriptPermissions.IsApprovable(permission, meta))
                throw new ArgumentException("This permission cannot be approved for this script.", nameof(permission));
            if (!rec.Approved.Contains(permission, StringComparer.Ordinal))
                rec.Approved.Add(permission);
            Save();
        }
    }

    public void RevokePermission(string id, string permission)
    {
        lock (_gate)
        {
            var rec = FindById(id) ?? throw new KeyNotFoundException("Script not found.");
            rec.Approved.RemoveAll(p => p == permission);
            if (permission == UserScriptPermissions.WebRequest) _webRules.Remove(id);
            Save();
        }
    }

    /// <summary>Recent log entries (newest last). If <paramref name="scriptId"/> is given, filter by script.</summary>
    public IReadOnlyList<UserScriptLogEntry> GetLog(string? scriptId = null)
    {
        lock (_gate)
        {
            return _log.Where(e => scriptId is null || e.ScriptId == scriptId).ToList();
        }
    }

    /// <summary>
    /// Build the injection script for one script. Returns null if the script is disabled, does not exist,
    /// or its content fails the SHA-256 check (logged).
    /// </summary>
    public string? BuildInjectionScript(string id, bool incognito = false, string frameId = MainFrameId)
    {
        if (!IsValidFrameId(frameId)) throw new ArgumentException("Invalid frame ID.", nameof(frameId));

        ScriptRecord rec;
        UserScriptMeta meta;
        Dictionary<string, string> values;
        lock (_gate)
        {
            var found = FindById(id);
            if (found is null || !found.Enabled) return null;
            if (!_metas.TryGetValue(id, out var foundMeta)) return null;
            rec = found;
            meta = foundMeta;
            values = new Dictionary<string, string>(ValueMap(id, incognito), StringComparer.Ordinal);
        }

        if (!string.Equals(Sha256Hex(Encoding.UTF8.GetBytes(rec.Source)), rec.SourceSha256, StringComparison.Ordinal))
        {
            Log("ERROR", id, "inject", "Script source SHA-256 check failed; script not loaded.");
            return null;
        }
        if (!VerifyPins(rec.Pins, out string? failure))
        {
            Log("ERROR", id, "inject", failure ?? "Pin check failed.");
            return null;
        }

        return PreludeBuilder.Build(meta, id, rec.Token, incognito, rec.Pins, rec.Source, frameId, values);
    }

    private static bool IsValidFrameId(string frameId) =>
        frameId.Length is >= 1 and <= 64 && frameId.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_');

    // ---------------------------------------------------------------------
    // Frames (iframe)

    /// <summary>
    /// Register a frame. The app calls this when WebView2 creates or navigates a frame.
    /// <paramref name="frameId"/> is the ID assigned by the app (not from the page), and <paramref name="url"/> is the frame's current URL.
    /// </summary>
    public void RegisterFrame(string frameId, string url, bool incognito, bool isMainFrame = false)
    {
        if (!IsValidFrameId(frameId)) throw new ArgumentException("Invalid frame ID.", nameof(frameId));
        ArgumentNullException.ThrowIfNull(url);
        lock (_gate) _frames[frameId] = new FrameContext(url, incognito, isMainFrame);
    }

    /// <summary>Unregister a frame (for example: closed or destroyed).</summary>
    public void UnregisterFrame(string frameId)
    {
        lock (_gate) _frames.Remove(frameId);
    }

    /// <summary>
    /// Handle a message from one frame. The context (URL, incognito, main frame) comes from the
    /// app registration, and the <c>frame</c> field in the message must match <paramref name="frameId"/>.
    /// </summary>
    public Task<string> HandleMessage(string frameId, string messageJson, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(frameId);
        FrameContext? ctx;
        lock (_gate) _frames.TryGetValue(frameId, out ctx);
        if (ctx is null) return Task.FromResult(Error("null", "Unknown frame."));
        return HandleCoreAsync(messageJson, ctx.Url, ctx.Incognito, ctx.IsMainFrame, frameId, ct);
    }

    /// <summary>Build the host-to-page message that triggers a menu item registered by the script.</summary>
    public string BuildMenuInvocation(string commandId)
    {
        return JsonSerializer.Serialize(new { winidm = 1, invoke = "menu", cid = commandId });
    }

    // ---------------------------------------------------------------------
    // Messages from the page

    /// <summary>
    /// Handle one message from the prelude. The return value is the JSON sent back to the page.
    /// <paramref name="sourceUrl"/> is the URL of the frame/page sending the message.
    /// </summary>
    public Task<string> HandleMessageAsync(string messageJson, string sourceUrl, bool incognito = false, CancellationToken ct = default) =>
        HandleCoreAsync(messageJson, sourceUrl, incognito, isMainFrame: true, expectedFrame: null, ct);

    /// <summary>
    /// Message handling core. A non-null <paramref name="expectedFrame"/> requires the <c>frame</c> field
    /// in the message to match. A false <paramref name="isMainFrame"/> blocks scripts marked @noframes.
    /// </summary>
    private async Task<string> HandleCoreAsync(string messageJson, string sourceUrl, bool incognito, bool isMainFrame,
        string? expectedFrame, CancellationToken ct)
    {
        if (messageJson is null || messageJson.Length > MaxMessageBytes) return Error("null", "Invalid message.");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(messageJson);
        }
        catch (JsonException)
        {
            return Error("null", "Invalid message.");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Error("null", "Invalid message.");

            string idRaw = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? JsonSerializer.Serialize(idEl.GetString())
                : "null";

            if (!root.TryGetProperty("winidm", out var ver) || ver.ValueKind != JsonValueKind.Number || ver.GetInt32() != 1)
                return Error(idRaw, "Unsupported protocol version.");

            if (expectedFrame is not null)
            {
                if (!root.TryGetProperty("frame", out var frameEl) || frameEl.ValueKind != JsonValueKind.String ||
                    !string.Equals(frameEl.GetString(), expectedFrame, StringComparison.Ordinal))
                {
                    Log("ERROR", "?", "frame", "Frame field does not match the registration.");
                    return Error(idRaw, "Frame does not match.");
                }
            }

            string token = root.TryGetProperty("t", out var tEl) && tEl.ValueKind == JsonValueKind.String ? tEl.GetString() ?? "" : "";
            string api = root.TryGetProperty("api", out var apiEl) && apiEl.ValueKind == JsonValueKind.String ? apiEl.GetString() ?? "" : "";
            JsonElement args = root.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Array
                ? argsEl
                : JsonDocument.Parse("[]").RootElement;

            ScriptRecord? rec;
            UserScriptMeta? meta;
            lock (_gate)
            {
                rec = FindByToken(token);
                meta = rec is null ? null : _metas.GetValueOrDefault(rec.Id);
            }

            if (rec is null || meta is null)
            {
                Log("ERROR", "?", api, "Invalid token.");
                return Error(idRaw, "Access denied.");
            }
            if (!rec.Enabled || !ScriptsForUrl(sourceUrl).Contains(rec.Id))
            {
                Log("ERROR", rec.Id, api, "Page origin does not match the script.");
                return Error(idRaw, "Access denied.");
            }
            if (!isMainFrame && meta.NoFrames)
            {
                Log("ERROR", rec.Id, api, "A @noframes script is not allowed in a frame.");
                return Error(idRaw, "Access denied.");
            }

            string? grant = GrantFor(api);
            if (grant is null && !IsKnownApi(api)) return Error(idRaw, "Unknown API.");

            if (grant is not null)
            {
                bool declared = meta.Grants.Contains(grant, StringComparer.Ordinal);
                bool approved = !UserScriptPermissions.IsSensitive(grant) || IsApproved(rec.Id, grant);
                if (!declared || !approved)
                {
                    Log("PERMISSION DENIED", rec.Id, api, $"{grant} is not allowed.");
                    return Error(idRaw, $"{grant}: permission not granted.");
                }
            }

            try
            {
                switch (api)
                {
                    case "getValue":
                    {
                        string key = ReadKey(args);
                        return Ok(idRaw, ReadValue(rec.Id, incognito, key) ?? "null");
                    }
                    case "setValue":
                    {
                        string key = ReadKey(args);
                        string json = ReadString(args, 1, 1024 * 1024);
                        ValidateJson(json);
                        WriteValue(rec.Id, incognito, key, json);
                        return Ok(idRaw, "null");
                    }
                    case "deleteValue":
                    {
                        string key = ReadKey(args);
                        DeleteValue(rec.Id, incognito, key);
                        return Ok(idRaw, "null");
                    }
                    case "listValues":
                        return Ok(idRaw, JsonSerializer.Serialize(ListKeys(rec.Id, incognito)));
                    case "xhr":
                    {
                        var result = await PerformXhrAsync(rec.Id, meta, args, ct).ConfigureAwait(false);
                        return Ok(idRaw, result);
                    }
                    case "notify":
                        Raise(rec.Id, "notification", ReadRaw(args, 0, 16 * 1024));
                        return Ok(idRaw, "null");
                    case "openTab":
                    {
                        string url = ReadString(args, 0, 4096);
                        if (!HostUtil.TryGetHttpUri(url, out var openUri)) return Error(idRaw, "Invalid URL.");
                        Raise(rec.Id, "openTab", JsonSerializer.Serialize(new { url = openUri.AbsoluteUri }));
                        return Ok(idRaw, "null");
                    }
                    case "clipboard":
                    {
                        string text = ReadString(args, 0, 1024 * 1024);
                        Raise(rec.Id, "clipboard", JsonSerializer.Serialize(new { text }));
                        return Ok(idRaw, "null");
                    }
                    case "download":
                    {
                        string raw = ReadRaw(args, 0, 16 * 1024);
                        using var d = JsonDocument.Parse(raw);
                        if (d.RootElement.ValueKind != JsonValueKind.Object ||
                            !d.RootElement.TryGetProperty("url", out var u) || u.ValueKind != JsonValueKind.String ||
                            !HostUtil.TryGetHttpUri(u.GetString(), out _))
                            return Error(idRaw, "Invalid download URL.");
                        Raise(rec.Id, "download", raw);
                        return Ok(idRaw, "null");
                    }
                    case "menu":
                    {
                        string name = ReadString(args, 0, 200);
                        string cid = ReadString(args, 1, 200);
                        Raise(rec.Id, "menu", JsonSerializer.Serialize(new { name, cid }));
                        return Ok(idRaw, "null");
                    }
                    case "log":
                    {
                        string message = ReadArgsText(args);
                        Log("INFO", rec.Id, "GM_log", Truncate(message, 2000));
                        return Ok(idRaw, "null");
                    }
                    case "cookieList":
                    case "cookieSet":
                    case "cookieDelete":
                    {
                        string result = await HandleCookieAsync(api, rec.Id, sourceUrl, args, ct).ConfigureAwait(false);
                        return Ok(idRaw, result);
                    }
                    case "webRequest":
                    {
                        if (args.GetArrayLength() < 1) throw new ArgumentException("Rules are required.");
                        SetWebRules(rec.Id, args[0]);
                        Log("INFO", rec.Id, "GM_webRequest", $"{ActiveWebRules(rec.Id).Count} rules set.");
                        return Ok(idRaw, "null");
                    }
                    default:
                        return Error(idRaw, "Unknown API.");
                }
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or JsonException or InvalidDataException or HttpRequestException or OperationCanceledException or InvalidOperationException)
            {
                Log("ERROR", rec.Id, api, Truncate(ex.Message, 300));
                return Error(idRaw, ex is OperationCanceledException && !ct.IsCancellationRequested
                    ? "Request timed out."
                    : "Request failed.");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _xhr.Dispose();
        if (_ownsDownload) _download.Dispose();
    }

    // ---------------------------------------------------------------------
    // Per-script values

    private string? ReadValue(string id, bool incognito, string key)
    {
        lock (_gate)
        {
            var map = ValueMap(id, incognito);
            return map.TryGetValue(key, out var json) ? json : null;
        }
    }

    private void WriteValue(string id, bool incognito, string key, string json)
    {
        lock (_gate)
        {
            var map = ValueMap(id, incognito);
            long current = StorageBytes(map);
            long previous = map.TryGetValue(key, out var old) ? Measure(key, old) : 0;
            if (current - previous + Measure(key, json) > MaxStorageBytesPerScript)
                throw new ArgumentException("Script storage quota is full (1 MB).");
            map[key] = json;
            if (!incognito) Save();
        }
    }

    private static long Measure(string key, string json) => Encoding.UTF8.GetByteCount(key) + Encoding.UTF8.GetByteCount(json);

    private static long StorageBytes(Dictionary<string, string> map)
    {
        long total = 0;
        foreach (var kv in map) total += Measure(kv.Key, kv.Value);
        return total;
    }

    private void DeleteValue(string id, bool incognito, string key)
    {
        lock (_gate)
        {
            if (ValueMap(id, incognito).Remove(key) && !incognito) Save();
        }
    }

    private List<string> ListKeys(string id, bool incognito)
    {
        lock (_gate)
        {
            return ValueMap(id, incognito).Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        }
    }

    private Dictionary<string, string> ValueMap(string id, bool incognito)
    {
        if (!incognito)
        {
            var rec = FindById(id) ?? throw new InvalidOperationException("Script not found.");
            return rec.Values;
        }
        if (!_incognitoValues.TryGetValue(id, out var map))
        {
            map = new Dictionary<string, string>(StringComparer.Ordinal);
            _incognitoValues[id] = map;
        }
        return map;
    }

    // ---------------------------------------------------------------------
    // XHR

    private async Task<string> PerformXhrAsync(string scriptId, UserScriptMeta meta, JsonElement args, CancellationToken ct)
    {
        if (args.GetArrayLength() < 1 || args[0].ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Invalid XHR details.");
        var details = args[0];

        string method = (details.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString() ?? "GET" : "GET").ToUpperInvariant();
        if (!AllowedMethods.Contains(method)) throw new ArgumentException("Unsupported HTTP method.");

        string urlText = details.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() ?? "" : "";
        if (!HostUtil.TryGetHttpUri(urlText, out var current)) throw new ArgumentException("Invalid URL.");

        string? bodyText = details.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
        int timeoutMs = details.TryGetProperty("timeout", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt32(out int tv)
            ? Math.Clamp(tv, 0, MaxTimeoutMs) : DefaultTimeoutMs;
        if (timeoutMs == 0) timeoutMs = DefaultTimeoutMs;

        var headers = new List<KeyValuePair<string, string>>();
        if (details.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in h.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.String) continue;
                if (ForbiddenRequestHeaders.Contains(prop.Name)) continue;
                headers.Add(new KeyValuePair<string, string>(prop.Name, prop.Value.GetString() ?? ""));
            }
        }

        if (!ConnectAllowed(scriptId, meta, current.Host))
        {
            Log("PERMISSION DENIED", scriptId, "GM_xmlhttpRequest", $"Host {current.Host} is not in @connect.");
            throw new InvalidOperationException("Host is not allowed by @connect.");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);

        for (int hop = 0; hop <= MaxRedirects; hop++)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), current);
            if (bodyText is not null && method is not ("GET" or "HEAD"))
            {
                request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(bodyText));
            }
            foreach (var kv in headers)
            {
                if (ContentHeaders.Contains(kv.Key) && request.Content is not null)
                    request.Content.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                else
                    request.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            }

            using var response = await _xhr.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);

            int status = (int)response.StatusCode;
            if (status is >= 300 and < 400 && response.Headers.Location is { } location && hop < MaxRedirects)
            {
                var next = new Uri(current, location);
                if (!HostUtil.TryGetHttpUri(next.AbsoluteUri, out _))
                    throw new InvalidOperationException("Redirect to a non-HTTP scheme is not allowed.");
                if (!ConnectAllowed(scriptId, meta, next.Host))
                {
                    Log("PERMISSION DENIED", scriptId, "GM_xmlhttpRequest", $"Redirect to host {next.Host} is not allowed.");
                    throw new InvalidOperationException("Redirect to that host is not allowed.");
                }
                if (status is 301 or 302 or 303)
                {
                    method = "GET";
                    bodyText = null;
                }
                current = next;
                continue;
            }

            string responseText = await ReadBodyLimitedAsync(response, MaxXhrResponseBytes, timeoutCts.Token).ConfigureAwait(false);
            var sbHeaders = new StringBuilder();
            foreach (var hv in response.Headers) sbHeaders.Append(hv.Key).Append(": ").Append(string.Join(", ", hv.Value)).Append("\r\n");
            foreach (var hv in response.Content.Headers) sbHeaders.Append(hv.Key).Append(": ").Append(string.Join(", ", hv.Value)).Append("\r\n");

            return JsonSerializer.Serialize(new XhrResult(
                status,
                response.ReasonPhrase ?? "",
                sbHeaders.ToString(),
                responseText,
                current.AbsoluteUri), JsonOptions);
        }

        throw new InvalidOperationException("Too many redirects.");
    }

    private bool ConnectAllowed(string scriptId, UserScriptMeta meta, string host)
    {
        if (meta.Connects.Count == 0) return false;
        bool approved;
        lock (_gate) approved = IsApproved(scriptId, UserScriptPermissions.ConnectWildcard);

        if (meta.Connects.Contains("*", StringComparer.Ordinal)) return approved;

        var domains = meta.Connects
            .Where(c => c != "self")
            .Select(HostUtil.NormalizeHost)
            .ToHashSet(StringComparer.Ordinal);
        return HostUtil.MatchesAnyDomain(HostUtil.NormalizeHost(host), domains);
    }

    private static async Task<string> ReadBodyLimitedAsync(HttpResponseMessage response, int maxBytes, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + read > maxBytes) throw new InvalidDataException("Response exceeds the size limit.");
            ms.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    private sealed record XhrResult(int Status, string StatusText, string ResponseHeaders, string ResponseText, string FinalUrl);

    // ---------------------------------------------------------------------
    // Installation helpers

    private async Task<UserScriptPreview> BuildPreviewAsync(string source, string? sourceUrl, CancellationToken ct)
    {
        var meta = UserScriptParser.Parse(source);
        var pins = new List<UserScriptPin>();
        foreach (string req in meta.Requires)
            pins.Add(await PinAsync("require", "", req, ct).ConfigureAwait(false));
        foreach (var res in meta.Resources)
            pins.Add(await PinAsync("resource", res.Name, res.Url, ct).ConfigureAwait(false));

        return new UserScriptPreview(
            sourceUrl,
            meta,
            source,
            Sha256Hex(Encoding.UTF8.GetBytes(source)),
            pins);
    }

    private async Task<UserScriptPin> PinAsync(string kind, string name, string rawUrl, CancellationToken ct)
    {
        var (uri, expected) = SplitFragment(rawUrl);
        EnsureTransportAllowed(uri);

        byte[] bytes = await FetchBytesAsync(_download, uri, MaxResourceBytes, ct).ConfigureAwait(false);
        string sha = Sha256Hex(bytes);
        if (expected is not null && !string.Equals(expected, sha, StringComparison.Ordinal))
            throw new UserScriptIntegrityException($"{(kind == "require" ? "@require" : "@resource")} '{uri.Host}' failed the SHA-256 check.");

        return new UserScriptPin(kind, name, uri.AbsoluteUri, sha, Convert.ToBase64String(bytes));
    }

    /// <summary>Split the #sha256=HEX fragment from the URL. The fragment is not sent to the server.</summary>
    private static (Uri Uri, string? Sha256) SplitFragment(string rawUrl)
    {
        int hash = rawUrl.IndexOf('#');
        string fetch = hash < 0 ? rawUrl : rawUrl[..hash];
        string? expected = null;
        if (hash >= 0)
        {
            string fragment = rawUrl[(hash + 1)..];
            if (fragment.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
            {
                expected = fragment["sha256=".Length..].Trim().ToLowerInvariant();
                if (expected.Length != 64 || !expected.All(Uri.IsHexDigit))
                    throw new FormatException("Invalid sha256 value in the fragment.");
            }
        }
        if (!HostUtil.TryGetHttpUri(fetch, out var uri))
            throw new FormatException("Resource URL must be http(s).");
        return (uri, expected);
    }

    private void EnsureTransportAllowed(Uri uri)
    {
        if (RequireHttps && uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Downloading a script or resource requires HTTPS.");
    }

    private static async Task<byte[]> FetchBytesAsync(HttpClient client, Uri uri, int maxBytes, CancellationToken ct)
    {
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + read > maxBytes) throw new InvalidDataException("Download exceeds the size limit.");
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    private static bool VerifyPins(IEnumerable<UserScriptPin> pins, out string? failure)
    {
        foreach (var pin in pins)
        {
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(pin.ContentBase64);
            }
            catch (FormatException)
            {
                failure = $"Pin content for '{pin.Name}' is corrupt.";
                return false;
            }
            if (!string.Equals(Sha256Hex(bytes), pin.Sha256, StringComparison.Ordinal))
            {
                failure = $"SHA-256 check for {pin.Kind} '{pin.Name}' failed; script not loaded.";
                return false;
            }
        }
        failure = null;
        return true;
    }

    private static string ComputeId(UserScriptMeta meta)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes((meta.Namespace ?? "") + "\n" + meta.Name));
        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }

    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    // ---------------------------------------------------------------------
    // Tokens, permissions, and logging

    private ScriptRecord? FindById(string id) => _scripts.FirstOrDefault(s => s.Id == id);

    /// <summary>Find a script by token using a constant-time comparison.</summary>
    private ScriptRecord? FindByToken(string token)
    {
        if (token.Length == 0) return null;
        byte[] presented = Encoding.UTF8.GetBytes(token);
        ScriptRecord? found = null;
        foreach (var rec in _scripts)
        {
            byte[] expected = Encoding.UTF8.GetBytes(rec.Token);
            if (expected.Length == presented.Length &&
                CryptographicOperations.FixedTimeEquals(expected, presented))
            {
                found = rec;
            }
        }
        return found;
    }

    private bool IsApproved(string id, string permission)
    {
        var rec = FindById(id);
        return rec is not null && rec.Approved.Contains(permission, StringComparer.Ordinal);
    }

    private static bool IsKnownApi(string api) => api is "getValue" or "setValue" or "deleteValue" or "listValues"
        or "xhr" or "notify" or "openTab" or "clipboard" or "download" or "menu" or "log"
        or "cookieList" or "cookieSet" or "cookieDelete" or "webRequest";

    private static string? GrantFor(string api) => api switch
    {
        "getValue" => UserScriptPermissions.GetValue,
        "setValue" => UserScriptPermissions.SetValue,
        "deleteValue" => UserScriptPermissions.DeleteValue,
        "listValues" => UserScriptPermissions.ListValues,
        "xhr" => UserScriptPermissions.Xhr,
        "notify" => UserScriptPermissions.Notification,
        "openTab" => UserScriptPermissions.OpenInTab,
        "clipboard" => UserScriptPermissions.SetClipboard,
        "download" => UserScriptPermissions.Download,
        "menu" => UserScriptPermissions.RegisterMenu,
        "cookieList" or "cookieSet" or "cookieDelete" => UserScriptPermissions.Cookie,
        "webRequest" => UserScriptPermissions.WebRequest,
        _ => null,
    };

    private void Raise(string scriptId, string kind, string payloadJson)
    {
        var handler = UiRequested;
        if (handler is null) return;
        try
        {
            handler(new UserScriptUiRequest(scriptId, kind, payloadJson));
        }
        catch (Exception ex)
        {
            Log("ERROR", scriptId, kind, Truncate("UI handler failed: " + ex.Message, 300));
        }
    }

    private void Log(string level, string scriptId, string method, string message)
    {
        lock (_gate)
        {
            _log.Add(new UserScriptLogEntry(DateTimeOffset.UtcNow, scriptId, level, method, message));
            if (_log.Count > MaxLogEntries) _log.RemoveRange(0, _log.Count - MaxLogEntries);
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    // ---------------------------------------------------------------------
    // Argument validation

    private static string ReadKey(JsonElement args)
    {
        string key = ReadString(args, 0, 512);
        if (key.Length == 0) throw new ArgumentException("Key cannot be empty.");
        return key;
    }

    private static string ReadString(JsonElement args, int index, int maxLength)
    {
        if (args.GetArrayLength() <= index || args[index].ValueKind != JsonValueKind.String)
            throw new ArgumentException("A string argument is required.");
        string s = args[index].GetString() ?? "";
        if (s.Length > maxLength) throw new ArgumentException("Argument is too long.");
        return s;
    }

    private static string ReadRaw(JsonElement args, int index, int maxLength)
    {
        if (args.GetArrayLength() <= index) throw new ArgumentException("Argument is required.");
        string raw = args[index].GetRawText();
        if (raw.Length > maxLength) throw new ArgumentException("Argument is too large.");
        return raw;
    }

    private static string ReadArgsText(JsonElement args)
    {
        var parts = new List<string>();
        foreach (var item in args.EnumerateArray())
        {
            parts.Add(item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : item.GetRawText());
        }
        return string.Join(" ", parts);
    }

    private static void ValidateJson(string json)
    {
        try
        {
            using var _ = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new ArgumentException("Value is not valid JSON.");
        }
    }

    private static string Ok(string idRaw, string resultRaw) =>
        "{\"winidm\":1,\"id\":" + idRaw + ",\"ok\":true,\"result\":" + resultRaw + "}";

    private static string Error(string idRaw, string message) =>
        "{\"winidm\":1,\"id\":" + idRaw + ",\"ok\":false,\"error\":" + JsonSerializer.Serialize(message) + "}";

    // ---------------------------------------------------------------------
    // Storage

    private void Load()
    {
        if (_storePath is null || !File.Exists(_storePath)) return;
        try
        {
            string json = File.ReadAllText(_storePath, Encoding.UTF8);
            var file = JsonSerializer.Deserialize<StoreFile>(json, JsonOptions) ?? new StoreFile();
            // Migration: v1 files have no AutoApplyUpdates or LastUpdateCheckUtc. Defaults are used,
            // and the file is rewritten as v2 on the next save.
            _autoApplyUpdates = file.AutoApplyUpdates;
            foreach (var rec in file.Scripts)
            {
                if (rec.Id.Length == 0 || rec.Token.Length == 0) continue;
                try
                {
                    _metas[rec.Id] = UserScriptParser.Parse(rec.Source);
                }
                catch (FormatException)
                {
                    rec.Enabled = false;
                }
                _scripts.Add(rec);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            string backup = _storePath + ".rosak-" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            try { File.Move(_storePath, backup, overwrite: true); } catch (IOException) { /* ignore */ }
            Log("ERROR", "store", "load", "Script file is corrupt; moved aside to a .rosak- backup.");
        }
    }

    private void Save()
    {
        if (_storePath is null) return;
        string full = Path.GetFullPath(_storePath);
        string? dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string tmp = full + ".tmp";
        string json = JsonSerializer.Serialize(new StoreFile
        {
            Version = StoreVersion,
            Scripts = _scripts,
            AutoApplyUpdates = _autoApplyUpdates,
        }, JsonOptions);
        File.WriteAllText(tmp, json, new UTF8Encoding(false));
        File.Move(tmp, full, overwrite: true);
    }

    /// <summary>JSON storage shape. Script data only; no browser secrets are stored.</summary>
    /// <summary>Storage format version. v1: scripts only. v2: + AutoApplyUpdates and LastUpdateCheckUtc.</summary>
    private const int StoreVersion = 2;

    private sealed class StoreFile
    {
        public int Version { get; set; } = 1;
        public bool AutoApplyUpdates { get; set; } = true;
        public List<ScriptRecord> Scripts { get; set; } = new();
    }

    private sealed class ScriptRecord
    {
        public string Id { get; set; } = "";
        public string Source { get; set; } = "";
        public string SourceSha256 { get; set; } = "";
        public string? SourceUrl { get; set; }
        public bool Enabled { get; set; } = true;
        public string Token { get; set; } = "";
        public List<string> Approved { get; set; } = new();
        public List<UserScriptPin> Pins { get; set; } = new();
        public Dictionary<string, string> Values { get; set; } = new(StringComparer.Ordinal);
        public DateTimeOffset InstalledUtc { get; set; }
        public DateTimeOffset? LastUpdateCheckUtc { get; set; }
    }
}
