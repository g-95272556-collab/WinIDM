namespace WinIDM.App.Services;

/// <summary>A search engine. <see cref="Template"/> uses {q}; empty means use the custom URL.</summary>
public sealed record BrowserSearchEngine(string Name, string Template);

/// <summary>
/// Complete search engine list (15 choices including custom). No third-party client identification
/// parameters are added to the URL; a search request carries only the search words.
/// </summary>
public static class BrowserSearchEngines
{
    public const string CustomName = "Custom";

    public static readonly IReadOnlyList<BrowserSearchEngine> All = new[]
    {
        new BrowserSearchEngine("Google", "https://www.google.com/search?q={q}"),
        new BrowserSearchEngine("Bing", "https://www.bing.com/search?q={q}"),
        new BrowserSearchEngine("DuckDuckGo", "https://duckduckgo.com/?q={q}"),
        new BrowserSearchEngine("DuckDuckGo Lite", "https://duckduckgo.com/lite/?q={q}"),
        new BrowserSearchEngine("Yahoo", "https://search.yahoo.com/search?p={q}"),
        new BrowserSearchEngine("Startpage", "https://www.startpage.com/do/search?q={q}"),
        new BrowserSearchEngine("Startpage Mobile", "https://www.startpage.com/do/m/mobilesearch?q={q}"),
        new BrowserSearchEngine("Baidu", "https://www.baidu.com/s?wd={q}"),
        new BrowserSearchEngine("Yandex.ru", "https://yandex.ru/yandsearch?text={q}"),
        new BrowserSearchEngine("Yahoo Japan", "https://search.yahoo.co.jp/search?p={q}"),
        new BrowserSearchEngine("AOL", "https://search.aol.com/aol/search?q={q}"),
        new BrowserSearchEngine("Brave", "https://search.brave.com/search?q={q}"),
        new BrowserSearchEngine("Yandex.com", "https://yandex.com/search/?text={q}"),
        new BrowserSearchEngine(CustomName, ""),
    };

    /// <summary>Build a search URL. An unknown engine or an empty custom value falls back to Google.</summary>
    public static string BuildSearchUrl(string engineName, string custom, string query)
    {
        var engine = All.FirstOrDefault(e => string.Equals(e.Name, engineName, StringComparison.OrdinalIgnoreCase));
        string template;
        if (engine is null || engine.Name == CustomName)
            template = IsTemplate(custom) ? custom : All[0].Template;
        else
            template = engine.Template;
        return template.Replace("{q}", Uri.EscapeDataString(query), StringComparison.Ordinal);
    }

    /// <summary>Decide whether the input is a URL (opened directly) or a search query.</summary>
    public static string ResolveInput(string input, string engineName, string custom)
    {
        var t = input.Trim();
        if (t.Length == 0) return "";
        if (Uri.TryCreate(t, UriKind.Absolute, out var abs) && abs.Scheme is "http" or "https" or "about" or "chrome-extension")
            return t;
        if (t.Contains(' ') || !t.Contains('.'))
            return BuildSearchUrl(engineName, custom, t);
        return "https://" + t;
    }

    private static bool IsTemplate(string? s) =>
        !string.IsNullOrWhiteSpace(s) && s.Contains("{q}", StringComparison.Ordinal)
        && Uri.TryCreate(s.Replace("{q}", "x", StringComparison.Ordinal), UriKind.Absolute, out var u)
        && u.Scheme is "http" or "https";
}

/// <summary>
/// Reverse image search as LINKS to search engines. The image URL goes into the link; nothing is uploaded.
/// The user clicks that link themselves in a new tab.
/// </summary>
public static class BrowserImageSearch
{
    public static IReadOnlyList<(string Provider, string Url)> Build(string imageUrl)
    {
        var q = Uri.EscapeDataString(imageUrl);
        return new[]
        {
            ("Google Lens", "https://lens.google.com/uploadbyurl?url=" + q),
            ("TinEye", "https://tineye.com/search?url=" + q),
            ("Yandex", "https://yandex.com/images/search?rpt=imageview&url=" + q),
            ("Bing Visual Search", "https://www.bing.com/images/search?view=detailv2&iss=sbi&q=imgurl:" + q),
        };
    }
}
