using System.IO;
using WinIDM.App.Services;
using System.Net.Http;
using WinIDM.Backup;
using WinIDM.Browser;
using WinIDM.Core.Contracts;
using WinIDM.Engine;
using WinIDM.Torrent;

namespace WinIDM.App;

/// <summary>
/// COMPOSITION ROOT: all concrete class wiring lives in this file only.
/// Do not call <c>new</c> on service classes anywhere else.
/// </summary>
public sealed class Composition
{
    private Composition(string dataDir) => DataDir = dataDir;

    /// <summary>User data folder (%LOCALAPPDATA%\WinIDM by default).</summary>
    public string DataDir { get; }
    public string LogDir => Path.Combine(DataDir, "logs");
    public string WebViewDataDir => Path.Combine(DataDir, "WebView2");
    public string BookmarksPath => Path.Combine(DataDir, "bookmarks.json");
    public string HistoryPath => Path.Combine(DataDir, "history.json");
    public string UserScriptsPath => Path.Combine(DataDir, "userscripts.json");
    public string ExtensionsDir => Path.Combine(DataDir, "Extensions");

    private WinIDM.Browser.ExtensionStore? _extensions;

    /// <summary>Browser extensions installed from the Chrome Web Store (created on first use).</summary>
    public WinIDM.Browser.ExtensionStore Extensions => _extensions ??= new(ExtensionsDir, Http);

    public AppSettings Settings { get; private set; } = null!;
    public ISettingsStore RawSettings { get; private set; } = null!;
    public IDownloadRepository Repository { get; private set; } = null!;
    public IDownloadEngine Engine { get; private set; } = null!;
    /// <summary>Concrete engine class (DownloadCompleted, DownloadFailed, AfterDownloadsFinished events).</summary>
    public DownloadEngine EngineImpl { get; private set; } = null!;
    /// <summary>The network policy the engine enforces (Wi-Fi only, metered); the UI reads it only to show status.</summary>
    public INetworkPolicy Network { get; private set; } = null!;
    /// <summary>Concrete class (extra torrent API: files, trackers, limits, recheck). It also fulfills <see cref="ITorrentService"/>.</summary>
    public TorrentService Torrent { get; private set; } = null!;
    /// <summary>Backup and restore (with password context and minimum password rules).</summary>
    public AppBackupService Backup { get; private set; } = null!;
    public BackupDestinations Destinations { get; private set; } = null!;
    /// <summary>Backup scheduler wired to settings.</summary>
    public BackupAutomation BackupAutomation { get; private set; } = null!;
    public DpapiTokenStore Tokens { get; private set; } = null!;
    public IAdBlocker AdBlocker { get; private set; } = null!;
    public AdblockConfigStore AdblockConfig { get; private set; } = null!;
    public IMediaDetector Media { get; private set; } = null!;
    public IUserScriptHost UserScripts { get; private set; } = null!;
    public PasswordVault Vault { get; private set; } = null!;

    /// <summary>Bookmarks and history are shared between the browser and backup (one instance, one cache).</summary>
    public BookmarkStore Bookmarks { get; private set; } = null!;
    public HistoryStore History { get; private set; } = null!;

    /// <summary>Remembered per-site permissions (camera, microphone, location, notifications).</summary>
    public SitePermissionStore SitePermissions { get; private set; } = null!;

    /// <summary>List of pages saved by the browser (MHTML/PDF).</summary>
    public SavedPageStore SavedPages { get; private set; } = null!;

    /// <summary>Shared HTTP client for cloud backup and update checks (only when the user asks).</summary>
    public HttpClient Http { get; } = new() { Timeout = TimeSpan.FromMinutes(5) };

    public static string DefaultDataDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinIDM");

    public static Composition Create(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        var c = new Composition(dataDir);

        ISettingsStore raw = new JsonSettingsStore(Path.Combine(dataDir, "settings.json"));
        var sqlite = new SqliteDownloadRepository(Path.Combine(dataDir, "winidm.db"));
        IDownloadRepository repo = sqlite;
        // DNS resolver (system / DoH / DoT) for download connections. Re-read from settings.
        var dnsResolver = new DnsHostResolver(raw);
        var network = new NetworkPolicyAdapter();
        var engineImpl = new DownloadEngine(repo, raw, hostResolver: dnsResolver.ResolveAsync, network: network)
        {
            PowerStatus = PowerState.ReadBattery,
        };
        IDownloadEngine engine = engineImpl;
        var torrent = new TorrentService(raw, dataDir);

        var bookmarks = new BookmarkStore(Path.Combine(dataDir, "bookmarks.json"));
        var history = new HistoryStore(Path.Combine(dataDir, "history.json"));
        var vault = new PasswordVault(Path.Combine(dataDir, "vault.bin"));
        var sitePermissions = new SitePermissionStore(Path.Combine(dataDir, "site_permissions.json"));
        var adblockConfig = new AdblockConfigStore(Path.Combine(dataDir, "adblock.json"));

        // Backup: settings without secret keys; extra data sources receive the password through a context.
        var backupCore = new BackupService(
            repo,
            new SecretFilteredSettings(raw),
            new BackupDataSource(bookmarks, history, raw, vault, adblockConfig, c.UserScriptsPath, sitePermissions, sqlite),
            dataDir);
        var backup = new AppBackupService(backupCore);
        var tokens = new DpapiTokenStore(Path.Combine(dataDir, "oauth"));
        var destinations = new BackupDestinations(new AppSettings(raw), tokens, c.Http);

        IAdBlocker adBlocker = new AdBlocker();
        adblockConfig.ApplyTo(adBlocker);
        IMediaDetector media = new MediaDetector();
        IUserScriptHost userScripts = new UserScriptHost(c.UserScriptsPath);

        c.RawSettings = raw;
        c.Settings = new AppSettings(raw);
        c.Repository = repo;
        c.Engine = engine;
        c.EngineImpl = engineImpl;
        c.Network = network;
        c.Torrent = torrent;
        c.Backup = backup;
        c.Destinations = destinations;
        c.Tokens = tokens;
        c.AdBlocker = adBlocker;
        c.AdblockConfig = adblockConfig;
        c.Media = media;
        c.UserScripts = userScripts;
        c.Vault = vault;
        c.Bookmarks = bookmarks;
        c.History = history;
        c.SitePermissions = sitePermissions;
        c.SavedPages = new SavedPageStore(Path.Combine(dataDir, "saved_pages.json"));
        c.BackupAutomation = new BackupAutomation(c.Settings, destinations, backup, Path.Combine(dataDir, "backup-staging"));
        return c;
    }

    /// <summary>Default download folder: the user setting if present, otherwise the Windows Downloads folder.</summary>
    public string DefaultDownloadFolder()
    {
        var custom = Settings.Text(Keys.DefaultFolder);
        if (!string.IsNullOrWhiteSpace(custom) && Directory.Exists(custom)) return custom;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }
}
