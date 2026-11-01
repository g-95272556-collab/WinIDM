using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinIDM.Core.Contracts;

namespace WinIDM.App.Services;

/// <summary>User-owned ad blocker data: host whitelist and manual filter rules.</summary>
public sealed class AdblockConfig
{
    public string Format { get; set; } = AdblockConfigStore.FormatId;
    public int Version { get; set; } = AdblockConfigStore.CurrentVersion;
    public List<string> Whitelist { get; set; } = new();
    public string CustomRules { get; set; } = "";
}

/// <summary>
/// Atomic JSON store for ad blocker data (adblock.json). No list downloads happen here;
/// external lists are only fetched after the user enables them in the Browser module.
/// </summary>
public sealed class AdblockConfigStore
{
    public const string FormatId = "winidm-adblock";
    public const int CurrentVersion = 1;
    public const int MaxBytes = 2 * 1024 * 1024;
    public const int MaxWhitelist = 10_000;

    private static readonly Regex HostPattern = new(@"^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)*$", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _gate = new();

    public AdblockConfigStore(string path) => _path = path;

    public AdblockConfig Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return new AdblockConfig();
                var cfg = JsonSerializer.Deserialize<AdblockConfig>(File.ReadAllText(_path));
                return cfg is null || cfg.Format != FormatId ? new AdblockConfig() : Normalize(cfg);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                AppLog.Error("Read adblock.json", ex);
                return new AdblockConfig();
            }
        }
    }

    public void Save(AdblockConfig config)
    {
        var cfg = Normalize(config);
        var json = JsonSerializer.Serialize(cfg, Options);
        lock (_gate)
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json, Encoding.UTF8);
            File.Move(tmp, _path, overwrite: true);
        }
    }

    /// <summary>Export JSON for backup (this data holds no secret keys).</summary>
    public string ExportJson() => JsonSerializer.Serialize(Load(), Options);

    /// <summary>Validate and save a backup payload. Throws <see cref="InvalidDataException"/> when invalid.</summary>
    public void ImportJson(string json, bool overwrite)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaxBytes) throw new InvalidDataException("Ad blocker data is too large.");
        var incoming = JsonSerializer.Deserialize<AdblockConfig>(json)
                       ?? throw new InvalidDataException("Ad blocker data is empty.");
        if (incoming.Format != FormatId || incoming.Version != CurrentVersion)
            throw new InvalidDataException("Unrecognized ad blocker data format.");
        incoming = Normalize(incoming);
        if (incoming.Whitelist.Count > MaxWhitelist) throw new InvalidDataException("Whitelist is too long.");

        var merged = overwrite ? incoming : MergeWith(Load(), incoming);
        Save(merged);
    }

    /// <summary>Apply stored data to the ad blocker (no download).</summary>
    public void ApplyTo(IAdBlocker blocker)
    {
        ArgumentNullException.ThrowIfNull(blocker);
        var cfg = Load();
        foreach (var host in cfg.Whitelist) blocker.AddWhitelistHost(host);
        if (!string.IsNullOrWhiteSpace(cfg.CustomRules)) blocker.LoadFilterList("winidm-custom", cfg.CustomRules);
    }

    private static AdblockConfig MergeWith(AdblockConfig current, AdblockConfig incoming)
    {
        var whitelist = current.Whitelist.Concat(incoming.Whitelist).Distinct(StringComparer.Ordinal).ToList();
        var rules = string.IsNullOrWhiteSpace(current.CustomRules)
            ? incoming.CustomRules
            : string.IsNullOrWhiteSpace(incoming.CustomRules)
                ? current.CustomRules
                : current.CustomRules.TrimEnd() + Environment.NewLine + incoming.CustomRules;
        return new AdblockConfig { Whitelist = whitelist, CustomRules = rules };
    }

    private static AdblockConfig Normalize(AdblockConfig cfg)
    {
        var hosts = (cfg.Whitelist ?? new List<string>())
            .Select(h => (h ?? "").Trim().ToLowerInvariant())
            .Where(h => HostPattern.IsMatch(h) && h.Length <= 253)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(h => h, StringComparer.Ordinal)
            .ToList();
        var rules = cfg.CustomRules ?? "";
        if (rules.Length > MaxBytes) rules = rules[..MaxBytes];
        return new AdblockConfig { Format = FormatId, Version = CurrentVersion, Whitelist = hosts, CustomRules = rules };
    }
}
