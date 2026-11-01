namespace WinIDM.Browser;

/// <summary>A search engine with a URL template. The template must contain {q}.</summary>
public sealed record SearchEngine(string Id, string Name, string Template);

/// <summary>
/// Search engine list. Google is the default. Third-party client identification parameters
/// (for example client=ddg_android found in the original code) are deliberately omitted.
/// </summary>
public static class SearchEngines
{
    public const string DefaultId = "google";
    public const string CustomId = "custom";
    public const string QueryPlaceholder = "{q}";

    private static readonly IReadOnlyList<SearchEngine> Engines = new[]
    {
        new SearchEngine("google", "Google", "https://www.google.com/search?q={q}"),
        new SearchEngine("bing", "Bing", "https://www.bing.com/search?q={q}"),
        new SearchEngine("yahoo", "Yahoo", "https://search.yahoo.com/search?p={q}"),
        new SearchEngine("yahoo-jp", "Yahoo Japan", "https://search.yahoo.co.jp/search?p={q}"),
        new SearchEngine("startpage", "Startpage", "https://www.startpage.com/do/search?q={q}"),
        new SearchEngine("startpage-mobile", "Startpage Mobile", "https://www.startpage.com/do/m/mobilesearch?q={q}"),
        new SearchEngine("duckduckgo", "DuckDuckGo", "https://duckduckgo.com/?q={q}"),
        new SearchEngine("duckduckgo-lite", "DuckDuckGo Lite", "https://duckduckgo.com/lite/?q={q}"),
        new SearchEngine("baidu", "Baidu", "https://www.baidu.com/s?wd={q}"),
        new SearchEngine("yandex-ru", "Yandex.ru", "https://yandex.ru/yandsearch?text={q}"),
        new SearchEngine("yandex-com", "Yandex.com", "https://yandex.com/search/?text={q}"),
        new SearchEngine("aol", "AOL", "https://search.aol.com/aol/search?q={q}"),
        new SearchEngine("brave", "Brave", "https://search.brave.com/search?q={q}"),
        new SearchEngine(CustomId, "Custom", string.Empty),
    };

    /// <summary>All engines, including the custom entry (the user supplies its template).</summary>
    public static IReadOnlyList<SearchEngine> All => Engines;

    public static SearchEngine Get(string? id) =>
        Engines.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal))
        ?? Engines.First(e => e.Id == DefaultId);

    /// <summary>Build a search URL. For the custom engine, <paramref name="customTemplate"/> must be valid.</summary>
    public static string BuildSearchUrl(string engineId, string query, string? customTemplate = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        var engine = Get(engineId);
        string template = engine.Id == CustomId ? (customTemplate ?? "") : engine.Template;
        if (!IsValidTemplate(template))
            throw new ArgumentException("Custom search template must be an http(s) URL that contains {q}.", nameof(customTemplate));
        return template.Replace(QueryPlaceholder, Uri.EscapeDataString(query), StringComparison.Ordinal);
    }

    public static bool IsValidTemplate(string? template)
    {
        if (string.IsNullOrWhiteSpace(template)) return false;
        if (!template.Contains(QueryPlaceholder, StringComparison.Ordinal)) return false;
        string probe = template.Replace(QueryPlaceholder, "x", StringComparison.Ordinal);
        return HostUtil.TryGetHttpUri(probe, out _);
    }
}
