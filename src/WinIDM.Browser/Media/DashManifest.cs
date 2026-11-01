using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace WinIDM.Browser;

/// <summary>A representation (video or audio) in a DASH manifest.</summary>
public sealed record DashRepresentation(
    string Id,
    string? ContentType,
    long Bandwidth,
    int? Width,
    int? Height,
    string? MimeType,
    string? Codecs)
{
    public string Quality
    {
        get
        {
            string kbps = $"{Bandwidth / 1000} kbps";
            if (Width is int w && Height is int h) return $"{w}x{h} ({kbps})";
            return $"{ContentType ?? "media"} ({kbps})";
        }
    }
}

/// <summary>Summary information for a DASH MPD manifest.</summary>
public sealed record DashManifestInfo(double? DurationSeconds, IReadOnlyList<DashRepresentation> Representations);

/// <summary>
/// MPD (DASH) parser. Reads only the attributes needed for the quality list and size estimate.
/// DTD is prohibited and the XML resolver is disabled to prevent XXE.
/// </summary>
public static class DashManifest
{
    private static readonly Regex IsoDuration = new(
        @"^P(?:(?<d>\d+(?:\.\d+)?)D)?(?:T(?:(?<h>\d+(?:\.\d+)?)H)?(?:(?<m>\d+(?:\.\d+)?)M)?(?:(?<s>\d+(?:\.\d+)?)S)?)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static DashManifestInfo Parse(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
        };

        XDocument doc;
        using (var reader = XmlReader.Create(new StringReader(xml), settings))
        {
            doc = XDocument.Load(reader);
        }

        XElement root = doc.Root ?? throw new FormatException("Empty MPD.");
        if (!string.Equals(root.Name.LocalName, "MPD", StringComparison.Ordinal))
            throw new FormatException("Root is not MPD.");

        XNamespace ns = root.Name.Namespace;
        double? duration = ParseIsoDuration(Attr(root, "mediaPresentationDuration"));

        var reps = new List<DashRepresentation>();
        foreach (var period in root.Elements(ns + "Period"))
        {
            foreach (var set in period.Elements(ns + "AdaptationSet"))
            {
                string? setMime = Attr(set, "mimeType");
                string? setCodecs = Attr(set, "codecs");
                string? setContent = Attr(set, "contentType");
                int? setWidth = IntAttr(set, "width");
                int? setHeight = IntAttr(set, "height");

                foreach (var rep in set.Elements(ns + "Representation"))
                {
                    string mime = Attr(rep, "mimeType") ?? setMime ?? "";
                    string? content = setContent ?? (mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ? "video"
                        : mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ? "audio" : null);
                    reps.Add(new DashRepresentation(
                        Attr(rep, "id") ?? reps.Count.ToString(CultureInfo.InvariantCulture),
                        content,
                        LongAttr(rep, "bandwidth"),
                        IntAttr(rep, "width") ?? setWidth,
                        IntAttr(rep, "height") ?? setHeight,
                        mime.Length > 0 ? mime : null,
                        Attr(rep, "codecs") ?? setCodecs));
                }
            }
        }

        return new DashManifestInfo(duration, reps);
    }

    /// <summary>Supported ISO 8601 duration: D, H, M, S (for example PT1H30M, PT90.5S).</summary>
    public static double? ParseIsoDuration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var m = IsoDuration.Match(value.Trim());
        if (!m.Success) return null;
        double Num(string g) => m.Groups[g].Success
            ? double.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture)
            : 0;
        return Num("d") * 86400 + Num("h") * 3600 + Num("m") * 60 + Num("s");
    }

    private static string? Attr(XElement e, string name) => (string?)e.Attribute(name);

    private static int? IntAttr(XElement e, string name) =>
        int.TryParse(Attr(e, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : null;

    private static long LongAttr(XElement e, string name) =>
        long.TryParse(Attr(e, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : 0;
}
