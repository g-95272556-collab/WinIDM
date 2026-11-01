using WinIDM.Core.Models;

namespace WinIDM.Core.Contracts;

/// <summary>SQLite storage for downloads and segments.</summary>
public interface IDownloadRepository
{
    Task<long> InsertAsync(DownloadItem item, CancellationToken ct = default);
    Task UpdateAsync(DownloadItem item, CancellationToken ct = default);
    Task DeleteAsync(long id, CancellationToken ct = default);
    Task<DownloadItem?> GetAsync(long id, CancellationToken ct = default);
    Task<IReadOnlyList<DownloadItem>> ListAsync(CancellationToken ct = default);
    Task SaveSegmentsAsync(long downloadId, IReadOnlyList<SegmentInfo> segments, CancellationToken ct = default);
    Task<IReadOnlyList<SegmentInfo>> LoadSegmentsAsync(long downloadId, CancellationToken ct = default);
}

/// <summary>Typed settings with defaults; keys are documented in 03a-jadual-tetapan.md.</summary>
public interface ISettingsStore
{
    T Get<T>(string key, T defaultValue);
    void Set<T>(string key, T value);
    IReadOnlyDictionary<string, string> Snapshot();
    void Restore(IReadOnlyDictionary<string, string> values);
    event Action<string>? Changed;
}

/// <summary>Multi-segment HTTP(S) download engine.</summary>
public interface IDownloadEngine
{
    Task<long> AddAsync(DownloadRequest request, CancellationToken ct = default);
    Task PauseAsync(long id);
    Task ResumeAsync(long id);
    Task RemoveAsync(long id, bool deleteFile);
    Task PauseAllAsync();
    Task StartAllAsync();
    event Action<DownloadProgress>? ProgressChanged;
    event Action<DownloadItem>? ItemChanged;
}

/// <summary>Torrent/magnet service.</summary>
public interface ITorrentService
{
    Task<long> AddMagnetAsync(string magnet, string saveDirectory, CancellationToken ct = default);
    Task<long> AddTorrentFileAsync(string torrentPath, string saveDirectory, CancellationToken ct = default);
    Task PauseAsync(long id);
    Task ResumeAsync(long id);
    Task RemoveAsync(long id, bool deleteFiles);
    event Action<DownloadProgress>? ProgressChanged;
}

public enum ResourceType { Document, Script, Image, Stylesheet, Media, XmlHttpRequest, Other, Subdocument }

/// <summary>Ad/tracker blocker (hosts + ABP/uBO filter lists).</summary>
public interface IAdBlocker
{
    bool ShouldBlock(string requestUrl, string pageUrl, ResourceType type);
    IReadOnlyList<string> CosmeticSelectors(string pageHost);
    void LoadFilterList(string name, string text);
    void LoadHostsList(string name, string text);
    void AddWhitelistHost(string host);
}

/// <summary>Media detector (HLS/DASH/file) based on network requests.</summary>
public interface IMediaDetector
{
    DetectedMedia? Inspect(string url, string? mimeType, long? contentLength, string? pageUrl);
    Task<IReadOnlyList<DetectedMedia>> ExpandPlaylistAsync(string playlistUrl, CancellationToken ct = default);
}

/// <summary>User script manager (GM_* API).</summary>
public interface IUserScriptHost
{
    Task<string> InstallFromUrlAsync(string url, CancellationToken ct = default);
    IReadOnlyList<string> ScriptsForUrl(string pageUrl);
}

/// <summary>Encrypted password vault (DPAPI / AES-GCM).</summary>
public interface IPasswordVault
{
    bool IsUnlocked { get; }
    Task<bool> UnlockAsync(string pin, CancellationToken ct = default);
    Task AddAsync(string origin, string username, string password, CancellationToken ct = default);
    Task<IReadOnlyList<(string Origin, string Username)>> ListAsync(CancellationToken ct = default);
    Task<string?> GetPasswordAsync(string origin, string username, CancellationToken ct = default);
}

/// <summary>ZIP backup/restore; optional password (AES-256-GCM + PBKDF2).</summary>
public interface IBackupService
{
    Task CreateAsync(string destinationPath, BackupCategories categories, string? password, CancellationToken ct = default);
    Task RestoreAsync(string sourcePath, BackupCategories categories, string? password, CancellationToken ct = default);
}
