using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using WinIDM.App.E2E.Support;

namespace WinIDM.App.E2E.Tests;

/// <summary>
/// Scenario 6: Browser window (WebView2). Navigation, ad blocker, user scripts, media panel and
/// passwords over HTTP. The page is read through DevTools (CDP) launched with --remote-debugging-port.
/// </summary>
[Trait("Category", "E2E")]
public sealed class BrowserTests
{
    private const string PageTitle = "E2E Browser";
    private const string BrowserTitle = "WinIDM Browser";
    private const string UserScriptName = "WinIDM E2E Marker";
    private const string UserScriptNamespace = "winidm-e2e";

    [Fact]
    public void S06_Browser_Navigate_AdblockBlocksLocalAds_UserScriptRuns_MediaDownload_NoPasswordSaveOnHttp()
    {
        var data = AppSession.NewTempDataDir();
        Directory.CreateDirectory(data);
        using var server = FixtureServer.StartAsync().GetAwaiter().GetResult();
        try
        {
            var mediaPayload = FixtureServer.Deterministic(1 * 1024 * 1024, seed: 6006);
            RegisterFixtures(server, mediaPayload);

            // Ad blocker: a custom rule matches "/ads/" on local resources.
            // PascalCase keys: the AdblockConfigStore reader is case-sensitive (same as the format the app writes).
            File.WriteAllText(Path.Combine(data, "adblock.json"), JsonSerializer.Serialize(new
            {
                Format = "winidm-adblock",
                Version = 1,
                Whitelist = Array.Empty<string>(),
                CustomRules = "/ads/",
            }));
            File.WriteAllText(Path.Combine(data, "userscripts.json"), BuildUserScriptStore());

            int devPort = Cdp.FreePort();
            using var app = AppSession.Launch(
                data,
                seed: new Dictionary<string, string> { ["browser.adblock"] = "true" },
                env: new Dictionary<string, string> { ["WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS"] = $"--remote-debugging-port={devPort}" });

            Shots.RunScenario(app, "S06-browser", () =>
            {
                AppFlows.ClickButton(app, "Browser");
                // The window title is "<page title> - WinIDM Browser", so match the suffix.
                var browser = Uia.WaitWindowEndingWith(app.Pid, " - " + BrowserTitle, TimeSpan.FromSeconds(30));

                // Navigation: the address is typed into AddressBox, then "Go".
                Navigate(browser, server.Url("/index.html"));
                string wsUrl = WaitForPage(devPort, server.Url("/index.html"), TimeSpan.FromSeconds(40));

                // Navigation finished: document.readyState = complete and the page title is read.
                Uia.WaitUntil(() => Eval(wsUrl, "document.readyState") == "complete", "readyState complete", TimeSpan.FromSeconds(30));
                Assert.Equal(PageTitle, Eval(wsUrl, "document.title"));

                // Ad blocker: /ads/* is blocked while ordinary resources still load (positive control).
                Uia.WaitUntil(() => Eval(wsUrl, "document.body && document.body.dataset.okJs") == "1",
                    "ordinary script loaded", TimeSpan.FromSeconds(20));
                Assert.Null(Eval(wsUrl, "document.body.dataset.adJs"));
                Assert.Equal(0, server.Requests.Count(r => r.Path.StartsWith("/ads/", StringComparison.Ordinal) && r.Status == 200));

                // User script: injected and modifies the DOM.
                try
                {
                    Uia.WaitUntil(() => Eval(wsUrl, "document.getElementById('winidm-e2e-marker')?.textContent") == "userscript-ok",
                        "user script modifies the DOM", TimeSpan.FromSeconds(20));
                }
                catch (TimeoutException)
                {
                    // Diagnostics: page state and the user script prelude.
                    throw new TimeoutException("The user script did not modify the DOM. " +
                        $"GM_info={Eval(wsUrl, "typeof GM_info")}; body={Eval(wsUrl, "document.body ? document.body.innerHTML.slice(0,300) : 'no-body'")}; " +
                        $"ready={Eval(wsUrl, "document.readyState")}");
                }

                // Media panel: a local video requests /media/clip.mp4 and is detected.
                Uia.WaitUntil(() => server.Requests.Any(r => r.Path == "/media/clip.mp4"), "media request", TimeSpan.FromSeconds(30));
                var mediaButton = Uia.Wait(() => Uia.FindAll(browser, ControlType.Button)
                    .FirstOrDefault(b => Uia.SafeName(b).StartsWith("Media (", StringComparison.Ordinal)
                                         && !Uia.SafeName(b).StartsWith("Media (0", StringComparison.Ordinal)),
                    "Media button with a count > 0", TimeSpan.FromSeconds(20));
                Uia.Invoke(mediaButton);
                var download = Uia.Wait(() => Uia.TryFind(browser, ControlType.Button, "Download"), "media Download button");
                Uia.Invoke(download);

                // The media download button opens the Add URL dialog (default editor) asynchronously; confirm with "Add".
                var addDlg = Uia.WaitWindow(app.Pid, "Add download", TimeSpan.FromSeconds(10));
                Uia.Invoke(Uia.Find(addDlg, ControlType.Button, "Add"));
                Uia.WaitWindowGone(app.Pid, "Add download");

                // Wait for the download to complete (row in the Completed tab), then verify the final file hash.
                AppFlows.WaitTabCount(app, "Completed", 1, TimeSpan.FromSeconds(60));
                var mediaPath = Path.Combine(app.DownloadDir, "clip.mp4");
                Assert.True(File.Exists(mediaPath), $"Media file does not exist: {mediaPath}");
                Assert.Equal(FixtureServer.Sha256Hex(mediaPayload), FixtureServer.Sha256File(mediaPath));

                // Passwords over HTTP: a form with a password field is submitted, but WinIDM does not
                // offer to save (HTTPS only). The "Save / Not now" bar must stay hidden.
                Navigate(browser, server.Url("/login.html"));
                string loginWs = WaitForPage(devPort, server.Url("/login.html"), TimeSpan.FromSeconds(40));
                Uia.WaitUntil(() => Eval(loginWs, "document.readyState") == "complete", "login page ready", TimeSpan.FromSeconds(30));
                Eval(loginWs, "(function(){ document.querySelector('form').requestSubmit(); return 1; })()");
                Thread.Sleep(3000);
                Assert.Null(Uia.TryFind(browser, ControlType.Button, "Not now"));

                // Close the browser window gracefully; the main app must stay alive.
                if (browser.TryGetCurrentPattern(WindowPattern.Pattern, out var wp)) ((WindowPattern)wp).Close();
                Uia.Wait(() => Uia.TryFindWindowEndingWith(app.Pid, " - " + BrowserTitle) is null ? "closed" : null, "browser window closed", TimeSpan.FromSeconds(20));
                Assert.NotNull(app.Main);
            });
        }
        finally
        {
            AppSession.TryDeleteDataDir(data);
        }
    }

    private static void RegisterFixtures(FixtureServer server, byte[] mediaPayload)
    {
        const string index = """
            <!doctype html>
            <html><head><meta charset="utf-8"><title>E2E Browser</title></head>
            <body>
            <h1>WinIDM test page</h1>
            <img src="/ads/banner.png" alt="ad">
            <script src="/ads/ad.js"></script>
            <script src="/static/ok.js"></script>
            <video src="/media/clip.mp4" preload="auto" muted></video>
            </body></html>
            """;
        const string login = """
            <!doctype html>
            <html><head><meta charset="utf-8"><title>Test login</title></head>
            <body>
            <form action="/login.html" method="post">
              <input type="text" name="user" autocomplete="username">
              <input type="password" name="pass" autocomplete="current-password">
              <input type="submit" value="Sign in">
            </form>
            </body></html>
            """;
        server.Add("/index.html", Encoding.UTF8.GetBytes(index), "text/html; charset=utf-8");
        server.Add("/login.html", Encoding.UTF8.GetBytes(login), "text/html; charset=utf-8");
        server.Add("/ads/banner.png", FixtureServer.Deterministic(2048, seed: 6100), "image/png");
        server.Add("/ads/ad.js", Encoding.UTF8.GetBytes("document.body.dataset.adJs='1';"), "application/javascript");
        server.Add("/static/ok.js", Encoding.UTF8.GetBytes("document.body.dataset.okJs='1';"), "application/javascript");
        server.Add("/media/clip.mp4", mediaPayload, "video/mp4");
    }

    /// <summary>Test user script in the userscripts.json store. The id is SHA-256(namespace + "\n" + name) (first 16 bytes).</summary>
    private static string BuildUserScriptStore()
    {
        const string source = """
            // ==UserScript==
            // @name         WinIDM E2E Marker
            // @namespace    winidm-e2e
            // @version      1.0
            // @description  Test marker: modifies the page DOM
            // @include      http://127.0.0.1*
            // @run-at       document-end
            // ==/UserScript==
            (function () {
              document.addEventListener('DOMContentLoaded', function () {
                var m = document.createElement('div');
                m.id = 'winidm-e2e-marker';
                m.textContent = 'userscript-ok';
                document.body.appendChild(m);
              });
            })();
            """;
        var idBytes = SHA256.HashData(Encoding.UTF8.GetBytes(UserScriptNamespace + "\n" + UserScriptName));
        var id = Convert.ToHexString(idBytes, 0, 16).ToLowerInvariant();
        // camelCase keys: UserScriptHost uses JsonNamingPolicy.CamelCase (case-sensitive on read).
        return JsonSerializer.Serialize(new
        {
            version = 2,
            autoApplyUpdates = false,
            scripts = new[]
            {
                new
                {
                    id,
                    source,
                    sourceSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant(),
                    sourceUrl = (string?)null,
                    enabled = true,
                    token = "e2e-token",
                    approved = Array.Empty<string>(),
                    pins = Array.Empty<object>(),
                    values = new Dictionary<string, string>(),
                    installedUtc = DateTimeOffset.UtcNow,
                    lastUpdateCheckUtc = (DateTimeOffset?)null,
                },
            },
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    private static void Navigate(AutomationElement browser, string url)
    {
        var address = Uia.FindAll(browser, ControlType.Edit).First();
        Uia.SetText(address, url);
        Uia.Invoke(Uia.Find(browser, ControlType.Button, "Go"));
    }

    /// <summary>Wait for a page whose URL starts with <paramref name="url"/> to appear in the DevTools list; return the WebSocket URL.</summary>
    private static string WaitForPage(int port, string url, TimeSpan timeout)
    {
        string? ws = null;
        Uia.WaitUntil(() =>
        {
            try
            {
                var page = Cdp.ListPagesAsync(port).GetAwaiter().GetResult()
                    .FirstOrDefault(p => p.Url.StartsWith(url, StringComparison.Ordinal));
                if (page is null || string.IsNullOrEmpty(page.WebSocketUrl)) return false;
                ws = page.WebSocketUrl;
                return true;
            }
            catch (HttpRequestException) { return false; }
            catch (TaskCanceledException) { return false; }
        }, $"page {url} in DevTools", timeout);
        return ws!;
    }

    private static string? Eval(string wsUrl, string expression) =>
        Cdp.EvaluateStringAsync(wsUrl, expression).GetAwaiter().GetResult();
}
