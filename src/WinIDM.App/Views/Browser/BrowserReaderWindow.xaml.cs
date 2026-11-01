using System.Speech.Synthesis;
using System.Windows;
using System.Windows.Controls;
using WinIDM.App.Services;
using WinIDM.Browser;

namespace WinIDM.App.Views;

/// <summary>
/// Reader mode: article text only, with font size and speech playback (System.Speech, fully local).
/// No data is sent out. Playback stops when the window closes.
/// </summary>
public partial class BrowserReaderWindow : Window
{
    private const string FontPref = "reader.fontSize";
    private const string RatePref = "reader.rate";

    private readonly SpeechSynthesizer _tts = new();
    private readonly BrowserPrefs _prefs;
    private bool _loading = true;

    public BrowserReaderWindow(ReaderArticle article, BrowserPrefs prefs)
    {
        InitializeComponent();
        _prefs = prefs;
        TitleText.Text = string.IsNullOrWhiteSpace(article.Title) ? "Untitled" : article.Title;
        BylineText.Text = article.Byline ?? "";
        BodyText.Text = article.Text;
        Title = "Reader mode - " + TitleText.Text;

        FontSlider.Value = Math.Clamp(prefs.Get(FontPref, 18), 12, 32);
        ApplyFont();
        RateSlider.Value = Math.Clamp(prefs.Get(RatePref, 0), -10, 10);
        _tts.Rate = (int)RateSlider.Value;

        foreach (var voice in _tts.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo.Name))
            VoiceBox.Items.Add(voice);
        if (VoiceBox.Items.Count == 0)
            StatusText.Text = "No speech voices are installed. Install voices in Windows Settings > Time & language.";
        else
            VoiceBox.SelectedIndex = 0;
        _loading = false;
        Closed += (_, _) =>
        {
            try { _tts.SpeakAsyncCancelAll(); }
            catch (InvalidOperationException) { }
            _tts.Dispose();
        };
    }

    private void ApplyFont()
    {
        BodyText.FontSize = FontSlider.Value;
        if (!_loading) _prefs.Set(FontPref, (int)FontSlider.Value);
    }

    private void Font_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => ApplyFont();

    private void Rate_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _tts.Rate = (int)RateSlider.Value;
        _prefs.Set(RatePref, (int)RateSlider.Value);
    }

    private void Voice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (VoiceBox.SelectedItem is string name)
        {
            try { _tts.SelectVoice(name); }
            catch (ArgumentException) { StatusText.Text = "Voice unavailable."; }
        }
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(BodyText.Text))
        {
            StatusText.Text = "No text to read.";
            return;
        }
        if (_tts.State == SynthesizerState.Paused)
        {
            _tts.Resume();
            return;
        }
        _tts.SpeakAsyncCancelAll();
        _tts.SpeakAsync(TitleText.Text + ". " + BodyText.Text);
        StatusText.Text = "Reading...";
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_tts.State == SynthesizerState.Speaking)
        {
            _tts.Pause();
            StatusText.Text = "Paused.";
        }
    }

    private void Resume_Click(object sender, RoutedEventArgs e)
    {
        if (_tts.State == SynthesizerState.Paused)
        {
            _tts.Resume();
            StatusText.Text = "Reading...";
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _tts.SpeakAsyncCancelAll();
        StatusText.Text = "Stopped.";
    }
}
