using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinIDM.Torrent;

/// <summary>Persisted record for a single torrent stored in torrents.json.</summary>
internal sealed class TorrentRecord
{
    public long Id { get; set; }

    /// <summary>"Magnet" or "File".</summary>
    public string Kind { get; set; } = "File";

    public string? MagnetUri { get; set; }

    /// <summary>Name of the copied .torrent file inside the torrents folder (for example "3.torrent").</summary>
    public string? TorrentFile { get; set; }

    public string SaveDirectory { get; set; } = "";
    public string Name { get; set; } = "";
    public string InfoHash { get; set; } = "";
    public bool Paused { get; set; }
    public bool Sequential { get; set; }
    public long DownloadLimit { get; set; }
    public long UploadLimit { get; set; }
    public DateTimeOffset AddedAt { get; set; }

    /// <summary>File priorities set by the user, keyed by file index.</summary>
    public Dictionary<int, TorrentFilePriority> FilePriorities { get; set; } = new();

    /// <summary>Tracker list edited by the user. Null means use the original trackers.</summary>
    public List<string>? Trackers { get; set; }
}

/// <summary>Top-level persistence document (torrents.json).</summary>
internal sealed class TorrentRegistryDocument
{
    public long NextId { get; set; } = 1;
    public List<TorrentRecord> Items { get; set; } = new();
}

/// <summary>Reads and writes torrents.json with atomic writes.</summary>
internal sealed class TorrentRegistry
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;

    public TorrentRegistry(string path)
    {
        _path = path;
    }

    public TorrentRegistryDocument Load()
    {
        if (!File.Exists(_path)) return new TorrentRegistryDocument();

        try
        {
            var doc = JsonSerializer.Deserialize<TorrentRegistryDocument>(File.ReadAllText(_path), Options);
            return doc ?? new TorrentRegistryDocument();
        }
        catch (JsonException)
        {
            // Corrupt file: keep a copy for diagnostics and start with an empty list.
            var backup = _path + ".corrupt-" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            File.Move(_path, backup, overwrite: true);
            return new TorrentRegistryDocument();
        }
    }

    public void Save(TorrentRegistryDocument document)
    {
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(document, Options));
        File.Move(temp, _path, overwrite: true);
    }
}
