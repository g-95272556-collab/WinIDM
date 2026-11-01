using System.Text;
using System.Text.Json;

namespace WinIDM.Browser;

/// <summary>
/// Builds the document-start injection script for one user script: a prelude that provides the
/// GM_* API through the WebView2 message bridge, followed by @require content and the script code, in one closure.
/// Configuration is embedded as a single JSON literal. Values from the user are never concatenated
/// into the JS code other than the script code itself.
/// </summary>
/// <remarks>
/// GM_getValue, GM_setValue, GM_deleteValue, and GM_listValues are synchronous. The page-side cache
/// is filled during injection from <c>values</c> in the configuration. Writes update the cache immediately
/// and are sent to the host asynchronously; if the host rejects them, the cache is restored.
/// </remarks>
internal static class PreludeBuilder
{
    private static readonly string[] GmParameterNames =
    {
        "GM_info", "GM_log", "GM_getValue", "GM_setValue", "GM_deleteValue", "GM_listValues",
        "GM_xmlhttpRequest", "GM_addStyle", "GM_notification", "GM_openInTab", "GM_setClipboard",
        "GM_download", "GM_registerMenuCommand", "GM_getResourceText", "GM_getResourceURL",
        "GM_cookie", "GM_webRequest",
        "unsafeWindow", "GM",
    };

    // The default encoder escapes <, >, &, ' and non-ASCII characters, so the JSON literal is safe for JS.
    private static readonly JsonSerializerOptions ConfigJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <param name="frameId">Frame ID carried by every message. "main" for the main frame.</param>
    /// <param name="valuesJson">Current script values (key -> valid raw JSON).</param>
    public static string Build(UserScriptMeta meta, string scriptId, string token, bool incognito,
        IReadOnlyList<UserScriptPin> pins, string source, string frameId,
        IReadOnlyDictionary<string, string> valuesJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(frameId);
        var resources = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var pin in pins.Where(p => p.Kind == "resource"))
        {
            resources[pin.Name] = new { b64 = pin.ContentBase64, mime = GuessMime(pin.Url) };
        }

        var info = new
        {
            scriptHandler = "WinIDM",
            version = "1",
            script = new
            {
                name = meta.Name,
                @namespace = meta.Namespace,
                version = meta.Version,
                author = meta.Author,
                description = meta.Description,
                grant = meta.Grants,
                runAt = meta.RunAt,
                noframes = meta.NoFrames,
            },
        };

        // Values are embedded as valid JSON already validated by UserScriptHost.
        var docs = new List<JsonDocument>(valuesJson.Count);
        try
        {
            var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var kv in valuesJson)
            {
                var doc = JsonDocument.Parse(kv.Value);
                docs.Add(doc);
                values[kv.Key] = doc.RootElement;
            }

            var config = new
            {
                token,
                id = scriptId,
                frame = frameId,
                incognito,
                declared = meta.Grants,
                info,
                resources,
                values,
            };

            var sb = new StringBuilder(16 * 1024 + source.Length);
            sb.Append(PartOne);
            sb.Append(JsonSerializer.Serialize(config, ConfigJson));
            sb.Append(PartTwo);

            sb.Append("    (function (").Append(string.Join(", ", GmParameterNames)).Append(") {\n");
            foreach (var pin in pins.Where(p => p.Kind == "require"))
            {
                sb.Append("\n// @require ").Append(pin.Url.Replace('\n', ' ').Replace('\r', ' ')).Append('\n');
                sb.Append(DecodeText(pin.ContentBase64)).Append('\n');
            }
            sb.Append("\n// ---- script code ----\n");
            sb.Append(source).Append('\n');
            sb.Append("    })");
            sb.Append(".apply(window, [");
            sb.Append(string.Join(", ", GmParameterNames.Select(n => "api." + n)));
            sb.Append("]);\n");
            sb.Append(PartThree);
            return sb.ToString();
        }
        finally
        {
            foreach (var d in docs) d.Dispose();
        }
    }

    private static string DecodeText(string base64)
    {
        byte[] bytes = Convert.FromBase64String(base64);
        return Encoding.UTF8.GetString(bytes);
    }

    private static string GuessMime(string url)
    {
        string path = url.Split('?', '#')[0];
        int dot = path.LastIndexOf('.');
        string ext = dot < 0 ? "" : path[(dot + 1)..].ToLowerInvariant();
        return ext switch
        {
            "png" => "image/png",
            "jpg" or "jpeg" => "image/jpeg",
            "gif" => "image/gif",
            "svg" => "image/svg+xml",
            "webp" => "image/webp",
            "css" => "text/css",
            "js" => "text/javascript",
            "json" => "application/json",
            "txt" => "text/plain",
            "html" or "htm" => "text/html",
            _ => "application/octet-stream",
        };
    }

    // Template parts. The configuration value is inserted between PartOne and PartTwo.
    private const string PartOne = """
(function () {
  var __cfg =
""";

    private const string PartTwo = """
;
  var __wm = (window.chrome && window.chrome.webview) ? window.chrome.webview : null;
  var __nonce = Math.random().toString(36).slice(2) + Date.now().toString(36);
  var __seq = 0;
  var __pending = new Map();
  var __menus = new Map();

  function __call(api, args) {
    return new Promise(function (resolve, reject) {
      if (!__wm) { reject(new Error("WinIDM bridge is not available")); return; }
      var id = __nonce + ":" + (++__seq);
      __pending.set(id, { resolve: resolve, reject: reject });
      __wm.postMessage(JSON.stringify({ winidm: 1, t: __cfg.token, frame: __cfg.frame, id: id, api: api, args: args }));
    });
  }

  function __report(err) {
    try {
      __call("log", ["ERROR " + ((err && err.stack) ? err.stack : String(err))]).catch(function () {});
    } catch (e) { /* ignore */ }
  }

  function __has(name) { return __cfg.declared.indexOf(name) !== -1; }

  function __decodeText(b64) {
    var bin = atob(b64);
    var bytes = new Uint8Array(bin.length);
    for (var i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
    return new TextDecoder("utf-8").decode(bytes);
  }

  // Synchronous value cache: key -> JSON text. Filled during injection.
  var __valueText = Object.create(null);
  (function () {
    var src = __cfg.values || {};
    for (var k in src) {
      if (Object.prototype.hasOwnProperty.call(src, k)) __valueText[k] = JSON.stringify(src[k]);
    }
  })();

  if (__wm) {
    __wm.addEventListener("message", function (ev) {
      var m = ev && ev.data;
      if (!m || m.winidm !== 1) return;
      if (m.invoke === "menu") {
        var fn = __menus.get(m.cid);
        if (typeof fn === "function") { try { fn(); } catch (e) { __report(e); } }
        return;
      }
      var p = __pending.get(m.id);
      if (!p) return;
      __pending.delete(m.id);
      if (m.ok) p.resolve(m.result); else p.reject(new Error(m.error || "GM error"));
    });
  }

  var api = { GM_info: __cfg.info, unsafeWindow: window };

  api.GM_log = function () {
    var parts = [];
    for (var i = 0; i < arguments.length; i++) {
      var a = arguments[i];
      try { parts.push(typeof a === "string" ? a : JSON.stringify(a)); } catch (e) { parts.push(String(a)); }
    }
    return __call("log", [parts.join(" ")]).catch(function () {});
  };

  if (__has("GM_getValue")) {
    api.GM_getValue = function (key, defaultValue) {
      var k = String(key);
      if (!Object.prototype.hasOwnProperty.call(__valueText, k)) return defaultValue;
      return JSON.parse(__valueText[k]);
    };
  }
  if (__has("GM_setValue")) {
    api.GM_setValue = function (key, value) {
      var k = String(key);
      var json = JSON.stringify(value);
      if (json === undefined) json = "null";
      var prev = __valueText[k];
      __valueText[k] = json;
      __call("setValue", [k, json]).catch(function (err) {
        if (prev === undefined) delete __valueText[k]; else __valueText[k] = prev;
        __report(err);
      });
    };
  }
  if (__has("GM_deleteValue")) {
    api.GM_deleteValue = function (key) {
      var k = String(key);
      var prev = __valueText[k];
      delete __valueText[k];
      __call("deleteValue", [k]).catch(function (err) {
        if (prev !== undefined) __valueText[k] = prev;
        __report(err);
      });
    };
  }
  if (__has("GM_listValues")) {
    api.GM_listValues = function () { return Object.keys(__valueText).sort(); };
  }
  if (__has("GM_xmlhttpRequest")) {
    api.GM_xmlhttpRequest = function (details) {
      details = details || {};
      var payload = {
        method: details.method || "GET",
        url: String(details.url || ""),
        headers: details.headers || {},
        data: (details.data === undefined || details.data === null) ? null : String(details.data),
        timeout: details.timeout || 0,
        responseType: details.responseType || "text"
      };
      __call("xhr", [payload]).then(function (r) {
        r = r || {};
        var resp = {
          readyState: 4, status: r.status, statusText: r.statusText,
          responseHeaders: r.responseHeaders, responseText: r.responseText,
          response: r.responseText, finalUrl: r.finalUrl, context: details.context
        };
        if (typeof details.onload === "function") { try { details.onload(resp); } catch (e) { __report(e); } }
      }, function (err) {
        var resp = {
          readyState: 4, status: 0, statusText: "", responseHeaders: "", responseText: "",
          response: "", finalUrl: payload.url, context: details.context,
          error: String((err && err.message) || err)
        };
        if (typeof details.onerror === "function") { try { details.onerror(resp); } catch (e) { __report(e); } }
      });
      return { abort: function () { } };
    };
  }
  if (__has("GM_addStyle")) {
    api.GM_addStyle = function (css) {
      var s = document.createElement("style");
      s.textContent = String(css);
      var attach = function () { (document.head || document.documentElement).appendChild(s); };
      if (document.head || document.documentElement) attach();
      else document.addEventListener("DOMContentLoaded", attach, { once: true });
      return s;
    };
  }
  if (__has("GM_notification")) {
    api.GM_notification = function (details, title) {
      var d = (typeof details === "string")
        ? { text: details, title: (typeof title === "string") ? title : undefined }
        : Object.assign({}, details || {});
      return __call("notify", [d]).then(function () {});
    };
  }
  if (__has("GM_openInTab")) {
    api.GM_openInTab = function (url) {
      return __call("openTab", [String(url)]).then(function () { return { close: function () {} }; });
    };
  }
  if (__has("GM_setClipboard")) {
    api.GM_setClipboard = function (text) {
      return __call("clipboard", [String(text)]).then(function () {});
    };
  }
  if (__has("GM_download")) {
    api.GM_download = function (details, name) {
      var d = (typeof details === "string") ? { url: details, name: name } : Object.assign({}, details || {});
      return __call("download", [d]).then(function () {});
    };
  }
  if (__has("GM_registerMenuCommand")) {
    api.GM_registerMenuCommand = function (name, fn) {
      var cid = __nonce + ":m" + (++__seq);
      __menus.set(cid, fn);
      return __call("menu", [String(name), cid]).then(function () { return cid; });
    };
  }
  if (__has("GM_cookie")) {
    var __cookieCall = function (op, details, cb) {
      __call(op, [details || {}]).then(function (r) {
        if (typeof cb === "function") { try { cb(r, undefined); } catch (e) { __report(e); } }
      }, function (err) {
        if (typeof cb === "function") { try { cb(undefined, String((err && err.message) || err)); } catch (e) { __report(e); } }
      });
    };
    api.GM_cookie = {
      list: function (details, cb) { __cookieCall("cookieList", details, cb); },
      set: function (details, cb) { __cookieCall("cookieSet", details, cb); },
      "delete": function (details, cb) { __cookieCall("cookieDelete", details, cb); }
    };
  }
  if (__has("GM_webRequest")) {
    api.GM_webRequest = function (rules) {
      return __call("webRequest", [Array.isArray(rules) ? rules : []]).then(function () {});
    };
  }
  api.GM_getResourceText = function (name) {
    var r = __cfg.resources[name];
    return r ? __decodeText(r.b64) : null;
  };
  api.GM_getResourceURL = function (name) {
    var r = __cfg.resources[name];
    return r ? ("data:" + r.mime + ";base64," + r.b64) : null;
  };

  var gm = { info: __cfg.info, log: api.GM_log };
  if (api.GM_getValue) gm.getValue = api.GM_getValue;
  if (api.GM_setValue) gm.setValue = api.GM_setValue;
  if (api.GM_deleteValue) gm.deleteValue = api.GM_deleteValue;
  if (api.GM_listValues) gm.listValues = api.GM_listValues;
  if (api.GM_xmlhttpRequest) gm.xmlHttpRequest = api.GM_xmlhttpRequest;
  if (api.GM_addStyle) gm.addStyle = api.GM_addStyle;
  if (api.GM_notification) gm.notification = api.GM_notification;
  if (api.GM_openInTab) gm.openInTab = api.GM_openInTab;
  if (api.GM_setClipboard) gm.setClipboard = api.GM_setClipboard;
  if (api.GM_download) gm.download = api.GM_download;
  if (api.GM_cookie) gm.cookie = api.GM_cookie;
  if (api.GM_webRequest) gm.webRequest = api.GM_webRequest;
  api.GM = gm;

  try {
""";

    private const string PartThree = """
  } catch (__e) {
    __report(__e);
  }
})();
""";
}
