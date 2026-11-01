using System.IO;
using System.Text.RegularExpressions;

namespace WinIDM.App.Services;

/// <summary>Size, speed and ETA formatting for display.</summary>
public static class Fmt
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    public static string Bytes(long bytes)
    {
        if (bytes < 0) return "-";
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < Units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : $"{v:0.##} {Units[u]}";
    }

    /// <summary>Speed in bytes per second, or bits per second if <paramref name="bits"/> is true.</summary>
    public static string Speed(double bytesPerSecond, bool bits = false)
    {
        if (bytesPerSecond <= 0 || double.IsNaN(bytesPerSecond)) return "-";
        if (!bits) return Bytes((long)bytesPerSecond) + "/s";
        double bps = bytesPerSecond * 8;
        string[] u = { "bit", "Kb", "Mb", "Gb" };
        int i = 0;
        while (bps >= 1000 && i < u.Length - 1) { bps /= 1000; i++; }
        return $"{bps:0.##} {u[i]}/s";
    }

    public static string Eta(TimeSpan? eta)
    {
        if (eta is null) return "-";
        var t = eta.Value;
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays} d {t.Hours} h";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours} h {t.Minutes} min";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes} min {t.Seconds} s";
        return $"{Math.Max(0, t.Seconds)} s";
    }
}

/// <summary>File type filter by extension (list from 01/03).</summary>
public enum FileTypeFilter { All, Audio, Video, Image, Document, Archive, App, Other }

public static class FileTypes
{
    public static string Label(FileTypeFilter f) => f switch
    {
        FileTypeFilter.All => "All types",
        FileTypeFilter.Audio => "Audio",
        FileTypeFilter.Video => "Video",
        FileTypeFilter.Image => "Image",
        FileTypeFilter.Document => "Document",
        FileTypeFilter.Archive => "Archive",
        FileTypeFilter.App => "Application",
        _ => "Other",
    };

    private static readonly Dictionary<string, FileTypeFilter> Map = BuildMap();

    private static Dictionary<string, FileTypeFilter> BuildMap()
    {
        var d = new Dictionary<string, FileTypeFilter>(StringComparer.OrdinalIgnoreCase);
        void Add(FileTypeFilter f, string exts) { foreach (var e in exts.Split(' ')) d[e] = f; }
        Add(FileTypeFilter.Audio, "mp3 wav flac aac ogg m4a opus wma");
        Add(FileTypeFilter.Video, "mp4 mkv avi mov webm flv wmv m4v ts 3gp m3u8 mpd");
        Add(FileTypeFilter.Image, "jpg jpeg png gif bmp webp svg heic tif tiff");
        Add(FileTypeFilter.Document, "pdf doc docx xls xlsx ppt pptx txt rtf odt ods csv epub");
        Add(FileTypeFilter.Archive, "zip rar 7z tar gz bz2 xz iso");
        Add(FileTypeFilter.App, "exe msi msix appx apk dmg deb");
        return d;
    }

    public static FileTypeFilter Classify(string fileName)
    {
        var ext = Path.GetExtension(fileName).TrimStart('.');
        return Map.TryGetValue(ext, out var f) ? f : FileTypeFilter.Other;
    }
}

/// <summary>URL handling: validation, file names and protocol scheme parsing.</summary>
public static class UrlHelper
{
    public static bool IsHttp(string? s) =>
        Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https");

    public static bool IsMagnet(string? s) =>
        s is not null && s.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase);

    public static bool IsDownloadable(string? s) => IsHttp(s) || IsMagnet(s);

    /// <summary>Take only the first line of the text (R-47) and trim whitespace.</summary>
    public static string? FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        using var r = new StringReader(text);
        return r.ReadLine()?.Trim();
    }

    /// <summary>
    /// Extracts a URL from a protocol argument such as <c>idmdownload:https://...</c> or
    /// <c>1dmdownload://https://...</c>. Returns the original argument if it is not one of our protocols.
    /// </summary>
    public static string? ExtractFromArgument(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg)) return null;
        var a = arg.Trim().Trim('"');
        foreach (var scheme in new[] { "1dmdownload:", "idmdownload:" })
        {
            if (a.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            {
                var rest = Uri.UnescapeDataString(a.Substring(scheme.Length)).TrimStart('/');
                int i = rest.IndexOf("http", StringComparison.OrdinalIgnoreCase);
                if (i >= 0) rest = rest.Substring(i);
                return rest;
            }
        }
        return a;
    }

    public static string FileNameFromUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return "download";
        var name = Uri.UnescapeDataString(Path.GetFileName(u.AbsolutePath));
        if (string.IsNullOrWhiteSpace(name)) return "download";
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '-');
        return name.Length > 180 ? name[..180] : name;
    }

    public static string? ExtensionOf(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
        var ext = Path.GetExtension(u.AbsolutePath).TrimStart('.');
        return string.IsNullOrEmpty(ext) ? null : ext.ToLowerInvariant();
    }

    private static readonly Regex UrlPattern = new(@"[a-zA-Z][a-zA-Z0-9+.-]*://[^\s""'<>]+", RegexOptions.Compiled);

    /// <summary>Scrub URLs in log text so no full link (and query/token) is written.</summary>
    public static string Redact(string text) => UrlPattern.Replace(text, "[url]");
}
