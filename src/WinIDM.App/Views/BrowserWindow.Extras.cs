using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using WinIDM.App.Services;
using WinIDM.App.ViewModels;

namespace WinIDM.App.Views;

/// <summary>
/// IDM+ browser features: find in page, view source, print, translate, zoom and text size, full screen, reopen a
/// closed tab (with an Undo message), safe browsing, the link target in the status bar, rendering modes, forced text
/// selection, the cookie switches and the default camera/microphone/location answers.
/// </summary>
public partial class BrowserWindow
{
    private const int MaxClosedTabs = 25;
    private readonly Stack<string> _closedTabs = new();
    private DispatcherTimer? _undoTimer;
    private string? _lastFind;
    private bool _fullScreen;
    private WindowState _stateBeforeFullScreen;
    private WindowStyle _styleBeforeFullScreen;

    private bool CookiesAllowed =>
        _c.Settings.Bool(Keys.AllowCookies) && (!_incognito || _c.Settings.Bool(Keys.IncognitoCookies));

    private async Task WireExtrasAsync(BrowserTabViewModel tab, CoreWebView2 core)
    {
        core.Settings.IsReputationCheckingRequired = _c.Settings.Bool(Keys.SafeBrowsing);
        core.StatusBarTextChanged += (_, _) => { if (_current == tab) ShowTargetUrl(core.StatusBarText); };
        core.ContainsFullScreenElementChanged += (_, _) => SetFullScreen(core.ContainsFullScreenElement);
        if (tab.View is { } view) view.ZoomFactor = DefaultZoom;

        if (RenderingCss() is { } css)
            await core.AddScriptToExecuteOnDocumentCreatedAsync(StyleScript("winidm-rendering", css));
        if (_c.Settings.Bool(Keys.ForceSelection))
            await core.AddScriptToExecuteOnDocumentCreatedAsync(ForceSelectionScript);

        if (!CookiesAllowed)
        {
            // Cookies off: requests carry none, scripts cannot set any, and anything a response stored is removed.
            core.WebResourceRequested += (_, e) => e.Request.Headers.RemoveHeader("Cookie");
            core.NavigationCompleted += (_, _) => core.CookieManager.DeleteAllCookies();
            await core.AddScriptToExecuteOnDocumentCreatedAsync(NoCookieScript);
        }
    }

    private double DefaultZoom => Math.Clamp(_c.Settings.Int(Keys.PageZoom), 25, 500) / 100.0;

    // ---------- Status bar: link target ----------

    private void ShowTargetUrl(string? text)
    {
        if (!_c.Settings.Bool(Keys.ShowTargetUrl) || string.IsNullOrEmpty(text))
        {
            RefreshChrome();
            return;
        }

        var max = Math.Clamp(_c.Settings.Int(Keys.TargetUrlMaxChars), 16, 2048);
        StatusText.Text = text.Length > max ? text[..max] + "…" : text;
    }

    // ---------- Rendering mode and forced selection ----------

    private string? RenderingCss() => _c.Settings.Text(Keys.RenderingMode) switch
    {
        "Inverted" => "html{filter:invert(1) hue-rotate(180deg)!important}img,video,picture,canvas{filter:invert(1) hue-rotate(180deg)!important}",
        "Grayscale" => "html{filter:grayscale(1)!important}",
        "Inverted grayscale" => "html{filter:invert(1) grayscale(1)!important}img,video,picture,canvas{filter:invert(1)!important}",
        "Increase contrast" => "html{filter:contrast(1.6)!important}",
        _ => null,
    };

    private static string StyleScript(string id, string css) =>
        "(()=>{const add=()=>{if(document.getElementById(" + JsonSerializer.Serialize(id) + "))return;" +
        "const s=document.createElement('style');s.id=" + JsonSerializer.Serialize(id) + ";s.textContent=" +
        JsonSerializer.Serialize(css) + ";(document.head||document.documentElement).appendChild(s);};" +
        "if(document.documentElement)add();document.addEventListener('DOMContentLoaded',add);})();";

    private static readonly string ForceSelectionScript =
        StyleScript("winidm-select", "*{-webkit-user-select:text!important;user-select:text!important}") +
        "['copy','cut','paste','selectstart','contextmenu'].forEach(t=>document.addEventListener(t,e=>e.stopPropagation(),true));";

    private const string NoCookieScript =
        "try{Object.defineProperty(Document.prototype,'cookie',{configurable:true,get(){return ''},set(v){}});}catch(e){}";

    // ---------- Find, source, print, translate, zoom ----------

    private async void FindInPage_Click(object sender, RoutedEventArgs e)
    {
        var term = _shell.Prompt("Find in page", "Text to find (F3 finds the next match):", _lastFind);
        if (string.IsNullOrEmpty(term)) return;
        _lastFind = term;
        await FindNextAsync();
    }

    private async Task FindNextAsync()
    {
        if (_current?.View?.CoreWebView2 is not { } core || string.IsNullOrEmpty(_lastFind)) return;
        var found = await core.ExecuteScriptAsync($"window.find({JsonSerializer.Serialize(_lastFind)}, false, false, true)");
        StatusText.Text = found == "true" ? $"Found \"{_lastFind}\"." : $"\"{_lastFind}\" was not found.";
    }

    private async void ViewSource_Click(object sender, RoutedEventArgs e)
    {
        var url = _current?.Address;
        if (!UrlHelper.IsHttp(url)) return;
        await NewTabAsync("view-source:" + url, select: true);
    }

    private void Print_Click(object sender, RoutedEventArgs e) => _current?.View?.CoreWebView2?.ShowPrintUI();

    private async void Translate_Click(object sender, RoutedEventArgs e)
    {
        var url = _current?.Address;
        if (!UrlHelper.IsHttp(url)) return;
        var lang = _c.Settings.Text(Keys.TranslateTo).Trim();
        if (lang.Length == 0) lang = "en";
        // The page address is sent to Google Translate, only when the user asks for it.
        await NewTabAsync($"https://translate.google.com/translate?sl=auto&tl={Uri.EscapeDataString(lang)}&u={Uri.EscapeDataString(url!)}", select: true);
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Zoom(+0.1);

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Zoom(-0.1);

    private void ZoomReset_Click(object sender, RoutedEventArgs e)
    {
        if (_current?.View is { } view) view.ZoomFactor = DefaultZoom;
    }

    private void Zoom(double step)
    {
        if (_current?.View is not { } view) return;
        view.ZoomFactor = Math.Clamp(Math.Round(view.ZoomFactor + step, 2), 0.25, 5);
        StatusText.Text = $"Zoom {view.ZoomFactor * 100:0}%";
    }

    // ---------- Full screen ----------

    private void FullScreen_Click(object sender, RoutedEventArgs e) => SetFullScreen(!_fullScreen);

    private void SetFullScreen(bool on)
    {
        if (on == _fullScreen) return;
        _fullScreen = on;
        var chrome = on ? Visibility.Collapsed : Visibility.Visible;
        TabStripBar.Visibility = chrome;
        NavBar.Visibility = chrome;
        StatusBar.Visibility = chrome;
        if (on)
        {
            _stateBeforeFullScreen = WindowState;
            _styleBeforeFullScreen = WindowStyle;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Normal; // a maximized window keeps the taskbar visible until it is re-maximized
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = _styleBeforeFullScreen;
            WindowState = _stateBeforeFullScreen;
        }
    }

    // ---------- Closed tabs ----------

    private void RememberClosedTab(BrowserTabViewModel tab)
    {
        if (!UrlHelper.IsHttp(tab.Address)) return;
        _closedTabs.Push(tab.Address);
        while (_closedTabs.Count > MaxClosedTabs)
        {
            var keep = _closedTabs.Take(MaxClosedTabs).Reverse().ToList();
            _closedTabs.Clear();
            foreach (var u in keep) _closedTabs.Push(u);
        }

        if (!_c.Settings.Bool(Keys.TabClosedUndo)) return;
        StatusText.Text = "Tab closed.";
        UndoCloseButton.Visibility = Visibility.Visible;
        _undoTimer?.Stop();
        _undoTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(Math.Clamp(_c.Settings.Int(Keys.TabClosedDurationMs), 1000, 30000)),
        };
        _undoTimer.Tick += (_, _) =>
        {
            _undoTimer?.Stop();
            UndoCloseButton.Visibility = Visibility.Collapsed;
            RefreshChrome();
        };
        _undoTimer.Start();
    }

    private async void ReopenClosedTab_Click(object sender, RoutedEventArgs e)
    {
        UndoCloseButton.Visibility = Visibility.Collapsed;
        if (_closedTabs.Count == 0)
        {
            StatusText.Text = "No closed tabs to reopen.";
            return;
        }

        await NewTabAsync(_closedTabs.Pop(), select: true);
    }

    /// <summary>Extra shortcuts: Ctrl+F / F3 find, Ctrl+U source, Ctrl+P print, Ctrl +/-/0 zoom, F11 full screen, Ctrl+Shift+T reopen.</summary>
    private bool HandleExtraKeys(KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (e.Key)
        {
            case Key.F when ctrl: FindInPage_Click(this, e); return true;
            case Key.F3: _ = FindNextAsync(); return true;
            case Key.U when ctrl: ViewSource_Click(this, e); return true;
            case Key.P when ctrl: Print_Click(this, e); return true;
            case Key.T when ctrl && shift: ReopenClosedTab_Click(this, e); return true;
            case Key.OemPlus or Key.Add when ctrl: Zoom(+0.1); return true;
            case Key.OemMinus or Key.Subtract when ctrl: Zoom(-0.1); return true;
            case Key.D0 or Key.NumPad0 when ctrl: ZoomReset_Click(this, e); return true;
            case Key.F11: SetFullScreen(!_fullScreen); return true;
            case Key.Escape when _fullScreen: SetFullScreen(false); return true;
            default: return false;
        }
    }

    // ---------- Permission defaults ----------

    /// <summary>Default answer for camera, microphone and location (IDM+ Website permissions); null = ask.</summary>
    private bool? DefaultPermission(CoreWebView2PermissionKind kind)
    {
        var key = kind switch
        {
            CoreWebView2PermissionKind.Camera => Keys.CameraPolicy,
            CoreWebView2PermissionKind.Microphone => Keys.MicrophonePolicy,
            CoreWebView2PermissionKind.Geolocation => Keys.LocationPolicy,
            _ => null,
        };
        if (key is null) return null;
        return _c.Settings.Text(_incognito ? key + "ig" : key) switch
        {
            "Allow" => true,
            "Block" => false,
            _ => null,
        };
    }
}
