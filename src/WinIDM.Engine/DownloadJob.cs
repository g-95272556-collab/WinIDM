using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using WinIDM.Core.Models;

namespace WinIDM.Engine;

/// <summary>Reason a running download is being stopped.</summary>
internal enum StopReason
{
    None,
    Pause,
    Remove,

    /// <summary>Wi-Fi only is enabled but the connection is mobile data.</summary>
    WifiLost,
    Shutdown,

    /// <summary>No Internet connection (<see cref="INetworkPolicy.IsOnline"/> = false).</summary>
    Offline,

    /// <summary>Metered connection and pause on metered is enabled.</summary>
    Metered,

    /// <summary>Metered connection and the daily/monthly data limit has been reached.</summary>
    QuotaExceeded,

    /// <summary>Running on battery below the battery limit (<see cref="SettingKeys.BatteryLimit"/>).</summary>
    LowBattery,
}

/// <summary>
/// One running download: probe, segment planning, direct writes at offsets,
/// resume (Range + If-Range), retries, checksum verification and progress checkpointing.
/// </summary>
internal sealed class DownloadJob : IDisposable
{
    private const int BufferSize = 64 * 1024;
    private const int SegmentRetries = 2;
    private const int MaxRestarts = 2;
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(10);

    private readonly DownloadEngine _engine;
    private readonly DownloadItem _item;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _checkpointGate = new(1, 1);
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TokenBucket _itemLimiter;

    private List<SegmentInfo> _segments = [];
    private long _downloaded;
    private StopReason _stopReason;
    private HttpMessageHandler? _transport;
    private bool _ownsTransport;
    private HttpClient? _client;
    private SafeFileHandle? _file;
    private bool _rangeMode;
    private int _workerLimit = 1;
    private long _lastCheckpointTicks;
    private long _sampleBytes;
    private long _sampleTimestamp;
    private double _speed;

    public DownloadJob(DownloadEngine engine, DownloadItem item)
    {
        _engine = engine;
        _item = item;
        _downloaded = item.DownloadedBytes;
        _itemLimiter = new TokenBucket(item.SpeedLimitBytesPerSec);
        _sampleTimestamp = Stopwatch.GetTimestamp();
        _sampleBytes = _downloaded;
        _lastCheckpointTicks = Environment.TickCount64;
    }

    public long Id => _item.Id;

    /// <summary>Direct reference to the download record (updated by the engine).</summary>
    public DownloadItem Item => _item;

    /// <summary>
    /// Completes once the engine has removed the job from the active list (see <see cref="MarkFinished"/>).
    /// That way waiters (pause, resume, change link) never see a job that is "still running" after it finished.
    /// </summary>
    public Task Completion => _completion.Task;

    /// <summary>True if a stop was requested (pause, remove, Wi-Fi lost) but the job has not been removed yet.</summary>
    public bool IsStopping
    {
        get
        {
            lock (_gate) return _stopReason != StopReason.None;
        }
    }

    /// <summary>Called by the engine after the job is removed from <c>_jobs</c>.</summary>
    internal void MarkFinished() => _completion.TrySetResult();

    public TokenBucket ItemLimiter => _itemLimiter;

    public long Downloaded
    {
        get
        {
            lock (_gate) return _downloaded;
        }
    }

    /// <summary>Signals a stop. Only the first call sets the reason.</summary>
    public void Stop(StopReason reason)
    {
        lock (_gate)
        {
            if (_stopReason == StopReason.None) _stopReason = reason;
        }

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already finished; nothing to do.
        }
    }

    /// <summary>Speed budget (bytes per second) estimated since the last sample.</summary>
    public (long Bytes, double Speed) Sample()
    {
        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp();
            var elapsed = Stopwatch.GetElapsedTime(_sampleTimestamp, now).TotalSeconds;
            if (elapsed >= 0.2)
            {
                var instant = (_downloaded - _sampleBytes) / elapsed;
                _speed = _speed <= 0 ? instant : (_speed + instant) / 2;
                _sampleBytes = _downloaded;
                _sampleTimestamp = now;
            }

            return (_downloaded, Math.Max(0, _speed));
        }
    }

    /// <summary>Runs the download until it completes, fails or is stopped. Never throws.</summary>
    public async Task RunAsync()
    {
        try
        {
            var retries = 0;
            while (true)
            {
                try
                {
                    InitializeTransport();
                    await ExecuteAsync();
                    break;
                }
                catch (TransferException ex) when (ShouldRetry(ex, retries))
                {
                    retries++;
                    var delay = ComputeRetryDelay(ex, retries);
                    await SetWaitingAsync(ex, delay, retries);
                    await Task.Delay(delay, _cts.Token);
                }
            }

            await CompleteAsync();
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            await HandleStopAsync();
        }
        catch (TransferException ex)
        {
            await FailAsync(ex.Code, ex.Message);
        }
        catch (RestartRequiredException ex)
        {
            await FailAsync(EngineErrorCodes.UnknownError, ex.Message);
        }
        catch (Exception ex)
        {
            await FailAsync(ex is IOException ? EngineErrorCodes.IoError : EngineErrorCodes.UnknownError, ex.Message);
        }
    }

    /// <summary>Saves current progress (the file is flushed first so saved progress never exceeds the data on disk).</summary>
    internal async Task CheckpointAsync()
    {
        await _checkpointGate.WaitAsync();
        try
        {
            SegmentInfo[] snapshot;
            long downloaded;
            DownloadItem itemSnapshot;
            lock (_gate)
            {
                snapshot = _segments.Select(CloneSegment).ToArray();
                downloaded = _downloaded;
                _item.DownloadedBytes = downloaded;
                itemSnapshot = DownloadEngine.CloneItem(_item);
            }

            if (_file is { IsInvalid: false, IsClosed: false })
            {
                RandomAccess.FlushToDisk(_file);
            }

            await _engine.Repository.SaveSegmentsAsync(_item.Id, snapshot);
            await _engine.PersistAsync(itemSnapshot);
            _lastCheckpointTicks = Environment.TickCount64;
        }
        finally
        {
            _checkpointGate.Release();
        }
    }

    public void Dispose()
    {
        _client?.Dispose();
        if (_ownsTransport) _transport?.Dispose();
        _file?.Dispose();
        _file = null;
        _checkpointGate.Dispose();
        _cts.Dispose();
    }

    // ------------------------------------------------------------------
    // Download stage loop
    // ------------------------------------------------------------------

    private bool ShouldRetry(TransferException ex, int retries)
    {
        var alwaysRetry = _engine.Settings.Get(SettingKeys.AlwaysRetryDownload, SettingDefaults.AlwaysRetryDownload);
        var retryable = ex.Retryable ||
                        (alwaysRetry && ex.Code is >= 400 and < 600 && ex.Code != EngineErrorCodes.RangeNotSatisfiable);
        if (!retryable) return false;

        if (!_engine.Settings.Get(SettingKeys.Retry, SettingDefaults.Retry)) return false;

        var max = _engine.Settings.Get(SettingKeys.RetryCount, SettingDefaults.RetryCount);
        return max <= 0 || retries < max;
    }

    private TimeSpan ComputeRetryDelay(TransferException ex, int retries)
    {
        var interval = Math.Max(0, _engine.Settings.Get(SettingKeys.RetryInterval, SettingDefaults.RetryIntervalSeconds));
        var backoff = TimeSpan.FromSeconds(interval * Math.Pow(2, Math.Min(retries - 1, 6)));
        if (ex.RetryAfter is { } retryAfter && retryAfter > backoff) backoff = retryAfter;
        if (backoff > MaxRetryDelay) backoff = MaxRetryDelay;
        return backoff < _engine.MinRetryDelay ? _engine.MinRetryDelay : backoff;
    }

    private async Task SetWaitingAsync(TransferException ex, TimeSpan delay, int retries)
    {
        lock (_gate)
        {
            // The Connecting state is used while waiting for a retry (the contract has no 'waiting' state).
            _item.State = DownloadState.Connecting;
            _item.ErrorCode = ex.Code;
            _item.ErrorMessage = $"{ex.Message} Retrying #{retries} in {delay.TotalSeconds:0} s.";
        }

        await PersistItemAsync();
    }

    private async Task ExecuteAsync()
    {
        lock (_gate)
        {
            _item.State = DownloadState.Connecting;
            _item.ErrorCode = 0;
            _item.ErrorMessage = null;
        }

        await PersistItemAsync();

        for (var restarts = 0; ; restarts++)
        {
            try
            {
                await RunAttemptAsync();
                return;
            }
            catch (RestartRequiredException) when (restarts < MaxRestarts)
            {
                // Browser/file changed: discard progress and restart the download from scratch.
                lock (_gate)
                {
                    _downloaded = 0;
                    _segments = [];
                    _item.DownloadedBytes = 0;
                }
            }
        }
    }

    private async Task RunAttemptAsync()
    {
        _cts.Token.ThrowIfCancellationRequested();

        var probe = await ProbeAsync(withRange: true);
        if (_item.Kind == DownloadKind.Hls || probe.IsPlaylist)
        {
            await RunPlaylistAsync(probe);
            return;
        }

        var fresh = ApplyProbe(probe);
        _rangeMode = probe.AcceptsRanges && probe.Total > 0;
        await LoadOrPlanSegmentsAsync(fresh, probe.Total);

        Directory.CreateDirectory(_item.SaveDirectory);
        _file = File.OpenHandle(_item.FullPath, FileMode.OpenOrCreate, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete, FileOptions.Asynchronous);

        try
        {
            if (fresh) RandomAccess.SetLength(_file, 0);
            Preallocate(probe.Total);

            lock (_gate) _item.State = DownloadState.Downloading;
            await PersistItemAsync();

            await RunSegmentsAsync();
            await VerifyAsync();
        }
        finally
        {
            await CheckpointAsync();
            _file.Dispose();
            _file = null;
        }
    }

    // ------------------------------------------------------------------
    // Probe and naming
    // ------------------------------------------------------------------

    private sealed record ProbeResult(
        Uri FinalUri,
        long Total,
        bool AcceptsRanges,
        string? ETag,
        string? LastModified,
        string? ContentDisposition,
        string? MediaType)
    {
        public bool IsPlaylist =>
            FinalUri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ||
            MediaType is "application/vnd.apple.mpegurl" or "application/x-mpegurl" or "audio/mpegurl";
    }

    private async Task<ProbeResult> ProbeAsync(bool withRange)
    {
        ProbeResult? result = null;
        var (response, finalUri) = await HttpTransfer.SendAsync(_client!, new Uri(_item.Url), request =>
        {
            HttpTransfer.ApplyItemHeaders(request, _item, _engine.UserAgent);
            if (withRange) request.Headers.Range = new RangeHeaderValue(0, 0);
        }, _engine.RequestTimeout, _cts.Token);

        using (response)
        {
            if (!(withRange && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable))
            {
                var error = HttpTransfer.ClassifyStatus(response);
                if (error is not null) throw error;

                var headers = response.Content.Headers;
                var etag = response.Headers.ETag?.ToString();
                var lastModified = HttpTransfer.FormatLastModified(headers.LastModified);
                var disposition = headers.ContentDisposition?.ToString();
                var mediaType = headers.ContentType?.MediaType;

                if (response.StatusCode == HttpStatusCode.PartialContent)
                {
                    var total = headers.ContentRange?.Length ?? -1;
                    result = new ProbeResult(finalUri, total, true, etag, lastModified, disposition, mediaType);
                }
                else if (response.StatusCode == HttpStatusCode.OK)
                {
                    result = new ProbeResult(finalUri, headers.ContentLength ?? -1, false, etag, lastModified, disposition, mediaType);
                }
                else
                {
                    throw new TransferException((int)response.StatusCode, $"Unexpected status {(int)response.StatusCode}.", retryable: false);
                }
            }
        }

        // A 416 on the 0-0 range usually means an empty file; retry without a range.
        return result ?? await ProbeAsync(withRange: false);
    }

    /// <summary>
    /// Updates the record from the probe. Returns true if the download starts from scratch
    /// (no valid progress to resume).
    /// </summary>
    private bool ApplyProbe(ProbeResult probe)
    {
        var root = _engine.DefaultSaveDirectory;
        bool fresh;

        lock (_gate)
        {
            var hasProgress = _downloaded > 0 || _segments.Any(s => s.Downloaded > 0);
            if (hasProgress)
            {
                var validatorsChanged =
                    (!string.IsNullOrEmpty(_item.ETag) && !string.IsNullOrEmpty(probe.ETag) && _item.ETag != probe.ETag) ||
                    (!string.IsNullOrEmpty(_item.LastModified) && !string.IsNullOrEmpty(probe.LastModified) && _item.LastModified != probe.LastModified) ||
                    (_item.TotalBytes > 0 && probe.Total > 0 && _item.TotalBytes != probe.Total) ||
                    _item.AcceptsRanges != probe.AcceptsRanges;
                var fileMissing = !File.Exists(_item.FullPath);

                if (validatorsChanged || fileMissing)
                {
                    // The file on the server changed or the local file is gone: start from scratch.
                    hasProgress = false;
                    _downloaded = 0;
                    _segments = [];
                    _item.DownloadedBytes = 0;
                }
            }

            fresh = !hasProgress;

            if (fresh)
            {
                var userName = string.IsNullOrEmpty(_item.FileName) ? null : _item.FileName;
                _item.FileName = FileNameResolver.Resolve(probe.FinalUri, probe.ContentDisposition, probe.MediaType, userName);
            }

            _item.Category = DownloadCategories.GetCategory(_item.FileName);
            if (_engine.CataloguingEnabled &&
                string.Equals(_item.SaveDirectory.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                _item.SaveDirectory = DownloadCategories.GetSaveDirectory(root, _item.Category);
            }

            Directory.CreateDirectory(_item.SaveDirectory);
            if (fresh && File.Exists(_item.FullPath))
            {
                _item.FileName = FileNameResolver.MakeUnique(_item.SaveDirectory, _item.FileName);
            }

            _item.FinalUrl = probe.FinalUri.AbsoluteUri;
            _item.TotalBytes = probe.Total >= 0 ? probe.Total : _item.TotalBytes;
            _item.AcceptsRanges = probe.AcceptsRanges;
            _item.ETag = probe.ETag ?? _item.ETag;
            _item.LastModified = probe.LastModified ?? _item.LastModified;
        }

        return fresh;
    }

    // ------------------------------------------------------------------
    // Segment planning and persistence
    // ------------------------------------------------------------------

    private async Task LoadOrPlanSegmentsAsync(bool fresh, long total)
    {
        var domainLimit = _rangeMode && _engine.Rules is { } rules
            ? await rules.GetDomainPartLimitAsync(HostOf(_item) ?? string.Empty, _cts.Token)
            : 0;
        _workerLimit = _rangeMode ? SegmentPlanner.WorkerCount(total, _item.MaxSegments, domainLimit) : 1;

        if (!fresh)
        {
            var saved = await _engine.Repository.LoadSegmentsAsync(_item.Id);
            if (saved.Count > 0 && SegmentsMatch(saved, total))
            {
                lock (_gate)
                {
                    _segments = saved.OrderBy(s => s.Index).Select(CloneSegment).ToList();
                    _downloaded = _segments.Sum(s => s.Downloaded);
                }

                return;
            }
        }

        List<SegmentInfo> planned;
        if (_rangeMode)
        {
            planned = SegmentPlanner.Plan(total, _item.MaxSegments, domainLimit, _engine.AdvancedMethod, _item.Id);
        }
        else
        {
            planned =
            [
                new SegmentInfo
                {
                    DownloadId = _item.Id,
                    Index = 0,
                    Start = 0,
                    End = total > 0 ? total - 1 : -1,
                    Downloaded = 0,
                    State = SegmentState.Pending,
                },
            ];
        }

        lock (_gate)
        {
            _segments = planned;
            _downloaded = 0;
        }

        await _engine.Repository.SaveSegmentsAsync(_item.Id, planned);
    }

    private bool SegmentsMatch(IReadOnlyList<SegmentInfo> saved, long total)
    {
        if (total <= 0) return saved.Count == 1 && saved[0].Start == 0;
        var ordered = saved.OrderBy(s => s.Index).ToList();
        if (ordered[0].Start != 0 || ordered[^1].End != total - 1) return false;
        for (var i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].Start != ordered[i - 1].End + 1) return false;
        }

        return _rangeMode ? ordered.Count <= SettingDefaults.MaxPartsAdvanced : ordered.Count == 1;
    }

    private void Preallocate(long total)
    {
        if (total <= 0 || _file is null) return;

        var length = RandomAccess.GetLength(_file);
        if (length == total) return;

        if (_engine.CreateEmptyFile || length > total)
        {
            RandomAccess.SetLength(_file, total);
        }
    }

    private static SegmentInfo CloneSegment(SegmentInfo s) => new()
    {
        DownloadId = s.DownloadId,
        Index = s.Index,
        Start = s.Start,
        End = s.End,
        Downloaded = s.Downloaded,
        State = s.State,
    };

    private bool IsDone(SegmentInfo seg)
    {
        lock (_gate)
        {
            return seg.End >= 0 && seg.Start + seg.Downloaded > seg.End;
        }
    }

    private long DownloadedOf(SegmentInfo seg)
    {
        lock (_gate) return seg.Downloaded;
    }

    // ------------------------------------------------------------------
    // Segment execution
    // ------------------------------------------------------------------

    private async Task RunSegmentsAsync()
    {
        var pending = _segments.Where(s => !IsDone(s)).OrderBy(s => s.Index).ToArray();
        if (pending.Length == 0) return;

        // At most _workerLimit connections; the advanced method's extra parts queue here in index order.
        using var slots = new SemaphoreSlim(Math.Max(1, _workerLimit));
        var tasks = pending.Select(async s =>
        {
            await slots.WaitAsync(_cts.Token);
            try
            {
                await (_rangeMode ? RunRangeSegmentAsync(s) : RunSingleSegmentAsync(s));
            }
            finally
            {
                slots.Release();
            }
        }).ToArray();

        try
        {
            await Task.WhenAll(tasks);
        }
        catch
        {
            // Errors are examined one by one below.
        }

        if (_cts.IsCancellationRequested) throw new OperationCanceledException(_cts.Token);

        Exception? transfer = null;
        foreach (var task in tasks.Where(t => t.IsFaulted))
        {
            var inner = task.Exception!.InnerException ?? task.Exception;
            if (inner is RestartRequiredException) ExceptionDispatchInfo.Capture(inner).Throw();
            transfer ??= inner;
        }

        if (transfer is not null) ExceptionDispatchInfo.Capture(transfer).Throw();
        if (tasks.Any(t => t.IsCanceled)) throw new OperationCanceledException(_cts.Token);
    }

    /// <summary>Range segment: retries up to 2 times without progress; progress resets the count.</summary>
    private async Task RunRangeSegmentAsync(SegmentInfo seg)
    {
        var failures = 0;
        while (true)
        {
            _cts.Token.ThrowIfCancellationRequested();
            if (IsDone(seg)) return;

            var before = DownloadedOf(seg);
            try
            {
                await FetchRangeAsync(seg);
                if (IsDone(seg)) return;
                if (DownloadedOf(seg) > before)
                {
                    failures = 0;
                    continue;
                }

                throw new TransferException(EngineErrorCodes.ConnectionError, "Connection dropped before the segment completed.", retryable: true);
            }
            catch (TransferException ex) when (ex.Retryable && ex.Code != EngineErrorCodes.TooManyRequests)
            {
                failures = DownloadedOf(seg) > before ? 0 : failures + 1;
                if (failures > SegmentRetries) throw;
                await Task.Delay(_engine.SegmentRetryDelay * failures, _cts.Token);
            }
        }
    }

    private async Task FetchRangeAsync(SegmentInfo seg)
    {
        var position = seg.Start + DownloadedOf(seg);
        var ifRange = HttpTransfer.BuildIfRange(_item.ETag, _item.LastModified);
        var uri = new Uri(_item.FinalUrl ?? _item.Url);

        var (response, _) = await HttpTransfer.SendAsync(_client!, uri, request =>
        {
            HttpTransfer.ApplyItemHeaders(request, _item, _engine.UserAgent);
            request.Headers.Range = seg.End >= 0
                ? new RangeHeaderValue(position, seg.End)
                : new RangeHeaderValue(position, null);
            if (ifRange is not null) request.Headers.IfRange = ifRange;
        }, _engine.RequestTimeout, _cts.Token);

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.OK)
            {
                // The server ignored the range (200): the body may be a new file or ranges are unsupported.
                throw new RestartRequiredException("The server replied 200 to a range request.");
            }

            if (response.StatusCode != HttpStatusCode.PartialContent)
            {
                throw HttpTransfer.ClassifyStatus(response)
                      ?? new TransferException((int)response.StatusCode, $"Unexpected status {(int)response.StatusCode}.", retryable: false);
            }

            ValidatePartial(response, position);
            await CopyBodyAsync(response, seg);
        }
    }

    /// <summary>Validates the 206: Content-Range starts at the requested offset and the size/ETag are unchanged.</summary>
    private void ValidatePartial(HttpResponseMessage response, long position)
    {
        var range = response.Content.Headers.ContentRange;
        if (range is null || range.From is null || range.To is null)
        {
            throw new TransferException(EngineErrorCodes.InvalidLength, "The Content-Range header is missing or invalid.", retryable: false);
        }

        if (range.From != position)
        {
            throw new TransferException(EngineErrorCodes.InvalidLength,
                $"Range starts at {range.From}, expected {position}.", retryable: false);
        }

        if (range.To < range.From)
        {
            throw new TransferException(EngineErrorCodes.InvalidLength, "Content-Range range is reversed.", retryable: false);
        }

        if (range.Length is { } length && _item.TotalBytes > 0 && length != _item.TotalBytes)
        {
            throw new RestartRequiredException("The file size on the server changed.");
        }

        var etag = response.Headers.ETag?.ToString();
        if (!string.IsNullOrEmpty(_item.ETag) && !string.IsNullOrEmpty(etag) && etag != _item.ETag)
        {
            throw new RestartRequiredException("The file ETag changed on the server.");
        }
    }

    /// <summary>Download without ranges: a single stream from the start on every attempt.</summary>
    private async Task RunSingleSegmentAsync(SegmentInfo seg)
    {
        var failures = 0;
        while (true)
        {
            _cts.Token.ThrowIfCancellationRequested();
            try
            {
                await FetchWholeAsync(seg);
                return;
            }
            catch (TransferException ex) when (ex.Retryable && ex.Code != EngineErrorCodes.TooManyRequests)
            {
                // Without ranges the received bytes cannot be kept. IDM+ only starts such a link again from zero
                // when "Auto restart non resumable links" is on; otherwise the download stops with an error.
                if (DownloadedOf(seg) > 0 && !_engine.AutoRestartNonResumable)
                {
                    throw new TransferException(EngineErrorCodes.NotResumable,
                        "The server does not support resume and the transfer was interrupted. Restart the download, " +
                        "or turn on \"Restart from zero if a link cannot be resumed\".", retryable: false, inner: ex);
                }

                failures++;
                if (failures > SegmentRetries) throw;
                await Task.Delay(_engine.SegmentRetryDelay * failures, _cts.Token);
            }
        }
    }

    private async Task FetchWholeAsync(SegmentInfo seg)
    {
        lock (_gate)
        {
            _downloaded -= seg.Downloaded;
            seg.Downloaded = 0;
        }

        var uri = new Uri(_item.FinalUrl ?? _item.Url);
        var (response, _) = await HttpTransfer.SendAsync(_client!, uri,
            request => HttpTransfer.ApplyItemHeaders(request, _item, _engine.UserAgent),
            _engine.RequestTimeout, _cts.Token);

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                var range = response.Content.Headers.ContentRange;
                if (range?.From != 0)
                {
                    throw new TransferException(EngineErrorCodes.InvalidLength, "The server returned a range that was not requested.", retryable: false);
                }
            }
            else if (response.StatusCode != HttpStatusCode.OK)
            {
                throw HttpTransfer.ClassifyStatus(response)
                      ?? new TransferException((int)response.StatusCode, $"Unexpected status {(int)response.StatusCode}.", retryable: false);
            }

            await CopyBodyAsync(response, seg);
        }
    }

    /// <summary>Copies the response body into the file at the segment offset. The speed limit is applied before every write.</summary>
    private async Task CopyBodyAsync(HttpResponseMessage response, SegmentInfo seg)
    {
        var ct = _cts.Token;
        Stream stream;
        try
        {
            stream = await response.Content.ReadAsStreamAsync(ct);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            throw HttpTransfer.FromNetworkException(ex);
        }

        await using (stream)
        {
            var buffer = new byte[BufferSize];
            while (true)
            {
                var offset = seg.Start + DownloadedOf(seg);
                long remaining = seg.End >= 0 ? seg.End - offset + 1 : BufferSize;
                if (remaining <= 0) break;

                var want = (int)Math.Min(buffer.Length, remaining);
                var read = await ReadChunkAsync(stream, buffer.AsMemory(0, want), ct);
                if (read == 0) break;

                await _engine.GlobalLimiter.ConsumeAsync(read, ct);
                await _itemLimiter.ConsumeAsync(read, ct);

                try
                {
                    await RandomAccess.WriteAsync(_file!, buffer.AsMemory(0, read), offset, ct);
                }
                catch (IOException ex)
                {
                    throw new TransferException(EngineErrorCodes.IoError, "Error writing file: " + ex.Message, retryable: false, inner: ex);
                }

                lock (_gate)
                {
                    seg.Downloaded += read;
                    _downloaded += read;
                }

                _engine.RecordTransfer(read);

                if (Environment.TickCount64 - Interlocked.Read(ref _lastCheckpointTicks) >= _engine.CheckpointInterval.TotalMilliseconds)
                {
                    await CheckpointAsync();
                }
            }
        }
    }

    private async Task<int> ReadChunkAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(_engine.RequestTimeout);
        try
        {
            return await stream.ReadAsync(buffer, linked.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TransferException(EngineErrorCodes.ConnectionError, "Timed out reading data.", retryable: true);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            throw HttpTransfer.FromNetworkException(ex);
        }
    }

    // ------------------------------------------------------------------
    // Verification and termination
    // ------------------------------------------------------------------

    private async Task VerifyAsync()
    {
        long sum;
        lock (_gate) sum = _segments.Sum(s => s.Downloaded);

        var total = _item.TotalBytes;
        if (total > 0 && sum != total)
        {
            throw new TransferException(EngineErrorCodes.InvalidLength,
                $"Downloaded size {sum} does not match {total}.", retryable: false);
        }

        if (total <= 0)
        {
            lock (_gate) _item.TotalBytes = sum;
        }

        if (string.IsNullOrEmpty(_item.ExpectedSha256) && string.IsNullOrEmpty(_item.ExpectedMd5)) return;

        await using var stream = new FileStream(_item.FullPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true);

        if (!string.IsNullOrEmpty(_item.ExpectedSha256))
        {
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, _cts.Token)).ToLowerInvariant();
            if (!actual.Equals(_item.ExpectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw new TransferException(EngineErrorCodes.ChecksumMismatch, "SHA-256 checksum does not match.", retryable: false);
            }

            return;
        }

        stream.Position = 0;
        var md5 = Convert.ToHexString(await MD5.HashDataAsync(stream, _cts.Token)).ToLowerInvariant();
        if (!md5.Equals(_item.ExpectedMd5!.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new TransferException(EngineErrorCodes.ChecksumMismatch, "MD5 checksum does not match.", retryable: false);
        }
    }

    private async Task RunPlaylistAsync(ProbeResult probe)
    {
        var root = _engine.DefaultSaveDirectory;
        var resume = await LoadHlsResumeAsync();
        lock (_gate)
        {
            _item.Kind = DownloadKind.Hls;
            _item.FinalUrl = probe.FinalUri.AbsoluteUri;
            _item.State = DownloadState.Downloading;
            if (resume is null)
            {
                // Only a new download picks a stem name; resume keeps the already-persisted name.
                _item.FileName = Path.GetFileNameWithoutExtension(string.IsNullOrEmpty(_item.FileName)
                    ? FileNameResolver.FromUrl(probe.FinalUri) ?? "stream"
                    : _item.FileName);
            }

            _item.Category = DownloadCategories.Video;
            if (_engine.CataloguingEnabled &&
                string.Equals(_item.SaveDirectory.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                _item.SaveDirectory = DownloadCategories.GetSaveDirectory(root, DownloadCategories.Video);
            }
        }

        await PersistItemAsync();
        Directory.CreateDirectory(_item.SaveDirectory);

        string baseName;
        lock (_gate)
        {
            // The unique name is picked only once; a retry must continue the same file.
            baseName = resume is null
                ? Path.GetFileNameWithoutExtension(FileNameResolver.MakeUnique(_item.SaveDirectory, _item.FileName + ".ts"))
                : _item.FileName;
            _item.FileName = baseName;
        }

        await PersistItemAsync();

        using var downloader = new HlsDownloader(_transport,
            request => HttpTransfer.ApplyItemHeaders(request, _item, _engine.UserAgent));
        var options = new HlsDownloadOptions
        {
            ConvertToMp4 = _engine.ConvertToMp4,
            RecordLiveUntilStopped = _engine.HlsRecordLiveUntilStopped,
            Resume = resume,
            Checkpoint = SaveHlsCheckpointAsync,
        };

        // Bytes that already exist from a resume are not counted as new traffic for the metered limit.
        var countedBytes = (resume?.Video.Bytes ?? 0) + (resume?.Audio?.Bytes ?? 0);
        var result = await downloader.DownloadDetailedAsync(probe.FinalUri, _item.SaveDirectory, baseName, options,
            bytes =>
            {
                lock (_gate) _downloaded = bytes;
                _engine.RecordTransfer(bytes - countedBytes);
                countedBytes = bytes;
            },
            _cts.Token);

        lock (_gate)
        {
            _item.FileName = Path.GetFileName(result.OutputPath);
            _item.TotalBytes = new FileInfo(result.OutputPath).Length;
            _item.DownloadedBytes = _downloaded = _item.TotalBytes;
            Notes.AddRange(result.Notes);
            if (result.AudioPath is not null)
            {
                Notes.Add($"Audio track saved separately: {Path.GetFileName(result.AudioPath)}");
            }
        }
    }

    /// <summary><c>ETag</c> prefix that carries the HLS playlist fingerprint (not an HTTP validator).</summary>
    private const string HlsTag = "hls:";

    /// <summary>Reads the HLS resume state from the item settings and the threads table (index 0 = video, 1 = audio).</summary>
    private async Task<HlsResumeState?> LoadHlsResumeAsync()
    {
        string? tag;
        lock (_gate) tag = _item.ETag;
        if (tag is null || !tag.StartsWith(HlsTag, StringComparison.Ordinal)) return null;

        var rows = await _engine.Repository.LoadSegmentsAsync(_item.Id);
        var video = rows.FirstOrDefault(r => r.Index == 0);
        if (video is null) return null;

        var audio = rows.FirstOrDefault(r => r.Index == 1);
        return new HlsResumeState(tag[HlsTag.Length..], ToHlsProgress(video), audio is null ? null : ToHlsProgress(audio));
    }

    /// <summary>
    /// Saves an HLS checkpoint. Mapping to the threads table: Index = track, Start = next sequence number,
    /// End = completed segment count, Downloaded = valid bytes in the track file.
    /// </summary>
    private async Task SaveHlsCheckpointAsync(HlsResumeState state)
    {
        var rows = new List<SegmentInfo> { ToHlsRow(0, state.Video) };
        if (state.Audio is { } audio) rows.Add(ToHlsRow(1, audio));

        var total = state.Video.Bytes + (state.Audio?.Bytes ?? 0);
        lock (_gate)
        {
            _item.ETag = HlsTag + state.Fingerprint;
            _item.DownloadedBytes = total;
            _downloaded = total;
        }

        await _engine.Repository.SaveSegmentsAsync(_item.Id, rows);
        await PersistItemAsync();
    }

    private SegmentInfo ToHlsRow(int track, HlsTrackProgress progress) => new()
    {
        DownloadId = _item.Id,
        Index = track,
        Start = progress.NextSequence,
        End = progress.CompletedSegments,
        Downloaded = progress.Bytes,
        State = SegmentState.Done,
    };

    private static HlsTrackProgress ToHlsProgress(SegmentInfo row) => new(row.Start, (int)row.End, row.Downloaded);

    /// <summary>Non-critical warnings from the download (for example a partial live stream, ffmpeg missing).</summary>
    internal List<string> Notes { get; } = [];

    private async Task CompleteAsync()
    {
        DownloadItem snapshot;
        lock (_gate)
        {
            _item.State = DownloadState.Completed;
            _item.CompletedAt = DateTimeOffset.UtcNow;
            _item.DownloadedBytes = _downloaded;
            _item.ErrorCode = 0;
            _item.ErrorMessage = null;
            snapshot = DownloadEngine.CloneItem(_item);
        }

        await _engine.Repository.SaveSegmentsAsync(_item.Id, []);
        await _engine.PersistAsync(snapshot);
        _engine.NotifyItem(snapshot);
    }

    private async Task FailAsync(int code, string message)
    {
        DownloadItem snapshot;
        lock (_gate)
        {
            _item.State = DownloadState.Error;
            _item.ErrorCode = code;
            _item.ErrorMessage = message;
            _item.DownloadedBytes = _downloaded;
            snapshot = DownloadEngine.CloneItem(_item);
        }

        await _engine.PersistAsync(snapshot);
        _engine.NotifyItem(snapshot);
    }

    private async Task HandleStopAsync()
    {
        StopReason reason;
        lock (_gate) reason = _stopReason;

        if (reason == StopReason.Remove) return;

        DownloadItem snapshot;
        lock (_gate)
        {
            // Stopped by network policy: back to the queue so the pump resumes it when allowed again.
            _item.State = IsNetworkPolicyStop(reason) ? DownloadState.Queued : DownloadState.Paused;
            _item.ErrorCode = 0;
            _item.ErrorMessage = null;
            _item.DownloadedBytes = _downloaded;
            snapshot = DownloadEngine.CloneItem(_item);
        }

        await _engine.PersistAsync(snapshot);
        _engine.NotifyItem(snapshot);
    }

    private static bool IsNetworkPolicyStop(StopReason reason) =>
        reason is StopReason.WifiLost or StopReason.Offline or StopReason.Metered or StopReason.QuotaExceeded
            or StopReason.LowBattery;

    private async Task PersistItemAsync()
    {
        DownloadItem snapshot;
        lock (_gate) snapshot = DownloadEngine.CloneItem(_item);
        await _engine.PersistAsync(snapshot);
        _engine.NotifyItem(snapshot);
    }

    // ------------------------------------------------------------------
    // HTTP transport
    // ------------------------------------------------------------------

    private void InitializeTransport()
    {
        if (_client is not null) return;

        if (_engine.Handler is { } shared)
        {
            _transport = shared;
            _ownsTransport = false;
        }
        else
        {
            _transport = _engine.CreateTransport();
            _ownsTransport = true;
        }

        _client = new HttpClient(_transport, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static string? HostOf(DownloadItem item) => DownloadEngine.HostOf(item.FinalUrl ?? item.Url);
}
