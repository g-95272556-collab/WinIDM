using CommunityToolkit.Mvvm.ComponentModel;
using WinIDM.App.Services;
using WinIDM.Core.Models;

namespace WinIDM.App.ViewModels;

/// <summary>Display state of one download row. Updated from <see cref="DownloadItem"/> and <see cref="DownloadProgress"/>.</summary>
public sealed partial class DownloadRowViewModel : ObservableObject
{
    private readonly Func<bool> _bitsSpeed;
    private double _bytesPerSecond;

    public DownloadRowViewModel(DownloadItem item, Func<bool> bitsSpeed)
    {
        _bitsSpeed = bitsSpeed;
        Item = item;
        Refresh();
    }

    public DownloadItem Item { get; private set; }
    public long Id => Item.Id;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _url = "";
    [ObservableProperty] private DownloadState _state;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _kindText = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isIndeterminate;
    [ObservableProperty] private string _sizeText = "";
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _speedText = "-";
    [ObservableProperty] private string _etaText = "-";
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isCompleted;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private bool _isTorrent;

    public double BytesPerSecond => IsActive ? _bytesPerSecond : 0;
    public long TotalBytes => Item.TotalBytes;
    public DateTimeOffset CreatedAt => Item.CreatedAt;
    public int SortOrder => Item.SortOrder;

    /// <summary>Replace the underlying record (for example after a repository or engine update).</summary>
    public void Apply(DownloadItem item)
    {
        Item = item;
        Refresh();
    }

    /// <summary>Apply the latest progress. Ignored when the id does not match.</summary>
    public void ApplyProgress(DownloadProgress p)
    {
        if (p.Id != Id) return;
        Item.DownloadedBytes = p.DownloadedBytes;
        if (p.TotalBytes > 0) Item.TotalBytes = p.TotalBytes;
        Item.State = p.State;
        _bytesPerSecond = p.BytesPerSecond;
        Refresh();
    }

    private void Refresh()
    {
        var it = Item;
        Name = it.FileName;
        Url = it.Url;
        State = it.State;
        IsActive = it.State is DownloadState.Connecting or DownloadState.Downloading or DownloadState.Merging;
        IsPaused = it.State == DownloadState.Paused;
        IsCompleted = it.State == DownloadState.Completed;
        IsError = it.State == DownloadState.Error;
        IsTorrent = it.Kind is DownloadKind.Torrent or DownloadKind.Magnet;
        KindText = it.Kind switch
        {
            DownloadKind.Hls => "HLS",
            DownloadKind.Dash => "DASH",
            DownloadKind.Torrent => "Torrent",
            DownloadKind.Magnet => "Magnet",
            _ => "HTTP",
        };
        StatusText = it.State switch
        {
            DownloadState.Queued => "Queued",
            DownloadState.Scheduled => "Scheduled",
            DownloadState.Connecting => "Connecting",
            DownloadState.Downloading => "Downloading",
            DownloadState.Paused => "Paused",
            DownloadState.Merging => "Merging",
            DownloadState.Completed => "Completed",
            DownloadState.Error => string.IsNullOrEmpty(it.ErrorMessage) ? "Error" : $"Error: {it.ErrorMessage}",
            _ => it.State.ToString(),
        };

        long total = it.TotalBytes, done = it.DownloadedBytes;
        Progress = total > 0 ? Math.Min(100, done * 100.0 / total) : (IsCompleted ? 100 : 0);
        IsIndeterminate = total <= 0 && IsActive;
        SizeText = total > 0 ? $"{Fmt.Bytes(done)} / {Fmt.Bytes(total)}" : Fmt.Bytes(done);
        ProgressText = total > 0 ? $"{Progress:0.#}%" : (IsCompleted ? "100%" : "-");
        SpeedText = IsActive ? Fmt.Speed(_bytesPerSecond, _bitsSpeed()) : "-";
        TimeSpan? eta = IsActive && _bytesPerSecond > 0 && total > done
            ? TimeSpan.FromSeconds((total - done) / _bytesPerSecond)
            : null;
        EtaText = IsActive ? Fmt.Eta(eta) : "-";
    }
}
