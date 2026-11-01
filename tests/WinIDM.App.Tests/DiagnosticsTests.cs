using System.IO;
using System.Net.Http;
using System.IO.Compression;
using System.Text;
using WinIDM.App.Services;

namespace WinIDM.App.Tests;

public class DiagnosticsTests
{
    [Fact]
    public void Redactor_RemovesUrlsSecretsAndProfilePath()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var text = $"download https://cdn.example.com/file.zip?token=abc123 password=secret {profile}\\Downloads\\x.zip";

        var result = DiagnosticsRedactor.Text(text);

        Assert.DoesNotContain("abc123", result);
        Assert.DoesNotContain("cdn.example.com", result);
        Assert.DoesNotContain("secret", result);
        Assert.DoesNotContain(profile, result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%USERPROFILE%", result);
    }

    [Theory]
    [InlineData("idm_pref_proxy_pass", true)]
    [InlineData("backup.google_secret", true)]
    [InlineData("backup.rest_token_secret", true)]
    [InlineData("backup.schedule_password_secret", true)]
    [InlineData("idm_pref_proxy_user", true)]
    [InlineData("backup.google_client_id", true)]
    [InlineData("idm_pref_download_count", false)]
    [InlineData("idm_pref_user_agent", false)]
    [InlineData("app.theme.custom", false)]
    public void SensitiveKeys_AreExcludedFromDiagnostics(string key, bool sensitive)
    {
        Assert.Equal(sensitive, DiagnosticsRedactor.IsSensitiveKey(key));
    }

    [Fact]
    public void Export_ContainsNoSecretsOrUrlsAndKeepsNormalSettings()
    {
        var logDir = Path.Combine(Path.GetTempPath(), "winidm-diag-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logDir);
        try
        {
            File.WriteAllText(Path.Combine(logDir, "app-20260101.log"),
                "2026-01-01 10:00:00 ERROR Download: https://files.example/a.bin?sig=SECRETSIG\n" +
                "2026-01-01 10:01:00 INFO password=DONOTLEAK\n", Encoding.UTF8);

            var settings = new Dictionary<string, string>
            {
                ["idm_pref_proxy_pass"] = "PROXYPASS-XYZ",
                ["backup.google_secret"] = "GOOGLESECRET-XYZ",
                ["idm_pref_download_count"] = "5",
            };

            var entries = DiagnosticsExporter.BuildEntries(settings, logDir, "1.2.3", DateTimeOffset.UtcNow);
            var all = string.Join("\n", entries.Select(e => Encoding.UTF8.GetString(e.Content)));

            Assert.DoesNotContain("PROXYPASS-XYZ", all);
            Assert.DoesNotContain("GOOGLESECRET-XYZ", all);
            Assert.DoesNotContain("SECRETSIG", all);
            Assert.DoesNotContain("DONOTLEAK", all);
            Assert.DoesNotContain("files.example", all);
            Assert.Contains("idm_pref_download_count", all);
            Assert.Contains("logs/app-20260101.log", entries.Select(e => e.Name));
        }
        finally
        {
            Directory.Delete(logDir, recursive: true);
        }
    }

    [Fact]
    public void WriteZip_ProducesReadableArchive()
    {
        var path = Path.Combine(Path.GetTempPath(), "winidm-diag-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            DiagnosticsExporter.WriteZip(path, new[] { ("README.txt", Encoding.UTF8.GetBytes("ok")) });
            using var zip = ZipFile.OpenRead(path);
            Assert.Single(zip.Entries);
            Assert.Equal("README.txt", zip.Entries[0].FullName);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task UpdateCheck_WithoutConfiguredUrl_MakesNoRequest()
    {
        using var http = new HttpClient(new ThrowingHandler());
        var result = await UpdateChecker.CheckAsync(http, "", new Version(1, 0, 0));
        Assert.False(result.Configured);
        Assert.False(result.UpdateAvailable);
    }

    [Fact]
    public async Task UpdateCheck_RejectsNonHttpsUrl()
    {
        using var http = new HttpClient(new ThrowingHandler());
        var result = await UpdateChecker.CheckAsync(http, "http://example.test/version.json", new Version(1, 0, 0));
        Assert.True(result.Configured);
        Assert.False(result.UpdateAvailable);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No network requests are allowed in this test.");
    }
}
