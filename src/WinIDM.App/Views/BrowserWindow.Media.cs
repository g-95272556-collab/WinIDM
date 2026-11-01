using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using WinIDM.App.Services;
using WinIDM.Browser;
using WinIDM.Core.Models;

namespace WinIDM.App.Views;

/// <summary>Media panel actions: HLS/DASH quality list, video player, and the grabber.</summary>
public partial class BrowserWindow
{
    /// <summary>Read quality variants from the playlist (network requests only on user click).</summary>
    private async void MediaQuality_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not DetectedMedia media) return;
        try
        {
            StatusText.Text = "Reading the quality list…";
            var variants = await _c.Media.ExpandPlaylistAsync(media.Url);
            if (variants.Count == 0)
            {
                StatusText.Text = "No quality variants found for this link.";
                return;
            }
            // Variants are downloaded with the same request context as the captured item (cookies, UA, Referer).
            new BrowserQualityWindow(_shell, media.Url, variants,
                v => BuildMediaRequestAsync(v with { PageUrl = v.PageUrl ?? media.PageUrl, Title = v.Title ?? media.Title }))
            { Owner = this }.Show();
            StatusText.Text = $"{variants.Count} variants found.";
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or TaskCanceledException or ArgumentException)
        {
            AppLog.Error("Media quality list", ex);
            StatusText.Text = "Failed to read the quality list: " + ex.Message;
        }
    }

    private void MediaPlay_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not DetectedMedia media) return;
        if (!UrlHelper.IsHttp(media.Url))
        {
            StatusText.Text = "The player only accepts HTTP(S) URLs.";
            return;
        }
        new BrowserVideoPlayerWindow(media.Url) { Owner = this }.Show();
    }

    private void OpenGrabber_Click(object sender, RoutedEventArgs e) =>
        new BrowserGrabberWindow(_c, _shell, _current?.Address) { Owner = this }.Show();
}
