using System.Net;
using System.Text;
using WinIDM.Core.Models;
using WinIDM.Engine;
using WinIDM.Engine.Tests.Support;

namespace WinIDM.Engine.Tests;

/// <summary>
/// SOCKS5/HTTP proxies with auth, a custom host resolver (DoH/DoT hook), and DPAPI secret protection.
/// All servers are 127.0.0.1 or imaginary host names resolved locally (.invalid).
/// </summary>
public sealed class TransportAndSecretsTests
{
    private const int Mib = 1024 * 1024;

    private static string Sha(byte[] data) => TestData.Sha256Hex(data);

    private static string FinalPath(DownloadItem item) => Path.Combine(item.SaveDirectory, item.FileName);

    private static DownloadRequest Request(string url) => new() { Url = url };

    // ------------------------------------------------------------------
    // SOCKS5
    // ------------------------------------------------------------------

    [Fact]
    public async Task Socks5_WithoutAuth_RoutesDownloadThroughProxy()
    {
        await using var socks = new Socks5TestServer();
        await using var server = new LocalHttpServer();
        var file = new VirtualFile(TestData.Random(1 * Mib, seed: 41));
        server.Handler = file.Serve;

        await using var h = new EngineHarness(s =>
        {
            s.Set(SettingKeys.ProxyEnabled, true);
            s.Set(SettingKeys.ProxyType, ProxyTypes.Socks5);
            s.Set(SettingKeys.ProxyHost, "127.0.0.1");
            s.Set(SettingKeys.ProxyPort, socks.Port);
        });

        var id = await h.Engine.AddAsync(Request($"http://example.invalid:{server.Port}/file.bin"));
        var item = await h.WaitForStateAsync(id, DownloadState.Completed);

        Assert.Equal(Sha(file.Content), TestData.Sha256Hex(FinalPath(item)));
        Assert.Contains($"example.invalid:{server.Port}", socks.Connects);
    }

    [Fact]
    public async Task Socks5_WithUsernameAndPassword_Succeeds_WhenCredentialsMatch()
    {
        await using var socks = new Socks5TestServer("ali", "test-password");
        await using var server = new LocalHttpServer();
        var file = new VirtualFile(TestData.Random(512 * 1024, seed: 42));
        server.Handler = file.Serve;

        await using var h = new EngineHarness(s =>
        {
            s.Set(SettingKeys.ProxyEnabled, true);
            s.Set(SettingKeys.ProxyType, ProxyTypes.Socks5);
            s.Set(SettingKeys.ProxyHost, "127.0.0.1");
            s.Set(SettingKeys.ProxyPort, socks.Port);
            s.Set(SettingKeys.ProxyUser, "ali");
            s.Set(SettingKeys.ProxyPass, "test-password");
        });

        var id = await h.Engine.AddAsync(Request($"http://example.invalid:{server.Port}/file.bin"));
        var item = await h.WaitForStateAsync(id, DownloadState.Completed);

        Assert.Equal(Sha(file.Content), TestData.Sha256Hex(FinalPath(item)));
        Assert.Equal(0, socks.AuthFailures);
    }

    [Fact]
    public async Task Socks5_WrongPassword_FailsWithConnectionError_AndNoData()
    {
        await using var socks = new Socks5TestServer("ali", "test-password");
        await using var server = new LocalHttpServer();
        server.Handler = _ => new ServerResponse { Status = 200, Body = [1, 2, 3] };

        await using var h = new EngineHarness(s =>
        {
            s.Set(SettingKeys.Retry, false);
            s.Set(SettingKeys.ProxyEnabled, true);
            s.Set(SettingKeys.ProxyType, ProxyTypes.Socks5);
            s.Set(SettingKeys.ProxyHost, "127.0.0.1");
            s.Set(SettingKeys.ProxyPort, socks.Port);
            s.Set(SettingKeys.ProxyUser, "ali");
            s.Set(SettingKeys.ProxyPass, "wrong");
        });

        var id = await h.Engine.AddAsync(Request($"http://example.invalid:{server.Port}/file.bin"));
        var item = await h.WaitForStateAsync(id, DownloadState.Error);

        Assert.Equal(EngineErrorCodes.ConnectionError, item.ErrorCode);
        Assert.True(socks.AuthFailures >= 1);
        Assert.Empty(server.Requests);
    }

    // ------------------------------------------------------------------
    // HTTP proxy with auth
    // ------------------------------------------------------------------

    [Fact]
    public async Task HttpProxy_WithCredentials_AnswersChallengeAndDownloads()
    {
        await using var proxy = new LocalHttpServer();
        var file = new VirtualFile(TestData.Random(256 * 1024, seed: 43));
        var expected = "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("ali:pass"));
        proxy.Handler = req =>
        {
            if (req.Get("Proxy-Authorization") != expected)
            {
                return new ServerResponse { Status = 407, Reason = "Proxy Authentication Required" }
                    .WithHeader("Proxy-Authenticate", "Basic realm=\"test\"");
            }

            return req.Target.StartsWith("http://example.invalid/", StringComparison.Ordinal)
                ? file.Serve(req)
                : new ServerResponse { Status = 502, Reason = "Bad Gateway" };
        };

        await using var h = new EngineHarness(s =>
        {
            s.Set(SettingKeys.ProxyEnabled, true);
            s.Set(SettingKeys.ProxyType, ProxyTypes.Http);
            s.Set(SettingKeys.ProxyHost, "127.0.0.1");
            s.Set(SettingKeys.ProxyPort, proxy.Port);
            s.Set(SettingKeys.ProxyUser, "ali");
            s.Set(SettingKeys.ProxyPass, "pass");
        });

        var id = await h.Engine.AddAsync(Request("http://example.invalid/file.bin"));
        var item = await h.WaitForStateAsync(id, DownloadState.Completed);

        Assert.Equal(Sha(file.Content), TestData.Sha256Hex(FinalPath(item)));
        Assert.Contains(proxy.Requests, r => r.Get("Proxy-Authorization") == expected);
    }

    [Fact]
    public void ProxyType_Unknown_IsInvalidProxy()
    {
        var store = new JsonSettingsStore(Path.Combine(Path.GetTempPath(), "winidm-tests", Guid.NewGuid().ToString("N") + ".json"));
        store.Set(SettingKeys.ProxyType, 7);
        store.Set(SettingKeys.ProxyHost, "127.0.0.1");
        store.Set(SettingKeys.ProxyPort, 8080);

        var ex = Assert.Throws<TransferException>(() => EngineTransport.BuildProxy(store));
        Assert.Equal(EngineErrorCodes.InvalidProxy, ex.Code);
    }

    [Fact]
    public void Socks5Proxy_BuildsSocks5Uri_WithHostAndPort()
    {
        var store = new JsonSettingsStore(Path.Combine(Path.GetTempPath(), "winidm-tests", Guid.NewGuid().ToString("N") + ".json"));
        store.Set(SettingKeys.ProxyType, ProxyTypes.Socks5);
        store.Set(SettingKeys.ProxyHost, "127.0.0.1");
        store.Set(SettingKeys.ProxyPort, 1080);

        var proxy = EngineTransport.BuildProxy(store);
        var target = new Uri("http://example.invalid/");
        Assert.Equal("socks5", proxy.GetProxy(target)!.Scheme);
        Assert.Equal(1080, proxy.GetProxy(target)!.Port);
    }

    // ------------------------------------------------------------------
    // Custom host resolver (DoH/DoT hook)
    // ------------------------------------------------------------------

    [Fact]
    public async Task HostResolver_IsUsedToReachTargetHost_WithoutSystemDns()
    {
        await using var server = new LocalHttpServer();
        var file = new VirtualFile(TestData.Random(300 * 1024, seed: 44));
        server.Handler = file.Serve;

        await using var h = new EngineHarness();
        var asked = new List<string>();
        h.Engine.HostResolver = (host, _) =>
        {
            asked.Add(host);
            return Task.FromResult(host == "example.invalid" ? new[] { IPAddress.Loopback } : Array.Empty<IPAddress>());
        };

        var id = await h.Engine.AddAsync(Request($"http://example.invalid:{server.Port}/file.bin"));
        var item = await h.WaitForStateAsync(id, DownloadState.Completed);

        Assert.Equal(Sha(file.Content), TestData.Sha256Hex(FinalPath(item)));
        Assert.Contains("example.invalid", asked);
    }

    [Fact]
    public async Task HostResolver_IsUsedForHttpProxyHost()
    {
        await using var proxy = new LocalHttpServer();
        var file = new VirtualFile(TestData.Random(128 * 1024, seed: 45));
        proxy.Handler = req => req.Target.StartsWith("http://example.invalid/", StringComparison.Ordinal)
            ? file.Serve(req)
            : new ServerResponse { Status = 502, Reason = "Bad Gateway" };

        await using var h = new EngineHarness(s =>
        {
            s.Set(SettingKeys.ProxyEnabled, true);
            s.Set(SettingKeys.ProxyType, ProxyTypes.Http);
            s.Set(SettingKeys.ProxyHost, "proxy.invalid");
            s.Set(SettingKeys.ProxyPort, proxy.Port);
        });

        var asked = new List<string>();
        h.Engine.HostResolver = (host, _) =>
        {
            asked.Add(host);
            return Task.FromResult(host == "proxy.invalid" ? new[] { IPAddress.Loopback } : Array.Empty<IPAddress>());
        };

        var id = await h.Engine.AddAsync(Request("http://example.invalid/file.bin"));
        var item = await h.WaitForStateAsync(id, DownloadState.Completed);

        Assert.Equal(Sha(file.Content), TestData.Sha256Hex(FinalPath(item)));
        Assert.Contains("proxy.invalid", asked);
    }

    // ------------------------------------------------------------------
    // Secret protection (DPAPI) in JsonSettingsStore
    // ------------------------------------------------------------------

    private static string TempSettingsPath(out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), "winidm-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "settings.json");
    }

    [Fact]
    public void SecretSetting_IsEncryptedOnDisk_AndReadBackTransparently()
    {
        var path = TempSettingsPath(out var dir);
        try
        {
            var store = new JsonSettingsStore(path);
            store.Set(SettingKeys.ProxyPass, "unique-secret-XYZ-4711");

            var onDisk = File.ReadAllText(path);
            Assert.DoesNotContain("unique-secret-XYZ-4711", onDisk);
            Assert.Contains("dpapi1:", onDisk);

            var reopened = new JsonSettingsStore(path);
            Assert.Equal("unique-secret-XYZ-4711", reopened.Get(SettingKeys.ProxyPass, string.Empty));
            Assert.Equal("unique-secret-XYZ-4711", reopened.Snapshot()[SettingKeys.ProxyPass]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void NonSecretSetting_RemainsPlaintextOnDisk()
    {
        var path = TempSettingsPath(out var dir);
        try
        {
            var store = new JsonSettingsStore(path);
            store.Set(SettingKeys.ProxyHost, "proxy.example");

            Assert.Contains("proxy.example", File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LegacyPlaintextSecret_IsEncryptedOnNextSave()
    {
        var path = TempSettingsPath(out var dir);
        try
        {
            File.WriteAllText(path, "{\"idm_pref_proxy_pass\": \"old-plaintext\"}");

            var store = new JsonSettingsStore(path);
            Assert.Equal("old-plaintext", store.Get(SettingKeys.ProxyPass, string.Empty));

            store.Set(SettingKeys.ProxyHost, "x");

            var onDisk = File.ReadAllText(path);
            Assert.DoesNotContain("old-plaintext", onDisk);
            Assert.Equal("old-plaintext", new JsonSettingsStore(path).Get(SettingKeys.ProxyPass, string.Empty));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void UndecryptableSecret_IsNotReadButIsPreservedOnDisk()
    {
        var path = TempSettingsPath(out var dir);
        try
        {
            // A blob created for another Windows user: it cannot be decrypted here.
            File.WriteAllText(path, "{\"idm_pref_proxy_pass\": \"dpapi1:AAAA\"}");

            var store = new JsonSettingsStore(path);
            Assert.Equal("empty", store.Get(SettingKeys.ProxyPass, "empty"));

            store.Set(SettingKeys.ProxyHost, "x");
            Assert.Contains("dpapi1:AAAA", File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void IsSecret_CoversPasswordsAndTokens()
    {
        Assert.True(SettingKeys.IsSecret(SettingKeys.ProxyPass));
        Assert.True(SettingKeys.IsSecret("winidm_api_token"));
        Assert.True(SettingKeys.IsSecret("winidm_site_password"));
        Assert.False(SettingKeys.IsSecret(SettingKeys.ProxyUser));
        Assert.False(SettingKeys.IsSecret(SettingKeys.ProxyHost));
    }
}

/// <summary>Small test helper for server responses.</summary>
internal static class ServerResponseTestExtensions
{
    public static ServerResponse WithHeader(this ServerResponse response, string name, string value)
    {
        response.Headers[name] = value;
        return response;
    }
}
