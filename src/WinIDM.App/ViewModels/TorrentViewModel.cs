using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinIDM.App.Services;
using WinIDM.Torrent;

namespace WinIDM.App.ViewModels;

/// <summary>
/// Torrent window: torrent list, details of the selected torrent (files, stats, trackers, limits).
/// The source of truth is <see cref="TorrentService"/> (not the HTTP download repository).
/// </summary>
public sealed partial class TorrentViewModel : ObservableObject, IDisposable
{
    private readonly Composition _c;
    private readonly IShell _shell;
    private readonly DispatcherTimer _timer;
    private readonly ObservableCollection<TorrentRowViewModel> _rows = new();
    private long? _loadedId;   // torrent whose editable fields (trackers, limits) are loaded
    private long? _filesId;    // torrent whose file list is displayed
    private bool _disposed;

    public TorrentViewModel(Composition composition, IShell shell, string? source)
    {
        _c = composition;
        _shell = shell;
        _source = source ?? "";
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => RefreshSelected();
        _timer.Start();
        _ = ReloadAsync();
    }

    public ObservableCollection<TorrentRowViewModel> Items => _rows;
    public ObservableCollection<TorrentFileRowViewModel> Files { get; } = new();
    public IReadOnlyList<TorrentPriorityOption> PriorityOptions => TorrentPriorityOption.All;

    [ObservableProperty] private string _source = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private TorrentRowViewModel? _selected;
    [ObservableProperty] private bool _hasSelection;
    [ObservableProperty] private bool _hasMetadata;
    [ObservableProperty] private string _detailText = "Select a torrent in the list to see its details.";
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private bool _sequential;
    [ObservableProperty] private string _trackersText = "";
    [ObservableProperty] private string _downloadLimitText = "0";
    [ObservableProperty] private string _uploadLimitText = "0";

    public void SetSource(string source) => Source = source;

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
    }

    partial void OnSelectedChanged(TorrentRowViewModel? value)
    {
        HasSelection = value is not null;
        _loadedId = null;
        _filesId = null;
        RefreshSelected();
    }

    /// <summary>Reload the list from the engine (called after adding and on start).</summary>
    public async Task ReloadAsync()
    {
        try
        {
            await _c.Torrent.InitializeAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("Torrent startup", ex);
            Status = "The torrent engine failed to start. See the log for details.";
            return;
        }
        if (_disposed) return;

        var keep = Selected?.Id;
        Items.Clear();
        foreach (var t in _c.Torrent.GetTorrents())
        {
            if (TryStats(t.Id) is { } s) Items.Add(new TorrentRowViewModel(s));
        }
        Selected = Items.FirstOrDefault(i => i.Id == keep) ?? Items.FirstOrDefault();
    }

    /// <summary>Refresh the list and the selected torrent details (called every second).</summary>
    private void RefreshSelected()
    {
        if (_disposed) return;
        try
        {
            var live = _c.Torrent.GetTorrents();
            var liveIds = live.Select(t => t.Id).ToHashSet();
            for (int i = Items.Count - 1; i >= 0; i--)
            {
                if (!liveIds.Contains(Items[i].Id)) Items.RemoveAt(i);
            }
            foreach (var t in live)
            {
                if (TryStats(t.Id) is not { } stats) continue;
                var row = Items.FirstOrDefault(r => r.Id == t.Id);
                if (row is null) Items.Add(new TorrentRowViewModel(stats));
                else row.Apply(stats);
            }

            if (Selected is not null && !Items.Contains(Selected))
            {
                Selected = null;   // safely calls RefreshSelected again (Selected is now null)
                return;
            }
            if (Selected is null)
            {
                ClearDetails();
                return;
            }

            var s2 = TryStats(Selected.Id);
            if (s2 is null) return;

            ErrorText = s2.LastError ?? "";
            DetailText = BuildDetail(s2);
            HasMetadata = s2.HasMetadata;
            Sequential = s2.Sequential;
            if (_loadedId != s2.Id) LoadEditable(s2);
            SyncFiles(s2.Id, s2.HasMetadata);
        }
        catch (Exception ex)
        {
            AppLog.Error("Torrent refresh", ex);
        }
    }

    private TorrentStats? TryStats(long id)
    {
        try { return _c.Torrent.GetStats(id); }
        catch (KeyNotFoundException) { return null; }
    }

    private void ClearDetails()
    {
        HasMetadata = false;
        Sequential = false;
        ErrorText = "";
        DetailText = "Select a torrent in the list to see its details.";
        TrackersText = "";
        Files.Clear();
        _loadedId = null;
        _filesId = null;
    }

    private static string BuildDetail(TorrentStats s) =>
        $"Info-hash: {s.InfoHash}{Environment.NewLine}" +
        $"Seeds: {s.Seeds}   Peers (leechers): {s.Leechers}   Available: {s.Available}   Active connections: {s.ConnectedPeers}{Environment.NewLine}" +
        $"Download: {Fmt.Speed(s.DownloadRate)}   Upload: {Fmt.Speed(s.UploadRate)}{Environment.NewLine}" +
        $"Download limit: {LimitText(s.DownloadLimit)}   Upload limit: {LimitText(s.UploadLimit)}{Environment.NewLine}" +
        $"Metadata: {(s.HasMetadata ? "available" : "not available yet")}   Sequential download: {(s.Sequential ? "yes" : "no")}";

    private static string LimitText(long bytesPerSecond) =>
        bytesPerSecond <= 0 ? "unlimited" : $"{bytesPerSecond / 1024} KB/s";

    private void LoadEditable(TorrentStats s)
    {
        _loadedId = s.Id;
        TrackersText = string.Join(Environment.NewLine, _c.Torrent.GetTrackers(s.Id));
        DownloadLimitText = (s.DownloadLimit / 1024).ToString(CultureInfo.InvariantCulture);
        UploadLimitText = (s.UploadLimit / 1024).ToString(CultureInfo.InvariantCulture);
    }

    private void SyncFiles(long id, bool hasMetadata)
    {
        if (!hasMetadata)
        {
            Files.Clear();
            _filesId = null;
            return;
        }

        var infos = _c.Torrent.GetFiles(id);
        if (_filesId != id || Files.Count != infos.Count)
        {
            Files.Clear();
            foreach (var info in infos) Files.Add(new TorrentFileRowViewModel(info));
            _filesId = id;
            return;
        }
        for (int i = 0; i < infos.Count; i++) Files[i].Apply(infos[i]);
    }

    // ------------------------------------------------------------ actions

    [RelayCommand]
    private async Task AddAsync()
    {
        var src = Source?.Trim() ?? "";
        var folder = _c.DefaultDownloadFolder();
        try
        {
            if (UrlHelper.IsMagnet(src))
            {
                await _c.Torrent.AddMagnetAsync(src, folder);
            }
            else if (src.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) && File.Exists(src))
            {
                await _c.Torrent.AddTorrentFileAsync(src, folder);
            }
            else
            {
                Status = "Enter a valid magnet: link or .torrent file path.";
                return;
            }
            Source = "";
            Status = "Torrent added.";
            await ReloadAsync();
        }
        catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or InvalidOperationException or NotSupportedException)
        {
            Status = ex.Message;
        }
        catch (Exception ex)
        {
            AppLog.Error("Add torrent", ex);
            Status = "Failed to add the torrent. See the log for details.";
        }
    }

    [RelayCommand]
    private Task PauseAsync(TorrentRowViewModel? row) =>
        OnTargetAsync(row, "Pause torrent", id => _c.Torrent.PauseAsync(id));

    [RelayCommand]
    private Task ResumeAsync(TorrentRowViewModel? row) =>
        OnTargetAsync(row, "Resume torrent", id => _c.Torrent.ResumeAsync(id));

    [RelayCommand]
    private Task RemoveAsync(TorrentRowViewModel? row) => RemoveCoreAsync(row, deleteFiles: false);

    [RelayCommand]
    private Task DeleteWithFilesAsync(TorrentRowViewModel? row) => RemoveCoreAsync(row, deleteFiles: true);

    [RelayCommand]
    private Task RecheckAsync() =>
        OnTargetAsync(null, "Recheck pieces", id => _c.Torrent.RecheckAsync(id), "Piece check started.");

    [RelayCommand]
    private Task ReannounceAsync() =>
        OnTargetAsync(null, "Reannounce", id => _c.Torrent.ReannounceAsync(id), "Reannounce sent to the tracker.");

    /// <summary>Toggle sequential download for the selected torrent (the CheckBox invokes this command).</summary>
    [RelayCommand]
    private Task ToggleSequentialAsync()
    {
        var target = !Sequential;
        return OnTargetAsync(null, "Sequential download", async id =>
        {
            await _c.Torrent.SetSequentialDownloadAsync(id, target);
            Sequential = target;
        });
    }

    [RelayCommand]
    private async Task ApplyTrackersAsync()
    {
        var trackers = TorrentInput.ParseTrackers(TrackersText);
        await OnTargetAsync(null, "Save trackers", async id =>
        {
            await _c.Torrent.SetTrackersAsync(id, trackers);
            _loadedId = null;
            Status = $"Trackers saved: {_c.Torrent.GetTrackers(id).Count} valid URLs.";
        });
    }

    [RelayCommand]
    private async Task ApplyLimitsAsync()
    {
        if (!TorrentInput.TryParseKilobytes(DownloadLimitText, out var dlKb) ||
            !TorrentInput.TryParseKilobytes(UploadLimitText, out var ulKb))
        {
            Status = "Limits must be whole numbers of 0 or more (KB/s). 0 means unlimited.";
            return;
        }
        await OnTargetAsync(null, "Save limits", async id =>
        {
            await _c.Torrent.SetSpeedLimitsAsync(id, dlKb * 1024, ulKb * 1024);
            _loadedId = null;
            Status = "Torrent speed limits saved.";
        });
    }

    /// <summary>Change one file's priority. Can be done while the download is running.</summary>
    public async Task SetFilePriorityAsync(TorrentFileRowViewModel file, TorrentFilePriority priority)
    {
        if (Selected is null || file.Priority == priority) return;
        var id = Selected.Id;
        file.Priority = priority;   // optimistic update; the next refresh confirms it
        await OnTargetAsync(null, "Change file priority", id2 => _c.Torrent.SetFilePriorityAsync(id2, file.Index, priority), targetId: id);
    }

    /// <summary>Save a copy of the selected torrent's .torrent file to a path chosen by the user.</summary>
    public Task SaveTorrentCopyAsync(string destinationPath) =>
        OnTargetAsync(null, "Save .torrent", id => _c.Torrent.SaveTorrentFileAsync(id, destinationPath), ".torrent file saved.");

    /// <summary>Copy the selected torrent's magnet link to the clipboard.</summary>
    [RelayCommand]
    private async Task CopyMagnetAsync()
    {
        if (Selected is null) return;
        try
        {
            var magnet = _c.Torrent.GetMagnetLink(Selected.Id);
            if (string.IsNullOrEmpty(magnet))
            {
                Status = "Magnet link not available.";
                return;
            }
            // Marked as the app's own copy, so the clipboard monitor does not offer it back as a new download.
            Status = await ClipboardWriter.TryCopyLinkAsync(magnet)
                ? "Magnet link copied."
                : "The clipboard is in use by another app. Try again.";
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            Status = ex.Message;
        }
    }

    private async Task RemoveCoreAsync(TorrentRowViewModel? row, bool deleteFiles)
    {
        if (row is null) return;
        var question = deleteFiles
            ? $"Remove \"{row.Name}\" and delete its files from disk? This action cannot be undone."
            : $"Remove \"{row.Name}\" from the list? Files are kept.";
        if (!_shell.Confirm("Remove torrent", question)) return;
        await OnTargetAsync(row, "Remove torrent", id => _c.Torrent.RemoveAsync(id, deleteFiles), "Torrent removed.");
    }

    /// <summary>Run an operation on the target torrent (the given row, or the selected torrent).</summary>
    private Task OnTargetAsync(TorrentRowViewModel? row, string operation, Func<long, Task> action, string? okMessage = null, long? targetId = null)
    {
        var id = targetId ?? (row ?? Selected)?.Id;
        if (id is null) return Task.CompletedTask;
        return RunAsync(operation, async () =>
        {
            await action(id.Value);
            if (okMessage is not null) Status = okMessage;
        });
    }

    private async Task RunAsync(string operation, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException or InvalidOperationException
                                    or NotSupportedException or KeyNotFoundException or FileNotFoundException or IOException)
        {
            Status = ex.Message;
        }
        catch (Exception ex)
        {
            AppLog.Error(operation, ex);
            Status = $"{operation} failed. See the log for details.";
        }
        if (!_disposed)
        {
            RefreshSelected();
            // The "sequential" CheckBox may have toggled visually; force a re-read of the real value.
            OnPropertyChanged(nameof(Sequential));
        }
    }
}

/// <summary>UI-independent parser for torrent form input (trackers and speed limits).</summary>
public static class TorrentInput
{
    /// <summary>One URL per line (or separated by whitespace). Empty lines are ignored.</summary>
    public static IReadOnlyList<string> ParseTrackers(string? text) =>
        (text ?? "")
            .Split(new[] { '\r', '\n', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .ToList();

    /// <summary>Whole number of 0 or more in KB/s. 0 means unlimited.</summary>
    public static bool TryParseKilobytes(string? text, out long kilobytes)
    {
        kilobytes = 0;
        if (!long.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var v)) return false;
        if (v < 0 || v > long.MaxValue / 1024) return false;
        kilobytes = v;
        return true;
    }
}
