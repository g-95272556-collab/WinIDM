using System.Net;
using WinIDM.Backup;

namespace WinIDM.Backup.Tests;

public class CloudAuthAndRetryTests
{
    private const string Key = "googledrive";

    private static GoogleDriveOptions Options(FakeDriveServer server, FakeClock clock, RetryPolicy? retry = null) => new()
    {
        ApiBase = new Uri(server.BaseUri, "drive/v3/"),
        UploadBase = new Uri(server.BaseUri, "upload/drive/v3/"),
        Cloud = new CloudOptions { Clock = clock, Retry = retry ?? RetryPolicy.Standard },
    };

    private static LoopbackPkceAuthorizer Authorizer(
        FakeClock clock, InMemoryTokenStore store, MockHttpHandler tokenHandler,
        Func<Uri, CancellationToken, Task>? openBrowser = null) =>
        new(OAuthClientOptions.ForGoogleDrive("cid-test"), new HttpClient(tokenHandler),
            openBrowser ?? ((_, _) => Task.CompletedTask),
            clock, tokenStore: store, tokenKey: Key, allowInteractiveFallback: false);

    private static MockHttpHandler Tokens(string access = "at-2") =>
        new(_ => TestHttp.Json($"{{\"access_token\":\"{access}\",\"expires_in\":3600}}"));

    private static InMemoryTokenStore StoreWith(FakeClock clock, string access, string? refresh, bool expired)
    {
        var store = new InMemoryTokenStore();
        store.Save(Key, new OAuthToken(access, refresh,
            expired ? clock.UtcNow.AddMinutes(-5) : clock.UtcNow.AddHours(1)));
        return store;
    }

    [Fact]
    public async Task Expired_token_is_refreshed_before_the_request_and_the_new_token_is_saved()
    {
        var clock = new FakeClock(CloudTest.Start);
        var store = StoreWith(clock, "at-old", "rt-1", expired: true);
        var tokens = Tokens("at-2");
        var auth = Authorizer(clock, store, tokens);
        using var server = new FakeDriveServer { AcceptedAccessToken = "at-2" };
        var drive = new GoogleDriveDestination(auth, CloudTest.NewHttp(), Options(server, clock));

        await drive.ListAsync();

        var refresh = Assert.Single(tokens.Sent);
        Assert.Contains("grant_type=refresh_token", refresh.Body, StringComparison.Ordinal);
        Assert.Equal(new[] { "Bearer at-2" }, server.Requests.Select(r => r.Header("Authorization")).Distinct());
        var saved = store.Load(Key)!;
        Assert.Equal("at-2", saved.AccessToken);
        Assert.Equal("rt-1", saved.RefreshToken);
    }

    [Fact]
    public async Task Token_rejected_with_401_triggers_one_refresh_and_one_retry()
    {
        var clock = new FakeClock(CloudTest.Start);
        var store = StoreWith(clock, "at-1", "rt-1", expired: false);
        var tokens = Tokens("at-2");
        var auth = Authorizer(clock, store, tokens);
        using var server = new FakeDriveServer { AcceptedAccessToken = "at-2" };
        var drive = new GoogleDriveDestination(auth, CloudTest.NewHttp(), Options(server, clock));

        await drive.ListAsync();

        Assert.Equal(new[] { "Bearer at-1", "Bearer at-2" }, server.Requests.Select(r => r.Header("Authorization")));
        Assert.Single(tokens.Sent);
    }

    [Fact]
    public async Task Token_still_rejected_after_refresh_fails_with_clear_message_and_no_secrets()
    {
        var clock = new FakeClock(CloudTest.Start);
        var store = StoreWith(clock, "at-1", "rt-1", expired: false);
        var tokens = Tokens("at-2");
        var auth = Authorizer(clock, store, tokens);
        using var server = new FakeDriveServer { AcceptedAccessToken = "at-rejected" };
        var drive = new GoogleDriveDestination(auth, CloudTest.NewHttp(), Options(server, clock));

        var ex = await Assert.ThrowsAsync<BackupTransportException>(() => drive.ListAsync());

        Assert.Equal(401, ex.StatusCode);
        Assert.Contains("Reconnect", ex.Message, StringComparison.Ordinal);
        Assert.Single(tokens.Sent);
        AssertNoSecrets(ex, "at-1", "at-2", "rt-1");
    }

    [Fact]
    public async Task Retry_after_header_on_429_is_honoured_before_retrying()
    {
        var clock = new FakeClock(CloudTest.Start);
        var auth = new FixedToken();
        using var server = new FakeDriveServer();
        server.AddFault(new FakeFault(r => r.Method == "GET", FaultKind.Respond, times: 1,
            response: FakeResponse.Json(429, "{\"error\":{\"code\":429}}", FakeResponse.Header("Retry-After", "3"))));
        var drive = new GoogleDriveDestination(auth, CloudTest.NewHttp(), Options(server, clock));

        var start = clock.UtcNow;
        await drive.ListAsync();

        Assert.Equal(TimeSpan.FromSeconds(3), clock.UtcNow - start);
        Assert.Equal(2, server.Requests.Count(r => r.Method == "GET"));
    }

    [Fact]
    public async Task Server_errors_back_off_exponentially_and_stop_at_max_attempts()
    {
        var clock = new FakeClock(CloudTest.Start);
        using var server = new FakeDriveServer();
        server.AddFault(new FakeFault(_ => true, FaultKind.Respond, times: 10,
            response: FakeResponse.Json(503, "{}")));
        var retry = new RetryPolicy { MaxAttempts = 3, BaseDelay = TimeSpan.FromSeconds(1), MaxDelay = TimeSpan.FromSeconds(30) };
        var drive = new GoogleDriveDestination(new FixedToken(), CloudTest.NewHttp(), Options(server, clock, retry));

        var start = clock.UtcNow;
        var ex = await Assert.ThrowsAsync<BackupTransportException>(() => drive.ListAsync());

        Assert.Equal(503, ex.StatusCode);
        Assert.Equal(3, server.Requests.Count);
        Assert.Equal(TimeSpan.FromSeconds(3), clock.UtcNow - start); // 1 s, then 2 s
    }

    [Fact]
    public async Task Transport_failures_are_retried_then_reported_without_status()
    {
        var clock = new FakeClock(CloudTest.Start);
        using var server = new FakeDriveServer();
        server.AddFault(new FakeFault(_ => true, FaultKind.AbortBeforeApply, times: 100));
        var drive = new GoogleDriveDestination(new FixedToken(), CloudTest.NewHttp(), Options(server, clock));

        var start = clock.UtcNow;
        var ex = await Assert.ThrowsAsync<BackupTransportException>(() => drive.ListAsync());

        Assert.Null(ex.StatusCode);
        // HttpClient may retry on its own after a broken connection; at least MaxAttempts happen.
        Assert.True(server.Requests.Count >= RetryPolicy.Standard.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(1 + 2 + 4 + 8), clock.UtcNow - start);
    }

    [Fact]
    public async Task Failed_refresh_without_interaction_never_opens_the_browser()
    {
        var clock = new FakeClock(CloudTest.Start);
        var store = StoreWith(clock, "at-old", "rt-dead", expired: true);
        var tokens = new MockHttpHandler(_ => TestHttp.Json("{\"error\":\"invalid_grant\"}", HttpStatusCode.BadRequest));
        var browserOpens = 0;
        var auth = Authorizer(clock, store, tokens, (_, _) =>
        {
            browserOpens++;
            return Task.CompletedTask;
        });
        using var server = new FakeDriveServer();
        var drive = new GoogleDriveDestination(auth, CloudTest.NewHttp(), Options(server, clock));

        var ex = await Assert.ThrowsAsync<BackupTransportException>(() => drive.ListAsync());

        Assert.Contains("Reconnect", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, browserOpens);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public void Token_saved_in_store_is_loaded_when_authorizer_is_created()
    {
        var clock = new FakeClock(CloudTest.Start);
        var store = StoreWith(clock, "at-stored", "rt-1", expired: false);

        var auth = Authorizer(clock, store, Tokens());

        Assert.Equal("at-stored", auth.CurrentToken!.AccessToken);
    }

    [Fact]
    public async Task Cancelled_token_stops_request_before_it_is_sent()
    {
        var clock = new FakeClock(CloudTest.Start);
        using var server = new FakeDriveServer();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var drive = new GoogleDriveDestination(new FixedToken(), CloudTest.NewHttp(), Options(server, clock));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => drive.ListAsync(cts.Token));
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Server_error_body_and_token_values_never_appear_in_exception_text()
    {
        var clock = new FakeClock(CloudTest.Start);
        var store = StoreWith(clock, "at-secret-1", "rt-secret-2", expired: false);
        using var server = new FakeDriveServer { AcceptedAccessToken = null };
        server.AddFault(new FakeFault(_ => true, FaultKind.Respond, times: 100,
            response: FakeResponse.Json(500, "{\"error\":{\"message\":\"internal failure\"}}")));
        var auth = Authorizer(clock, store, Tokens());
        var drive = new GoogleDriveDestination(auth, CloudTest.NewHttp(), Options(server, clock));

        var ex = await Assert.ThrowsAsync<BackupTransportException>(() => drive.ListAsync());

        Assert.Equal(500, ex.StatusCode);
        AssertNoSecrets(ex, "at-secret-1", "rt-secret-2");
    }

    [Fact]
    public void OAuth_token_text_representation_is_redacted()
    {
        var token = new OAuthToken("at-secret", "rt-secret", DateTimeOffset.UnixEpoch);

        var text = token.ToString();

        Assert.DoesNotContain("at-secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("rt-secret", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Retry_policy_delay_doubles_and_is_capped()
    {
        var policy = new RetryPolicy { BaseDelay = TimeSpan.FromSeconds(1), MaxDelay = TimeSpan.FromSeconds(5) };

        Assert.Equal(TimeSpan.FromSeconds(1), policy.ExponentialDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(2), policy.ExponentialDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(4), policy.ExponentialDelay(3));
        Assert.Equal(TimeSpan.FromSeconds(5), policy.ExponentialDelay(4));
        Assert.Equal(TimeSpan.FromSeconds(5), policy.ExponentialDelay(60));
    }

    private static void AssertNoSecrets(Exception ex, params string[] secrets)
    {
        var text = ex.ToString() + ex.Message;
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        }
    }
}
