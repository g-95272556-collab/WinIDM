namespace WinIDM.Browser.Tests;

public class FingerprintShieldTests
{
    [Fact]
    public void BuildScript_EmbedsKeyAndExemptSitesAsJsonLiterals()
    {
        var js = FingerprintShield.BuildScript("k\"ey", new[] { "Example.COM", "example.com", "bank.my" });

        Assert.Contains("var KEY = \"k\\u0022ey\", EXEMPT = [\"example.com\",\"bank.my\"];", js);
        Assert.DoesNotContain("__KEY__", js);
        Assert.DoesNotContain("__EXEMPT__", js);
    }

    [Fact]
    public void BuildScript_DoesNotTouchMediaPlayback()
    {
        var js = FingerprintShield.BuildScript("k", Array.Empty<string>());

        foreach (var api in new[] { "HTMLMediaElement", "MediaSource", "requestMediaKeySystemAccess", "AudioContext.prototype", "fetch", "XMLHttpRequest" })
            Assert.DoesNotContain(api, js.Replace("OfflineAudioContext.prototype", "", StringComparison.Ordinal));
    }
}
