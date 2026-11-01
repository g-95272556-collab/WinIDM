namespace WinIDM.Browser.Tests;

public class TrackingParamsTests
{
    [Theory]
    [InlineData("https://ex.com/a?utm_source=x&id=5&fbclid=abc", "https://ex.com/a?id=5")]
    [InlineData("https://ex.com/a?gclid=1#top", "https://ex.com/a#top")]
    [InlineData("https://www.youtube.com/watch?v=abc&si=xyz", "https://www.youtube.com/watch?v=abc")]
    [InlineData("https://ex.com:8443/p?q=a%20b&UTM_Medium=m", "https://ex.com:8443/p?q=a%20b")]
    public void Strip_RemovesTrackingAndKeepsTheRest(string input, string expected) =>
        Assert.Equal(expected, TrackingParams.Strip(input));

    [Theory]
    [InlineData("https://ex.com/a?id=5")]
    [InlineData("https://ex.com/a")]
    [InlineData("https://ex.com/search?si=1")] // "si" is tracking only on YouTube/Spotify
    [InlineData("ftp://ex.com/a?utm_source=x")]
    public void Strip_NothingToChange_ReturnsNull(string input) => Assert.Null(TrackingParams.Strip(input));

    [Theory]
    [InlineData("https://bucket.s3.amazonaws.com/v.mp4?utm_source=x&X-Amz-Signature=abc&X-Amz-Expires=60")]
    [InlineData("https://cdn.ex.com/master.m3u8?utm_source=x&token=abc")]
    [InlineData("https://d1.cloudfront.net/v.mp4?fbclid=1&Expires=1&Signature=a&Key-Pair-Id=k")]
    public void Strip_SignedMediaUrl_IsNeverTouched(string input) => Assert.Null(TrackingParams.Strip(input));
}
