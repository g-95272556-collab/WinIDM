using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinIDM.App.Services;
using WinIDM.Core.Contracts;

namespace WinIDM.App.ViewModels;

/// <summary>Settings with 9 categories (generated from <see cref="SettingsCatalog"/>).</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly ISettingsStore _raw;

    public SettingsViewModel(AppSettings settings, ISettingsStore raw)
    {
        _settings = settings;
        _raw = raw;
        _selectedCategory = SettingsCatalog.Categories[0];
        Rebuild();
    }

    public IReadOnlyList<string> Categories => SettingsCatalog.Categories;

    public ObservableCollection<SettingGroupViewModel> Groups { get; } = new();

    [ObservableProperty] private string _selectedCategory;
    [ObservableProperty] private string _footer = "Changes are saved immediately. A \"Not active yet\" marker means the setting has not taken effect.";

    partial void OnSelectedCategoryChanged(string value) => Rebuild();

    private void Rebuild()
    {
        Groups.Clear();
        var defs = SettingsCatalog.All.Where(d => d.Visible && d.Category == SelectedCategory);
        foreach (var g in defs.GroupBy(d => d.Group))
            Groups.Add(new SettingGroupViewModel(g.Key, g.Select(d => new SettingItemViewModel(d, _settings, OnItemChanged)).ToList()));
    }

    private void OnItemChanged(string key, object? value)
    {
        try
        {
            switch (key)
            {
                case Keys.StartWithWindows:
                    SystemIntegration.SetStartWithWindows((bool)value!);
                    break;
                case Keys.RegisterProtocols:
                    SystemIntegration.SetProtocolRegistration((bool)value!);
                    break;
                case Keys.Theme:
                    ThemeService.Apply((string)value!);
                    break;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            AppLog.Error($"Settings {key}", ex);
            Footer = "Could not write the Windows registry. Try again or check permissions.";
        }
    }

    [RelayCommand]
    private void ResetCategory()
    {
        foreach (var d in SettingsCatalog.All.Where(d => d.Category == SelectedCategory && d.Visible))
            _raw.Set<object>(d.Key, d.Default);
        Rebuild();
        Footer = $"Category \"{SelectedCategory}\" reset to defaults.";
    }
}

public sealed class SettingGroupViewModel
{
    public SettingGroupViewModel(string title, IReadOnlyList<SettingItemViewModel> items)
    {
        Title = title;
        Items = items;
    }

    public string Title { get; }
    public IReadOnlyList<SettingItemViewModel> Items { get; }
}

/// <summary>One settings row. Only one of the typed values is used, depending on <see cref="Kind"/>.</summary>
public sealed partial class SettingItemViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly Action<string, object?> _onChanged;

    public SettingItemViewModel(SettingDef def, AppSettings settings, Action<string, object?> onChanged)
    {
        Def = def;
        Key = def.Key;
        Label = def.Label;
        Kind = def.Kind;
        ChoiceOptions = def.Choices ?? Array.Empty<string>();
        _settings = settings;
        _onChanged = onChanged;

        StatusText = def.Status switch
        {
            SettingStatus.EngineHook => "Read by engine",
            SettingStatus.Pending => "Not active yet",
            _ => "",
        };
        IsPending = def.Status == SettingStatus.Pending;

        // Load the existing value without triggering a save (direct field, not the property).
        switch (def.Kind)
        {
            case SettingKind.Bool: _boolValue = settings.Bool(def.Key); break;
            case SettingKind.Int: _intText = settings.Int(def.Key).ToString(); break;
            case SettingKind.Text: _textValue = settings.Text(def.Key); break;
            case SettingKind.Choice: _choiceValue = settings.Text(def.Key); break;
        }
    }

    public SettingDef Def { get; }
    public string Key { get; }
    public string Label { get; }
    public SettingKind Kind { get; }
    public string[] ChoiceOptions { get; }
    public string StatusText { get; }
    public bool IsPending { get; }

    [ObservableProperty] private bool _boolValue;
    [ObservableProperty] private string _textValue = "";
    [ObservableProperty] private string _intText = "";
    [ObservableProperty] private string _choiceValue = "";
    [ObservableProperty] private bool _isIntValid = true;

    partial void OnBoolValueChanged(bool value) => Save(value);

    partial void OnTextValueChanged(string value)
    {
        if (Kind == SettingKind.Text) Save(value);
    }

    partial void OnChoiceValueChanged(string value)
    {
        if (Kind == SettingKind.Choice && !string.IsNullOrEmpty(value)) Save(value);
    }

    partial void OnIntTextChanged(string value)
    {
        if (Kind != SettingKind.Int) return;
        if (int.TryParse(value, out int n) && n >= 0)
        {
            IsIntValid = true;
            Save(n);
        }
        else
        {
            IsIntValid = false;
        }
    }

    private void Save<T>(T value)
    {
        _settings.Set(Key, value);
        _onChanged(Key, value);
    }
}
