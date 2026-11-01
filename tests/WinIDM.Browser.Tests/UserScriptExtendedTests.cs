using System.Text;
using System.Text.RegularExpressions;

namespace WinIDM.Browser.Tests;

public class UserScriptExtendedTests : IDisposable
{
    private const string Page = "https://site.test/page";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "winidm-usx-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static UserScriptHost NewHost(string? store = null) => new(store) { RequireHttps = false };

    private static string Script(string body = "x();", string extra = "", string version = "1.0.0", string name = "Ext", string match = "https://site.test/*") =>
        "// ==UserScript==\n" +
        "// @name " + name + "\n" +
        "// @namespace https://ext.test/\n" +
        "// @version " + version + "\n" +
        "// @match " + match + "\n" +
        extra +
        "// ==/UserScript==\n" + body + "\n";

    private static string TokenOf(string injection)
    {
        var m = Regex.Match(injection, "\"token\":\"([^\"]+)\"");
        Assert.True(m.Success, "Token not found");
        return m.Groups[1].Value;
    }

    private static string Msg(string token, string id, string api, string argsJson = "[]") =>
        "{\"winidm\":1,\"t\":\"" + token + "\",\"id\":\"" + id + "\",\"api\":\"" + api + "\",\"args\":" + argsJson + "}";

    private static string FrameMsg(string token, string frame, string id, string api, string argsJson = "[]") =>
        "{\"winidm\":1,\"t\":\"" + token + "\",\"frame\":\"" + frame + "\",\"id\":\"" + id + "\",\"api\":\"" + api + "\",\"args\":" + argsJson + "}";

    // ---------------------------------------------------------------------
    // @updateURL and @downloadURL metadata

    [Fact]
    public void Parser_reads_update_and_download_urls_and_rejects_non_http()
    {
        var meta = UserScriptParser.Parse("// ==UserScript==\n// @name A\n// @updateURL https://x.test/u.js\n// @downloadURL https://x.test/d.js\n// ==/UserScript==\n");
        Assert.Equal("https://x.test/u.js", meta.UpdateUrl);
        Assert.Equal("https://x.test/d.js", meta.DownloadUrl);

        Assert.Throws<FormatException>(() => UserScriptParser.Parse("// ==UserScript==\n// @name A\n// @updateURL ftp://x/u.js\n// ==/UserScript==\n"));
    }

    [Theory]
    [InlineData("1.10.0", "1.9.0", 1)]
    [InlineData("1.0.0-beta", "1.0.0", -1)]
    [InlineData("2.0", "2.0.0", 0)]
    [InlineData("1.0.0.1", "1.0.0", 1)]
    [InlineData("v3", "v2", 1)]
    [InlineData("0.9.9", "1.0", -1)]
    public void Version_compare_orders_numeric_segments(string a, string b, int sign)
    {
        Assert.Equal(sign, Math.Sign(UserScriptVersion.Compare(a, b)));
    }

    // ---------------------------------------------------------------------
    // GM_cookie

    [Fact]
    public async Task Cookie_api_requires_grant_approval_and_stays_on_the_same_site()
    {
        var host = NewHost();
        string id = await host.InstallFromSourceAsync(Script(extra: "// @grant GM_cookie\n"));
        string token = TokenOf(host.BuildInjectionScript(id)!);

        Assert.Contains("\"ok\":false", await host.HandleMessageAsync(Msg(token, "c0", "cookieSet", "[{\"name\":\"sid\",\"value\":\"1\"}]"), Page));
        host.ApprovePermission(id, UserScriptPermissions.Cookie);

        Assert.Contains("\"ok\":true", await host.HandleMessageAsync(Msg(token, "c1", "cookieSet", "[{\"name\":\"sid\",\"value\":\"abc\",\"path\":\"/\"}]"), Page));
        string list = await host.HandleMessageAsync(Msg(token, "c2", "cookieList", "[{}]"), Page);
        Assert.Contains("\"name\":\"sid\"", list);
        Assert.Contains("\"value\":\"abc\"", list);

        Assert.Contains("\"ok\":false", await host.HandleMessageAsync(Msg(token, "c3", "cookieList", "[{\"url\":\"https://other.test/\"}]"), Page));
        Assert.Contains(host.GetLog(id), e => e.Level == "PERMISSION DENIED" && e.Message.Contains("other.test"));
        Assert.Contains("\"ok\":false", await host.HandleMessageAsync(Msg(token, "c4", "cookieSet", "[{\"name\":\"x\",\"value\":\"1\",\"domain\":\"other.test\"}]"), Page));

        Assert.Contains("\"ok\":false", await host.HandleMessageAsync(Msg(token, "c7", "cookieSet", "[{\"name\":\"s\",\"value\":\"1\",\"secure\":true,\"url\":\"http://site.test/\"}]"), Page));
        Assert.Contains("\"ok\":false", await host.HandleMessageAsync(Msg(token, "c8", "cookieSet", "[{\"name\":\"bad\",\"value\":\"a;b\"}]"), Page));
        Assert.Contains("\"ok\":false", await host.HandleMessageAsync(Msg(token, "c9", "cookieSet", "[{\"name\":\"bad name\",\"value\":\"1\"}]"), Page));

        Assert.Contains("\"ok\":true", await host.HandleMessageAsync(Msg(token, "c5", "cookieDelete", "[{\"name\":\"sid\"}]"), Page));
        Assert.DoesNotContain("\"name\":\"sid\"", await host.HandleMessageAsync(Msg(token, "c6", "cookieList", "[{}]"), Page));
    }

    [Fact]
    public async Task Cookie_domain_attribute_must_match_target_host()
    {
        var host = NewHost();
        string id = await host.InstallFromSourceAsync(Script(extra: "// @grant GM_cookie\n"));
        host.ApprovePermission(id, UserScriptPermissions.Cookie);
        string token = TokenOf(host.BuildInjectionScript(id)!);

        Assert.Contains("\"ok\":true", await host.HandleMessageAsync(Msg(token, "d1", "cookieSet", "[{\"name\":\"parent\",\"value\":\"1\",\"domain\":\".site.test\"}]"), Page));
        Assert.Contains("\"ok\":false", await host.HandleMessageAsync(Msg(token, "d2", "cookieSet", "[{\"name\":\"deep\",\"value\":\"1\",\"domain\":\"evil.site.test\"}]"), Page));
    }

    // ---------------------------------------------------------------------
    // GM_webRequest

    [Fact]
    public async Task WebRequest_rules_apply_only_for_approved_enabled_scripts()
    {
        var host = NewHost();
        string id = await host.InstallFromSourceAsync(Script(extra: "// @grant GM_webRequest\n"));
        string token = TokenOf(host.BuildInjectionScript(id)!);
        string rules = "[{\"selector\":\"https://ads.test/*\",\"action\":\"cancel\"}," +
                       "{\"selector\":\"*://old.test/*\",\"action\":{\"redirect\":\"https://new.test/\"}}]";

        Assert.Contains("\"ok\":false", await host.HandleMessageAsync(Msg(token, "w0", "webRequest", "[" + rules + "]"), Page));
        host.ApprovePermission(id, UserScriptPermissions.WebRequest);
        Assert.Contains("\"ok\":true", await host.HandleMessageAsync(Msg(token, "w1", "webRequest", "[" + rules + "]"), Page));
        Assert.Equal(2, host.ActiveWebRules(id).Count);

        var cancel = host.EvaluateWebRequest("https://ads.test/banner.js");
        Assert.Equal(WebRequestActionKind.Cancel, cancel.Kind);
        Assert.Equal(id, cancel.ScriptId);

        var redirect = host.EvaluateWebRequest("https://old.test/a");
        Assert.Equal(WebRequestActionKind.Redirect, redirect.Kind);
        Assert.Equal("https://new.test/", redirect.RedirectUrl);

        Assert.Equal(WebRequestActionKind.Allow, host.EvaluateWebRequest("https://fine.test/").Kind);

        host.SetEnabled(id, false);
        Assert.Equal(WebRequestActionKind.Allow, host.EvaluateWebRequest("https://ads.test/banner.js").Kind);
        host.SetEnabled(id, true);

        host.RevokePermission(id, UserScriptPermissions.WebRequest);
        Assert.Equal(WebRequestActionKind.Allow, host.EvaluateWebRequest("https://ads.test/banner.js").Kind);
        Assert.Empty(host.ActiveWebRules(id));
    }

    [Fact]
    public async Task WebRequest_rejects_invalid_rules_and_ignores_self_redirect_loops()
    {
        var host = NewHost();
        string id = await host.InstallFromSourceAsync(Script(extra: "// @grant GM_webRequest\n"));
        host.ApprovePermission(id, UserScriptPermissions.WebRequest);
        string token = TokenOf(host.BuildInjectionScript(id)!);

        // The first argument is the rules array (as the prelude sends it).
        Assert.Contains("\"ok\":true", await host.HandleMessageAsync(
            Msg(token, "w1", "webRequest", "[[{\"selector\":\"https://ads.test/*\",\"action\":\"cancel\"}]]"), Page));

        Assert.Contains("\"ok\":false", await host.HandleMessageAsync(
            Msg(token, "w2", "webRequest", "[[{\"selector\":\"https://x.test/*\",\"action\":\"block\"}]]"), Page));
        Assert.Contains("\"ok\":false", await host.HandleMessageAsync(
            Msg(token, "w3", "webRequest", "[[{\"selector\":\"https://x.test/*\",\"action\":{\"redirect\":\"ftp://x.test/\"}}]]"), Page));
        Assert.Contains("\"ok\":false", await host.HandleMessageAsync(
            Msg(token, "w4", "webRequest", "[[{\"selector\":\"/([/\",\"action\":\"cancel\"}]]"), Page));
        Assert.Single(host.ActiveWebRules(id));
        Assert.Equal(WebRequestActionKind.Cancel, host.EvaluateWebRequest("https://ads.test/a").Kind);

        Assert.Contains("\"ok\":true", await host.HandleMessageAsync(
            Msg(token, "w5", "webRequest", "[[{\"selector\":\"https://loop.test/*\",\"action\":{\"redirect\":\"https://loop.test/\"}}]]"), Page));
        Assert.Equal(WebRequestActionKind.Allow, host.EvaluateWebRequest("https://loop.test/").Kind);
        Assert.Equal(WebRequestActionKind.Redirect, host.EvaluateWebRequest("https://loop.test/other").Kind);
    }

    // ---------------------------------------------------------------------
    // Update checks

    [Fact]
    public async Task Update_check_uses_update_url_compares_versions_and_keeps_permissions()
    {
        using var server = new TestHttpServer(new Dictionary<string, Func<HttpCall, HttpReply>>
        {
            ["/update.user.js"] = _ => HttpReply.Text(Script(version: "1.1.0", body: "window.updated = true;",
                extra: "// @grant GM_getValue\n// @grant GM_setValue\n// @grant GM_xmlhttpRequest\n// @connect 127.0.0.1\n"), contentType: "text/javascript"),
        });

        var host = NewHost();
        string old = Script(version: "1.0.0", body: "window.old = true;",
            extra: "// @grant GM_getValue\n// @grant GM_setValue\n// @grant GM_xmlhttpRequest\n// @connect 127.0.0.1\n" +
                   "// @updateURL " + server.BaseUrl + "/update.user.js\n");
        string id = await host.InstallFromSourceAsync(old);
        string token = TokenOf(host.BuildInjectionScript(id)!);
        await host.HandleMessageAsync(Msg(token, "s1", "setValue", "[\"keep\",\"5\"]"), Page);
        host.ApprovePermission(id, UserScriptPermissions.Xhr);

        var check = await host.CheckForUpdateAsync(id);
        Assert.Equal(UserScriptUpdateState.Available, check.State);
        Assert.Equal("1.1.0", check.RemoteVersion);
        Assert.NotNull(check.Preview);

        Assert.Equal(id, host.ApplyUpdate(check.Preview!, id));
        var summary = Assert.Single(host.ListScripts());
        Assert.Equal("1.1.0", summary.Version);
        Assert.Contains(UserScriptPermissions.GetValue, summary.Grants);
        Assert.Contains(UserScriptPermissions.SetValue, summary.Grants);
        Assert.Contains(UserScriptPermissions.Xhr, summary.Approved);
        Assert.Contains("\"result\":5", await host.HandleMessageAsync(Msg(token, "g1", "getValue", "[\"keep\"]"), Page));

        var same = await host.CheckForUpdateAsync(id);
        Assert.Equal(UserScriptUpdateState.UpToDate, same.State);
        Assert.Throws<InvalidOperationException>(() => host.ApplyUpdate(check.Preview!, id));
    }

    [Fact]
    public async Task Update_check_follows_download_url_when_update_url_is_meta_only()
    {
        // A meta-only script declares @downloadURL using the request host (Host header) of the server.
        using var meta = new TestHttpServer(new Dictionary<string, Func<HttpCall, HttpReply>>
        {
            ["/meta.js"] = call => HttpReply.Text("// ==UserScript==\n// @name Ext\n// @namespace https://ext.test/\n// @version 2.0.0\n" +
                                                  "// @downloadURL http://" + call.Headers["Host"] + "/full.user.js\n// ==/UserScript==\n"),
            ["/full.user.js"] = _ => HttpReply.Text(Script(version: "2.0.0", body: "window.full = 2;")),
        });

        var host = NewHost();
        string id = await host.InstallFromSourceAsync(Script(extra: "// @updateURL " + meta.BaseUrl + "/meta.js\n"));
        var check = await host.CheckForUpdateAsync(id);
        Assert.Equal(UserScriptUpdateState.Available, check.State);
        Assert.Contains("window.full = 2", check.Preview!.Source);
        Assert.Equal("2.0.0", check.RemoteVersion);
    }

    [Fact]
    public async Task Update_without_any_source_reports_no_source_and_network_errors_are_contained()
    {
        var host = NewHost();
        string id = await host.InstallFromSourceAsync(Script());
        var check = await host.CheckForUpdateAsync(id);
        Assert.Equal(UserScriptUpdateState.NoSource, check.State);

        string bad = await host.InstallFromSourceAsync(Script(name: "Broken", extra: "// @updateURL http://127.0.0.1:1/none.js\n"));
        var failed = await host.CheckForUpdateAsync(bad);
        Assert.Equal(UserScriptUpdateState.Failed, failed.State);
        Assert.NotNull(failed.Error);
    }

    [Fact]
    public async Task Scheduled_update_checks_run_once_per_day_and_apply_when_enabled()
    {
        using var server = new TestHttpServer(new Dictionary<string, Func<HttpCall, HttpReply>>
        {
            ["/update.user.js"] = _ => HttpReply.Text(Script(version: "1.1.0", body: "window.u = 1;")),
        });

        var host = NewHost();
        string id = await host.InstallFromSourceAsync(Script(extra: "// @updateURL " + server.BaseUrl + "/update.user.js\n"));
        var t0 = DateTimeOffset.UtcNow;

        var first = await host.RunScheduledUpdateChecksAsync(t0);
        var applied = Assert.Single(first);
        Assert.True(applied.Applied);
        Assert.Equal("1.1.0", host.ListScripts().Single().Version);

        Assert.Empty(await host.RunScheduledUpdateChecksAsync(t0.AddHours(1)));

        var nextDay = await host.RunScheduledUpdateChecksAsync(t0.AddHours(25));
        var nd = Assert.Single(nextDay);
        Assert.Equal(UserScriptUpdateState.UpToDate, nd.State);
        Assert.False(nd.Applied);
        Assert.Equal(id, nd.ScriptId);
    }

    [Fact]
    public async Task Scheduled_checks_report_without_applying_when_auto_apply_is_off()
    {
        using var server = new TestHttpServer(new Dictionary<string, Func<HttpCall, HttpReply>>
        {
            ["/update.user.js"] = _ => HttpReply.Text(Script(version: "1.1.0", body: "window.u = 1;")),
        });

        var host = NewHost();
        host.AutoApplyUpdates = false;
        await host.InstallFromSourceAsync(Script(extra: "// @updateURL " + server.BaseUrl + "/update.user.js\n"));

        var results = await host.RunScheduledUpdateChecksAsync(DateTimeOffset.UtcNow);
        var r = Assert.Single(results);
        Assert.Equal(UserScriptUpdateState.Available, r.State);
        Assert.False(r.Applied);
        Assert.Equal("1.0.0", host.ListScripts().Single().Version);
    }

    [Fact]
    public async Task Store_v2_persists_auto_apply_and_migrates_v1_files()
    {
        Directory.CreateDirectory(_dir);
        string store = Path.Combine(_dir, "scripts.json");
        File.WriteAllText(store, "{\"Version\":1,\"Scripts\":[]}");

        using (var host = NewHost(store))
        {
            Assert.True(host.AutoApplyUpdates);
            host.AutoApplyUpdates = false;
            await host.InstallFromSourceAsync(Script());
        }

        string json = File.ReadAllText(store);
        Assert.Contains("\"version\":2", json);

        using var reopened = NewHost(store);
        Assert.False(reopened.AutoApplyUpdates);
        Assert.Single(reopened.ListScripts());
    }

    // ---------------------------------------------------------------------
    // Frames (iframe) and the message bridge

    [Fact]
    public async Task Frame_messages_are_bound_to_registered_frame_ids()
    {
        var host = NewHost();
        string id = await host.InstallFromSourceAsync(Script(extra: "// @grant GM_getValue\n// @grant GM_setValue\n"));
        string injection = host.BuildInjectionScript(id, frameId: "f-1")!;
        Assert.Contains("\"frame\":\"f-1\"", injection);
        string token = TokenOf(injection);

        host.RegisterFrame("f-1", Page, incognito: false, isMainFrame: false);
        Assert.Contains("\"ok\":true", await host.HandleMessage("f-1", FrameMsg(token, "f-1", "a", "setValue", "[\"k\",\"1\"]")));
        Assert.Contains("\"result\":1", await host.HandleMessage("f-1", FrameMsg(token, "f-1", "b", "getValue", "[\"k\"]")));

        Assert.Contains("Frame does not match", await host.HandleMessage("f-1", FrameMsg(token, "f-2", "c", "getValue", "[\"k\"]")));
        Assert.Contains("Frame does not match", await host.HandleMessage("f-1", Msg(token, "d", "getValue", "[\"k\"]")));
        Assert.Contains("Unknown frame", await host.HandleMessage("ghost", FrameMsg(token, "ghost", "e", "getValue", "[\"k\"]")));

        host.UnregisterFrame("f-1");
        Assert.Contains("Unknown frame", await host.HandleMessage("f-1", FrameMsg(token, "f-1", "f", "getValue", "[\"k\"]")));
    }

    [Fact]
    public async Task Frames_with_foreign_urls_and_noframes_scripts_are_refused()
    {
        var host = NewHost();
        string id = await host.InstallFromSourceAsync(Script(extra: "// @noframes\n"));
        string token = TokenOf(host.BuildInjectionScript(id)!);

        host.RegisterFrame("child", Page, incognito: false, isMainFrame: false);
        Assert.Contains("Access denied", await host.HandleMessage("child", FrameMsg(token, "child", "n1", "log", "[\"hi\"]")));

        host.RegisterFrame("top", Page, incognito: false, isMainFrame: true);
        Assert.Contains("\"ok\":true", await host.HandleMessage("top", FrameMsg(token, "top", "n2", "log", "[\"hi\"]")));

        host.RegisterFrame("ads", "https://evil.test/", incognito: false, isMainFrame: false);
        Assert.Contains("Access denied", await host.HandleMessage("ads", FrameMsg(token, "ads", "n3", "log", "[\"hi\"]")));
    }

    [Fact]
    public void Invalid_frame_ids_are_rejected()
    {
        var host = NewHost();
        Assert.Throws<ArgumentException>(() => host.RegisterFrame("bad id!", Page, incognito: false));
        Assert.Throws<ArgumentException>(() => host.RegisterFrame("", Page, incognito: false));
        Assert.Throws<ArgumentException>(() => host.BuildInjectionScript("missing", frameId: "../x"));
    }

    // ---------------------------------------------------------------------
    // Synchronous values and quota

    [Fact]
    public async Task Initial_values_are_embedded_for_synchronous_get_value()
    {
        var host = NewHost();
        string id = await host.InstallFromSourceAsync(Script(extra: "// @grant GM_getValue\n// @grant GM_setValue\n"));
        string token = TokenOf(host.BuildInjectionScript(id)!);
        await host.HandleMessageAsync(Msg(token, "v1", "setValue", "[\"count\",\"42\"]"), Page);
        await host.HandleMessageAsync(Msg(token, "v2", "setValue", "[\"cfg\",\"{\\\"on\\\":true}\"]"), Page);

        string injection = host.BuildInjectionScript(id)!;
        Assert.Contains("\"values\":{", injection);
        Assert.Contains("\"count\":42", injection);
        Assert.Contains("\"cfg\":{\"on\":true}", injection);
        Assert.Contains("api.GM_getValue = function", injection);
        Assert.Contains("__valueText", injection);
    }

    [Fact]
    public async Task Incognito_values_are_only_embedded_in_incognito_injection()
    {
        var host = NewHost();
        string id = await host.InstallFromSourceAsync(Script(extra: "// @grant GM_setValue\n"));
        string token = TokenOf(host.BuildInjectionScript(id)!);
        await host.HandleMessageAsync(Msg(token, "i1", "setValue", "[\"secret\",\"1\"]"), Page, incognito: true);

        Assert.DoesNotContain("\"secret\":1", host.BuildInjectionScript(id, incognito: false)!);
        Assert.Contains("\"secret\":1", host.BuildInjectionScript(id, incognito: true)!);
    }

    [Fact]
    public async Task Storage_quota_per_script_is_enforced()
    {
        var host = NewHost();
        string id = await host.InstallFromSourceAsync(Script(extra: "// @grant GM_setValue\n"));
        string token = TokenOf(host.BuildInjectionScript(id)!);
        string big = new string('x', 600_000);

        Assert.Contains("\"ok\":true", await host.HandleMessageAsync(Msg(token, "q1", "setValue", "[\"a\",\"\\\"" + big + "\\\"\"]"), Page));
        Assert.Contains("\"ok\":false", await host.HandleMessageAsync(Msg(token, "q2", "setValue", "[\"b\",\"\\\"" + big + "\\\"\"]"), Page));
        Assert.Contains("\"ok\":true", await host.HandleMessageAsync(Msg(token, "q3", "setValue", "[\"a\",\"\\\"small\\\"\"]"), Page));
    }

    [Fact]
    public async Task Tokens_are_per_script_and_do_not_work_on_other_pages()
    {
        var host = NewHost();
        string a = await host.InstallFromSourceAsync(Script(name: "A", match: "https://a.test/*", extra: "// @grant GM_getValue\n"));
        string b = await host.InstallFromSourceAsync(Script(name: "B", match: "https://b.test/*", extra: "// @grant GM_getValue\n"));
        string tokenA = TokenOf(host.BuildInjectionScript(a)!);
        string tokenB = TokenOf(host.BuildInjectionScript(b)!);

        Assert.NotEqual(tokenA, tokenB);
        Assert.Contains("\"ok\":false", await host.HandleMessageAsync(Msg(tokenA, "t1", "getValue", "[\"k\"]"), "https://b.test/page"));
        Assert.Contains("\"ok\":true", await host.HandleMessageAsync(Msg(tokenB, "t2", "getValue", "[\"k\"]"), "https://b.test/page"));
    }

    [Fact]
    public async Task Removing_a_script_clears_its_web_request_rules()
    {
        var host = NewHost();
        string id = await host.InstallFromSourceAsync(Script(extra: "// @grant GM_webRequest\n"));
        host.ApprovePermission(id, UserScriptPermissions.WebRequest);
        string token = TokenOf(host.BuildInjectionScript(id)!);
        await host.HandleMessageAsync(Msg(token, "r1", "webRequest", "[[{\"selector\":\"https://ads.test/*\",\"action\":\"cancel\"}]]"), Page);
        Assert.Equal(WebRequestActionKind.Cancel, host.EvaluateWebRequest("https://ads.test/x").Kind);

        host.Remove(id);
        Assert.Equal(WebRequestActionKind.Allow, host.EvaluateWebRequest("https://ads.test/x").Kind);
    }
}
