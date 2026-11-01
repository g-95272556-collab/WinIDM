using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinIDM.App.Services;
using WinIDM.App.Views;
using WinIDM.Core.Models;
using WinIDM.Engine;

namespace WinIDM.App.ViewModels;

/// <summary>Time filter of the download list (03a: idm_pref_show_time_filter), by the date a download was added.</summary>
public enum TimeFilter { All, Today, Last7Days, Last30Days, Older }

public sealed record TimeFilterOption(TimeFilter Value, string Label);

/// <summary>Download list tools from IDM+: time filter, per-part progress, batch download, link import/export, leftovers.</summary>
public sealed partial class MainViewModel
{
    public static IReadOnlyList<TimeFilterOption> TimeFilterOptions { get; } = new[]
    {
        new TimeFilterOption(TimeFilter.All, "Any time"),
        new TimeFilterOption(TimeFilter.Today, "Today"),
        new TimeFilterOption(TimeFilter.Last7Days, "Last 7 days"),
        new TimeFilterOption(TimeFilter.Last30Days, "Last 30 days"),
        new TimeFilterOption(TimeFilter.Older, "Older than 30 days"),
    };

    [ObservableProperty] private TimeFilterOption _selectedTimeFilter;
    [ObservableProperty] private bool _showFileTypeFilter = true;
    [ObservableProperty] private bool _showTimeFilter;

    private bool TimeMatches(DownloadRowViewModel r)
    {
        var filter = SelectedTimeFilter?.Value ?? TimeFilter.All;
        if (filter == TimeFilter.All) return true;
        var age = DateTime.Today - r.CreatedAt.LocalDateTime.Date;
        return filter switch
        {
            TimeFilter.Today => age.TotalDays < 1,
            TimeFilter.Last7Days => age.TotalDays < 7,
            TimeFilter.Last30Days => age.TotalDays < 30,
            TimeFilter.Older => age.TotalDays >= 30,
            _ => true,
        };
    }

    /// <summary>Applies the download list settings: tab counts, which filters are shown, per-part bars.</summary>
    private void ApplyListSettings()
    {
        bool counts = _c.Settings.Bool(Keys.ShowTabCounts);
        foreach (var t in Tabs) t.ShowCount = counts;

        ShowFileTypeFilter = _c.Settings.Bool(Keys.ShowFileTypeFilter);
        ShowTimeFilter = _c.Settings.Bool(Keys.ShowTimeFilter);
        // A hidden filter must not keep hiding rows.
        if (!ShowFileTypeFilter) SelectedFileType = FileTypeOptions[0];
        if (!ShowTimeFilter) SelectedTimeFilter = TimeFilterOptions[0];

        foreach (var r in Rows) RefreshSegments(r);
        RowsView.Refresh();
    }

    private void RefreshSegments(DownloadRowViewModel row)
    {
        if (!row.IsActive || !_c.Settings.Bool(Keys.ShowMultiPart))
        {
            if (row.Segments.Count > 0) row.Segments = [];
            return;
        }

        row.Segments = SegmentFill.From(_c.EngineImpl.GetLiveSegments(row.Id), row.TotalBytes);
    }

    private DownloadRequest NewRequest(string url, string? fileName = null) => new()
    {
        Url = url,
        FileName = string.IsNullOrWhiteSpace(fileName) ? UrlHelper.FileNameFromUrl(url) : fileName,
        SaveDirectory = _c.DefaultDownloadFolder(),
        StartImmediately = _c.Settings.Bool(Keys.AutoStart),
    };

    /// <summary>IDM+ "Batch download": one URL pattern with [1-10], [01-10], [a-z] or [0-100:5] groups.</summary>
    [RelayCommand]
    private async Task BatchDownloadAsync()
    {
        var pattern = _shell.Prompt("Batch download",
            "URL pattern, for example https://example.com/photo[001-120].jpg ([a-z] and [0-100:5] also work):", "https://");
        if (string.IsNullOrWhiteSpace(pattern)) return;

        IReadOnlyList<string> urls;
        try
        {
            urls = BatchPattern.Expand(pattern);
        }
        catch (FormatException ex)
        {
            _shell.ShowMessage("Batch download", ex.Message, error: true);
            return;
        }

        var valid = urls.Where(UrlHelper.IsHttp).ToList();
        if (valid.Count == 0)
        {
            _shell.ShowMessage("Batch download", "The pattern does not produce any http:// or https:// link.", error: true);
            return;
        }

        if (!_shell.Confirm("Batch download", $"Add {valid.Count} downloads?\n\nFirst: {valid[0]}\nLast: {valid[^1]}")) return;
        await AddAllAsync("Batch download", valid.Select(u => NewRequest(u)).ToList(), rejected: urls.Count - valid.Count);
    }

    /// <summary>Import a link list (text, CSV, JSON or browser bookmarks HTML).</summary>
    [RelayCommand]
    private async Task ImportLinksAsync()
    {
        var path = _shell.PickOpenFile("Import links",
            "Link lists (*.txt;*.csv;*.json;*.html;*.htm)|*.txt;*.csv;*.json;*.html;*.htm|All files (*.*)|*.*");
        if (path is null) return;

        ImportResult result;
        try
        {
            result = ImportExport.Parse(await File.ReadAllTextAsync(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or NotSupportedException)
        {
            _shell.ShowMessage("Import links", "The file could not be read: " + ex.Message, error: true);
            return;
        }

        if (result.Links.Count == 0)
        {
            _shell.ShowMessage("Import links", "No http:// or https:// links were found in the file.", error: true);
            return;
        }

        var note = result.Truncated ? $" Only the first {ImportExport.MaxLinks} links were read." : "";
        if (!_shell.Confirm("Import links", $"Add {result.Links.Count} downloads?{note}")) return;
        await AddAllAsync("Import links", result.Links.Select(l => NewRequest(l.Url, l.FileName)).ToList(), result.Rejected.Count);
    }

    private async Task AddAllAsync(string title, IReadOnlyList<DownloadRequest> requests, int rejected)
    {
        int added = 0, failed = 0;
        foreach (var request in requests)
        {
            try
            {
                await _c.Engine.AddAsync(request);
                added++;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
            {
                AppLog.Error(title, ex);
                failed++;
            }
        }

        var skipped = rejected + failed;
        NotifyRequested?.Invoke(title, skipped > 0 ? $"{added} added, {skipped} skipped." : $"{added} added.", skipped > 0 && added == 0);
    }

    /// <summary>Export the selected links (or every listed link) to a text file, one URL per line.</summary>
    [RelayCommand]
    private void ExportLinks()
    {
        var rows = SelectedRows.Count > 0 ? SelectedRows.ToList() : RowsView.Cast<DownloadRowViewModel>().ToList();
        if (rows.Count == 0)
        {
            _shell.ShowMessage("Export links", "There are no links to export.");
            return;
        }

        var path = _shell.PickSaveFile("Export links", "Text file (*.txt)|*.txt", "links.txt");
        if (path is null) return;
        try
        {
            File.WriteAllLines(path, rows.Select(r => r.Url));
            NotifyRequested?.Invoke("Export links", $"{rows.Count} links saved.", false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _shell.ShowMessage("Export links", "The file could not be written: " + ex.Message, error: true);
        }
    }

    /// <summary>
    /// IDM+ "Remove leftover part files": the separate video/audio track files of HLS/DASH downloads
    /// (<c>name.video.ts</c>, <c>name.audio.m4a</c>, ...) that no unfinished download in the list still needs.
    /// </summary>
    [RelayCommand]
    private void RemoveLeftoverFiles()
    {
        var needed = Rows.Where(r => !r.IsCompleted)
            .Select(r => Path.Combine(r.Item.SaveDirectory, Path.GetFileNameWithoutExtension(r.Item.FileName)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var folders = Rows.Select(r => r.Item.SaveDirectory).Append(_c.DefaultDownloadFolder())
            .Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);

        var leftovers = new List<FileInfo>();
        foreach (var folder in folders)
        {
            try
            {
                foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*.*", SearchOption.TopDirectoryOnly))
                {
                    var name = file.Name;
                    var marker = name.Contains(".video.", StringComparison.OrdinalIgnoreCase) ? ".video."
                        : name.Contains(".audio.", StringComparison.OrdinalIgnoreCase) ? ".audio." : null;
                    if (marker is null) continue;
                    var stem = name[..name.IndexOf(marker, StringComparison.OrdinalIgnoreCase)];
                    if (!needed.Contains(Path.Combine(folder, stem))) leftovers.Add(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Error("Leftover scan", ex);
            }
        }

        if (leftovers.Count == 0)
        {
            _shell.ShowMessage("Remove leftover files", "No leftover part files were found.");
            return;
        }

        var size = leftovers.Sum(f => f.Length);
        if (!_shell.Confirm("Remove leftover files",
                $"Delete {leftovers.Count} leftover part files ({Fmt.Bytes(size)})?\n\n" +
                string.Join("\n", leftovers.Take(8).Select(f => f.FullName)) + (leftovers.Count > 8 ? "\n..." : "")))
            return;

        int removed = 0;
        foreach (var file in leftovers)
        {
            try
            {
                file.Delete();
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Error("Delete leftover", ex);
            }
        }

        NotifyRequested?.Invoke("Remove leftover files", $"{removed} files deleted.", removed < leftovers.Count);
    }
}
