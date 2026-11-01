using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using WinIDM.App.Services;
using WinIDM.Browser;

namespace WinIDM.App.Views;

/// <summary>
/// Built-in video player (MediaElement). Speed, repeat, 5/10/30 second jumps, PiP (small floating window),
/// full screen, SRT/VTT subtitles, control lock, and keyboard shortcuts. Only HTTP(S) URLs are accepted.
/// No external communication beyond the media the player itself downloads.
/// </summary>
public partial class BrowserVideoPlayerWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private IReadOnlyList<SubtitleCue> _cues = Array.Empty<SubtitleCue>();
    private bool _dragging;
    private bool _playing;
    private bool _locked;
    private bool _pip;
    private bool _fullscreen;
    private double _savedWidth = 900, _savedHeight = 560;

    public BrowserVideoPlayerWindow(string url)
    {
        InitializeComponent();
        if (!UrlHelper.IsHttp(url)) throw new ArgumentException("The player only accepts HTTP(S) URLs.", nameof(url));
        Title = "Video player - " + UrlHelper.Redact(url);
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Player.Source = new Uri(url, UriKind.Absolute);
        Player.Play();
        _playing = true;
        HintText.Text = "Shortcuts: Space = play/pause, ←/→ = 5 s, ↑/↓ = volume, +/- = speed, M = mute, R = repeat, P = PiP, F = full screen, L = lock.";
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _timer.Stop();
        Player.Stop();
        Player.Close();
        Player.Source = null;
    }

    // ---------- Timing and controls

    private void Player_Opened(object sender, RoutedEventArgs e)
    {
        if (Player.NaturalDuration.HasTimeSpan)
        {
            Seekbar.Maximum = Player.NaturalDuration.TimeSpan.TotalSeconds;
            InfoText.Text = $"{Player.NaturalVideoWidth}x{Player.NaturalVideoHeight}";
        }
    }

    private void Player_Ended(object sender, RoutedEventArgs e)
    {
        if (RepeatBox.IsChecked == true)
        {
            Player.Position = TimeSpan.Zero;
            Player.Play();
            _playing = true;
        }
        else
        {
            Player.Pause();
            _playing = false;
        }
    }

    private void Player_Failed(object sender, ExceptionRoutedEventArgs e)
    {
        AppLog.Error("Video player", e.ErrorException);
        HintText.Text = "Failed to load the media. The format or source is not supported.";
    }

    private void Tick()
    {
        if (!Player.NaturalDuration.HasTimeSpan) return;
        var position = Player.Position;
        var duration = Player.NaturalDuration.TimeSpan;
        if (!_dragging) Seekbar.Value = Math.Min(position.TotalSeconds, Seekbar.Maximum);
        TimeText.Text = $"{Format(position)} / {Format(duration)}";

        if (_cues.Count > 0)
        {
            var text = BrowserSubtitles.ActiveText(_cues, position);
            SubtitleBox.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
            SubtitleText.Text = text ?? "";
        }
    }

    private static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");

    private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePlay();

    private void TogglePlay()
    {
        if (_locked) return;
        if (_playing)
        {
            Player.Pause();
            _playing = false;
        }
        else
        {
            Player.Play();
            _playing = true;
        }
    }

    private void Seek_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && double.TryParse(tag, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double seconds))
        {
            Seek(TimeSpan.FromSeconds(seconds));
        }
    }

    private void Seek(TimeSpan delta)
    {
        if (_locked || !Player.NaturalDuration.HasTimeSpan) return;
        var target = Player.Position + delta;
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        var max = Player.NaturalDuration.TimeSpan;
        if (target > max) target = max;
        Player.Position = target;
    }

    private void Seekbar_Down(object sender, MouseButtonEventArgs e) => _dragging = true;

    private void Seekbar_Up(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        if (_locked || !Player.NaturalDuration.HasTimeSpan) return;
        Player.Position = TimeSpan.FromSeconds(Seekbar.Value);
    }

    private void Speed_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SpeedBox.SelectedItem is ComboBoxItem { Tag: string tag } &&
            double.TryParse(tag, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double ratio))
        {
            Player.SpeedRatio = ratio;
        }
    }

    private void Mute_Click(object sender, RoutedEventArgs e) => Player.IsMuted = MuteBox.IsChecked == true;

    private void Pip_Click(object sender, RoutedEventArgs e) => SetPip(!_pip);

    /// <summary>In-browser PiP: a small always-on-top window. The MediaElement keeps the video aspect ratio.</summary>
    private void SetPip(bool on)
    {
        if (on == _pip) return;
        _pip = on;
        if (on)
        {
            _savedWidth = Width;
            _savedHeight = Height;
            Topmost = true;
            Width = 480;
            Height = 270;
            Toolbar.Visibility = Visibility.Collapsed;
        }
        else
        {
            Topmost = false;
            Width = _savedWidth;
            Height = _savedHeight;
            Toolbar.Visibility = Visibility.Visible;
        }
    }

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => SetFullscreen(!_fullscreen);

    private void SetFullscreen(bool on)
    {
        if (on == _fullscreen) return;
        _fullscreen = on;
        if (on)
        {
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            Toolbar.Visibility = Visibility.Collapsed;
            Seekbar.Visibility = Visibility.Collapsed;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = WindowState.Normal;
            Toolbar.Visibility = Visibility.Visible;
            Seekbar.Visibility = Visibility.Visible;
        }
    }

    private void Lock_Click(object sender, RoutedEventArgs e) => SetLocked(!_locked);

    /// <summary>Lock controls: mouse clicks and the control bar are disabled. Only L (or this button before locking) unlocks.</summary>
    private void SetLocked(bool on)
    {
        _locked = on;
        Toolbar.IsHitTestVisible = !on;
        Seekbar.IsHitTestVisible = !on;
        LockButton.Content = on ? "Unlock" : "Lock";
        HintText.Text = on ? "Controls locked. Press L to unlock." : HintText.Text;
    }

    // ---------- Subtitles

    private void Subtitles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choose a subtitle file (SRT or VTT)",
            Filter = "Subtitles (*.srt;*.vtt)|*.srt;*.vtt|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(dlg.FileName).Length > BrowserSubtitles.MaxBytes)
            {
                HintText.Text = "Subtitle file is too large (2 MB limit).";
                return;
            }
            var text = File.ReadAllText(dlg.FileName, Encoding.UTF8);
            _cues = BrowserSubtitles.Parse(text);
            HintText.Text = _cues.Count == 0 ? "No valid subtitle cues in the file." : $"{_cues.Count} subtitle cues loaded.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            AppLog.Error("Load subtitles", ex);
            HintText.Text = "Failed to read the subtitle file.";
        }
    }

    // ---------- Keyboard shortcuts

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.L)
        {
            SetLocked(!_locked);
            e.Handled = true;
            return;
        }
        if (_locked) return;

        switch (e.Key)
        {
            case Key.Space:
                TogglePlay();
                break;
            case Key.Left:
                Seek(TimeSpan.FromSeconds(-5));
                break;
            case Key.Right:
                Seek(TimeSpan.FromSeconds(5));
                break;
            case Key.Up:
                Player.Volume = Math.Min(1, Player.Volume + 0.05);
                break;
            case Key.Down:
                Player.Volume = Math.Max(0, Player.Volume - 0.05);
                break;
            case Key.OemPlus or Key.Add:
                StepSpeed(+1);
                break;
            case Key.OemMinus or Key.Subtract:
                StepSpeed(-1);
                break;
            case Key.M:
                MuteBox.IsChecked = !(MuteBox.IsChecked == true);
                Player.IsMuted = MuteBox.IsChecked == true;
                break;
            case Key.R:
                RepeatBox.IsChecked = !(RepeatBox.IsChecked == true);
                break;
            case Key.P:
                SetPip(!_pip);
                break;
            case Key.F:
                SetFullscreen(!_fullscreen);
                break;
            case Key.Escape when _fullscreen:
                SetFullscreen(false);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void StepSpeed(int direction)
    {
        int next = Math.Clamp(SpeedBox.SelectedIndex + direction, 0, SpeedBox.Items.Count - 1);
        SpeedBox.SelectedIndex = next;
    }
}
