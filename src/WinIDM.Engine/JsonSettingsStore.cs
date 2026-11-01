using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinIDM.Core.Contracts;

namespace WinIDM.Engine;

/// <summary>
/// JSON file settings store (string key-value pairs).
/// Thread-safe; every change is saved atomically (write to a temporary file, then replace).
/// Secret keys (<see cref="SettingKeys.IsSecret"/>) are encrypted with DPAPI (current user scope) on disk only;
/// <see cref="Get{T}"/>, <see cref="Set{T}"/>, <see cref="Snapshot"/> and <see cref="Restore"/> work with plain values.
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    /// <summary>Prefix for encrypted secret values. Same as the password format in the repository.</summary>
    internal const string SecretPrefix = "dpapi1:";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _gate = new();

    /// <summary>Plain values (after decryption) used by the engine.</summary>
    private Dictionary<string, string> _values;

    /// <summary>
    /// Secrets on disk that could not be decrypted (for example a file copied from another Windows user).
    /// Written back verbatim so they are not lost, but not read as values.
    /// </summary>
    private readonly Dictionary<string, string> _undecryptable = new(StringComparer.Ordinal);

    public JsonSettingsStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A settings path is required.", nameof(path));
        _path = path;
        _values = Load(path, _undecryptable);
    }

    public event Action<string>? Changed;

    public string Path => _path;

    public T Get<T>(string key, T defaultValue)
    {
        string? raw;
        lock (_gate)
        {
            if (!_values.TryGetValue(key, out raw)) return defaultValue;
        }

        return TryParse(raw, out T? parsed) ? parsed! : defaultValue;
    }

    public void Set<T>(string key, T value)
    {
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("The key must not be empty.", nameof(key));

        lock (_gate)
        {
            _undecryptable.Remove(key);
            if (value is null)
            {
                _values.Remove(key);
            }
            else
            {
                _values[key] = Format(value);
            }

            Save();
        }

        RaiseChanged(key);
    }

    public IReadOnlyDictionary<string, string> Snapshot()
    {
        lock (_gate)
        {
            return new Dictionary<string, string>(_values, StringComparer.Ordinal);
        }
    }

    public void Restore(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        List<string> changedKeys;
        lock (_gate)
        {
            changedKeys = _values.Keys.Union(values.Keys, StringComparer.Ordinal).ToList();
            _values = new Dictionary<string, string>(values, StringComparer.Ordinal);
            _undecryptable.Clear();
            Save();
        }

        foreach (var key in changedKeys) RaiseChanged(key);
    }

    private void RaiseChanged(string key)
    {
        try
        {
            Changed?.Invoke(key);
        }
        catch
        {
            // A broken listener must not bring down the settings store.
        }
    }

    /// <summary>Must be called while holding the <see cref="_gate"/> lock.</summary>
    private void Save()
    {
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var onDisk = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in _values)
        {
            onDisk[key] = SettingKeys.IsSecret(key) ? Protect(value) : value;
        }

        foreach (var (key, raw) in _undecryptable)
        {
            onDisk.TryAdd(key, raw);
        }

        var tempPath = _path + ".tmp";
        var json = JsonSerializer.Serialize(onDisk, JsonOptions);
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _path, overwrite: true);
    }

    private static Dictionary<string, string> Load(string path, Dictionary<string, string> undecryptable)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return values;

        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            if (loaded is null) return values;

            foreach (var (key, stored) in loaded)
            {
                if (!SettingKeys.IsSecret(key) || !stored.StartsWith(SecretPrefix, StringComparison.Ordinal))
                {
                    // Plain value, or an old secret still stored as plain text (it will be encrypted on the next save).
                    values[key] = stored;
                    continue;
                }

                if (TryUnprotect(stored, out var plain))
                {
                    values[key] = plain;
                }
                else
                {
                    undecryptable[key] = stored;
                }
            }

            return values;
        }
        catch (JsonException)
        {
            // Corrupt file: keep a copy for diagnosis, then restart with defaults.
            try
            {
                File.Copy(path, path + ".corrupt", overwrite: true);
            }
            catch (IOException)
            {
                // No copy was made; continue with defaults.
            }

            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    // DPAPI exists on Windows only; WinIDM is a Windows application (same as SqliteDownloadRepository).
#pragma warning disable CA1416
    private static string Protect(string plain)
    {
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), optionalEntropy: null, DataProtectionScope.CurrentUser);
        return SecretPrefix + Convert.ToBase64String(bytes);
    }

    private static bool TryUnprotect(string stored, out string plain)
    {
        try
        {
            var bytes = Convert.FromBase64String(stored[SecretPrefix.Length..]);
            plain = Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser));
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            plain = string.Empty;
            return false;
        }
    }
#pragma warning restore CA1416

    private static string Format(object value) => value switch
    {
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static bool TryParse<T>(string raw, out T? result)
    {
        var type = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        object? parsed = null;

        if (type == typeof(string))
        {
            parsed = raw;
        }
        else if (type == typeof(bool))
        {
            if (bool.TryParse(raw, out var b)) parsed = b;
            else if (raw == "1") parsed = true;
            else if (raw == "0") parsed = false;
        }
        else if (type == typeof(int))
        {
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) parsed = i;
        }
        else if (type == typeof(long))
        {
            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) parsed = l;
        }
        else if (type == typeof(double))
        {
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) parsed = d;
        }
        else if (type.IsEnum)
        {
            if (Enum.TryParse(type, raw, ignoreCase: true, out var e)) parsed = e;
        }

        if (parsed is null)
        {
            result = default;
            return false;
        }

        result = (T)parsed;
        return true;
    }
}
