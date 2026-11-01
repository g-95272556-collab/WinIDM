using System.IO;
using System.Text.Json;

namespace WinIDM.App.Services;

/// <summary>
/// Browser-only preferences (for example, daily update check, reader mode font size). Stored as JSON
/// in the app data folder. A read or write failure never brings the browser down; defaults are used.
/// </summary>
public sealed class BrowserPrefs
{
    private readonly string _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _values;

    public BrowserPrefs(string path)
    {
        _path = path;
        _values = Load();
    }

    public T Get<T>(string key, T fallback)
    {
        lock (_gate)
        {
            if (!_values.TryGetValue(key, out var raw)) return fallback;
            try { return JsonSerializer.Deserialize<T>(raw) ?? fallback; }
            catch (JsonException) { return fallback; }
        }
    }

    public void Set<T>(string key, T value)
    {
        lock (_gate)
        {
            _values[key] = JsonSerializer.Serialize(value);
            Save();
        }
    }

    private Dictionary<string, string> Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            AppLog.Error("Read browser preferences", ex);
        }
        return new();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_values));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Save browser preferences", ex);
        }
    }
}
