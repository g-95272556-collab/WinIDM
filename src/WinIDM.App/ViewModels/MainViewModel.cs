using System.Collections;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinIDM.App.Services;
using WinIDM.Core.Models;

namespace WinIDM.App.ViewModels;

/// <summary>Status tabs / filters on the main window (11 tabs, per research 01/03).</summary>
public enum DownloadTab
{
    All, Downloading, Started, Active, Inactive, Unfinished, Finished,
    Errored, MissingFiles, Scheduled, Scheduler,
}

public enum SortMode { Manual, Name, Size, Date }

public sealed record SortOption(SortMode Mode, string Label);

public sealed record FileTypeOption(FileTypeFilter Value, string Label);

public sealed partial class TabItemViewModel : ObservableObject
{
    public TabItemViewModel(DownloadTab tab, string title)
    {
        Tab = tab;
        Title = title;
    }

    public DownloadTab Tab { get; }
    public string Title { get; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayTitle))] private int _count;
    [ObservableProperty] private bool _isSelected;

    /// <summary>"Show download count with tab name" (03a: sdcwtn).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayTitle))] private bool _showCount = true;

    public string DisplayTitle => ShowCount ? $"{Title} ({Count})" : Title;
}

/// <summary>Main view model: download list, tabs, filters, row actions and bulk actions.</summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private static readonly (DownloadTab Tab, string Title)[] TabDefs =
    {
        (DownloadTab.All, "All"),
        (DownloadTab.Downloading, "Downloading"),
        (DownloadTab.Started, "Started"),
        (DownloadTab.Active, "Active"),
        (DownloadTab.Inactive, "Inactive"),
        (DownloadTab.Unfinished, "Unfinished"),
        (DownloadTab.Finished, "Completed"),
        (DownloadTab.Errored, "Error"),
        (DownloadTab.MissingFiles, "Missing files"),
        (DownloadTab.Scheduled, "Scheduled"),
        (DownloadTab.Scheduler, "Scheduler"),
    };

    private readonly Composition _c;
    private readonly IShell _shell;
    private readonly Dispatcher _ui;
    private readonly ConcurrentDictionary<long, DownloadProgress> _pendingProgress = new();
    private readonly DispatcherTimer _flushTimer;
    private readonly EventHandler _networkHandler;
    private readonly Action<string> _settingsHandler;
    private bool _wifiOnlyBlocked;
    private DateTime _quietAddsUntil = DateTime.MinValue;
    private int _lastActive = -1;

    public static IReadOnlyList<SortOption> SortOptions { get; } = new[]
    {
        new SortOption(SortMode.Manual, "Manual order"),
        new SortOption(SortMode.Name, "Name (A-Z)"),
        new SortOption(SortMode.Size, "Size"),
        new SortOption(SortMode.Date, "Date (newest)"),
    };

    public static IReadOnlyList<FileTypeOption> FileTypeOptions { get; } =
        Enum.GetValues<FileTypeFilter>().Select(f => new FileTypeOption(f, FileTypes.Label(f))).ToList();

    public MainViewModel(Composition composition, IShell shell)
    {
        _c = composition;
        _shell = shell;
        _ui = Application.Current.Dispatcher;

        foreach (var (tab, title) in TabDefs) Tabs.Add(new TabItemViewModel(tab, title));
        Tabs[0].IsSelected = true;

        RowsView = (ListCollectionView)CollectionViewSource.GetDefaultView(Rows);
        RowsView.Filter = Filter;
        RowsView.CustomSort = Comparer<object>.Create(Compare);

        // Separate view of the running rows for the overlay window (independent of the selected tab and filters).
        ActiveRowsView = new ListCollectionView(Rows)
        {
            Filter = o => o is DownloadRowViewModel { IsActive: true },
            IsLiveFiltering = true,
        };
        ActiveRowsView.LiveFilteringProperties.Add(nameof(DownloadRowViewModel.IsActive));

        _selectedFileType = FileTypeOptions[0];
        _selectedSort = SortOptions[0];
        _selectedTimeFilter = TimeFilterOptions[0];
        ApplyListSettings();
        Scheduler = new SchedulerViewModel(_c.Settings);

        _c.Engine.ProgressChanged += OnEngineProgress;
        _c.Engine.ItemChanged += OnEngineItem;

        _flushTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) => FlushProgress(), _ui);
        _flushTimer.Start();

        // Raised on a background thread by Windows.Networking.Connectivity.
        _networkHandler = (_, _) => _ui.BeginInvoke(new Action(UpdateNetworkStatus));
        _c.Network.Changed += _networkHandler;

        _settingsHandler = key => _ui.BeginInvoke(new Action(() => OnSettingChanged(key)));
        _c.Settings.Changed += _settingsHandler;
    }

    public ObservableCollection<DownloadRowViewModel> Rows { get; } = new();
    public ListCollectionView RowsView { get; }
    /// <summary>Running downloads only, for the overlay window.</summary>
    public ListCollectionView ActiveRowsView { get; }
    public ObservableCollection<TabItemViewModel> Tabs { get; } = new();
    /// <summary>Rows selected in the list (kept in sync by the code-behind).</summary>
    public ObservableCollection<DownloadRowViewModel> SelectedRows { get; } = new();
    public SchedulerViewModel Scheduler { get; }

    [ObservableProperty] private DownloadTab _selectedTab;
    [ObservableProperty] private bool _isSchedulerTab;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private FileTypeOption _selectedFileType;
    [ObservableProperty] private SortOption _selectedSort;
    [ObservableProperty] private string _statusSummary = "";
    [ObservableProperty] private string _totalSpeedText = "";
    [ObservableProperty] private string _networkText = "";
    [ObservableProperty] private bool _hasNoRows;

    // Headline numbers for the dashboard cards.
    [ObservableProperty] private int _activeCountStat;
    [ObservableProperty] private int _completedCountStat;
    [ObservableProperty] private int _totalCountStat;
    [ObservableProperty] private string _speedValueText = "-";

    /// <summary>Number of active downloads, for the tray icon and sleep control.</summary>
    public event Action<int>? ActiveCountChanged;
    /// <summary>Notification (title, text, is error) for the tray.</summary>
    public event Action<string, string, bool>? NotifyRequested;

    public int ActiveCount => Rows.Count(r => r.IsActive);

    public void Dispose()
    {
        _flushTimer.Stop();
        _c.Engine.ProgressChanged -= OnEngineProgress;
        _c.Engine.ItemChanged -= OnEngineItem;
        _c.Network.Changed -= _networkHandler;
        _c.Settings.Changed -= _settingsHandler;
    }

    /// <summary>Load existing records from the repository. Called once during startup.</summary>
    public async Task InitializeAsync()
    {
        var items = await _c.Repository.ListAsync();
        foreach (var it in items.OrderBy(i => i.SortOrder)) Rows.Add(new DownloadRowViewModel(it, BitsSpeed));
        RefreshAll();
        _lastActive = ActiveCount;
        UpdateNetworkStatus();
    }

    private bool BitsSpeed() => _c.Settings.Bool(Keys.ShowBits);

    // ---------- Filtering and sorting ----------

    partial void OnSelectedTabChanged(DownloadTab value)
    {
        IsSchedulerTab = value == DownloadTab.Scheduler;
        RowsView.Refresh();
        RefreshCounts();
    }

    partial void OnSearchTextChanged(string value) => RowsView.Refresh();
    partial void OnSelectedFileTypeChanged(FileTypeOption value) => RowsView.Refresh();
    partial void OnSelectedSortChanged(SortOption value) => RowsView.Refresh();
    partial void OnSelectedTimeFilterChanged(TimeFilterOption value) => RowsView.Refresh();

    private bool Filter(object o)
    {
        if (o is not DownloadRowViewModel r) return false;
        if (SelectedTab == DownloadTab.Scheduler) return false;
        if (!TabMatches(SelectedTab, r)) return false;
        if (SelectedFileType.Value != FileTypeFilter.All && FileTypes.Classify(r.Name) != SelectedFileType.Value) return false;
        if (!TimeMatches(r)) return false;
        var q = SearchText?.Trim();
        if (!string.IsNullOrEmpty(q) &&
            r.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 &&
            r.Url.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)
            return false;
        return true;
    }

    private static bool TabMatches(DownloadTab tab, DownloadRowViewModel r) => tab switch
    {
        DownloadTab.All => true,
        DownloadTab.Downloading => r.Item.State == DownloadState.Downloading,
        DownloadTab.Started => r.Item.DownloadedBytes > 0 && r.Item.State != DownloadState.Completed,
        DownloadTab.Active => r.IsActive,
        DownloadTab.Inactive => r.Item.State is DownloadState.Paused or DownloadState.Queued or DownloadState.Scheduled,
        DownloadTab.Unfinished => r.Item.State != DownloadState.Completed,
        DownloadTab.Finished => r.Item.State == DownloadState.Completed,
        DownloadTab.Errored => r.Item.State == DownloadState.Error,
        DownloadTab.MissingFiles => r.Item.State == DownloadState.Completed && !File.Exists(r.Item.FullPath),
        DownloadTab.Scheduled => r.Item.ScheduledAt is not null || r.Item.State == DownloadState.Scheduled,
        _ => false,
    };

    private int Compare(object? a, object? b)
    {
        if (a is not DownloadRowViewModel x || b is not DownloadRowViewModel y) return 0;
        return SelectedSort.Mode switch
        {
            SortMode.Name => string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase),
            SortMode.Size => x.TotalBytes.CompareTo(y.TotalBytes),
            SortMode.Date => y.CreatedAt.CompareTo(x.CreatedAt),
            _ => x.SortOrder.CompareTo(y.SortOrder),
        };
    }

    private void RefreshCounts()
    {
        foreach (var t in Tabs)
            t.Count = t.Tab == DownloadTab.Scheduler ? 0 : Rows.Count(r => TabMatches(t.Tab, r));
    }

    private void RefreshAll()
    {
        RowsView.Refresh();
        RefreshCounts();
        UpdateSummary();
        HasNoRows = Rows.Count == 0;
    }

    private void UpdateSummary()
    {
        var active = Rows.Where(r => r.IsActive).ToList();
        int completed = Rows.Count(r => r.IsCompleted);
        double speed = active.Sum(r => r.BytesPerSecond);
        TotalSpeedText = active.Count == 0 ? "No active downloads" : $"Total speed: {Fmt.Speed(speed, BitsSpeed())}";
        SpeedValueText = active.Count == 0 ? "-" : Fmt.Speed(speed, BitsSpeed());
        StatusSummary = $"{Rows.Count} items  |  {active.Count} active  |  {completed} completed";
        ActiveCountStat = active.Count;
        CompletedCountStat = completed;
        TotalCountStat = Rows.Count;
        HasNoRows = Rows.Count == 0;

        long done = active.Where(r => r.TotalBytes > 0).Sum(r => r.Item.DownloadedBytes);
        long total = active.Where(r => r.TotalBytes > 0).Sum(r => r.TotalBytes);
        var percent = total > 0 ? $" - {done * 100.0 / total:0}%" : "";
        var summary = active.Count == 0 ? null : $"{active.Count} active{percent} - {Fmt.Speed(speed, BitsSpeed())}";
        if (summary != _traySummary)
        {
            _traySummary = summary;
            TraySummaryChanged?.Invoke(summary);
        }
    }

    private string? _traySummary;

    /// <summary>One-line progress for the tray tooltip and the combined overlay line; null when nothing is running.</summary>
    public event Action<string?>? TraySummaryChanged;

    public string? TraySummary => _traySummary;

    // ---------- Engine events ----------

    // May be called from any thread: only the latest state is kept; the UI is refreshed by the timer.
    private void OnEngineProgress(DownloadProgress p) => _pendingProgress[p.Id] = p;

    private void OnEngineItem(DownloadItem item) => _ui.BeginInvoke(new Action(() => Upsert(item)));

    private void FlushProgress()
    {
        if (_pendingProgress.IsEmpty) return;
        bool stateChanged = false;
        foreach (var key in _pendingProgress.Keys.ToList())
        {
            if (!_pendingProgress.TryRemove(key, out var p)) continue;
            var row = Rows.FirstOrDefault(r => r.Id == key);
            if (row is null) continue;
            var prev = row.State;
            row.ApplyProgress(p);
            RefreshSegments(row);
            if (row.State != prev)
            {
                stateChanged = true;
                OnStateTransition(row, prev);
            }
        }
        UpdateSummary();
        if (stateChanged) RefreshAll();
        CheckActiveTransition();
    }

    private void Upsert(DownloadItem item)
    {
        var row = Rows.FirstOrDefault(r => r.Id == item.Id);
        if (row is null)
        {
            Rows.Add(new DownloadRowViewModel(item, BitsSpeed));
            // IDM+ "download added" toast; a batch or import reports once instead of once per link.
            if (!_c.Settings.Bool(Keys.NoAddedToast) && DateTime.UtcNow >= _quietAddsUntil)
                NotifyRequested?.Invoke("Download added", item.FileName, false);
        }
        else
        {
            var prev = row.State;
            row.Apply(item);
            if (prev != row.State) OnStateTransition(row, prev);
        }
        RefreshAll();
        CheckActiveTransition();
    }

    private void OnStateTransition(DownloadRowViewModel row, DownloadState prev)
    {
        if (row.State == DownloadState.Downloading && prev is not (DownloadState.Downloading or DownloadState.Connecting))
        {
            SoundService.Play(_c.Settings, Keys.SoundStart);
        }
        else if (row.State == DownloadState.Completed && prev != DownloadState.Completed)
        {
            SoundService.Play(_c.Settings, Keys.SoundComplete);
            if (_c.Settings.Bool(Keys.RemoveFinished))
            {
                Rows.Remove(row);
                _ = SafeAsync("Remove finished link", () => _c.Engine.RemoveAsync(row.Id, deleteFile: false));
            }
        }
        else if (row.State == DownloadState.Error && prev != DownloadState.Error)
        {
            SoundService.Play(_c.Settings, Keys.SoundError);
        }
    }

    private void CheckActiveTransition()
    {
        int active = ActiveCount;
        if (active == _lastActive) return;
        _lastActive = active;

        ActiveCountChanged?.Invoke(active);
        PowerState.Update(active > 0, _c.Settings.Bool(Keys.WakeLock), _c.Settings.Bool(Keys.KeepScreenOn));
    }

    private void OnSettingChanged(string key)
    {
        switch (key)
        {
            case Keys.ShowBits:
            case Keys.EtaActiveTime:
                foreach (var r in Rows) r.Apply(r.Item);
                RefreshAll();
                break;
            case Keys.WifiOnly:
                UpdateNetworkStatus();
                break;
            case Keys.ShowTabCounts:
            case Keys.ShowFileTypeFilter:
            case Keys.ShowTimeFilter:
            case Keys.ShowMultiPart:
                ApplyListSettings();
                break;
            case Keys.WakeLock:
            case Keys.KeepScreenOn:
                PowerState.Update(ActiveCount > 0, _c.Settings.Bool(Keys.WakeLock), _c.Settings.Bool(Keys.KeepScreenOn));
                break;
        }
    }

    // ---------- Network status (the engine enforces Wi-Fi only / metered; this only reports it) ----------

    private void UpdateNetworkStatus()
    {
        var st = NetworkStatus.From(_c.Network);
        bool blocked = st.BlocksWifiOnly(_c.Settings.Bool(Keys.WifiOnly));
        NetworkText = blocked ? $"{st.Label} - waiting for Wi-Fi" : st.Label;
        // Tell the user once when Wi-Fi only starts holding downloads back (the engine re-queues and resumes them).
        if (blocked && !_wifiOnlyBlocked && ActiveCount > 0)
            NotifyRequested?.Invoke("Downloads waiting", "Wi-Fi only is on and the connection is mobile data. Downloads resume on Wi-Fi or a wired network.", false);
        _wifiOnlyBlocked = blocked;
    }

    // ---------- Main commands ----------

    [RelayCommand] private async Task AddUrlAsync() => await _shell.AddDownloadAsync(null);

    [RelayCommand] private async Task StartAllAsync() => await SafeAsync("Start all", () => _c.Engine.StartAllAsync());

    [RelayCommand] private async Task PauseAllAsync() => await SafeAsync("Pause all", () => _c.Engine.PauseAllAsync());

    [RelayCommand] private void OpenSettings() => _shell.OpenSettings();

    [RelayCommand] private void OpenBrowser() => _shell.OpenBrowser(false, null);

    [RelayCommand] private void OpenTorrent() => _shell.OpenTorrent(null);

    [RelayCommand] private void OpenBackup() => _shell.OpenBackup();

    [RelayCommand] private void OpenPasswords() => _shell.OpenPasswords();

    [RelayCommand] private void OpenAbout() => _shell.OpenAbout();

    [RelayCommand] private void OpenThemeEditor() => _shell.OpenThemeEditor();

    [RelayCommand]
    private void SelectTab(TabItemViewModel? tab)
    {
        if (tab is null) return;
        foreach (var t in Tabs) t.IsSelected = t == tab;
        SelectedTab = tab.Tab;
    }

    // ---------- Row actions ----------

    [RelayCommand] private async Task ResumeAsync(DownloadRowViewModel? row)
    {
        if (row is null) return;
        await SafeAsync("Resume", () => _c.Engine.ResumeAsync(row.Id));
    }

    [RelayCommand] private async Task PauseAsync(DownloadRowViewModel? row)
    {
        if (row is null) return;
        // IDM+ warns that a server without range support cannot resume (resume_not_supported_server).
        if (row.IsActive && row.Item.Kind == DownloadKind.Http && !row.Item.AcceptsRanges && row.Item.DownloadedBytes > 0 &&
            !_shell.Confirm("Pause", "This server does not support resume. If you pause, the download starts again from zero. Pause anyway?"))
            return;
        await SafeAsync("Pause", () => _c.Engine.PauseAsync(row.Id));
    }

    [RelayCommand]
    private async Task RemoveFromListAsync(DownloadRowViewModel? row)
    {
        if (row is null) return;
        if (!_shell.Confirm("Remove from list", $"Remove \"{row.Name}\" from the list? The file on disk is kept.")) return;
        await SafeAsync("Remove", () => _c.Engine.RemoveAsync(row.Id, deleteFile: false));
        Rows.Remove(row);
        RefreshAll();
    }

    [RelayCommand]
    private async Task RemoveWithFileAsync(DownloadRowViewModel? row)
    {
        if (row is null) return;
        if (!_shell.Confirm("Remove and delete file", $"Remove \"{row.Name}\" and delete the file from disk? This cannot be undone.")) return;
        await SafeAsync("Delete file", () => _c.Engine.RemoveAsync(row.Id, deleteFile: true));
        Rows.Remove(row);
        RefreshAll();
    }

    [RelayCommand] private async Task MoveUpAsync(DownloadRowViewModel? row) => await SwapOrderAsync(row, -1);

    [RelayCommand] private async Task MoveDownAsync(DownloadRowViewModel? row) => await SwapOrderAsync(row, +1);

    private async Task SwapOrderAsync(DownloadRowViewModel? row, int delta)
    {
        if (row is null) return;
        var ordered = Rows.OrderBy(r => r.SortOrder).ToList();
        int idx = ordered.IndexOf(row);
        int target = idx + delta;
        if (idx < 0 || target < 0 || target >= ordered.Count) return;
        var other = ordered[target];
        (row.Item.SortOrder, other.Item.SortOrder) = (other.Item.SortOrder, row.Item.SortOrder);
        await SafeAsync("Reorder", async () =>
        {
            await _c.Repository.UpdateAsync(row.Item);
            await _c.Repository.UpdateAsync(other.Item);
        });
        row.Apply(row.Item);
        other.Apply(other.Item);
        RowsView.Refresh();
    }

    [RelayCommand]
    private void OpenFile(DownloadRowViewModel? row)
    {
        if (row is null) return;
        var path = row.Item.FullPath;
        if (!File.Exists(path))
        {
            _shell.ShowMessage("Open file", "The file could not be found on disk.", error: true);
            return;
        }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    [RelayCommand]
    private void OpenFolder(DownloadRowViewModel? row)
    {
        if (row is null) return;
        var path = row.Item.FullPath;
        if (File.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        else if (Directory.Exists(row.Item.SaveDirectory))
            Process.Start(new ProcessStartInfo(row.Item.SaveDirectory) { UseShellExecute = true });
        else
            _shell.ShowMessage("Open folder", "The folder could not be found.", error: true);
    }

    [RelayCommand]
    private async Task CopyLinkAsync(DownloadRowViewModel? row)
    {
        if (row is null) return;
        if (!await ClipboardWriter.TryCopyLinkAsync(row.Url))
            _shell.ShowMessage("Copy link", "The clipboard is in use by another app. Try again.", error: true);
    }

    [RelayCommand]
    private async Task RefreshLinkAsync(DownloadRowViewModel? row)
    {
        if (row is null) return;
        var url = _shell.Prompt("Refresh link", "New URL:", row.Url);
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!UrlHelper.IsDownloadable(url.Trim()))
        {
            _shell.ShowMessage("Refresh link", "Invalid URL. Use http, https or magnet.", error: true);
            return;
        }
        var item = row.Item;
        item.Url = url.Trim();
        item.FinalUrl = null;
        await SafeAsync("Refresh link", async () =>
        {
            await _c.Repository.UpdateAsync(item);
            if (item.State == DownloadState.Paused) await _c.Engine.ResumeAsync(item.Id);
        });
        row.Apply(item);
    }

    [RelayCommand]
    private async Task RedownloadAsync(DownloadRowViewModel? row)
    {
        if (row is null) return;
        if (!_shell.Confirm("Re-download", $"Delete \"{row.Name}\" and download it again from scratch?")) return;
        var req = new DownloadRequest
        {
            Url = row.Item.Url,
            FileName = row.Item.FileName,
            SaveDirectory = row.Item.SaveDirectory,
            Referer = row.Item.Referer,
            UserAgent = row.Item.UserAgent,
            CookieHeader = row.Item.CookieHeader,
            Headers = new Dictionary<string, string>(row.Item.Headers),
            Username = row.Item.Username,
            Password = row.Item.Password,
            MaxSegments = row.Item.MaxSegments,
        };
        await SafeAsync("Re-download", async () =>
        {
            await _c.Engine.RemoveAsync(row.Id, deleteFile: true);
            await _c.Engine.AddAsync(req);
        });
        Rows.Remove(row);
        RefreshAll();
    }

    [RelayCommand]
    private void ShowProperties(DownloadRowViewModel? row)
    {
        if (row is null) return;
        var it = row.Item;
        var text = string.Join(Environment.NewLine, new[]
        {
            $"File name: {it.FileName}",
            $"URL: {it.Url}",
            $"Type: {row.KindText}",
            $"Folder: {it.SaveDirectory}",
            $"Size: {(it.TotalBytes > 0 ? Fmt.Bytes(it.TotalBytes) : "unknown")}",
            $"Downloaded: {Fmt.Bytes(it.DownloadedBytes)}",
            $"State: {row.StatusText}",
            $"Created: {it.CreatedAt.LocalDateTime:yyyy-MM-dd HH:mm}",
            $"Completed: {(it.CompletedAt is null ? "-" : it.CompletedAt.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm"))}",
            $"Referer: {it.Referer ?? "-"}",
            $"Expected SHA-256: {it.ExpectedSha256 ?? "-"}",
            $"Expected MD5: {it.ExpectedMd5 ?? "-"}",
        });
        _shell.ShowText("Download properties", text);
    }

    [RelayCommand]
    private async Task ChecksumAsync(DownloadRowViewModel? row)
    {
        if (row is null) return;
        var path = row.Item.FullPath;
        if (!File.Exists(path))
        {
            _shell.ShowMessage("Checksum", "The file could not be found on disk.", error: true);
            return;
        }
        (string Md5, string Sha256)? result = null;
        await SafeAsync("Checksum", async () =>
        {
            result = await ChecksumService.ComputeAsync(path);
        });
        if (result is not { } hashes) return;

        var lines = new List<string>
        {
            $"File: {row.Name}",
            $"MD5: {hashes.Md5}",
            $"SHA-256: {hashes.Sha256}",
        };
        if (!string.IsNullOrEmpty(row.Item.ExpectedMd5))
            lines.Add($"MD5 match: {(string.Equals(row.Item.ExpectedMd5, hashes.Md5, StringComparison.OrdinalIgnoreCase) ? "MATCH" : "MISMATCH")}");
        if (!string.IsNullOrEmpty(row.Item.ExpectedSha256))
            lines.Add($"SHA-256 match: {(string.Equals(row.Item.ExpectedSha256, hashes.Sha256, StringComparison.OrdinalIgnoreCase) ? "MATCH" : "MISMATCH")}");
        _shell.ShowText("Checksum", string.Join(Environment.NewLine, lines));
    }

    // ---------- Bulk actions ----------

    [RelayCommand]
    private async Task RemoveSelectedAsync()
    {
        var rows = SelectedRows.ToList();
        if (rows.Count == 0) return;
        if (!_shell.Confirm("Remove from list", $"Remove {rows.Count} selected items from the list? Files on disk are kept.")) return;
        foreach (var r in rows)
        {
            await SafeAsync("Remove", () => _c.Engine.RemoveAsync(r.Id, deleteFile: false));
            Rows.Remove(r);
        }
        RefreshAll();
    }

    [RelayCommand]
    private async Task RemoveFinishedAsync()
    {
        var rows = Rows.Where(r => r.IsCompleted).ToList();
        if (rows.Count == 0) return;
        if (!_shell.Confirm("Remove completed", $"Remove {rows.Count} completed items from the list? Files are kept.")) return;
        foreach (var r in rows)
        {
            await SafeAsync("Remove", () => _c.Engine.RemoveAsync(r.Id, deleteFile: false));
            Rows.Remove(r);
        }
        RefreshAll();
    }

    private async Task SafeAsync(string action, Func<Task> op)
    {
        try
        {
            await op();
        }
        catch (NotSupportedException ex)
        {
            _shell.ShowMessage(action, ex.Message);
        }
        catch (Exception ex)
        {
            AppLog.Error(action, ex);
            _shell.ShowMessage(action, "The operation failed. See the log for details.", error: true);
        }
    }
}

/// <summary>Scheduler settings (start/stop time, repeat, speed limit). The engine reads these keys.</summary>
public sealed partial class SchedulerViewModel : ObservableObject
{
    private readonly AppSettings _settings;

    public SchedulerViewModel(AppSettings settings)
    {
        _settings = settings;
        _enabled = settings.Bool(Keys.SchedEnabled);
        _start = settings.Text(Keys.SchedStart);
        _stop = settings.Text(Keys.SchedStop);
        _days = settings.Text(Keys.SchedDays);
        _speedKbps = settings.Int(Keys.SchedSpeed).ToString();
    }

    public IReadOnlyList<string> DayOptions { get; } = new[] { "Every day", "Weekdays", "Weekends" };

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _start;
    [ObservableProperty] private string _stop;
    [ObservableProperty] private string _days;
    [ObservableProperty] private string _speedKbps;
    [ObservableProperty] private string _message = "";

    [RelayCommand]
    private void Save()
    {
        if (!TimeSpan.TryParseExact(Start, @"hh\:mm", null, out _) || !TimeSpan.TryParseExact(Stop, @"hh\:mm", null, out _))
        {
            Message = "Times must use the HH:mm format (for example 02:30).";
            return;
        }
        if (!int.TryParse(SpeedKbps, out int speed) || speed < 0)
        {
            Message = "The speed limit must be a whole number of 0 or more.";
            return;
        }
        _settings.Set(Keys.SchedEnabled, Enabled);
        _settings.Set(Keys.SchedStart, Start);
        _settings.Set(Keys.SchedStop, Stop);
        _settings.Set(Keys.SchedDays, Days);
        _settings.Set(Keys.SchedSpeed, speed);
        Message = "Scheduler settings saved.";
    }
}
