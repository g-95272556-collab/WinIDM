using CommunityToolkit.Mvvm.ComponentModel;
using WinIDM.App.Services;
using WinIDM.Core.Models;
using WinIDM.Torrent;

namespace WinIDM.App.ViewModels;

/// <summary>File priority option for display (value + label).</summary>
public sealed record TorrentPriorityOption(TorrentFilePriority Value, string Label)
{
    public static IReadOnlyList<TorrentPriorityOption> All { get; } = new[]
    {
        new TorrentPriorityOption(TorrentFilePriority.Ignore, "Ignore"),
        new TorrentPriorityOption(TorrentFilePriority.Normal, "Normal"),
        new TorrentPriorityOption(TorrentFilePriority.High, "High"),
    };
}

/// <summary>Display of one torrent in the list. Updated from <see cref="TorrentStats"/> every second.</summary>
public sealed partial class TorrentRowViewModel : ObservableObject
{
    public TorrentRowViewModel(TorrentStats stats)
    {
        Id = stats.Id;
        Apply(stats);
    }

    public long Id { get; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _infoHash = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isIndeterminate;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _speedText = "-";
    [ObservableProperty] private string _peersText = "";
    [ObservableProperty] private bool _paused;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string? _lastError;

    /// <summary>Update the display fields from the current stats.</summary>
    public void Apply(TorrentStats s)
    {
        Name = s.Name;
        InfoHash = s.InfoHash;
        Paused = s.Paused;
        HasError = !string.IsNullOrEmpty(s.LastError);
        LastError = s.LastError;
        Progress = Math.Clamp(s.Progress, 0, 100);   // TorrentStats.Progress is a percentage (0..100)
        IsIndeterminate = !s.HasMetadata && !s.Paused && !HasError;
        ProgressText = s.HasMetadata ? $"{Progress:0.#}%" : "metadata...";
        SpeedText = s.State == DownloadState.Downloading
            ? Fmt.Speed(s.DownloadRate)
            : "-";
        PeersText = $"{s.Seeds} seed / {s.Leechers} peer";
        StatusText = StatusOf(s);
    }

    private static string StatusOf(TorrentStats s)
    {
        if (!string.IsNullOrEmpty(s.LastError)) return $"Error: {s.LastError}";
        return s.State switch
        {
            DownloadState.Paused => "Paused",
            DownloadState.Completed => "Completed",
            DownloadState.Downloading => s.HasMetadata ? "Downloading" : "Fetching metadata",
            DownloadState.Queued => "Queued",
            DownloadState.Connecting => "Connecting",
            DownloadState.Error => "Error",
            _ => s.State.ToString(),
        };
    }
}

/// <summary>One file in the selected torrent, with a changeable priority.</summary>
public sealed partial class TorrentFileRowViewModel : ObservableObject
{
    public TorrentFileRowViewModel(TorrentFileInfo info)
    {
        Index = info.Index;
        Path = info.Path;
        SizeText = Fmt.Bytes(info.Length);
        Apply(info);
    }

    public int Index { get; }
    public string Path { get; }
    public string SizeText { get; }

    [ObservableProperty] private TorrentFilePriority _priority;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private double _progress;

    /// <summary>Update progress and priority from the engine (does not trigger a callback).</summary>
    public void Apply(TorrentFileInfo info)
    {
        Priority = info.Priority;
        Progress = Math.Clamp(info.Progress, 0, 100);
        ProgressText = $"{Progress:0.#}%";
    }
}
