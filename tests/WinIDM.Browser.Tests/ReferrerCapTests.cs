namespace WinIDM.Browser.Tests;

public class ReferrerCapTests
{
    [Theory]
    [InlineData("https://news.example.com/story/42?id=7", "https://ads.tracker.net/p.gif", "https://news.example.com/")]
    [InlineData("http://shop.example.co.uk:8080/cart", "https://cdn.other.com/a.js", "http://shop.example.co.uk:8080/")]
    public void Cap_CrossSite_KeepsOnlyTheOrigin(string referer, string request, string expected) =>
        Assert.Equal(expected, ReferrerCap.Cap(referer, request));

    [Theory]
    [InlineData("https://www.example.com/page", "https://static.example.com/a.js")] // same site
    [InlineData("https://news.example.com/", "https://ads.tracker.net/p.gif")]     // already an origin
    [InlineData("", "https://ads.tracker.net/p.gif")]                             // no referrer sent
    public void Cap_NothingToChange_ReturnsNull(string referer, string request) =>
        Assert.Null(ReferrerCap.Cap(referer, request));
}
