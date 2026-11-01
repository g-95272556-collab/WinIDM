using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace WinIDM.Browser.Tests;

public class UserScriptHostTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "winidm-us-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string Header(string extra = "") => "// ==UserScript==\n" +
        "// @name        Demo\n" +
        "// @namespace   https://demo.test/\n" +
        "// @version     1.2.3\n" +
        "// @author      Tester\n" +
        "// @description Demo script\n" +
        "// @match       https://site.test/*\n" +
        "// @include     /^https:\\/\\/regex\\.test\\/\\d+$/\n" +
        "// @exclude     https://site.test/private/*\n" +
        "// @grant       GM_getValue\n" +
        "// @grant       GM_setValue\n" +
        "// @grant       GM_xmlhttpRequest\n" +
        "// @connect     127.0.0.1\n" +
        "// @run-at      document-start\n" +
        extra +
        "// ==/UserScript==\n";

    private static string Script(string extra = "", string body = "console.log('demo-marker');") =>
        Header(extra) + body + "\n";

    private static UserScriptHost NewHost(string? store = null) => new(store) { RequireHttps = false };

    private static string TokenOf(string injection)
    {
        var m = Regex.Match(injection, "\"token\":\"([^\"]+)\"");
        Assert.True(m.Success, "Token not found in the injection");
        return m.Groups[1].Value;
    }

    private static string Msg(string token, string id, string api, string argsJson = "[]") =>
        "{\"winidm\":1,\"t\":\"" + token + "\",\"id\":\"" + id + "\",\"api\":\"" + api + "\",\"args\":" + argsJson + "}";

    private static string Origin(string url = "https://site.test/page") => url;

    // ---------------------------------------------------------------------
    // Parser

    [Fact]
    public void Parser_reads_header_fields()
    {
        var meta = UserScriptParser.Parse(Script());
        Assert.Equal("Demo", meta.Name);
        Assert.Equal("https://demo.test/", meta.Namespace);
        Assert.Equal("1.2.3", meta.Version);
        Assert.Equal("Tester", meta.Author);
        Assert.Equal("Demo script", meta.Description);
        Assert.Equal(new[] { "https://site.test/*" }, meta.Matches);
        Assert.Single(meta.Includes);
        Assert.Equal(new[] { "https://site.test/private/*" }, meta.Excludes);
        Assert.Equal(new[] { "GM_getValue", "GM_setValue", "GM_xmlhttpRequest" }, meta.Grants);
        Assert.Equal(new[] { "127.0.0.1" }, meta.Connects);
        Assert.Equal("document-start", meta.RunAt);
        Assert.False(meta.NoFrames);
    }

    [Fact]
    public void Parser_rejects_missing_block_and_missing_name()
    {
        Assert.Throws<FormatException>(() => UserScriptParser.Parse("console.log(1);"));
        Assert.Throws<FormatException>(() => UserScriptParser.Parse("// ==UserScript==\n// @match *://*/*\n// ==/UserScript==\n"));
        Assert.Throws<FormatException>(() => UserScriptParser.Parse("// ==UserScript==\n// @name X\n"));
    }

    [Fact]
    public void Parser_defaults_run_at_and_validates_requires_and_resources()
    {
        var meta = UserScriptParser.Parse("// ==UserScript==\n// @name A\n// @run-at weird\n// @grant none\n// ==/UserScript==\n");
        Assert.Equal("document-end", meta.RunAt);
        Assert.Empty(meta.Grants);

        Assert.Throws<FormatException>(() => UserScriptParser.Parse("// ==UserScript==\n// @name A\n// @require ftp://x.test/a.js\n// ==/UserScript==\n"));
        Assert.Throws<FormatException>(() => UserScriptParser.Parse("// ==UserScript==\n// @name A\n// @resource onlyname\n// ==/UserScript==\n"));

        var withRes = UserScriptParser.Parse("// ==UserScript==\n// @name A\n// @resource logo https://x.test/logo.png\n// @noframes\n// ==/UserScript==\n");
        Assert.Equal("logo", withRes.Resources[0].Name);
        Assert.True(withRes.NoFrames);
    }

    // ---------------------------------------------------------------------
    // URL matching

    [Fact]
    public async Task ScriptsForUrl_applies_match_include_and_exclude()
    {
        var host = NewHost();
        await host.InstallFromSourceAsync(Script());

        Assert.Single(host.ScriptsForUrl("https://site.test/page"));
        Assert.Empty(host.ScriptsForUrl("https://site.test/private/x"));
        Assert.Single(host.ScriptsForUrl("https://regex.test/123"));
        Assert.Empty(host.ScriptsForUrl("https://regex.test/abc"));
        Assert.Empty(host.ScriptsForUrl("https://other.test/"));
        Assert.Empty(host.ScriptsForUrl("file:///C:/x.html"));
        Assert.Empty(host.ScriptsForUrl("not a url"));
    }

    [Fact]
    public async Task Wildcard_host_and_all_urls_patterns()
    {
        var host = NewHost();
        await host.InstallFromSourceAsync("// ==UserScript==\n// @name Wild\n// @match *://*.example.com/*\n// ==/UserScript==\n", "https://w.test/a.user.js");
        await host.InstallFromSourceAsync("// ==UserScript==\n// @name All\n// @match <all_urls>\n// ==/UserScript==\n");
        await host.InstallFromSourceAsync("// ==UserScript==\n// @name Ex\n// @match https://*/*\n// @exclude-match https://skip.test/*\n// ==/UserScript==\n");

        Assert.Equal(3, host.ScriptsForUrl("https://a.example.com/x").Count);
        Assert.Equal(3, host.ScriptsForUrl("https://example.com/").Count);
        Assert.Single(host.ScriptsForUrl("http://notexample.com/"));
        Assert.Single(host.ScriptsForUrl("https://skip.test/"));
        Assert.Equal("https://w.test/a.user.js", host.ListScripts().First(s => s.Name == "Wild").SourceUrl);
    }

    // ---------------------------------------------------------------------
    // Install, SHA-256 pin, and manipulation

    [Fact]
    public async Task Install_from_url_verifies_require_hash_and_rejects_mismatch()
    {
        const string lib = "window.libMarker = 'lib-marker';";
        string libHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(lib))).ToLowerInvariant();
        string scriptText = "";
        using var server = new TestHttpServer(new Dictionary<string, Func<HttpCall, HttpReply>>
        {
            ["/lib.js"] = _ => HttpReply.Text(lib, contentType: "text/javascript"),
            ["/demo.user.js"] = _ => HttpReply.Text(scriptText, contentType: "text/javascript"),
        });

        scriptText = Script($"// @require {server.BaseUrl}/lib.js#sha256={libHash}\n");
        var host = NewHost();
        string id = await host.InstallFromUrlAsync(server.BaseUrl + "/demo.user.js");
        string? injection = host.BuildInjectionScript(id);
        Assert.NotNull(injection);
        Assert.Contains("lib-marker", injection);
        Assert.Contains("console.log('demo-marker');", injection);

        scriptText = Script($"// @require {server.BaseUrl}/lib.js#sha256={new string('0', 64)}\n");
        await Assert.ThrowsAsync<UserScriptIntegrityException>(() => host.InstallFromUrlAsync(server.BaseUrl + "/demo.user.js"));

        scriptText = Script($"// @require {server.BaseUrl}/lib.js#sha256=short\n");
        await Assert.ThrowsAsync<FormatException>(() => host.InstallFromUrlAsync(server.BaseUrl + "/demo.user.js"));
    }

    [Fact]
    public async Task Preview_then_install_and_https_requirement()
    {
        using var server = new TestHttpServer(new Dictionary<string, Func<HttpCall, HttpReply>>
        {
            ["/p.user.js"] = _ => HttpReply.Text(Script()),
        });

        var host = NewHost();
        var preview = await host.PreviewFromUrlAsync(server.BaseUrl + "/p.user.js");
        Assert.Equal("Demo", preview.Meta.Name);
        Assert.Equal(server.BaseUrl + "/p.user.js", preview.SourceUrl);
        Assert.Empty(host.ListScripts());
        string id = host.Install(preview);
        Assert.Single(host.ListScripts());
        Assert.Equal(id, host.ListScripts()[0].Id);

        var strict = new UserScriptHost();
        await Assert.ThrowsAsync<InvalidOperationException>(() => strict.InstallFromUrlAsync("http://127.0.0.1:1/x.user.js"));
        await Assert.ThrowsAsync<ArgumentException>(() => host.PreviewFromUrlAsync("ftp://x.test/a.user.js"));
    }

    [Fact]
    public async Task Persisted_pin_and_source_tampering_blocks_injection()
    {
        Directory.CreateDirectory(_dir);
        string store = Path.Combine(_dir, "scripts.json");
        const string lib = "window.libOriginal = 'original-lib';";
        using var server = new TestHttpServer(new Dictionary<string, Func<HttpCall, HttpReply>>
        {
            ["/lib.js"] = _ => HttpReply.Text(lib),
        });

        var host = NewHost(store);
        string id = await host.InstallFromSourceAsync(Script($"// @require {server.BaseUrl}/lib.js\n", "console.log('demo-marker');"));
        Assert.NotNull(host.BuildInjectionScript(id));
        host.Dispose();

        string json = File.ReadAllText(store);
        string originalB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(lib));
        string evilB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("window.evil = 1;"));
        Assert.Contains(originalB64, json);
        File.WriteAllText(store, json.Replace(originalB64, evilB64));

        var reloaded = NewHost(store);
        Assert.Null(reloaded.BuildInjectionScript(id));
        Assert.Contains(reloaded.GetLog(id), e => e.Level == "ERROR" && e.Message.Contains("SHA-256"));
        reloaded.Dispose();

        // Tamper with the script source itself.
        string restored = File.ReadAllText(store).Replace(evilB64, originalB64);
        File.WriteAllText(store, restored.Replace("demo-marker", "demo-evil-marker"));
        var tampered = NewHost(store);
        Assert.Null(tampered.BuildInjectionScript(id));
        tampered.Dispose();
    }

    [Fact]
    public async Task Store_survives_reload_and_recovers_from_corrupt_file()
    {
        Directory.CreateDirectory(_dir);
        string store = Path.Combine(_dir, "s.json");
        var host = NewHost(store);
        string id = await host.InstallFromSourceAsync(Script());
        host.SetEnabled(id, false);
        host.Dispose();

        var again = NewHost(store);
        var summary = again.ListScripts().Single();
        Assert.False(summary.Enabled);
        again.Dispose();

        File.WriteAllText(store, "{ not json");
        var recovered = NewHost(store);
        Assert.Empty(recovered.ListScripts());
        Assert.Contains(recovered.GetLog(), e => e.Message.Contains("corrupt"));
        recovered.Dispose();
        Assert.True(Directory.GetFiles(_dir, "s.json.rosak-*").Length >= 1);
    }

    [Fact]
    public async Task Reinstall_keeps_values_and_enabled_state_and_drops_unknown_approvals()
    {
        var host = NewHost();
        string id = await host.InstallFromSourceAsync(Script());
        string token = TokenOf(host.BuildInjectionScript(id)!);
        await host.HandleMessageAsync(Msg(token, "s1", "setValue", "[\"k\",\"42\"]"), Origin());
        host.ApprovePermission(id, UserScriptPermissions.Xhr);
        host.SetEnabled(id, false);

        await host.InstallFromSourceAsync(Script());
        var summary = host.ListScripts().Single();
        Assert.False(summary.Enabled);
        Assert.Contains(UserScriptPermissions.Xhr, summary.Approved);

        string noXhr = Header().Replace("// @grant       GM_xmlhttpRequest\n", "") + "x();\n";
        await host.InstallFromSourceAsync(noXhr);
        Assert.Empty(host.ListScripts().Single().Approved);

        host.SetEnabled(id, true);
        string reply = await host.HandleMessageAsync(Msg(token, "s2", "getValue", "[\"k\"]"), Origin());
        Assert.Contains("\"result\":42", reply);
    }

    // ---------------------------------------------------------------------
    // Token, origin, and permissions

    [Fact]
    public async Task Messages_require_valid_token_and_matching_origin()
    {
        var host = NewHost();
        string id = await host.InstallFromSourceAsync(Script());
        string token = TokenOf(host.BuildInjectionScript(id)!);

        string bad = await host.HandleMessageAsync(Msg("wrong-token", "m1", "getValue", "[\"k\"]"), Origin());
        Assert.Contains("\"ok\":false", bad);
        Assert.Contains("Access denied", bad);

        string offOrigin = await host.HandleMessageAsync(Msg(token, "m2", "getValue", "[\"k\"]"), "https://evil.test/");
        Assert.Contains("\"ok\":false", offOrigin);

        string ok = await host.HandleMessageAsync(Msg(token, "m3", "getValue", "[\"k\"]"), Origin());
        Assert.Equal("{\"winidm\":1,\"id\":\"m3\",\"ok\":true,\"result\":null}", ok);

        host.SetEnabled(id, false);
        Assert.Contains("\"ok\":false", await host.HandleMessageAsync(Msg(token, "m4", "getValue", "[\"k\"]"), Origin()));
    }

    [Fact]
    public async Task Value_storage_round_trip_list_and_delete()
    {
        var host = NewHost();
        string id = await host.InstallFromSourceAsync(Script("// @grant       GM_listValues\n// @grant       GM_deleteValue\n"));
        string token = TokenOf(host.BuildInjectionScript(id)!);

        Assert.Contains("\"ok\":true", await host.HandleMessageAsync(Msg(token, "a", "setValue", "[\"zeta\",\"{\\\"n\\\":1}\"]"), Origin()));
        await host.HandleMessageAsync(Msg(token, "b", "setValue", "[\"alpha\",\"true\"]"), Origin());

        string got = await host.HandleMessageAsync(Msg(token, "c", "getValue", "[\"zeta\"]"), Origin());
        Assert.Contains("\"result\":{\"n\":1}", got);

        string list = await host.HandleMessageAsync(Msg(token, "d", "listValues"), Origin());
        Assert.Contains("\"result\":[\"alpha\",\"zeta\"]", list);

        await host.HandleMessageAsync(Msg(token, "e", "deleteValue", "[\"alpha\"]"), Origin());
        Assert.DoesNotContain("alpha", await host.HandleMessageAsync(Msg(token, "f", "listValues"), Origin()));

        string invalidJson = await host.HandleMessageAsync(Msg(token, "g", "setValue", "[\"k\",\"{broken\"]"), Origin());
        Assert.Contains("\"ok\":false", invalidJson);

        string emptyKey = await host.HandleMessageAsync(Msg(token, "h", "getValue", "[\"\"]"), Origin());
        Assert.Contains("\"ok\":false", emptyKey);
    }

    [Fact]
    public async Task Values_persist_to_disk_but_incognito_values_do_not()
    {
        Directory.CreateDirectory(_dir);
        string store = Path.Combine(_dir, "v.json");
        var host = NewHost(store);
        string id = await host.InstallFromSourceAsync(Script());
        string token = TokenOf(host.BuildInjectionScript(id)!);
        await host.HandleMessageAsync(Msg(token, "p1", "setValue", "[\"persist\",\"1\"]"), Origin());
        await host.HandleMessageAsync(Msg(token, "p2", "setValue", "[\"private\",\"2\"]"), Origin(), incognito: true);
        host.Dispose();

        var reloaded = NewHost(store);
        string reloadedToken = TokenOf(reloaded.BuildInjectionScript(id)!);
        Assert.Contains("\"result\":1", await reloaded.HandleMessageAsync(Msg(reloadedToken, "r1", "getValue", "[\"persist\"]"), Origin()));
        Assert.Contains("\"result\":null", await reloaded.HandleMessageAsync(Msg(reloadedToken, "r2", "getValue", "[\"private\"]"), Origin()));
        reloaded.Dispose();
    }

    [Fact]
    public async Task Undeclared_or_unapproved_permissions_are_denied_and_logged()
    {
        var host = NewHost();
        string id = await host.InstallFromSourceAsync(Script());
        string token = TokenOf(host.BuildInjectionScript(id)!);

        string noGrant = await host.HandleMessageAsync(Msg(token, "d1", "download", "[{\"url\":\"https://x.test/a.bin\"}]"), Origin());
        Assert.Contains("\"ok\":false", noGrant);
        Assert.Contains(host.GetLog(id), e => e.Level == "PERMISSION DENIED");

        string unapproved = await host.HandleMessageAsync(Msg(token, "d2", "openTab", "[\"https://x.test/\"]"), Origin());
        Assert.Contains("\"ok\":false", unapproved);

        Assert.Contains("\"ok\":false", await host.HandleMessageAsync(Msg(token, "d3", "unknownApi"), Origin()));
        Assert.Contains("\"ok\":true", await host.HandleMessageAsync(Msg(token, "d4", "log", "[\"hello\",{\"a\":1}]"), Origin()));
        Assert.Contains(host.GetLog(id), e => e.Level == "INFO" && e.Message.Contains("hello"));
        Assert.Contains("\"ok\":false", await host.HandleMessageAsync("{not json", Origin()));
        Assert.Contains("\"ok\":false", await host.HandleMessageAsync("[]", Origin()));
        Assert.Contains("\"ok\":false", await host.HandleMessageAsync("{\"winidm\":2,\"t\":\"x\"}", Origin()));
    }

    [Fact]
    public async Task Approve_rejects_undeclared_permission_and_wildcard_connect_needs_declaration()
    {
        var host = NewHost();
        string id = await host.InstallFromSourceAsync(Script());
        Assert.Throws<ArgumentException>(() => host.ApprovePermission(id, UserScriptPermissions.Download));
        Assert.Throws<ArgumentException>(() => host.ApprovePermission(id, UserScriptPermissions.ConnectWildcard));
        Assert.Throws<KeyNotFoundException>(() => host.ApprovePermission("missing", UserScriptPermissions.Xhr));
        Assert.Throws<KeyNotFoundException>(() => host.SetEnabled("missing", true));

        host.ApprovePermission(id, UserScriptPermissions.Xhr);
        host.ApprovePermission(id, UserScriptPermissions.Xhr);
        Assert.Single(host.ListScripts().Single().Approved);
        host.RevokePermission(id, UserScriptPermissions.Xhr);
        Assert.Empty(host.ListScripts().Single().Approved);
        host.Remove("missing");
    }

    // ---------------------------------------------------------------------
    // XHR (implemented by the host)

    [Fact]
    public async Task Xhr_executes_only_for_approved_declared_connect_hosts()
    {
        using var server = new TestHttpServer(new Dictionary<string, Func<HttpCall, HttpReply>>
        {
            ["/data"] = call => HttpReply.Text("payload:" + call.Method, 200),
            ["/redir"] = _ => HttpReply.Redirect("http://localhost:1/never"),
            ["/redir-ok"] = _ => HttpReply.Redirect("/data"),
            ["/echo"] = call => HttpReply.Text(Encoding.UTF8.GetString(call.Body)),
        });

        var host = NewHost();
        string id = await host.InstallFromSourceAsync(Script());
        string token = TokenOf(host.BuildInjectionScript(id)!);
        host.ApprovePermission(id, UserScriptPermissions.Xhr);

        string reply = await host.HandleMessageAsync(
            Msg(token, "x1", "xhr", "[{\"method\":\"post\",\"url\":\"" + server.BaseUrl + "/data\"}]"), Origin());
        Assert.Contains("\"ok\":true", reply);
        Assert.Contains("payload:POST", reply);
        Assert.Contains("\"status\":200", reply);
        Assert.Contains("finalUrl", reply);
        Assert.Equal("POST", server.Calls.Last().Method);

        string echo = await host.HandleMessageAsync(
            Msg(token, "x2", "xhr", "[{\"method\":\"PUT\",\"url\":\"" + server.BaseUrl + "/echo\",\"data\":\"body-text\",\"headers\":{\"X-Test\":\"1\",\"Cookie\":\"secret=1\",\"Content-Type\":\"text/plain\"}}]"), Origin());
        Assert.Contains("body-text", echo);
        Assert.DoesNotContain("secret=1", string.Join("|", server.Calls.Where(c => c.Path == "/echo").Select(c => c.Headers.GetValueOrDefault("Cookie") ?? "")));

        string redirectOut = await host.HandleMessageAsync(
            Msg(token, "x3", "xhr", "[{\"url\":\"" + server.BaseUrl + "/redir\"}]"), Origin());
        Assert.Contains("\"ok\":false", redirectOut);
        Assert.Contains(host.GetLog(id), e => e.Message.Contains("redirect", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("localhost"));

        string redirectIn = await host.HandleMessageAsync(
            Msg(token, "x4", "xhr", "[{\"url\":\"" + server.BaseUrl + "/redir-ok\"}]"), Origin());
        Assert.Contains("payload:GET", redirectIn);

        string notConnect = await host.HandleMessageAsync(
            Msg(token, "x5", "xhr", "[{\"url\":\"http://localhost:" + server.Port + "/data\"}]"), Origin());
        Assert.Contains("\"ok\":false", notConnect);

        string badScheme = await host.HandleMessageAsync(
            Msg(token, "x6", "xhr", "[{\"url\":\"ftp://127.0.0.1/data\"}]"), Origin());
        Assert.Contains("\"ok\":false", badScheme);

        string badMethod = await host.HandleMessageAsync(
            Msg(token, "x7", "xhr", "[{\"method\":\"TRACE\",\"url\":\"" + server.BaseUrl + "/data\"}]"), Origin());
        Assert.Contains("\"ok\":false", badMethod);
    }

    [Fact]
    public async Task Connect_wildcard_requires_explicit_user_approval()
    {
        using var server = new TestHttpServer(new Dictionary<string, Func<HttpCall, HttpReply>>
        {
            ["/w"] = _ => HttpReply.Text("wild"),
        });
        string source = Header().Replace("// @connect     127.0.0.1", "// @connect     *") + "x();\n";
        var host = NewHost();
        string id = await host.InstallFromSourceAsync(source);
        string token = TokenOf(host.BuildInjectionScript(id)!);
        host.ApprovePermission(id, UserScriptPermissions.Xhr);

        string denied = await host.HandleMessageAsync(Msg(token, "w1", "xhr", "[{\"url\":\"" + server.BaseUrl + "/w\"}]"), Origin());
        Assert.Contains("\"ok\":false", denied);

        host.ApprovePermission(id, UserScriptPermissions.ConnectWildcard);
        string allowed = await host.HandleMessageAsync(Msg(token, "w2", "xhr", "[{\"url\":\"" + server.BaseUrl + "/w\"}]"), Origin());
        Assert.Contains("wild", allowed);
    }

    [Fact]
    public async Task Xhr_without_connect_declaration_is_refused()
    {
        string source = Header().Replace("// @connect     127.0.0.1\n", "") + "x();\n";
        var host = NewHost();
        string id = await host.InstallFromSourceAsync(source);
        string token = TokenOf(host.BuildInjectionScript(id)!);
        host.ApprovePermission(id, UserScriptPermissions.Xhr);
        Assert.Contains("\"ok\":false", await host.HandleMessageAsync(Msg(token, "n1", "xhr", "[{\"url\":\"http://127.0.0.1:1/x\"}]"), Origin()));
    }

    // ---------------------------------------------------------------------
    // UI requests, menu, and injection

    [Fact]
    public async Task UI_requests_are_raised_after_approval()
    {
        string source = Script().Replace("// @grant       GM_xmlhttpRequest\n",
            "// @grant       GM_xmlhttpRequest\n// @grant       GM_openInTab\n// @grant       GM_registerMenuCommand\n// @grant       GM_notification\n");
        var host = NewHost();
        var events = new List<UserScriptUiRequest>();
        host.UiRequested += e => events.Add(e);
        string id = await host.InstallFromSourceAsync(source);
        string token = TokenOf(host.BuildInjectionScript(id)!);
        host.ApprovePermission(id, UserScriptPermissions.OpenInTab);
        host.ApprovePermission(id, UserScriptPermissions.Notification);

        string tab = await host.HandleMessageAsync(Msg(token, "t1", "openTab", "[\"https://news.test/a?b=1\"]"), Origin());
        Assert.Contains("\"ok\":true", tab);
        Assert.Contains(events, e => e.Kind == "openTab" && e.PayloadJson.Contains("news.test"));

        Assert.Contains("\"ok\":false", await host.HandleMessageAsync(Msg(token, "t2", "openTab", "[\"javascript:alert(1)\"]"), Origin()));

        string notify = await host.HandleMessageAsync(Msg(token, "n1", "notify", "[{\"text\":\"hi\",\"title\":\"T\"}]"), Origin());
        Assert.Contains("\"ok\":true", notify);
        Assert.Contains(events, e => e.Kind == "notification" && e.PayloadJson.Contains("hi"));

        string menu = await host.HandleMessageAsync(Msg(token, "m1", "menu", "[\"Open\",\"cid-1\"]"), Origin());
        Assert.Contains("\"ok\":true", menu);
        Assert.Contains(events, e => e.Kind == "menu" && e.PayloadJson.Contains("cid-1"));

        string invocation = host.BuildMenuInvocation("cid-1");
        Assert.Contains("\"invoke\":\"menu\"", invocation);
        Assert.Contains("\"cid\":\"cid-1\"", invocation);
    }

    [Fact]
    public async Task Failing_ui_handler_is_contained_and_logged()
    {
        string source = Script().Replace("// @grant       GM_xmlhttpRequest\n", "// @grant       GM_openInTab\n");
        var host = NewHost();
        host.UiRequested += _ => throw new InvalidOperationException("ui down");
        string id = await host.InstallFromSourceAsync(source);
        string token = TokenOf(host.BuildInjectionScript(id)!);
        host.ApprovePermission(id, UserScriptPermissions.OpenInTab);
        Assert.Contains("\"ok\":true", await host.HandleMessageAsync(Msg(token, "f1", "openTab", "[\"https://a.test/\"]"), Origin()));
        Assert.Contains(host.GetLog(id), e => e.Level == "ERROR" && e.Message.Contains("ui down"));
    }

    [Fact]
    public async Task Injection_exposes_only_declared_grants_and_no_secrets()
    {
        var host = NewHost();
        string id = await host.InstallFromSourceAsync(Header().Replace("// @grant       GM_setValue\n", "") + "x();\n");
        string injection = host.BuildInjectionScript(id)!;
        Assert.Contains("\"declared\":[\"GM_getValue\",\"GM_xmlhttpRequest\"]", injection);
        Assert.Contains("GM_info", injection);
        Assert.Contains("__report", injection);
        Assert.Contains("api.GM_getValue = function", injection);
        Assert.DoesNotContain("@e-mail", injection, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("android_id", injection, StringComparison.OrdinalIgnoreCase);

        Assert.Null(host.BuildInjectionScript("missing"));
        host.SetEnabled(id, false);
        Assert.Null(host.BuildInjectionScript(id));
    }

    [Fact]
    public async Task Incognito_injection_uses_separate_value_namespace_in_memory()
    {
        var host = NewHost();
        string id = await host.InstallFromSourceAsync(Script());
        string token = TokenOf(host.BuildInjectionScript(id, incognito: true)!);
        Assert.Contains("\"incognito\":true", host.BuildInjectionScript(id, incognito: true)!);
        await host.HandleMessageAsync(Msg(token, "i1", "setValue", "[\"k\",\"7\"]"), Origin(), incognito: true);
        Assert.Contains("\"result\":7", await host.HandleMessageAsync(Msg(token, "i2", "getValue", "[\"k\"]"), Origin(), incognito: true));
        Assert.Contains("\"result\":null", await host.HandleMessageAsync(Msg(token, "i3", "getValue", "[\"k\"]"), Origin(), incognito: false));
    }

    [Fact]
    public async Task Remove_deletes_script_and_its_log_context()
    {
        var host = NewHost();
        string id = await host.InstallFromSourceAsync(Script());
        Assert.NotNull(host.GetMeta(id));
        host.Remove(id);
        Assert.Null(host.GetMeta(id));
        Assert.Empty(host.ScriptsForUrl("https://site.test/page"));
        Assert.Empty(host.ListScripts());
    }

    [Fact]
    public void Build_menu_invocation_escapes_identifiers()
    {
        var host = NewHost();
        string json = host.BuildMenuInvocation("a\"b");
        // System.Text.Json encodes the quote as \u0022; what matters is that the JSON is valid and the value survives.
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("a\"b", doc.RootElement.GetProperty("cid").GetString());
        Assert.Equal("menu", doc.RootElement.GetProperty("invoke").GetString());
    }
}
