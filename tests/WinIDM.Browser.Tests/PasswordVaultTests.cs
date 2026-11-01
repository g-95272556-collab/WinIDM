using System.Text;
using Microsoft.Data.Sqlite;

namespace WinIDM.Browser.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class PasswordVaultTests : IDisposable
{
    private const string Pin = "482915";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "winidm-vault-" + Guid.NewGuid().ToString("N"));
    private long _mono;
    private DateTimeOffset _utc = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    public PasswordVaultTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string DbPath => Path.Combine(_dir, "vault.db");

    private PasswordVault Open() => new(DbPath, () => _mono, () => _utc);

    private void Advance(TimeSpan span)
    {
        _mono += (long)span.TotalMilliseconds;
        _utc += span;
    }

    [Fact]
    public async Task Setup_unlock_add_and_retrieve_credential()
    {
        using var vault = Open();
        Assert.False(vault.IsInitialized);
        await vault.SetupAsync(Pin);
        Assert.True(vault.IsInitialized);
        Assert.True(vault.IsUnlocked);

        await vault.AddAsync("https://Login.Example.com:443/signin", "alice", "s3cret!Pass");
        Assert.Equal("s3cret!Pass", await vault.GetPasswordAsync("https://login.example.com/other", "alice"));
        Assert.Null(await vault.GetPasswordAsync("https://login.example.com", "bob"));
        Assert.Null(await vault.GetPasswordAsync("https://other.example.com", "alice"));

        var list = await vault.ListAsync();
        Assert.Single(list);
        Assert.Equal(("https://login.example.com", "alice"), list[0]);

        await vault.AddAsync("https://login.example.com", "alice", "updated-pass");
        Assert.Equal("updated-pass", await vault.GetPasswordAsync("https://login.example.com", "alice"));
        Assert.Single(await vault.ListAsync());

        await vault.RemoveAsync("https://login.example.com", "alice");
        Assert.Empty(await vault.ListAsync());
    }

    [Fact]
    public async Task Stored_file_contains_no_plaintext_secrets()
    {
        using (var vault = Open())
        {
            await vault.SetupAsync(Pin);
            await vault.AddAsync("https://bank.example.org", "carol-unique-user", "PlaintextMarker-7f3a9");
        }
        byte[] raw = File.ReadAllBytes(DbPath);
        Assert.Equal(-1, IndexOf(raw, Encoding.UTF8.GetBytes("PlaintextMarker-7f3a9")));
        Assert.Equal(-1, IndexOf(raw, Encoding.UTF8.GetBytes("carol-unique-user")));
    }

    [Fact]
    public async Task Wrong_pin_fails_and_lockout_after_five_attempts_for_thirty_seconds()
    {
        using var vault = Open();
        await vault.SetupAsync(Pin);
        vault.Lock();
        Assert.False(vault.IsUnlocked);

        for (int i = 0; i < 4; i++)
            Assert.False(await vault.UnlockAsync("000000"));
        Assert.Equal(TimeSpan.Zero, vault.LockoutRemaining);

        Assert.False(await vault.UnlockAsync("000000"));
        Assert.True(vault.LockoutRemaining > TimeSpan.Zero);
        Assert.False(await vault.UnlockAsync(Pin));

        Advance(TimeSpan.FromSeconds(31));
        Assert.True(await vault.UnlockAsync(Pin));
        Assert.True(vault.IsUnlocked);
    }

    [Fact]
    public async Task Malformed_pin_is_rejected_without_key_derivation()
    {
        using var vault = Open();
        await vault.SetupAsync(Pin);
        vault.Lock();
        Assert.False(await vault.UnlockAsync("123"));
        Assert.False(await vault.UnlockAsync(new string('9', 17)));
        Assert.False(await vault.UnlockAsync(null!));
        Assert.Throws<ArgumentException>(() => { var _ = vault.SetupAsync("123"); });
    }

    [Fact]
    public async Task Session_expires_after_five_minutes_of_inactivity()
    {
        using var vault = Open();
        await vault.SetupAsync(Pin);
        await vault.AddAsync("https://a.example.com", "u", "p");
        Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        Assert.False(vault.IsUnlocked);
        await Assert.ThrowsAsync<InvalidOperationException>(() => vault.ListAsync());
        Assert.True(await vault.UnlockAsync(Pin));
        Assert.Equal("p", await vault.GetPasswordAsync("https://a.example.com", "u"));
    }

    [Fact]
    public async Task Data_persists_across_instances_and_wrong_pin_stays_locked()
    {
        using (var vault = Open())
        {
            await vault.SetupAsync(Pin);
            await vault.AddAsync("https://site.example.com", "dave", "persisted-pw");
        }

        using var reopened = Open();
        Assert.True(reopened.IsInitialized);
        Assert.False(reopened.IsUnlocked);
        Assert.True(await reopened.UnlockAsync(Pin));
        Assert.Equal("persisted-pw", await reopened.GetPasswordAsync("https://site.example.com", "dave"));
        Assert.Equal(TimeSpan.Zero, reopened.LockoutRemaining);
    }

    [Fact]
    public async Task Setup_twice_and_unlock_before_setup_are_errors()
    {
        using var vault = Open();
        await Assert.ThrowsAsync<InvalidOperationException>(() => vault.UnlockAsync(Pin));
        await vault.SetupAsync(Pin);
        await Assert.ThrowsAsync<InvalidOperationException>(() => vault.SetupAsync(Pin));
    }

    [Fact]
    public async Task Change_pin_rewraps_key_and_keeps_entries()
    {
        using var vault = Open();
        await vault.SetupAsync(Pin);
        await vault.AddAsync("https://keep.example.com", "erin", "kept");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => vault.ChangePinAsync("111111", "222222"));
        await vault.ChangePinAsync(Pin, "246810");

        vault.Lock();
        Assert.False(await vault.UnlockAsync(Pin));
        Assert.True(await vault.UnlockAsync("246810"));
        Assert.Equal("kept", await vault.GetPasswordAsync("https://keep.example.com", "erin"));
    }

    [Fact]
    public async Task Autofill_requires_https_and_matching_form_origin()
    {
        using var vault = Open();
        await vault.SetupAsync(Pin);
        await vault.AddAsync("https://secure.example.com", "frank", "pw");

        Assert.Equal(AutofillDecision.Allowed, vault.CheckAutofill("https://secure.example.com/login", null));
        Assert.Equal(AutofillDecision.Allowed, vault.CheckAutofill("https://secure.example.com/login", "https://secure.example.com/post"));
        Assert.Equal(AutofillDecision.NotHttps, vault.CheckAutofill("http://secure.example.com/login", null));
        Assert.Equal(AutofillDecision.FormActionMismatch, vault.CheckAutofill("https://secure.example.com/login", "https://evil.example.net/steal"));
        Assert.Equal(AutofillDecision.FormActionMismatch, vault.CheckAutofill("https://secure.example.com/login", "::not a url::"));
        Assert.Equal(AutofillDecision.NotHttps, vault.CheckAutofill("https://secure.example.com/login", "http://secure.example.com/post"));
        Assert.Equal(AutofillDecision.InvalidUrl, vault.CheckAutofill("ftp://secure.example.com/", null));

        vault.Lock();
        Assert.Equal(AutofillDecision.Locked, vault.CheckAutofill("https://secure.example.com/login", null));
    }

    [Fact]
    public async Task Http_origin_credentials_are_never_returned_and_invalid_origins_rejected()
    {
        using var vault = Open();
        await vault.SetupAsync(Pin);
        await vault.AddAsync("https://ok.example.com", "gina", "pw");
        Assert.Null(await vault.GetPasswordAsync("http://ok.example.com", "gina"));
        Assert.Null(await vault.GetPasswordAsync("javascript:alert(1)", "gina"));
        Assert.Throws<ArgumentException>(() => { var _ = vault.AddAsync("file:///C:/x", "u", "p"); });
        Assert.Throws<ArgumentNullException>(() => { var _ = vault.AddAsync("https://ok.example.com", null!, "p"); });
        Assert.Throws<ArgumentNullException>(() => { var _ = vault.AddAsync("https://ok.example.com", "u", null!); });
    }

    [Fact]
    public async Task Tampered_ciphertext_is_detected()
    {
        using (var vault = Open())
        {
            await vault.SetupAsync(Pin);
            await vault.AddAsync("https://tamper.example.com", "hank", "original");
        }

        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath }.ToString()))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            // Replace the last 8 base64 characters (part of the GCM tag) with a different value that is still valid base64.
            cmd.CommandText = "UPDATE passwords SET payload = '1:' || substr(payload, 3, length(payload) - 10) || 'AAAAAAAA';";
            cmd.ExecuteNonQuery();
        }

        using var vault2 = Open();
        Assert.True(await vault2.UnlockAsync(Pin));
        await Assert.ThrowsAsync<InvalidDataException>(() => vault2.GetPasswordAsync("https://tamper.example.com", "hank"));
    }

    [Fact]
    public async Task Unsupported_payload_version_is_rejected()
    {
        using (var vault = Open())
        {
            await vault.SetupAsync(Pin);
            await vault.AddAsync("https://v.example.com", "ivy", "pw");
        }
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath }.ToString()))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE passwords SET payload = '9:xx';";
            cmd.ExecuteNonQuery();
        }
        using var vault2 = Open();
        await vault2.UnlockAsync(Pin);
        await Assert.ThrowsAsync<InvalidDataException>(() => vault2.GetPasswordAsync("https://v.example.com", "ivy"));
    }

    [Fact]
    public async Task Csv_import_detects_chrome_firefox_safari_bitwarden_and_onepassword()
    {
        using var vault = Open();
        await vault.SetupAsync(Pin);

        const string chrome = "name,url,username,password,note\r\n" +
                              "example,https://www.example.com/login,jo,\"p,w \"\"q\"\"\",\r\n" +
                              "android,android://abc@com.app/,x,y,\r\n" +
                              "empty,https://nopass.test,z,,\r\n";
        var r1 = await vault.ImportCsvAsync(chrome);
        Assert.Equal("Chrome", r1.Format);
        Assert.Equal(1, r1.Imported);
        Assert.Equal(2, r1.Skipped);
        Assert.Equal("p,w \"q\"", await vault.GetPasswordAsync("https://www.example.com", "jo"));

        const string firefox = "url,username,password,httpRealm,formActionOrigin,guid,timeCreated,timeLastUsed,timePasswordChanged\n" +
                               "https://ff.example.org,kim,ffpass,,https://ff.example.org,{1},1,2,3\n";
        var r2 = await vault.ImportCsvAsync(firefox);
        Assert.Equal("Firefox", r2.Format);
        Assert.Equal(1, r2.Imported);

        const string safari = "Title,URL,Username,Password,Notes\nSite,https://sa.example.net/,lee,safepw,\n";
        var r3 = await vault.ImportCsvAsync(safari);
        Assert.Equal("Safari", r3.Format);
        Assert.Equal(1, r3.Imported);

        const string bitwarden = "folder,favorite,type,name,notes,fields,reminder_date,reminder_lead_days,login_uri,login_username,login_password,login_totp\n" +
                                 ",0,login,Bw,,,,,\"https://bw.example.io\nhttps://bw2.example.io\",mo,bwpass,\n" +
                                 ",0,note,Note,,,,,,,,\n";
        var r4 = await vault.ImportCsvAsync(bitwarden);
        Assert.Equal("Bitwarden", r4.Format);
        Assert.Equal(2, r4.Imported);
        Assert.Equal(1, r4.Skipped);

        const string onePassword = "Title,Website,Username,Password,OTPAuth,Favorite,Archived,Tags,Notes\n" +
                                   "OP,https://op.example.dev,ni,oppass,,0,0,,\n";
        var r5 = await vault.ImportCsvAsync(onePassword);
        Assert.Equal("1Password", r5.Format);
        Assert.Equal(1, r5.Imported);

        Assert.Equal("bwpass", await vault.GetPasswordAsync("https://bw2.example.io", "mo"));
        Assert.Equal("oppass", await vault.GetPasswordAsync("https://op.example.dev", "ni"));
    }

    [Fact]
    public async Task Csv_import_rejects_unknown_format_and_unterminated_quotes()
    {
        using var vault = Open();
        await vault.SetupAsync(Pin);
        await Assert.ThrowsAsync<FormatException>(() => vault.ImportCsvAsync("a,b,c\n1,2,3\n"));
        await Assert.ThrowsAsync<FormatException>(() => vault.ImportCsvAsync("url,username,password\n\"unterminated,a,b\n"));
        await Assert.ThrowsAsync<FormatException>(() => vault.ImportCsvAsync(""));
    }

    [Fact]
    public async Task Raw_export_requires_explicit_acknowledgement()
    {
        using var vault = Open();
        await vault.SetupAsync(Pin);
        await vault.AddAsync("https://exp.example.com", "joy", "exp,\"pw\"");

        await Assert.ThrowsAsync<InvalidOperationException>(() => vault.ExportRawCsvAsync(false));
        string csv = await vault.ExportRawCsvAsync(true);
        Assert.StartsWith("name,url,username,password\r\n", csv);
        Assert.Contains("\"exp,\"\"pw\"\"\"", csv);
    }

    [Fact]
    public void Password_generator_guarantees_each_selected_set_and_length()
    {
        string pw = PasswordGenerator.Generate(24);
        Assert.Equal(24, pw.Length);
        Assert.Contains(pw, c => char.IsUpper(c));
        Assert.Contains(pw, c => char.IsLower(c));
        Assert.Contains(pw, c => char.IsDigit(c));
        Assert.Contains(pw, c => "!@#$%^&*()-_=+[]{};:,.<>?".Contains(c));

        string digitsOnly = PasswordGenerator.Generate(16, new PasswordGeneratorOptions(Lowercase: false, Uppercase: false, Digits: true, Symbols: false));
        Assert.All(digitsOnly, c => Assert.True(char.IsDigit(c)));

        Assert.Throws<ArgumentOutOfRangeException>(() => PasswordGenerator.Generate(4));
        Assert.Throws<ArgumentOutOfRangeException>(() => PasswordGenerator.Generate(300));
        Assert.Throws<ArgumentException>(() => PasswordGenerator.Generate(16, new PasswordGeneratorOptions(false, false, false, false)));
    }

    [Fact]
    public void Password_generator_produces_varied_output()
    {
        var set = new HashSet<string>();
        for (int i = 0; i < 50; i++) set.Add(PasswordGenerator.Generate(20));
        Assert.Equal(50, set.Count);
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length && match; j++) match = haystack[i + j] == needle[j];
            if (match) return i;
        }
        return -1;
    }
}
