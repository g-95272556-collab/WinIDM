using System.Globalization;
using System.Text.RegularExpressions;

namespace WinIDM.Browser;

/// <summary>HLS master variant (EXT-X-STREAM-INF).</summary>
public sealed record HlsVariant(
    Uri Uri,
    long Bandwidth,
    long AverageBandwidth,
    int? Width,
    int? Height,
    string? Codecs,
    bool AudioOnly)
{
    /// <summary>
    /// Effective bitrate (bit/s). Use AVERAGE-BANDWIDTH when present; otherwise BANDWIDTH x 0.8
    /// (spec heuristic for estimating the average from the peak value).
    /// </summary>
    public long EffectiveBitsPerSecond =>
        AverageBandwidth > 0 ? AverageBandwidth : (long)(Bandwidth * 0.8);
}

/// <summary>Alternative rendition (EXT-X-MEDIA), for example an audio track or subtitles.</summary>
public sealed record HlsRendition(string Type, string? GroupId, string? Name, string? Language, Uri? Uri, bool IsDefault);

/// <summary>AES-128 key (EXT-X-KEY). Method NONE means no encryption.</summary>
public sealed record HlsKey(string Method, Uri? Uri, byte[]? Iv);

/// <summary>Media segment in the playlist.</summary>
public sealed record HlsSegment(
    Uri Uri,
    double DurationSeconds,
    long SequenceNumber,
    long? ByteLength,
    long? ByteOffset,
    HlsKey? Key);

/// <summary>
/// HLS playlist parser (RFC 8216 subset). Handles master and media playlists,
/// EXT-X-KEY, EXT-X-MAP, EXT-X-BYTERANGE, and EXT-X-MEDIA-SEQUENCE.
/// </summary>
public sealed class HlsPlaylist
{
    private static readonly Regex AttributePattern =
        new(@"([A-Z0-9\-]+)=(""[^""]*""|[^,]*)", RegexOptions.Compiled);

    private static readonly string[] VideoCodecPrefixes = { "avc", "hvc", "hev", "vp0", "vp8", "vp9", "av01", "mp4v" };

    private HlsPlaylist(
        bool isMaster,
        IReadOnlyList<HlsVariant> variants,
        IReadOnlyList<HlsRendition> renditions,
        IReadOnlyList<HlsSegment> segments,
        Uri? mapUri,
        long mediaSequence,
        bool hasEndList,
        bool hasUnsupportedEncryption)
    {
        IsMaster = isMaster;
        Variants = variants;
        Renditions = renditions;
        Segments = segments;
        MapUri = mapUri;
        MediaSequence = mediaSequence;
        HasEndList = hasEndList;
        HasUnsupportedEncryption = hasUnsupportedEncryption;
    }

    public bool IsMaster { get; }
    public IReadOnlyList<HlsVariant> Variants { get; }
    public IReadOnlyList<HlsRendition> Renditions { get; }
    public IReadOnlyList<HlsSegment> Segments { get; }
    public Uri? MapUri { get; }
    public long MediaSequence { get; }
    public bool HasEndList { get; }

    /// <summary>True if any encryption method other than AES-128 or NONE is present (for example SAMPLE-AES).</summary>
    public bool HasUnsupportedEncryption { get; }

    public double TotalDurationSeconds => Segments.Sum(s => s.DurationSeconds);

    public bool IsEncrypted => Segments.Any(s => s.Key is { Method: not "NONE" });

    /// <summary>Parse the playlist contents. <paramref name="baseUri"/> is used for relative URIs.</summary>
    public static HlsPlaylist Parse(string text, Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(baseUri);

        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || !lines[0].TrimStart('﻿').StartsWith("#EXTM3U", StringComparison.Ordinal))
            throw new FormatException("Not an HLS playlist (no #EXTM3U).");

        bool isMaster = false;
        var variants = new List<HlsVariant>();
        var renditions = new List<HlsRendition>();
        var segments = new List<HlsSegment>();
        var pendingVariantAttrs = (Dictionary<string, string>?)null;
        double pendingDuration = -1;
        long mediaSequence = 0;
        long sequence = 0;
        bool hasEnd = false;
        bool unsupportedEnc = false;
        HlsKey? currentKey = null;
        Uri? mapUri = null;
        long lastByteEnd = 0;
        Uri? lastByteUri = null;
        long? pendingByteLength = null;
        long? pendingByteOffset = null;

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal))
            {
                isMaster = true;
                pendingVariantAttrs = ParseAttributes(line[(line.IndexOf(':') + 1)..]);
            }
            else if (line.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal))
            {
                isMaster = true;
                var a = ParseAttributes(line["#EXT-X-MEDIA:".Length..]);
                renditions.Add(new HlsRendition(
                    Unquote(a.GetValueOrDefault("TYPE")) ?? "",
                    Unquote(a.GetValueOrDefault("GROUP-ID")),
                    Unquote(a.GetValueOrDefault("NAME")),
                    Unquote(a.GetValueOrDefault("LANGUAGE")),
                    ResolveOptional(baseUri, Unquote(a.GetValueOrDefault("URI"))),
                    string.Equals(Unquote(a.GetValueOrDefault("DEFAULT")), "YES", StringComparison.OrdinalIgnoreCase)));
            }
            else if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal))
            {
                long.TryParse(line["#EXT-X-MEDIA-SEQUENCE:".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out mediaSequence);
            }
            else if (line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal))
            {
                var a = ParseAttributes(line["#EXT-X-KEY:".Length..]);
                string method = Unquote(a.GetValueOrDefault("METHOD")) ?? "NONE";
                if (method != "NONE" && method != "AES-128") unsupportedEnc = true;
                Uri? keyUri = ResolveOptional(baseUri, Unquote(a.GetValueOrDefault("URI")));
                byte[]? iv = ParseHex(Unquote(a.GetValueOrDefault("IV")));
                currentKey = new HlsKey(method, keyUri, iv);
            }
            else if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
            {
                var a = ParseAttributes(line["#EXT-X-MAP:".Length..]);
                mapUri = ResolveOptional(baseUri, Unquote(a.GetValueOrDefault("URI")));
            }
            else if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                string durText = line["#EXTINF:".Length..];
                int comma = durText.IndexOf(',');
                if (comma >= 0) durText = durText[..comma];
                pendingDuration = double.TryParse(durText, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : 0;
            }
            else if (line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.Ordinal))
            {
                string spec = line["#EXT-X-BYTERANGE:".Length..];
                int at = spec.IndexOf('@');
                if (long.TryParse(at >= 0 ? spec[..at] : spec, NumberStyles.Integer, CultureInfo.InvariantCulture, out long len))
                {
                    pendingByteLength = len;
                    pendingByteOffset = at >= 0 && long.TryParse(spec[(at + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out long off) ? off : null;
                }
            }
            else if (line.StartsWith("#EXT-X-ENDLIST", StringComparison.Ordinal))
            {
                hasEnd = true;
            }
            else if (line[0] == '#')
            {
                // Other tags are ignored.
            }
            else
            {
                Uri uri = ResolveRequired(baseUri, line);
                if (isMaster && pendingVariantAttrs is not null)
                {
                    variants.Add(BuildVariant(uri, pendingVariantAttrs));
                    pendingVariantAttrs = null;
                }
                else if (!isMaster)
                {
                    long? offset = pendingByteOffset;
                    if (pendingByteLength is not null && offset is null)
                        offset = lastByteUri == uri ? lastByteEnd : 0;
                    if (pendingByteLength is not null && offset is not null)
                    {
                        lastByteEnd = offset.Value + pendingByteLength.Value;
                        lastByteUri = uri;
                    }

                    segments.Add(new HlsSegment(
                        uri,
                        pendingDuration < 0 ? 0 : pendingDuration,
                        mediaSequence + sequence,
                        pendingByteLength,
                        pendingByteLength is null ? null : offset,
                        currentKey));
                    sequence++;
                    pendingDuration = -1;
                    pendingByteLength = null;
                    pendingByteOffset = null;
                }
            }
        }

        return new HlsPlaylist(isMaster, variants, renditions, segments, mapUri, mediaSequence, hasEnd, unsupportedEnc);
    }

    private static HlsVariant BuildVariant(Uri uri, Dictionary<string, string> a)
    {
        long bandwidth = ParseLong(a.GetValueOrDefault("BANDWIDTH"));
        long average = ParseLong(a.GetValueOrDefault("AVERAGE-BANDWIDTH"));
        int? width = null, height = null;
        string? resolution = Unquote(a.GetValueOrDefault("RESOLUTION"));
        if (resolution is not null)
        {
            var parts = resolution.Split('x', 'X');
            if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h))
            {
                width = w;
                height = h;
            }
        }
        string? codecs = Unquote(a.GetValueOrDefault("CODECS"));
        bool hasVideoCodec = codecs is not null &&
            codecs.Split(',').Any(c => VideoCodecPrefixes.Any(p => c.Trim().StartsWith(p, StringComparison.OrdinalIgnoreCase)));
        bool audioOnly = !hasVideoCodec && width is null && (codecs is not null || Unquote(a.GetValueOrDefault("AUDIO")) is not null);
        return new HlsVariant(uri, bandwidth, average, width, height, codecs, audioOnly);
    }

    /// <summary>Split attributes such as KEY=VALUE,KEY2="a,b" into a dictionary (uppercase keys).</summary>
    internal static Dictionary<string, string> ParseAttributes(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in AttributePattern.Matches(text))
            map[m.Groups[1].Value.ToUpperInvariant()] = m.Groups[2].Value;
        return map;
    }

    private static string? Unquote(string? value)
    {
        if (value is null) return null;
        value = value.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') return value[1..^1];
        return value;
    }

    private static long ParseLong(string? value) =>
        long.TryParse(Unquote(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : 0;

    private static Uri? ResolveOptional(Uri baseUri, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return ResolveRequired(baseUri, value);
    }

    private static Uri ResolveRequired(Uri baseUri, string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var abs)) return abs;
        return new Uri(baseUri, value);
    }

    /// <summary>Hex IV such as 0x0123... becomes 16 bytes. Null if missing or invalid.</summary>
    internal static byte[]? ParseHex(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        string hex = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        if (hex.Length == 0 || hex.Length % 2 != 0) return null;
        try { return Convert.FromHexString(hex); }
        catch (FormatException) { return null; }
    }
}
