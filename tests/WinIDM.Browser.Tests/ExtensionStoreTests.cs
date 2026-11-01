using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace WinIDM.Browser.Tests;

/// <summary>Builds signed CRX3 packages the same way the Chrome Web Store does.</summary>
internal static class TestCrx
{
    public static byte[] Build(RSA key, IDictionary<string, string> files)
    {
        var zip = Zip(files);
        var pub = key.ExportSubjectPublicKeyInfo();
        var id = SHA256.HashData(pub).AsSpan(0, 16).ToArray();
        var signedData = Field(1, id);

        var prefix = Encoding.ASCII.GetBytes("CRX3 SignedData\0");
        var len = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)signedData.Length);
        var sig = key.SignData(prefix.Concat(len).Concat(signedData).Concat(zip).ToArray(), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var header = Field(2, Field(1, pub).Concat(Field(2, sig)).ToArray()).Concat(Field(10000, signedData)).ToArray();
        var crx = new List<byte>(Encoding.ASCII.GetBytes("Cr24"));
        crx.AddRange(BitConverter.GetBytes(3u));
        crx.AddRange(BitConverter.GetBytes((uint)header.Length));
        crx.AddRange(header);
        crx.AddRange(zip);
        return crx.ToArray();
    }

    public static byte[] Zip(IDictionary<string, string> files)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in files)
            {
                using var w = new StreamWriter(z.CreateEntry(name).Open());
                w.Write(text);
            }
        }
        return ms.ToArray();
    }

    private static byte[] Field(int number, byte[] value) =>
        Varint((ulong)number << 3 | 2).Concat(Varint((ulong)value.Length)).Concat(value).ToArray();

    private static byte[] Varint(ulong v)
    {
        var b = new List<byte>();
        while (v >= 0x80) { b.Add((byte)(v | 0x80)); v >>= 7; }
        b.Add((byte)v);
        return b.ToArray();
    }
}

public class CrxPackageTests
{
    private static readonly Dictionary<string, string> Files = new() { ["manifest.json"] = "{\"name\":\"T\",\"version\":\"1.0\",\"manifest_version\":3}" };

    [Fact]
    public void Parse_ValidPackage_ReturnsIdKeyAndArchive()
    {
        using var rsa = RSA.Create(2048);
        var pkg = CrxPackage.Parse(TestCrx.Build(rsa, Files));

        Assert.Equal(CrxPackage.IdFromPublicKey(rsa.ExportSubjectPublicKeyInfo()), pkg.Id);
        Assert.Matches("^[a-p]{32}$", pkg.Id);
        Assert.Equal(CrxPackage.Parse(TestCrx.Build(rsa, Files), pkg.Id).Id, pkg.Id);
    }

    [Fact]
    public void Parse_AlteredArchive_IsRefused()
    {
        using var rsa = RSA.Create(2048);
        var crx = TestCrx.Build(rsa, Files);
        crx[^30] ^= 0xFF;
        Assert.Throws<InvalidDataException>(() => CrxPackage.Parse(crx));
    }

    [Fact]
    public void Parse_OtherExtensionThanRequested_IsRefused()
    {
        using var rsa = RSA.Create(2048);
        Assert.Throws<InvalidDataException>(() => CrxPackage.Parse(TestCrx.Build(rsa, Files), new string('a', 32)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("PK\u0003\u0004 not a crx")]
    public void Parse_NotACrx_IsRefused(string text) =>
        Assert.Throws<InvalidDataException>(() => CrxPackage.Parse(Encoding.ASCII.GetBytes(text)));
}

public class ExtensionStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "winidm-ext-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Theory]
    [InlineData("fcoeoabgfenejglbffodgkkbkcdhcgfn", "fcoeoabgfenejglbffodgkkbkcdhcgfn")]
    [InlineData("https://chromewebstore.google.com/detail/claude/fcoeoabgfenejglbffodgkkbkcdhcgfn?hl=en", "fcoeoabgfenejglbffodgkkbkcdhcgfn")]
    [InlineData("https://chromewebstore.google.com/detail/chatgpt/HEHGGADAOPOACECDLLHHAJMBJKDCMAJG", "hehggadaopoacecdllhhajmbjkdcmajg")]
    [InlineData("not an id", null)]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz", null)]
    public void ParseStoreId(string input, string? expected) => Assert.Equal(expected, ExtensionStore.ParseStoreId(input));

    [Fact]
    public void InstallPackage_UnpacksAddsKeyAndFindsPanels()
    {
        using var rsa = RSA.Create(2048);
        var crx = TestCrx.Build(rsa, new Dictionary<string, string>
        {
            ["manifest.json"] = "{\"name\":\"__MSG_app__\",\"default_locale\":\"en\",\"version\":\"2.1\",\"manifest_version\":3," +
                                "\"permissions\":[\"sidePanel\",\"tabs\"],\"host_permissions\":[\"<all_urls>\"]," +
                                "\"side_panel\":{\"default_path\":\"side-panel-loading.html\"},\"action\":{\"default_popup\":\"popup.html\"}}",
            ["_locales/en/messages.json"] = "{\"app\":{\"message\":\"Helper\"}}",
            ["side-panel-loading.html"] = "<html></html>",
            ["work-sidepanel.html"] = "<html></html>",
            ["popup.html"] = "<html></html>",
        });
        var store = new ExtensionStore(_dir, new HttpClient());

        var info = store.InstallPackage(crx);

        Assert.Equal("Helper", info.Name);
        Assert.Equal("2.1", info.Version);
        Assert.Equal(new[] { "work-sidepanel.html", "side-panel-loading.html" }, info.PanelPages);
        Assert.Equal("popup.html", info.PopupPage);
        Assert.Contains("<all_urls>", info.HostPermissions);
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(info.Folder, "manifest.json")))!;
        Assert.Equal(CrxPackage.IdFromPublicKey(Convert.FromBase64String(manifest["key"]!.GetValue<string>())), info.Id);

        var reloaded = new ExtensionStore(_dir, new HttpClient());
        Assert.Equal(info.Id, Assert.Single(reloaded.All).Id);
        reloaded.SetEnabled(info.Id, false);
        Assert.False(new ExtensionStore(_dir, new HttpClient()).Find(info.Id)!.Enabled);

        reloaded.Remove(info.Id);
        Assert.Empty(reloaded.All);
        Assert.False(Directory.Exists(Path.Combine(_dir, info.Id)));
    }

    [Fact]
    public void InstallPackage_PathOutsideFolder_IsRefused()
    {
        using var rsa = RSA.Create(2048);
        var crx = TestCrx.Build(rsa, new Dictionary<string, string>
        {
            ["manifest.json"] = "{\"name\":\"T\",\"version\":\"1\",\"manifest_version\":3}",
            ["../../evil.txt"] = "x",
        });
        var store = new ExtensionStore(_dir, new HttpClient());

        Assert.Throws<InvalidDataException>(() => store.InstallPackage(crx));
        Assert.False(File.Exists(Path.Combine(_dir, "..", "evil.txt")));
        Assert.Empty(store.All);
    }
}
