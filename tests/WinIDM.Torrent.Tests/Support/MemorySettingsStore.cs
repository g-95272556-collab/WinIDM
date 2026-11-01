using System.Globalization;
using WinIDM.Core.Contracts;

namespace WinIDM.Torrent.Tests.Support;

/// <summary>In-memory ISettingsStore for tests.</summary>
internal sealed class MemorySettingsStore : ISettingsStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public event Action<string>? Changed;

    /// <summary>Set a value fluently, for example new MemorySettingsStore().Put("enable_dht", false).</summary>
    public MemorySettingsStore Put(string key, object value)
    {
        _values[key] = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        Changed?.Invoke(key);
        return this;
    }

    public T Get<T>(string key, T defaultValue)
    {
        if (!_values.TryGetValue(key, out var raw)) return defaultValue;

        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        if (target == typeof(string)) return (T)(object)raw;
        if (target == typeof(bool)) return (T)(object)bool.Parse(raw);
        return (T)Convert.ChangeType(raw, target, CultureInfo.InvariantCulture)!;
    }

    void ISettingsStore.Set<T>(string key, T value) => Put(key, value!);

    public IReadOnlyDictionary<string, string> Snapshot() => new Dictionary<string, string>(_values, StringComparer.Ordinal);

    public void Restore(IReadOnlyDictionary<string, string> values)
    {
        _values.Clear();
        foreach (var (k, v) in values) _values[k] = v;
    }
}

/// <summary>
/// "Offline" settings for tests: DHT, LSD, UPnP, and NAT-PMP are off, magnet metadata
/// is not fetched, and no public network is touched.
/// </summary>
internal static class OfflineSettings
{
    public static MemorySettingsStore Create() => new MemorySettingsStore()
        .Put("enable_dht", false)
        .Put("enable_lsd", false)
        .Put("enable_upnp", false)
        .Put("enable_nat_pmp", false)
        .Put("idm_pref_fetch_metadata", false)
        .Put("use_random_port", true);
}
