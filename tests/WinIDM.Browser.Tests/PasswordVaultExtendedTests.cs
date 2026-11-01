using System.Text;

namespace WinIDM.Browser.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class PasswordVaultExtendedTests : IDisposable
{
    private const string Pin = "482915";
    private const string Passphrase = "correct horse battery";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "winidm-vault-x-" + Guid.NewGuid().ToString("N"));
    private long _mono;
    private DateTimeOffset _utc = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    public PasswordVaultExtendedTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private PasswordVault Open(string name = "vault.db") =>
        new(Path.Combine(_dir, name), () => _mono, () => _utc);

    private void Advance(TimeSpan span)
    {
        _mono += (long)span.TotalMilliseconds;
        _utc += span;
    }

    private async Task<PasswordVault> OpenUnlockedAsync(string name = "vault.db")
    {
        var v = Open(name);
        await v.SetupAsync(Pin);
        return v;
    }

    // ---------------------------------------------------------------------
    // List, edit, search

    [Fact]
    public async Task List_all_returns_origin_username_and_dates()
    {
        using var vault = await OpenUnlockedAsync();
        await vault.AddAsync("https://b.example.com", "zed", "p1");
        Advance(TimeSpan.FromDays(1));
        Assert.True(await vault.UnlockAsync(Pin)); // session expired after 5 minutes idle
        await vault.AddAsync("https://a.example.com/login", "amy", "p2");

        var all = await vault.ListAllAsync();
        Assert.Equal(2, all.Count);
        Assert.Equal("https://a.example.com", all[0].Origin);
        Assert.Equal("amy", all[0].Username);
        Assert.True(all[0].HttpsOnly);
        Assert.Equal(_utc.ToUnixTimeSeconds(), all[0].Created.ToUnixTimeSeconds());
        Assert.True(all[1].Created < all[0].Created);
    }

    [Fact]
    public async Task Edit_changes_username_and_password_and_keeps_created_date()
    {
        using var vault = await OpenUnlockedAsync();
        await vault.AddAsync("https://site.example", "old", "pw1");
        var created = (await vault.ListAllAsync()).Single().Created;

        Advance(TimeSpan.FromDays(1));
        Assert.True(await vault.UnlockAsync(Pin)); // session expired after 5 minutes idle
        await vault.EditAsync("https://site.example", "old", "https://site.example", "new");
        Assert.Equal("pw1", await vault.GetPasswordAsync("https://site.example", "new"));

        await vault.EditAsync("https://site.example", "new", "https://site.example", "new", "pw2");
        var after = (await vault.ListAllAsync()).Single();
        Assert.Equal("new", after.Username);
        Assert.Equal(created.ToUnixTimeSeconds(), after.Created.ToUnixTimeSeconds());
        Assert.True(after.Modified > created);
        Assert.Equal("pw2", await vault.GetPasswordAsync("https://site.example", "new"));
        Assert.Null(await vault.GetPasswordAsync("https://site.example", "old"));
    }

    [Fact]
    public async Task Edit_missing_or_conflicting_entries_are_refused()
    {
        using var vault = await OpenUnlockedAsync();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => vault.EditAsync("https://x.example", "u", "https://x.example", "v"));

        await vault.AddAsync("https://c.example", "one", "a");
        await vault.AddAsync("https://c.example", "two", "b");
        await Assert.ThrowsAsync<InvalidOperationException>(() => vault.EditAsync("https://c.example", "one", "https://c.example", "two"));
    }

    [Fact]
    public async Task Search_matches_origin_and_username_but_never_password()
    {
        using var vault = await OpenUnlockedAsync();
        await vault.AddAsync("https://Mail.Example.org", "Carol@Mail", "secretXYZ");
        await vault.AddAsync("https://shop.test", "dave", "other");

        Assert.Single(await vault.SearchAsync("example.org"));
        Assert.Single(await vault.SearchAsync("CAROL"));
        Assert.Empty(await vault.SearchAsync("secretXYZ"));
        Assert.Equal(2, (await vault.SearchAsync("")).Count);
    }

    [Fact]
    public async Task Duplicate_passwords_are_grouped_without_exposing_them()
    {
        using var vault = await OpenUnlockedAsync();
        await vault.AddAsync("https://one.test", "u1", "SamePassword!1");
        await vault.AddAsync("https://two.test", "u2", "SamePassword!1");
        await vault.AddAsync("https://three.test", "u3", "Different!2");

        var groups = await vault.FindDuplicatePasswordsAsync();
        var group = Assert.Single(groups);
        Assert.Equal(2, group.Entries.Count);
        Assert.Contains(group.Entries, e => e.Origin == "https://one.test" && e.Username == "u1");
        Assert.Contains(group.Entries, e => e.Origin == "https://two.test" && e.Username == "u2");
    }

    // ---------------------------------------------------------------------
    // Autofill

    [Fact]
    public async Task Autofill_returns_all_accounts_for_https_origin_only()
    {
        using var vault = await OpenUnlockedAsync();
        await vault.AddAsync("https://login.example.com", "alice", "pa");
        await vault.AddAsync("https://login.example.com", "bob", "pb");
        await vault.AddAsync("https://other.example.com", "carol", "pc");

        var ok = await vault.AutofillAsync("https://login.example.com/signin");
        Assert.Equal(AutofillDecision.Allowed, ok.Decision);
        Assert.Equal(new[] { "alice", "bob" }, ok.Credentials.Select(c => c.Username).ToArray());
        Assert.Equal("pa", ok.Credentials[0].Password);

        var http = await vault.AutofillAsync("http://login.example.com/");
        Assert.Equal(AutofillDecision.NotHttps, http.Decision);
        Assert.Empty(http.Credentials);

        var mismatch = await vault.AutofillAsync("https://login.example.com/", "https://evil.example.net/post");
        Assert.Equal(AutofillDecision.FormActionMismatch, mismatch.Decision);
        Assert.Empty(mismatch.Credentials);

        var none = await vault.AutofillAsync("https://nowhere.test/");
        Assert.Equal(AutofillDecision.Allowed, none.Decision);
        Assert.Empty(none.Credentials);

        vault.Lock();
        var locked = await vault.AutofillAsync("https://login.example.com/");
        Assert.Equal(AutofillDecision.Locked, locked.Decision);
        Assert.Empty(locked.Credentials);
    }

    // ---------------------------------------------------------------------
    // Password strength

    [Fact]
    public void Strength_flags_common_and_repetitive_passwords()
    {
        Assert.Equal(0, PasswordStrength.Evaluate("password").Score);
        Assert.Equal(0, PasswordStrength.Evaluate("123456").Score);
        Assert.Equal(0, PasswordStrength.Evaluate("password2024").Score);
        Assert.True(PasswordStrength.Evaluate("abc").Score <= 1);

        var repeat = PasswordStrength.Evaluate("aaaaaaaaaaaa");
        Assert.True(repeat.Score <= 1);
        Assert.Contains(repeat.Warnings, w => w.Contains("repeated", StringComparison.Ordinal));

        Assert.True(PasswordStrength.Evaluate("Tr0ub4dor&3xq").Score >= 3);
        Assert.Equal(4, PasswordStrength.Evaluate("correct horse battery staple").Score);
    }

    // ---------------------------------------------------------------------
    // Export and import

    [Fact]
    public async Task Raw_export_needs_acknowledgement_and_warning_is_explicit()
    {
        using var vault = await OpenUnlockedAsync();
        await vault.AddAsync("https://raw.example", "rawuser", "Pw-raw-1");

        await Assert.ThrowsAsync<InvalidOperationException>(() => vault.ExportRawCsvAsync(false));
        Assert.Contains("WARNING", PasswordVault.RawExportWarning, StringComparison.Ordinal);

        string csv = await vault.ExportRawCsvAsync(true);
        Assert.Contains("Pw-raw-1", csv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Encrypted_export_hides_secrets_and_imports_into_another_vault()
    {
        using var source = await OpenUnlockedAsync("src.db");
        await source.AddAsync("https://bank.example.org", "frank", "BankSecret-QQ77");

        string envelope = await source.ExportEncryptedAsync(Passphrase);
        Assert.DoesNotContain("BankSecret-QQ77", envelope, StringComparison.Ordinal);
        Assert.DoesNotContain("frank", envelope, StringComparison.Ordinal);

        using var dest = await OpenUnlockedAsync("dst.db");
        var result = await dest.ImportEncryptedAsync(envelope, Passphrase);
        Assert.Equal(1, result.Imported);
        Assert.Equal("BankSecret-QQ77", await dest.GetPasswordAsync("https://bank.example.org", "frank"));
    }

    [Fact]
    public async Task Encrypted_import_rejects_wrong_passphrase_and_malformed_files()
    {
        using var source = await OpenUnlockedAsync("src2.db");
        await source.AddAsync("https://x.example", "u", "p");
        string envelope = await source.ExportEncryptedAsync(Passphrase);

        using var dest = await OpenUnlockedAsync("dst2.db");
        await Assert.ThrowsAsync<InvalidDataException>(() => dest.ImportEncryptedAsync(envelope, "wrong passphrase!!"));
        await Assert.ThrowsAsync<InvalidDataException>(() => dest.ImportEncryptedAsync("not json", Passphrase));
        await Assert.ThrowsAsync<InvalidDataException>(() => dest.ImportEncryptedAsync("{\"format\":\"other\"}", Passphrase));
        await Assert.ThrowsAsync<ArgumentException>(() => source.ExportEncryptedAsync("short"));
    }

    // ---------------------------------------------------------------------
    // Lockout survives wall-clock changes

    [Fact]
    public async Task Wall_clock_changes_within_one_boot_do_not_change_lockout()
    {
        using var vault = await OpenUnlockedAsync();
        vault.Lock();
        for (int i = 0; i < PasswordVault.MaxFailedAttemptsPerWindow; i++)
            Assert.False(await vault.UnlockAsync("000000"));

        var before = vault.LockoutRemaining;
        Assert.True(before > TimeSpan.Zero);

        _utc = _utc.AddHours(-1);
        Assert.Equal(before, vault.LockoutRemaining);

        _utc = _utc.AddHours(3);
        Assert.Equal(before, vault.LockoutRemaining);
        Assert.False(await vault.UnlockAsync(Pin));
    }

    [Fact]
    public async Task Lockout_persists_across_restart_using_monotonic_remaining()
    {
        using (var vault = await OpenUnlockedAsync())
        {
            vault.Lock();
            for (int i = 0; i < PasswordVault.MaxFailedAttemptsPerWindow; i++)
                await vault.UnlockAsync("000000");
        }

        Advance(TimeSpan.FromSeconds(10));
        using var reopened = Open();
        Assert.Equal(TimeSpan.FromSeconds(20), reopened.LockoutRemaining);
        Assert.False(await reopened.UnlockAsync(Pin));
    }

    [Fact]
    public async Task Reboot_resets_monotonic_clock_and_falls_back_to_stored_wall_deadline()
    {
        Advance(TimeSpan.FromMinutes(10));
        using var vault = await OpenUnlockedAsync();
        vault.Lock();
        for (int i = 0; i < PasswordVault.MaxFailedAttemptsPerWindow; i++)
            await vault.UnlockAsync("000000");

        // Simulate a reboot: the monotonic clock drops back to a small value, the wall clock advances 10 seconds.
        _mono = 1_000;
        _utc = _utc.AddSeconds(10);
        Assert.Equal(TimeSpan.FromSeconds(20), vault.LockoutRemaining);

        _utc = _utc.AddSeconds(25);
        Assert.Equal(TimeSpan.Zero, vault.LockoutRemaining);
        Assert.True(await vault.UnlockAsync(Pin));
    }

    [Fact]
    public void Raw_export_warning_mentions_plaintext_risk()
    {
        Assert.Contains("plain text", PasswordVault.RawExportWarning, StringComparison.Ordinal);
        Assert.True(Encoding.UTF8.GetByteCount(PasswordVault.RawExportWarning) > 0);
    }
}
