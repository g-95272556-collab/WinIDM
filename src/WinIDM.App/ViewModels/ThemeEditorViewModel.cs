using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinIDM.App.Services;

namespace WinIDM.App.ViewModels;

/// <summary>One color slot in the editor. HEX values are validated before the preview updates.</summary>
public sealed partial class ThemeColorItem : ObservableObject
{
    private readonly ThemeEditorViewModel _owner;

    public ThemeColorItem(ThemeSlot slot, string hex, ThemeEditorViewModel owner)
    {
        Slot = slot;
        _owner = owner;
        _hex = hex;
    }

    public ThemeSlot Slot { get; }
    public string Label => Slot.Label;
    public string Group => Slot.Group;
    public bool IsValid => ThemePalette.IsValidColor(Hex);

    [ObservableProperty] private string _hex = "";

    partial void OnHexChanged(string value)
    {
        OnPropertyChanged(nameof(IsValid));
        _owner.OnColorChanged();
    }
}

/// <summary>
/// Custom theme editor: 41 named slots, live preview, save/cancel/reset, plus JSON export and import in its own
/// <c>winidm-theme</c> format.
/// </summary>
public sealed partial class ThemeEditorViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private bool _suspend;

    public ThemeEditorViewModel(AppSettings settings)
    {
        _settings = settings;
        var json = _settings.Text("app.theme.custom");
        var dark = string.IsNullOrWhiteSpace(json) ? ThemeService.IsDark : ThemePalette.IsDarkBase(json);
        var colors = ThemePalette.Resolve(json, dark);
        _baseChoice = dark ? ThemePalette.BaseDark : ThemePalette.BaseLight;
        _themeName = string.IsNullOrWhiteSpace(json) ? "My theme" : ReadName(json);
        _status = "The preview updates as you type. Save to keep it.";
        foreach (var slot in ThemePalette.Slots)
        {
            Items.Add(new ThemeColorItem(slot, colors[slot.Key], this));
        }
    }

    public ObservableCollection<ThemeColorItem> Items { get; } = new();

    public IReadOnlyList<string> BaseChoices { get; } = new[] { ThemePalette.BaseLight, ThemePalette.BaseDark };

    [ObservableProperty] private string _themeName = "";
    [ObservableProperty] private string _status = "";

    /// <summary>Base for reset defaults and export. Changing the base loads the new base defaults.</summary>
    [ObservableProperty] private string _baseChoice = ThemePalette.BaseLight;

    public bool IsAllValid => Items.All(i => i.IsValid);

    partial void OnBaseChoiceChanged(string value)
    {
        if (_suspend) return;
        LoadColors(ThemePalette.Defaults(value == ThemePalette.BaseDark));
        Status = $"Base changed to {value}. Colors reloaded from the base defaults.";
    }

    public void OnColorChanged()
    {
        if (_suspend) return;
        OnPropertyChanged(nameof(IsAllValid));
        if (IsAllValid) ThemeService.Preview(CurrentColors());
        else Status = "Some HEX values are invalid (use #RRGGBB or #AARRGGBB).";
    }

    [RelayCommand]
    private void Save()
    {
        if (!IsAllValid)
        {
            Status = "Save canceled: some HEX values are invalid.";
            return;
        }

        var json = ThemePalette.Export(ThemeName, BaseChoice == ThemePalette.BaseDark, CurrentColors());
        _settings.Set("app.theme.custom", json);
        _settings.Set(Keys.Theme, "Custom");
        ThemeService.Apply("Custom", json);
        Status = "Custom theme saved and applied.";
    }

    [RelayCommand]
    private void Reset()
    {
        LoadColors(ThemePalette.Defaults(BaseChoice == ThemePalette.BaseDark));
        Status = "Colors reset to the base defaults.";
    }

    /// <summary>Export the current palette as JSON (to a file chosen by the user).</summary>
    public string ExportJson() => ThemePalette.Export(ThemeName, BaseChoice == ThemePalette.BaseDark, CurrentColors());

    /// <summary>Import a palette from JSON. Throws <see cref="System.IO.InvalidDataException"/> when invalid.</summary>
    public void ImportJson(string json)
    {
        var parsed = ThemePalette.Import(json);
        _suspend = true;
        try
        {
            ThemeName = parsed.Name;
            BaseChoice = parsed.Dark ? ThemePalette.BaseDark : ThemePalette.BaseLight;
        }
        finally
        {
            _suspend = false;
        }

        LoadColors(parsed.Colors);
        Status = parsed.IgnoredKeys > 0
            ? $"Theme imported. {parsed.IgnoredKeys} unknown keys ignored."
            : "Theme imported. Click Save to apply it.";
    }

    /// <summary>Effective colors for preview/save (valid entries only; invalid HEX falls back to the default).</summary>
    public IReadOnlyDictionary<string, string> CurrentColors()
    {
        var defaults = ThemePalette.Defaults(BaseChoice == ThemePalette.BaseDark);
        return Items.ToDictionary(
            i => i.Slot.Key,
            i => i.IsValid ? i.Hex.ToUpperInvariant() : defaults[i.Slot.Key],
            StringComparer.Ordinal);
    }

    private void LoadColors(IReadOnlyDictionary<string, string> colors)
    {
        _suspend = true;
        try
        {
            foreach (var item in Items)
            {
                item.Hex = colors.TryGetValue(item.Slot.Key, out var v) ? v : item.Hex;
            }
        }
        finally
        {
            _suspend = false;
        }

        OnPropertyChanged(nameof(IsAllValid));
        if (IsAllValid) ThemeService.Preview(CurrentColors());
    }

    private static string ReadName(string json)
    {
        try
        {
            return ThemePalette.Import(json).Name;
        }
        catch (System.IO.InvalidDataException)
        {
            return "My theme";
        }
    }
}
