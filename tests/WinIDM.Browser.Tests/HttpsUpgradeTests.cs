namespace WinIDM.Browser.Tests;

public class HttpsUpgradeTests
{
    [Theory]
    [InlineData("http://example.com/a?b=1#c", "https://example.com/a?b=1#c")]
    [InlineData("http://www.Example.com:80/", "https://www.example.com/")]
    public void TryUpgrade_PublicHost_BecomesHttps(string input, string expected) =>
        Assert.Equal(expected, HttpsUpgrade.TryUpgrade(input, new HashSet<string>()));

    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("http://localhost/")]
    [InlineData("http://127.0.0.1/v.mp4")]
    [InlineData("http://192.168.1.10/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://router/")]
    [InlineData("http://nas.local/")]
    [InlineData("http://abcdefghijklmnop.onion/")]
    [InlineData("http://example.com:8080/")]
    public void TryUpgrade_NotApplicable_ReturnsNull(string input) =>
        Assert.Null(HttpsUpgrade.TryUpgrade(input, new HashSet<string>()));

    [Fact]
    public void TryUpgrade_HostThatFailedBefore_StaysHttp() =>
        Assert.Null(HttpsUpgrade.TryUpgrade("http://old.example.org/x", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "old.example.org" }));

    [Fact]
    public void Downgrade_RestoresHttp() =>
        Assert.Equal("http://example.com/a?b=1", HttpsUpgrade.Downgrade("https://example.com/a?b=1"));
}
