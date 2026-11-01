using System.IO;
using System.Text;
using System.Text.Json;
using WinIDM.Backup;
using WinIDM.Browser;
using WinIDM.Core.Contracts;
using WinIDM.Engine;

namespace WinIDM.App.Services;

/// <summary>
/// Extra backup data sources: bookmarks, history, tabs, passwords (encrypted with the backup password),
/// ad blocker data, user scripts, site settings (downloads and browser permissions).
/// No secret keys or cookies are exported. A restored user script takes effect after the app restarts.
/// </summary>
public sealed class BackupDataSource : IBackupDataSource
{
    private const string SavedTabsKey = "browser.saved_tabs";
    private const int MaxTabsJsonBytes = 1024 * 1024;

    private readonly BookmarkStore _bookmarks;
    private readonly HistoryStore _history;
    private readonly ISettingsStore _settings;
    private readonly PasswordVault _vault;
    private readonly AdblockConfigStore _adblock;
    private readonly string _userScriptsPath;
    private readonly SitePermissionStore _sitePermissions;
    private readonly SqliteDownloadRepository _sites;

    public BackupDataSource(
        BookmarkStore bookmarks,
        HistoryStore history,
        ISettingsStore settings,
        PasswordVault vault,
        AdblockConfigStore adblock,
        string userScriptsPath,
        SitePermissionStore sitePermissions,
        SqliteDownloadRepository sites)
    {
        _bookmarks = bookmarks;
        _history = history;
        _settings = settings;
        _vault = vault;
        _adblock = adblock;
        _userScriptsPath = userScriptsPath;
        _sitePermissions = sitePermissions;
        _sites = sites;
    }

    public async Task<string?> ExportAsync(string name, CancellationToken ct = default)
    {
        switch (name)
        {
            case BackupDataNames.Bookmarks:
                return JsonSerializer.Serialize(_bookmarks.All);
            case BackupDataNames.History:
                return JsonSerializer.Serialize(_history.All);
            case BackupDataNames.Tabs:
                return TabsJson();
            case BackupDataNames.Passwords:
                return await ExportPasswordsAsync(ct);
            case BackupDataNames.Adblock:
                return _adblock.ExportJson();
            case BackupDataNames.UserScripts:
                return ReadUserScripts();
            case BackupDataNames.SiteSettings:
                return await ExportSiteSettingsAsync(ct);
            default:
                return null;
        }
    }

    public async Task ImportAsync(string name, string json, RestoreMode mode, CancellationToken ct = default)
    {
        bool overwrite = mode == RestoreMode.Overwrite;
        switch (name)
        {
            case BackupDataNames.Bookmarks:
                _bookmarks.Restore(JsonSerializer.Deserialize<List<Bookmark>>(json) ?? new List<Bookmark>(), overwrite);
                break;
            case BackupDataNames.History:
                _history.Restore(JsonSerializer.Deserialize<List<HistoryEntry>>(json) ?? new List<HistoryEntry>(), overwrite);
                break;
            case BackupDataNames.Tabs:
                ImportTabs(json);
                break;
            case BackupDataNames.Passwords:
                await ImportPasswordsAsync(json, ct);
                break;
            case BackupDataNames.Adblock:
                _adblock.ImportJson(json, overwrite);
                break;
            case BackupDataNames.UserScripts:
                WriteUserScripts(json);
                break;
            case BackupDataNames.SiteSettings:
                await ImportSiteSettingsAsync(json, overwrite, ct);
                break;
        }
    }

    private string TabsJson()
    {
        var raw = _settings.Get(SavedTabsKey, "[]");
        if (string.IsNullOrWhiteSpace(raw)) return "[]";
        return raw;
    }

    private void ImportTabs(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaxTabsJsonBytes) throw new InvalidDataException("Tab list is too large.");
        var urls = SavedTabs.Parse(json);
        _settings.Set(SavedTabsKey, SavedTabs.Serialize(urls));
    }

    // ---- Passwords: the export is encrypted by PasswordVault with the backup password ----

    private async Task<string?> ExportPasswordsAsync(CancellationToken ct)
    {
        if (!_vault.IsInitialized) return null;
        if (!_vault.IsUnlocked) throw new InvalidOperationException("Unlock the password vault before backing up passwords.");
        var passphrase = BackupPasswordContext.Current
                         ?? throw new InvalidOperationException("A backup password is required for the Passwords category.");
        return await _vault.ExportEncryptedAsync(passphrase, ct);
    }

    private async Task ImportPasswordsAsync(string json, CancellationToken ct)
    {
        var passphrase = BackupPasswordContext.Current
                         ?? throw new InvalidOperationException("A backup password is required to restore passwords.");
        if (!_vault.IsInitialized || !_vault.IsUnlocked)
            throw new InvalidOperationException("Set up and unlock the password vault before restoring passwords.");
        await _vault.ImportEncryptedAsync(json, passphrase, ct);
    }

    // ---- User scripts: JSON file owned by the Browser module ----

    private string? ReadUserScripts()
    {
        if (!File.Exists(_userScriptsPath)) return null;
        var text = File.ReadAllText(_userScriptsPath, Encoding.UTF8);
        JsonDocument.Parse(text).Dispose();
        return text;
    }

    private void WriteUserScripts(string json)
    {
        JsonDocument.Parse(json).Dispose();
        var dir = Path.GetDirectoryName(_userScriptsPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = _userScriptsPath + ".tmp";
        File.WriteAllText(tmp, json, Encoding.UTF8);
        File.Move(tmp, _userScriptsPath, overwrite: true);
    }

    // ---- Site settings: per-domain download settings (no cookies) and browser permissions ----

    public sealed record SiteDto(string Domain, string? UserAgent, string? SaveDirectory, bool UseForSubdomain);

    public sealed record SiteBundle(List<SiteDto> DownloadSites, List<SitePermission> BrowserPermissions);

    private async Task<string> ExportSiteSettingsAsync(CancellationToken ct)
    {
        var sites = await _sites.ListSiteSettingsAsync(ct);
        var bundle = new SiteBundle(
            sites.Select(s => new SiteDto(s.Domain, s.UserAgent, s.SaveDirectory, s.UseForSubdomain)).ToList(),
            _sitePermissions.All.ToList());
        return JsonSerializer.Serialize(bundle);
    }

    private async Task ImportSiteSettingsAsync(string json, bool overwrite, CancellationToken ct)
    {
        var bundle = JsonSerializer.Deserialize<SiteBundle>(json) ?? new SiteBundle(new(), new());
        foreach (var dto in bundle.DownloadSites)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(dto.Domain)) continue;
            // Cookies are never part of a backup; keep the existing cookies for the same domain.
            var existing = await _sites.GetSiteSettingsAsync(dto.Domain, ct);
            await _sites.SetSiteSettingsAsync(
                new SiteSettings(dto.Domain, dto.UserAgent, existing?.Cookies, dto.SaveDirectory, dto.UseForSubdomain), ct);
        }

        if (overwrite)
        {
            var keep = bundle.BrowserPermissions.Select(p => (p.Origin, p.Kind)).ToHashSet();
            foreach (var existing in _sitePermissions.All.ToList())
            {
                if (!keep.Contains((existing.Origin, existing.Kind))) _sitePermissions.Remove(existing.Origin, existing.Kind);
            }
        }

        foreach (var p in bundle.BrowserPermissions) _sitePermissions.Set(p.Origin, p.Kind, p.Allow);
    }
}
