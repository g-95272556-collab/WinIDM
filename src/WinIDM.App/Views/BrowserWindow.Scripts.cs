using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using WinIDM.App.Services;
using WinIDM.App.ViewModels;
using WinIDM.Browser;
using WinIDM.Core.Models;

namespace WinIDM.App.Views;

/// <summary>
/// User script bridge (GM_*) for WebView2.
/// <list type="bullet">
/// <item>Tab main frame: scripts are registered through <c>AddScriptToExecuteOnDocumentCreatedAsync</c> on every
/// main navigation, wrapped so they run only in the top frame. The frame ID is the tab ID.</item>
/// <item>Iframes: <see cref="CoreWebView2Frame"/> receives scripts through <c>ExecuteScriptAsync</c> on
/// <c>ContentLoading</c>, with its own frame ID. Messages arrive through <c>Frame.WebMessageReceived</c>.</item>
/// <item>Only messages with the <c>winidm</c> field are processed here. Password messages are handled elsewhere.</item>
/// </list>
/// </summary>
public partial class BrowserWindow
{
    private readonly Dictionary<string, CoreWebView2Frame> _frames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _frameUrls = new(StringComparer.Ordinal);
    private readonly List<MenuItem> _scriptMenuItems = new();

    private UserScriptHost? ScriptHost => _c.UserScripts as UserScriptHost;

    private void WireScripts(BrowserTabViewModel tab, CoreWebView2 core)
    {
        // Script cookies use the normal profile only. Incognito is rejected in HandleScriptMessageAsync.
        if (!_incognito && ScriptHost is { } host) host.CookieStore = new BrowserCookieStore(core.CookieManager);
        ScriptHost?.RegisterFrame(tab.FrameId, "about:blank", _incognito, isMainFrame: true);

        core.FrameCreated += (_, e) => OnFrameCreated(tab, e.Frame);
        core.WebMessageReceived += (_, e) =>
        {
            OnPageMessageForAdblock(tab, e.WebMessageAsJson);
            _ = HandleScriptMessageAsync(tab.FrameId, e.WebMessageAsJson, json => core.PostWebMessageAsJson(json));
        };
    }

    /// <summary>Unregister the frame and scripts of one tab (called when the tab is closed).</summary>
    private void ReleaseScripts(BrowserTabViewModel tab)
    {
        ScriptHost?.UnregisterFrame(tab.FrameId);
        foreach (var key in _frames.Keys.Where(k => k.StartsWith(tab.FrameId + "f", StringComparison.Ordinal)).ToList())
        {
            ScriptHost?.UnregisterFrame(key);
            _frames.Remove(key);
            _frameUrls.Remove(key);
        }
    }

    /// <summary>Called on main navigation. Register the tab URL and replace the document scripts.</summary>
    private void OnScriptNavigationStarting(BrowserTabViewModel tab, CoreWebView2 core, string uri)
    {
        tab.TopNavigationUrl = uri;
        ScriptHost?.RegisterFrame(tab.FrameId, uri, _incognito, isMainFrame: true);
        if (_current == tab) ClearScriptMenu();
        _ = SyncDocumentScriptsAsync(tab, core, uri);
    }

    /// <summary>
    /// Replace the set of scripts registered for the tab's document. Old scripts are removed first. Serialized through
    /// <see cref="BrowserTabViewModel.ScriptGate"/> so two fast navigations do not mix.
    /// </summary>
    private async Task SyncDocumentScriptsAsync(BrowserTabViewModel tab, CoreWebView2 core, string url)
    {
        await tab.ScriptGate.WaitAsync();
        try
        {
            foreach (var registration in tab.ScriptRegistrations)
            {
                try { core.RemoveScriptToExecuteOnDocumentCreated(registration); }
                catch (ArgumentException ex) { AppLog.Error("Remove document script", ex); }
            }
            tab.ScriptRegistrations.Clear();

            // Cosmetic filter styles are injected before render. Wrapped like user scripts (top frame only).
            if (BuildCosmeticDocumentScript(url) is { } cosmetic)
                tab.ScriptRegistrations.Add(await core.AddScriptToExecuteOnDocumentCreatedAsync(GuardTopFrame(cosmetic)));

            var host = ScriptHost;
            if (host is null || !UrlHelper.IsHttp(url)) return;

            foreach (var scriptId in host.ScriptsForUrl(url))
            {
                var js = host.BuildInjectionScript(scriptId, _incognito, tab.FrameId);
                if (js is null) continue;
                // Non-main frames get their scripts through OnFrameCreated, not here.
                tab.ScriptRegistrations.Add(await core.AddScriptToExecuteOnDocumentCreatedAsync(GuardTopFrame(js)));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            AppLog.Error("Register user script", ex);
        }
        finally
        {
            tab.ScriptGate.Release();
        }
    }

    /// <summary>Global document scripts run in every frame. This wrapper limits them to the top frame.</summary>
    private static string GuardTopFrame(string script) => "if (window.top === window) {\n" + script + "\n}";

    private void OnFrameCreated(BrowserTabViewModel tab, CoreWebView2Frame frame)
    {
        var id = tab.FrameId + "f" + frame.FrameId.ToString(CultureInfo.InvariantCulture);
        _frames[id] = frame;
        _frameUrls[id] = "about:blank";

        frame.NavigationStarting += (_, e) =>
        {
            _frameUrls[id] = e.Uri;
            ScriptHost?.RegisterFrame(id, e.Uri, _incognito, isMainFrame: false);
        };
        frame.ContentLoading += (_, _) => _ = InjectFrameScriptsAsync(id, frame);
        frame.WebMessageReceived += (_, e) =>
            _ = HandleScriptMessageAsync(id, e.WebMessageAsJson, json =>
            {
                // Frames that are already destroyed are removed from _frames by the Destroyed event.
                if (_frames.ContainsKey(id)) frame.PostWebMessageAsJson(json);
            });
        frame.Destroyed += (_, _) =>
        {
            _frames.Remove(id);
            _frameUrls.Remove(id);
            ScriptHost?.UnregisterFrame(id);
        };
    }

    private async Task InjectFrameScriptsAsync(string frameId, CoreWebView2Frame frame)
    {
        var host = ScriptHost;
        if (host is null || !_frameUrls.TryGetValue(frameId, out var url) || !UrlHelper.IsHttp(url)) return;

        foreach (var scriptId in host.ScriptsForUrl(url))
        {
            if (host.GetMeta(scriptId) is { NoFrames: true }) continue;
            var js = host.BuildInjectionScript(scriptId, _incognito, frameId);
            if (js is null) continue;
            try
            {
                await frame.ExecuteScriptAsync(js);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException)
            {
                AppLog.Error("Inject frame script", ex);
            }
        }
    }

    /// <summary>
    /// Accept one JavaScript message. Messages are wrapped in WebView2 JSON; strings are unwrapped first.
    /// Only objects that have the <c>winidm</c> field are processed. The reply goes to the same frame.
    /// </summary>
    private async Task HandleScriptMessageAsync(string frameId, string webMessageJson, Action<string> reply)
    {
        var host = ScriptHost;
        if (host is null) return;

        string? payload = UnwrapScriptPayload(webMessageJson);
        if (payload is null) return;

        string id = "null";
        string? api = null;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("winidm", out _)) return;
            if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                id = JsonSerializer.Serialize(idEl.GetString());
            if (root.TryGetProperty("api", out var apiEl) && apiEl.ValueKind == JsonValueKind.String)
                api = apiEl.GetString();
        }
        catch (JsonException)
        {
            return;
        }

        // Incognito must not reach normal-profile cookies through GM_cookie.
        if (_incognito && api is not null && api.StartsWith("cookie", StringComparison.Ordinal))
        {
            reply("{\"winidm\":1,\"id\":" + id + ",\"ok\":false,\"error\":\"GM_cookie is not available in incognito mode.\"}");
            return;
        }

        string response = await host.HandleMessage(frameId, payload);
        reply(response);
    }

    /// <summary>
    /// WebView2 delivers messages as JSON. If the bridge sends a string, that JSON is wrapped in a string.
    /// </summary>
    private static string? UnwrapScriptPayload(string webMessageJson)
    {
        if (string.IsNullOrEmpty(webMessageJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(webMessageJson);
            return doc.RootElement.ValueKind switch
            {
                JsonValueKind.String => doc.RootElement.GetString(),
                JsonValueKind.Object => webMessageJson,
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ---------- UI requests from scripts (GM_openInTab, GM_setClipboard, GM_notification, GM_download, GM_registerMenuCommand)

    private void OnScriptUiRequested(UserScriptUiRequest req)
    {
        // The handler is called from a background thread. Only the active window responds.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (IsActive) HandleScriptUi(req);
        }));
    }

    private void HandleScriptUi(UserScriptUiRequest req)
    {
        try
        {
            using var doc = JsonDocument.Parse(req.PayloadJson);
            var root = doc.RootElement;
            switch (req.Kind)
            {
                case "openTab":
                    if (root.TryGetProperty("url", out var openUrl) && UrlHelper.IsHttp(openUrl.GetString()))
                        _ = NewTabAsync(openUrl.GetString(), select: true);
                    break;
                case "clipboard":
                    if (root.TryGetProperty("text", out var clip) && clip.ValueKind == JsonValueKind.String)
                        Clipboard.SetText(clip.GetString() ?? "");
                    break;
                case "notification":
                    string title = StringOr(root, "title", "Script");
                    string text = StringOr(root, "text", "");
                    StatusText.Text = $"Script notification: {title} {text}".Trim();
                    break;
                case "download":
                    if (root.TryGetProperty("url", out var dlUrl) && UrlHelper.IsHttp(dlUrl.GetString()))
                    {
                        var req2 = new DownloadRequest
                        {
                            Url = dlUrl.GetString()!,
                            FileName = StringOr(root, "name", "") is { Length: > 0 } n ? n : UrlHelper.FileNameFromUrl(dlUrl.GetString()!),
                            SaveDirectory = _c.DefaultDownloadFolder(),
                            Referer = _current?.Address,
                        };
                        _ = _shell.AddDownloadAsync(req2);
                    }
                    break;
                case "menu":
                    AddScriptMenuItem(StringOr(root, "name", "Script command"), StringOr(root, "cid", ""));
                    break;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            AppLog.Error("Script UI request", ex);
        }
    }

    private static string StringOr(JsonElement obj, string name, string fallback) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? fallback
            : fallback;

    private void AddScriptMenuItem(string name, string cid)
    {
        if (cid.Length == 0) return;
        var item = new MenuItem { Header = name };
        item.Click += (_, _) =>
        {
            var host = ScriptHost;
            if (host is null || _current?.View?.CoreWebView2 is not { } core) return;
            core.PostWebMessageAsJson(host.BuildMenuInvocation(cid));
        };
        _scriptMenuItems.Add(item);
        ScriptMenuSeparator.Visibility = Visibility.Visible;
        ScriptMenu.Items.Add(item);
    }

    private void ClearScriptMenu()
    {
        foreach (var item in _scriptMenuItems) ScriptMenu.Items.Remove(item);
        _scriptMenuItems.Clear();
        ScriptMenuSeparator.Visibility = Visibility.Collapsed;
    }

    // ---------- GM_webRequest

    /// <summary>Apply GM_webRequest rules for a URL. Returns true if a response has been set.</summary>
    private bool ApplyScriptWebRequestRules(CoreWebView2 core, CoreWebView2WebResourceRequestedEventArgs e, string url)
    {
        var host = ScriptHost;
        if (host is null) return false;
        var decision = host.EvaluateWebRequest(url);
        switch (decision.Kind)
        {
            case WebRequestActionKind.Cancel:
                e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
                return true;
            case WebRequestActionKind.Redirect when decision.RedirectUrl is { } target:
                e.Response = core.Environment.CreateWebResourceResponse(null, 307, "Temporary Redirect", "Location: " + target + "\r\n");
                return true;
            default:
                return false;
        }
    }

    // ---------- Daily updates (opt-in)

    /// <summary>
    /// The scheduled update check runs only if the user enables it (the <c>scripts.dailyCheck</c> preference).
    /// Automatic installation happens only if <c>scripts.autoApply</c> is also enabled.
    /// </summary>
    private async Task RunDailyScriptUpdatesAsync()
    {
        var host = ScriptHost;
        if (host is null) return;
        var prefs = BrowserServices.For(_c).Prefs;
        host.AutoApplyUpdates = prefs.Get("scripts.autoApply", false);
        if (!prefs.Get("scripts.dailyCheck", false)) return;
        try
        {
            var results = await host.RunScheduledUpdateChecksAsync(DateTimeOffset.UtcNow);
            int available = results.Count(r => r.State == UserScriptUpdateState.Available);
            int applied = results.Count(r => r.Applied);
            if (available + applied > 0)
                StatusText.Text = $"Script updates: {available} available, {applied} installed.";
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException or IOException)
        {
            AppLog.Error("Script update check", ex);
        }
    }

    private void OpenScriptManager_Click(object sender, RoutedEventArgs e)
    {
        new BrowserUserScriptsWindow(_c) { Owner = this }.Show();
    }
}
