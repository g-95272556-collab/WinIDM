using System.Globalization;
using System.Text;
using WinIDM.Core.Models;

namespace WinIDM.Browser;

/// <summary>
/// URL rules for media captured from the embedded browser.
/// <para>
/// The browser reports every media response it sees. Two problems follow from that:
/// a progressive stream can be requested dozens of times with different byte ranges (YouTube's
/// <c>googlevideo.com/videoplayback</c>), and the URL alone rarely carries a usable file name.
/// This class normalises the URL for a full-file download, builds a stable de-duplication key,
/// and derives a sensible file name from the page title, MIME type and URL.
/// </para>
/// The rules are deliberately conservative: only byte-range query parameters are removed (on any host, plus
/// YouTube's <c>rn</c>/<c>rbuf</c>), and the rest of the URL is kept byte-for-byte so signed/tokenised URLs keep working.
/// </summary>
public static class MediaCapture
{
    /// <summary>Query keys that can describe the byte range a player asked for, not the media itself.</summary>
    private static readonly string[] RangeQueryKeys = { "range", "bytestart", "byteend" };

    /// <summary>Extra range keys used only by YouTube's googlevideo hosts.</summary>
    private static readonly string[] GooglevideoRangeKeys = { "rn", "rbuf" };

    /// <summary>A value that really is a byte range ("0-1048575" or "bytes=0-1048575").</summary>
    private static readonly System.Text.RegularExpressions.Regex ByteRangeValue =
        new(@"^(bytes=)?\d+-\d*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly IReadOnlyDictionary<string, string> MimeExtensions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["video/mp4"] = "mp4",
            ["video/webm"] = "webm",
            ["video/x-matroska"] = "mkv",
            ["video/quicktime"] = "mov",
            ["video/mp2t"] = "ts",
            ["video/3gpp"] = "3gp",
            ["audio/mp4"] = "m4a",
            ["audio/mpeg"] = "mp3",
            ["audio/webm"] = "weba",
            ["audio/ogg"] = "ogg",
            ["audio/aac"] = "aac",
            ["audio/opus"] = "opus",
            ["audio/flac"] = "flac",
            ["audio/wav"] = "wav",
            ["audio/x-wav"] = "wav",
            ["audio/wave"] = "wav",
            ["audio/x-m4a"] = "m4a",
            ["video/x-flv"] = "flv",
            ["video/avi"] = "avi",
            ["video/x-msvideo"] = "avi",
            ["application/vnd.apple.mpegurl"] = "m3u8",
            ["application/x-mpegurl"] = "m3u8",
            ["application/dash+xml"] = "mpd",
        };

    private static readonly HashSet<string> KnownExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "m4v", "webm", "mkv", "mov", "avi", "ts", "m2ts", "mpeg", "mpg", "flv", "3gp", "wmv", "ogv",
        "mp3", "m4a", "aac", "ogg", "oga", "opus", "flac", "wav", "wma", "weba", "m3u8", "mpd",
    };

    /// <summary>
    /// Rewrite a playable-stream URL into a full-file URL. <c>range</c>, <c>bytestart</c> and <c>byteend</c> are removed
    /// when their value really is a byte range (so a signed parameter that merely reuses the name keeps working), and
    /// YouTube's <c>googlevideo.com/videoplayback</c> links additionally lose <c>rn</c>/<c>rbuf</c>. Signatures
    /// (<c>sig</c>, <c>n</c>), tokens and <c>itag</c>/<c>mime</c> are kept untouched. Any other URL is returned unchanged.
    /// </summary>
    public static string NormalizeForDownload(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        if (uri.Scheme is not ("http" or "https")) return url;

        bool youTube = IsGooglevideoStream(uri);

        // Work on the original string so the kept parameters (signatures included) stay byte-for-byte identical;
        // re-serialising through Uri/UriBuilder can re-normalise percent-encoding and break a signed URL.
        int hash = url.IndexOf('#');
        var beforeFragment = hash >= 0 ? url[..hash] : url;
        var fragment = hash >= 0 ? url[hash..] : "";
        int question = beforeFragment.IndexOf('?');
        if (question < 0) return url;

        var pieces = beforeFragment[(question + 1)..].Split('&');
        var kept = pieces.Where(p => p.Length > 0 && !ShouldStripRangeParameter(SplitQueryPiece(p), youTube)).ToList();
        if (kept.Count == pieces.Count(p => p.Length > 0)) return url;

        var query = string.Join("&", kept);
        return beforeFragment[..question] + (query.Length > 0 ? "?" + query : "") + fragment;
    }

    private static bool ShouldStripRangeParameter(KeyValuePair<string, string> part, bool youTube)
    {
        if (youTube && GooglevideoRangeKeys.Contains(part.Key, StringComparer.OrdinalIgnoreCase)) return true;
        if (!RangeQueryKeys.Contains(part.Key, StringComparer.OrdinalIgnoreCase)) return false;

        // bytestart/byteend are plain byte offsets; "range" is stripped only when it really is a byte range so a
        // signed parameter that merely reuses the name keeps working.
        bool isStartOrEnd = string.Equals(part.Key, "bytestart", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(part.Key, "byteend", StringComparison.OrdinalIgnoreCase);
        return isStartOrEnd ? part.Value.All(char.IsDigit) : ByteRangeValue.IsMatch(part.Value);
    }

    /// <summary>
    /// File name advertised by a <c>Content-Disposition</c> header (RFC 6266/5987), or null. Handles
    /// <c>filename*=UTF-8''name%20x.mp4</c>, <c>filename="name.mp4"</c> and the unquoted form.
    /// </summary>
    public static string? FileNameFromContentDisposition(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;

        string? extended = null, plain = null;
        // Split on ';' outside quotes so filename="Tom; Jerry.mp4" stays one value.
        foreach (var rawPiece in ParameterSeparator.Split(header))
        {
            var piece = rawPiece.Trim();
            if (piece.StartsWith("filename*=", StringComparison.OrdinalIgnoreCase))
            {
                extended = piece["filename*=".Length..].Trim().Trim('"');
            }
            else if (piece.StartsWith("filename=", StringComparison.OrdinalIgnoreCase))
            {
                plain = piece["filename=".Length..].Trim().Trim('"');
            }
        }

        var value = extended ?? plain;
        if (string.IsNullOrWhiteSpace(value)) return null;

        if (extended is not null)
        {
            // charset'language'percent-encoded-name; only the extended form is percent-encoded (RFC 5987).
            int first = value.IndexOf('\'');
            int second = first >= 0 ? value.IndexOf('\'', first + 1) : -1;
            if (second > first) value = value[(second + 1)..];
            value = Uri.UnescapeDataString(value);
        }

        var name = value.Replace('\\', '/');
        int slash = name.LastIndexOf('/');
        if (slash >= 0 && slash < name.Length - 1) name = name[(slash + 1)..];
        name = Sanitize(name);
        return name.Length == 0 ? null : name;
    }

    /// <summary>Stable identity of a captured item: range variants of the same stream collapse into one entry.</summary>
    public static string DedupeKey(string url) => NormalizeForDownload(url);

    /// <summary>
    /// File name for a captured item, including a suitable extension. Precedence follows IDM+: the
    /// <c>Content-Disposition</c> name, then a <c>?title=</c>/<c>?filename=</c> query value, then the last path segment
    /// when it already looks like a media file, then the page title, then the media title, then a stable fallback.
    /// The server name can be given either as the raw header (<paramref name="contentDisposition"/>) or as a name
    /// already parsed from it (<paramref name="sourceFileName"/>, as stored on <see cref="DetectedMedia"/>).
    /// </summary>
    public static string SuggestedFileName(string url, string? pageTitle, string? mediaTitle, string? mimeType,
        string? contentDisposition = null, string? sourceFileName = null)
    {
        var extension = ExtensionFromMime(mimeType) ?? ExtensionFromUrl(url)
                        ?? (IsAudioMime(mimeType) ? "m4a" : "mp4");

        var fromHeader = FileNameFromContentDisposition(contentDisposition)
                         ?? (string.IsNullOrWhiteSpace(sourceFileName) ? null : Sanitize(sourceFileName));
        if (fromHeader is not null) return WithExtension(fromHeader, extension);

        var fromQuery = QueryTitle(url);
        if (fromQuery is not null) return WithExtension(fromQuery, extension);

        var pathName = LastPathSegment(url);
        if (pathName is not null && HasKnownExtension(pathName)) return Sanitize(pathName);

        var candidate = !string.IsNullOrWhiteSpace(pageTitle) ? pageTitle
            : !string.IsNullOrWhiteSpace(mediaTitle) ? mediaTitle
            : null;

        return WithExtension(candidate ?? DefaultStem(url), extension);
    }

    private static string WithExtension(string name, string extension)
    {
        var clean = Sanitize(name);
        return HasKnownExtension(clean) ? clean : clean + "." + extension;
    }

    /// <summary>A <c>title</c> or <c>filename</c> query parameter, which many players already carry.</summary>
    private static string? QueryTitle(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        foreach (var part in ParseQuery(uri))
        {
            if (!string.Equals(part.Key, "title", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(part.Key, "filename", StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.IsNullOrWhiteSpace(part.Value)) continue;
            string value;
            try
            {
                value = Uri.UnescapeDataString(part.Value.Replace('+', ' '));
            }
            catch (UriFormatException)
            {
                value = part.Value;
            }
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }

    /// <summary>True when the URL is a YouTube media stream (used for diagnostics and UI hints).</summary>
    public static bool IsYouTubeSource(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsGooglevideoStream(uri);

    private static bool IsGooglevideoStream(Uri uri)
    {
        bool isGooglevideoHost = uri.Host.EndsWith(".googlevideo.com", StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(uri.Host, "googlevideo.com", StringComparison.OrdinalIgnoreCase);
        return isGooglevideoHost && uri.AbsolutePath.Contains("videoplayback", StringComparison.Ordinal);
    }

    private static bool IsAudioMime(string? mimeType) =>
        mimeType?.TrimStart().StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true;

    private static string? ExtensionFromMime(string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType)) return null;
        int semi = mimeType.IndexOf(';');
        var core = (semi >= 0 ? mimeType[..semi] : mimeType).Trim().ToLowerInvariant();
        return MimeExtensions.GetValueOrDefault(core);
    }

    private static string? ExtensionFromUrl(string url)
    {
        var segment = LastPathSegment(url);
        if (segment is null) return null;
        int dot = segment.LastIndexOf('.');
        if (dot < 0 || dot == segment.Length - 1) return null;
        var ext = segment[(dot + 1)..];
        return KnownExtensions.Contains(ext) ? ext.ToLowerInvariant() : null;
    }

    private static bool HasKnownExtension(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot > 0 && dot < name.Length - 1 && KnownExtensions.Contains(name[(dot + 1)..]);
    }

    private static string? LastPathSegment(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var path = uri.AbsolutePath;
        int slash = path.LastIndexOf('/');
        var name = Uri.UnescapeDataString(slash >= 0 ? path[(slash + 1)..] : path);
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>Base name when the URL and the page give nothing useful: "video-itag" for YouTube, otherwise a hash.</summary>
    private static string DefaultStem(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsGooglevideoStream(uri))
        {
            var parts = ParseQuery(uri);
            var itag = parts.Where(p => string.Equals(p.Key, "itag", StringComparison.OrdinalIgnoreCase))
                            .Select(p => p.Value)
                            .FirstOrDefault();
            if (!string.IsNullOrEmpty(itag) && itag.All(char.IsLetterOrDigit)) return "video-" + itag;
            return "video";
        }
        return "media-" + ShortHash(url);
    }

    private static string ShortHash(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..8].ToLower(CultureInfo.InvariantCulture);
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(invalid.Contains(ch) || char.IsControl(ch) ? ' ' : ch);
        var clean = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim().TrimEnd('.');
        if (clean.Length == 0) clean = "media";
        if (clean.Length <= MaxNameLength) return clean;

        // Clamp the stem only, so a long name keeps its media extension.
        int dot = clean.LastIndexOf('.');
        if (dot > 0 && clean.Length - dot <= 6 && KnownExtensions.Contains(clean[(dot + 1)..]))
        {
            var ext = clean[dot..];
            return clean[..Math.Min(dot, MaxNameLength - ext.Length)].TrimEnd(' ', '.') + ext;
        }
        return clean[..MaxNameLength].TrimEnd();
    }

    private const int MaxNameLength = 120;

    private static readonly System.Text.RegularExpressions.Regex ParameterSeparator =
        new(";(?=(?:[^\"]*\"[^\"]*\")*[^\"]*$)", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static KeyValuePair<string, string> SplitQueryPiece(string piece)
    {
        int eq = piece.IndexOf('=');
        return eq < 0
            ? new KeyValuePair<string, string>(Uri.UnescapeDataString(piece), "")
            : new KeyValuePair<string, string>(Uri.UnescapeDataString(piece[..eq]), piece[(eq + 1)..]);
    }

    private static List<KeyValuePair<string, string>> ParseQuery(Uri uri)
    {
        var list = new List<KeyValuePair<string, string>>();
        var query = uri.Query;
        if (query.Length <= 1) return list;
        foreach (var piece in query[1..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            list.Add(SplitQueryPiece(piece));
        return list;
    }
}
