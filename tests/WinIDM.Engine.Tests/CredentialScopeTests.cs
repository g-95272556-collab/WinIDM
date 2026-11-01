using System.Net.Http;
using WinIDM.Core.Models;

namespace WinIDM.Engine.Tests;

/// <summary>Cookies and credentials stay on the site the user added; redirects and segments elsewhere do not get them.</summary>
public class CredentialScopeTests
{
    private static DownloadItem Item(string url) => new()
    {
        Url = url,
        CookieHeader = "sid=abc",
        Username = "ali",
        Password = "secret",
        Referer = "https://www.example.com/watch",
        Headers = new Dictionary<string, string> { ["Origin"] = "https://www.example.com", ["X-Test"] = "yes" },
    };

    private static HttpRequestMessage Apply(string requestUrl, DownloadItem item)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
        HttpTransfer.ApplyItemHeaders(request, item, userAgentSetting: null);
        return request;
    }

    [Theory]
    [InlineData("https://www.example.com/a.mp4", "https://www.example.com/b.mp4", true)]
    [InlineData("https://www.example.com/a.mp4", "https://cdn.example.com/b.mp4", true)]
    [InlineData("https://www.bbc.co.uk/a.mp4", "https://media.bbc.co.uk/b.mp4", true)]
    [InlineData("https://www.bbc.co.uk/a.mp4", "https://evil.co.uk/b.mp4", false)]
    [InlineData("https://www.example.com/a.mp4", "https://other.test/b.mp4", false)]
    [InlineData("http://127.0.0.1:5000/a.mp4", "http://127.0.0.1:6000/b.mp4", true)]
    [InlineData("http://127.0.0.1/a.mp4", "http://127.0.0.2/b.mp4", false)]
    public void IsSameSite_ComparesRegistrableDomain(string original, string target, bool expected) =>
        Assert.Equal(expected, HttpTransfer.IsSameSite(new Uri(target), original));

    [Fact]
    public void SameSite_ReceivesCookieAndAuthorization()
    {
        using var request = Apply("https://cdn.example.com/seg1.ts", Item("https://www.example.com/master.m3u8"));

        Assert.Equal("sid=abc", string.Join("", request.Headers.GetValues("Cookie")));
        Assert.True(request.Headers.Contains("Authorization"));
        Assert.True(request.Headers.Contains("X-Test"));
    }

    [Fact]
    public void OtherSite_DoesNotReceiveCookieOrAuthorization_ButKeepsOtherHeaders()
    {
        var item = Item("https://www.example.com/video.mp4");
        item.Headers["Cookie"] = "extra=1";
        item.Headers["Authorization"] = "Bearer token";

        using var request = Apply("https://redirected.other.test/video.mp4", item);

        Assert.False(request.Headers.Contains("Cookie"));
        Assert.False(request.Headers.Contains("Authorization"));
        // Referer/Origin and harmless custom headers still travel, as a browser would send them.
        Assert.True(request.Headers.Contains("Referer"));
        Assert.True(request.Headers.Contains("Origin"));
        Assert.True(request.Headers.Contains("X-Test"));
    }
}
