using System.IO;
using System.Text.Json;

namespace WinIDM.App.Services;

/// <summary>Hosts source imported by the user. The actual text is stored in a separate file.</summary>
public sealed class BrowserHostSourceRecord
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string? Url { get; set; }
}

/// <summary>Browser rule data: site whitelist, manual element filters, and hosts sources.</summary>
public sealed class BrowserRuleData
{
    public List<string> SiteAdblockOff { get; set; } = new();
    public List<string> HostWhitelist { get; set; } = new();
    public Dictionary<string, List<string>> ManualCosmetic { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<BrowserHostSourceRecord> HostSources { get; set; } = new();
}

/// <summary>
/// Rule store for the blocker. Written as JSON with errors caught; a corrupt file means an empty set
/// (it never brings the browser down).
/// </summary>
public sealed class BrowserRuleStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public BrowserRuleStore(string path)
    {
        _path = path;
        Data = Load();
    }

    public BrowserRuleData Data { get; }

    public bool IsSiteAdblockOff(string host)
    {
        lock (_gate) return Data.SiteAdblockOff.Contains(host, StringComparer.OrdinalIgnoreCase);
    }

    public void SetSiteAdblockOff(string host, bool off)
    {
        lock (_gate)
        {
            Data.SiteAdblockOff.RemoveAll(h => string.Equals(h, host, StringComparison.OrdinalIgnoreCase));
            if (off) Data.SiteAdblockOff.Add(host);
            Save();
        }
    }

    public IReadOnlyList<string> ManualSelectors(string host)
    {
        lock (_gate)
            return Data.ManualCosmetic.TryGetValue(host, out var list) ? list.ToList() : new List<string>();
    }

    /// <summary>Add an element rule for a host. Returns false when the selector already exists.</summary>
    public bool AddManualSelector(string host, string selector)
    {
        lock (_gate)
        {
            if (!Data.ManualCosmetic.TryGetValue(host, out var list))
                Data.ManualCosmetic[host] = list = new List<string>();
            if (list.Contains(selector, StringComparer.Ordinal)) return false;
            list.Add(selector);
            Save();
            return true;
        }
    }

    public void RemoveManualSelector(string host, string selector)
    {
        lock (_gate)
        {
            if (Data.ManualCosmetic.TryGetValue(host, out var list) && list.Remove(selector)) Save();
        }
    }

    public void Persist()
    {
        lock (_gate) Save();
    }

    private BrowserRuleData Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<BrowserRuleData>(File.ReadAllText(_path)) ?? new BrowserRuleData();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            AppLog.Error("Read browser rules", ex);
        }
        return new BrowserRuleData();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Data, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Save browser rules", ex);
        }
    }
}

/// <summary>A single home page shortcut.</summary>
public sealed record BrowserShortcut(string Title, string Url);

/// <summary>Home page shortcuts: up to <see cref="Max"/> items, reorderable and editable.</summary>
public sealed class BrowserShortcutStore
{
    public const int Max = 40;

    private readonly string _path;
    private readonly object _gate = new();
    private List<BrowserShortcut> _items;

    public BrowserShortcutStore(string path)
    {
        _path = path;
        _items = Load();
    }

    public IReadOnlyList<BrowserShortcut> All
    {
        get { lock (_gate) return _items.ToList(); }
    }

    /// <summary>Add a shortcut. Returns false when the limit is reached or the URL is invalid.</summary>
    public bool Add(string title, string url)
    {
        lock (_gate)
        {
            if (_items.Count >= Max || !UrlHelper.IsHttp(url)) return false;
            _items.Add(new BrowserShortcut(Clean(title, url), url));
            Save();
            return true;
        }
    }

    public bool Update(int index, string title, string url)
    {
        lock (_gate)
        {
            if (index < 0 || index >= _items.Count || !UrlHelper.IsHttp(url)) return false;
            _items[index] = new BrowserShortcut(Clean(title, url), url);
            Save();
            return true;
        }
    }

    public void Remove(int index)
    {
        lock (_gate)
        {
            if (index < 0 || index >= _items.Count) return;
            _items.RemoveAt(index);
            Save();
        }
    }

    public void Move(int index, int delta)
    {
        lock (_gate)
        {
            int target = index + delta;
            if (index < 0 || index >= _items.Count || target < 0 || target >= _items.Count) return;
            (_items[index], _items[target]) = (_items[target], _items[index]);
            Save();
        }
    }

    private static string Clean(string title, string url)
    {
        var t = (title ?? "").Trim();
        if (t.Length == 0) return url;
        return t.Length > 120 ? t[..120] : t;
    }

    private List<BrowserShortcut> Load()
    {
        try
        {
            if (File.Exists(_path))
                return (JsonSerializer.Deserialize<List<BrowserShortcut>>(File.ReadAllText(_path)) ?? new())
                    .Where(s => UrlHelper.IsHttp(s.Url)).Take(Max).ToList();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            AppLog.Error("Read shortcuts", ex);
        }
        return new();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_items));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Save shortcuts", ex);
        }
    }
}
