using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace WinIDM.Browser;

/// <summary>Reader mode result: title and plain article text.</summary>
public sealed record ReaderArticle(string Title, string? Byline, string Text)
{
    public string Excerpt => Text.Length <= 200 ? Text : Text[..200];
}

/// <summary>File category based on the URL extension.</summary>
public enum FileCategory
{
    Page,
    Document,
    Archive,
    Application,
    Audio,
    Video,
    Image,
    Subtitle,
    Other,
}

/// <summary>A link or file found on a page.</summary>
public sealed record GrabbedLink(string Url, string Text, FileCategory Category, long? SizeBytes)
{
    public GrabbedLink WithSize(long? size) => this with { SizeBytes = size };
}

/// <summary>
/// Filter for link lists: URL regex, file category, and size range.
/// Links with unknown size are not filtered by the size limits (they are kept).
/// </summary>
public sealed class LinkFilter
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    public Regex? UrlPattern { get; init; }
    public IReadOnlySet<FileCategory>? Categories { get; init; }
    public long? MinSizeBytes { get; init; }
    public long? MaxSizeBytes { get; init; }

    /// <summary>Build a regex filter with a timeout. An invalid regex throws ArgumentException.</summary>
    public static LinkFilter FromPattern(string pattern, IReadOnlySet<FileCategory>? categories = null,
        long? minSize = null, long? maxSize = null) =>
        new()
        {
            UrlPattern = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout),
            Categories = categories,
            MinSizeBytes = minSize,
            MaxSizeBytes = maxSize,
        };

    public bool Matches(GrabbedLink link)
    {
        if (UrlPattern is not null)
        {
            try
            {
                if (!UrlPattern.IsMatch(link.Url)) return false;
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }
        if (Categories is not null && !Categories.Contains(link.Category)) return false;
        if (link.SizeBytes is long size)
        {
            if (MinSizeBytes is long min && size < min) return false;
            if (MaxSizeBytes is long max && size > max) return false;
        }
        return true;
    }

    public IReadOnlyList<GrabbedLink> Apply(IEnumerable<GrabbedLink> links) => links.Where(Matches).ToList();
}

/// <summary>
/// Page tools: reader mode (HTML to article text) and the page-to-link converter for the Grabber.
/// No page code runs. Only HEAD requests are made, and only when called explicitly.
/// </summary>
public sealed class PageTools : IDisposable
{
    private const int MaxParagraphCandidates = 50_000;
    private const int MaxLinkTextLength = 200;

    private static readonly HashSet<string> SkippedElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "noscript", "template", "svg", "iframe", "object", "embed", "nav", "footer",
        "aside", "form", "button", "select", "input", "textarea", "canvas", "video", "audio", "dialog", "head",
    };

    private static readonly HashSet<string> NoiseTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "ad", "ads", "advert", "advertisement", "sidebar", "comment", "comments", "share", "social",
        "menu", "cookie", "banner", "promo", "newsletter", "popup", "related", "navigation", "nav",
    };

    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "section", "article", "main", "header", "blockquote", "pre", "ul", "ol", "table", "tr",
        "h1", "h2", "h3", "h4", "h5", "h6", "figure", "figcaption", "dl", "dt", "dd", "li",
    };

    private static readonly Dictionary<string, FileCategory> ExtensionMap = BuildExtensionMap();

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public PageTools(HttpClient? httpClient = null)
    {
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient(new SocketsHttpHandler { UseCookies = false }) { Timeout = TimeSpan.FromSeconds(15) };
    }

    // ---------------------------------------------------------------------
    // Reader mode

    /// <summary>
    /// Extract the main article. Non-content elements (scripts, navigation, ads) are dropped. The container
    /// is chosen by paragraph text density, with a penalty for link text.
    /// </summary>
    public ReaderArticle ExtractReadableArticle(string html, string? baseUrl = null)
    {
        ArgumentNullException.ThrowIfNull(html);
        var doc = HtmlTree.Parse(html);

        string title = ExtractTitle(doc);
        string? byline = FindMetaContent(doc, "author");

        var body = FindFirst(doc, "body") ?? doc;
        Prune(body);

        var container = PickContainer(body) ?? body;
        string text = RenderText(container);
        if (text.Length == 0)
            text = RenderText(body);

        return new ReaderArticle(title, byline, text);
    }

    private static string ExtractTitle(HtmlNode doc)
    {
        var titleNode = FindFirst(doc, "title");
        string? title = titleNode is null ? null : Normalize(HtmlTree.InnerText(titleNode));
        if (!string.IsNullOrWhiteSpace(title)) return title!;

        foreach (var node in HtmlTree.Descendants(doc))
        {
            if (!IsName(node, "meta")) continue;
            if (string.Equals(node.Attr("property"), "og:title", StringComparison.OrdinalIgnoreCase) && node.Attr("content") is string og)
                return Normalize(og);
        }
        return "";
    }

    private static string? FindMetaContent(HtmlNode doc, string name)
    {
        foreach (var node in HtmlTree.Descendants(doc))
        {
            if (IsName(node, "meta") && string.Equals(node.Attr("name"), name, StringComparison.OrdinalIgnoreCase))
                return node.Attr("content") is string c && c.Trim().Length > 0 ? Normalize(c) : null;
        }
        return null;
    }

    private static void Prune(HtmlNode root)
    {
        var stack = new Stack<HtmlNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            node.Children.RemoveAll(child => !child.IsText && IsNoise(child));
            foreach (var child in node.Children)
            {
                if (!child.IsText) stack.Push(child);
            }
        }
    }

    private static bool IsNoise(HtmlNode node)
    {
        if (node.Name is not null && SkippedElements.Contains(node.Name)) return true;
        string classes = (node.Attr("class") ?? "") + " " + (node.Attr("id") ?? "");
        if (classes.Trim().Length == 0) return false;
        foreach (string token in classes.Split(new[] { ' ', '_', '-', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (NoiseTokens.Contains(token)) return true;
        }
        return false;
    }

    /// <summary>The best container collects the most paragraph text with a low link ratio.</summary>
    private static HtmlNode? PickContainer(HtmlNode body)
    {
        var textByNode = new Dictionary<HtmlNode, double>(ReferenceEqualityComparer.Instance);
        var linkByNode = new Dictionary<HtmlNode, double>(ReferenceEqualityComparer.Instance);
        int examined = 0;

        foreach (var node in HtmlTree.Descendants(body))
        {
            if (node.IsText || !IsName(node, "p") && !IsName(node, "pre") && !IsName(node, "blockquote")) continue;
            if (++examined > MaxParagraphCandidates) break;

            string text = Normalize(HtmlTree.InnerText(node));
            if (text.Length < 25) continue;
            double linkChars = LinkTextLength(node);

            for (var ancestor = node.Parent; ancestor is not null; ancestor = ancestor.Parent)
            {
                textByNode[ancestor] = textByNode.GetValueOrDefault(ancestor) + text.Length;
                linkByNode[ancestor] = linkByNode.GetValueOrDefault(ancestor) + linkChars;
                if (ReferenceEquals(ancestor, body)) break;
            }
        }

        HtmlNode? best = null;
        double bestScore = 0;
        foreach (var (node, text) in textByNode)
        {
            double links = linkByNode.GetValueOrDefault(node);
            double density = text > 0 ? 1 - Math.Min(1, links / text) : 0;
            double score = text * density;
            if (IsName(node, "article") || IsName(node, "main") || string.Equals(node.Attr("role"), "main", StringComparison.OrdinalIgnoreCase))
                score *= 1.25;
            if (score > bestScore)
            {
                bestScore = score;
                best = node;
            }
        }
        return best;
    }

    private static double LinkTextLength(HtmlNode node)
    {
        double total = 0;
        foreach (var d in HtmlTree.Descendants(node))
        {
            if (IsName(d, "a")) total += Normalize(HtmlTree.InnerText(d)).Length;
        }
        return total;
    }

    private static string RenderText(HtmlNode root)
    {
        var sb = new StringBuilder();
        Render(root, sb);
        string raw = sb.ToString().Replace("\r", "");

        var lines = raw.Split('\n').Select(l => Regex.Replace(l, @"[ \t ]+", " ").Trim());
        string joined = string.Join("\n", lines);
        joined = Regex.Replace(joined, @"\n{3,}", "\n\n");
        return joined.Trim();
    }

    private static void Render(HtmlNode node, StringBuilder sb)
    {
        if (node.IsText)
        {
            sb.Append(node.Text);
            return;
        }

        string? name = node.Name?.ToLowerInvariant();
        if (name is null || SkippedElements.Contains(name)) return;

        if (name == "br")
        {
            sb.Append('\n');
            return;
        }
        bool block = BlockTags.Contains(name) || name is "h1" or "h2" or "h3" or "h4" or "h5" or "h6";
        if (block) EnsureNewline(sb);
        if (name == "li") sb.Append("- ");

        foreach (var child in node.Children) Render(child, sb);

        if (block)
        {
            EnsureNewline(sb);
            sb.Append('\n');
        }
    }

    private static void EnsureNewline(StringBuilder sb)
    {
        if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n');
    }

    // ---------------------------------------------------------------------
    // Page-to-link converter (Grabber)

    /// <summary>
    /// Extract all links and media sources from the page, with URLs made absolute
    /// (respecting &lt;base href&gt;). Duplicate links are dropped. The filter (if any) is applied.
    /// </summary>
    public IReadOnlyList<GrabbedLink> ExtractLinks(string html, string baseUrl, LinkFilter? filter = null)
    {
        ArgumentNullException.ThrowIfNull(html);
        if (!HostUtil.TryGetHttpUri(baseUrl, out var pageUri))
            throw new ArgumentException("Base URL must be http(s).", nameof(baseUrl));

        var doc = HtmlTree.Parse(html);
        Uri effectiveBase = pageUri;
        var baseNode = FindFirst(doc, "base");
        if (baseNode?.Attr("href") is string baseHref && Uri.TryCreate(pageUri, baseHref.Trim(), out var fromBase)
            && HostUtil.TryGetHttpUri(fromBase.AbsoluteUri, out _))
        {
            effectiveBase = fromBase;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var links = new List<GrabbedLink>();

        foreach (var node in HtmlTree.Descendants(doc))
        {
            if (node.IsText || node.Name is null) continue;
            string? raw = node.Name switch
            {
                "a" or "area" => node.Attr("href"),
                "img" or "video" or "audio" or "source" or "embed" or "track" => node.Attr("src"),
                _ => null,
            };
            if (raw is null) continue;

            string? absolute = Resolve(effectiveBase, raw);
            if (absolute is null || !seen.Add(absolute)) continue;

            string text = node.Name is "a" or "area"
                ? Truncate(Normalize(HtmlTree.InnerText(node)), MaxLinkTextLength)
                : Normalize(node.Attr("alt") ?? node.Attr("title") ?? "");
            if (text.Length == 0)
                text = Uri.UnescapeDataString(Path.GetFileName(new Uri(absolute).AbsolutePath));

            var link = new GrabbedLink(absolute, text, Categorize(absolute), null);
            if (filter is null || filter.Matches(link)) links.Add(link);
        }

        return links;
    }

    /// <summary>
    /// Fill in the size (Content-Length) for each link using HEAD requests. Failed links are
    /// kept with a null size. The concurrency limit prevents request floods.
    /// </summary>
    public async Task<IReadOnlyList<GrabbedLink>> ProbeSizesAsync(IReadOnlyList<GrabbedLink> links, int maxConcurrency = 4, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(links);
        if (maxConcurrency < 1) throw new ArgumentOutOfRangeException(nameof(maxConcurrency));

        using var gate = new SemaphoreSlim(maxConcurrency);
        var tasks = links.Select(async link =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return link.WithSize(await HeadSizeAsync(link.Url, ct).ConfigureAwait(false));
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task<long?> HeadSizeAsync(string url, CancellationToken ct)
    {
        if (!HostUtil.TryGetHttpUri(url, out var uri)) return null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, uri);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            return response.Content.Headers.ContentLength;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    // ---------------------------------------------------------------------
    // Internals

    private static string? Resolve(Uri baseUri, string raw)
    {
        string value = raw.Trim();
        if (value.Length == 0 || value.StartsWith('#')) return null;
        foreach (string skip in new[] { "javascript:", "mailto:", "tel:", "data:", "blob:", "about:", "sms:" })
        {
            if (value.StartsWith(skip, StringComparison.OrdinalIgnoreCase)) return null;
        }

        Uri? uri;
        try
        {
            uri = Uri.TryCreate(value, UriKind.Absolute, out var abs) ? abs : new Uri(baseUri, value);
        }
        catch (UriFormatException)
        {
            return null;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        var builder = new UriBuilder(uri) { Fragment = "" };
        return builder.Uri.AbsoluteUri;
    }

    internal static FileCategory Categorize(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return FileCategory.Other;
        string path = uri.AbsolutePath;
        int slash = path.LastIndexOf('/');
        string name = slash >= 0 ? path[(slash + 1)..] : path;
        int dot = name.LastIndexOf('.');
        if (dot < 0) return FileCategory.Page;
        string ext = name[(dot + 1)..].ToLowerInvariant();
        return ExtensionMap.TryGetValue(ext, out var category) ? category : FileCategory.Other;
    }

    private static Dictionary<string, FileCategory> BuildExtensionMap()
    {
        var map = new Dictionary<string, FileCategory>(StringComparer.Ordinal);
        void Add(FileCategory category, params string[] exts)
        {
            foreach (string e in exts) map[e] = category;
        }

        Add(FileCategory.Page, "htm", "html", "php", "asp", "aspx", "jsp", "shtml", "xhtml");
        Add(FileCategory.Document, "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "odt", "ods", "odp", "txt", "rtf", "epub", "mobi", "csv");
        Add(FileCategory.Archive, "zip", "rar", "7z", "tar", "gz", "tgz", "bz2", "xz", "iso", "cab");
        Add(FileCategory.Application, "exe", "msi", "apk", "dmg", "pkg", "deb", "rpm", "appimage", "bat");
        Add(FileCategory.Audio, "mp3", "m4a", "aac", "flac", "wav", "ogg", "opus", "wma");
        Add(FileCategory.Video, "mp4", "mkv", "avi", "mov", "webm", "flv", "wmv", "m4v", "mpeg", "mpg", "ts", "m3u8", "mpd");
        Add(FileCategory.Image, "jpg", "jpeg", "png", "gif", "webp", "bmp", "svg", "tif", "tiff", "avif");
        Add(FileCategory.Subtitle, "srt", "vtt", "ass", "ssa", "sub");
        return map;
    }

    private static HtmlNode? FindFirst(HtmlNode root, string name)
    {
        foreach (var node in HtmlTree.Descendants(root))
        {
            if (IsName(node, name)) return node;
        }
        return null;
    }

    private static bool IsName(HtmlNode node, string name) =>
        node.Name is not null && string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string text) =>
        Regex.Replace(text, @"\s+", " ", RegexOptions.CultureInvariant).Trim();

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
}
