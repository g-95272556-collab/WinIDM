using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using WinIDM.Core.Models;
using WinIDM.Engine;
using WinIDM.Engine.Tests.Support;

namespace WinIDM.Engine.Tests;

/// <summary>
/// HTTPS proxy (TLS to the proxy, <see cref="ProxyTypes.HttpsTls"/>). .NET supports it through the <c>https://</c> scheme on
/// <see cref="System.Net.WebProxy"/>: a TLS connection is opened to the proxy and the proxy certificate is validated. All servers are on 127.0.0.1.
/// </summary>
public sealed class ProxyTlsTests
{
    private const int Mib = 1024 * 1024;

    [Fact]
    public void HttpsProxyType_BuildsHttpsProxyUri()
    {
        using var temp = new TempDirectory();
        var settings = new JsonSettingsStore(Path.Combine(temp.Path, "s.json"));
        settings.Set(SettingKeys.ProxyType, ProxyTypes.HttpsTls);
        settings.Set(SettingKeys.ProxyHost, "proxy.example.test");
        settings.Set(SettingKeys.ProxyPort, 8443);

        var proxy = EngineTransport.BuildProxy(settings);

        Assert.Equal("https", proxy.Address!.Scheme);
        Assert.Equal("proxy.example.test", proxy.Address.Host);
        Assert.Equal(8443, proxy.Address.Port);
    }

    [Fact]
    public void ProxyHostWithScheme_IsRejectedWith182()
    {
        using var temp = new TempDirectory();
        var settings = new JsonSettingsStore(Path.Combine(temp.Path, "s.json"));
        settings.Set(SettingKeys.ProxyType, ProxyTypes.HttpsTls);
        settings.Set(SettingKeys.ProxyHost, "https://127.0.0.1");
        settings.Set(SettingKeys.ProxyPort, 8443);

        var ex = Assert.Throws<TransferException>(() => EngineTransport.BuildProxy(settings));

        Assert.Equal(EngineErrorCodes.InvalidProxy, ex.Code);
        Assert.Contains("scheme", ex.Message);
    }

    [Fact]
    public async Task HttpsProxy_UntrustedCertificate_FailsWithConnectionError_BeforeAnyRequest()
    {
        await using var certificateHolder = new CertificateHolder();
        await using var proxy = new TlsForwardProxy(certificateHolder.Certificate);
        await using var h = new EngineHarness(s =>
        {
            ConfigureProxy(s, proxy.Port);
            s.Set(SettingKeys.Retry, false);
        });
        await using var server = new LocalHttpServer();
        server.Handler = new VirtualFile(TestData.Random(64 * 1024, seed: 71)).Serve;

        // No TlsCertificateCallback: the self-signed certificate is not trusted by the system certificate store.
        var id = await h.Engine.AddAsync(new DownloadRequest { Url = server.BaseUrl + "/file.bin", StartImmediately = true });
        var item = await h.WaitForStateAsync(id, DownloadState.Error);

        Assert.Equal(EngineErrorCodes.ConnectionError, item.ErrorCode);
        Assert.Empty(proxy.RequestLines);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task HttpsProxy_TrustedCertificate_CarriesDownloadThroughProxy()
    {
        await using var certificateHolder = new CertificateHolder();
        await using var proxy = new TlsForwardProxy(certificateHolder.Certificate);
        await using var h = new EngineHarness(s => ConfigureProxy(s, proxy.Port));
        await using var server = new LocalHttpServer();
        var content = TestData.Random(2 * Mib, seed: 72);
        server.Handler = new VirtualFile(content).Serve;

        // The test accepts only this test certificate (thumbprint match), not every certificate.
        var expected = certificateHolder.Certificate.Thumbprint;
        h.Engine.TlsCertificateCallback = (_, cert, _, _) =>
            cert is not null && new X509Certificate2(cert).Thumbprint == expected;

        var id = await h.Engine.AddAsync(new DownloadRequest { Url = server.BaseUrl + "/through-proxy.bin", StartImmediately = true });
        var item = await h.WaitForStateAsync(id, DownloadState.Completed, timeoutMs: 30000);

        Assert.Equal(TestData.Sha256Hex(content), TestData.Sha256Hex(Path.Combine(item.SaveDirectory, item.FileName)));
        Assert.NotEmpty(proxy.RequestLines);
        Assert.All(proxy.RequestLines, line => Assert.StartsWith("GET http://127.0.0.1:", line, StringComparison.Ordinal));
    }

    private static void ConfigureProxy(JsonSettingsStore settings, int port)
    {
        settings.Set(SettingKeys.ProxyEnabled, true);
        settings.Set(SettingKeys.ProxyType, ProxyTypes.HttpsTls);
        settings.Set(SettingKeys.ProxyHost, "127.0.0.1");
        settings.Set(SettingKeys.ProxyPort, port);
    }

    /// <summary>Single-use test certificate disposed after each test.</summary>
    private sealed class CertificateHolder : IAsyncDisposable
    {
        public CertificateHolder() => Certificate = TestCertificate.CreateSelfSigned();

        public X509Certificate2 Certificate { get; }

        public ValueTask DisposeAsync()
        {
            Certificate.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
