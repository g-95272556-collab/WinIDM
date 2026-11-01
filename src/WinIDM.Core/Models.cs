namespace WinIDM.Core.Models;

public enum DownloadState { Queued, Scheduled, Connecting, Downloading, Paused, Merging, Completed, Error }

public enum DownloadKind { Http, Hls, Dash, Torrent, Magnet }

public enum SegmentState { Pending, Active, Done, Failed }

/// <summary>Persisted download record (downloads table).</summary>
public sealed class DownloadItem
{
    public long Id { get; set; }
    public DownloadKind Kind { get; set; } = DownloadKind.Http;
    public string Url { get; set; } = "";
    public string? FinalUrl { get; set; }
    public string FileName { get; set; } = "";
    public string SaveDirectory { get; set; } = "";
    public long TotalBytes { get; set; } = -1;
    public long DownloadedBytes { get; set; }
    public DownloadState State { get; set; } = DownloadState.Queued;
    public string Category { get; set; } = "Other";
    public string? Referer { get; set; }
    public string? UserAgent { get; set; }
    public string? CookieHeader { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new();
    public string? Username { get; set; }
    public string? Password { get; set; }
    public int MaxSegments { get; set; } = 7;
    public long SpeedLimitBytesPerSec { get; set; }
    public bool AcceptsRanges { get; set; }
    public string? ETag { get; set; }
    public string? LastModified { get; set; }
    public string? ExpectedSha256 { get; set; }
    public string? ExpectedMd5 { get; set; }
    public string? ErrorMessage { get; set; }
    public int ErrorCode { get; set; }
    public int SortOrder { get; set; }
    public DateTimeOffset? ScheduledAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public string FullPath => Path.Combine(SaveDirectory, FileName);
}

/// <summary>Segment of a single download (threads table). Offset is inclusive [Start, End].</summary>
public sealed class SegmentInfo
{
    public long DownloadId { get; set; }
    public int Index { get; set; }
    public long Start { get; set; }
    public long End { get; set; }
    public long Downloaded { get; set; }
    public SegmentState State { get; set; } = SegmentState.Pending;
}

/// <summary>Request to add a new download.</summary>
public sealed class DownloadRequest
{
    public string Url { get; set; } = "";
    public string? FileName { get; set; }
    public string? SaveDirectory { get; set; }
    public string? Referer { get; set; }
    public string? UserAgent { get; set; }
    public string? CookieHeader { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new();
    public string? Username { get; set; }
    public string? Password { get; set; }
    public int? MaxSegments { get; set; }
    public bool StartImmediately { get; set; } = true;
    public DateTimeOffset? ScheduledAt { get; set; }
}

public sealed record DownloadProgress(long Id, long DownloadedBytes, long TotalBytes, double BytesPerSecond, DownloadState State);

/// <summary>Media detected in the browser.</summary>
public sealed record DetectedMedia(string Url, string? PageUrl, string? Title, string? MimeType, long? SizeBytes, DownloadKind Kind, string? Quality, string? SourceFileName = null);

/// <summary>Categories of backup content.</summary>
[Flags]
public enum BackupCategories
{
    None = 0, Settings = 1, Downloads = 2, Bookmarks = 4, History = 8, Passwords = 16,
    SavedPages = 32, DomainLimits = 64, Adblock = 128, UserScripts = 256, All = 511
}

