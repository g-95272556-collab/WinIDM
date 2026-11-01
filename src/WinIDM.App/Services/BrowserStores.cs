namespace WinIDM.App.Services;

/// <summary>Stored permission decision for one site (kind is the <c>CoreWebView2PermissionKind</c> name).</summary>
public sealed record SitePermission(string Origin, string Kind, bool Allow);

/// <summary>Per-site permissions (camera, microphone, location, notifications) remembered on disk.</summary>
public sealed class SitePermissionStore
{
    private readonly JsonFileStore<SitePermission> _store;
    private List<SitePermission> _items;

    public SitePermissionStore(string path)
    {
        _store = new JsonFileStore<SitePermission>(path);
        _items = _store.Load();
    }

    public IReadOnlyList<SitePermission> All => _items;

    /// <summary>Returns the stored decision, or null if the user has not chosen yet.</summary>
    public bool? Get(string origin, string kind) =>
        _items.FirstOrDefault(p => p.Origin == origin && p.Kind == kind)?.Allow;

    public void Set(string origin, string kind, bool allow)
    {
        _items.RemoveAll(p => p.Origin == origin && p.Kind == kind);
        _items.Add(new SitePermission(origin, kind, allow));
        _store.Save(_items);
    }

    public void Remove(string origin, string kind)
    {
        if (_items.RemoveAll(p => p.Origin == origin && p.Kind == kind) > 0) _store.Save(_items);
    }
}

/// <summary>Record of a page saved by the browser (MHTML or PDF).</summary>
public sealed record SavedPage(string Title, string Url, string FilePath, string Format, DateTimeOffset SavedAt);

/// <summary>List of saved pages (JSON index; the actual files stay where the user chose).</summary>
public sealed class SavedPageStore
{
    private readonly JsonFileStore<SavedPage> _store;
    private List<SavedPage> _items;

    public SavedPageStore(string path)
    {
        _store = new JsonFileStore<SavedPage>(path);
        _items = _store.Load();
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<SavedPage> All => _items.OrderByDescending(p => p.SavedAt).ToList();

    public void Add(SavedPage page)
    {
        _items.Add(page);
        _store.Save(_items);
    }

    public void Remove(string filePath)
    {
        if (_items.RemoveAll(p => string.Equals(p.FilePath, filePath, StringComparison.OrdinalIgnoreCase)) > 0)
            _store.Save(_items);
    }
}
