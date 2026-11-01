using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using WinIDM.Core.Contracts;
using WinIDM.Core.Models;

namespace WinIDM.Engine;

/// <summary>
/// Multi-segment HTTP(S) download engine: queue with a concurrency limit, global and per-download speed limits,
/// per-site/domain limits, scheduler (<see cref="DownloadRequest.ScheduledAt"/>), network policy (<see cref="INetworkPolicy"/>:
/// Wi-Fi only, pause on metered data, daily/monthly data limits, offline), and recovery after restart
/// through <see cref="IDownloadRepository"/>.
/// </summary>
public sealed class DownloadEngine : IDownloadEngine, IDisposable
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    private readonly IDownloadRepository _repository;
    private readonly ISettingsStore _settings;
    private readonly HttpMessageHandler? _handler;
    private readonly INetworkPolicy _network;
    private readonly MeteredUsage _usage;
    private readonly object _gate = new();
    private readonly Dictionary<long, DownloadItem> _items = new();
    private readonly Dictionary<long, DownloadJob> _jobs = new();
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private readonly SemaphoreSlim _pumpGate = new(1, 1);
    private readonly TokenBucket _globalLimiter = new();
    private readonly Timer _progressTimer;
    private readonly Timer _tickTimer;
    private bool _initialized;
    private volatile bool _disposed;
    private Func<string, CancellationToken, Task<IPAddress[]>>? _hostResolver;
    private DateTimeOffset _lastLinkCleanup = DateTimeOffset.MinValue;
    private int _completedSinceRaise;
    private int _failedSinceRaise;
    private bool _queueFinishedRaised;
    private int _policyKickPending;

    /// <summary>
    /// Minimum delay before a download retry (default 3 s, matching IDM+, 01 section 4.6).
    /// </summary>
    internal TimeSpan MinRetryDelay { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Base delay for segment retries (multiplied by the attempt number).</summary>
    internal TimeSpan SegmentRetryDelay { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Progress checkpoint interval during a download (default 2 s).</summary>
    internal TimeSpan CheckpointInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Wi-Fi availability override for tests. Null (default) means <see cref="INetworkPolicy.IsWifiOrEthernet"/> is read.
    /// </summary>
    internal Func<bool>? WifiAvailability { get; set; }

    /// <summary>Current clock for daily/monthly limits and the scheduler. Replaceable in tests to simulate a new day.</summary>
    internal Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.Now;

    /// <summary>
    /// Optional custom TLS certificate validator, for tests only (for example an HTTPS proxy with a test certificate). Null = system validation.
    /// </summary>
    internal RemoteCertificateValidationCallback? TlsCertificateCallback { get; set; }

    /// <summary>
    /// Creates the engine. <paramref name="handler"/> (optional) is a shared transfer handler that the engine does not dispose;
    /// if null, each download creates its own handler (with the proxy and <paramref name="hostResolver"/> from settings).
    /// <paramref name="hostResolver"/> (optional) is a custom DNS resolver (for example DoH/DoT); null means system DNS.
    /// <paramref name="network"/> (optional) is the network policy (the App supplies the Windows implementation). Null means
    /// <see cref="DefaultNetworkPolicy"/> (no restrictions). If the policy is <see cref="IDisposable"/>, the engine disposes it.
    /// </summary>
    public DownloadEngine(
        IDownloadRepository repository,
        ISettingsStore settings,
        HttpMessageHandler? handler = null,
        Func<string, CancellationToken, Task<IPAddress[]>>? hostResolver = null,
        INetworkPolicy? network = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _handler = handler;
        _hostResolver = hostResolver;
        _network = network ?? new DefaultNetworkPolicy();
        _usage = new MeteredUsage(settings, DateTimeOffset.Now);
        _network.Changed += OnNetworkChanged;

        _progressTimer = new Timer(state => Guard(OnProgressTick), null, ProgressInterval, ProgressInterval);
        _tickTimer = new Timer(state => _ = GuardAsync(TickAsync), null, TickInterval, TickInterval);
    }

    public event Action<DownloadProgress>? ProgressChanged;

    public event Action<DownloadItem>? ItemChanged;

    /// <summary>A download completed (after moving the file and removing the link if configured). For App notifications.</summary>
    public event Action<DownloadCompletedInfo>? DownloadCompleted;

    /// <summary>A download switched to the error state.</summary>
    public event Action<DownloadFailedInfo>? DownloadFailed;

    /// <summary>
    /// All active and queued downloads have finished. The engine only reports <see cref="SystemActionRequest.Action"/>
    /// (from the <see cref="SettingKeys.AfterDownloadsAction"/> setting); the App performs it after user confirmation.
    /// </summary>
    public event Action<SystemActionRequest>? AfterDownloadsFinished;

    /// <summary>
    /// Custom DNS resolver (for example DoH/DoT from the App). Null means system DNS. Read each time a transfer
    /// handler is created for a download, so changes take effect for the next download.
    /// Not used if a shared <c>handler</c> was supplied when building the engine.
    /// </summary>
    public Func<string, CancellationToken, Task<IPAddress[]>>? HostResolver
    {
        get => _hostResolver;
        set => _hostResolver = value;
    }

    /// <summary>
    /// Power source reader for the battery limit (<see cref="SettingKeys.BatteryLimit"/>). The App supplies the Windows
    /// reading; null (default) or a null result means "no battery information", so the limit never blocks.
    /// </summary>
    public Func<PowerReading?>? PowerStatus { get; set; }

    /// <summary>Last background error (for example a repository error while pumping the queue). Null if none.</summary>
    public Exception? LastBackgroundException { get; private set; }

    // ------------------------------------------------------------------
    // IDownloadEngine
    // ------------------------------------------------------------------

    public async Task<long> AddAsync(DownloadRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("Download URL must use the http or https scheme.", nameof(request));
        }

        await EnsureInitializedAsync(ct);

        var now = DateTimeOffset.UtcNow;
        var scheduled = request.ScheduledAt is { } at && at > now;

        // Per-site settings fill in fields the request did not provide (UA, cookies, folder).
        var site = _repository is ISiteSettingsProvider siteProvider
            ? await siteProvider.GetSiteSettingsAsync(uri.Host, ct)
            : null;
        var siteDirectory = string.IsNullOrWhiteSpace(site?.SaveDirectory) ? null : site!.SaveDirectory;

        var item = new DownloadItem
        {
            Kind = uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ? DownloadKind.Hls : DownloadKind.Http,
            Url = uri.AbsoluteUri,
            FileName = FileNameResolver.Sanitize(request.FileName) ?? string.Empty,
            SaveDirectory = !string.IsNullOrWhiteSpace(request.SaveDirectory)
                ? request.SaveDirectory
                : siteDirectory ?? DefaultSaveDirectory,
            Referer = request.Referer,
            UserAgent = request.UserAgent ?? site?.UserAgent,
            CookieHeader = request.CookieHeader ?? site?.Cookies,
            Headers = new Dictionary<string, string>(request.Headers),
            Username = request.Username,
            Password = request.Password,
            MaxSegments = Math.Clamp(request.MaxSegments ?? PartsSetting, 1, SettingDefaults.MaxParts),
            ScheduledAt = request.ScheduledAt,
            CreatedAt = now,
            State = scheduled ? DownloadState.Scheduled : request.StartImmediately ? DownloadState.Queued : DownloadState.Paused,
        };

        lock (_gate)
        {
            item.SortOrder = _items.Values.Select(i => i.SortOrder).DefaultIfEmpty(0).Max() + 1;
        }

        await _repository.InsertAsync(item, ct);

        lock (_gate) _items[item.Id] = item;
        NotifyItem(CloneItem(item));
        _ = GuardAsync(PumpAsync);

        return item.Id;
    }

    public async Task PauseAsync(long id)
    {
        await EnsureInitializedAsync();

        DownloadJob? job;
        DownloadItem? snapshot = null;
        lock (_gate)
        {
            if (!_items.TryGetValue(id, out var item)) throw new KeyNotFoundException($"Download {id} does not exist.");

            _jobs.TryGetValue(id, out job);
            if (job is null && item.State is DownloadState.Queued or DownloadState.Scheduled)
            {
                item.State = DownloadState.Paused;
                snapshot = CloneItem(item);
            }
        }

        if (job is not null)
        {
            job.Stop(StopReason.Pause);
            return;
        }

        if (snapshot is not null)
        {
            await _repository.UpdateAsync(snapshot);
            NotifyItem(snapshot);
        }
    }

    public async Task ResumeAsync(long id)
    {
        await EnsureInitializedAsync();

        DownloadJob? running;
        lock (_gate)
        {
            if (!_items.ContainsKey(id)) throw new KeyNotFoundException($"Download {id} does not exist.");
            _jobs.TryGetValue(id, out running);
            if (running is not null && !running.IsStopping) return;
        }

        // Job already stopping (pause just pressed): wait until it is removed, otherwise this request is silently lost.
        if (running is not null) await running.Completion;

        DownloadItem? snapshot = null;
        lock (_gate)
        {
            if (!_items.TryGetValue(id, out var item)) return; // removed while stopping
            if (_jobs.ContainsKey(id)) return;

            if (item.State is DownloadState.Paused or DownloadState.Error)
            {
                item.State = DownloadState.Queued;
                item.ErrorCode = 0;
                item.ErrorMessage = null;
                snapshot = CloneItem(item);
            }
        }

        if (snapshot is null) return;

        await _repository.UpdateAsync(snapshot);
        NotifyItem(snapshot);
        await PumpAsync();
    }

    public async Task RemoveAsync(long id, bool deleteFile)
    {
        await EnsureInitializedAsync();

        DownloadJob? job;
        string? path;
        lock (_gate)
        {
            if (!_items.TryGetValue(id, out var item)) return;
            path = item.FullPath;
            _jobs.TryGetValue(id, out job);
        }

        if (job is not null)
        {
            job.Stop(StopReason.Remove);
            await job.Completion;
        }

        lock (_gate) _items.Remove(id);
        await _repository.DeleteAsync(id);

        if (deleteFile && File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public async Task PauseAllAsync()
    {
        await EnsureInitializedAsync();

        List<long> ids;
        lock (_gate)
        {
            ids = _items.Values
                .Where(i => i.State is DownloadState.Queued or DownloadState.Scheduled or DownloadState.Connecting
                    or DownloadState.Downloading or DownloadState.Merging)
                .Select(i => i.Id)
                .ToList();
        }

        foreach (var id in ids) await PauseAsync(id);
    }

    public async Task StartAllAsync()
    {
        await EnsureInitializedAsync();

        List<long> ids;
        lock (_gate)
        {
            ids = _items.Values
                .Where(i => i.State is DownloadState.Paused or DownloadState.Error)
                .Select(i => i.Id)
                .ToList();
        }

        foreach (var id in ids) await ResumeAsync(id);
    }

    // ------------------------------------------------------------------
    // Additional API (not part of the contract)
    // ------------------------------------------------------------------

    /// <summary>Sets the per-download speed limit (bytes per second); 0 means unlimited.</summary>
    public async Task SetSpeedLimitAsync(long id, long bytesPerSecond)
    {
        await EnsureInitializedAsync();

        DownloadItem snapshot;
        lock (_gate)
        {
            if (!_items.TryGetValue(id, out var item)) throw new KeyNotFoundException($"Download {id} does not exist.");

            item.SpeedLimitBytesPerSec = Math.Max(0, bytesPerSecond);
            if (_jobs.TryGetValue(id, out var job)) job.ItemLimiter.BytesPerSecond = item.SpeedLimitBytesPerSec;
            snapshot = CloneItem(item);
        }

        await _repository.UpdateAsync(snapshot);
        NotifyItem(snapshot);
    }

    /// <summary>
    /// Sets the expected checksum (SHA-256 and/or MD5, hex). Checked after the download completes.
    /// A null or empty value disables the check for that algorithm.
    /// </summary>
    public async Task SetChecksumAsync(long id, string? sha256, string? md5)
    {
        await EnsureInitializedAsync();

        DownloadItem snapshot;
        lock (_gate)
        {
            if (!_items.TryGetValue(id, out var item)) throw new KeyNotFoundException($"Download {id} does not exist.");

            item.ExpectedSha256 = string.IsNullOrWhiteSpace(sha256) ? null : sha256.Trim().ToLowerInvariant();
            item.ExpectedMd5 = string.IsNullOrWhiteSpace(md5) ? null : md5.Trim().ToLowerInvariant();
            snapshot = CloneItem(item);
        }

        await _repository.UpdateAsync(snapshot);
        NotifyItem(snapshot);
    }

    /// <summary>
    /// Sets the expected checksum from text (the <c>hex  file-name</c> format used by sha256sum/md5sum, or bare hex).
    /// The checksum is verified automatically after the download completes, before the Completed state.
    /// </summary>
    /// <exception cref="ArgumentException">No valid checksum for the download's file name.</exception>
    public async Task SetChecksumFromTextAsync(long id, string text)
    {
        await EnsureInitializedAsync();

        string fileName;
        lock (_gate)
        {
            if (!_items.TryGetValue(id, out var item)) throw new KeyNotFoundException($"Download {id} does not exist.");
            fileName = item.FileName;
        }

        var parsed = ChecksumText.Parse(text, fileName)
                     ?? throw new ArgumentException("No valid SHA-256 or MD5 checksum found for this file.", nameof(text));

        await SetChecksumAsync(id,
            sha256: parsed.Algorithm == "sha256" ? parsed.Hex : null,
            md5: parsed.Algorithm == "md5" ? parsed.Hex : null);
    }

    /// <summary>
    /// Changes the source URL of an existing download (for example an expired link). Segments are kept if the file size matches:
    /// old validators (ETag, Last-Modified) are dropped, and probing the new link only compares size and range support.
    /// A running download is paused first. A completed download cannot be changed.
    /// </summary>
    public async Task RefreshLinkAsync(long id, string newUrl)
    {
        if (!Uri.TryCreate(newUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("The new URL must use the http or https scheme.", nameof(newUrl));
        }

        await EnsureInitializedAsync();

        DownloadJob? job;
        lock (_gate)
        {
            if (!_items.TryGetValue(id, out var existing)) throw new KeyNotFoundException($"Download {id} does not exist.");
            if (existing.State == DownloadState.Completed)
            {
                throw new InvalidOperationException("The download is already complete; the link cannot be changed.");
            }

            _jobs.TryGetValue(id, out job);
        }

        if (job is not null)
        {
            job.Stop(StopReason.Pause);
            await job.Completion;
        }

        DownloadItem snapshot;
        lock (_gate)
        {
            var item = _items[id];
            item.Url = uri.AbsoluteUri;
            item.FinalUrl = null;

            // The HLS ETag is a playlist fingerprint ("hls:" prefix), not an HTTP validator: kept so the file name stays the same.
            if (item.ETag is null || !item.ETag.StartsWith("hls:", StringComparison.Ordinal))
            {
                item.ETag = null;
            }

            item.LastModified = null;
            if (item.State is DownloadState.Error or DownloadState.Connecting or DownloadState.Downloading or DownloadState.Merging)
            {
                item.State = DownloadState.Paused;
            }

            item.ErrorCode = 0;
            item.ErrorMessage = null;
            snapshot = CloneItem(item);
        }

        await _repository.UpdateAsync(snapshot);
        NotifyItem(snapshot);
    }

    /// <summary>
    /// Live parts of a running download, for "Show progress for each segment" (03a: idm_pref_show_multi_part).
    /// Empty when the download is not running.
    /// </summary>
    public IReadOnlyList<SegmentInfo> GetLiveSegments(long id)
    {
        DownloadJob? job;
        lock (_gate) _jobs.TryGetValue(id, out job);
        return job?.SegmentsSnapshot() ?? [];
    }

    /// <summary>
    /// Removes old download records (links) without deleting files. If <see cref="SettingKeys.DeleteLinksAfterDays"/> is
    /// 0 or -1, nothing is removed. With <see cref="SettingKeys.DeleteLinksOnlyFinished"/> = true, only completed downloads
    /// are removed. Called periodically (once an hour) and can be called directly.
    /// </summary>
    /// <returns>Number of records removed.</returns>
    public async Task<int> RemoveOldLinksAsync(DateTimeOffset? now = null, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        // 0 also means off: the Settings page cannot enter -1, and "after 0 days" would wipe every finished record.
        var days = _settings.Get(SettingKeys.DeleteLinksAfterDays, SettingDefaults.DeleteLinksAfterDays);
        if (days <= 0) return 0;

        var onlyFinished = _settings.Get(SettingKeys.DeleteLinksOnlyFinished, SettingDefaults.DeleteLinksOnlyFinished);
        var cutoff = (now ?? DateTimeOffset.UtcNow).AddDays(-days);

        List<long> ids;
        lock (_gate)
        {
            ids = _items.Values
                .Where(i => !_jobs.ContainsKey(i.Id))
                .Where(i => onlyFinished
                    ? i.State == DownloadState.Completed
                    : i.State is DownloadState.Completed or DownloadState.Error or DownloadState.Paused)
                .Where(i => (i.CompletedAt ?? i.CreatedAt) <= cutoff)
                .Select(i => i.Id)
                .ToList();
        }

        foreach (var id in ids)
        {
            lock (_gate) _items.Remove(id);
            await _repository.DeleteAsync(id, ct);
        }

        return ids.Count;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _network.Changed -= OnNetworkChanged;
        _progressTimer.Dispose();
        _tickTimer.Dispose();

        List<DownloadJob> running;
        lock (_gate) running = _jobs.Values.ToList();

        foreach (var job in running) job.Stop(StopReason.Shutdown);
        if (running.Count > 0)
        {
            try
            {
                Task.WaitAll(running.Select(j => j.Completion).ToArray(), TimeSpan.FromSeconds(15));
            }
            catch (AggregateException)
            {
                // Download errors are already recorded per download.
            }
        }

        // Save unwritten metered counts (after the jobs stop, so the last bytes are counted too).
        Guard(() => _usage.Flush(Clock()));

        if (_network is IDisposable disposable) disposable.Dispose();
    }

    // ------------------------------------------------------------------
    // Resources for DownloadJob (internal)
    // ------------------------------------------------------------------

    internal IDownloadRepository Repository => _repository;

    internal ISettingsStore Settings => _settings;

    internal HttpMessageHandler? Handler => _handler;

    internal TokenBucket GlobalLimiter => _globalLimiter;

    internal ISiteRuleProvider? Rules => _repository as ISiteRuleProvider;

    internal TimeSpan RequestTimeout =>
        TimeSpan.FromSeconds(Math.Max(5, _settings.Get(SettingKeys.Timeout, SettingDefaults.TimeoutSeconds)));

    internal string UserAgent => _settings.Get(SettingKeys.UserAgent, SettingDefaults.UserAgent);

    internal bool CataloguingEnabled =>
        !_settings.Get(SettingKeys.DisableCataloguing, SettingDefaults.DisableCataloguing);

    internal bool AdvancedMethod =>
        _settings.Get(SettingKeys.AdvanceDownloadMethod, SettingDefaults.AdvanceDownloadMethod);

    internal bool AutoRestartNonResumable =>
        _settings.Get(SettingKeys.AutoRestartNonResumable, SettingDefaults.AutoRestartNonResumable);

    internal bool CreateEmptyFile =>
        _settings.Get(SettingKeys.CreateEmptyFile, SettingDefaults.CreateEmptyFile);

    internal bool ConvertToMp4 =>
        _settings.Get(SettingKeys.ConvertTsToMp4, SettingDefaults.ConvertTsToMp4);

    internal bool HlsRecordLiveUntilStopped =>
        _settings.Get(SettingKeys.HlsRecordLiveUntilStopped, SettingDefaults.HlsRecordLiveUntilStopped);

    internal string DefaultSaveDirectory
    {
        get
        {
            var configured = _settings.Get(SettingKeys.DefaultSaveDirectory, SettingDefaults.DefaultSaveDirectory);
            return string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
                : configured;
        }
    }

    /// <summary>Effective part count = setting value + 1 (01 section 3.1).</summary>
    private int PartsSetting =>
        Math.Clamp(_settings.Get(SettingKeys.DownloadPart, SettingDefaults.DownloadPart) + 1, 1, SettingDefaults.MaxParts);

    /// <summary>Effective concurrent download count = setting value + 1 (01 section 5.1).</summary>
    private int Concurrency =>
        Math.Clamp(_settings.Get(SettingKeys.DownloadCount, SettingDefaults.DownloadCount) + 1, 1, 32);

    internal async Task PersistAsync(DownloadItem snapshot)
    {
        await _repository.UpdateAsync(snapshot);
    }

    internal void NotifyItem(DownloadItem snapshot)
    {
        Raise(() => ItemChanged?.Invoke(snapshot));
    }

    /// <summary>Creates the transfer handler for one download (with the proxy from settings if enabled).</summary>
    internal HttpMessageHandler CreateTransport() =>
        EngineTransport.Create(_settings, RequestTimeout, HostResolver, TlsCertificateCallback);

    /// <summary>
    /// Records transferred bytes. Only counted if the current network is marked metered. If the daily/monthly limit
    /// has been reached, a policy check is scheduled so running downloads stop immediately.
    /// </summary>
    internal void RecordTransfer(long bytes)
    {
        if (bytes <= 0 || !_network.IsMetered) return;

        var now = Clock();
        _usage.Add(bytes, now);
        if (_usage.IsOverQuota(now) && Interlocked.Exchange(ref _policyKickPending, 1) == 0)
        {
            _ = GuardAsync(PumpAsync);
        }
    }

    internal static DownloadItem CloneItem(DownloadItem i) => new()
    {
        Id = i.Id,
        Kind = i.Kind,
        Url = i.Url,
        FinalUrl = i.FinalUrl,
        FileName = i.FileName,
        SaveDirectory = i.SaveDirectory,
        TotalBytes = i.TotalBytes,
        DownloadedBytes = i.DownloadedBytes,
        State = i.State,
        Category = i.Category,
        Referer = i.Referer,
        UserAgent = i.UserAgent,
        CookieHeader = i.CookieHeader,
        Headers = new Dictionary<string, string>(i.Headers),
        Username = i.Username,
        Password = i.Password,
        MaxSegments = i.MaxSegments,
        SpeedLimitBytesPerSec = i.SpeedLimitBytesPerSec,
        AcceptsRanges = i.AcceptsRanges,
        ETag = i.ETag,
        LastModified = i.LastModified,
        ExpectedSha256 = i.ExpectedSha256,
        ExpectedMd5 = i.ExpectedMd5,
        ErrorMessage = i.ErrorMessage,
        ErrorCode = i.ErrorCode,
        SortOrder = i.SortOrder,
        ScheduledAt = i.ScheduledAt,
        CreatedAt = i.CreatedAt,
        CompletedAt = i.CompletedAt,
    };

    internal static string? HostOf(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host.ToLowerInvariant() : null;

    // ------------------------------------------------------------------
    // Queue, scheduler and tick
    // ------------------------------------------------------------------

    private async Task EnsureInitializedAsync(CancellationToken ct = default)
    {
        if (_initialized) return;

        await _initGate.WaitAsync(ct);
        try
        {
            if (_initialized) return;

            var items = await _repository.ListAsync(ct);
            foreach (var item in items)
            {
                // Downloads that were running when the app shut down become Paused (no auto-start).
                if (item.State is DownloadState.Connecting or DownloadState.Downloading or DownloadState.Merging)
                {
                    item.State = DownloadState.Paused;
                    await _repository.UpdateAsync(item, ct);
                }

                lock (_gate) _items[item.Id] = item;
            }

            _initialized = true;
        }
        finally
        {
            _initGate.Release();
        }
    }

    /// <summary>
    /// Current network/policy reason blocking a download (null = allowed). Order: offline, Wi-Fi only,
    /// pause on metered, metered quota limit.
    /// </summary>
    private StopReason? BlockingReason(DateTimeOffset now)
    {
        if (!_network.IsOnline) return StopReason.Offline;

        var batteryLimit = _settings.Get(SettingKeys.BatteryLimit, SettingDefaults.BatteryLimit);
        if (batteryLimit > 0 && PowerStatus?.Invoke() is { OnBattery: true } power && power.Percent < batteryLimit)
            return StopReason.LowBattery;

        var wifiOnly = _settings.Get(SettingKeys.WifiOnly, SettingDefaults.WifiOnly);
        if (wifiOnly && !(WifiAvailability?.Invoke() ?? _network.IsWifiOrEthernet)) return StopReason.WifiLost;

        if (!_network.IsMetered) return null;
        if (_settings.Get(SettingKeys.PauseOnMetered, SettingDefaults.PauseOnMetered)) return StopReason.Metered;
        if (_usage.IsOverQuota(now)) return StopReason.QuotaExceeded;

        return null;
    }

    /// <summary>Network policy changed: re-evaluate immediately (the 1 second tick still acts as a fallback re-check).</summary>
    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        _ = GuardAsync(PumpAsync);
    }

    private async Task PumpAsync()
    {
        if (_disposed) return;

        await _pumpGate.WaitAsync();
        try
        {
            await EnsureInitializedAsync();
            if (_disposed) return;

            Interlocked.Exchange(ref _policyKickPending, 0);
            _globalLimiter.BytesPerSecond = _settings.Get(SettingKeys.SpeedLimiter, SettingDefaults.SpeedLimiter)
                ? Math.Max(0, _settings.Get(SettingKeys.SpeedLimit, SettingDefaults.SpeedLimitKibPerSecond)) * 1024L
                : 0;

            List<DownloadJob> running;
            lock (_gate) running = _jobs.Values.ToList();

            // Policy blocks: stop everything running (back to the queue) and do not start anything new.
            // When the policy allows again, queued downloads are restarted by the next pump.
            if (BlockingReason(Clock()) is { } blocked)
            {
                foreach (var job in running) job.Stop(blocked);
                return;
            }

            List<DownloadItem> queued;
            lock (_gate)
            {
                queued = _items.Values
                    .Where(i => i.State == DownloadState.Queued)
                    .OrderBy(i => i.SortOrder)
                    .ThenBy(i => i.Id)
                    .ToList();
            }

            foreach (var item in queued)
            {
                int active;
                lock (_gate) active = _jobs.Count;
                if (active >= Concurrency) break;

                if (!await SiteAllowsAsync(item)) continue;

                StartJob(item);
            }
        }
        finally
        {
            _pumpGate.Release();
        }
    }

    private async Task<bool> SiteAllowsAsync(DownloadItem item)
    {
        var rules = Rules;
        if (rules is null) return true;

        var host = HostOf(item.FinalUrl ?? item.Url);
        if (host is not null)
        {
            var limit = await rules.GetSiteLimitAsync(host);
            if (limit is { Limit: > 0, DownloadLink: true } && CountJobsOnSite(limit.Domain, referer: false) >= limit.Limit)
            {
                return false;
            }
        }

        var referer = HostOf(item.Referer);
        if (referer is not null)
        {
            var limit = await rules.GetSiteLimitAsync(referer);
            if (limit is { Limit: > 0, RefererLink: true } && CountJobsOnSite(limit.Domain, referer: true) >= limit.Limit)
            {
                return false;
            }
        }

        return true;
    }

    private int CountJobsOnSite(string domain, bool referer)
    {
        lock (_gate)
        {
            return _jobs.Values.Count(j =>
            {
                var host = referer ? HostOf(j.Item.Referer) : HostOf(j.Item.FinalUrl ?? j.Item.Url);
                return host is not null && (host == domain || host.EndsWith("." + domain, StringComparison.Ordinal));
            });
        }
    }

    private bool StartJob(DownloadItem item)
    {
        DownloadJob job;
        lock (_gate)
        {
            if (item.State != DownloadState.Queued || _jobs.ContainsKey(item.Id)) return false;

            item.State = DownloadState.Connecting;
            item.ErrorCode = 0;
            item.ErrorMessage = null;
            job = new DownloadJob(this, item);
            _jobs[item.Id] = job;
            _queueFinishedRaised = false;
        }

        _ = RunJobAsync(job);
        return true;
    }

    private async Task RunJobAsync(DownloadJob job)
    {
        try
        {
            await job.RunAsync();
        }
        finally
        {
            lock (_gate) _jobs.Remove(job.Id);
            job.Dispose();
            job.MarkFinished();

            var final = job.Item;
            var bytes = job.Downloaded;
            Raise(() => ProgressChanged?.Invoke(new DownloadProgress(job.Id, bytes, final.TotalBytes, 0, final.State)));

            await GuardAsync(() => FinalizeJobAsync(job));
            await GuardAsync(PumpAsync);
        }
    }

    /// <summary>Post-download actions: move, notification (events), link removal and queue completion.</summary>
    private async Task FinalizeJobAsync(DownloadJob job)
    {
        var item = job.Item;
        DownloadState state;
        lock (_gate) state = item.State;

        if (state == DownloadState.Completed)
        {
            await OnCompletedAsync(job);
        }
        else if (state == DownloadState.Error)
        {
            DownloadFailedInfo info;
            lock (_gate)
            {
                _failedSinceRaise++;
                info = new DownloadFailedInfo(item.Id, item.FileName, item.ErrorCode, item.ErrorMessage ?? string.Empty);
            }

            Raise(() => DownloadFailed?.Invoke(info));
        }

        MaybeRaiseQueueFinished();
    }

    private async Task OnCompletedAsync(DownloadJob job)
    {
        var item = job.Item;
        var notes = new List<string>(job.Notes);
        var duration = (item.CompletedAt ?? DateTimeOffset.UtcNow) - item.CreatedAt;
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;

        string? movedTo = null;
        var rules = PostDownloadActions.ParseMoveRules(_settings.Get(SettingKeys.MoveRules, SettingDefaults.MoveRules));
        var destination = PostDownloadActions.ResolveDestination(item, _settings, rules);
        if (destination is not null)
        {
            DownloadItem moved;
            lock (_gate)
            {
                movedTo = PostDownloadActions.TryMove(item, destination, out var warning);
                if (warning is not null) notes.Add(warning);
                moved = CloneItem(item);
            }

            if (movedTo is not null) await PersistAsync(moved);
        }

        var checksumVerified = !string.IsNullOrEmpty(item.ExpectedSha256) || !string.IsNullOrEmpty(item.ExpectedMd5);
        DownloadCompletedInfo info;
        lock (_gate)
        {
            _completedSinceRaise++;
            info = new DownloadCompletedInfo(
                item.Id,
                item.FileName,
                item.FullPath,
                item.Category,
                item.TotalBytes,
                duration,
                movedTo is null ? null : destination,
                checksumVerified,
                notes);
        }

        if (_settings.Get(SettingKeys.RemoveLinkOnFinish, SettingDefaults.RemoveLinkOnFinish))
        {
            lock (_gate) _items.Remove(item.Id);
            await _repository.DeleteAsync(item.Id);
        }

        Raise(() => DownloadCompleted?.Invoke(info));
    }

    /// <summary>Raises queue completion once per batch of downloads (nothing active or waiting).</summary>
    private void MaybeRaiseQueueFinished()
    {
        var action = _settings.Get(SettingKeys.AfterDownloadsAction, SystemAction.None);
        var raise = false;
        SystemActionRequest request = new(action, 0, 0);

        lock (_gate)
        {
            if (_queueFinishedRaised || _jobs.Count > 0) return;
            if (_items.Values.Any(i => i.State is DownloadState.Queued or DownloadState.Scheduled
                    or DownloadState.Connecting or DownloadState.Downloading or DownloadState.Merging))
            {
                return;
            }

            if (_completedSinceRaise == 0 && _failedSinceRaise == 0) return;

            _queueFinishedRaised = true;
            request = new SystemActionRequest(action, _completedSinceRaise, _failedSinceRaise);
            _completedSinceRaise = 0;
            _failedSinceRaise = 0;
            raise = true;
        }

        if (raise) Raise(() => AfterDownloadsFinished?.Invoke(request));
    }

    private async Task TickAsync()
    {
        if (_disposed) return;
        await EnsureInitializedAsync();

        var now = Clock();
        Guard(() => _usage.Flush(now));

        if (now - _lastLinkCleanup >= TimeSpan.FromHours(1))
        {
            _lastLinkCleanup = now;
            await GuardAsync(() => RemoveOldLinksAsync(now));
        }

        List<DownloadItem> due;
        lock (_gate)
        {
            due = _items.Values
                .Where(i => i.State == DownloadState.Scheduled && i.ScheduledAt <= now)
                .ToList();
            foreach (var item in due) item.State = DownloadState.Queued;
        }

        foreach (var item in due)
        {
            DownloadItem snapshot;
            lock (_gate) snapshot = CloneItem(item);
            await _repository.UpdateAsync(snapshot);
            NotifyItem(snapshot);
        }

        await PumpAsync();
    }

    private void OnProgressTick()
    {
        if (_disposed) return;

        List<DownloadJob> running;
        lock (_gate) running = _jobs.Values.ToList();

        foreach (var job in running)
        {
            var (bytes, speed) = job.Sample();
            var item = job.Item;
            var progress = new DownloadProgress(job.Id, bytes, item.TotalBytes, speed, item.State);
            Raise(() => ProgressChanged?.Invoke(progress));
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private void Raise(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            // A user-supplied event handler must not bring down the engine.
            LastBackgroundException = ex;
        }
    }

    private void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            LastBackgroundException = ex;
        }
    }

    private async Task GuardAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            LastBackgroundException = ex;
        }
    }
}

/// <summary>Network checks for Wi-Fi only mode (used by <see cref="DefaultNetworkPolicy"/>).</summary>
internal static class NetworkStatus
{
    public static bool IsWifiConnected() => HasUpInterface(NetworkInterfaceType.Wireless80211);

    /// <summary>Wi-Fi or Ethernet is up (mobile data excluded).</summary>
    public static bool IsWifiOrEthernetConnected() =>
        HasUpInterface(NetworkInterfaceType.Wireless80211) || HasUpInterface(NetworkInterfaceType.Ethernet);

    private static bool HasUpInterface(NetworkInterfaceType type)
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Any(n => n.NetworkInterfaceType == type && n.OperationalStatus == OperationalStatus.Up);
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }
}
