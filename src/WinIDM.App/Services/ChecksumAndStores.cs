using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace WinIDM.App.Services;

/// <summary>On-demand MD5 and SHA-256 checksum (P0 #5). Read once as a stream, without loading the file into memory.</summary>
public static class ChecksumService
{
    public static Task<(string Md5, string Sha256)> ComputeAsync(string path, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan);
            using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1 << 16];
            int n;
            while ((n = fs.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                md5.AppendData(buffer, 0, n);
                sha.AppendData(buffer, 0, n);
            }
            return (Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant(),
                    Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant());
        }, ct);
}

/// <summary>Atomic JSON file: write to a temporary file, then replace.</summary>
public sealed class JsonFileStore<T>
{
    private readonly string _path;
    private readonly object _gate = new();

    public JsonFileStore(string path)
    {
        _path = path;
    }

    public List<T> Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return new List<T>();
                return JsonSerializer.Deserialize<List<T>>(File.ReadAllText(_path)) ?? new List<T>();
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                AppLog.Error($"Read {Path.GetFileName(_path)}", ex);
                return new List<T>();
            }
        }
    }

    public void Save(IEnumerable<T> items)
    {
        lock (_gate)
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(items.ToList(), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _path, overwrite: true);
        }
    }
}

/// <summary>A bookmark; <paramref name="Folder"/> is the IDM+ bookmark folder (null or empty = the top level).</summary>
public sealed record Bookmark(string Title, string Url, DateTimeOffset Added, string? Folder = null);

public sealed record HistoryEntry(string Title, string Url, DateTimeOffset Visited);

/// <summary>Browser bookmarks. No Composition counterpart until the Browser module is finished (see the TODO there).</summary>
public sealed class BookmarkStore
{
    private readonly JsonFileStore<Bookmark> _store;
    private List<Bookmark> _items;

    public BookmarkStore(string path)
    {
        _store = new JsonFileStore<Bookmark>(path);
        _items = _store.Load();
    }

    public IReadOnlyList<Bookmark> All => _items;

    public bool Contains(string url) => _items.Any(b => string.Equals(b.Url, url, StringComparison.OrdinalIgnoreCase));

    public void Add(string title, string url)
    {
        if (Contains(url)) return;
        _items.Insert(0, new Bookmark(title, url, DateTimeOffset.Now));
        _store.Save(_items);
    }

    public void Remove(string url)
    {
        _items.RemoveAll(b => string.Equals(b.Url, url, StringComparison.OrdinalIgnoreCase));
        _store.Save(_items);
    }

    /// <summary>Puts a bookmark in a folder (null or blank = the top level).</summary>
    public void MoveToFolder(string url, string? folder)
    {
        var name = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim();
        _items = _items.Select(b => string.Equals(b.Url, url, StringComparison.OrdinalIgnoreCase) ? b with { Folder = name } : b).ToList();
        _store.Save(_items);
    }

    /// <summary>
    /// Bookmarks for display: by folder (IDM+ "Show bookmark folders first" puts foldered ones before the top level),
    /// newest first inside each group.
    /// </summary>
    public IReadOnlyList<Bookmark> Ordered(bool foldersFirst) =>
        _items.OrderBy(b => foldersFirst ? (string.IsNullOrEmpty(b.Folder) ? 1 : 0) : (string.IsNullOrEmpty(b.Folder) ? 0 : 1))
            .ThenBy(b => b.Folder ?? "", StringComparer.CurrentCultureIgnoreCase)
            .ThenByDescending(b => b.Added)
            .ToList();

    /// <summary>Backup restore: replace everything, or merge (existing URLs are kept).</summary>
    public void Restore(IEnumerable<Bookmark> incoming, bool overwrite)
    {
        var list = overwrite ? new List<Bookmark>() : new List<Bookmark>(_items);
        foreach (var b in incoming.Where(b => UrlHelper.IsHttp(b.Url)))
            if (!list.Any(x => string.Equals(x.Url, b.Url, StringComparison.OrdinalIgnoreCase))) list.Add(b);
        _items = list;
        _store.Save(_items);
    }
}

/// <summary>Browser history (capped at 5000 entries). Incognito tabs do not write here.</summary>
public sealed class HistoryStore
{
    private const int MaxEntries = 5000;
    private readonly JsonFileStore<HistoryEntry> _store;
    private List<HistoryEntry> _items;

    public HistoryStore(string path)
    {
        _store = new JsonFileStore<HistoryEntry>(path);
        _items = _store.Load();
    }

    public IReadOnlyList<HistoryEntry> All => _items;

    public void Add(string title, string url)
    {
        if (_items.Count > 0 && _items[0].Url == url) return; // avoid consecutive duplicates
        _items.Insert(0, new HistoryEntry(title, url, DateTimeOffset.Now));
        if (_items.Count > MaxEntries) _items = _items.Take(MaxEntries).ToList();
        _store.Save(_items);
    }

    public void Clear()
    {
        _items = new List<HistoryEntry>();
        _store.Save(_items);
    }

    /// <summary>Backup restore: replace everything, or merge (entries with the same URL and time are not duplicated).</summary>
    public void Restore(IEnumerable<HistoryEntry> incoming, bool overwrite)
    {
        var list = overwrite ? new List<HistoryEntry>() : new List<HistoryEntry>(_items);
        foreach (var h in incoming.Where(h => UrlHelper.IsHttp(h.Url)))
            if (!list.Any(x => x.Url == h.Url && x.Visited == h.Visited)) list.Add(h);
        _items = list.OrderByDescending(h => h.Visited).Take(MaxEntries).ToList();
        _store.Save(_items);
    }
}
