using System.Globalization;

namespace WinIDM.Engine;

/// <summary>A variant in the master playlist (<c>#EXT-X-STREAM-INF</c>). <see cref="AudioGroup"/> is the audio GROUP-ID when the audio track is separate.</summary>
public sealed record HlsVariant(Uri Uri, long Bandwidth, string? Resolution, string? AudioGroup = null);

/// <summary>Key information from <c>#EXT-X-KEY</c>. The method is for example AES-128 or SAMPLE-AES.</summary>
public sealed record HlsKey(string Method, Uri? Uri, byte[]? Iv);

/// <summary>Byte range: <see cref="Length"/> bytes starting at <see cref="Offset"/> (null = after the previous range).</summary>
public sealed record HlsByteRange(long Length, long? Offset);

/// <summary>Media segment. <see cref="Sequence"/> is the sequence number (used for the default IV and live resume).</summary>
public sealed record HlsSegment(Uri Uri, double Duration, long Sequence, HlsKey? Key, HlsByteRange? ByteRange = null);

/// <summary><c>#EXT-X-MAP</c>: fMP4 initialization segment (moov) that precedes all fragments.</summary>
public sealed record HlsMap(Uri Uri, HlsByteRange? ByteRange);

/// <summary><c>#EXT-X-MEDIA</c> rendition (for example a separate audio track).</summary>
public sealed record HlsRendition(string Type, string GroupId, string? Name, Uri? Uri, bool IsDefault);

/// <summary>
/// Parser for HLS playlists (m3u8). Supports master playlists (variants, audio renditions) and media playlists:
/// <c>#EXT-X-KEY</c> (AES-128 and SAMPLE-AES detected), <c>#EXT-X-MAP</c> (fMP4), <c>#EXT-X-BYTERANGE</c>,
/// <c>#EXT-X-MEDIA-SEQUENCE</c>, <c>#EXT-X-TARGETDURATION</c>, <c>#EXT-X-PLAYLIST-TYPE</c>, and <c>#EXT-X-ENDLIST</c>.
/// A playlist without <c>#EXT-X-ENDLIST</c> is treated as a live stream (<see cref="IsLive"/>).
/// </summary>
public sealed class M3u8Playlist
{
    public bool IsMaster { get; private init; }

    public IReadOnlyList<HlsVariant> Variants { get; private init; } = [];

    public IReadOnlyList<HlsRendition> Renditions { get; private init; } = [];

    public IReadOnlyList<HlsSegment> Segments { get; private init; } = [];

    public bool HasEndList { get; private init; }

    /// <summary>fMP4 initialization map (<c>#EXT-X-MAP</c>), or null if none.</summary>
    public HlsMap? Map { get; private init; }

    /// <summary>True if there is an <c>#EXT-X-MAP</c> (fMP4 container).</summary>
    public bool HasInitializationMap => Map is not null;

    /// <summary>Sequence number of the first segment in this playlist window.</summary>
    public long MediaSequence { get; private init; }

    /// <summary>Target segment duration (seconds), from <c>#EXT-X-TARGETDURATION</c>; 6 if absent.</summary>
    public int TargetDuration { get; private init; } = 6;

    /// <summary>The <c>#EXT-X-PLAYLIST-TYPE</c> value (VOD or EVENT), or null.</summary>
    public string? PlaylistType { get; private init; }

    /// <summary>True if this playlist does not end (no <c>#EXT-X-ENDLIST</c>), that is a live stream or a rolling window.</summary>
    public bool IsLive => !HasEndList;

    /// <summary>Parses playlist text. <paramref name="baseUri"/> is used to resolve relative URIs.</summary>
    public static M3u8Playlist Parse(string text, Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(baseUri);

        var lines = text
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        if (lines.Count == 0 || !lines[0].StartsWith("#EXTM3U", StringComparison.Ordinal))
        {
            throw new FormatException("Content is not a valid M3U8 playlist.");
        }

        var variants = new List<HlsVariant>();
        var renditions = new List<HlsRendition>();
        var segments = new List<HlsSegment>();
        var isMaster = false;
        var hasEnd = false;
        HlsMap? map = null;
        long mediaSequence = 0;
        int targetDuration = 6;
        string? playlistType = null;
        HlsKey? key = null;
        Dictionary<string, string>? pendingVariant = null;
        double pendingDuration = 0;
        HlsByteRange? pendingRange = null;

        // A range offset without an explicit value starts after the last range for the same source.
        var rangeEnds = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var line in lines.Skip(1))
        {
            if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal))
            {
                mediaSequence = long.Parse(line["#EXT-X-MEDIA-SEQUENCE:".Length..], CultureInfo.InvariantCulture);
            }
            else if (line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.Ordinal))
            {
                targetDuration = int.Parse(line["#EXT-X-TARGETDURATION:".Length..], CultureInfo.InvariantCulture);
            }
            else if (line.StartsWith("#EXT-X-PLAYLIST-TYPE:", StringComparison.Ordinal))
            {
                playlistType = line["#EXT-X-PLAYLIST-TYPE:".Length..].Trim();
            }
            else if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
            {
                isMaster = true;
                pendingVariant = ParseAttributes(line["#EXT-X-STREAM-INF:".Length..]);
            }
            else if (line.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal))
            {
                isMaster = true;
                var attributes = ParseAttributes(line["#EXT-X-MEDIA:".Length..]);
                renditions.Add(new HlsRendition(
                    attributes.GetValueOrDefault("TYPE", string.Empty).ToUpperInvariant(),
                    attributes.GetValueOrDefault("GROUP-ID", string.Empty),
                    attributes.GetValueOrDefault("NAME"),
                    attributes.TryGetValue("URI", out var mediaUri) ? new Uri(baseUri, mediaUri) : null,
                    attributes.GetValueOrDefault("DEFAULT", "NO").Equals("YES", StringComparison.OrdinalIgnoreCase)));
            }
            else if (line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal))
            {
                var attributes = ParseAttributes(line["#EXT-X-KEY:".Length..]);
                var method = attributes.GetValueOrDefault("METHOD", "NONE");
                key = method == "NONE"
                    ? null
                    : new HlsKey(
                        method,
                        attributes.TryGetValue("URI", out var keyUri) ? new Uri(baseUri, keyUri) : null,
                        attributes.TryGetValue("IV", out var iv) ? ParseIv(iv) : null);
            }
            else if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
            {
                var attributes = ParseAttributes(line["#EXT-X-MAP:".Length..]);
                if (attributes.TryGetValue("URI", out var mapUri))
                {
                    var range = attributes.TryGetValue("BYTERANGE", out var mapRange) ? ParseByteRange(mapRange) : null;
                    map ??= new HlsMap(new Uri(baseUri, mapUri), range);
                }
            }
            else if (line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.Ordinal))
            {
                pendingRange = ParseByteRange(line["#EXT-X-BYTERANGE:".Length..]);
            }
            else if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                var value = line["#EXTINF:".Length..];
                var comma = value.IndexOf(',');
                var durationText = comma >= 0 ? value[..comma] : value;
                pendingDuration = double.Parse(durationText, NumberStyles.Float, CultureInfo.InvariantCulture);
            }
            else if (line.StartsWith("#EXT-X-ENDLIST", StringComparison.Ordinal))
            {
                hasEnd = true;
            }
            else if (line.StartsWith('#'))
            {
                // Other tags are ignored.
            }
            else
            {
                var uri = new Uri(baseUri, line);
                if (pendingVariant is not null)
                {
                    var bandwidth = pendingVariant.TryGetValue("BANDWIDTH", out var bw)
                        ? long.Parse(bw, CultureInfo.InvariantCulture)
                        : 0;
                    variants.Add(new HlsVariant(
                        uri,
                        bandwidth,
                        pendingVariant.GetValueOrDefault("RESOLUTION"),
                        pendingVariant.GetValueOrDefault("AUDIO")));
                    pendingVariant = null;
                }
                else
                {
                    HlsByteRange? range = null;
                    if (pendingRange is not null)
                    {
                        var offset = pendingRange.Offset ?? (rangeEnds.TryGetValue(uri.AbsoluteUri, out var end) ? end : 0L);
                        range = new HlsByteRange(pendingRange.Length, offset);
                        rangeEnds[uri.AbsoluteUri] = offset + pendingRange.Length;
                        pendingRange = null;
                    }

                    segments.Add(new HlsSegment(uri, pendingDuration, mediaSequence + segments.Count, key, range));
                    pendingDuration = 0;
                }
            }
        }

        return new M3u8Playlist
        {
            IsMaster = isMaster,
            Variants = variants,
            Renditions = renditions,
            Segments = segments,
            HasEndList = hasEnd,
            Map = map,
            MediaSequence = mediaSequence,
            TargetDuration = targetDuration,
            PlaylistType = playlistType,
        };
    }

    /// <summary>Parses <c>NAME=VALUE</c> attribute pairs; quoted values may contain commas.</summary>
    internal static Dictionary<string, string> ParseAttributes(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        while (i < text.Length)
        {
            var eq = text.IndexOf('=', i);
            if (eq < 0) break;

            var name = text[i..eq].Trim();
            var j = eq + 1;
            string value;
            if (j < text.Length && text[j] == '"')
            {
                var close = text.IndexOf('"', j + 1);
                if (close < 0) close = text.Length;
                value = text[(j + 1)..close];
                i = Math.Min(text.Length, close + 1);
            }
            else
            {
                var comma = text.IndexOf(',', j);
                if (comma < 0) comma = text.Length;
                value = text[j..comma].Trim();
                i = comma;
            }

            if (name.Length > 0) result[name] = value;

            while (i < text.Length && (text[i] == ',' || text[i] == ' ')) i++;
        }

        return result;
    }

    /// <summary>Parses <c>n[@o]</c> (EXT-X-BYTERANGE). An invalid value throws <see cref="FormatException"/>.</summary>
    internal static HlsByteRange ParseByteRange(string value)
    {
        var parts = value.Trim().Split('@');
        if (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length <= 0)
        {
            throw new FormatException("Invalid EXT-X-BYTERANGE value.");
        }

        long? offset = null;
        if (parts.Length > 1)
        {
            if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedOffset) || parsedOffset < 0)
            {
                throw new FormatException("Invalid EXT-X-BYTERANGE offset.");
            }

            offset = parsedOffset;
        }

        return new HlsByteRange(length, offset);
    }

    private static byte[] ParseIv(string value)
    {
        var hex = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        var bytes = Convert.FromHexString(hex);
        if (bytes.Length != 16)
        {
            throw new FormatException("The AES-128 IV must be 16 bytes.");
        }

        return bytes;
    }
}
