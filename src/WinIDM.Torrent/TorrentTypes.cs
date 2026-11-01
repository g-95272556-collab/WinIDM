using MonoTorrent;
using MonoTorrent.Client;
using WinIDM.Core.Models;

namespace WinIDM.Torrent;

/// <summary>Torrent file priority (can be changed during the download).</summary>
public enum TorrentFilePriority { Ignore, Normal, High }

/// <summary>Details of a single file inside a torrent.</summary>
public sealed record TorrentFileInfo(int Index, string Path, long Length, TorrentFilePriority Priority, double Progress);

/// <summary>Torrent summary for the list.</summary>
public sealed record TorrentSummary(long Id, string Name, string InfoHash, DownloadState State, double Progress, bool Paused);

/// <summary>Detailed statistics for a single torrent.</summary>
/// <summary>One connected peer: address, client name, percent complete, speeds in bytes/second, seeder and encryption.</summary>
public sealed record TorrentPeer(string Address, string Client, double Progress, long DownloadBytesPerSecond,
    long UploadBytesPerSecond, bool Seeder, bool Encrypted);

public sealed record TorrentStats(
    long Id,
    string Name,
    string InfoHash,
    DownloadState State,
    double Progress,
    bool Paused,
    bool HasMetadata,
    int Seeds,
    int Leechers,
    int Available,
    int ConnectedPeers,
    long DownloadRate,
    long UploadRate,
    long DownloadLimit,
    long UploadLimit,
    bool Sequential,
    string? LastError);

/// <summary>Mapping between MonoTorrent types and WinIDM types.</summary>
public static class TorrentMapping
{
    /// <summary>Convert a WinIDM priority to a MonoTorrent priority.</summary>
    public static Priority ToMonoPriority(TorrentFilePriority priority) => priority switch
    {
        TorrentFilePriority.Ignore => Priority.DoNotDownload,
        TorrentFilePriority.High => Priority.High,
        _ => Priority.Normal,
    };

    /// <summary>Convert a MonoTorrent priority to a WinIDM priority (Lowest/Low and Highest/Immediate map to Normal/High).</summary>
    public static TorrentFilePriority FromMonoPriority(Priority priority) => priority switch
    {
        Priority.DoNotDownload => TorrentFilePriority.Ignore,
        Priority.High or Priority.Highest or Priority.Immediate => TorrentFilePriority.High,
        _ => TorrentFilePriority.Normal,
    };

    /// <summary>
    /// Convert a MonoTorrent state to <see cref="DownloadState"/>.
    /// Seeding maps to Completed because DownloadState has no seeding state.
    /// </summary>
    public static DownloadState ToDownloadState(TorrentState state, bool paused, double progress, bool hasError)
    {
        if (hasError || state == TorrentState.Error) return DownloadState.Error;
        var complete = progress >= 100.0;
        if (paused) return complete ? DownloadState.Completed : DownloadState.Paused;

        return state switch
        {
            TorrentState.Downloading => DownloadState.Downloading,
            TorrentState.Seeding => DownloadState.Completed,
            TorrentState.Stopped or TorrentState.Paused or TorrentState.HashingPaused or TorrentState.Stopping
                => complete ? DownloadState.Completed : DownloadState.Paused,
            _ => DownloadState.Connecting, // Starting, Metadata, FetchingHashes, Hashing
        };
    }
}
