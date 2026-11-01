using System.Net;
using System.Web;
using WinIDM.Backup;

namespace WinIDM.Backup.Tests;

public class OAuthTests
{
    private const string TokenJson = "{\"access_token\":\"at-1\",\"refresh_token\":\"rt-1\",\"expires_in\":3600}";

    [Fact]
    public void Pkce_matches_rfc7636_appendix_b_vector()
    {
        const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";

        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", Pkce.Challenge(verifier));
    }

    [Fact]
    public void Pkce_create_gives_43_character_verifier_and_matching_challenge()
    {
        var (verifier, challenge) = Pkce.Create();

        Assert.Equal(43, verifier.Length);
        Assert.Equal(Pkce.Challenge(verifier), challenge);
        Assert.DoesNotContain('=', verifier);
    }

    [Fact]
    public void Authorization_url_carries_pkce_s256_scope_and_extra_parameters()
    {
        var authorizer = new LoopbackPkceAuthorizer(OAuthClientOptions.ForGoogleDrive("cid-test"), new HttpClient(),
            (_, _) => Task.CompletedTask);

        var url = authorizer.BuildAuthorizationUrl("http://127.0.0.1:5555/", "state-1", "challenge-1");
        var query = HttpUtility.ParseQueryString(url.Query);

        Assert.Equal("accounts.google.com", url.Host);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("cid-test", query["client_id"]);
        Assert.Equal("http://127.0.0.1:5555/", query["redirect_uri"]);
        Assert.Equal("state-1", query["state"]);
        Assert.Equal("challenge-1", query["code_challenge"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("offline", query["access_type"]);
        Assert.Contains("drive.file", query["scope"]);
    }

    [Fact]
    public void Missing_client_id_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new LoopbackPkceAuthorizer(OAuthClientOptions.ForDropbox(string.Empty), new HttpClient(), (_, _) => Task.CompletedTask));
    }

    [Fact]
    public void Callback_with_wrong_state_is_rejected()
    {
        var ok = LoopbackPkceAuthorizer.TryParseCallback(
            new Uri("http://127.0.0.1:1/?code=abc&state=other"), "expected", out var code, out var error);

        Assert.False(ok);
        Assert.Null(code);
        Assert.Contains("state does not match", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Callback_with_provider_error_is_rejected()
    {
        var ok = LoopbackPkceAuthorizer.TryParseCallback(
            new Uri("http://127.0.0.1:1/?error=access_denied&state=expected"), "expected", out _, out var error);

        Assert.False(ok);
        Assert.Contains("access_denied", error);
    }

    [Fact]
    public void Callback_with_matching_state_returns_code()
    {
        var ok = LoopbackPkceAuthorizer.TryParseCallback(
            new Uri("http://127.0.0.1:1/?code=code-123&state=expected"), "expected", out var code, out _);

        Assert.True(ok);
        Assert.Equal("code-123", code);
    }

    [Fact]
    public async Task Loopback_flow_exchanges_code_with_pkce_verifier_and_caches_token()
    {
        var tokenHandler = new MockHttpHandler(_ => TestHttp.Json(TokenJson));
        var openCount = 0;
        using var browser = new HttpClient(new HttpClientHandler { UseProxy = false });

        Task OpenBrowser(Uri authorizationUrl, CancellationToken ct)
        {
            openCount++;
            var query = HttpUtility.ParseQueryString(authorizationUrl.Query);
            var redirect = query["redirect_uri"]!;
            var state = query["state"]!;

            // Simulate the browser: call the loopback back asynchronously after the listener starts.
            _ = Task.Run(() => browser.GetAsync($"{redirect}?code=code-test&state={Uri.EscapeDataString(state)}"), ct);
            return Task.CompletedTask;
        }

        var authorizer = new LoopbackPkceAuthorizer(
            OAuthClientOptions.ForGoogleDrive("cid-test"),
            new HttpClient(tokenHandler),
            OpenBrowser,
            timeout: TimeSpan.FromSeconds(20));

        var token = await authorizer.GetAccessTokenAsync();
        var cached = await authorizer.GetAccessTokenAsync();

        Assert.Equal("at-1", token);
        Assert.Equal("at-1", cached);
        Assert.Equal(1, openCount);
        Assert.Equal("rt-1", authorizer.CurrentToken!.RefreshToken);

        var sent = Assert.Single(tokenHandler.Sent);
        var form = HttpUtility.ParseQueryString(sent.Body!);
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal("code-test", form["code"]);
        Assert.Equal("cid-test", form["client_id"]);
        Assert.Null(form["client_secret"]);
        Assert.StartsWith("http://127.0.0.1:", form["redirect_uri"]);
        Assert.Equal(43, form["code_verifier"]!.Length);
    }

    [Fact]
    public async Task Token_endpoint_error_is_reported_without_leaking_description()
    {
        var handler = new MockHttpHandler(_ => TestHttp.Json(
            "{\"error\":\"invalid_grant\",\"error_description\":\"secret-internal\"}", HttpStatusCode.BadRequest));
        var authorizer = new LoopbackPkceAuthorizer(OAuthClientOptions.ForDropbox("cid-test"), new HttpClient(handler),
            (_, _) => Task.CompletedTask);

        var ex = await Assert.ThrowsAsync<BackupTransportException>(() => authorizer.RefreshAsync("rt-old"));

        Assert.Equal(400, ex.StatusCode);
        Assert.Contains("invalid_grant", ex.Message);
        Assert.DoesNotContain("secret-internal", ex.Message);
    }

    [Fact]
    public async Task Google_drive_sends_bearer_and_uses_patch_when_file_exists()
    {
        var handler = new MockHttpHandler(req => req.Method == "GET"
            ? TestHttp.Json("{\"files\":[{\"id\":\"f1\",\"name\":\"winidm-backup-20260101-000000-000.zip\",\"size\":\"12\",\"modifiedTime\":\"2026-01-01T00:00:00Z\"}]}")
            : TestHttp.Json("{\"id\":\"f1\"}"));
        var drive = new GoogleDriveDestination(new StaticToken("tok-test"), new HttpClient(handler));
        using var dir = new TempDir();
        var file = dir.File("source.zip");
        File.WriteAllText(file, "x");

        var listed = await drive.ListAsync();
        var entry = Assert.Single(listed);
        Assert.Equal(12, entry.SizeBytes);

        await drive.UploadAsync(file, entry.Name);

        Assert.All(handler.Sent, s => Assert.Equal("Bearer tok-test", s.Authorization));
        var upload = handler.Sent[^1];
        Assert.Equal("PATCH", upload.Method);
        Assert.Equal("multipart/related", upload.ContentType);
    }

    [Fact]
    public async Task Dropbox_missing_folder_lists_as_empty_and_files_are_parsed()
    {
        var missing = new MockHttpHandler(_ => TestHttp.Json(
            "{\"error_summary\":\"path/not_found/..\"}", HttpStatusCode.Conflict));
        Assert.Empty(await new DropboxDestination(new StaticToken("t"), new HttpClient(missing)).ListAsync());

        var present = new MockHttpHandler(_ => TestHttp.Json(
            "{\"entries\":[" +
            "{\".tag\":\"file\",\"name\":\"winidm-backup-20260101-000000-000.zip\",\"size\":30,\"server_modified\":\"2026-01-01T00:00:00Z\"}," +
            "{\".tag\":\"folder\",\"name\":\"subfolder\"}],\"has_more\":false}"));
        var listed = await new DropboxDestination(new StaticToken("tok-dbx"), new HttpClient(present)).ListAsync();

        var entry = Assert.Single(listed);
        Assert.Equal("winidm-backup-20260101-000000-000.zip", entry.Name);
        Assert.Equal(30, entry.SizeBytes);
        Assert.All(present.Sent, s => Assert.Equal("Bearer tok-dbx", s.Authorization));
    }

    private sealed class StaticToken : IOAuthTokenProvider
    {
        private readonly string _token;

        public StaticToken(string token) => _token = token;

        public Task<string> GetAccessTokenAsync(CancellationToken ct = default) => Task.FromResult(_token);
    }
}
