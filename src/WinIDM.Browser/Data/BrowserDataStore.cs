using Microsoft.Data.Sqlite;

namespace WinIDM.Browser;

/// <summary>Bookmark node: a folder (IsFolder) or a link (Url).</summary>
public sealed record BookmarkNode(long Id, string Title, string? Url, bool IsFolder, IReadOnlyList<BookmarkNode> Children);

public sealed record HistoryEntry(long Id, string Url, string? Title, DateTimeOffset VisitedUtc);

/// <summary>A tab saved for restore. Regular tabs only, never incognito tabs.</summary>
public sealed record SavedTab(string Url, string? Title, bool DesktopMode);

public enum SitePermissionKind
{
    Camera,
    Microphone,
    Location,
    Notifications,
    Popups,
}

public enum PermissionState
{
    Ask = 0,
    Allow = 1,
    Block = 2,
}

[Flags]
public enum BrowsingDataKinds
{
    None = 0,
    History = 1,
    SavedTabs = 2,
    SiteSettings = 4,
    Bookmarks = 8,
    All = History | SavedTabs | SiteSettings | Bookmarks,
}

/// <summary>
/// SQLite store for folder bookmarks (Netscape import/export), history, saved tabs,
/// and per-site settings (permissions, UA mode, and so on). No incognito data is stored here.
/// </summary>
public sealed class BrowserDataStore : IDisposable
{
    private const int SchemaVersion = 1;
    private const int MaxUrlLength = 2048;
    private const int MaxTitleLength = 500;
    private const string UserAgentModeKey = "ua_mode";
    private const string PermissionPrefix = "perm:";

    private readonly SqliteConnection _db;
    private readonly object _gate = new();
    private bool _disposed;

    public BrowserDataStore(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        string full = Path.GetFullPath(dbPath);
        string? dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = full,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        _db.Open();
        Exec("PRAGMA foreign_keys = ON; PRAGMA secure_delete = ON;");
        CreateSchema();
    }

    // ---------------------------------------------------------------------
    // Bookmarks

    /// <summary>Add a link. Only http(s) is accepted.</summary>
    public long AddBookmark(string title, string url, long? folderId = null)
    {
        if (!HostUtil.TryGetHttpUri(url, out var uri)) throw new ArgumentException("Bookmark must be an http(s) URL.", nameof(url));
        return Insert(folderId, false, Clip(title, MaxTitleLength, uri.AbsoluteUri)!, uri.AbsoluteUri);
    }

    public long AddFolder(string title, long? parentId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        return Insert(parentId, true, Clip(title, MaxTitleLength, title)!, null);
    }

    public void UpdateBookmark(long id, string title, string? url = null)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            if (url is null)
            {
                cmd.CommandText = "UPDATE bookmarks SET title = $t WHERE id = $id;";
            }
            else
            {
                if (!HostUtil.TryGetHttpUri(url, out var uri)) throw new ArgumentException("Bookmark must be an http(s) URL.", nameof(url));
                url = uri.AbsoluteUri;
                cmd.CommandText = "UPDATE bookmarks SET title = $t, url = $u WHERE id = $id AND is_folder = 0;";
                cmd.Parameters.AddWithValue("$u", url);
            }
            cmd.Parameters.AddWithValue("$t", Clip(title, MaxTitleLength, title));
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Move a node to another folder (null = root). Moving into one's own descendant is rejected.</summary>
    public void MoveBookmark(long id, long? newParentId)
    {
        lock (_gate)
        {
            if (newParentId is long target)
            {
                if (target == id) throw new InvalidOperationException("Cannot move a folder into itself.");
                long? cursor = target;
                while (cursor is long c)
                {
                    if (c == id) throw new InvalidOperationException("Cannot move a folder into its own descendant.");
                    cursor = ParentOf(c);
                }
            }

            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE bookmarks SET parent_id = $p, position = $pos WHERE id = $id;";
            cmd.Parameters.AddWithValue("$p", (object?)newParentId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pos", NextPosition(newParentId));
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Delete a node. Deleting a folder deletes its contents too.</summary>
    public void RemoveBookmark(long id)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM bookmarks WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Return the bookmark tree. The root is the list of top-level nodes.</summary>
    public IReadOnlyList<BookmarkNode> GetBookmarkTree()
    {
        lock (_gate)
        {
            var byParent = new Dictionary<long, List<(long Id, string Title, string? Url, bool IsFolder)>>();
            var roots = new List<(long Id, string Title, string? Url, bool IsFolder)>();

            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = "SELECT id, parent_id, title, url, is_folder FROM bookmarks ORDER BY position, id;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var row = (reader.GetInt64(0), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt64(4) != 0);
                    if (reader.IsDBNull(1))
                    {
                        roots.Add(row);
                    }
                    else
                    {
                        long parent = reader.GetInt64(1);
                        if (!byParent.TryGetValue(parent, out var list))
                        {
                            list = new List<(long, string, string?, bool)>();
                            byParent[parent] = list;
                        }
                        list.Add(row);
                    }
                }
            }

            return Build(roots, byParent, 0);
        }
    }

    /// <summary>Export to Netscape bookmark HTML (a format many browsers recognize).</summary>
    public string ExportBookmarksHtml() => NetscapeBookmarks.Export(GetBookmarkTree());

    /// <summary>Import Netscape bookmark HTML. Returns the number of links added.</summary>
    public int ImportBookmarksHtml(string html, long? folderId = null)
    {
        ArgumentNullException.ThrowIfNull(html);
        var nodes = NetscapeBookmarks.Parse(html, out _);
        lock (_gate)
        {
            int added = 0;
            Exec("BEGIN IMMEDIATE;");
            try
            {
                foreach (var node in nodes) added += InsertNetscape(node, folderId, 0);
                Exec("COMMIT;");
            }
            catch
            {
                Exec("ROLLBACK;");
                throw;
            }
            return added;
        }
    }

    // ---------------------------------------------------------------------
    // History

    /// <summary>Record a visit. Schemes other than http(s) (including data:) are ignored.</summary>
    public bool AddHistory(string url, string? title, DateTimeOffset? visitedUtc = null)
    {
        if (!HostUtil.TryGetHttpUri(url, out var uri) || uri.AbsoluteUri.Length > MaxUrlLength) return false;
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO history(url, title, visited_utc) VALUES ($u, $t, $v);";
            cmd.Parameters.AddWithValue("$u", uri.AbsoluteUri);
            cmd.Parameters.AddWithValue("$t", (object?)Clip(title, MaxTitleLength, null) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$v", (visitedUtc ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds());
            cmd.ExecuteNonQuery();
        }
        return true;
    }

    /// <summary>Search history by URL or title (newest first).</summary>
    public IReadOnlyList<HistoryEntry> SearchHistory(string? query = null, int limit = 100)
    {
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        lock (_gate)
        {
            var result = new List<HistoryEntry>();
            using var cmd = _db.CreateCommand();
            if (string.IsNullOrWhiteSpace(query))
            {
                cmd.CommandText = "SELECT id, url, title, visited_utc FROM history ORDER BY visited_utc DESC, id DESC LIMIT $n;";
            }
            else
            {
                cmd.CommandText = "SELECT id, url, title, visited_utc FROM history " +
                                  "WHERE url LIKE $q ESCAPE '\\' OR title LIKE $q ESCAPE '\\' " +
                                  "ORDER BY visited_utc DESC, id DESC LIMIT $n;";
                cmd.Parameters.AddWithValue("$q", "%" + EscapeLike(query.Trim()) + "%");
            }
            cmd.Parameters.AddWithValue("$n", limit);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new HistoryEntry(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3))));
            }
            return result;
        }
    }

    /// <summary>Delete history within the time range [from, to]. Returns the number of rows deleted.</summary>
    public int DeleteHistory(DateTimeOffset from, DateTimeOffset to)
    {
        if (to < from) throw new ArgumentException("Invalid time range.");
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM history WHERE visited_utc BETWEEN $from AND $to;";
            cmd.Parameters.AddWithValue("$from", from.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$to", to.ToUnixTimeMilliseconds());
            return cmd.ExecuteNonQuery();
        }
    }

    public int HistoryCount
    {
        get
        {
            lock (_gate)
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM history;";
                return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            }
        }
    }

    // ---------------------------------------------------------------------
    // Saved tabs

    /// <summary>Replace the saved tab list. Incognito tabs cannot be saved here.</summary>
    public void SaveTabs(IReadOnlyList<SavedTab> tabs)
    {
        ArgumentNullException.ThrowIfNull(tabs);
        lock (_gate)
        {
            Exec("BEGIN IMMEDIATE;");
            try
            {
                Exec("DELETE FROM saved_tabs;");
                int position = 0;
                foreach (var tab in tabs)
                {
                    if (!HostUtil.TryGetHttpUri(tab.Url, out var uri)) continue;
                    using var cmd = _db.CreateCommand();
                    cmd.CommandText = "INSERT INTO saved_tabs(position, url, title, desktop_mode) VALUES ($p, $u, $t, $d);";
                    cmd.Parameters.AddWithValue("$p", position++);
                    cmd.Parameters.AddWithValue("$u", uri.AbsoluteUri);
                    cmd.Parameters.AddWithValue("$t", (object?)Clip(tab.Title, MaxTitleLength, null) ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$d", tab.DesktopMode ? 1 : 0);
                    cmd.ExecuteNonQuery();
                }
                Exec("COMMIT;");
            }
            catch
            {
                Exec("ROLLBACK;");
                throw;
            }
        }
    }

    public IReadOnlyList<SavedTab> LoadTabs()
    {
        lock (_gate)
        {
            var result = new List<SavedTab>();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT url, title, desktop_mode FROM saved_tabs ORDER BY position;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new SavedTab(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetInt64(2) != 0));
            }
            return result;
        }
    }

    // ---------------------------------------------------------------------
    // Per-site settings

    public PermissionState GetPermission(string host, SitePermissionKind kind)
    {
        string? raw = GetSiteSetting(host, PermissionPrefix + kind);
        return raw switch
        {
            "allow" => PermissionState.Allow,
            "block" => PermissionState.Block,
            _ => PermissionState.Ask,
        };
    }

    public void SetPermission(string host, SitePermissionKind kind, PermissionState state)
    {
        string? value = state switch
        {
            PermissionState.Allow => "allow",
            PermissionState.Block => "block",
            _ => null,
        };
        SetSiteSetting(host, PermissionPrefix + kind, value);
    }

    public UserAgentMode GetUserAgentMode(string host)
    {
        string? raw = GetSiteSetting(host, UserAgentModeKey);
        return int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int v)
               && Enum.IsDefined(typeof(UserAgentMode), v)
            ? (UserAgentMode)v
            : UserAgentMode.Default;
    }

    public void SetUserAgentMode(string host, UserAgentMode mode) =>
        SetSiteSetting(host, UserAgentModeKey, mode == UserAgentMode.Default ? null : ((int)mode).ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Read one site setting. Returns null when absent.</summary>
    public string? GetSiteSetting(string host, string key)
    {
        string h = HostUtil.NormalizeHost(host);
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT value FROM site_settings WHERE host = $h AND key = $k;";
            cmd.Parameters.AddWithValue("$h", h);
            cmd.Parameters.AddWithValue("$k", key);
            return cmd.ExecuteScalar() as string;
        }
    }

    /// <summary>Set a site setting. A null value deletes the setting.</summary>
    public void SetSiteSetting(string host, string key, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        string h = HostUtil.NormalizeHost(host);
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            if (value is null)
            {
                cmd.CommandText = "DELETE FROM site_settings WHERE host = $h AND key = $k;";
            }
            else
            {
                cmd.CommandText = "INSERT INTO site_settings(host, key, value) VALUES ($h, $k, $v) " +
                                  "ON CONFLICT(host, key) DO UPDATE SET value = excluded.value;";
                cmd.Parameters.AddWithValue("$v", value);
            }
            cmd.Parameters.AddWithValue("$h", h);
            cmd.Parameters.AddWithValue("$k", key);
            cmd.ExecuteNonQuery();
        }
    }

    // ---------------------------------------------------------------------
    // Clearing

    /// <summary>Clear the selected data categories. Used on exit or at the user's request.</summary>
    public void ClearBrowsingData(BrowsingDataKinds kinds)
    {
        lock (_gate)
        {
            Exec("BEGIN IMMEDIATE;");
            try
            {
                if (kinds.HasFlag(BrowsingDataKinds.History)) Exec("DELETE FROM history;");
                if (kinds.HasFlag(BrowsingDataKinds.SavedTabs)) Exec("DELETE FROM saved_tabs;");
                if (kinds.HasFlag(BrowsingDataKinds.SiteSettings)) Exec("DELETE FROM site_settings;");
                if (kinds.HasFlag(BrowsingDataKinds.Bookmarks)) Exec("DELETE FROM bookmarks;");
                Exec("COMMIT;");
            }
            catch
            {
                Exec("ROLLBACK;");
                throw;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate) _db.Dispose();
    }

    // ---------------------------------------------------------------------
    // Internals

    private void CreateSchema()
    {
        Exec(@"
CREATE TABLE IF NOT EXISTS bookmarks(
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    parent_id INTEGER REFERENCES bookmarks(id) ON DELETE CASCADE,
    is_folder INTEGER NOT NULL,
    title TEXT NOT NULL,
    url TEXT,
    position INTEGER NOT NULL,
    created_utc INTEGER NOT NULL);
CREATE INDEX IF NOT EXISTS ix_bookmarks_parent ON bookmarks(parent_id, position);
CREATE TABLE IF NOT EXISTS history(
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    url TEXT NOT NULL,
    title TEXT,
    visited_utc INTEGER NOT NULL);
CREATE INDEX IF NOT EXISTS ix_history_time ON history(visited_utc);
CREATE TABLE IF NOT EXISTS saved_tabs(
    position INTEGER PRIMARY KEY,
    url TEXT NOT NULL,
    title TEXT,
    desktop_mode INTEGER NOT NULL DEFAULT 0);
CREATE TABLE IF NOT EXISTS site_settings(
    host TEXT NOT NULL,
    key TEXT NOT NULL,
    value TEXT NOT NULL,
    PRIMARY KEY(host, key));");

        Exec($"PRAGMA user_version = {SchemaVersion};");
    }

    private void Exec(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private long Insert(long? parentId, bool isFolder, string title, string? url)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO bookmarks(parent_id, is_folder, title, url, position, created_utc) " +
                              "VALUES ($p, $f, $t, $u, $pos, $now); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$p", (object?)parentId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$f", isFolder ? 1 : 0);
            cmd.Parameters.AddWithValue("$t", title);
            cmd.Parameters.AddWithValue("$u", (object?)url ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pos", NextPosition(parentId));
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private int InsertNetscape(NetscapeNode node, long? parentId, int depth)
    {
        if (depth > NetscapeBookmarks.MaxDepth) return 0;
        if (node.IsFolder)
        {
            long folderId = Insert(parentId, true, Clip(node.Title, MaxTitleLength, "(untitled)")!, null);
            int added = 0;
            foreach (var child in node.Children) added += InsertNetscape(child, folderId, depth + 1);
            return added;
        }
        Insert(parentId, false, Clip(node.Title, MaxTitleLength, node.Url)!, node.Url);
        return 1;
    }

    private long? ParentOf(long id)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT parent_id FROM bookmarks WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        object? value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private long NextPosition(long? parentId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(position), -1) + 1 FROM bookmarks WHERE parent_id IS $p;";
        cmd.Parameters.AddWithValue("$p", (object?)parentId ?? DBNull.Value);
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static IReadOnlyList<BookmarkNode> Build(
        List<(long Id, string Title, string? Url, bool IsFolder)> nodes,
        Dictionary<long, List<(long Id, string Title, string? Url, bool IsFolder)>> byParent,
        int depth)
    {
        var result = new List<BookmarkNode>(nodes.Count);
        foreach (var n in nodes)
        {
            IReadOnlyList<BookmarkNode> children = Array.Empty<BookmarkNode>();
            if (n.IsFolder && depth < NetscapeBookmarks.MaxDepth && byParent.TryGetValue(n.Id, out var kids))
                children = Build(kids, byParent, depth + 1);
            result.Add(new BookmarkNode(n.Id, n.Title, n.Url, n.IsFolder, children));
        }
        return result;
    }

    private static string? Clip(string? value, int max, string? fallback)
    {
        string? v = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        if (v is null) return null;
        return v.Length <= max ? v : v[..max];
    }

    private static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
