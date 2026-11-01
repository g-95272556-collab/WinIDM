using WinIDM.Engine;

namespace WinIDM.Engine.Tests;

/// <summary>URL patterns for IDM+ batch download.</summary>
public sealed class BatchPatternTests
{
    [Fact]
    public void NumberRange_ExpandsInOrder()
    {
        Assert.Equal(["https://x/1.jpg", "https://x/2.jpg", "https://x/3.jpg"], BatchPattern.Expand("https://x/[1-3].jpg"));
    }

    [Fact]
    public void LeadingZero_KeepsWidth()
    {
        var urls = BatchPattern.Expand("https://x/p[008-011].png");
        Assert.Equal(["https://x/p008.png", "https://x/p009.png", "https://x/p010.png", "https://x/p011.png"], urls);
    }

    [Fact]
    public void Letters_StepAndCountdown()
    {
        Assert.Equal(["a", "b", "c"], BatchPattern.Expand("[a-c]"));
        Assert.Equal(["0", "25", "50", "75", "100"], BatchPattern.Expand("[0-100:25]"));
        Assert.Equal(["3", "2", "1"], BatchPattern.Expand("[3-1]"));
    }

    [Fact]
    public void SeveralGroups_GiveEveryCombination()
    {
        Assert.Equal(["s1e1", "s1e2", "s2e1", "s2e2"], BatchPattern.Expand("s[1-2]e[1-2]"));
    }

    [Fact]
    public void NonRangeBrackets_StayLiteral()
    {
        Assert.Equal(["http://[::1]:8080/f1", "http://[::1]:8080/f2"], BatchPattern.Expand("http://[::1]:8080/f[1-2]"));
        Assert.Equal(["https://x/plain.zip"], BatchPattern.Expand("https://x/plain.zip"));
    }

    [Fact]
    public void TooManyLinks_IsRejected()
    {
        Assert.Throws<FormatException>(() => BatchPattern.Expand("[1-200][1-200]"));
    }
}
