using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Win32;
using WinIDM.App.Services;
using WinIDM.Browser;
using WinIDM.Core.Models;

namespace WinIDM.App.Views;

/// <summary>
/// Grabber: crawl site links iteratively with limits on depth, host scope, regex, file type, and size.
/// One session at a time. The user selects the results before they are sent to downloads or exported.
/// </summary>
public partial class BrowserGrabberWindow : Window
{
    private static readonly IReadOnlyDictionary<string, string[]> Presets = new Dictionary<string, string[]>
    {
        ["docs"] = new[] { "pdf", "doc", "docx", "txt", "rtf", "xls", "xlsx", "ppt", "pptx", "epub" },
        ["video"] = new[] { "mp4", "mkv", "webm", "avi", "mov", "m4v", "ts", "flv" },
        ["audio"] = new[] { "mp3", "m4a", "flac", "ogg", "wav", "aac", "opus" },
        ["images"] = new[] { "jpg", "jpeg", "png", "gif", "webp", "bmp", "svg" },
        ["archives"] = new[] { "zip", "rar", "7z", "tar", "gz" },
        ["apps"] = new[] { "exe", "msi", "apk", "dmg", "deb" },
    };

    private sealed class GrabRow
    {
        public required string Url { get; init; }
        public required string Extension { get; init; }
        public required string Size { get; init; }
        public required int Depth { get; init; }
        public bool Selected { get; set; }
    }

    private readonly Composition _c;
    private readonly IShell _shell;
    private readonly SiteCrawler _crawler = new();
    private CancellationTokenSource? _cts;
    private readonly List<GrabRow> _rows = new();

    public BrowserGrabberWindow(Composition composition, IShell shell, string? startUrl)
    {
        InitializeComponent();
        _c = composition;
        _shell = shell;
        StartUrlBox.Text = startUrl ?? "";
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _cts?.Cancel();
        _crawler.Dispose();
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBuildOptions(out var options, out var error))
        {
            StatusText.Text = error;
            return;
        }

        RunButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        _cts = new CancellationTokenSource();
        var progress = new Progress<CrawlProgress>(p =>
            StatusText.Text = $"Page {p.PagesVisited} (queued {p.PagesQueued}), found {p.CandidatesFound}: {p.CurrentUrl}");
        try
        {
            var result = await _crawler.RunAsync(options, progress, _cts.Token);
            _rows.Clear();
            foreach (var c in result.Candidates)
            {
                _rows.Add(new GrabRow
                {
                    Url = c.Url,
                    Extension = c.Extension,
                    Size = c.SizeBytes is { } s ? FormatSize(s) : "?",
                    Depth = c.Depth,
                });
            }
            List.ItemsSource = _rows;
            UpdateCount();
            StatusText.Text = (result.Cancelled ? "Canceled. " : "Finished. ") +
                $"{result.Pages.Count} pages crawled, {result.Candidates.Count} files found, " +
                $"{result.SkippedByRobots} skipped by robots.txt, {result.SkippedByScope} out of scope, {result.Errors} errors.";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            AppLog.Error("Grabber", ex);
            StatusText.Text = "Crawl failed: " + ex.Message;
        }
        finally
        {
            RunButton.IsEnabled = true;
            StopButton.IsEnabled = false;
        }
    }

    private bool TryBuildOptions(out CrawlOptions options, out string error)
    {
        options = null!;
        error = "";
        if (!UrlHelper.IsHttp(StartUrlBox.Text.Trim()))
        {
            error = "Start URL must be http:// or https://.";
            return false;
        }
        if (!int.TryParse(DepthBox.Text, out int depth) || depth < 0 || depth > SiteCrawler.MaxDepthLimit)
        {
            error = $"Depth must be between 0 and {SiteCrawler.MaxDepthLimit}.";
            return false;
        }
        if (!int.TryParse(PagesBox.Text, out int pages) || pages < 1 || pages > SiteCrawler.MaxPagesLimit)
        {
            error = $"Page count must be between 1 and {SiteCrawler.MaxPagesLimit}.";
            return false;
        }
        if (!double.TryParse(DelayBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double delay))
            delay = 1;
        delay = Math.Max(1, delay);

        string? include = NullIfBlank(IncludeBox.Text);
        string? exclude = NullIfBlank(ExcludeBox.Text);
        if (!IsValidRegex(include) || !IsValidRegex(exclude))
        {
            error = "Invalid regex.";
            return false;
        }

        long? minBytes = ParseKb(MinSizeBox.Text);
        long? maxBytes = ParseKb(MaxSizeBox.Text);
        if (minBytes is { } mn && maxBytes is { } mx && mn > mx)
        {
            error = "Minimum size is greater than maximum size.";
            return false;
        }

        var scope = ScopeBox.SelectedIndex switch
        {
            1 => CrawlHostScope.IncludeSubdomains,
            2 => CrawlHostScope.AnyHost,
            _ => CrawlHostScope.ExactHost,
        };

        options = new CrawlOptions
        {
            StartUrl = StartUrlBox.Text.Trim(),
            MaxDepth = depth,
            MaxPages = pages,
            HostScope = scope,
            IncludeRegex = include,
            ExcludeRegex = exclude,
            Extensions = SelectedExtensions(),
            MinSizeBytes = minBytes,
            MaxSizeBytes = maxBytes,
            RespectRobotsTxt = RobotsBox.IsChecked == true,
            PoliteDelay = TimeSpan.FromSeconds(delay),
        };
        return true;
    }

    private IReadOnlySet<string>? SelectedExtensions()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(bool? on, string key)
        {
            if (on == true) foreach (var ext in Presets[key]) set.Add(ext);
        }
        Add(TypeDocs.IsChecked, "docs");
        Add(TypeVideo.IsChecked, "video");
        Add(TypeAudio.IsChecked, "audio");
        Add(TypeImages.IsChecked, "images");
        Add(TypeArchives.IsChecked, "archives");
        Add(TypeApps.IsChecked, "apps");
        return set.Count == 0 ? null : set;
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static bool IsValidRegex(string? pattern)
    {
        if (pattern is null) return true;
        try
        {
            _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromMilliseconds(200));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static long? ParseKb(string text) =>
        long.TryParse(text.Trim(), out long kb) && kb > 0 ? kb * 1024 : null;

    private void SelectAll_Click(object sender, RoutedEventArgs e) => SetAll(true);

    private void SelectNone_Click(object sender, RoutedEventArgs e) => SetAll(false);

    private void SetAll(bool on)
    {
        foreach (var r in _rows) r.Selected = on;
        List.Items.Refresh();
        UpdateCount();
    }

    private void UpdateCount() =>
        CountText.Text = $"{_rows.Count(r => r.Selected)} selected of {_rows.Count}";

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var targets = _rows.Any(r => r.Selected) ? _rows.Where(r => r.Selected).ToList() : _rows;
        if (targets.Count == 0)
        {
            StatusText.Text = "No links to export.";
            return;
        }
        var dlg = new SaveFileDialog
        {
            Title = "Export links",
            Filter = "Text files (*.txt)|*.txt",
            FileName = "grabber-links.txt",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, string.Join(Environment.NewLine, targets.Select(t => t.Url)) + Environment.NewLine,
                              new UTF8Encoding(false));
            StatusText.Text = $"{targets.Count} links exported.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Grabber link export", ex);
            StatusText.Text = "Export failed. See the log.";
        }
    }

    /// <summary>Send the selected links to the download engine through the shell (respects the "skip editor" option).</summary>
    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        var selected = _rows.Where(r => r.Selected).ToList();
        if (selected.Count == 0)
        {
            StatusText.Text = "Select at least one file.";
            return;
        }
        if (MessageBox.Show(this, $"Send {selected.Count} files to the download engine?", "Download",
                            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        foreach (var row in selected)
        {
            await _shell.AddDownloadAsync(new DownloadRequest
            {
                Url = row.Url,
                FileName = UrlHelper.FileNameFromUrl(row.Url),
                SaveDirectory = _c.DefaultDownloadFolder(),
                Referer = StartUrlBox.Text.Trim(),
            });
        }
        StatusText.Text = $"{selected.Count} files sent to the download engine.";
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024.0 * 1024 * 1024):0.##} GB"
        : bytes >= 1024L * 1024 ? $"{bytes / (1024.0 * 1024):0.#} MB"
        : $"{bytes / 1024.0:0} KB";
}
