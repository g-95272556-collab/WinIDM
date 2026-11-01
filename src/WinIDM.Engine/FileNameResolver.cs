using System.Globalization;
using System.Net.Http.Headers;

namespace WinIDM.Engine;

/// <summary>
/// Download file name resolution and name conflict handling. Priority order (01 section 6.1):
/// user-supplied name, <c>filename*</c>, <c>filename</c>, HTML title, then the last URL segment.
/// </summary>
public static class FileNameResolver
{
    private static readonly Dictionary<string, string> MimeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["video/mp4"] = ".mp4",
        ["video/webm"] = ".webm",
        ["video/x-matroska"] = ".mkv",
        ["video/x-msvideo"] = ".avi",
        ["video/quicktime"] = ".mov",
        ["video/mp2t"] = ".ts",
        ["application/vnd.apple.mpegurl"] = ".m3u8",
        ["application/x-mpegurl"] = ".m3u8",
        ["audio/mpeg"] = ".mp3",
        ["audio/mp4"] = ".m4a",
        ["audio/ogg"] = ".ogg",
        ["audio/wav"] = ".wav",
        ["audio/x-wav"] = ".wav",
        ["audio/flac"] = ".flac",
        ["image/jpeg"] = ".jpg",
        ["image/png"] = ".png",
        ["image/gif"] = ".gif",
        ["image/webp"] = ".webp",
        ["image/svg+xml"] = ".svg",
        ["application/pdf"] = ".pdf",
        ["application/zip"] = ".zip",
        ["application/x-rar-compressed"] = ".rar",
        ["application/x-7z-compressed"] = ".7z",
        ["application/gzip"] = ".gz",
        ["application/x-tar"] = ".tar",
        ["application/vnd.microsoft.portable-executable"] = ".exe",
        ["application/x-msdownload"] = ".exe",
        ["application/x-msi"] = ".msi",
        ["application/json"] = ".json",
        ["text/html"] = ".html",
        ["text/plain"] = ".txt",
        ["text/csv"] = ".csv",
    };

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private const int MaxNameLength = 200;

    /// <summary>
    /// Determines the final file name. <paramref name="userFileName"/> takes precedence if not empty;
    /// the MIME extension is added if the name has no extension (or that extension does not match for a user-supplied name).
    /// </summary>
    public static string Resolve(Uri uri, string? contentDisposition, string? contentType, string? userFileName = null, string? htmlTitle = null)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var mimeExtension = ExtensionFromMime(contentType);

        if (!string.IsNullOrWhiteSpace(userFileName))
        {
            var userName = Sanitize(userFileName) ?? "download";
            if (mimeExtension is not null && !userName.EndsWith(mimeExtension, StringComparison.OrdinalIgnoreCase))
            {
                userName += mimeExtension;
            }

            return userName;
        }

        var name = FromContentDisposition(contentDisposition)
                   ?? (IsHtml(contentType) ? Sanitize(htmlTitle) : null)
                   ?? FromUrl(uri)
                   ?? "download";

        name = Sanitize(name) ?? "download";

        if (Path.GetExtension(name).Length == 0 && mimeExtension is not null)
        {
            name += mimeExtension;
        }

        return name;
    }

    /// <summary>Extracts the name from the <c>Content-Disposition</c> header (prefers <c>filename*</c>).</summary>
    public static string? FromContentDisposition(string? headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue)) return null;

        if (!ContentDispositionHeaderValue.TryParse(headerValue, out var disposition) || disposition is null)
        {
            return null;
        }

        // FileName keeps the quotes when the header value is quoted; strip them.
        var name = disposition.FileNameStar ?? disposition.FileName?.Trim().Trim('"');
        return Sanitize(name);
    }

    /// <summary>Takes the last URL path segment (without query, fragment, or the part after ';').</summary>
    public static string? FromUrl(Uri uri)
    {
        var path = uri.AbsolutePath;
        var slash = path.LastIndexOf('/');
        var last = slash >= 0 ? path[(slash + 1)..] : path;
        var semicolon = last.IndexOf(';');
        if (semicolon >= 0) last = last[..semicolon];
        last = Uri.UnescapeDataString(last);
        return string.IsNullOrWhiteSpace(last) ? null : Sanitize(last);
    }

    /// <summary>Maps a MIME type to a file extension (including the dot), or null if unknown.</summary>
    public static string? ExtensionFromMime(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return null;
        var mime = contentType.Split(';')[0].Trim();
        return MimeExtensions.TryGetValue(mime, out var ext) ? ext : null;
    }

    /// <summary>
    /// Cleans a name so it is safe on Windows: replaces path separators and invalid characters with '-',
    /// strips trailing dots/spaces, avoids reserved device names, and limits the length.
    /// </summary>
    public static string? Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => c == '/' || c == '\\' || Array.IndexOf(invalid, c) >= 0 || c < ' ' ? '-' : c).ToArray();
        var cleaned = new string(chars).Trim().TrimEnd('.', ' ');
        if (cleaned.Length == 0) return null;

        var stem = Path.GetFileNameWithoutExtension(cleaned);
        if (ReservedNames.Contains(stem))
        {
            cleaned = "_" + cleaned;
        }

        if (cleaned.Length > MaxNameLength)
        {
            var ext = Path.GetExtension(cleaned);
            if (ext.Length > 20) ext = string.Empty;
            cleaned = cleaned[..(MaxNameLength - ext.Length)] + ext;
        }

        return cleaned;
    }

    /// <summary>
    /// Returns a name that does not yet exist in <paramref name="directory"/> by adding " (n)" before the extension.
    /// </summary>
    public static string MakeUnique(string directory, string fileName)
    {
        if (!File.Exists(Path.Combine(directory, fileName))) return fileName;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var n = 1; n < int.MaxValue; n++)
        {
            var candidate = string.Create(CultureInfo.InvariantCulture, $"{stem} ({n}){ext}");
            if (!File.Exists(Path.Combine(directory, candidate))) return candidate;
        }

        throw new IOException("No unique name available.");
    }

    private static bool IsHtml(string? contentType) =>
        contentType is not null && contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase);
}
