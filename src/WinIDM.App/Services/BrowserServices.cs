using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using WinIDM.Browser;

namespace WinIDM.App.Services;

/// <summary>
/// Browser state shared across windows: blocker engine, hosts sources, rules, shortcuts, and preferences.
/// Reached through <see cref="For"/> so each <see cref="Composition"/> has exactly one instance.
/// The blocker engine is rebuilt when lists change because <see cref="AdBlocker"/> cannot be cleared.
/// </summary>
public sealed class BrowserServices : IDisposable
{
    public const long MaxHostsBytes = 16 * 1024 * 1024;

    private static readonly ConditionalWeakTable<Composition, BrowserServices> Instances = new();
    private readonly object _gate = new();
    private readonly HttpClient _http;
    private AdBlocker _engine;
    private readonly string _dataDir;

    private BrowserServices(string dataDir)
    {
        _dataDir = dataDir;
        Root = Path.Combine(dataDir, "browser");
        Prefs = new BrowserPrefs(Path.Combine(Root, "prefs.json"));
        Rules = new BrowserRuleStore(Path.Combine(Root, "rules.json"));
        Shortcuts = new BrowserShortcutStore(Path.Combine(Root, "shortcuts.json"));
        Filters = new FilterListManager(Path.Combine(Root, "filters"));
        Hosts = new HostsBlocklist();
        PageTools = new WinIDM.Browser.PageTools();
        _http = new HttpClient(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(60),
        };
        _engine = new AdBlocker();
        LoadHostSources();
        RebuildEngine();
    }

    /// <summary>Get the browser services for this composition (one instance per composition).</summary>
    public static BrowserServices For(Composition composition) =>
        Instances.GetValue(composition, c => new BrowserServices(c.DataDir));

    public string Root { get; }
    public BrowserPrefs Prefs { get; }
    public BrowserRuleStore Rules { get; }
    public BrowserShortcutStore Shortcuts { get; }
    public FilterListManager Filters { get; }
    public HostsBlocklist Hosts { get; }
    public PageTools PageTools { get; }

    /// <summary>Current blocker engine. Rebuilt after the filter lists or the whitelist change.</summary>
    public AdBlocker Engine
    {
        get { lock (_gate) return _engine; }
    }

    private string HostsDir => Path.Combine(Root, "hosts");

    /// <summary>Rebuild the blocker engine from the enabled filter lists and the whitelist.</summary>
    public void RebuildEngine()
    {
        var engine = new AdBlocker();
        Filters.ApplyTo(engine);
        foreach (var host in Rules.Data.HostWhitelist) engine.AddWhitelistHost(host);
        // Custom rules and the whitelist in adblock.json must also take effect in the browser
        // (previously they were only loaded into a different engine that the browser never used).
        new AdblockConfigStore(Path.Combine(_dataDir, "adblock.json")).ApplyTo(engine);
        lock (_gate) _engine = engine;
    }

    /// <summary>Download a hosts source only on user action. HTTPS only, 16 MB limit.</summary>
    public async Task<int> AddHostSourceFromUrlAsync(string name, string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Host source must be an HTTPS URL.");
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A source name is required.");

        using var resp = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength is { } len && len > MaxHostsBytes)
            throw new InvalidDataException("Host source is too large (16 MB limit).");

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (ms.Length + read > MaxHostsBytes) throw new InvalidDataException("Host source is too large (16 MB limit).");
            ms.Write(buffer, 0, read);
        }
        string text = Encoding.UTF8.GetString(ms.ToArray());

        var record = new BrowserHostSourceRecord { Name = name.Trim(), Enabled = true, Url = url };
        SaveHostSourceText(record.Name, text);
        Rules.Data.HostSources.RemoveAll(s => string.Equals(s.Name, record.Name, StringComparison.OrdinalIgnoreCase));
        Rules.Data.HostSources.Add(record);
        Rules.Persist();
        int count = Hosts.AddOrReplaceSource(record.Name, text, enabled: true);
        return count;
    }

    public void SetHostSourceEnabled(string name, bool enabled)
    {
        var rec = Rules.Data.HostSources.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        if (rec is null) return;
        rec.Enabled = enabled;
        Hosts.SetEnabled(rec.Name, enabled);
        Rules.Persist();
    }

    public void RemoveHostSource(string name)
    {
        Rules.Data.HostSources.RemoveAll(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        Hosts.RemoveSource(name);
        try
        {
            var path = HostSourcePath(name);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException ex) { AppLog.Error("Delete host source", ex); }
        Rules.Persist();
    }

    public void AddHostWhitelist(string domain)
    {
        var d = domain.Trim().ToLowerInvariant();
        if (d.Length == 0) return;
        if (!Rules.Data.HostWhitelist.Contains(d, StringComparer.Ordinal)) Rules.Data.HostWhitelist.Add(d);
        Hosts.AddWhitelist(d);
        Rules.Persist();
        RebuildEngine();
    }

    public void RemoveHostWhitelist(string domain)
    {
        Rules.Data.HostWhitelist.RemoveAll(h => string.Equals(h, domain, StringComparison.Ordinal));
        Hosts.RemoveWhitelist(domain);
        Rules.Persist();
        RebuildEngine();
    }

    private void LoadHostSources()
    {
        foreach (var rec in Rules.Data.HostSources.ToList())
        {
            try
            {
                var path = HostSourcePath(rec.Name);
                if (!File.Exists(path)) continue;
                Hosts.AddOrReplaceSource(rec.Name, File.ReadAllText(path, Encoding.UTF8), rec.Enabled);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Error("Load host source", ex);
            }
        }
        foreach (var host in Rules.Data.HostWhitelist) Hosts.AddWhitelist(host);
    }

    private void SaveHostSourceText(string name, string text)
    {
        Directory.CreateDirectory(HostsDir);
        File.WriteAllText(HostSourcePath(name), text, new UTF8Encoding(false));
    }

    private string HostSourcePath(string name)
    {
        var safe = new string(name.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_').ToArray());
        return Path.Combine(HostsDir, safe + ".txt");
    }

    public void Dispose()
    {
        _http.Dispose();
        PageTools.Dispose();
        Filters.Dispose();
    }
}
