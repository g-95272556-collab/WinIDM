using System.Text;
using System.Windows.Automation;
using WinIDM.App.E2E.Support;

namespace WinIDM.App.E2E.Tests;

/// <summary>
/// Scenario 6B/6C: a media item captured by the browser is downloaded with the page's session cookie.
/// The fixture media URL answers 403 unless the request carries "Cookie: media_session=&lt;token&gt;", so
/// only a download that repeats the browser cookie receives the payload. The SHA-256 of the downloaded
/// file therefore proves the cookie travelled from WebView2 to the download engine.
/// 6B takes the direct path ("skip editor" on), 6C goes through the "Add download" editor so that the editor
/// is also proven not to drop the captured cookies/user agent/headers.
/// </summary>
[Trait("Category", "E2E")]
public sealed class BrowserMediaSessionTests
{
    private const string BrowserTitle = "WinIDM Browser";
    private const string CookieName = "media_session";
    private const string CookieToken = "e2e-session-4f8a2c";
    private const string PagePath = "/media/session.html";
    private const string MediaPath = "/media/session-tone.mp3";
    private const string MediaFileName = "session-tone.mp3";
    private const int PayloadBytes = 320 * 1024;
    private const int PayloadSeed = 6007;

    [Fact]
    public void S06B_Browser_MediaDownload_CarriesSessionCookie_FileHashMatches() =>
        RunMediaSessionScenario(skipEditor: true, "S06B-browser-media-session-cookie");

    [Fact]
    public void S06C_Browser_MediaDownload_ThroughEditor_KeepsSessionCookie() =>
        RunMediaSessionScenario(skipEditor: false, "S06C-browser-media-session-editor");

    private static void RunMediaSessionScenario(bool skipEditor, string scenario)
    {
        var data = AppSession.NewTempDataDir();
        using var server = FixtureServer.StartAsync().GetAwaiter().GetResult();
        try
        {
            var payload = FixtureServer.Deterministic(PayloadBytes, seed: PayloadSeed);
            server.Add(PagePath, Encoding.UTF8.GetBytes(SessionPageHtml()), "text/html; charset=utf-8");
            server.AddCookieGuarded(MediaPath, payload, "audio/mpeg", CookieName, CookieToken);

            // With the editor on (the default) the media panel hands the captured request to the editor, so the
            // editor must preserve the cookies/UA/headers it never shows.
            using var app = AppSession.Launch(data, seed: skipEditor
                ? new Dictionary<string, string> { ["idm_pref_skip_editor"] = "true" }
                : null);

            Shots.RunScenario(app, scenario, () =>
            {
                AppFlows.ClickButton(app, "Browser");
                var browser = Uia.WaitWindowEndingWith(app.Pid, " - " + BrowserTitle, TimeSpan.FromSeconds(30));

                Navigate(browser, server.Url(PagePath));

                // The page sets document.cookie and then references the audio element; WebView2 sends the
                // cookie with the media request, the server returns the payload, and the panel gets an entry.
                var mediaButton = Uia.Wait(() => Uia.FindAll(browser, ControlType.Button)
                        .FirstOrDefault(b => Uia.SafeName(b).StartsWith("Media (", StringComparison.Ordinal)
                                             && !Uia.SafeName(b).StartsWith("Media (0", StringComparison.Ordinal)),
                    "Media button with a count > 0", TimeSpan.FromSeconds(40));
                Uia.Invoke(mediaButton);

                var download = Uia.Wait(() => Uia.TryFind(browser, ControlType.Button, "Download"),
                    "media Download button", TimeSpan.FromSeconds(20));
                Uia.Invoke(download);

                if (!skipEditor)
                {
                    var editor = Uia.WaitWindow(app.Pid, "Add download", TimeSpan.FromSeconds(20));
                    // The URL is prefilled from the captured item; confirming must keep its cookies.
                    Uia.Invoke(Uia.Find(editor, ControlType.Button, "Add"));
                    Uia.WaitWindowGone(app.Pid, "Add download", TimeSpan.FromSeconds(20));
                }

                // The row reaches Completed only if the request carried the cookie; then compare the final
                // file with the fixture payload (the server returns that payload only to a cookie-bearing request).
                AppFlows.WaitTabCount(app, "Completed", 1, TimeSpan.FromSeconds(90));

                var path = Path.Combine(app.DownloadDir, MediaFileName);
                Assert.True(File.Exists(path), $"Downloaded media file does not exist: {path}");
                Assert.Equal(payload.LongLength, new FileInfo(path).Length);
                Assert.Equal(FixtureServer.Sha256Hex(payload), FixtureServer.Sha256File(path));

                // Success must not be a hidden failure: no error row and no message box.
                Assert.Equal(0, Uia.TabCount(app.Main, "Error"));
                // A window can close between enumeration and the property read (the editor just closed).
                Assert.DoesNotContain(Uia.WindowsOfProcess(app.Pid), w =>
                {
                    try { return w.Current.ClassName == "#32770"; }
                    catch (ElementNotAvailableException) { return false; }
                });
                Assert.NotNull(app.Main);

                // Diagnostics: the engine's own probe (Range 0-0, which the browser never sends) was accepted, so the
                // engine — not only the browser — presented the session cookie.
                server.DumpLog(Path.Combine(RepoPaths.ArtifactsDir, scenario + "-requests.txt"));
                Assert.Contains(server.Requests, r => r.Path == MediaPath && r.Status == 206 && r.RangeStart == 0 && r.RangeEnd == 0);

                // Close the browser window gracefully; the main app must stay alive.
                if (browser.TryGetCurrentPattern(WindowPattern.Pattern, out var wp)) ((WindowPattern)wp).Close();
                Uia.Wait(() => Uia.TryFindWindowEndingWith(app.Pid, " - " + BrowserTitle) is null ? "closed" : null,
                    "browser window closed", TimeSpan.FromSeconds(20));
                Assert.NotNull(app.Main);
            });
        }
        finally
        {
            AppSession.TryDeleteDataDir(data);
        }
    }

    /// <summary>The page sets the session cookie first and only then requests the media (same origin).</summary>
    private static string SessionPageHtml() => $$"""
        <!doctype html>
        <html><head><meta charset="utf-8"><title>E2E media session</title></head>
        <body>
        <h1>Media session cookie</h1>
        <script>document.cookie = "{{CookieName}}={{CookieToken}}; path=/";</script>
        <audio src="{{MediaPath}}" preload="auto"></audio>
        </body></html>
        """;

    private static void Navigate(AutomationElement browser, string url)
    {
        var address = Uia.FindAll(browser, ControlType.Edit).First();
        Uia.SetText(address, url);
        Uia.Invoke(Uia.Find(browser, ControlType.Button, "Go"));
    }
}
