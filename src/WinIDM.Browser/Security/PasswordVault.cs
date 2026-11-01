using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WinIDM.Core.Contracts;

namespace WinIDM.Browser;

/// <summary>Autofill check result.</summary>
public enum AutofillDecision
{
    Allowed,
    Locked,
    InvalidUrl,
    NotHttps,
    FormActionMismatch,
}

/// <summary>One credential imported from a CSV file.</summary>
public sealed record ImportedCredential(string Origin, string Username, string Password);

/// <summary>CSV import summary.</summary>
public sealed record CsvImportResult(string Format, int Imported, int Skipped);

/// <summary>Record for UI display. The password is not included.</summary>
public sealed record VaultEntry(string Origin, string Username, bool HttpsOnly, DateTimeOffset Created, DateTimeOffset Modified);

/// <summary>Key for one entry (origin and user name).</summary>
public sealed record VaultEntryKey(string Origin, string Username);

/// <summary>Group of entries that share the same password.</summary>
public sealed record DuplicatePasswordGroup(IReadOnlyList<VaultEntryKey> Entries);

/// <summary>Credential for autofill. Returned only after the origin and form are validated.</summary>
public sealed record AutofillCredential(string Username, string Password);

/// <summary>Autofill result: the check decision and the list of matching accounts (there may be more than one).</summary>
public sealed record AutofillResult(AutofillDecision Decision, IReadOnlyList<AutofillCredential> Credentials);

/// <summary>
/// Encrypted password vault (SQLite). The master key is wrapped by the PIN (PBKDF2-HMAC-SHA256, 600000
/// iterations) and that wrapping is protected by DPAPI for the current Windows account. Passwords are never
/// stored in plain text. Lockout: after every 5 failed attempts, lock for 30 seconds.
/// The session expires after 5 minutes of inactivity.
/// </summary>
/// <remarks>
/// Lockout is stored as the remaining duration together with a monotonic clock stamp (<see cref="Environment.TickCount64"/>),
/// not as wall-clock time. Changes to the system clock during the same boot do not change the remainder.
/// If the monotonic clock is reset (reboot), the remainder is taken from the stored wall-clock time.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class PasswordVault : IPasswordVault, IDisposable
{
    public const int MinPinLength = 6;
    public const int MaxPinLength = 16;
    public const int MaxFailedAttemptsPerWindow = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan SessionTimeout = TimeSpan.FromMinutes(5);
    public const int MinExportPassphraseLength = 12;

    /// <summary>Warning that must be shown to the user before a raw CSV export.</summary>
    public const string RawExportWarning =
        "WARNING: The CSV file is not encrypted. All user names and passwords will be stored in plain text. " +
        "Anyone who has this file can sign in to your accounts. Keep it in a safe place and delete it after use.";

    private const string ExportFormat = "winidm-vault-export";
    private const int ExportVersion = 1;

    private const string MetaSalt = "salt";
    private const string MetaIterations = "kdf_iterations";
    private const string MetaKeyBlob = "dpapi_key";
    private const string MetaFailed = "failed_attempts";
    private const string MetaFormat = "format";
    private const string MetaLockoutRemaining = "lockout_remaining_ms";
    private const string MetaLockoutMonoAt = "lockout_mono_at_ms";
    private const string MetaLockoutWallUntil = "lockout_wall_until_ms";

    private readonly object _gate = new();
    private readonly SqliteConnection _db;
    private readonly Func<long> _monotonicMs;
    private readonly Func<DateTimeOffset> _utcNow;
    private byte[]? _masterKey;
    private long _lastActivityMs;
    private int _failedAttempts;
    private long _lockoutRemainingAtStoreMs;
    private long _lockoutMonoAtStoreMs;
    private long _lockoutWallUntilMs;
    private bool _disposed;

    public PasswordVault(string filePath) : this(filePath, null, null) { }

    /// <summary>Constructor with clocks for tests (lockout and session expiry).</summary>
    internal PasswordVault(string filePath, Func<long>? monotonicMs, Func<DateTimeOffset>? utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _monotonicMs = monotonicMs ?? (() => Environment.TickCount64);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);

        string full = Path.GetFullPath(filePath);
        string? dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Pooling is disabled so the vault file is fully released after Dispose (for example for data deletion).
        _db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = full,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        _db.Open();
        using (var pragma = _db.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA secure_delete = ON;";
            pragma.ExecuteNonQuery();
        }
        CreateSchema();

        _failedAttempts = (int)(ReadMetaLong(MetaFailed) ?? 0);
        LoadLockoutLocked();
        // Old vault (before the lockout remainder was stored): lock again if the failed count is at the limit.
        if (_lockoutRemainingAtStoreMs <= 0 && _failedAttempts > 0 && _failedAttempts % MaxFailedAttemptsPerWindow == 0)
            StartLockoutLocked();
    }

    public bool IsInitialized
    {
        get
        {
            lock (_gate) return ReadMeta(MetaKeyBlob) is not null;
        }
    }

    /// <summary>True if the vault is unlocked and the session has not expired.</summary>
    public bool IsUnlocked
    {
        get
        {
            lock (_gate)
            {
                return IsSessionActiveLocked();
            }
        }
    }

    /// <summary>Remaining lockout time. Zero means no lockout.</summary>
    public TimeSpan LockoutRemaining
    {
        get
        {
            lock (_gate) return TimeSpan.FromMilliseconds(LockoutRemainingMsLocked());
        }
    }

    /// <summary>Set up the vault for the first time with a PIN.</summary>
    public Task SetupAsync(string pin, CancellationToken ct = default)
    {
        ValidatePin(pin);
        lock (_gate)
        {
            if (ReadMeta(MetaKeyBlob) is not null)
                throw new InvalidOperationException("The vault is already set up.");

            byte[] masterKey = VaultCrypto.NewMasterKey();
            byte[] salt = VaultCrypto.NewSalt();
            byte[] kek = VaultCrypto.DeriveKek(pin, salt, VaultCrypto.MinIterations);
            try
            {
                byte[] wrapped = VaultCrypto.Seal(kek, masterKey, Aad("master-key"));
                byte[] protectedBlob = VaultCrypto.ProtectForUser(wrapped);
                InTransaction(() =>
                {
                    WriteMeta(MetaFormat, "1");
                    WriteMeta(MetaSalt, salt);
                    WriteMeta(MetaIterations, Encoding.ASCII.GetBytes(VaultCrypto.MinIterations.ToString()));
                    WriteMeta(MetaKeyBlob, protectedBlob);
                    WriteMetaLong(MetaFailed, 0);
                    ClearLockoutLocked();
                });
            }
            finally
            {
                CryptographicOperations.ZeroMemory(kek);
            }
            SetMasterKeyLocked(masterKey);
            _failedAttempts = 0;
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Unlock the vault with the PIN. Returns false if the PIN is wrong or the vault is locked because of
    /// too many attempts. Configuration errors (vault not set up) are thrown.
    /// </summary>
    public Task<bool> UnlockAsync(string pin, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (pin is null || pin.Length < MinPinLength || pin.Length > MaxPinLength) return Task.FromResult(false);

        lock (_gate)
        {
            if (!IsInitializedLocked()) throw new InvalidOperationException("The vault is not set up.");

            if (LockoutRemainingMsLocked() > 0) return Task.FromResult(false);

            byte[] salt = ReadMeta(MetaSalt) ?? throw new InvalidDataException("Vault metadata is incomplete.");
            int iterations = ParseIterations(ReadMeta(MetaIterations));
            byte[] protectedBlob = ReadMeta(MetaKeyBlob) ?? throw new InvalidDataException("Vault metadata is incomplete.");

            byte[] wrapped;
            try
            {
                wrapped = VaultCrypto.UnprotectForUser(protectedBlob);
            }
            catch (CryptographicException)
            {
                throw new InvalidOperationException("The vault cannot be opened by this Windows account.");
            }

            byte[] kek = VaultCrypto.DeriveKek(pin, salt, iterations);
            try
            {
                byte[] masterKey = VaultCrypto.Open(kek, wrapped, Aad("master-key"));
                _failedAttempts = 0;
                WriteMetaLong(MetaFailed, 0);
                ClearLockoutLocked();
                SetMasterKeyLocked(masterKey);
                return Task.FromResult(true);
            }
            catch (CryptographicException)
            {
                _failedAttempts++;
                WriteMetaLong(MetaFailed, _failedAttempts);
                if (_failedAttempts % MaxFailedAttemptsPerWindow == 0)
                    StartLockoutLocked();
                return Task.FromResult(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(kek);
            }
        }
    }

    /// <summary>Lock the vault and erase the master key from memory.</summary>
    public void Lock()
    {
        lock (_gate) ClearMasterKeyLocked();
    }

    /// <summary>Change the PIN. The old PIN must be correct; the master key stays the same.</summary>
    public Task ChangePinAsync(string oldPin, string newPin, CancellationToken ct = default)
    {
        ValidatePin(newPin);
        lock (_gate)
        {
            if (!IsSessionActiveLocked()) throw new InvalidOperationException("The vault is locked.");
            if (!PinMatches(oldPin)) throw new UnauthorizedAccessException("The old PIN is incorrect.");

            byte[] masterKey = _masterKey!;
            byte[] salt = VaultCrypto.NewSalt();
            byte[] kek = VaultCrypto.DeriveKek(newPin, salt, VaultCrypto.MinIterations);
            try
            {
                byte[] wrapped = VaultCrypto.Seal(kek, masterKey, Aad("master-key"));
                byte[] protectedBlob = VaultCrypto.ProtectForUser(wrapped);
                InTransaction(() =>
                {
                    WriteMeta(MetaSalt, salt);
                    WriteMeta(MetaIterations, Encoding.ASCII.GetBytes(VaultCrypto.MinIterations.ToString()));
                    WriteMeta(MetaKeyBlob, protectedBlob);
                });
            }
            finally
            {
                CryptographicOperations.ZeroMemory(kek);
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>Add or update the credential for an origin and user name.</summary>
    public Task AddAsync(string origin, string username, string password, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(username);
        ArgumentNullException.ThrowIfNull(password);
        if (!OriginNormalizer.TryNormalize(origin, out var normalized))
            throw new ArgumentException("Origin must be an http(s) URL.", nameof(origin));

        lock (_gate)
        {
            EnsureUnlockedLocked();
            string binding = VaultCrypto.BindingId(_masterKey!, normalized.Origin, username);
            string json = JsonSerializer.Serialize(new EntryPayload(username, password));
            string payload = VaultCrypto.SealToPayload(_masterKey!, json, Encoding.UTF8.GetBytes(normalized.Origin + "|" + binding));
            long now = _utcNow().ToUnixTimeSeconds();

            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"
INSERT INTO passwords(binding_id, domain, https_only, payload, created, modified)
VALUES ($binding, $domain, $https, $payload, $now, $now)
ON CONFLICT(binding_id) DO UPDATE SET payload = excluded.payload, modified = excluded.modified;";
            cmd.Parameters.AddWithValue("$binding", binding);
            cmd.Parameters.AddWithValue("$domain", normalized.Origin);
            cmd.Parameters.AddWithValue("$https", 1);
            cmd.Parameters.AddWithValue("$payload", payload);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
            Touch();
        }
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string origin, string username, CancellationToken ct = default)
    {
        if (!OriginNormalizer.TryNormalize(origin, out var normalized)) return Task.CompletedTask;
        lock (_gate)
        {
            EnsureUnlockedLocked();
            string binding = VaultCrypto.BindingId(_masterKey!, normalized.Origin, username);
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM passwords WHERE binding_id = $binding;";
            cmd.Parameters.AddWithValue("$binding", binding);
            cmd.ExecuteNonQuery();
            Touch();
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Edit one entry: change the origin and/or user name, and/or the password (if <paramref name="newPassword"/> is given).
    /// The creation date is preserved. Errors if the source entry is missing, or the target already exists as another entry.
    /// </summary>
    public Task EditAsync(string origin, string username, string newOrigin, string newUsername, string? newPassword = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(username);
        ArgumentNullException.ThrowIfNull(newUsername);
        if (!OriginNormalizer.TryNormalize(origin, out var from)) throw new ArgumentException("The original origin must be an http(s) URL.", nameof(origin));
        if (!OriginNormalizer.TryNormalize(newOrigin, out var to)) throw new ArgumentException("The new origin must be an http(s) URL.", nameof(newOrigin));

        lock (_gate)
        {
            EnsureUnlockedLocked();
            string oldBinding = VaultCrypto.BindingId(_masterKey!, from.Origin, username);
            string newBinding = VaultCrypto.BindingId(_masterKey!, to.Origin, newUsername);

            var row = ReadRowLocked(oldBinding, from.Origin) ?? throw new KeyNotFoundException("Entry not found.");
            var current = DecryptEntry(from.Origin, oldBinding, row.Payload);
            string password = newPassword ?? current.Password;

            if (newBinding != oldBinding && ReadRowLocked(newBinding, to.Origin) is not null)
                throw new InvalidOperationException("The target entry already exists.");

            string json = JsonSerializer.Serialize(new EntryPayload(newUsername, password));
            string payload = VaultCrypto.SealToPayload(_masterKey!, json, Encoding.UTF8.GetBytes(to.Origin + "|" + newBinding));

            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"
UPDATE passwords SET binding_id = $nb, domain = $nd, payload = $payload, modified = $now
WHERE binding_id = $ob AND domain = $od;";
            cmd.Parameters.AddWithValue("$nb", newBinding);
            cmd.Parameters.AddWithValue("$nd", to.Origin);
            cmd.Parameters.AddWithValue("$payload", payload);
            cmd.Parameters.AddWithValue("$now", _utcNow().ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$ob", oldBinding);
            cmd.Parameters.AddWithValue("$od", from.Origin);
            cmd.ExecuteNonQuery();
            Touch();
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<(string Origin, string Username)>> ListAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            EnsureUnlockedLocked();
            var result = new List<(string Origin, string Username)>();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT binding_id, domain, payload FROM passwords ORDER BY domain, binding_id;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string binding = reader.GetString(0);
                string origin = reader.GetString(1);
                var entry = DecryptEntry(origin, binding, reader.GetString(2));
                result.Add((origin, entry.Username));
            }
            Touch();
            return Task.FromResult<IReadOnlyList<(string Origin, string Username)>>(result);
        }
    }

    /// <summary>List all entries for the UI: origin, user name, and dates. The password is not returned.</summary>
    public Task<IReadOnlyList<VaultEntry>> ListAllAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            EnsureUnlockedLocked();
            var result = ReadAllRowsLocked()
                .Select(r => new VaultEntry(r.Origin, DecryptEntry(r.Origin, r.Binding, r.Payload).Username,
                    r.HttpsOnly, r.Created, r.Modified))
                .OrderBy(e => e.Origin, StringComparer.Ordinal)
                .ThenBy(e => e.Username, StringComparer.Ordinal)
                .ToList();
            Touch();
            return Task.FromResult<IReadOnlyList<VaultEntry>>(result);
        }
    }

    /// <summary>
    /// Case-insensitive search on the origin and user name. Passwords are not searched.
    /// </summary>
    public async Task<IReadOnlyList<VaultEntry>> SearchAsync(string query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        string q = query.Trim();
        var all = await ListAllAsync(ct).ConfigureAwait(false);
        if (q.Length == 0) return all;
        return all.Where(e =>
            e.Origin.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            e.Username.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// Identify entries that share the same password. The comparison uses a keyed HMAC
    /// fingerprint, so passwords are not compared in plain text and are not returned.
    /// </summary>
    public Task<IReadOnlyList<DuplicatePasswordGroup>> FindDuplicatePasswordsAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            EnsureUnlockedLocked();
            var groups = new Dictionary<string, List<VaultEntryKey>>(StringComparer.Ordinal);
            foreach (var row in ReadAllRowsLocked())
            {
                var entry = DecryptEntry(row.Origin, row.Binding, row.Payload);
                string fp = VaultCrypto.Fingerprint(_masterKey!, entry.Password);
                if (!groups.TryGetValue(fp, out var list))
                {
                    list = new List<VaultEntryKey>();
                    groups[fp] = list;
                }
                list.Add(new VaultEntryKey(row.Origin, entry.Username));
            }
            Touch();
            IReadOnlyList<DuplicatePasswordGroup> result = groups.Values
                .Where(g => g.Count >= 2)
                .Select(g => (IReadOnlyList<VaultEntryKey>)g
                    .OrderBy(k => k.Origin, StringComparer.Ordinal).ThenBy(k => k.Username, StringComparer.Ordinal).ToList())
                .Select(g => new DuplicatePasswordGroup(g))
                .ToList();
            return Task.FromResult(result);
        }
    }

    /// <summary>
    /// Return accounts for the page origin. HTTPS only, and only if the form (if any) is also HTTPS
    /// with the same origin. Multiple accounts for the same origin are returned.
    /// </summary>
    public Task<AutofillResult> AutofillAsync(string pageUrl, string? formActionUrl = null, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var decision = CheckAutofill(pageUrl, formActionUrl);
            if (decision != AutofillDecision.Allowed)
                return Task.FromResult(new AutofillResult(decision, Array.Empty<AutofillCredential>()));

            OriginNormalizer.TryNormalize(pageUrl, out var page);
            var creds = new List<AutofillCredential>();
            foreach (var row in ReadAllRowsLocked())
            {
                if (!string.Equals(row.Origin, page.Origin, StringComparison.Ordinal)) continue;
                if (row.HttpsOnly && page.Scheme != "https") continue;
                var entry = DecryptEntry(row.Origin, row.Binding, row.Payload);
                creds.Add(new AutofillCredential(entry.Username, entry.Password));
            }
            Touch();
            creds.Sort((a, b) => string.CompareOrdinal(a.Username, b.Username));
            return Task.FromResult(new AutofillResult(AutofillDecision.Allowed, creds));
        }
    }

    /// <summary>
    /// Return the password for an origin and user name. Returns null if there is no entry, or if
    /// the entry requires HTTPS and this origin is not HTTPS. The other autofill checks are done by <see cref="CheckAutofill"/>.
    /// </summary>
    public Task<string?> GetPasswordAsync(string origin, string username, CancellationToken ct = default)
    {
        if (!OriginNormalizer.TryNormalize(origin, out var normalized)) return Task.FromResult<string?>(null);
        lock (_gate)
        {
            EnsureUnlockedLocked();
            string binding = VaultCrypto.BindingId(_masterKey!, normalized.Origin, username);
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT payload, https_only FROM passwords WHERE binding_id = $binding AND domain = $domain;";
            cmd.Parameters.AddWithValue("$binding", binding);
            cmd.Parameters.AddWithValue("$domain", normalized.Origin);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return Task.FromResult<string?>(null);

            bool httpsOnly = reader.GetInt64(1) != 0;
            if (httpsOnly && normalized.Scheme != "https") return Task.FromResult<string?>(null);

            var entry = DecryptEntry(normalized.Origin, binding, reader.GetString(0));
            Touch();
            return Task.FromResult<string?>(entry.Password);
        }
    }

    /// <summary>Check whether autofill is allowed for the given page and form.</summary>
    public AutofillDecision CheckAutofill(string pageUrl, string? formActionUrl)
    {
        if (!IsUnlocked) return AutofillDecision.Locked;
        if (!OriginNormalizer.TryNormalize(pageUrl, out var page)) return AutofillDecision.InvalidUrl;
        if (page.Scheme != "https") return AutofillDecision.NotHttps;

        if (formActionUrl is not null)
        {
            if (!OriginNormalizer.TryNormalize(formActionUrl, out var form)) return AutofillDecision.FormActionMismatch;
            if (form.Scheme != "https") return AutofillDecision.NotHttps;
            if (!string.Equals(form.Origin, page.Origin, StringComparison.Ordinal)) return AutofillDecision.FormActionMismatch;
        }
        return AutofillDecision.Allowed;
    }

    /// <summary>Import CSV (Chrome, Firefox, Safari, Bitwarden, 1Password). Duplicate credentials are updated.</summary>
    public async Task<CsvImportResult> ImportCsvAsync(string csv, CancellationToken ct = default)
    {
        var parsed = CsvPasswordCodec.Parse(csv);
        int imported = 0;
        lock (_gate)
        {
            EnsureUnlockedLocked();
        }
        foreach (var cred in parsed.Credentials)
        {
            ct.ThrowIfCancellationRequested();
            await AddAsync(cred.Origin, cred.Username, cred.Password, ct).ConfigureAwait(false);
            imported++;
        }
        return new CsvImportResult(parsed.Format, imported, parsed.SkippedRows);
    }

    /// <summary>
    /// Raw CSV export (passwords in plain text). Allowed only if the user
    /// has explicitly acknowledged with <paramref name="acknowledgeRawExport"/> = true.
    /// Show <see cref="RawExportWarning"/> before asking for confirmation.
    /// </summary>
    public async Task<string> ExportRawCsvAsync(bool acknowledgeRawExport, CancellationToken ct = default)
    {
        if (!acknowledgeRawExport)
            throw new InvalidOperationException("Raw export requires explicit acknowledgment from the user.");

        var rows = new List<(string Origin, string Username, string Password)>();
        var list = await ListAsync(ct).ConfigureAwait(false);
        foreach (var (origin, username) in list)
        {
            string? password = await GetPasswordAsync(origin, username, ct).ConfigureAwait(false);
            if (password is null) continue;
            rows.Add((origin, username, password));
        }
        return CsvPasswordCodec.Export(rows);
    }

    /// <summary>
    /// Encrypted export: the CSV is encrypted with a passphrase (PBKDF2-HMAC-SHA256 + AES-256-GCM).
    /// Returns serialized JSON that can be stored safely. The passphrase is not stored.
    /// </summary>
    public async Task<string> ExportEncryptedAsync(string passphrase, CancellationToken ct = default)
    {
        ValidatePassphrase(passphrase);
        string csv = await ExportRawCsvAsync(acknowledgeRawExport: true, ct).ConfigureAwait(false);

        byte[] salt = VaultCrypto.NewSalt();
        byte[] kek = VaultCrypto.DeriveKek(passphrase, salt, VaultCrypto.MinIterations);
        try
        {
            byte[] blob = VaultCrypto.Seal(kek, Encoding.UTF8.GetBytes(csv), ExportAad());
            var envelope = new ExportEnvelope(ExportFormat, ExportVersion, "pbkdf2-sha256", VaultCrypto.MinIterations,
                Convert.ToBase64String(salt), Convert.ToBase64String(blob));
            return JsonSerializer.Serialize(envelope);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    /// <summary>Import an encrypted export. A wrong passphrase or a corrupt file throws <see cref="InvalidDataException"/>.</summary>
    public async Task<CsvImportResult> ImportEncryptedAsync(string envelopeJson, string passphrase, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(envelopeJson);
        ValidatePassphrase(passphrase);

        ExportEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<ExportEnvelope>(envelopeJson);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Invalid export file.", ex);
        }
        if (envelope is null || envelope.Format != ExportFormat || envelope.Version != ExportVersion || envelope.Kdf != "pbkdf2-sha256")
            throw new InvalidDataException("Invalid export file.");
        if (envelope.Iterations < VaultCrypto.MinIterations)
            throw new InvalidDataException("Invalid PBKDF2 iteration count.");

        string csv;
        byte[] kek;
        try
        {
            byte[] salt = Convert.FromBase64String(envelope.Salt);
            byte[] blob = Convert.FromBase64String(envelope.Blob);
            kek = VaultCrypto.DeriveKek(passphrase, salt, envelope.Iterations);
            try
            {
                csv = Encoding.UTF8.GetString(VaultCrypto.Open(kek, blob, ExportAad()));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(kek);
            }
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            throw new InvalidDataException("Wrong passphrase or corrupt export file.", ex);
        }

        return await ImportCsvAsync(csv, ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
        {
            ClearMasterKeyLocked();
            _db.Dispose();
        }
    }

    // ---------------------------------------------------------------------
    // Internals

    private sealed record EntryPayload(string Username, string Password);

    private sealed record ExportEnvelope(string Format, int Version, string Kdf, int Iterations, string Salt, string Blob);

    private sealed record Row(string Binding, string Origin, string Payload, bool HttpsOnly, DateTimeOffset Created, DateTimeOffset Modified);

    private (string Username, string Password) DecryptEntry(string origin, string binding, string payload)
    {
        try
        {
            string json = VaultCrypto.OpenFromPayload(_masterKey!, payload, Encoding.UTF8.GetBytes(origin + "|" + binding));
            var doc = JsonSerializer.Deserialize<EntryPayload>(json)
                      ?? throw new InvalidDataException("Vault entry is corrupt.");
            return (doc.Username, doc.Password);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
        {
            throw new InvalidDataException("Vault entry is corrupt or has been modified.", ex);
        }
    }

    private List<Row> ReadAllRowsLocked()
    {
        var rows = new List<Row>();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT binding_id, domain, payload, https_only, created, modified FROM passwords;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new Row(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3) != 0,
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(4)),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(5))));
        }
        return rows;
    }

    private Row? ReadRowLocked(string binding, string origin)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT binding_id, domain, payload, https_only, created, modified FROM passwords WHERE binding_id = $b AND domain = $d;";
        cmd.Parameters.AddWithValue("$b", binding);
        cmd.Parameters.AddWithValue("$d", origin);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new Row(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3) != 0,
            DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(4)),
            DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(5)));
    }

    private static byte[] Aad(string purpose) => Encoding.UTF8.GetBytes("winidm.vault:" + purpose);

    private static byte[] ExportAad() => Encoding.UTF8.GetBytes("winidm.vault-export:v1");

    private static void ValidatePin(string pin)
    {
        ArgumentNullException.ThrowIfNull(pin);
        if (pin.Length < MinPinLength || pin.Length > MaxPinLength)
            throw new ArgumentException($"PIN must be {MinPinLength} to {MaxPinLength} characters.", nameof(pin));
    }

    private static void ValidatePassphrase(string passphrase)
    {
        ArgumentNullException.ThrowIfNull(passphrase);
        if (passphrase.Length < MinExportPassphraseLength)
            throw new ArgumentException($"The export passphrase must be at least {MinExportPassphraseLength} characters.", nameof(passphrase));
    }

    private bool PinMatches(string pin)
    {
        // Validate the PIN by trying to open the wrapping, without touching the current master key.
        byte[] salt = ReadMeta(MetaSalt) ?? throw new InvalidDataException("Vault metadata is incomplete.");
        byte[] wrapped = VaultCrypto.UnprotectForUser(ReadMeta(MetaKeyBlob)!);
        byte[] kek = VaultCrypto.DeriveKek(pin, salt, ParseIterations(ReadMeta(MetaIterations)));
        try
        {
            VaultCrypto.Open(kek, wrapped, Aad("master-key"));
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    private bool IsInitializedLocked() => ReadMeta(MetaKeyBlob) is not null;

    private bool IsSessionActiveLocked()
    {
        if (_masterKey is null) return false;
        if (_monotonicMs() - _lastActivityMs > (long)SessionTimeout.TotalMilliseconds)
        {
            ClearMasterKeyLocked();
            return false;
        }
        return true;
    }

    private void EnsureUnlockedLocked()
    {
        if (!IsSessionActiveLocked()) throw new InvalidOperationException("The vault is locked. Unlock it with the PIN first.");
    }

    private void Touch() => _lastActivityMs = _monotonicMs();

    /// <summary>
    /// Remaining lockout (ms). As long as the monotonic clock is not reset (same boot), the remainder is calculated
    /// from the monotonic clock only and is not affected by wall-clock changes. If the monotonic clock
    /// is smaller than the stored value (reboot), the stored wall-clock time is used.
    /// </summary>
    private long LockoutRemainingMsLocked()
    {
        if (_lockoutRemainingAtStoreMs <= 0) return 0;
        long nowMono = _monotonicMs();
        long left = nowMono >= _lockoutMonoAtStoreMs
            ? _lockoutRemainingAtStoreMs - (nowMono - _lockoutMonoAtStoreMs)
            : _lockoutWallUntilMs - _utcNow().ToUnixTimeMilliseconds();
        return Math.Max(0, left);
    }

    private void StartLockoutLocked()
    {
        long nowMono = _monotonicMs();
        long nowWall = _utcNow().ToUnixTimeMilliseconds();
        _lockoutRemainingAtStoreMs = (long)LockoutDuration.TotalMilliseconds;
        _lockoutMonoAtStoreMs = nowMono;
        _lockoutWallUntilMs = nowWall + (long)LockoutDuration.TotalMilliseconds;
        WriteMetaLong(MetaLockoutRemaining, _lockoutRemainingAtStoreMs);
        WriteMetaLong(MetaLockoutMonoAt, _lockoutMonoAtStoreMs);
        WriteMetaLong(MetaLockoutWallUntil, _lockoutWallUntilMs);
    }

    private void ClearLockoutLocked()
    {
        _lockoutRemainingAtStoreMs = 0;
        _lockoutMonoAtStoreMs = 0;
        _lockoutWallUntilMs = 0;
        WriteMetaLong(MetaLockoutRemaining, 0);
    }

    private void LoadLockoutLocked()
    {
        _lockoutRemainingAtStoreMs = ReadMetaLong(MetaLockoutRemaining) ?? 0;
        _lockoutMonoAtStoreMs = ReadMetaLong(MetaLockoutMonoAt) ?? 0;
        _lockoutWallUntilMs = ReadMetaLong(MetaLockoutWallUntil) ?? 0;
    }

    private void SetMasterKeyLocked(byte[] key)
    {
        ClearMasterKeyLocked();
        _masterKey = key;
        Touch();
    }

    private void ClearMasterKeyLocked()
    {
        if (_masterKey is not null)
        {
            CryptographicOperations.ZeroMemory(_masterKey);
            _masterKey = null;
        }
    }

    private static int ParseIterations(byte[]? raw)
    {
        if (raw is null || !int.TryParse(Encoding.ASCII.GetString(raw), out int n) || n < VaultCrypto.MinIterations)
            throw new InvalidDataException("Invalid PBKDF2 iteration count.");
        return n;
    }

    private void CreateSchema()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS vault_metadata(name TEXT PRIMARY KEY, value BLOB NOT NULL);
CREATE TABLE IF NOT EXISTS passwords(
    _id INTEGER PRIMARY KEY AUTOINCREMENT,
    binding_id TEXT NOT NULL UNIQUE,
    domain TEXT NOT NULL,
    https_only INTEGER NOT NULL DEFAULT 1,
    payload TEXT NOT NULL,
    created INTEGER NOT NULL,
    modified INTEGER NOT NULL);
CREATE INDEX IF NOT EXISTS ix_passwords_domain ON passwords(domain);";
        cmd.ExecuteNonQuery();
    }

    private byte[]? ReadMeta(string name)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT value FROM vault_metadata WHERE name = $name;";
        cmd.Parameters.AddWithValue("$name", name);
        return cmd.ExecuteScalar() as byte[];
    }

    private long? ReadMetaLong(string name)
    {
        var raw = ReadMeta(name);
        if (raw is null) return null;
        return long.TryParse(Encoding.ASCII.GetString(raw), out long v) ? v : null;
    }

    /// <summary>Run the work in a single SQLite transaction (raw BEGIN/COMMIT, safe for batch changes).</summary>
    private void InTransaction(Action work)
    {
        Exec("BEGIN IMMEDIATE;");
        try
        {
            work();
            Exec("COMMIT;");
        }
        catch
        {
            Exec("ROLLBACK;");
            throw;
        }
    }

    private void Exec(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void WriteMeta(string name, string value) => WriteMeta(name, Encoding.UTF8.GetBytes(value));

    private void WriteMetaLong(string name, long value) =>
        WriteMeta(name, Encoding.ASCII.GetBytes(value.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    private void WriteMeta(string name, byte[] value)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "INSERT INTO vault_metadata(name, value) VALUES ($name, $value) " +
                          "ON CONFLICT(name) DO UPDATE SET value = excluded.value;";
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.ExecuteNonQuery();
    }
}

/// <summary>Origin normalizer (scheme://host[:port]) for exact matching.</summary>
internal static class OriginNormalizer
{
    public sealed record Parsed(string Origin, string Scheme, string Host);

    public static bool TryNormalize(string? url, out Parsed parsed)
    {
        parsed = null!;
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return false;
        if (string.IsNullOrEmpty(uri.Host)) return false;

        string host = HostUtil.NormalizeHost(uri.Host);
        bool defaultPort = uri.IsDefaultPort;
        string origin = uri.Scheme + "://" + host + (defaultPort ? "" : ":" + uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        parsed = new Parsed(origin, uri.Scheme, host);
        return true;
    }

}
