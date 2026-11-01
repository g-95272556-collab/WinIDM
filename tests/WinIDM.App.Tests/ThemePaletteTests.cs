using System.IO;
using WinIDM.App.Services;

namespace WinIDM.App.Tests;

public class ThemePaletteTests
{
    [Fact]
    public void Palette_HasFortyOneUniqueNamedSlots()
    {
        Assert.Equal(41, ThemePalette.Slots.Count);
        Assert.Equal(41, ThemePalette.Slots.Select(s => s.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.All(ThemePalette.Slots, s => Assert.False(string.IsNullOrWhiteSpace(s.Label)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Defaults_CoverEverySlotWithValidColor(bool dark)
    {
        var defaults = ThemePalette.Defaults(dark);
        foreach (var slot in ThemePalette.Slots)
        {
            Assert.True(defaults.ContainsKey(slot.Key), slot.Key);
            Assert.True(ThemePalette.IsValidColor(defaults[slot.Key]), slot.Key);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Export_ThenImport_RoundTripsAllColors(bool dark)
    {
        var colors = ThemePalette.Defaults(dark).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        colors["Accent"] = "#AA11CC";
        var json = ThemePalette.Export("Theme test", dark, colors);

        var parsed = ThemePalette.Import(json);

        Assert.Equal("Theme test", parsed.Name);
        Assert.Equal(dark, parsed.Dark);
        Assert.Equal(0, parsed.IgnoredKeys);
        Assert.Equal("#AA11CC", parsed.Colors["Accent"]);
        Assert.Equal(41, parsed.Colors.Count);
    }

    [Fact]
    public void Export_UsesOwnFormatIdentifierNotIdmTheme()
    {
        var json = ThemePalette.Export("x", false, ThemePalette.Defaults(false));
        Assert.Contains("\"format\": \"winidm-theme\"", json);
        Assert.DoesNotContain("idmtheme", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Import_RejectsForeignOrBrokenDocuments()
    {
        Assert.Throws<InvalidDataException>(() => ThemePalette.Import("{\"format\":\"idmtheme\",\"version\":1,\"base\":\"Light\"}"));
        Assert.Throws<InvalidDataException>(() => ThemePalette.Import("not json"));
        Assert.Throws<InvalidDataException>(() => ThemePalette.Import("{\"format\":\"winidm-theme\",\"version\":99,\"base\":\"Light\"}"));
        Assert.Throws<InvalidDataException>(() => ThemePalette.Import("{\"format\":\"winidm-theme\",\"version\":1,\"base\":\"Blue\"}"));
    }

    [Fact]
    public void Import_RejectsInvalidColorValue()
    {
        const string json = "{\"format\":\"winidm-theme\",\"version\":1,\"base\":\"Light\",\"colors\":{\"Accent\":\"red\"}}";
        Assert.Throws<InvalidDataException>(() => ThemePalette.Import(json));
    }

    [Fact]
    public void Import_IgnoresUnknownKeysAndFillsMissingFromBaseDefaults()
    {
        const string json = "{\"format\":\"winidm-theme\",\"version\":1,\"base\":\"Dark\",\"colors\":{\"Accent\":\"#112233\",\"DoesNotExist\":\"#000000\"}}";
        var parsed = ThemePalette.Import(json);

        Assert.True(parsed.Dark);
        Assert.Equal(1, parsed.IgnoredKeys);
        Assert.Equal("#112233", parsed.Colors["Accent"]);
        Assert.Equal(ThemePalette.Defaults(true)["WindowBackground"], parsed.Colors["WindowBackground"]);
    }

    [Fact]
    public void Import_RejectsOversizedDocument()
    {
        var padded = "{\"format\":\"winidm-theme\",\"version\":1,\"base\":\"Light\",\"name\":\"" + new string('a', ThemePalette.MaxJsonBytes) + "\"}";
        Assert.Throws<InvalidDataException>(() => ThemePalette.Import(padded));
    }

    [Fact]
    public void Resolve_FallsBackToDefaultsForBadJson()
    {
        var colors = ThemePalette.Resolve("corrupt", dark: true);
        Assert.Equal(ThemePalette.Defaults(true)["Accent"], colors["Accent"]);
    }

    [Fact]
    public void Name_IsSanitisedOfControlCharacters()
    {
        var json = ThemePalette.Export("Secret\u0001 theme\n", false, ThemePalette.Defaults(false));
        Assert.Equal("Secret theme", ThemePalette.Import(json).Name);
    }

    [Fact]
    public void LegacyMalayBaseAndChoiceValues_AreStillAccepted()
    {
        var parsed = ThemePalette.Import("{\"format\":\"winidm-theme\",\"version\":1,\"base\":\"Gelap\"}");
        Assert.True(parsed.Dark);

        Assert.Equal(ThemeService.ChoiceLight, ThemeService.NormalizeChoice("Terang"));
        Assert.Equal(ThemeService.ChoiceDark, ThemeService.NormalizeChoice("Gelap"));
        Assert.Equal(ThemeService.ChoiceSystem, ThemeService.NormalizeChoice("Sistem"));
        Assert.Equal(ThemeService.ChoiceCustom, ThemeService.NormalizeChoice("Tersuai"));
    }
}
