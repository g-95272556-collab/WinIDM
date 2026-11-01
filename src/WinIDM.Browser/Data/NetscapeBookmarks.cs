using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace WinIDM.Browser;

/// <summary>Intermediate node produced by parsing a Netscape bookmark file.</summary>
internal sealed class NetscapeNode
{
    public NetscapeNode(string title, string? url)
    {
        Title = title;
        Url = url;
    }

    public string Title { get; }
    public string? Url { get; }
    public bool IsFolder => Url is null;
    public List<NetscapeNode> Children { get; } = new();
}

/// <summary>
/// Writer and parser for the Netscape bookmark HTML format (exported by most browsers).
/// The parser is tolerant: unknown tags are ignored, and only http(s) is accepted as a URL.
/// </summary>
public static class NetscapeBookmarks
{
    public const int MaxDepth = 64;
    public const int MaxItems = 100_000;

    private static readonly Regex TagPattern = new(@"<(/?)([A-Za-z0-9]+)([^>]*)>", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex HrefPattern = new(@"HREF\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string Export(IEnumerable<BookmarkNode> roots)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE NETSCAPE-Bookmark-file-1>\n");
        sb.Append("<!-- This is an automatically generated file.\n     It will be read and overwritten.\n     DO NOT EDIT! -->\n");
        sb.Append("<META HTTP-EQUIV=\"Content-Type\" CONTENT=\"text/html; charset=UTF-8\">\n");
        sb.Append("<TITLE>Bookmarks</TITLE>\n<H1>Bookmarks</H1>\n<DL><p>\n");
        foreach (var node in roots) WriteNode(sb, node, 1);
        sb.Append("</DL><p>\n");
        return sb.ToString();
    }

    private static void WriteNode(StringBuilder sb, BookmarkNode node, int depth)
    {
        string indent = new(' ', depth * 4);
        if (node.IsFolder)
        {
            sb.Append(indent).Append("<DT><H3>").Append(WebUtility.HtmlEncode(node.Title)).Append("</H3>\n");
            sb.Append(indent).Append("<DL><p>\n");
            foreach (var child in node.Children) WriteNode(sb, child, depth + 1);
            sb.Append(indent).Append("</DL><p>\n");
        }
        else
        {
            sb.Append(indent).Append("<DT><A HREF=\"").Append(WebUtility.HtmlEncode(node.Url ?? ""))
              .Append("\">").Append(WebUtility.HtmlEncode(node.Title)).Append("</A>\n");
        }
    }

    /// <summary>Parse bookmark HTML. Returns the list of top-level nodes.</summary>
    internal static List<NetscapeNode> Parse(string html, out int skipped)
    {
        var root = new NetscapeNode("", null);
        var stack = new Stack<NetscapeNode>();
        stack.Push(root);
        NetscapeNode? pendingFolder = null;
        string? captureKind = null; // "H3" or "A"
        string? captureHref = null;
        var capture = new StringBuilder();
        skipped = 0;
        int items = 0;

        int cursor = 0;
        foreach (Match m in TagPattern.Matches(html))
        {
            if (captureKind is not null && m.Index > cursor)
                capture.Append(html, cursor, m.Index - cursor);
            cursor = m.Index + m.Length;

            bool closing = m.Groups[1].Value.Length > 0;
            string tag = m.Groups[2].Value.ToUpperInvariant();
            string attrs = m.Groups[3].Value;

            switch (tag)
            {
                case "H3" when !closing:
                    captureKind = "H3";
                    capture.Clear();
                    break;

                case "H3" when closing && captureKind == "H3":
                {
                    string title = Clean(capture.ToString());
                    captureKind = null;
                    var folder = new NetscapeNode(title.Length == 0 ? "(untitled)" : title, null);
                    stack.Peek().Children.Add(folder);
                    pendingFolder = folder;
                    break;
                }

                case "A" when !closing:
                {
                    captureKind = "A";
                    capture.Clear();
                    var hm = HrefPattern.Match(attrs);
                    captureHref = hm.Success
                        ? WebUtility.HtmlDecode(hm.Groups[1].Success ? hm.Groups[1].Value
                            : hm.Groups[2].Success ? hm.Groups[2].Value : hm.Groups[3].Value)
                        : null;
                    break;
                }

                case "A" when closing && captureKind == "A":
                {
                    string title = Clean(capture.ToString());
                    captureKind = null;
                    if (items >= MaxItems) { skipped++; }
                    else if (captureHref is not null && HostUtil.TryGetHttpUri(captureHref, out var uri))
                    {
                        stack.Peek().Children.Add(new NetscapeNode(title.Length == 0 ? uri.AbsoluteUri : title, uri.AbsoluteUri));
                        items++;
                    }
                    else
                    {
                        skipped++;
                    }
                    captureHref = null;
                    break;
                }

                case "DL" when !closing:
                    // Every DL pushes one entry, so </DL> lines up with a pop.
                    if (stack.Count < MaxDepth) stack.Push(pendingFolder ?? stack.Peek());
                    pendingFolder = null;
                    break;

                case "DL" when closing:
                    if (stack.Count > 1) stack.Pop();
                    break;
            }
        }

        return root.Children;
    }

    private static string Clean(string raw)
    {
        string text = Regex.Replace(raw, @"<[^>]*>", "");
        return WebUtility.HtmlDecode(text).Trim();
    }
}
