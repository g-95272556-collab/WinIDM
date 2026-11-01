using System.Security.Cryptography;
using System.Text;

namespace WinIDM.Torrent.Tests.Support;

/// <summary>
/// Hand-written .torrent generator for tests (bencode is written in the test, no network).
/// Multiple files in one root folder; pieces are computed as SHA-1 of the real content.
/// </summary>
internal sealed class TestTorrent
{
    public const int DefaultPieceLength = 16 * 1024;

    private TestTorrent(string name, string path, byte[] bytes, string infoHash, IReadOnlyList<TestFile> files)
    {
        Name = name;
        Path = path;
        Bytes = bytes;
        InfoHash = infoHash;
        Files = files;
    }

    public string Name { get; }
    public string Path { get; }
    public byte[] Bytes { get; }

    /// <summary>Info hash v1 (SHA-1 of the info dict), lowercase.</summary>
    public string InfoHash { get; }
    public IReadOnlyList<TestFile> Files { get; }
    public long TotalLength => Files.Sum(f => (long)f.Content.Length);

    /// <summary>Create a .torrent in the <paramref name="directory"/> folder. The tracker is a non-existent loopback URL.</summary>
    public static TestTorrent Create(
        string directory,
        string name,
        IReadOnlyList<TestFile> files,
        string tracker = "http://127.0.0.1:9/announce",
        int pieceLength = DefaultPieceLength)
    {
        Directory.CreateDirectory(directory);

        var all = new List<byte>();
        foreach (var f in files) all.AddRange(f.Content);
        var content = all.ToArray();

        var pieces = new List<byte>();
        for (var offset = 0; offset < content.Length; offset += pieceLength)
        {
            var length = Math.Min(pieceLength, content.Length - offset);
            pieces.AddRange(SHA1.HashData(content.AsSpan(offset, length)));
        }

        var fileList = new List<object>();
        foreach (var f in files)
        {
            fileList.Add(new SortedDictionary<string, object>(StringComparer.Ordinal)
            {
                ["length"] = (long)f.Content.Length,
                ["path"] = f.RelativePath.Split('/').Cast<object>().ToList(),
            });
        }

        var info = new SortedDictionary<string, object>(StringComparer.Ordinal)
        {
            ["files"] = fileList,
            ["name"] = name,
            ["piece length"] = (long)pieceLength,
            ["pieces"] = pieces.ToArray(),
        };

        var root = new SortedDictionary<string, object>(StringComparer.Ordinal)
        {
            ["announce"] = tracker,
            ["info"] = info,
            ["creation date"] = 1_700_000_000L,
            ["created by"] = "WinIDM tests",
        };

        var infoBytes = Bencode.Encode(info);
        var infoHash = Convert.ToHexString(SHA1.HashData(infoBytes)).ToLowerInvariant();
        var bytes = Bencode.Encode(root);
        var path = System.IO.Path.Combine(directory, name + ".torrent");
        File.WriteAllBytes(path, bytes);

        return new TestTorrent(name, path, bytes, infoHash, files);
    }

    /// <summary>Write the file contents to the save folder using the torrent's relative paths (for verification tests).</summary>
    public void WriteContent(string saveDirectory)
    {
        foreach (var f in Files)
        {
            var target = System.IO.Path.Combine(saveDirectory, Name, f.RelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, f.Content);
        }
    }

    public static byte[] Bytes32(int seed, int length)
    {
        var rnd = new Random(seed);
        var buffer = new byte[length];
        rnd.NextBytes(buffer);
        return buffer;
    }

    
}

internal sealed record TestFile(string RelativePath, byte[] Content);

/// <summary>Minimal bencode encoder (integer, string/bytes, list, dictionary).</summary>
internal static class Bencode
{
    public static byte[] Encode(object value)
    {
        using var ms = new MemoryStream();
        Write(ms, value);
        return ms.ToArray();
    }

    private static void Write(Stream stream, object value)
    {
        switch (value)
        {
            case byte[] bytes:
                WriteAscii(stream, bytes.Length + ":");
                stream.Write(bytes);
                break;
            case string text:
                var utf8 = Encoding.UTF8.GetBytes(text);
                WriteAscii(stream, utf8.Length + ":");
                stream.Write(utf8);
                break;
            case long number:
                WriteAscii(stream, "i" + number + "e");
                break;
            case int number:
                WriteAscii(stream, "i" + number + "e");
                break;
            case SortedDictionary<string, object> dict:
                stream.WriteByte((byte)'d');
                foreach (var (key, item) in dict.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    Write(stream, key);
                    Write(stream, item);
                }
                stream.WriteByte((byte)'e');
                break;
            case List<object> list:
                stream.WriteByte((byte)'l');
                foreach (var item in list) Write(stream, item);
                stream.WriteByte((byte)'e');
                break;
            default:
                throw new NotSupportedException("Unsupported type: " + value.GetType());
        }
    }

    private static void WriteAscii(Stream stream, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        stream.Write(bytes);
    }
}
