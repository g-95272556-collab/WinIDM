using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.PiecePicking;
using WinIDM.Core.Contracts;
using WinIDM.Core.Models;
using MTorrent = MonoTorrent.Torrent;

namespace WinIDM.Torrent;

/// <summary>
/// MonoTorrent-based torrent service. Internal ids start at 1; the App maps these ids
/// to its own <see cref="DownloadItem.Id"/>. State (torrent list, priorities, limits, trackers)
/// is stored in the data folder so it is restored after a restart; MonoTorrent fast-resume data is
/// stored in the cache subfolder.
/// </summary>
public sealed class TorrentService : ITorrentService, IAsyncDisposable
{
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan AnnounceTimeout = TimeSpan.FromSeconds(30);

    private readonly ISettingsStore _settings;
    private readonly string _dataDirectory;
    private readonly string _torrentDirectory;
    private readonly string _cacheDirectory;
    private readonly TorrentRegistry _registry;
    private readonly PeerConnectionGate _peerGate = new();

    /// <summary>Serializes all state-changing operations (async).</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Protects <see cref="_entries"/>, <see cref="_nextId"/> and registry writes (synchronous, no await).</summary>
    private readonly object _sync = new();
    private readonly Dictionary<long, Entry> _entries = new();

    private TorrentConfiguration _config;
    private ClientEngine? _engine;
    private volatile bool _initialized;
    private volatile bool _disposed;
    private long _nextId = 1;
    private int _globalDownloadLimit;
    private int _globalUploadLimit;
    private int _maintenanceRunning;
    private System.Threading.Timer? _timer;

    /// <summary>Last IP filter error (null if it succeeded or filtering is not enabled).</summary>
    public string? IpFilterError { get; private set; }

    public event Action<DownloadProgress>? ProgressChanged;

    /// <summary>The service uses the default data folder %LOCALAPPDATA%\WinIDM\Torrent.</summary>
    public TorrentService(ISettingsStore settings)
        : this(settings, DefaultDataDirectory())
    {
    }

    /// <summary>The service uses the given data folder for state, .torrent copies and the cache.</summary>
    public TorrentService(ISettingsStore settings, string dataDirectory)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        if (string.IsNullOrWhiteSpace(dataDirectory)) throw new ArgumentException("Data folder is required.", nameof(dataDirectory));

        _dataDirectory = Path.GetFullPath(dataDirectory);
        _torrentDirectory = Path.Combine(_dataDirectory, "torrents");
        _cacheDirectory = Path.Combine(_dataDirectory, "cache");
        _registry = new TorrentRegistry(Path.Combine(_dataDirectory, "torrents.json"));
        _config = TorrentConfiguration.FromSettings(settings);
        _globalDownloadLimit = _config.GlobalDownloadLimit;
        _globalUploadLimit = _config.GlobalUploadLimit;
    }

    public static string DefaultDataDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinIDM", "Torrent");

    public string DataDirectory => _dataDirectory;

    /// <summary>Underlying MonoTorrent engine (accessed by tests to verify configuration). Null before initialization.</summary>
    internal ClientEngine? Engine => _engine;

    /// <summary>Configuration used by the engine (read during <see cref="InitializeAsync"/>).</summary>
    public TorrentConfiguration Configuration => _config;

    /// <summary>Number of ranges in the current IP filter.</summary>
    public int IpFilterRangeCount => _peerGate.Filter.RangeCount;

    /// <summary>
    /// Start the engine and restore torrents from the data folder. Called automatically
    /// by the first operation if it has not been called yet.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            ThrowIfDisposed();

            Directory.CreateDirectory(_dataDirectory);
            Directory.CreateDirectory(_torrentDirectory);
            Directory.CreateDirectory(_cacheDirectory);

            _config = TorrentConfiguration.FromSettings(_settings);
            _globalDownloadLimit = _config.GlobalDownloadLimit;
            _globalUploadLimit = _config.GlobalUploadLimit;
            ReloadIpFilter();

            var port = _config.UseRandomPort ? PickFreePort() : _config.CustomPort;
            _engine ??= new ClientEngine(BuildEngineSettings(port), BuildFactories());

            var document = _registry.Load();
            lock (_sync)
            {
                var maxId = document.Items.Count == 0 ? 0 : document.Items.Max(r => r.Id);
                _nextId = Math.Max(Math.Max(document.NextId, 1), maxId + 1);
                foreach (var record in document.Items)
                {
                    _entries[record.Id] = new Entry { Record = record };
                }
            }

            List<Entry> restore;
            lock (_sync) restore = _entries.Values.OrderBy(e => e.Record.Id).ToList();
            foreach (var entry in restore)
            {
                try
                {
                    await AddEntryToEngineAsync(entry).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    entry.LastError = ex.Message;
                }
            }

            SaveRegistry();
            _timer = new System.Threading.Timer(OnTick, null, TickInterval, TickInterval);
            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---------------------------------------------------------------- ITorrentService

    public async Task<long> AddMagnetAsync(string magnet, string saveDirectory, CancellationToken ct = default)
    {
        EnsureTorrentEnabled();
        if (string.IsNullOrWhiteSpace(magnet)) throw new ArgumentException("Magnet link is required.", nameof(magnet));
        if (string.IsNullOrWhiteSpace(saveDirectory)) throw new ArgumentException("Save folder is required.", nameof(saveDirectory));
        if (!MagnetLink.TryParse(magnet.Trim(), out var link) || link is null)
            throw new ArgumentException("Invalid magnet link.", nameof(magnet));

        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        // Fetch metadata before holding the gate so other operations are not blocked for up to 60 seconds.
        byte[]? metadata = _config.FetchMetadata ? await TryFetchMetadataAsync(link, ct).ConfigureAwait(false) : null;
        var extraTrackers = await ExtraTrackersAsync(ct).ConfigureAwait(false);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var hash = InfoHashHex(link.InfoHashes);
            if (await MergeIntoDuplicateAsync(hash, link.AnnounceUrls ?? []).ConfigureAwait(false) is { } existingId)
                return existingId;

            var id = AllocateId();
            var record = new TorrentRecord
            {
                Id = id,
                Kind = "Magnet",
                MagnetUri = magnet.Trim(),
                SaveDirectory = Path.GetFullPath(saveDirectory),
                Name = link.Name ?? "",
                InfoHash = hash,
                Sequential = _config.SequentialDownload,
                AddedAt = DateTimeOffset.UtcNow,
            };

            if (metadata != null)
            {
                var fileName = $"{id}.torrent";
                File.WriteAllBytes(Path.Combine(_torrentDirectory, fileName), metadata);
                record.TorrentFile = fileName;
                if (MTorrent.TryLoad(metadata.AsSpan(), out var loaded) && loaded is not null)
                    record.Name = loaded.Name;
            }

            var entry = new Entry { Record = record };
            try
            {
                await AddEntryToEngineAsync(entry).ConfigureAwait(false);
                await ApplyAddTimeTrackersAsync(entry, extraTrackers).ConfigureAwait(false);
            }
            catch
            {
                DeleteIfExists(ResolveTorrentFile(record));
                throw;
            }

            lock (_sync) _entries[id] = entry;
            SaveRegistry();
            return id;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<long> AddTorrentFileAsync(string torrentPath, string saveDirectory, CancellationToken ct = default)
    {
        EnsureTorrentEnabled();
        if (string.IsNullOrWhiteSpace(torrentPath)) throw new ArgumentException(".torrent path is required.", nameof(torrentPath));
        if (string.IsNullOrWhiteSpace(saveDirectory)) throw new ArgumentException("Save folder is required.", nameof(saveDirectory));
        if (!File.Exists(torrentPath)) throw new FileNotFoundException(".torrent file not found.", torrentPath);

        var parsed = await MTorrent.LoadAsync(torrentPath).ConfigureAwait(false);
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var extraTrackers = await ExtraTrackersAsync(ct).ConfigureAwait(false);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var hash = InfoHashHex(parsed.InfoHashes);
            if (await MergeIntoDuplicateAsync(hash, parsed.AnnounceUrls.SelectMany(t => t)).ConfigureAwait(false) is { } existingId)
                return existingId;

            var id = AllocateId();
            var fileName = $"{id}.torrent";
            File.Copy(torrentPath, Path.Combine(_torrentDirectory, fileName), overwrite: true);

            var record = new TorrentRecord
            {
                Id = id,
                Kind = "File",
                TorrentFile = fileName,
                SaveDirectory = Path.GetFullPath(saveDirectory),
                Name = parsed.Name,
                InfoHash = hash,
                Sequential = _config.SequentialDownload,
                AddedAt = DateTimeOffset.UtcNow,
            };

            var entry = new Entry { Record = record };
            try
            {
                await AddEntryToEngineAsync(entry).ConfigureAwait(false);
                await ApplyAddTimeTrackersAsync(entry, extraTrackers).ConfigureAwait(false);
            }
            catch
            {
                DeleteIfExists(ResolveTorrentFile(record));
                throw;
            }

            lock (_sync) _entries[id] = entry;
            SaveRegistry();
            return id;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task PauseAsync(long id)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var entry = RequireEntry(id);
            entry.Record.Paused = true;
            if (entry.Manager is { } manager && manager.State != TorrentState.Stopped)
            {
                // StopAsync saves fast-resume; the "paused" state is kept in the registry.
                await manager.StopAsync().ConfigureAwait(false);
            }
            SaveRegistry();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ResumeAsync(long id)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var entry = RequireEntry(id);
            entry.Record.Paused = false;
            if (entry.Manager is null)
            {
                // Initial restore failed: try adding it again now.
                entry.LastError = null;
                await AddEntryToEngineAsync(entry).ConfigureAwait(false);
            }
            else if (entry.Manager.State == TorrentState.Stopped)
            {
                await entry.Manager.StartAsync().ConfigureAwait(false);
            }
            SaveRegistry();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(long id, bool deleteFiles)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var entry = RequireEntry(id);
            var manager = entry.Manager;
            var filePaths = manager?.Files.Select(f => f.FullPath).ToList() ?? new List<string>();
            var containing = manager?.ContainingDirectory;

            if (manager != null && _engine != null)
            {
                if (manager.State != TorrentState.Stopped) await manager.StopAsync().ConfigureAwait(false);
                await _engine.RemoveAsync(manager, deleteFiles
                    ? RemoveMode.CacheDataAndDownloadedData
                    : RemoveMode.KeepAllData).ConfigureAwait(false);
            }

            if (deleteFiles)
            {
                foreach (var path in filePaths) DeleteIfExists(path);
                DeleteEmptyDirectory(containing, entry.Record.SaveDirectory);
            }

            DeleteIfExists(ResolveTorrentFile(entry.Record));
            lock (_sync) _entries.Remove(id);
            SaveRegistry();
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---------------------------------------------------------------- additional API

    /// <summary>Brief list of all managed torrents.</summary>
    public IReadOnlyList<TorrentSummary> GetTorrents()
    {
        lock (_sync)
        {
            return _entries.Values
                .OrderBy(e => e.Record.Id)
                .Select(e =>
                {
                    var progress = e.Manager?.Progress ?? 0;
                    var state = TorrentMapping.ToDownloadState(
                        e.Manager?.State ?? TorrentState.Stopped,
                        e.Record.Paused,
                        progress,
                        e.LastError != null);
                    return new TorrentSummary(e.Record.Id, DisplayName(e), e.Record.InfoHash, state, progress, e.Record.Paused);
                })
                .ToList();
        }
    }

    public TorrentStats GetStats(long id)
    {
        var entry = RequireEntry(id);
        var manager = entry.Manager;
        var progress = manager?.Progress ?? 0;
        return new TorrentStats(
            Id: id,
            Name: DisplayName(entry),
            InfoHash: entry.Record.InfoHash,
            State: TorrentMapping.ToDownloadState(manager?.State ?? TorrentState.Stopped, entry.Record.Paused, progress, entry.LastError != null),
            Progress: progress,
            Paused: entry.Record.Paused,
            HasMetadata: manager?.HasMetadata ?? false,
            Seeds: manager?.Peers.Seeds ?? 0,
            Leechers: manager?.Peers.Leechs ?? 0,
            Available: manager?.Peers.Available ?? 0,
            ConnectedPeers: manager?.OpenConnections ?? 0,
            DownloadRate: manager?.Monitor.DownloadRate ?? 0,
            UploadRate: manager?.Monitor.UploadRate ?? 0,
            DownloadLimit: entry.Record.DownloadLimit,
            UploadLimit: entry.Record.UploadLimit,
            Sequential: entry.Record.Sequential,
            LastError: entry.LastError);
    }

    /// <summary>File list with current priorities and per-file progress. Empty if metadata is not available yet.</summary>
    public IReadOnlyList<TorrentFileInfo> GetFiles(long id)
    {
        var entry = RequireEntry(id);
        var manager = entry.Manager;
        if (manager is null || !manager.HasMetadata) return Array.Empty<TorrentFileInfo>();

        var bitfield = manager.Bitfield;
        var files = manager.Files;
        var result = new List<TorrentFileInfo>(files.Count);
        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var pieces = file.EndPieceIndex - file.StartPieceIndex + 1;
            var done = 0;
            for (var p = file.StartPieceIndex; p <= file.EndPieceIndex; p++)
            {
                if (bitfield[p]) done++;
            }
            var progress = pieces <= 0 ? 0 : done * 100.0 / pieces;
            result.Add(new TorrentFileInfo(i, file.Path, file.Length, TorrentMapping.FromMonoPriority(file.Priority), progress));
        }
        return result;
    }

    /// <summary>Change the priority of a single file. Can be done while the download is running.</summary>
    public async Task SetFilePriorityAsync(long id, int fileIndex, TorrentFilePriority priority)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var entry = RequireEntry(id);
            var manager = entry.Manager is { HasMetadata: true } m ? m : null;
            var fileCount = manager?.Files.Count ?? -1;
            if (fileIndex < 0 || (manager != null && fileIndex >= fileCount))
                throw new ArgumentOutOfRangeException(nameof(fileIndex));

            entry.Record.FilePriorities[fileIndex] = priority;
            if (manager != null)
            {
                await manager.SetFilePriorityAsync(manager.Files[fileIndex], TorrentMapping.ToMonoPriority(priority)).ConfigureAwait(false);
            }
            SaveRegistry();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Sequential download: pick pieces in file order (without rarest-first or random).</summary>
    public async Task SetSequentialDownloadAsync(long id, bool enabled)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var entry = RequireEntry(id);
            entry.Record.Sequential = enabled;
            if (entry.Manager is { HasMetadata: true } manager)
            {
                await ChangePickerSafeAsync(manager, enabled, restart: !entry.Record.Paused).ConfigureAwait(false);
            }
            SaveRegistry();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Per-torrent speed limit in bytes/second. 0 means unlimited. Takes effect immediately: a running
    /// torrent is stopped briefly, its settings are updated, then it is started again.
    /// </summary>
    public async Task SetSpeedLimitsAsync(long id, long downloadBytesPerSecond, long uploadBytesPerSecond)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var entry = RequireEntry(id);
            entry.Record.DownloadLimit = Math.Max(0, downloadBytesPerSecond);
            entry.Record.UploadLimit = Math.Max(0, uploadBytesPerSecond);
            try
            {
                if (entry.Manager is not null) await ApplyTransferLimitsAsync(entry.Manager, entry.Record).ConfigureAwait(false);
            }
            finally
            {
                SaveRegistry();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Apply the speed limits to an existing manager. A running manager is stopped first,
    /// because <c>UpdateSettingsAsync</c> does not apply limits while running in MonoTorrent 3.0.2.
    /// Must be called while holding the gate. Do not use <c>RemoveAsync</c> for this: it breaks the speed
    /// limits of the same engine afterwards.
    /// </summary>
    private static async Task ApplyTransferLimitsAsync(TorrentManager manager, TorrentRecord record)
    {
        var wasRunning = manager.State != TorrentState.Stopped;
        if (wasRunning) await manager.StopAsync().ConfigureAwait(false);
        await manager.UpdateSettingsAsync(new TorrentSettingsBuilder(manager.Settings)
        {
            MaximumDownloadRate = ClampRate(record.DownloadLimit),
            MaximumUploadRate = ClampRate(record.UploadLimit),
        }.ToSettings()).ConfigureAwait(false);
        if (wasRunning) await manager.StartAsync().ConfigureAwait(false);
    }

    /// <summary>Global speed limit in bytes/second (runtime; re-read from settings after a restart).</summary>
    public async Task SetGlobalSpeedLimitsAsync(long downloadBytesPerSecond, long uploadBytesPerSecond)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _globalDownloadLimit = ClampRate(downloadBytesPerSecond);
            _globalUploadLimit = ClampRate(uploadBytesPerSecond);
            var engine = RequireEngine();
            await engine.UpdateSettingsAsync(new EngineSettingsBuilder(engine.Settings)
            {
                MaximumDownloadRate = _globalDownloadLimit,
                MaximumUploadRate = _globalUploadLimit,
            }.ToSettings()).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Recheck (full hash check) the torrent pieces. The torrent is stopped first, then started again if it is not paused.</summary>
    public async Task RecheckAsync(long id)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var entry = RequireEntry(id);
            if (entry.Manager is not { } manager) throw new InvalidOperationException("Torrent is not loaded into the engine yet.");
            if (manager.State != TorrentState.Stopped) await manager.StopAsync().ConfigureAwait(false);
            await manager.HashCheckAsync(autoStart: !entry.Record.Paused).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Announce again to all trackers of this torrent.</summary>
    public async Task ReannounceAsync(long id)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        var entry = RequireEntry(id);
        if (entry.Manager is not { } manager) throw new InvalidOperationException("Torrent is not loaded into the engine yet.");

        using var cts = new CancellationTokenSource(AnnounceTimeout);
        await manager.TrackerManager.AnnounceAsync(cts.Token).ConfigureAwait(false);
    }

    /// <summary>Current tracker URL list.</summary>
    /// <summary>Connected peers of a torrent (IDM+ "Display peer info"). Empty while the torrent is not running.</summary>
    public async Task<IReadOnlyList<TorrentPeer>> GetPeersAsync(long id)
    {
        var entry = RequireEntry(id);
        if (entry.Manager is not { } manager) return [];
        var peers = await manager.GetPeersAsync().ConfigureAwait(false);
        return peers.Select(p => new TorrentPeer(
                p.Uri.ToString(),
                p.ClientApp.ToString(),
                p.BitField.PercentComplete,
                p.Monitor.DownloadRate,
                p.Monitor.UploadRate,
                p.IsSeeder,
                p.EncryptionType != MonoTorrent.Connections.EncryptionType.PlainText))
            .OrderByDescending(p => p.DownloadBytesPerSecond)
            .ToList();
    }

    public IReadOnlyList<string> GetTrackers(long id)
    {
        var entry = RequireEntry(id);
        if (entry.Manager is null) return entry.Record.Trackers ?? new List<string>();
        return entry.Manager.TrackerManager.Tiers
            .SelectMany(t => t.Trackers)
            .Select(t => t.Uri.ToString())
            .Distinct()
            .ToList();
    }

    /// <summary>Replace the tracker list with a new list (add new ones, remove missing ones).</summary>
    public async Task SetTrackersAsync(long id, IEnumerable<string> trackerUrls)
    {
        ArgumentNullException.ThrowIfNull(trackerUrls);
        var desired = trackerUrls
            .Select(u => u?.Trim() ?? "")
            .Where(u => u.Length > 0)
            .Where(u => Uri.TryCreate(u, UriKind.Absolute, out var uri)
                        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == "udp"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        await EnsureInitializedAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var entry = RequireEntry(id);
            entry.Record.Trackers = desired;
            if (entry.Manager is { } manager)
            {
                await ApplyTrackersAsync(manager, desired).ConfigureAwait(false);
            }
            SaveRegistry();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Copy this torrent's .torrent file to destinationPath.</summary>
    public async Task SaveTorrentFileAsync(long id, string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(destinationPath)) throw new ArgumentException("Destination path is required.", nameof(destinationPath));
        await EnsureInitializedAsync().ConfigureAwait(false);

        string? source;
        lock (_sync)
        {
            var entry = RequireEntryLocked(id);
            source = ResolveTorrentFile(entry.Record);
            if ((source is null || !File.Exists(source)) && entry.Manager is { } manager
                && !string.IsNullOrEmpty(manager.MetadataPath) && File.Exists(manager.MetadataPath))
            {
                source = manager.MetadataPath;
            }
        }

        if (source is null || !File.Exists(source))
            throw new InvalidOperationException(".torrent metadata is not available for this torrent yet.");

        var destination = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await Task.Run(() => File.Copy(source, destination, overwrite: true)).ConfigureAwait(false);
    }

    /// <summary>Magnet link of the torrent (v1). Magnet torrents keep the original link with trackers.</summary>
    public string? GetMagnetLink(long id)
    {
        Entry entry;
        lock (_sync) entry = RequireEntryLocked(id);

        if (entry.Record.Kind == "Magnet" && !string.IsNullOrEmpty(entry.Record.MagnetUri))
            return entry.Record.MagnetUri;

        var sb = new System.Text.StringBuilder("magnet:?xt=urn:btih:").Append(entry.Record.InfoHash);
        if (!string.IsNullOrEmpty(entry.Record.Name)) sb.Append("&dn=").Append(Uri.EscapeDataString(entry.Record.Name));
        if (entry.Manager is { } manager)
        {
            foreach (var tracker in manager.TrackerManager.Tiers.SelectMany(t => t.Trackers).Select(t => t.Uri.ToString()).Distinct())
                sb.Append("&tr=").Append(Uri.EscapeDataString(tracker));
        }
        return sb.ToString();
    }

    /// <summary>Reload the IP filter from settings (enable_ip_filtering and ip_filter_path).</summary>
    public void ReloadIpFilter()
    {
        var enabled = _settings.Get(TorrentSettingKeys.EnableIpFiltering, false);
        var path = (_settings.Get<string?>(TorrentSettingKeys.IpFilterPath, null) ?? "").Trim();

        IpFilterError = null;
        if (!enabled || path.Length == 0)
        {
            _peerGate.Filter = IpFilter.Empty;
            return;
        }

        try
        {
            _peerGate.Filter = IpFilter.FromFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _peerGate.Filter = IpFilter.Empty;
            IpFilterError = ex.Message;
        }
    }

    /// <summary>Replace the IP filter with an already loaded object (for example for tests or another source).</summary>
    public void UseIpFilter(IpFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        IpFilterError = null;
        _peerGate.Filter = filter;
    }

    // ---------------------------------------------------------------- internals

    private sealed class Entry
    {
        public required TorrentRecord Record { get; init; }
        public TorrentManager? Manager { get; set; }
        public string? LastError { get; set; }

        /// <summary>File priorities and picker have been applied (after metadata becomes available).</summary>
        public bool PendingApplied { get; set; }
    }

    private void EnsureTorrentEnabled()
    {
        if (_config.Disabled) throw new InvalidOperationException("Torrent support is disabled in settings.");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(TorrentService));
    }

    private async Task EnsureInitializedAsync(CancellationToken ct = default)
    {
        if (!_initialized) await InitializeAsync(ct).ConfigureAwait(false);
    }

    private ClientEngine RequireEngine() =>
        _engine ?? throw new InvalidOperationException("Torrent engine has not been started yet.");

    private long AllocateId()
    {
        lock (_sync) return _nextId++;
    }

    /// <summary>Public trackers added to new torrents when "Use default trackers" is on.</summary>
    public static readonly IReadOnlyList<string> DefaultTrackers =
    [
        "udp://tracker.opentrackr.org:1337/announce",
        "udp://open.stealth.si:80/announce",
        "udp://tracker.torrent.eu.org:451/announce",
        "udp://exodus.desync.com:6969/announce",
        "udp://tracker.openbittorrent.com:6969/announce",
        "udp://open.demonii.com:1337/announce",
        "https://tracker.tamersunion.org/announce",
    ];

    private static readonly HttpClient TrackerListClient = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly TimeSpan TrackerListLifetime = TimeSpan.FromHours(6);
    private (string Url, DateTimeOffset At, List<string> Trackers)? _trackerList;

    private static bool IsTrackerUrl(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == "udp");

    /// <summary>Trackers added to every new torrent: the default list and the list fetched from "dturl" (cached).</summary>
    private async Task<List<string>> ExtraTrackersAsync(CancellationToken ct)
    {
        var list = new List<string>();
        if (_config.UseDefaultTrackers) list.AddRange(DefaultTrackers);

        var url = _config.TrackerListUrl;
        if (Uri.TryCreate(url, UriKind.Absolute, out var source) && source.Scheme is "http" or "https")
        {
            if (_trackerList is { } cached && cached.Url == url && DateTimeOffset.UtcNow - cached.At < TrackerListLifetime)
            {
                list.AddRange(cached.Trackers);
            }
            else
            {
                try
                {
                    var text = await TrackerListClient.GetStringAsync(source, ct).ConfigureAwait(false);
                    var fetched = text.Split('\n').Select(l => l.Trim()).Where(IsTrackerUrl).Take(500).ToList();
                    _trackerList = (url, DateTimeOffset.UtcNow, fetched);
                    list.AddRange(fetched);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    // An unreachable list never blocks adding the torrent.
                }
            }
        }

        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> CurrentTrackers(Entry entry) =>
        entry.Manager is { } m
            ? m.TrackerManager.Tiers.SelectMany(t => t.Trackers).Select(t => t.Uri.ToString()).ToList()
            : entry.Record.Trackers ?? [];

    /// <summary>Adds the extra trackers and, with "Always contact all trackers", gives each tracker its own tier.</summary>
    private async Task ApplyAddTimeTrackersAsync(Entry entry, IReadOnlyList<string> extra)
    {
        if (entry.Manager is not { } manager) return;
        var all = CurrentTrackers(entry).Concat(extra).Where(IsTrackerUrl).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (extra.Count > 0)
        {
            entry.Record.Trackers = all;
            await ApplyTrackersAsync(manager, all).ConfigureAwait(false);
        }

        // MonoTorrent announces to one tracker per tier; one tracker per tier means every tracker is contacted.
        if (_config.AlwaysContactAllTrackers && manager.TrackerManager.Tiers.Any(t => t.Trackers.Count > 1))
        {
            foreach (var tracker in manager.TrackerManager.Tiers.SelectMany(t => t.Trackers).ToList())
                await manager.TrackerManager.RemoveTrackerAsync(tracker).ConfigureAwait(false);
            foreach (var url in all)
                await manager.TrackerManager.AddTrackerAsync(new Uri(url)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A torrent that is already in the list: with "Automatically add trackers to duplicate torrents" its trackers are
    /// merged into the existing torrent and that id is returned; otherwise the add fails as before. Null = not a duplicate.
    /// </summary>
    private async Task<long?> MergeIntoDuplicateAsync(string infoHash, IEnumerable<string> trackers)
    {
        Entry? existing;
        lock (_sync)
            existing = _entries.Values.FirstOrDefault(e => string.Equals(e.Record.InfoHash, infoHash, StringComparison.OrdinalIgnoreCase));
        if (existing is null) return null;
        if (!_config.AddTrackersToDuplicates) EnsureNotDuplicate(infoHash);

        var merged = CurrentTrackers(existing).Concat(trackers).Where(IsTrackerUrl)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        existing.Record.Trackers = merged;
        if (existing.Manager is { } manager) await ApplyTrackersAsync(manager, merged).ConfigureAwait(false);
        SaveRegistry();
        return existing.Record.Id;
    }

    private void EnsureNotDuplicate(string infoHash)
    {
        lock (_sync)
        {
            var existing = _entries.Values.FirstOrDefault(e => string.Equals(e.Record.InfoHash, infoHash, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
                throw new InvalidOperationException($"This torrent already exists (id {existing.Record.Id}).");
        }
    }

    private Entry RequireEntryLocked(long id)
    {
        if (_entries.TryGetValue(id, out var entry)) return entry;
        throw new KeyNotFoundException($"Torrent with id {id} does not exist.");
    }

    private Entry RequireEntry(long id)
    {
        lock (_sync) return RequireEntryLocked(id);
    }

    private static string DisplayName(Entry entry) =>
        string.IsNullOrWhiteSpace(entry.Record.Name) ? entry.Record.InfoHash : entry.Record.Name;

    private static string InfoHashHex(InfoHashes hashes)
    {
        return hashes.V1OrV2.ToHex().ToLowerInvariant();
    }

    private string? ResolveTorrentFile(TorrentRecord record) =>
        string.IsNullOrEmpty(record.TorrentFile) ? null : Path.Combine(_torrentDirectory, record.TorrentFile);

    private async Task<byte[]?> TryFetchMetadataAsync(MagnetLink link, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(MetadataTimeout);
            var engine = RequireEngine();
            var data = await engine.DownloadMetadataAsync(link, cts.Token).ConfigureAwait(false);
            return data.ToArray();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // timeout: the magnet link will be started without metadata
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Create a TorrentManager from the record and hook events. Must be called while holding the gate.</summary>
    private async Task AddEntryToEngineAsync(Entry entry)
    {
        var record = entry.Record;
        Directory.CreateDirectory(record.SaveDirectory);

        var engine = RequireEngine();
        var settings = BuildTorrentSettings(record);
        var torrentFile = ResolveTorrentFile(record);
        TorrentManager manager;
        if (torrentFile != null && File.Exists(torrentFile))
        {
            manager = await engine.AddAsync(torrentFile, record.SaveDirectory, settings).ConfigureAwait(false);
        }
        else
        {
            if (string.IsNullOrEmpty(record.MagnetUri)) throw new InvalidOperationException("Torrent record has no source.");
            manager = await engine.AddAsync(MagnetLink.Parse(record.MagnetUri), record.SaveDirectory, settings).ConfigureAwait(false);
        }

        entry.Manager = manager;
        entry.LastError = null;
        HookEvents(entry);

        if (record.Trackers != null)
        {
            await ApplyTrackersAsync(manager, record.Trackers).ConfigureAwait(false);
        }

        if (manager.HasMetadata)
        {
            await ApplyPendingSettingsAsync(entry).ConfigureAwait(false);
        }

        if (!record.Paused)
        {
            await manager.StartAsync().ConfigureAwait(false);
        }
    }

    private void HookEvents(Entry entry)
    {
        var manager = entry.Manager!;
        var id = entry.Record.Id;
        manager.TorrentStateChanged += (_, e) =>
        {
            if (e.NewState == TorrentState.Seeding && !_config.SeedFinishedTorrents)
            {
                _ = StopAfterCompletionAsync(id);
            }
        };
    }

    private async Task StopAfterCompletionAsync(long id)
    {
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed) return;
                Entry? entry;
                lock (_sync) _entries.TryGetValue(id, out entry);
                if (entry?.Manager is { } manager && manager.State == TorrentState.Seeding && !_config.SeedFinishedTorrents)
                {
                    await manager.StopAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception)
        {
            // A stop error after completion must not bring down the process; state is reported through progress.
        }
    }

    private async Task ApplyPendingSettingsAsync(Entry entry)
    {
        var manager = entry.Manager!;
        var files = manager.Files;
        foreach (var (index, priority) in entry.Record.FilePriorities)
        {
            if (index >= 0 && index < files.Count)
            {
                await manager.SetFilePriorityAsync(files[index], TorrentMapping.ToMonoPriority(priority)).ConfigureAwait(false);
            }
        }

        if (entry.Record.Sequential)
        {
            await ChangePickerSafeAsync(manager, sequential: true, restart: !entry.Record.Paused).ConfigureAwait(false);
        }

        entry.PendingApplied = true;
    }

    private static async Task ApplyTrackersAsync(TorrentManager manager, IReadOnlyList<string> desired)
    {
        var trackerManager = manager.TrackerManager;
        var desiredUris = desired.Select(u => new Uri(u)).ToList();
        var current = trackerManager.Tiers.SelectMany(t => t.Trackers).ToList();

        foreach (var tracker in current.Where(t => !desiredUris.Contains(t.Uri)).ToList())
        {
            await trackerManager.RemoveTrackerAsync(tracker).ConfigureAwait(false);
        }

        foreach (var uri in desiredUris)
        {
            if (!current.Any(t => t.Uri == uri))
            {
                await trackerManager.AddTrackerAsync(uri).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// MonoTorrent only allows the picker to be changed while the manager is stopped. If the manager is running,
    /// it is stopped, the picker is changed, then it is started again (unless <paramref name="restart"/> is false).
    /// </summary>
    private static async Task ChangePickerSafeAsync(TorrentManager manager, bool sequential, bool restart)
    {
        var wasRunning = manager.State != TorrentState.Stopped;
        if (wasRunning) await manager.StopAsync().ConfigureAwait(false);
        await manager.ChangePickerAsync(CreatePieceRequester(sequential)).ConfigureAwait(false);
        if (wasRunning && restart) await manager.StartAsync().ConfigureAwait(false);
    }

    private static IPieceRequester CreatePieceRequester(bool sequential)
    {
        var defaults = PieceRequesterSettings.Default;
        var settings = sequential
            ? new PieceRequesterSettings(
                allowPrioritisation: defaults.AllowPrioritisation,
                allowRandomised: false,
                allowRarestFirst: false,
                maximumDuplicateRequests: defaults.MaximumDuplicateRequests)
            : defaults;
        return Factories.Default.CreatePieceRequester(settings);
    }

    private TorrentSettings BuildTorrentSettings(TorrentRecord record) => new TorrentSettingsBuilder
    {
        AllowDht = _config.EnableDht,
        AllowPeerExchange = _config.EnablePex,
        MaximumConnections = _config.MaxPeersPerTorrent,
        MaximumDownloadRate = ClampRate(record.DownloadLimit),
        MaximumUploadRate = ClampRate(record.UploadLimit),
        AllowInitialSeeding = false,
    }.ToSettings();

    private EngineSettings BuildEngineSettings(int port)
    {
        var builder = new EngineSettingsBuilder
        {
            CacheDirectory = _cacheDirectory,
            AutoSaveLoadFastResume = true,
            AutoSaveLoadMagnetLinkMetadata = true,
            AutoSaveLoadDhtCache = false,
            AllowLocalPeerDiscovery = _config.EnableLsd,
            AllowPortForwarding = _config.EnableUpnp || _config.EnableNatPmp,
            AllowedEncryption = _config.AllowedEncryption().ToList(),
            MaximumConnections = _config.MaxConnections,
            MaximumDownloadRate = _globalDownloadLimit,
            MaximumUploadRate = _globalUploadLimit,
            // IDM+ "Merge disk writes: buffer size" (torrent_merge_buffer_size).
            DiskCacheBytes = _config.WriteBufferBytes,
            ListenEndPoints = new Dictionary<string, IPEndPoint>
            {
                ["ipv4"] = new IPEndPoint(IPAddress.Any, port),
            },
        };
        return builder.ToSettings();
    }

    private Factories BuildFactories()
    {
        var userAgent = _config.UserAgent;
        return Factories.Default
            .WithPeerConnectionCreator("ipv4", uri => _peerGate.Wrap(Factories.Default.CreatePeerConnection(uri)!))
            .WithPeerConnectionCreator("ipv6", uri => _peerGate.Wrap(Factories.Default.CreatePeerConnection(uri)!))
            .WithPeerConnectionListenerCreator(endPoint =>
                new FilteredPeerConnectionListener(Factories.Default.CreatePeerConnectionListener(endPoint), _peerGate))
            .WithDhtCreator(() => _config.EnableDht ? Factories.Default.CreateDht() : new DisabledDhtEngine())
            .WithHttpClientCreator(family =>
            {
                var client = Factories.Default.CreateHttpClient(family);
                if (userAgent.Length > 0 && ProductInfoHeaderValue.TryParse(userAgent, out var product))
                {
                    client.DefaultRequestHeaders.UserAgent.Clear();
                    client.DefaultRequestHeaders.UserAgent.Add(product);
                }
                return client;
            });
    }

    private static int PickFreePort()
    {
        var listener = new TcpListener(IPAddress.Any, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static int ClampRate(long bytesPerSecond) =>
        (int)Math.Clamp(bytesPerSecond, 0, int.MaxValue);

    private void SaveRegistry()
    {
        lock (_sync)
        {
            var document = new TorrentRegistryDocument
            {
                NextId = _nextId,
                Items = _entries.Values.Select(e => e.Record).OrderBy(r => r.Id).ToList(),
            };
            _registry.Save(document);
        }
    }

    private void OnTick(object? _)
    {
        if (_disposed) return;

        List<(long Id, Entry Entry, TorrentManager? Manager)> snapshot;
        lock (_sync)
        {
            // Entries that failed to restore (Manager == null, LastError set) are also reported as Error,
            // so the UI does not assume the torrent is gone or still waiting.
            snapshot = _entries.Values
                .Where(e => e.Manager != null || e.LastError != null)
                .Select(e => (e.Record.Id, e, e.Manager))
                .ToList();
        }

        foreach (var (id, entry, manager) in snapshot)
        {
            try
            {
                if (manager is null)
                {
                    ProgressChanged?.Invoke(new DownloadProgress(id, 0, -1, 0, DownloadState.Error));
                    continue;
                }

                var total = manager.Torrent?.Size ?? -1;
                var progress = Math.Clamp(manager.Progress, 0, 100);
                var done = total > 0 ? (long)Math.Round(total * progress / 100.0) : 0;
                var downloadState = TorrentMapping.ToDownloadState(manager.State, entry.Record.Paused, progress, entry.LastError != null);
                var report = new DownloadProgress(id, done, total, manager.Monitor.DownloadRate, downloadState);
                ProgressChanged?.Invoke(report);
            }
            catch (Exception)
            {
                // A single failed torrent must not stop the reporting of the other torrents.
            }
        }

        if (Interlocked.CompareExchange(ref _maintenanceRunning, 1, 0) == 0)
        {
            _ = MaintenanceAsync();
        }
    }

    /// <summary>Apply pending settings (file priorities, picker, metadata) and save a .torrent copy for magnets that already have metadata.</summary>
    private async Task MaintenanceAsync()
    {
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed) return;
                List<Entry> pending;
                lock (_sync)
                {
                    pending = _entries.Values
                        .Where(e => e.Manager is { HasMetadata: true } && (!e.PendingApplied || e.Record.TorrentFile == null))
                        .ToList();
                }

                foreach (var entry in pending)
                {
                    if (!entry.PendingApplied) await ApplyPendingSettingsAsync(entry).ConfigureAwait(false);

                    var source = entry.Manager!.MetadataPath;
                    if (entry.Record.TorrentFile == null && !string.IsNullOrEmpty(source) && File.Exists(source))
                    {
                        var fileName = $"{entry.Record.Id}.torrent";
                        File.Copy(source, Path.Combine(_torrentDirectory, fileName), overwrite: true);
                        entry.Record.TorrentFile = fileName;
                        SaveRegistry();
                    }
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception)
        {
            // Retried on the next tick.
        }
        finally
        {
            Volatile.Write(ref _maintenanceRunning, 0);
        }
    }

    private static void DeleteIfExists(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // File is in use; left alone.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void DeleteEmptyDirectory(string? containing, string saveDirectory)
    {
        if (string.IsNullOrEmpty(containing)) return;
        var full = Path.GetFullPath(containing);
        var save = Path.GetFullPath(saveDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), save, StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            if (Directory.Exists(full) && !Directory.EnumerateFileSystemEntries(full).Any())
                Directory.Delete(full);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        var timer = _timer;
        _timer = null;
        timer?.Dispose();

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_engine is { } engine)
            {
                try
                {
                    await engine.StopAllAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Engine may already be stopped; existing fast-resume data is kept.
                }

                SaveRegistry();
                engine.Dispose();
                _engine = null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
