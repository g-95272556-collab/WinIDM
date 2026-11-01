using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using WinIDM.App.Services;
using WinIDM.Core.Models;

namespace WinIDM.App.ViewModels;

/// <summary>Download editor (URL, file name, folder, segments, auth, referer).</summary>
public sealed partial class AddUrlViewModel : ObservableObject
{
    private readonly Composition _c;
    private string _autoName = "";

    // Request context the editor does not expose but must not lose: captured media and browser downloads arrive
    // with cookies, a user agent, extra headers and (for scheduled items) a time. Dropping them here is what made
    // signed CDN links answer 403 after the user confirmed the dialog.
    private readonly string? _cookieHeader;
    private readonly string? _userAgent;
    private readonly Dictionary<string, string> _headers;
    private readonly DateTimeOffset? _scheduledAt;
    private readonly string? _prefillHost;

    public AddUrlViewModel(Composition composition, DownloadRequest prefill)
    {
        _c = composition;
        _cookieHeader = prefill.CookieHeader;
        _userAgent = prefill.UserAgent;
        _headers = new Dictionary<string, string>(prefill.Headers, StringComparer.OrdinalIgnoreCase);
        _scheduledAt = prefill.ScheduledAt;
        _prefillHost = HostOf(prefill.Url);
        // Fields are set directly so initialization does not trigger OnXxxChanged.
        _url = prefill.Url ?? "";
        _folder = string.IsNullOrWhiteSpace(prefill.SaveDirectory) ? _c.DefaultDownloadFolder() : prefill.SaveDirectory!;
        _referer = prefill.Referer ?? "";
        _username = prefill.Username ?? "";
        // The stored value is count - 1 (IDM+ convention, read the same way by the engine).
        _parts = prefill.MaxSegments ?? Math.Clamp(_c.Settings.Int(Keys.Parts) + 1, 1, 32);
        _startImmediately = prefill.StartImmediately;
        _fileName = prefill.FileName ?? "";
        if (string.IsNullOrWhiteSpace(_fileName) && UrlHelper.IsHttp(_url))
        {
            _autoName = UrlHelper.FileNameFromUrl(_url);
            _fileName = _autoName;
        }
    }

    [ObservableProperty] private string _url = "";
    [ObservableProperty] private string _fileName = "";
    [ObservableProperty] private string _folder = "";
    [ObservableProperty] private int _parts = 8;
    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _referer = "";
    [ObservableProperty] private bool _startImmediately = true;
    [ObservableProperty] private bool _skipEditorFromNow;
    [ObservableProperty] private string _error = "";

    /// <summary>The file name is suggested again when the URL changes, as long as the user has not edited it.</summary>
    partial void OnUrlChanged(string value)
    {
        if (!UrlHelper.IsHttp(value?.Trim())) return;
        var suggested = UrlHelper.FileNameFromUrl(value!.Trim());
        if (string.IsNullOrWhiteSpace(FileName) || FileName == _autoName)
        {
            _autoName = suggested;
            FileName = suggested;
        }
    }

    /// <summary>Validate and build the download request. Returns null and an error message when invalid.</summary>
    public DownloadRequest? Build()
    {
        Error = "";
        var url = Url?.Trim() ?? "";
        if (!UrlHelper.IsDownloadable(url))
        {
            Error = "Invalid URL. Use http://, https:// or magnet:?.";
            return null;
        }
        if (Parts < 1 || Parts > 32)
        {
            Error = "The number of segments must be between 1 and 32.";
            return null;
        }
        if (string.IsNullOrWhiteSpace(Folder) || !Directory.Exists(Folder))
        {
            Error = "The save folder does not exist.";
            return null;
        }

        // Strip the path: only a file name is accepted (prevents path traversal).
        var name = Path.GetFileName(string.IsNullOrWhiteSpace(FileName) ? UrlHelper.FileNameFromUrl(url) : FileName.Trim());
        if (string.IsNullOrWhiteSpace(name)) name = UrlHelper.FileNameFromUrl(url);

        if (SkipEditorFromNow) _c.Settings.Set(Keys.SkipEditor, true);

        // Captured cookies, UA and extra headers belong to the original host. If the user points the URL at a
        // different host, do not leak them there.
        bool sameHost = string.Equals(_prefillHost, HostOf(url), StringComparison.OrdinalIgnoreCase);

        return new DownloadRequest
        {
            Url = url,
            FileName = name,
            SaveDirectory = Folder,
            Referer = string.IsNullOrWhiteSpace(Referer) ? null : Referer.Trim(),
            Username = string.IsNullOrWhiteSpace(Username) ? null : Username.Trim(),
            Password = string.IsNullOrEmpty(Password) ? null : Password,
            MaxSegments = Parts,
            StartImmediately = StartImmediately,
            // Keep the context the editor does not show (captured cookies, UA, extra headers, schedule).
            CookieHeader = sameHost ? _cookieHeader : null,
            UserAgent = sameHost ? _userAgent : null,
            Headers = sameHost
                ? new Dictionary<string, string>(_headers, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            ScheduledAt = _scheduledAt,
        };
    }

    private static string? HostOf(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host.ToLowerInvariant() : null;
}
