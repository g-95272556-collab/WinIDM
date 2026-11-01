using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WinIDM.Core.Contracts;
using WinIDM.Core.Models;

namespace WinIDM.Engine;

/// <summary>
/// SQLite storage for downloads, segments (threads), credentials, domain limits and site limits.
/// The schema is controlled by <c>PRAGMA user_version</c> and upgraded in steps.
/// Passwords are encrypted with DPAPI (current user scope) before being stored.
/// </summary>
public sealed class SqliteDownloadRepository : IDownloadRepository, ISiteRuleProvider, ISiteSettingsProvider, IDisposable
{
    /// <summary>Latest schema version.</summary>
    public const int SchemaVersion = 3;

    private const string SecretPrefix = "dpapi1:";

    private static readonly string[] Migration1 =
    [
        """
        CREATE TABLE downloads (
            id                INTEGER PRIMARY KEY AUTOINCREMENT,
            kind              INTEGER NOT NULL,
            url               TEXT    NOT NULL,
            final_url         TEXT,
            file_name         TEXT    NOT NULL DEFAULT '',
            save_directory    TEXT    NOT NULL DEFAULT '',
            total_bytes       INTEGER NOT NULL DEFAULT -1,
            downloaded_bytes  INTEGER NOT NULL DEFAULT 0,
            state             INTEGER NOT NULL,
            category          TEXT    NOT NULL DEFAULT 'Other',
            referer           TEXT,
            user_agent        TEXT,
            cookie_header     TEXT,
            headers_json      TEXT    NOT NULL DEFAULT '{}',
            username          TEXT,
            password_protected TEXT,
            max_segments      INTEGER NOT NULL DEFAULT 7,
            speed_limit       INTEGER NOT NULL DEFAULT 0,
            accepts_ranges    INTEGER NOT NULL DEFAULT 0,
            etag              TEXT,
            last_modified     TEXT,
            expected_sha256   TEXT,
            expected_md5      TEXT,
            error_message     TEXT,
            error_code        INTEGER NOT NULL DEFAULT 0,
            sort_order        INTEGER NOT NULL DEFAULT 0,
            scheduled_at      INTEGER,
            created_at        INTEGER NOT NULL,
            completed_at      INTEGER
        );
        """,
        """
        CREATE TABLE threads (
            download_id  INTEGER NOT NULL REFERENCES downloads(id) ON DELETE CASCADE,
            idx          INTEGER NOT NULL,
            start_byte   INTEGER NOT NULL,
            end_byte     INTEGER NOT NULL,
            downloaded   INTEGER NOT NULL DEFAULT 0,
            state        INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (download_id, idx)
        );
        """,
        """
        CREATE TABLE credentials (
            domain             TEXT PRIMARY KEY,
            username           TEXT    NOT NULL,
            password_protected TEXT    NOT NULL,
            use_for_subdomain  INTEGER NOT NULL DEFAULT 0
        );
        """,
        """
        CREATE TABLE domain_part_limit (
            domain            TEXT PRIMARY KEY,
            part_limit        INTEGER NOT NULL,
            use_for_subdomain INTEGER NOT NULL DEFAULT 0
        );
        """,
        """
        CREATE TABLE site_settings (
            domain                    TEXT PRIMARY KEY,
            features                  INTEGER NOT NULL DEFAULT 0,
            one_bit_store             INTEGER NOT NULL DEFAULT 0,
            four_bit_store            INTEGER NOT NULL DEFAULT 0,
            useragent                 TEXT,
            additional_extensions     TEXT,
            additional_contenttypes   TEXT
        );
        """,
    ];

    private static readonly string[] Migration2 =
    [
        """
        CREATE TABLE site_limits (
            domain        TEXT PRIMARY KEY,
            download_limit INTEGER NOT NULL,
            download_link INTEGER NOT NULL DEFAULT 1,
            referer_link  INTEGER NOT NULL DEFAULT 0
        );
        """,
        "CREATE INDEX idx_downloads_state ON downloads(state);",
    ];

    /// <summary>v3: per-site settings columns (default folder, encrypted cookies, subdomain matching).</summary>
    private static readonly string[] Migration3 =
    [
        "ALTER TABLE site_settings ADD COLUMN save_directory TEXT;",
        "ALTER TABLE site_settings ADD COLUMN cookies_protected TEXT;",
        "ALTER TABLE site_settings ADD COLUMN use_for_subdomain INTEGER NOT NULL DEFAULT 1;",
    ];

    private static readonly string[][] Migrations = [Migration1, Migration2, Migration3];

    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public SqliteDownloadRepository(string dbPath)
    {
        if (string.IsNullOrWhiteSpace(dbPath)) throw new ArgumentException("A database path is required.", nameof(dbPath));

        var directory = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();

        using (var pragma = _connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
            pragma.ExecuteNonQuery();
        }

        Migrate();
    }

    /// <summary>Current schema version of the database.</summary>
    public int CurrentSchemaVersion
    {
        get
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "PRAGMA user_version;";
            return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    // ------------------------------------------------------------------
    // IDownloadRepository
    // ------------------------------------------------------------------

    public async Task<long> InsertAsync(DownloadItem item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        await _gate.WaitAsync(ct);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"INSERT INTO downloads ({ItemColumnsNoId}) VALUES ({ItemParameters});";
            BindItem(cmd, item);
            await cmd.ExecuteNonQueryAsync(ct);

            using var idCmd = _connection.CreateCommand();
            idCmd.CommandText = "SELECT last_insert_rowid();";
            var id = Convert.ToInt64(await idCmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
            item.Id = id;
            return id;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateAsync(DownloadItem item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        await _gate.WaitAsync(ct);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"UPDATE downloads SET {ItemAssignments} WHERE id = $id;";
            BindItem(cmd, item);
            cmd.Parameters.AddWithValue("$id", item.Id);
            var rows = await cmd.ExecuteNonQueryAsync(ct);
            if (rows == 0) throw new KeyNotFoundException($"Download {item.Id} does not exist.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM downloads WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DownloadItem?> GetAsync(long id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"SELECT {ItemColumns} FROM downloads WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = await cmd.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct) ? ReadItem(reader) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<DownloadItem>> ListAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"SELECT {ItemColumns} FROM downloads ORDER BY sort_order, id;";
            using var reader = await cmd.ExecuteReaderAsync(ct);
            var list = new List<DownloadItem>();
            while (await reader.ReadAsync(ct)) list.Add(ReadItem(reader));
            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveSegmentsAsync(long downloadId, IReadOnlyList<SegmentInfo> segments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        await _gate.WaitAsync(ct);
        try
        {
            using var tx = _connection.BeginTransaction();

            using (var del = _connection.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM threads WHERE download_id = $id;";
                del.Parameters.AddWithValue("$id", downloadId);
                await del.ExecuteNonQueryAsync(ct);
            }

            foreach (var seg in segments)
            {
                using var ins = _connection.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO threads (download_id, idx, start_byte, end_byte, downloaded, state)
                    VALUES ($id, $idx, $start, $end, $downloaded, $state);
                    """;
                ins.Parameters.AddWithValue("$id", downloadId);
                ins.Parameters.AddWithValue("$idx", seg.Index);
                ins.Parameters.AddWithValue("$start", seg.Start);
                ins.Parameters.AddWithValue("$end", seg.End);
                ins.Parameters.AddWithValue("$downloaded", seg.Downloaded);
                ins.Parameters.AddWithValue("$state", (int)seg.State);
                await ins.ExecuteNonQueryAsync(ct);
            }

            tx.Commit();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<SegmentInfo>> LoadSegmentsAsync(long downloadId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT idx, start_byte, end_byte, downloaded, state FROM threads WHERE download_id = $id ORDER BY idx;";
            cmd.Parameters.AddWithValue("$id", downloadId);
            using var reader = await cmd.ExecuteReaderAsync(ct);
            var list = new List<SegmentInfo>();
            while (await reader.ReadAsync(ct))
            {
                list.Add(new SegmentInfo
                {
                    DownloadId = downloadId,
                    Index = reader.GetInt32(0),
                    Start = reader.GetInt64(1),
                    End = reader.GetInt64(2),
                    Downloaded = reader.GetInt64(3),
                    State = (SegmentState)reader.GetInt32(4),
                });
            }

            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ------------------------------------------------------------------
    // Credentials, domain limits and site limits
    // ------------------------------------------------------------------

    /// <summary>Stores or updates the Basic credentials for a domain.</summary>
    public async Task SetCredentialAsync(string domain, string username, string password, bool useForSubdomain, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        await Execute(
            """
            INSERT INTO credentials (domain, username, password_protected, use_for_subdomain)
            VALUES ($domain, $user, $pass, $sub)
            ON CONFLICT(domain) DO UPDATE SET username = $user, password_protected = $pass, use_for_subdomain = $sub;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$domain", domain.ToLowerInvariant());
                cmd.Parameters.AddWithValue("$user", username);
                cmd.Parameters.AddWithValue("$pass", ProtectSecret(password) ?? string.Empty);
                cmd.Parameters.AddWithValue("$sub", useForSubdomain ? 1 : 0);
            }, ct);
    }

    /// <summary>Finds credentials for a host (exact match, or subdomain if allowed).</summary>
    public async Task<(string Username, string Password)?> GetCredentialAsync(string host, CancellationToken ct = default)
    {
        var rows = await QueryRows("SELECT domain, username, password_protected, use_for_subdomain FROM credentials;", ct);
        foreach (var row in rows.Where(r => HostMatches(host, r.Domain, r.UseForSubdomain)).OrderByDescending(r => r.Domain.Length))
        {
            return (row.Value1, UnprotectSecret(row.Value2));
        }

        return null;
    }

    public async Task SetDomainPartLimitAsync(string domain, int partLimit, bool useForSubdomain, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        if (partLimit < 0) throw new ArgumentOutOfRangeException(nameof(partLimit));
        await Execute(
            """
            INSERT INTO domain_part_limit (domain, part_limit, use_for_subdomain) VALUES ($domain, $limit, $sub)
            ON CONFLICT(domain) DO UPDATE SET part_limit = $limit, use_for_subdomain = $sub;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$domain", domain.ToLowerInvariant());
                cmd.Parameters.AddWithValue("$limit", partLimit);
                cmd.Parameters.AddWithValue("$sub", useForSubdomain ? 1 : 0);
            }, ct);
    }

    public async Task SetSiteLimitAsync(SiteLimit limit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(limit);
        if (limit.Limit < 0) throw new ArgumentOutOfRangeException(nameof(limit));
        await Execute(
            """
            INSERT INTO site_limits (domain, download_limit, download_link, referer_link) VALUES ($domain, $limit, $dl, $rl)
            ON CONFLICT(domain) DO UPDATE SET download_limit = $limit, download_link = $dl, referer_link = $rl;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$domain", limit.Domain.ToLowerInvariant());
                cmd.Parameters.AddWithValue("$limit", limit.Limit);
                cmd.Parameters.AddWithValue("$dl", limit.DownloadLink ? 1 : 0);
                cmd.Parameters.AddWithValue("$rl", limit.RefererLink ? 1 : 0);
            }, ct);
    }

    public async Task<int> GetDomainPartLimitAsync(string host, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(host)) return 0;
        var rows = await QueryRows("SELECT domain, part_limit, use_for_subdomain FROM domain_part_limit;", ct);
        var match = rows
            .Where(r => HostMatches(host, r.Domain, r.UseForSubdomain))
            .OrderByDescending(r => r.Domain.Length)
            .FirstOrDefault();
        return match is null ? 0 : int.Parse(match.Value1, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<SiteLimit?> GetSiteLimitAsync(string host, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(host)) return null;
        await _gate.WaitAsync(ct);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT domain, download_limit, download_link, referer_link FROM site_limits;";
            using var reader = await cmd.ExecuteReaderAsync(ct);
            SiteLimit? best = null;
            while (await reader.ReadAsync(ct))
            {
                var domain = reader.GetString(0);
                if (!HostMatches(host, domain, useForSubdomain: true)) continue;
                if (best is null || domain.Length > best.Domain.Length)
                {
                    best = new SiteLimit(domain, reader.GetInt32(1), reader.GetInt64(2) != 0, reader.GetInt64(3) != 0);
                }
            }

            return best;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ------------------------------------------------------------------
    // Per-site settings (site_settings)
    // ------------------------------------------------------------------

    /// <summary>Stores or updates per-site settings. Cookies are encrypted with DPAPI.</summary>
    public async Task SetSiteSettingsAsync(SiteSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.Domain);

        var domain = NormalizeDomain(settings.Domain);
        await Execute(
            """
            INSERT INTO site_settings (domain, useragent, cookies_protected, save_directory, use_for_subdomain)
            VALUES ($domain, $ua, $ck, $dir, $sub)
            ON CONFLICT(domain) DO UPDATE SET useragent = $ua, cookies_protected = $ck,
                save_directory = $dir, use_for_subdomain = $sub;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$domain", domain);
                cmd.Parameters.AddWithValue("$ua", (object?)settings.UserAgent ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$ck", (object?)ProtectSecret(settings.Cookies) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$dir", (object?)settings.SaveDirectory ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$sub", settings.UseForSubdomain ? 1 : 0);
            }, ct);
    }

    /// <summary>Deletes the per-site settings for that domain (if any).</summary>
    public async Task DeleteSiteSettingsAsync(string domain, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        var normalized = NormalizeDomain(domain);
        await Execute("DELETE FROM site_settings WHERE domain = $domain;",
            cmd => cmd.Parameters.AddWithValue("$domain", normalized), ct);
    }

    /// <summary>Lists all per-site settings, sorted by domain. Cookies are decrypted.</summary>
    public async Task<IReadOnlyList<SiteSettings>> ListSiteSettingsAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT domain, useragent, cookies_protected, save_directory, use_for_subdomain FROM site_settings ORDER BY domain;";
            using var reader = await cmd.ExecuteReaderAsync(ct);
            var list = new List<SiteSettings>();
            while (await reader.ReadAsync(ct))
            {
                list.Add(ReadSiteSettings(reader));
            }

            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SiteSettings?> GetSiteSettingsAsync(string host, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(host)) return null;
        await _gate.WaitAsync(ct);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT domain, useragent, cookies_protected, save_directory, use_for_subdomain FROM site_settings;";
            using var reader = await cmd.ExecuteReaderAsync(ct);
            SiteSettings? best = null;
            while (await reader.ReadAsync(ct))
            {
                var settings = ReadSiteSettings(reader);
                if (!HostMatches(host, settings.Domain, settings.UseForSubdomain)) continue;
                if (best is null || settings.Domain.Length > best.Domain.Length) best = settings;
            }

            return best;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static SiteSettings ReadSiteSettings(SqliteDataReader reader)
    {
        var cookies = reader.IsDBNull(2) ? null : reader.GetString(2);
        return new SiteSettings(
            Domain: reader.GetString(0),
            UserAgent: reader.IsDBNull(1) ? null : reader.GetString(1),
            Cookies: cookies is null ? null : UnprotectSecret(cookies),
            SaveDirectory: reader.IsDBNull(3) ? null : reader.GetString(3),
            UseForSubdomain: reader.GetInt64(4) != 0);
    }

    private static string NormalizeDomain(string domain) => domain.Trim().TrimEnd('.').ToLowerInvariant();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Wait();
        try
        {
            _connection.Dispose();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    // ------------------------------------------------------------------
    // Internal helpers
    // ------------------------------------------------------------------

    private void Migrate()
    {
        var current = CurrentSchemaVersion;
        if (current > SchemaVersion)
        {
            throw new InvalidOperationException($"Database schema v{current} is newer than supported (v{SchemaVersion}).");
        }

        for (var version = current; version < SchemaVersion; version++)
        {
            using var tx = _connection.BeginTransaction();
            foreach (var sql in Migrations[version])
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }

            using (var setVersion = _connection.CreateCommand())
            {
                setVersion.Transaction = tx;
                setVersion.CommandText = $"PRAGMA user_version = {version + 1};";
                setVersion.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    private const string ItemColumns =
        "id, kind, url, final_url, file_name, save_directory, total_bytes, downloaded_bytes, state, category, " +
        "referer, user_agent, cookie_header, headers_json, username, password_protected, max_segments, speed_limit, " +
        "accepts_ranges, etag, last_modified, expected_sha256, expected_md5, error_message, error_code, sort_order, " +
        "scheduled_at, created_at, completed_at";

    private const string ItemColumnsNoId =
        "kind, url, final_url, file_name, save_directory, total_bytes, downloaded_bytes, state, category, " +
        "referer, user_agent, cookie_header, headers_json, username, password_protected, max_segments, speed_limit, " +
        "accepts_ranges, etag, last_modified, expected_sha256, expected_md5, error_message, error_code, sort_order, " +
        "scheduled_at, created_at, completed_at";

    private const string ItemParameters =
        "$kind, $url, $final_url, $file_name, $save_directory, $total_bytes, $downloaded_bytes, $state, $category, " +
        "$referer, $user_agent, $cookie_header, $headers_json, $username, $password_protected, $max_segments, $speed_limit, " +
        "$accepts_ranges, $etag, $last_modified, $expected_sha256, $expected_md5, $error_message, $error_code, $sort_order, " +
        "$scheduled_at, $created_at, $completed_at";

    private static readonly string ItemAssignments = string.Join(", ",
        ItemColumnsNoId.Split(',', StringSplitOptions.TrimEntries).Select(c => $"{c} = ${c}"));

    private static void BindItem(SqliteCommand cmd, DownloadItem item)
    {
        cmd.Parameters.AddWithValue("$kind", (int)item.Kind);
        cmd.Parameters.AddWithValue("$url", item.Url);
        cmd.Parameters.AddWithValue("$final_url", (object?)item.FinalUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$file_name", item.FileName);
        cmd.Parameters.AddWithValue("$save_directory", item.SaveDirectory);
        cmd.Parameters.AddWithValue("$total_bytes", item.TotalBytes);
        cmd.Parameters.AddWithValue("$downloaded_bytes", item.DownloadedBytes);
        cmd.Parameters.AddWithValue("$state", (int)item.State);
        cmd.Parameters.AddWithValue("$category", item.Category);
        cmd.Parameters.AddWithValue("$referer", (object?)item.Referer ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$user_agent", (object?)item.UserAgent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cookie_header", (object?)item.CookieHeader ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$headers_json", JsonSerializer.Serialize(item.Headers));
        cmd.Parameters.AddWithValue("$username", (object?)item.Username ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$password_protected", (object?)ProtectSecret(item.Password) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$max_segments", item.MaxSegments);
        cmd.Parameters.AddWithValue("$speed_limit", item.SpeedLimitBytesPerSec);
        cmd.Parameters.AddWithValue("$accepts_ranges", item.AcceptsRanges ? 1 : 0);
        cmd.Parameters.AddWithValue("$etag", (object?)item.ETag ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$last_modified", (object?)item.LastModified ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$expected_sha256", (object?)item.ExpectedSha256 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$expected_md5", (object?)item.ExpectedMd5 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$error_message", (object?)item.ErrorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$error_code", item.ErrorCode);
        cmd.Parameters.AddWithValue("$sort_order", item.SortOrder);
        cmd.Parameters.AddWithValue("$scheduled_at", item.ScheduledAt is { } s ? (object)s.ToUnixTimeMilliseconds() : DBNull.Value);
        cmd.Parameters.AddWithValue("$created_at", item.CreatedAt.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$completed_at", item.CompletedAt is { } c ? (object)c.ToUnixTimeMilliseconds() : DBNull.Value);
    }

    private static DownloadItem ReadItem(SqliteDataReader r)
    {
        var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(13)) ?? new();
        return new DownloadItem
        {
            Id = r.GetInt64(0),
            Kind = (DownloadKind)r.GetInt32(1),
            Url = r.GetString(2),
            FinalUrl = r.IsDBNull(3) ? null : r.GetString(3),
            FileName = r.GetString(4),
            SaveDirectory = r.GetString(5),
            TotalBytes = r.GetInt64(6),
            DownloadedBytes = r.GetInt64(7),
            State = (DownloadState)r.GetInt32(8),
            Category = r.GetString(9),
            Referer = r.IsDBNull(10) ? null : r.GetString(10),
            UserAgent = r.IsDBNull(11) ? null : r.GetString(11),
            CookieHeader = r.IsDBNull(12) ? null : r.GetString(12),
            Headers = headers,
            Username = r.IsDBNull(14) ? null : r.GetString(14),
            Password = r.IsDBNull(15) ? null : UnprotectSecret(r.GetString(15)),
            MaxSegments = r.GetInt32(16),
            SpeedLimitBytesPerSec = r.GetInt64(17),
            AcceptsRanges = r.GetInt64(18) != 0,
            ETag = r.IsDBNull(19) ? null : r.GetString(19),
            LastModified = r.IsDBNull(20) ? null : r.GetString(20),
            ExpectedSha256 = r.IsDBNull(21) ? null : r.GetString(21),
            ExpectedMd5 = r.IsDBNull(22) ? null : r.GetString(22),
            ErrorMessage = r.IsDBNull(23) ? null : r.GetString(23),
            ErrorCode = r.GetInt32(24),
            SortOrder = r.GetInt32(25),
            ScheduledAt = r.IsDBNull(26) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(26)),
            CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(27)),
            CompletedAt = r.IsDBNull(28) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(28)),
        };
    }

    private async Task Execute(string sql, Action<SqliteCommand> bind, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            bind(cmd);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed record Row(string Value0, string Value1, string Value2, bool UseForSubdomain)
    {
        public string Domain => Value0;
    }

    /// <summary>Reads a small table with (domain, value, value, subdomain).</summary>
    private async Task<List<Row>> QueryRows(string sql, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            using var reader = await cmd.ExecuteReaderAsync(ct);
            var rows = new List<Row>();
            var columns = reader.FieldCount;
            while (await reader.ReadAsync(ct))
            {
                var v0 = reader.GetString(0);
                var v1 = columns > 1 ? Convert.ToString(reader.GetValue(1), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty : string.Empty;
                var v2 = columns > 2 && reader.GetValue(2) is string s2 ? s2 : string.Empty;
                var sub = reader.GetValue(columns - 1);
                rows.Add(new Row(v0, v1, v2, Convert.ToInt64(sub, System.Globalization.CultureInfo.InvariantCulture) != 0));
            }

            return rows;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool HostMatches(string host, string domain, bool useForSubdomain)
    {
        if (host.Equals(domain, StringComparison.OrdinalIgnoreCase)) return true;
        return useForSubdomain && host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
    }

    // DPAPI exists on Windows only; WinIDM is a Windows application, so the CA1416 warning is accepted here.
#pragma warning disable CA1416
    private static string? ProtectSecret(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), optionalEntropy: null, DataProtectionScope.CurrentUser);
        return SecretPrefix + Convert.ToBase64String(protectedBytes);
    }

    private static string UnprotectSecret(string stored)
    {
        if (!stored.StartsWith(SecretPrefix, StringComparison.Ordinal)) return stored;
        var bytes = Convert.FromBase64String(stored[SecretPrefix.Length..]);
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser));
    }
#pragma warning restore CA1416
}
