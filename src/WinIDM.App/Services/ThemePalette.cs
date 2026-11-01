using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinIDM.App.Services;

/// <summary>One theme color slot. The resource name is <c>{Key}Brush</c> in the ResourceDictionary.</summary>
public sealed record ThemeSlot(string Key, string Label, string Group)
{
    public string ResourceKey => Key + "Brush";
}

/// <summary>
/// Custom theme palette. The file format is WinIDM's own JSON (<c>"format": "winidm-theme"</c>); it does not use
/// any third-party theme marketplace schema. Import validates format, version, color values and size; unknown keys
/// are ignored.
/// </summary>
public static class ThemePalette
{
    public const string FormatId = "winidm-theme";
    public const int CurrentVersion = 1;
    public const int MaxJsonBytes = 64 * 1024;
    public const string BaseLight = "Light";
    public const string BaseDark = "Dark";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private static readonly Regex ColorPattern = new(@"^#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$", RegexOptions.CultureInvariant);

    /// <summary>41 named color slots, grouped by UI area.</summary>
    public static readonly IReadOnlyList<ThemeSlot> Slots = new[]
    {
        new ThemeSlot("WindowBackground", "Window background", "Basics"),
        new ThemeSlot("Surface", "Card surface", "Basics"),
        new ThemeSlot("SurfaceAlt", "Alternate surface", "Basics"),
        new ThemeSlot("InputBackground", "Input background", "Basics"),
        new ThemeSlot("Border", "Border", "Basics"),
        new ThemeSlot("TextPrimary", "Primary text", "Basics"),
        new ThemeSlot("TextSecondary", "Secondary text", "Basics"),
        new ThemeSlot("Accent", "Accent", "Basics"),
        new ThemeSlot("AccentText", "Text on accent", "Basics"),
        new ThemeSlot("Danger", "Danger / error", "Basics"),
        new ThemeSlot("Success", "Success", "Basics"),
        new ThemeSlot("ProgressTrack", "Progress track", "Basics"),
        new ThemeSlot("ProgressFill", "Progress fill", "Basics"),
        new ThemeSlot("HeaderBackground", "Header background", "Header and bars"),
        new ThemeSlot("HeaderText", "Header text", "Header and bars"),
        new ThemeSlot("ToolbarBackground", "Toolbar background", "Header and bars"),
        new ThemeSlot("ToolbarText", "Toolbar text", "Header and bars"),
        new ThemeSlot("ButtonBackground", "Button background", "Buttons"),
        new ThemeSlot("ButtonText", "Button text", "Buttons"),
        new ThemeSlot("ButtonBorder", "Button border", "Buttons"),
        new ThemeSlot("ButtonHover", "Button hover", "Buttons"),
        new ThemeSlot("ButtonPressed", "Button pressed", "Buttons"),
        new ThemeSlot("ButtonDisabledText", "Disabled button text", "Buttons"),
        new ThemeSlot("ListBackground", "List background", "Lists and tables"),
        new ThemeSlot("ListAltRow", "Alternate row", "Lists and tables"),
        new ThemeSlot("ListSelection", "Selection background", "Lists and tables"),
        new ThemeSlot("ListSelectionText", "Selection text", "Lists and tables"),
        new ThemeSlot("ListHover", "Row hover", "Lists and tables"),
        new ThemeSlot("GridLine", "Grid line", "Lists and tables"),
        new ThemeSlot("ColumnHeaderBackground", "Column header background", "Columns and tabs"),
        new ThemeSlot("ColumnHeaderText", "Column header text", "Columns and tabs"),
        new ThemeSlot("TabBackground", "Inactive tab background", "Columns and tabs"),
        new ThemeSlot("TabSelectedBackground", "Active tab background", "Columns and tabs"),
        new ThemeSlot("TabSelectedText", "Active tab text", "Columns and tabs"),
        new ThemeSlot("TabText", "Inactive tab text", "Columns and tabs"),
        new ThemeSlot("TooltipBackground", "Tooltip background", "Tooltips, menus, links"),
        new ThemeSlot("TooltipText", "Tooltip text", "Tooltips, menus, links"),
        new ThemeSlot("MenuBackground", "Menu background", "Tooltips, menus, links"),
        new ThemeSlot("MenuText", "Menu text", "Tooltips, menus, links"),
        new ThemeSlot("LinkText", "Link text", "Tooltips, menus, links"),
        new ThemeSlot("Warning", "Warning", "Other"),
    };

    private static readonly IReadOnlyDictionary<string, string> LightColors = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["WindowBackground"] = "#F2F4F8", ["Surface"] = "#FFFFFF", ["SurfaceAlt"] = "#F6F8FB", ["InputBackground"] = "#FFFFFF",
        ["Border"] = "#DCE1E9", ["TextPrimary"] = "#10151C", ["TextSecondary"] = "#5D6776", ["Accent"] = "#2F6FDE",
        ["AccentText"] = "#FFFFFF", ["Danger"] = "#C6383B", ["Success"] = "#2C7A46", ["ProgressTrack"] = "#E4E9F0",
        ["ProgressFill"] = "#2F6FDE", ["HeaderBackground"] = "#FFFFFF", ["HeaderText"] = "#10151C", ["ToolbarBackground"] = "#FFFFFF",
        ["ToolbarText"] = "#10151C", ["ButtonBackground"] = "#FFFFFF", ["ButtonText"] = "#10151C", ["ButtonBorder"] = "#DCE1E9",
        ["ButtonHover"] = "#EDF2FA", ["ButtonPressed"] = "#E0E8F6", ["ButtonDisabledText"] = "#9BA5B2", ["ListBackground"] = "#FFFFFF",
        ["ListAltRow"] = "#F8FAFD", ["ListSelection"] = "#DDE9FB", ["ListSelectionText"] = "#10151C", ["ListHover"] = "#F1F5FB",
        ["GridLine"] = "#E6EAF1", ["ColumnHeaderBackground"] = "#F6F8FB", ["ColumnHeaderText"] = "#5D6776", ["TabBackground"] = "#F6F8FB",
        ["TabSelectedBackground"] = "#FFFFFF", ["TabSelectedText"] = "#10151C", ["TabText"] = "#5D6776", ["TooltipBackground"] = "#10151C",
        ["TooltipText"] = "#FFFFFF", ["MenuBackground"] = "#FFFFFF", ["MenuText"] = "#10151C", ["LinkText"] = "#2F6FDE", ["Warning"] = "#B26A00",
    };

    private static readonly IReadOnlyDictionary<string, string> DarkColors = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["WindowBackground"] = "#14171C", ["Surface"] = "#1C2026", ["SurfaceAlt"] = "#242931", ["InputBackground"] = "#171B21",
        ["Border"] = "#2C333D", ["TextPrimary"] = "#EEF1F5", ["TextSecondary"] = "#9AA4B2", ["Accent"] = "#5B8FF0",
        ["AccentText"] = "#0B1220", ["Danger"] = "#F0655F", ["Success"] = "#5FBF77", ["ProgressTrack"] = "#2C333D",
        ["ProgressFill"] = "#5B8FF0", ["HeaderBackground"] = "#1C2026", ["HeaderText"] = "#EEF1F5", ["ToolbarBackground"] = "#1C2026",
        ["ToolbarText"] = "#EEF1F5", ["ButtonBackground"] = "#242931", ["ButtonText"] = "#EEF1F5", ["ButtonBorder"] = "#2C333D",
        ["ButtonHover"] = "#2C333D", ["ButtonPressed"] = "#343C47", ["ButtonDisabledText"] = "#5A636F", ["ListBackground"] = "#171B21",
        ["ListAltRow"] = "#1C2026", ["ListSelection"] = "#22304A", ["ListSelectionText"] = "#EEF1F5", ["ListHover"] = "#212630",
        ["GridLine"] = "#262D36", ["ColumnHeaderBackground"] = "#1C2026", ["ColumnHeaderText"] = "#9AA4B2", ["TabBackground"] = "#242931",
        ["TabSelectedBackground"] = "#1C2026", ["TabSelectedText"] = "#EEF1F5", ["TabText"] = "#9AA4B2", ["TooltipBackground"] = "#EEF1F5",
        ["TooltipText"] = "#14171C", ["MenuBackground"] = "#1C2026", ["MenuText"] = "#EEF1F5", ["LinkText"] = "#7EA8F5", ["Warning"] = "#FFB74D",
    };

    /// <summary>Default colors for a light or dark base. Complete for all 41 slots.</summary>
    public static IReadOnlyDictionary<string, string> Defaults(bool dark) => dark ? DarkColors : LightColors;

    public static bool IsValidColor(string? value) => value is not null && ColorPattern.IsMatch(value);

    /// <summary>Import result: name, base and all 41 colors.</summary>
    public sealed record Parsed(string Name, bool Dark, IReadOnlyDictionary<string, string> Colors, int IgnoredKeys);

    /// <summary>Export the full palette as JSON (no personal data).</summary>
    public static string Export(string name, bool dark, IReadOnlyDictionary<string, string> colors)
    {
        var ordered = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var slot in Slots)
        {
            ordered[slot.Key] = colors.TryGetValue(slot.Key, out var c) && IsValidColor(c) ? c.ToUpperInvariant() : Defaults(dark)[slot.Key];
        }

        var doc = new ThemeDocument
        {
            Format = FormatId,
            Version = CurrentVersion,
            Name = SanitizeName(name),
            Base = dark ? BaseDark : BaseLight,
            Colors = ordered,
        };
        return JsonSerializer.Serialize(doc, JsonOptions);
    }

    /// <summary>Validate and read a palette. Throws <see cref="InvalidDataException"/> with a user-facing message when invalid.</summary>
    public static Parsed Import(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > MaxJsonBytes) throw new InvalidDataException("Theme file is too large (64 KB maximum).");

        ThemeDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize<ThemeDocument>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The theme file is not valid JSON.", ex);
        }

        if (doc is null || doc.Format != FormatId) throw new InvalidDataException("Not a WinIDM theme file (unrecognized format).");
        if (doc.Version != CurrentVersion) throw new InvalidDataException($"Theme version {doc.Version} is not supported.");

        // Theme files written before the UI language change used the Malay base names; keep accepting them.
        var baseName = NormalizeBase(doc.Base);
        if (baseName is not (BaseLight or BaseDark)) throw new InvalidDataException("Theme base must be Light or Dark.");

        bool dark = baseName == BaseDark;
        var colors = new Dictionary<string, string>(Defaults(dark), StringComparer.Ordinal);
        int ignored = 0;
        var known = Slots.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var kv in doc.Colors ?? new Dictionary<string, string>())
        {
            if (!known.Contains(kv.Key))
            {
                ignored++;
                continue;
            }

            if (!IsValidColor(kv.Value)) throw new InvalidDataException($"Invalid color value for '{kv.Key}'. Use #RRGGBB or #AARRGGBB.");
            colors[kv.Key] = kv.Value.ToUpperInvariant();
        }

        return new Parsed(SanitizeName(doc.Name), dark, colors, ignored);
    }

    /// <summary>Palette to apply: custom JSON when valid, otherwise the light or dark base default.</summary>
    public static IReadOnlyDictionary<string, string> Resolve(string? json, bool dark)
    {
        if (string.IsNullOrWhiteSpace(json)) return Defaults(dark);
        try
        {
            return Import(json).Colors;
        }
        catch (InvalidDataException)
        {
            return Defaults(dark);
        }
    }

    /// <summary>Base (dark or light) of a custom JSON; light when missing or invalid.</summary>
    public static bool IsDarkBase(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            return Import(json).Dark;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>Map legacy Malay base names onto the current English ones.</summary>
    public static string NormalizeBase(string? value) => value switch
    {
        "Light" or "Terang" => BaseLight,
        "Dark" or "Gelap" => BaseDark,
        _ => value ?? "",
    };

    private static string SanitizeName(string? name)
    {
        var clean = new string((name ?? "").Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        if (clean.Length == 0) clean = "Custom theme";
        return clean.Length > 60 ? clean[..60] : clean;
    }

    private sealed class ThemeDocument
    {
        public string Format { get; set; } = "";
        public int Version { get; set; }
        public string Name { get; set; } = "";
        public string Base { get; set; } = "";
        public Dictionary<string, string>? Colors { get; set; }
    }
}
