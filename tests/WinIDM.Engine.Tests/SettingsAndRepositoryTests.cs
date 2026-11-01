using WinIDM.Core.Models;
using WinIDM.Engine;
using WinIDM.Engine.Tests.Support;

namespace WinIDM.Engine.Tests;

public sealed class JsonSettingsStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string PathFor(string name) => System.IO.Path.Combine(_temp.Path, name);

    [Fact]
    public void MissingKey_ReturnsDefault()
    {
        var store = new JsonSettingsStore(PathFor("s.json"));

        Assert.Equal(SettingDefaults.DownloadPart, store.Get(SettingKeys.DownloadPart, SettingDefaults.DownloadPart));
        Assert.Equal("fallback", store.Get("missing", "fallback"));
    }

    [Fact]
    public void SetAndGet_RoundTripsPrimitiveAndEnumTypes()
    {
        var store = new JsonSettingsStore(PathFor("s.json"));

        store.Set("flag", true);
        store.Set("count", 42);
        store.Set("big", 9_000_000_000L);
        store.Set("ratio", 1.5);
        store.Set("text", "hello world");
        store.Set("state", DownloadState.Paused);

        Assert.True(store.Get("flag", false));
        Assert.Equal(42, store.Get("count", 0));
        Assert.Equal(9_000_000_000L, store.Get("big", 0L));
        Assert.Equal(1.5, store.Get("ratio", 0.0));
        Assert.Equal("hello world", store.Get("text", ""));
        Assert.Equal(DownloadState.Paused, store.Get("state", DownloadState.Queued));
    }

    [Fact]
    public void UnparsableValue_FallsBackToDefault()
    {
        var path = PathFor("s.json");
        File.WriteAllText(path, "{\"idm_pref_download_part\":\"many\"}");
        var store = new JsonSettingsStore(path);

        Assert.Equal(SettingDefaults.DownloadPart, store.Get(SettingKeys.DownloadPart, SettingDefaults.DownloadPart));
    }

    [Fact]
    public void Values_PersistAcrossInstances_AndNullRemovesKey()
    {
        var path = PathFor("s.json");
        var first = new JsonSettingsStore(path);
        first.Set(SettingKeys.Timeout, 90);
        first.Set("temp", "x");
        first.Set<string?>("temp", null);

        var second = new JsonSettingsStore(path);
        Assert.Equal(90, second.Get(SettingKeys.Timeout, 0));
        Assert.False(second.Snapshot().ContainsKey("temp"));
    }

    [Fact]
    public void Save_IsAtomic_NoTempFileLeftBehind()
    {
        var path = PathFor("s.json");
        var store = new JsonSettingsStore(path);
        store.Set("a", 1);

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void CorruptFile_IsBackedUpAndStartsEmpty()
    {
        var path = PathFor("s.json");
        File.WriteAllText(path, "{ not json");

        var store = new JsonSettingsStore(path);

        Assert.Empty(store.Snapshot());
        Assert.True(File.Exists(path + ".corrupt"));
    }

    [Fact]
    public void Snapshot_And_Restore_ReplaceAllValues()
    {
        var store = new JsonSettingsStore(PathFor("s.json"));
        store.Set("a", "1");
        var snapshot = store.Snapshot();

        store.Set("b", "2");
        store.Restore(snapshot);

        Assert.Equal("1", store.Get("a", ""));
        Assert.False(store.Snapshot().ContainsKey("b"));
    }

    [Fact]
    public void ChangedEvent_RaisedForSetAndRestore()
    {
        var store = new JsonSettingsStore(PathFor("s.json"));
        var keys = new List<string>();
        store.Changed += keys.Add;

        store.Set("k1", 1);
        store.Restore(new Dictionary<string, string> { ["k2"] = "v" });

        Assert.Contains("k1", keys);
        Assert.Contains("k2", keys);
    }

    [Fact]
    public async Task ConcurrentWrites_AreThreadSafe()
    {
        var store = new JsonSettingsStore(PathFor("s.json"));

        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 25; i++) store.Set($"t{t}_{i}", i);
        })));

        Assert.Equal(200, store.Snapshot().Count);
        Assert.Equal(24, new JsonSettingsStore(PathFor("s.json")).Get("t3_24", -1));
    }

    [Fact]
    public void Defaults_MatchSpecification03a()
    {
        Assert.Equal(2, SettingDefaults.DownloadCount);
        Assert.Equal(7, SettingDefaults.DownloadPart);
        Assert.Equal(60, SettingDefaults.TimeoutSeconds);
        Assert.Equal(999999, SettingDefaults.RetryCount);
        Assert.Equal(5, SettingDefaults.RetryIntervalSeconds);
        Assert.False(SettingDefaults.WifiOnly);
        Assert.True(SettingDefaults.AdvanceDownloadMethod);
        Assert.True(SettingDefaults.CreateEmptyFile);
    }
}

public sealed class SqliteDownloadRepositoryTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string DbPath => System.IO.Path.Combine(_temp.Path, "data", "downloads.db");

    private static DownloadItem SampleItem() => new()
    {
        Kind = DownloadKind.Http,
        Url = "https://example.test/file.zip",
        FinalUrl = "https://cdn.example.test/file.zip",
        FileName = "file.zip",
        SaveDirectory = @"C:\Downloads",
        TotalBytes = 1234,
        DownloadedBytes = 500,
        State = DownloadState.Paused,
        Category = "Archive",
        Referer = "https://example.test/",
        UserAgent = "UA-Test",
        CookieHeader = "sid=abc",
        Headers = new Dictionary<string, string> { ["X-Test"] = "1" },
        Username = "user",
        Password = "secret-123",
        MaxSegments = 8,
        SpeedLimitBytesPerSec = 4096,
        AcceptsRanges = true,
        ETag = "\"e1\"",
        LastModified = "Wed, 21 Oct 2015 07:28:00 GMT",
        ExpectedSha256 = "abcd",
        ErrorCode = 130,
        ErrorMessage = "test error",
        SortOrder = 3,
        ScheduledAt = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000),
        CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(1_690_000_000_000),
    };

    [Fact]
    public async Task InsertGetUpdate_RoundTripsAllFields()
    {
        using var repo = new SqliteDownloadRepository(DbPath);
        var item = SampleItem();

        var id = await repo.InsertAsync(item);
        var loaded = await repo.GetAsync(id);

        Assert.NotNull(loaded);
        Assert.Equal(id, loaded!.Id);
        Assert.Equal(item.FinalUrl, loaded.FinalUrl);
        Assert.Equal(item.Headers["X-Test"], loaded.Headers["X-Test"]);
        Assert.Equal("secret-123", loaded.Password);
        Assert.Equal(item.ScheduledAt, loaded.ScheduledAt);
        Assert.Equal(item.CreatedAt, loaded.CreatedAt);
        Assert.Null(loaded.CompletedAt);
        Assert.True(loaded.AcceptsRanges);

        loaded.State = DownloadState.Completed;
        loaded.CompletedAt = DateTimeOffset.FromUnixTimeMilliseconds(1_710_000_000_000);
        loaded.Headers.Clear();
        await repo.UpdateAsync(loaded);

        var updated = await repo.GetAsync(id);
        Assert.Equal(DownloadState.Completed, updated!.State);
        Assert.Equal(loaded.CompletedAt, updated.CompletedAt);
        Assert.Empty(updated.Headers);
    }

    [Fact]
    public async Task Password_IsStoredProtected_NotPlaintext()
    {
        using var repo = new SqliteDownloadRepository(DbPath);
        var id = await repo.InsertAsync(SampleItem());

        using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={DbPath}"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = "SELECT password_protected FROM downloads WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            var stored = (string)cmd.ExecuteScalar()!;
            Assert.DoesNotContain("secret-123", stored);
            Assert.StartsWith("dpapi1:", stored);
        }
    }

    [Fact]
    public async Task ListAsync_OrdersBySortOrderThenId()
    {
        using var repo = new SqliteDownloadRepository(DbPath);
        var late = SampleItem();
        late.SortOrder = 5;
        var early = SampleItem();
        early.SortOrder = 1;
        await repo.InsertAsync(late);
        await repo.InsertAsync(early);

        var list = await repo.ListAsync();

        Assert.Equal(1, list[0].SortOrder);
        Assert.Equal(5, list[1].SortOrder);
    }

    [Fact]
    public async Task Delete_RemovesDownloadAndCascadesSegments()
    {
        using var repo = new SqliteDownloadRepository(DbPath);
        var id = await repo.InsertAsync(SampleItem());
        await repo.SaveSegmentsAsync(id, [new SegmentInfo { Index = 0, Start = 0, End = 9, Downloaded = 3 }]);

        await repo.DeleteAsync(id);

        Assert.Null(await repo.GetAsync(id));
        Assert.Empty(await repo.LoadSegmentsAsync(id));
    }

    [Fact]
    public async Task Segments_SaveReplacesAndLoadsInOrder()
    {
        using var repo = new SqliteDownloadRepository(DbPath);
        var id = await repo.InsertAsync(SampleItem());

        await repo.SaveSegmentsAsync(id,
        [
            new SegmentInfo { Index = 1, Start = 50, End = 99, Downloaded = 10, State = SegmentState.Active },
            new SegmentInfo { Index = 0, Start = 0, End = 49, Downloaded = 49, State = SegmentState.Done },
        ]);
        await repo.SaveSegmentsAsync(id, [new SegmentInfo { Index = 0, Start = 0, End = 99, Downloaded = 7 }]);

        var loaded = await repo.LoadSegmentsAsync(id);
        Assert.Single(loaded);
        Assert.Equal(99, loaded[0].End);
        Assert.Equal(7, loaded[0].Downloaded);
        Assert.Equal(id, loaded[0].DownloadId);
    }

    [Fact]
    public async Task DataSurvivesReopen()
    {
        long id;
        using (var repo = new SqliteDownloadRepository(DbPath))
        {
            id = await repo.InsertAsync(SampleItem());
        }

        using var reopened = new SqliteDownloadRepository(DbPath);
        var item = await reopened.GetAsync(id);
        Assert.Equal("file.zip", item!.FileName);
        Assert.Equal(SqliteDownloadRepository.SchemaVersion, reopened.CurrentSchemaVersion);
    }

    [Fact]
    public async Task Migration_UpgradesVersionOneDatabaseToCurrent()
    {
        using (var repo = new SqliteDownloadRepository(DbPath))
        {
            await repo.InsertAsync(SampleItem());
        }

        // Simulate a v1 database: drop the v2 tables and downgrade the version.
        using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={DbPath}"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText =
                "DROP INDEX idx_downloads_state; DROP TABLE site_limits; " +
                "ALTER TABLE site_settings DROP COLUMN save_directory; " +
                "ALTER TABLE site_settings DROP COLUMN cookies_protected; " +
                "ALTER TABLE site_settings DROP COLUMN use_for_subdomain; " +
                "PRAGMA user_version = 1;";
            cmd.ExecuteNonQuery();
        }

        using var migrated = new SqliteDownloadRepository(DbPath);
        Assert.Equal(SqliteDownloadRepository.SchemaVersion, migrated.CurrentSchemaVersion);
        await migrated.SetSiteLimitAsync(new SiteLimit("example.test", 2, true, false));
        Assert.Single(await migrated.ListAsync());
    }

    [Fact]
    public void NewerSchemaThanSupported_Throws()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(DbPath)!);
        using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={DbPath}"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = "PRAGMA user_version = 99;";
            cmd.ExecuteNonQuery();
        }

        Assert.Throws<InvalidOperationException>(() => new SqliteDownloadRepository(DbPath));
    }

    [Fact]
    public async Task CredentialsAndDomainLimits_MatchHostsAndSubdomains()
    {
        using var repo = new SqliteDownloadRepository(DbPath);

        await repo.SetCredentialAsync("example.test", "ali", "test-password", useForSubdomain: true);
        await repo.SetDomainPartLimitAsync("example.test", 2, useForSubdomain: true);
        await repo.SetDomainPartLimitAsync("exact.test", 1, useForSubdomain: false);

        var credential = await repo.GetCredentialAsync("cdn.example.test");
        Assert.Equal(("ali", "test-password"), credential);
        Assert.Null(await repo.GetCredentialAsync("other.test"));

        Assert.Equal(2, await repo.GetDomainPartLimitAsync("files.example.test"));
        Assert.Equal(1, await repo.GetDomainPartLimitAsync("exact.test"));
        Assert.Equal(0, await repo.GetDomainPartLimitAsync("sub.exact.test"));
        Assert.Equal(0, await repo.GetDomainPartLimitAsync(""));
    }

    [Fact]
    public async Task SiteLimits_UseLongestMatchingDomain()
    {
        using var repo = new SqliteDownloadRepository(DbPath);
        await repo.SetSiteLimitAsync(new SiteLimit("example.test", 3, true, false));
        await repo.SetSiteLimitAsync(new SiteLimit("video.example.test", 1, true, true));

        var limit = await repo.GetSiteLimitAsync("a.video.example.test");

        Assert.NotNull(limit);
        Assert.Equal(1, limit!.Limit);
        Assert.True(limit.RefererLink);
        Assert.Null(await repo.GetSiteLimitAsync("other.test"));
        Assert.Null(await repo.GetSiteLimitAsync(""));
    }
}
