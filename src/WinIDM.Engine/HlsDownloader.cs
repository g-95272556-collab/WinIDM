using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace WinIDM.Engine;

/// <summary>Variant preference when the master playlist lists several qualities.</summary>
public enum HlsVariantPreference
{
    /// <summary>Highest bandwidth.</summary>
    Highest,

    /// <summary>Lowest bandwidth.</summary>
    Lowest,
}

/// <summary>Progress of one HLS track: next sequence number, completed segment count and bytes written to the track file.</summary>
/// <param name="NextSequence">Media sequence number of the next segment to download.</param>
/// <param name="CompletedSegments">Number of segments already written and flushed to disk.</param>
/// <param name="Bytes">Valid bytes in the track file as of this checkpoint.</param>
public sealed record HlsTrackProgress(long NextSequence, int CompletedSegments, long Bytes);

/// <summary>
/// HLS resume state at a checkpoint. <see cref="Fingerprint"/> identifies the playlist (URL without query);
/// if it does not match, the download starts from scratch. <see cref="Audio"/> is null when audio is not separate.
/// </summary>
public sealed record HlsResumeState(string Fingerprint, HlsTrackProgress Video, HlsTrackProgress? Audio);

/// <summary>HLS download options.</summary>
public sealed class HlsDownloadOptions
{
    /// <summary>Converts .ts to .mp4 through an external ffmpeg found on PATH. Not relevant to fMP4 (already .mp4).</summary>
    public bool ConvertToMp4 { get; init; } = true;

    /// <summary>Explicit ffmpeg path; null means search PATH.</summary>
    public string? FfmpegPath { get; init; }

    public HlsVariantPreference VariantPreference { get; init; } = HlsVariantPreference.Highest;

    /// <summary>Number of segments downloaded concurrently (1-8).</summary>
    public int MaxConcurrentSegments { get; init; } = 4;

    /// <summary>
    /// Live stream (no ENDLIST): if true, record until canceled or ENDLIST appears.
    /// If false, only the segments in the current playlist are recorded, then the download ends (<see cref="HlsDownloadResult.IsLive"/> = true).
    /// </summary>
    public bool RecordLiveUntilStopped { get; init; }

    /// <summary>Reload interval for a live playlist. Null = half the target segment duration (1-10 s).</summary>
    public TimeSpan? LivePollInterval { get; init; }

    /// <summary>Resume state from a previous checkpoint. Null = start from scratch.</summary>
    public HlsResumeState? Resume { get; init; }

    /// <summary>
    /// Called after the file is prepared (initial checkpoint) and after each batch of segments is written and flushed.
    /// The caller (engine) stores it for resume.
    /// </summary>
    public Func<HlsResumeState, Task>? Checkpoint { get; init; }
}

/// <summary>HLS download result.</summary>
/// <param name="OutputPath">Final file (.mp4 if merged or converted; otherwise the .ts/.mp4 video track).</param>
/// <param name="AudioPath">Separate audio file if not merged; null if none.</param>
/// <param name="Merged">True if ffmpeg merged the audio and video.</param>
/// <param name="IsLive">True if the original playlist was a live stream.</param>
/// <param name="Bytes">Total bytes written across all tracks.</param>
/// <param name="Notes">Non-critical warnings (for example ffmpeg not found, live stream partially recorded).</param>
public sealed record HlsDownloadResult(
    string OutputPath,
    string? AudioPath,
    bool Merged,
    bool IsLive,
    long Bytes,
    IReadOnlyList<string> Notes);

/// <summary>
/// HLS stream downloader: picks a variant (if master), downloads segments (AES-128 decrypted; fMP4 with
/// <c>#EXT-X-MAP</c>; byte ranges <c>#EXT-X-BYTERANGE</c>), resumes from checkpoints, handles live streams, and
/// merges separate audio+video (<c>#EXT-X-MEDIA</c>) through <see cref="MediaMerger"/>.
/// SAMPLE-AES is supported for MPEG-TS segments (see <see cref="SampleAesDecryptor"/>); SAMPLE-AES fMP4 and other methods
/// are reported with a clear error 184.
/// </summary>
public sealed class HlsDownloader : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);
    private const int MaxConsecutiveReloadFailures = 5;

    private readonly HttpClient _client;
    private readonly HttpMessageHandler _handler;
    private readonly bool _ownsHandler;
    private readonly Action<HttpRequestMessage>? _configureRequest;

    /// <param name="handler">Transfer handler (optional). If null, the engine creates its own handler.</param>
    /// <param name="configureRequest">Request modifier (for example cookies, referer), called for every request.</param>
    public HlsDownloader(HttpMessageHandler? handler = null, Action<HttpRequestMessage>? configureRequest = null)
    {
        _ownsHandler = handler is null;
        _handler = handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false };
        _configureRequest = configureRequest;
        _client = new HttpClient(_handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>Downloads the stream and returns the output file path (.mp4 if merged or converted; otherwise the .ts/.mp4 video track).</summary>
    /// <param name="playlistUri">Playlist URL (master or media).</param>
    /// <param name="outputDirectory">Output folder.</param>
    /// <param name="baseName">Base file name without extension.</param>
    /// <param name="options">Options (defaults if null).</param>
    /// <param name="onProgress">Receives the total bytes written across all tracks.</param>
    public async Task<string> DownloadAsync(
        Uri playlistUri,
        string outputDirectory,
        string baseName,
        HlsDownloadOptions? options,
        Action<long>? onProgress,
        CancellationToken ct)
    {
        var result = await DownloadDetailedAsync(playlistUri, outputDirectory, baseName, options, onProgress, ct);
        return result.OutputPath;
    }

    /// <summary>Same as <see cref="DownloadAsync"/> but returns details (audio track, merge, live stream, warnings).</summary>
    public async Task<HlsDownloadResult> DownloadDetailedAsync(
        Uri playlistUri,
        string outputDirectory,
        string baseName,
        HlsDownloadOptions? options,
        Action<long>? onProgress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(playlistUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseName);
        options ??= new HlsDownloadOptions();

        Directory.CreateDirectory(outputDirectory);
        var notes = new List<string>();

        var (text, finalUri) = await GetTextAsync(playlistUri, ct);
        var playlist = M3u8Playlist.Parse(text, finalUri);
        Uri? audioUri = null;

        if (playlist.IsMaster)
        {
            if (playlist.Variants.Count == 0)
            {
                throw new TransferException(EngineErrorCodes.UnsupportedPlaylist, "The master playlist has no variants.", retryable: false);
            }

            var variant = options.VariantPreference == HlsVariantPreference.Lowest
                ? playlist.Variants.MinBy(v => v.Bandwidth)!
                : playlist.Variants.MaxBy(v => v.Bandwidth)!;

            if (variant.AudioGroup is { } group)
            {
                audioUri = playlist.Renditions
                    .Where(r => r.Type == "AUDIO" && r.GroupId == group && r.Uri is not null)
                    .OrderByDescending(r => r.IsDefault)
                    .FirstOrDefault()?.Uri;
            }

            (text, finalUri) = await GetTextAsync(variant.Uri, ct);
            playlist = M3u8Playlist.Parse(text, finalUri);
            if (playlist.IsMaster)
            {
                throw new TransferException(EngineErrorCodes.UnsupportedPlaylist, "Nested master playlists are not supported.", retryable: false);
            }
        }

        ValidateMediaPlaylist(playlist);

        M3u8Playlist? audioPlaylist = null;
        Uri? audioFinalUri = null;
        if (audioUri is not null)
        {
            (text, audioFinalUri) = await GetTextAsync(audioUri, ct);
            audioPlaylist = M3u8Playlist.Parse(text, audioFinalUri);
            if (audioPlaylist.IsMaster) audioPlaylist = null;
            else ValidateMediaPlaylist(audioPlaylist);
        }

        var fmp4 = playlist.Map is not null || audioPlaylist?.Map is not null;
        var extension = fmp4 ? ".mp4" : ".ts";
        var fingerprint = Fingerprint(finalUri, audioFinalUri, playlist.Map?.Uri);
        var resume = options.Resume is { } saved && saved.Fingerprint == fingerprint ? saved : null;
        if (options.Resume is not null && resume is null)
        {
            notes.Add("The playlist changed since the previous download; the download starts from scratch.");
        }

        var video = new TrackRun(
            index: 0,
            path: Path.Combine(outputDirectory, audioPlaylist is null ? baseName + extension : baseName + ".video" + extension),
            playlistUri: finalUri,
            playlist: playlist,
            fmp4: fmp4,
            progress: resume?.Video);

        TrackRun? audio = audioPlaylist is null
            ? null
            : new TrackRun(1, Path.Combine(outputDirectory, baseName + ".audio" + extension), audioFinalUri!, audioPlaylist, fmp4, resume?.Audio);

        var tracks = audio is null ? new[] { video } : new[] { video, audio };
        HlsResumeState CurrentState() => new(fingerprint, video.Progress, audio?.Progress);

        var keyCache = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var maxConcurrent = Math.Clamp(options.MaxConcurrentSegments, 1, 8);
        var liveInterval = options.LivePollInterval ?? TimeSpan.FromSeconds(Math.Clamp(playlist.TargetDuration / 2.0, 1, 10));
        var reportBytes = new Action(() => onProgress?.Invoke(tracks.Sum(t => t.Progress.Bytes)));

        try
        {
            if (options.Checkpoint is { } checkpoint) await checkpoint(CurrentState());

            foreach (var track in tracks)
            {
                await RunTrackAsync(track, keyCache, maxConcurrent, liveInterval, options, notes, CurrentState, reportBytes, ct);
            }
        }
        finally
        {
            foreach (var track in tracks) track.Dispose();
        }

        var live = playlist.IsLive || (audioPlaylist?.IsLive ?? false);
        if (live && !options.RecordLiveUntilStopped)
        {
            notes.Add("Live stream: only the current segments are recorded. Enable 'record until stopped' to record continuously.");
        }

        return await FinishAsync(video, audio, baseName, outputDirectory, options, notes, live);
    }

    public void Dispose()
    {
        _client.Dispose();
        if (_ownsHandler) _handler.Dispose();
    }

    // ------------------------------------------------------------------
    // Tracks and writing
    // ------------------------------------------------------------------

    /// <summary>State of one track during a download (video/muxed or audio).</summary>
    private sealed class TrackRun : IDisposable
    {
        public TrackRun(int index, string path, Uri playlistUri, M3u8Playlist playlist, bool fmp4, HlsTrackProgress? progress)
        {
            Index = index;
            Path = path;
            PlaylistUri = playlistUri;
            Playlist = playlist;
            Fmp4 = fmp4;
            Progress = progress ?? new HlsTrackProgress(playlist.Segments.Count > 0 ? playlist.Segments[0].Sequence : playlist.MediaSequence, 0, 0);
        }

        public int Index { get; }

        public string Path { get; }

        public Uri PlaylistUri { get; }

        public M3u8Playlist Playlist { get; set; }

        public bool Fmp4 { get; }

        public HlsTrackProgress Progress { get; set; }

        public FileStream? File { get; set; }

        public void Dispose()
        {
            File?.Dispose();
            File = null;
        }
    }

    /// <summary>
    /// Downloads one track: opens the file (truncates to the checkpoint bytes on resume), writes the fMP4 init if needed,
    /// downloads segments in batches, flushes and saves a checkpoint after each batch.
    /// </summary>
    private async Task RunTrackAsync(
        TrackRun track,
        Dictionary<string, byte[]> keyCache,
        int maxConcurrent,
        TimeSpan liveInterval,
        HlsDownloadOptions options,
        List<string> notes,
        Func<HlsResumeState> currentState,
        Action reportBytes,
        CancellationToken ct)
    {
        track.File = OpenTrackFile(track);

        if (track.Fmp4 && track.Progress.Bytes == 0 && track.Playlist.Map is { } map)
        {
            var init = await FetchBytesAsync(map.Uri, map.ByteRange, ct);
            await track.File.WriteAsync(init, ct);
            track.Progress = track.Progress with { Bytes = track.Progress.Bytes + init.Length };
            reportBytes();
            await CheckpointTrackAsync(track, options, currentState, ct);
        }

        var failures = 0;
        while (true)
        {
            var window = track.Playlist.Segments;
            if (window.Count > 0 && track.Progress.NextSequence < window[0].Sequence)
            {
                // Segments already left the live window: continue from the earliest available segment.
                notes.Add($"Some segments of track {track.Index} fell out of the live window.");
                track.Progress = track.Progress with { NextSequence = window[0].Sequence };
            }

            var pending = window.Where(s => s.Sequence >= track.Progress.NextSequence).ToList();
            for (var start = 0; start < pending.Count; start += maxConcurrent)
            {
                ct.ThrowIfCancellationRequested();
                var batch = pending.Skip(start).Take(maxConcurrent).ToArray();

                // Keys are prepared sequentially so the cache is never accessed concurrently.
                foreach (var segment in batch) await EnsureKeyAsync(segment, keyCache, ct);

                var parts = await Task.WhenAll(batch.Select(segment => FetchSegmentAsync(segment, keyCache, ct)));
                for (var i = 0; i < batch.Length; i++)
                {
                    await track.File!.WriteAsync(parts[i], ct);
                    track.Progress = new HlsTrackProgress(
                        batch[i].Sequence + 1,
                        track.Progress.CompletedSegments + 1,
                        track.Progress.Bytes + parts[i].Length);
                }

                reportBytes();
                await CheckpointTrackAsync(track, options, currentState, ct);
            }

            if (track.Playlist.HasEndList || !options.RecordLiveUntilStopped) break;

            // Live stream: wait, then reload the playlist for new segments.
            await Task.Delay(liveInterval, ct);
            try
            {
                var (text, uri) = await GetTextAsync(track.PlaylistUri, ct);
                track.Playlist = M3u8Playlist.Parse(text, uri);
                failures = 0;
            }
            catch (TransferException) when (++failures < MaxConsecutiveReloadFailures)
            {
                // Transient error while polling: try again on the next poll.
            }
        }

        await track.File!.FlushAsync(ct);
    }

    /// <summary>
    /// Opens the track file. If the checkpoint has bytes and the file is still at least that long, truncate the file
    /// to the checkpoint bytes (dropping the partial segment after the checkpoint) and continue from there. Otherwise start the track from scratch.
    /// </summary>
    private static FileStream OpenTrackFile(TrackRun track)
    {
        var resumable = track.Progress.Bytes > 0 && System.IO.File.Exists(track.Path) &&
                        new FileInfo(track.Path).Length >= track.Progress.Bytes;

        if (!resumable)
        {
            var first = track.Playlist.Segments.Count > 0 ? track.Playlist.Segments[0].Sequence : track.Playlist.MediaSequence;
            track.Progress = new HlsTrackProgress(first, 0, 0);
            return new FileStream(track.Path, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024, useAsync: true);
        }

        var stream = new FileStream(track.Path, FileMode.Open, FileAccess.Write, FileShare.Read, 64 * 1024, useAsync: true);
        stream.SetLength(track.Progress.Bytes);
        stream.Seek(0, SeekOrigin.End);
        return stream;
    }

    private static async Task CheckpointTrackAsync(TrackRun track, HlsDownloadOptions options, Func<HlsResumeState> currentState, CancellationToken ct)
    {
        if (options.Checkpoint is null) return;
        await track.File!.FlushAsync(ct);
        await options.Checkpoint(currentState());
    }

    /// <summary>Ensures the AES-128 key for the segment is cached (downloaded once).</summary>
    private async Task EnsureKeyAsync(HlsSegment segment, Dictionary<string, byte[]> keyCache, CancellationToken ct)
    {
        var key = segment.Key;
        if (key is null) return;

        if (!IsSupportedMethod(key.Method) || key.Uri is null)
        {
            throw UnsupportedEncryption(key.Method);
        }

        if (keyCache.ContainsKey(key.Uri.AbsoluteUri)) return;

        var bytes = await GetBytesAsync(key.Uri, ct);
        if (bytes.Length != 16)
        {
            throw new TransferException(EngineErrorCodes.UnsupportedPlaylist, "The AES-128 key must be 16 bytes.", retryable: false);
        }

        keyCache[key.Uri.AbsoluteUri] = bytes;
    }

    private async Task<byte[]> FetchSegmentAsync(HlsSegment segment, Dictionary<string, byte[]> keyCache, CancellationToken ct)
    {
        var data = await FetchBytesAsync(segment.Uri, segment.ByteRange, ct);
        if (segment.Key is null) return data;

        var key = keyCache[segment.Key.Uri!.AbsoluteUri];
        var iv = segment.Key.Iv ?? SequenceIv(segment.Sequence);
        try
        {
            return IsSampleAes(segment.Key.Method)
                ? SampleAesDecryptor.DecryptTransportStream(data, key, iv)
                : Decrypt(data, key, iv);
        }
        catch (CryptographicException ex)
        {
            throw new TransferException(EngineErrorCodes.UnsupportedPlaylist,
                $"Failed to decrypt segment {segment.Sequence}: {ex.Message}", retryable: false, inner: ex);
        }
    }

    /// <summary>Fetches segment bytes or a byte range. If the server replies 200 to a range, slice out the needed part.</summary>
    private async Task<byte[]> FetchBytesAsync(Uri uri, HlsByteRange? range, CancellationToken ct)
    {
        if (range is null) return await GetBytesAsync(uri, ct);

        var from = range.Offset ?? 0;
        var to = from + range.Length - 1;
        var (response, _) = await HttpTransfer.SendAsync(_client, uri, request =>
        {
            Decorate(request);
            request.Headers.Range = new RangeHeaderValue(from, to);
        }, RequestTimeout, ct);

        using (response)
        {
            var error = HttpTransfer.ClassifyStatus(response);
            if (error is not null) throw error;

            if (response.StatusCode == System.Net.HttpStatusCode.PartialContent)
            {
                var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                if (bytes.Length != range.Length)
                {
                    throw new TransferException(EngineErrorCodes.InvalidLength,
                        $"Byte range returned {bytes.Length} bytes, expected {range.Length}.", retryable: true);
                }

                return bytes;
            }

            // 200: the server sent the whole source; slice out the requested range.
            var whole = await response.Content.ReadAsByteArrayAsync(ct);
            if (whole.Length < to + 1)
            {
                throw new TransferException(EngineErrorCodes.InvalidLength,
                    "The source is shorter than the declared byte range.", retryable: false);
            }

            return whole[(int)from..(int)(to + 1)];
        }
    }

    // ------------------------------------------------------------------
    // Finishing: merge and convert
    // ------------------------------------------------------------------

    private async Task<HlsDownloadResult> FinishAsync(
        TrackRun video,
        TrackRun? audio,
        string baseName,
        string outputDirectory,
        HlsDownloadOptions options,
        List<string> notes,
        bool live)
    {
        var bytes = video.Progress.Bytes + (audio?.Progress.Bytes ?? 0);
        var merger = new MediaMerger(options.FfmpegPath);

        if (audio is null)
        {
            var output = video.Path;
            if (!video.Fmp4 && options.ConvertToMp4)
            {
                if (merger.IsAvailable)
                {
                    var mp4Path = Path.Combine(outputDirectory, baseName + ".mp4");
                    await merger.ConvertTsToMp4Async(video.Path, mp4Path);
                    File.Delete(video.Path);
                    output = mp4Path;
                }
                else
                {
                    notes.Add("ffmpeg not found; the .ts file is kept without conversion.");
                }
            }

            return new HlsDownloadResult(output, null, false, live, bytes, notes);
        }

        var finalPath = Path.Combine(outputDirectory, baseName + ".mp4");
        if (merger.IsAvailable)
        {
            await merger.MergeAsync(video.Path, audio.Path, finalPath);
            File.Delete(video.Path);
            File.Delete(audio.Path);
            return new HlsDownloadResult(finalPath, null, true, live, bytes, notes);
        }

        notes.Add("ffmpeg not found; audio and video are saved as two separate files.");
        return new HlsDownloadResult(video.Path, audio.Path, false, live, bytes, notes);
    }

    // ------------------------------------------------------------------
    // Fetching and validation
    // ------------------------------------------------------------------

    /// <summary>Accepts a downloadable media playlist; rejects unsupported encryption methods with a clear message.</summary>
    private static void ValidateMediaPlaylist(M3u8Playlist playlist)
    {
        if (playlist.Segments.Count == 0)
        {
            throw new TransferException(EngineErrorCodes.UnsupportedPlaylist, "The media playlist has no segments.", retryable: false);
        }

        foreach (var method in playlist.Segments.Select(s => s.Key?.Method).Where(m => m is not null).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (IsSampleAes(method!) && playlist.Map is not null)
            {
                throw new TransferException(EngineErrorCodes.UnsupportedPlaylist,
                    "SAMPLE-AES encryption for fMP4 (EXT-X-MAP) is not supported yet; only SAMPLE-AES MPEG-TS. Download canceled.",
                    retryable: false);
            }

            if (!IsSupportedMethod(method!))
            {
                throw UnsupportedEncryption(method!);
            }
        }
    }

    private static bool IsSupportedMethod(string method) =>
        method.Equals("AES-128", StringComparison.OrdinalIgnoreCase) || IsSampleAes(method);

    private static bool IsSampleAes(string method) =>
        method.Equals("SAMPLE-AES", StringComparison.OrdinalIgnoreCase);

    private static TransferException UnsupportedEncryption(string method) =>
        new(EngineErrorCodes.UnsupportedPlaylist,
            $"Encryption method {method} is not supported yet. Download canceled.",
            retryable: false);

    /// <summary>Playlist fingerprint: SHA-256 of the video/audio/map URLs without query, truncated to 40 hex characters.</summary>
    internal static string Fingerprint(Uri video, Uri? audio, Uri? map)
    {
        static string Strip(Uri u) => new UriBuilder(u) { Query = string.Empty, Fragment = string.Empty }.Uri.AbsoluteUri;

        var source = string.Join('|', Strip(video), audio is null ? string.Empty : Strip(audio), map is null ? string.Empty : Strip(map));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant()[..40];
    }

    /// <summary>The default AES-128 IV is the segment sequence number as a 128-bit big-endian integer.</summary>
    internal static byte[] SequenceIv(long sequence)
    {
        var iv = new byte[16];
        BinaryPrimitives.WriteInt64BigEndian(iv.AsSpan(8), sequence);
        return iv;
    }

    internal static byte[] Decrypt(byte[] data, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.KeySize = 128;
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(data, 0, data.Length);
    }

    private async Task<(string Text, Uri FinalUri)> GetTextAsync(Uri uri, CancellationToken ct)
    {
        var (response, finalUri) = await HttpTransfer.SendAsync(_client, uri, Decorate, RequestTimeout, ct);
        using (response)
        {
            var error = HttpTransfer.ClassifyStatus(response);
            if (error is not null) throw error;

            var text = await response.Content.ReadAsStringAsync(ct);
            return (text, finalUri);
        }
    }

    private async Task<byte[]> GetBytesAsync(Uri uri, CancellationToken ct)
    {
        var (response, _) = await HttpTransfer.SendAsync(_client, uri, Decorate, RequestTimeout, ct);
        using (response)
        {
            var error = HttpTransfer.ClassifyStatus(response);
            if (error is not null) throw error;

            return await response.Content.ReadAsByteArrayAsync(ct);
        }
    }

    private void Decorate(HttpRequestMessage request)
    {
        if (_configureRequest is null)
        {
            request.Headers.TryAddWithoutValidation("User-Agent", HttpTransfer.DefaultUserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "*/*");
            return;
        }

        _configureRequest(request);
    }
}
