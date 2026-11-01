using System.IO;
using System.Media;

namespace WinIDM.App.Services;

/// <summary>
/// Per-event sounds (03a: idm_pref_sound_ringtone_start/error/complete/complete_all). Each setting is empty (no sound),
/// "default" (the Windows sound for that kind of event), or the path of a .wav file. Nothing plays unless the master
/// switch <see cref="Keys.Sound"/> is on.
/// </summary>
public static class SoundService
{
    public const string Default = "default";

    public static void Play(AppSettings settings, string eventKey)
    {
        if (!settings.Bool(Keys.Sound)) return;
        var choice = settings.Text(eventKey).Trim().Trim('"');
        if (choice.Length == 0 || choice.Equals("none", StringComparison.OrdinalIgnoreCase)) return;

        if (choice.Equals(Default, StringComparison.OrdinalIgnoreCase))
        {
            SystemSoundFor(eventKey).Play();
            return;
        }

        if (!choice.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || !File.Exists(choice))
        {
            SystemSoundFor(eventKey).Play(); // a moved or invalid file still gives an audible cue
            return;
        }

        try
        {
            using var player = new SoundPlayer(choice);
            player.Play();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
        {
            AppLog.Error("Play sound", ex);
        }
    }

    private static SystemSound SystemSoundFor(string eventKey) => eventKey switch
    {
        Keys.SoundError => SystemSounds.Hand,
        Keys.SoundStart => SystemSounds.Beep,
        Keys.SoundCompleteAll => SystemSounds.Exclamation,
        _ => SystemSounds.Asterisk,
    };
}
