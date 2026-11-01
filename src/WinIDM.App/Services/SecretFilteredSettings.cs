using WinIDM.Core.Contracts;
using WinIDM.Engine;

namespace WinIDM.App.Services;

/// <summary>
/// <see cref="ISettingsStore"/> wrapper for backup. Secret keys (<see cref="SettingKeys.IsSecret"/>)
/// are not exported through <see cref="Snapshot"/>, and during restore the existing secret values are kept
/// so a backup never overwrites or exposes proxy passwords, tokens or client keys.
/// </summary>
public sealed class SecretFilteredSettings : ISettingsStore
{
    private readonly ISettingsStore _inner;

    public SecretFilteredSettings(ISettingsStore inner) => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public T Get<T>(string key, T defaultValue) => _inner.Get(key, defaultValue);

    public void Set<T>(string key, T value) => _inner.Set(key, value);

    public IReadOnlyDictionary<string, string> Snapshot() =>
        _inner.Snapshot()
            .Where(kv => !SettingKeys.IsSecret(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

    public void Restore(IReadOnlyDictionary<string, string> values)
    {
        var target = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in _inner.Snapshot())
        {
            if (SettingKeys.IsSecret(kv.Key)) target[kv.Key] = kv.Value;
        }

        foreach (var kv in values)
        {
            if (!SettingKeys.IsSecret(kv.Key)) target[kv.Key] = kv.Value;
        }

        _inner.Restore(target);
    }

    public event Action<string>? Changed
    {
        add => _inner.Changed += value;
        remove => _inner.Changed -= value;
    }
}
