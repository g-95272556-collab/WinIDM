using System.Globalization;
using WinIDM.Core.Contracts;
using WinIDM.Core.Models;

namespace WinIDM.Browser;

/// <summary>
/// Media detector based on MIME, file extension, and size. Downloads nothing
/// unless <see cref="ExpandPlaylistAsync"/> is called explicitly for a playlist.
/// User rules: an entry of 'mp4' or '.mp4' adds a type; an entry prefixed with '!' ignores
/// that type (for example '!jpg' or '!image/png'). Deny always wins. Per-host rules
/// take precedence over global rules.
/// </summary>
public sealed class MediaDetector : IMediaDetector
{
    /// <summary>Minimum size (10 KB) for ordinary media files. HLS/DASH playlists are not subject to this limit.</summary>
    public const long DefaultMinimumSizeBytes = 10 * 1024;

    private const int MaxPlaylistBytes = 4 * 1024 * 1024;
    private const int MaxVariantsForSizeEstimate = 20;

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.Ordinal)
    {
        "mp3", "m4a", "aac", "ogg", "oga", "opus", "flac", "wav", "wma", "weba",
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.Ordinal)
    {
        "mp4", "m4v", "webm", "mkv", "mov", "avi", "ts", "m2ts", "mpeg", "mpg", "flv", "3gp", "wmv", "ogv",
    };

    private static readonly HashSet<string> HlsMimes = new(StringComparer.Ordinal)
    {
        "application/vnd.apple.mpegurl", "application/x-mpegurl", "audio/mpegurl", "audio/x-mpegurl",
    };

    private static readonly string[] NonMediaMimePrefixes =
    {
        "text/", "image/", "font/", "application/javascript", "application/x-javascript", "application/json",
        "application/xml", "application/xhtml", "application/css",
    };

    private readonly HttpClient _http;
    private readonly object _gate = new();
    private readonly UserTypeRules _global = new();
    private readonly Dictionary<string, UserTypeRules> _perHost = new(StringComparer.OrdinalIgnoreCase);
    private long _minimumSizeBytes = DefaultMinimumSizeBytes;

    public MediaDetector(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient(new SocketsHttpHandler { UseCookies = false })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    public long MinimumSizeBytes
    {
        get => Volatile.Read(ref _minimumSizeBytes);
        set
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            Volatile.Write(ref _minimumSizeBytes, value);
        }
    }

    /// <summary>Replace the user type list. If <paramref name="pageHost"/> is null, the global rules are used.</summary>
    public void SetUserTypes(IEnumerable<string> entries, string? pageHost = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        lock (_gate)
        {
            var rules = pageHost is null ? _global : GetOrCreateHostRules(HostUtil.NormalizeHost(pageHost));
            rules.Clear();
            foreach (string entry in entries) rules.Add(entry);
        }
    }

    public IReadOnlyList<string> GetUserTypes(string? pageHost = null)
    {
        lock (_gate)
        {
            var rules = pageHost is null ? _global : _perHost.GetValueOrDefault(HostUtil.NormalizeHost(pageHost));
            return rules?.Describe() ?? Array.Empty<string>();
        }
    }

    public DetectedMedia? Inspect(string url, string? mimeType, long? contentLength, string? pageUrl)
    {
        if (!HostUtil.TryGetHttpUri(url, out var uri)) return null;

        string? pageHost = HostUtil.TryGetHttpUri(pageUrl, out var pageUri) ? HostUtil.NormalizeHost(pageUri.Host) : null;
        string ext = ExtensionOf(uri);
        string mime = NormalizeMime(mimeType);

        var decision = Decide(pageHost, ext, mime);
        if (decision == UserDecision.Deny) return null;

        DownloadKind? kind = Classify(ext, mime);
        if (kind is null && decision == UserDecision.Allow) kind = DownloadKind.Http;
        if (kind is null) return null;

        if (kind == DownloadKind.Http && contentLength is long length && length < MinimumSizeBytes)
            return null;

        return new DetectedMedia(
            url,
            pageUrl,
            FileNameOf(uri),
            mime.Length == 0 ? null : mime,
            contentLength,
            kind.Value,
            null);
    }

    /// <summary>
    /// Parse a single HLS or DASH playlist. For an HLS master, each variant becomes one result
    /// with a size estimate (if the variant playlist can be fetched). For an MPD, each representation.
    /// </summary>
    public async Task<IReadOnlyList<DetectedMedia>> ExpandPlaylistAsync(string playlistUrl, CancellationToken ct = default)
    {
        if (!HostUtil.TryGetHttpUri(playlistUrl, out var uri)) return Array.Empty<DetectedMedia>();

        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        Uri baseUri = response.RequestMessage?.RequestUri ?? uri;
        string text = await ReadTextLimitedAsync(response, MaxPlaylistBytes, ct).ConfigureAwait(false);

        string trimmed = text.TrimStart('﻿', ' ', '\t', '\r', '\n');
        if (trimmed.StartsWith("#EXTM3U", StringComparison.Ordinal))
            return await ExpandHlsAsync(text, baseUri, playlistUrl, ct).ConfigureAwait(false);

        if (trimmed.StartsWith('<') && trimmed.Contains("<MPD", StringComparison.Ordinal))
            return ExpandDash(text, playlistUrl);

        return Array.Empty<DetectedMedia>();
    }

    // ---------------------------------------------------------------------
    // Type decision

    private enum UserDecision { None, Allow, Deny }

    private UserDecision Decide(string? pageHost, string ext, string mime)
    {
        lock (_gate)
        {
            // Per-host rules also apply to subdomains (for example www.site.test inherits site.test).
            if (pageHost is not null)
            {
                int start = 0;
                while (true)
                {
                    string suffix = start == 0 ? pageHost : pageHost[start..];
                    if (_perHost.TryGetValue(suffix, out var hostRules))
                    {
                        var d = hostRules.Decide(ext, mime);
                        if (d != UserDecision.None) return d;
                    }
                    int dot = pageHost.IndexOf('.', start);
                    if (dot < 0) break;
                    start = dot + 1;
                }
            }
            return _global.Decide(ext, mime);
        }
    }

    private UserTypeRules GetOrCreateHostRules(string host)
    {
        if (!_perHost.TryGetValue(host, out var rules))
        {
            rules = new UserTypeRules();
            _perHost[host] = rules;
        }
        return rules;
    }

    /// <summary>Classification by MIME first, then by extension. Null means not media.</summary>
    internal static DownloadKind? Classify(string ext, string mime)
    {
        if (HlsMimes.Contains(mime) || ext == "m3u8") return DownloadKind.Hls;
        if (mime == "application/dash+xml" || ext == "mpd") return DownloadKind.Dash;
        if (mime.StartsWith("audio/", StringComparison.Ordinal) || mime.StartsWith("video/", StringComparison.Ordinal))
            return DownloadKind.Http;
        if (NonMediaMimePrefixes.Any(p => mime.StartsWith(p, StringComparison.Ordinal)))
            return null;
        if (AudioExtensions.Contains(ext) || VideoExtensions.Contains(ext))
            return DownloadKind.Http;
        return null;
    }

    internal static string NormalizeMime(string? mime)
    {
        if (string.IsNullOrWhiteSpace(mime)) return "";
        int semi = mime.IndexOf(';');
        string core = (semi >= 0 ? mime[..semi] : mime).Trim().ToLowerInvariant();
        return core == "application/octet-stream" ? "" : core;
    }

    internal static string ExtensionOf(Uri uri)
    {
        string path = uri.AbsolutePath;
        int slash = path.LastIndexOf('/');
        string name = slash >= 0 ? path[(slash + 1)..] : path;
        int dot = name.LastIndexOf('.');
        return dot < 0 ? "" : name[(dot + 1)..].ToLowerInvariant();
    }

    private static string? FileNameOf(Uri uri)
    {
        string path = uri.AbsolutePath;
        int slash = path.LastIndexOf('/');
        string name = Uri.UnescapeDataString(slash >= 0 ? path[(slash + 1)..] : path);
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    // ---------------------------------------------------------------------
    // Playlist expansion

    private async Task<IReadOnlyList<DetectedMedia>> ExpandHlsAsync(string text, Uri baseUri, string playlistUrl, CancellationToken ct)
    {
        var playlist = HlsPlaylist.Parse(text, baseUri);
        string? title = FileNameOf(baseUri);

        if (!playlist.IsMaster)
        {
            string quality = $"{playlist.Segments.Count} segments, {playlist.TotalDurationSeconds.ToString("F0", CultureInfo.InvariantCulture)} s";
            return new[]
            {
                new DetectedMedia(playlistUrl, null, title, "application/vnd.apple.mpegurl",
                    null, DownloadKind.Hls, quality),
            };
        }

        var results = new List<DetectedMedia>();
        var ordered = playlist.Variants.OrderByDescending(v => v.EffectiveBitsPerSecond).ToList();
        foreach (var variant in ordered)
        {
            long? size = ordered.Count <= MaxVariantsForSizeEstimate
                ? await EstimateHlsSizeAsync(variant, ct).ConfigureAwait(false)
                : null;

            string kbps = (variant.EffectiveBitsPerSecond / 1000).ToString(CultureInfo.InvariantCulture);
            string label = variant.AudioOnly
                ? "audio only"
                : variant.Width is int w && variant.Height is int h ? $"{w}x{h}" : "unknown";
            results.Add(new DetectedMedia(
                variant.Uri.AbsoluteUri,
                null,
                title,
                "application/vnd.apple.mpegurl",
                size,
                DownloadKind.Hls,
                $"{label} ({kbps} kbps)"));
        }
        return results;
    }

    private async Task<long?> EstimateHlsSizeAsync(HlsVariant variant, CancellationToken ct)
    {
        if (variant.Uri.Scheme is not ("http" or "https")) return null;
        try
        {
            using var response = await _http.GetAsync(variant.Uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            string body = await ReadTextLimitedAsync(response, MaxPlaylistBytes, ct).ConfigureAwait(false);
            var media = HlsPlaylist.Parse(body, response.RequestMessage?.RequestUri ?? variant.Uri);
            if (media.IsMaster || media.TotalDurationSeconds <= 0) return null;
            return (long)(variant.EffectiveBitsPerSecond / 8.0 * media.TotalDurationSeconds);
        }
        catch (Exception ex) when (ex is HttpRequestException or FormatException or InvalidDataException or TaskCanceledException)
        {
            return null;
        }
    }

    private List<DetectedMedia> ExpandDash(string text, string playlistUrl)
    {
        var info = DashManifest.Parse(text);
        string? title = FileNameOf(new Uri(playlistUrl));
        var list = new List<DetectedMedia>();
        foreach (var rep in info.Representations.OrderByDescending(r => r.Bandwidth))
        {
            long? size = info.DurationSeconds is double d && rep.Bandwidth > 0
                ? (long)(rep.Bandwidth / 8.0 * d)
                : null;
            list.Add(new DetectedMedia(
                playlistUrl,
                null,
                title,
                "application/dash+xml",
                size,
                DownloadKind.Dash,
                $"{rep.Quality} [id {rep.Id}]"));
        }
        return list;
    }

    private static async Task<string> ReadTextLimitedAsync(HttpResponseMessage response, int maxBytes, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + read > maxBytes) throw new InvalidDataException("Playlist exceeds the size limit.");
            ms.Write(buffer, 0, read);
        }
        return System.Text.Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    /// <summary>User type rules: a list of extensions and MIME types, with a '!' ignore marker.</summary>
    private sealed class UserTypeRules
    {
        private readonly HashSet<string> _extAllow = new(StringComparer.Ordinal);
        private readonly HashSet<string> _extDeny = new(StringComparer.Ordinal);
        private readonly HashSet<string> _mimeAllow = new(StringComparer.Ordinal);
        private readonly HashSet<string> _mimeDeny = new(StringComparer.Ordinal);
        private readonly List<string> _entries = new();

        public void Clear()
        {
            _extAllow.Clear();
            _extDeny.Clear();
            _mimeAllow.Clear();
            _mimeDeny.Clear();
            _entries.Clear();
        }

        public void Add(string raw)
        {
            string entry = raw.Trim().ToLowerInvariant();
            bool deny = entry.StartsWith('!');
            if (deny) entry = entry[1..].Trim();
            if (entry.Length == 0) return;

            if (entry.Contains('/'))
            {
                if (entry.Any(c => char.IsWhiteSpace(c)) || entry.Length > 127) return;
                (deny ? _mimeDeny : _mimeAllow).Add(entry);
            }
            else
            {
                entry = entry.TrimStart('.');
                if (entry.Length == 0 || entry.Length > 10 || !entry.All(char.IsLetterOrDigit)) return;
                (deny ? _extDeny : _extAllow).Add(entry);
            }
            _entries.Add((deny ? "!" : "") + entry);
        }

        public UserDecision Decide(string ext, string mime)
        {
            if (_extDeny.Contains(ext) || (mime.Length > 0 && _mimeDeny.Contains(mime))) return UserDecision.Deny;
            if (_extAllow.Contains(ext) || (mime.Length > 0 && _mimeAllow.Contains(mime))) return UserDecision.Allow;
            return UserDecision.None;
        }

        public IReadOnlyList<string> Describe() => _entries.ToArray();
    }
}
