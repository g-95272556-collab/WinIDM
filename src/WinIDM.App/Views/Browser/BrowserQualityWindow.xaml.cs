using System.Windows;
using WinIDM.App.Services;
using WinIDM.Core.Models;

namespace WinIDM.App.Views;

/// <summary>
/// HLS/DASH variant list with quality and estimated size. The user picks one variant before downloading.
/// Downloads are sent to the download engine; nothing starts automatically.
/// </summary>
public partial class BrowserQualityWindow : Window
{
    private sealed record Row(string Quality, string Kind, string Size, string Url, DetectedMedia Media);

    private readonly IShell _shell;
    private readonly Func<DetectedMedia, Task<DownloadRequest>> _buildRequest;

    /// <param name="buildRequest">Builds the request for a variant with the browser's cookies, user agent and Referer.</param>
    public BrowserQualityWindow(IShell shell, string sourceUrl, IReadOnlyList<DetectedMedia> variants,
        Func<DetectedMedia, Task<DownloadRequest>> buildRequest)
    {
        InitializeComponent();
        _shell = shell;
        _buildRequest = buildRequest;
        HeaderText.Text = "Variants for " + UrlHelper.Redact(sourceUrl);
        List.ItemsSource = variants.Select(v => new Row(
            string.IsNullOrWhiteSpace(v.Quality) ? "Not specified" : v.Quality,
            v.Kind.ToString(),
            v.SizeBytes is { } s ? FormatSize(s) : "unknown",
            v.Url,
            v)).ToList();
        if (variants.Count > 0) List.SelectedIndex = 0;
    }

    private Row? Selected => List.SelectedItem as Row;

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row)
        {
            StatusText.Text = "Select a quality.";
            return;
        }
        try
        {
            await _shell.AddDownloadAsync(await _buildRequest(row.Media));
            StatusText.Text = $"Sent to the download engine: {row.Quality}.";
        }
        catch (Exception ex)
        {
            AppLog.Error("Quality download", ex);
            StatusText.Text = "Could not start the download: " + ex.Message;
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        try
        {
            Clipboard.SetText(row.Media.Url);
            StatusText.Text = "Link copied.";
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            AppLog.Error("Copy quality link", ex);
            StatusText.Text = "Clipboard is busy. Try again.";
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static string FormatSize(long bytes) =>
        bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024.0 * 1024 * 1024):0.##} GB"
        : bytes >= 1024L * 1024 ? $"{bytes / (1024.0 * 1024):0.#} MB"
        : $"{bytes / 1024.0:0} KB";
}
