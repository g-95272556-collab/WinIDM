using System.Text.RegularExpressions;

namespace WinIDM.Browser;

/// <summary>Four User-Agent choices per site.</summary>
public enum UserAgentMode
{
    /// <summary>System UA (the WebView2 default).</summary>
    Default = 0,

    /// <summary>Windows 10 x64 desktop UA.</summary>
    Desktop = 1,

    /// <summary>Generic Android UA (no device model).</summary>
    CustomMobile = 2,

    /// <summary>Custom UA text supplied by the user.</summary>
    Custom = 3,
}

/// <summary>Client Hints that match the UA (sec-ch-ua*).</summary>
public sealed record ClientHints(string SecChUa, string SecChUaMobile, string SecChUaPlatform);

/// <summary>
/// UA mode and Client Hints mapping. The mobile UA uses the generic model "K" so the device
/// cannot be identified (privacy first). The app should pass the real Chrome version from WebView2.
/// </summary>
public static partial class UserAgentProfile
{
    /// <summary>Fixed version used when the app does not pass the real version.</summary>
    public const string FallbackChromeVersion = "149.0.0.0";

    private const int MaxCustomLength = 512;

    [GeneratedRegex(@"Chrome/(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex ChromeVersionPattern();

    public static string DesktopUserAgent(string? chromeVersion = null) =>
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/" +
        (chromeVersion ?? FallbackChromeVersion) + " Safari/537.36";

    public static string MobileUserAgent(string? chromeVersion = null) =>
        "Mozilla/5.0 (Linux; Android 10; K) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/" +
        (chromeVersion ?? FallbackChromeVersion) + " Mobile Safari/537.36";

    /// <summary>Determine the effective UA for one mode.</summary>
    public static string Resolve(UserAgentMode mode, string systemUserAgent, string? chromeVersion = null, string? customText = null)
    {
        switch (mode)
        {
            case UserAgentMode.Desktop:
                return DesktopUserAgent(chromeVersion);
            case UserAgentMode.CustomMobile:
                return MobileUserAgent(chromeVersion);
            case UserAgentMode.Custom:
                if (IsValidCustomUserAgent(customText)) return customText!;
                return string.IsNullOrWhiteSpace(systemUserAgent) ? DesktopUserAgent(chromeVersion) : systemUserAgent;
            default:
                return string.IsNullOrWhiteSpace(systemUserAgent) ? DesktopUserAgent(chromeVersion) : systemUserAgent;
        }
    }

    public static bool IsValidCustomUserAgent(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxCustomLength) return false;
        return !text.Any(char.IsControl);
    }

    /// <summary>True if the UA indicates a desktop (Windows NT, Macintosh, or X11).</summary>
    public static bool IsDesktopUserAgent(string? userAgent)
    {
        if (string.IsNullOrEmpty(userAgent)) return false;
        return userAgent.Contains("Windows NT", StringComparison.Ordinal)
               || userAgent.Contains("Macintosh", StringComparison.Ordinal)
               || userAgent.Contains("X11", StringComparison.Ordinal);
    }

    /// <summary>Build Client Hints from the UA so both stay consistent.</summary>
    public static ClientHints ClientHintsFor(string userAgent)
    {
        ArgumentNullException.ThrowIfNull(userAgent);

        string brands = "\"Not.A/Brand\";v=\"99\"";
        var match = ChromeVersionPattern().Match(userAgent);
        if (match.Success)
        {
            string major = match.Groups[1].Value;
            brands = $"\"Chromium\";v=\"{major}\", \"Google Chrome\";v=\"{major}\", " + brands;
        }

        bool mobile = userAgent.Contains("Mobile", StringComparison.Ordinal) || userAgent.Contains("Android", StringComparison.Ordinal);

        string platform =
            userAgent.Contains("Android", StringComparison.Ordinal) ? "\"Android\"" :
            userAgent.Contains("Windows NT", StringComparison.Ordinal) ? "\"Windows\"" :
            userAgent.Contains("Macintosh", StringComparison.Ordinal) ? "\"macOS\"" :
            userAgent.Contains("Linux", StringComparison.Ordinal) || userAgent.Contains("X11", StringComparison.Ordinal) ? "\"Linux\"" :
            "\"Unknown\"";

        return new ClientHints(brands, mobile ? "?1" : "?0", platform);
    }
}
