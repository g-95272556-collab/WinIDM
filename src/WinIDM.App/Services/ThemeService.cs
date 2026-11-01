using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace WinIDM.App.Services;

/// <summary>
/// Light, dark, custom and system themes. The XAML base (Light/Dark) loads first; an overlay ResourceDictionary is then
/// built from the 41 color slots. That overlay is replaced during previews without reloading XAML.
/// </summary>
public static class ThemeService
{
    // Absolute pack URI: ResourceDictionaries created in code do not inherit the XAML base URI.
    private const string LightPath = "pack://application:,,,/Themes/Light.xaml";
    private const string DarkPath = "pack://application:,,,/Themes/Dark.xaml";

    /// <summary>Theme choices exposed to the UI.</summary>
    public const string ChoiceLight = "Light";
    public const string ChoiceDark = "Dark";
    public const string ChoiceSystem = "System";
    public const string ChoiceCustom = "Custom";

    private static string _choice = ChoiceSystem;
    private static string _customJson = "";
    private static ResourceDictionary? _overlay;
    private static bool _hooked;

    public static bool IsDark { get; private set; }

    /// <summary>Raised after a theme has been applied, so shell windows can repaint native chrome.</summary>
    public static event Action? Changed;

    /// <summary>Current choice (Light, Dark, System, Custom).</summary>
    public static string Choice => _choice;

    /// <summary>
    /// Apply a theme. <paramref name="customJson"/> is a custom palette (<c>winidm-theme</c> format); null keeps the current one.
    /// Legacy Malay choice names from older settings files are normalized, so existing user data keeps working.
    /// </summary>
    public static void Apply(string choice, string? customJson = null)
    {
        _choice = NormalizeChoice(choice);
        if (customJson is not null) _customJson = customJson;
        var app = Application.Current;
        if (app is null) return;

        IsDark = _choice switch
        {
            ChoiceDark => true,
            ChoiceLight => false,
            ChoiceCustom => ThemePalette.IsDarkBase(_customJson),
            _ => SystemUsesDarkTheme(),
        };
        app.ThemeMode = _choice switch
        {
            ChoiceDark => ThemeMode.Dark,
            ChoiceLight => ThemeMode.Light,
            ChoiceCustom => IsDark ? ThemeMode.Dark : ThemeMode.Light,
            _ => ThemeMode.System,
        };

        var target = IsDark ? DarkPath : LightPath;
        var merged = app.Resources.MergedDictionaries;
        for (int i = merged.Count - 1; i >= 0; i--)
        {
            var src = merged[i].Source?.OriginalString ?? "";
            if (src.EndsWith("Light.xaml", StringComparison.OrdinalIgnoreCase) ||
                src.EndsWith("Dark.xaml", StringComparison.OrdinalIgnoreCase))
                merged.RemoveAt(i);
        }
        merged.Add(new ResourceDictionary { Source = new Uri(target, UriKind.Absolute) });

        // The overlay must be added after the XAML base so its values win.
        SetOverlay(ColorsFor(_choice));

        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // A window may already be closing; repainting chrome is best effort.
        }

        if (!_hooked)
        {
            _hooked = true;
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category == UserPreferenceCategory.General && _choice == ChoiceSystem)
                    Application.Current?.Dispatcher.BeginInvoke(() => Apply(ChoiceSystem));
            };
        }
    }

    /// <summary>Preview: replace the overlay colors without changing settings. Use <see cref="Apply"/> to restore.</summary>
    public static void Preview(IReadOnlyDictionary<string, string> colors) => SetOverlay(colors);

    /// <summary>Effective colors for the current choice (light/dark default, or the custom palette).</summary>
    public static IReadOnlyDictionary<string, string> ColorsFor(string choice)
    {
        var normalized = NormalizeChoice(choice);
        if (normalized == ChoiceCustom) return ThemePalette.Resolve(_customJson, ThemePalette.IsDarkBase(_customJson));
        return ThemePalette.Defaults(normalized switch
        {
            ChoiceDark => true,
            ChoiceLight => false,
            _ => SystemUsesDarkTheme(),
        });
    }

    /// <summary>Dark base of the current custom palette (for display and the theme editor).</summary>
    public static string CustomJson => _customJson;

    /// <summary>Map legacy Malay choice names ("Terang", "Gelap", "Sistem", "Tersuai") onto the English ones.</summary>
    public static string NormalizeChoice(string? choice) => choice?.Trim() switch
    {
        null or "" => ChoiceSystem,
        "Terang" => ChoiceLight,
        "Gelap" => ChoiceDark,
        "Sistem" => ChoiceSystem,
        "Tersuai" => ChoiceCustom,
        ChoiceLight => ChoiceLight,
        ChoiceDark => ChoiceDark,
        ChoiceSystem => ChoiceSystem,
        ChoiceCustom => ChoiceCustom,
        _ => ChoiceSystem,
    };

    private static void SetOverlay(IReadOnlyDictionary<string, string> colors)
    {
        var app = Application.Current;
        if (app is null) return;

        var dict = new ResourceDictionary();
        Color Slot(string key)
        {
            var hex = colors.TryGetValue(key, out var v) && ThemePalette.IsValidColor(v) ? v : ThemePalette.Defaults(IsDark)[key];
            return (Color)ColorConverter.ConvertFromString(hex);
        }

        foreach (var slot in ThemePalette.Slots)
        {
            var brush = new SolidColorBrush(Slot(slot.Key));
            brush.Freeze();
            dict[slot.ResourceKey] = brush;
        }

        // Derived brushes used by the shell layout: soft tints for selected navigation, badges and status pills.
        var surface = Slot("Surface");
        AddDerived(dict, "AccentSoftBrush", Slot("Accent"), surface, 0.14);
        AddDerived(dict, "AccentSoftBorderBrush", Slot("Accent"), surface, 0.34);
        AddDerived(dict, "DangerSoftBrush", Slot("Danger"), surface, 0.12);
        AddDerived(dict, "SuccessSoftBrush", Slot("Success"), surface, 0.12);
        AddDerived(dict, "WarningSoftBrush", Slot("Warning"), surface, 0.14);

        var merged = app.Resources.MergedDictionaries;
        if (_overlay is not null) merged.Remove(_overlay);
        merged.Add(dict);
        _overlay = dict;
    }

    private static void AddDerived(ResourceDictionary dict, string key, Color tint, Color surface, double amount)
    {
        var brush = new SolidColorBrush(Blend(surface, tint, amount));
        brush.Freeze();
        dict[key] = brush;
    }

    /// <summary>Blend <paramref name="tint"/> over <paramref name="baseColor"/> by <paramref name="amount"/> (0..1).</summary>
    private static Color Blend(Color baseColor, Color tint, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        byte Mix(byte a, byte b) => (byte)Math.Round(a + (b - a) * amount);
        return Color.FromArgb(255, Mix(baseColor.R, tint.R), Mix(baseColor.G, tint.G), Mix(baseColor.B, tint.B));
    }

    private static bool SystemUsesDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch (Exception ex) when (ex is IOException or System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
