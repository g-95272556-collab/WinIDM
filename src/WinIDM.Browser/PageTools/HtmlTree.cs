using System.Net;
using System.Text.RegularExpressions;

namespace WinIDM.Browser;

/// <summary>Simple HTML tree node. Text nodes have Name = null.</summary>
internal sealed class HtmlNode
{
    public HtmlNode(string? name, HtmlNode? parent, IReadOnlyDictionary<string, string>? attributes = null, string? text = null)
    {
        Name = name;
        Parent = parent;
        Attributes = attributes ?? EmptyAttributes;
        Text = text;
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyAttributes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string? Name { get; }
    public HtmlNode? Parent { get; }
    public string? Text { get; }
    public IReadOnlyDictionary<string, string> Attributes { get; }
    public List<HtmlNode> Children { get; } = new();

    public bool IsText => Name is null;

    public string? Attr(string name) => Attributes.TryGetValue(name, out var v) ? v : null;
}

/// <summary>
/// Tolerant HTML parser (not a full DOM). Handles void tags, unclosed tags,
/// implied closing for p, li, and td/th, and raw script/style content.
/// Does not fetch resources or run scripts.
/// </summary>
internal static class HtmlTree
{
    private const int MaxNodes = 200_000;

    private static readonly Regex Token = new(
        @"<!--.*?-->|<![^>]*>|<\?[^>]*>|</([A-Za-z][A-Za-z0-9:-]*)\s*>|<([A-Za-z][A-Za-z0-9:-]*)((?:\s+[^\s""'>/=]+(?:\s*=\s*(?:""[^""]*""|'[^']*'|[^\s""'>]+))?)*)\s*(/?)>",
        RegexOptions.Singleline | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AttributePattern = new(
        @"([^\s""'>/=]+)(?:\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s""'>]+)))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr",
    };

    private static readonly HashSet<string> RawTextElements = new(StringComparer.OrdinalIgnoreCase) { "script", "style" };

    private static readonly HashSet<string> BlockStarters = new(StringComparer.OrdinalIgnoreCase)
    {
        "address", "article", "aside", "blockquote", "div", "dl", "fieldset", "figure", "footer", "form",
        "h1", "h2", "h3", "h4", "h5", "h6", "header", "hr", "main", "nav", "ol", "p", "pre", "section", "table", "ul",
    };

    public static HtmlNode Parse(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        var root = new HtmlNode("#document", null);
        var stack = new List<HtmlNode> { root };
        int count = 0;
        int pos = 0;

        foreach (Match m in Token.Matches(html))
        {
            // Matches that fall inside skipped raw content are not processed.
            if (m.Index < pos) continue;
            if (m.Index > pos) AddText(stack, html[pos..m.Index], ref count);
            pos = m.Index + m.Length;

            if (m.Groups[1].Success)
            {
                CloseTag(stack, m.Groups[1].Value);
                continue;
            }
            if (!m.Groups[2].Success) continue; // comment, doctype, or PI

            string name = m.Groups[2].Value.ToLowerInvariant();
            bool selfClosing = m.Groups[4].Value == "/";
            var attrs = ParseAttributes(m.Groups[3].Value);

            ApplyImpliedClose(stack, name);

            var node = new HtmlNode(name, stack[^1], attrs);
            stack[^1].Children.Add(node);
            if (++count > MaxNodes) break;

            if (selfClosing || VoidElements.Contains(name)) continue;

            if (RawTextElements.Contains(name))
            {
                // Skip raw content up to the matching closing tag.
                var close = Regex.Match(html[pos..], @"</" + name + @"\s*>", RegexOptions.IgnoreCase);
                pos = close.Success ? pos + close.Index + close.Length : html.Length;
                continue;
            }

            stack.Add(node);
        }

        if (pos < html.Length && count <= MaxNodes) AddText(stack, html[pos..], ref count);
        return root;
    }

    /// <summary>Collect a node's inner text (without separators). Used for titles and link text.</summary>
    public static string InnerText(HtmlNode node)
    {
        var sb = new System.Text.StringBuilder();
        Collect(node, sb);
        return sb.ToString();
    }

    public static IEnumerable<HtmlNode> Descendants(HtmlNode node)
    {
        var stack = new Stack<HtmlNode>();
        for (int i = node.Children.Count - 1; i >= 0; i--) stack.Push(node.Children[i]);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            yield return current;
            for (int i = current.Children.Count - 1; i >= 0; i--) stack.Push(current.Children[i]);
        }
    }

    private static void Collect(HtmlNode node, System.Text.StringBuilder sb)
    {
        if (node.IsText)
        {
            sb.Append(node.Text);
            return;
        }
        foreach (var child in node.Children) Collect(child, sb);
    }

    private static void AddText(List<HtmlNode> stack, string raw, ref int count)
    {
        if (raw.Length == 0) return;
        string decoded = WebUtility.HtmlDecode(raw);
        var parent = stack[^1];
        parent.Children.Add(new HtmlNode(null, parent, null, decoded));
        count++;
    }

    private static void CloseTag(List<HtmlNode> stack, string rawName)
    {
        for (int i = stack.Count - 1; i >= 1; i--)
        {
            if (string.Equals(stack[i].Name, rawName, StringComparison.OrdinalIgnoreCase))
            {
                stack.RemoveRange(i, stack.Count - i);
                return;
            }
        }
    }

    private static void ApplyImpliedClose(List<HtmlNode> stack, string name)
    {
        if (BlockStarters.Contains(name) && stack.Count > 1 && IsName(stack[^1], "p"))
            stack.RemoveAt(stack.Count - 1);

        if (name == "li")
        {
            for (int i = stack.Count - 1; i >= 1; i--)
            {
                if (IsName(stack[i], "li")) { stack.RemoveRange(i, stack.Count - i); break; }
                if (IsName(stack[i], "ul") || IsName(stack[i], "ol")) break;
            }
        }

        if (name is "td" or "th")
        {
            for (int i = stack.Count - 1; i >= 1; i--)
            {
                if (IsName(stack[i], "td") || IsName(stack[i], "th")) { stack.RemoveRange(i, stack.Count - i); break; }
                if (IsName(stack[i], "tr") || IsName(stack[i], "table")) break;
            }
        }
    }

    private static bool IsName(HtmlNode node, string name) =>
        string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, string> ParseAttributes(string raw)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in AttributePattern.Matches(raw))
        {
            string key = m.Groups[1].Value.ToLowerInvariant();
            string value = m.Groups[2].Success ? m.Groups[2].Value
                : m.Groups[3].Success ? m.Groups[3].Value
                : m.Groups[4].Success ? m.Groups[4].Value
                : "";
            map.TryAdd(key, WebUtility.HtmlDecode(value));
        }
        return map;
    }
}
