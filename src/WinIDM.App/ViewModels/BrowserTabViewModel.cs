using System.Collections.ObjectModel;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Web.WebView2.Wpf;
using WinIDM.Core.Models;

namespace WinIDM.App.ViewModels;

/// <summary>
/// State of one browser tab. The WebView2 control is stored in <see cref="View"/> and is not bound directly.
/// </summary>
public sealed partial class BrowserTabViewModel : ObservableObject
{
    private static int _frameSeq;

    [ObservableProperty] private string _title = "New page";
    [ObservableProperty] private string _url = "";
    [ObservableProperty] private string _address = "";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isDesktopMode;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isStartPage = true;

    /// <summary>Media detected for the current page (cleared when the main navigation starts).</summary>
    public ObservableCollection<DetectedMedia> Media { get; } = new();

    /// <summary>Main frame ID of the tab for the GM_* bridge (unique per tab, not from the page).</summary>
    public string FrameId { get; } = "tab" + Interlocked.Increment(ref _frameSeq).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Script IDs registered for the next document (to be removed on the next navigation).</summary>
    public List<string> ScriptRegistrations { get; } = new();

    /// <summary>Serializes script registration so two fast navigations do not interleave.</summary>
    public SemaphoreSlim ScriptGate { get; } = new(1, 1);

    /// <summary>Last top-level navigation URL (used to detect iframe frames).</summary>
    public string TopNavigationUrl { get; set; } = "";

    public WebView2? View { get; set; }
}
