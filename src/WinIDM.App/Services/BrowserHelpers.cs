using System.Runtime.InteropServices;
using System.Text.Json;
using WinIDM.Core.Contracts;
using WinIDM.Core.Models;

namespace WinIDM.App.Services;

/// <summary>Search engine for the address bar. Templates use {q} in place of the search words.</summary>
public static class SearchEngines
{
    private static readonly Dictionary<string, string> Templates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Google"] = "https://www.google.com/search?q={q}",
        ["Bing"] = "https://www.bing.com/search?q={q}",
        ["DuckDuckGo"] = "https://duckduckgo.com/?q={q}",
        ["Yahoo"] = "https://search.yahoo.com/search?p={q}",
        ["Brave"] = "https://search.brave.com/search?q={q}",
        ["Startpage"] = "https://www.startpage.com/do/search?q={q}",
        ["Baidu"] = "https://www.baidu.com/s?wd={q}",
        ["Yandex"] = "https://yandex.com/search/?text={q}",
    };

    public static string BuildSearchUrl(string engine, string custom, string query)
    {
        var template = Templates.TryGetValue(engine, out var t)
            ? t
            : (string.IsNullOrWhiteSpace(custom) ? Templates["Google"] : custom);
        return template.Replace("{q}", Uri.EscapeDataString(query));
    }

    /// <summary>
    /// Decide whether the input is a URL (opened directly) or a search query. External schemes are not handled here.
    /// </summary>
    public static string ResolveInput(string input, string engine, string custom)
    {
        var t = input.Trim();
        if (t.Length == 0) return "";
        if (Uri.TryCreate(t, UriKind.Absolute, out var abs) && abs.Scheme is "http" or "https" or "about")
            return t;
        if (t.Contains(' ') || !t.Contains('.'))
            return BuildSearchUrl(engine, custom, t);
        return "https://" + t;
    }
}

/// <summary>Class group for browser functions that share state (saved tabs are a JSON list of URLs).</summary>
public static class SavedTabs
{
    public static List<string> Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch (JsonException)
        {
            return new List<string>();
        }
    }

    public static string Serialize(IEnumerable<string> urls) => JsonSerializer.Serialize(urls.ToList());
}

/// <summary>Maps WebView2 resource kinds to IAdBlocker filter kinds.</summary>
public static class ResourceTypeMap
{
    public static ResourceType From(Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext ctx) => ctx switch
    {
        Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.Document => ResourceType.Document,
        Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.Script => ResourceType.Script,
        Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.Image => ResourceType.Image,
        Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.Stylesheet => ResourceType.Stylesheet,
        Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.Media => ResourceType.Media,
        Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.XmlHttpRequest => ResourceType.XmlHttpRequest,
        Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.Fetch => ResourceType.XmlHttpRequest,
        _ => ResourceType.Other,
    };
}

/// <summary>Win32 helper for incognito windows (WDA_EXCLUDEFROMCAPTURE: blocks screen capture).</summary>
public static class WindowProtection
{
    private const uint WdaExcludeFromCapture = 0x11;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    public static void ExcludeFromCapture(IntPtr hwnd)
    {
        try { SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture); }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }
}
