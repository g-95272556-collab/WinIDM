using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinIDM.Backup;

namespace WinIDM.App.Services;

/// <summary>
/// Cloud destination OAuth tokens are stored on disk encrypted with DPAPI (current user scope).
/// Tokens never enter settings, backups or logs. Corrupt files are ignored with a brief log entry.
/// </summary>
public sealed class DpapiTokenStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WinIDM.OAuth.v1");
    private static readonly Regex ProviderName = new(@"^[a-z]{2,20}$", RegexOptions.CultureInvariant);

    private readonly string _directory;

    public DpapiTokenStore(string directory) => _directory = directory;

    public void Save(string provider, OAuthToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        var path = PathFor(provider);
        var plain = JsonSerializer.SerializeToUtf8Bytes(token);
        var sealedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(_directory);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, sealedBytes);
        File.Move(tmp, path, overwrite: true);
    }

    public OAuthToken? Load(string provider)
    {
        var path = PathFor(provider);
        if (!File.Exists(path)) return null;
        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<OAuthToken>(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException)
        {
            AppLog.Error($"Token {provider} could not be read", ex);
            return null;
        }
    }

    public void Delete(string provider)
    {
        var path = PathFor(provider);
        if (File.Exists(path)) File.Delete(path);
    }

    private string PathFor(string provider)
    {
        if (!ProviderName.IsMatch(provider)) throw new ArgumentException("Invalid provider name.", nameof(provider));
        return Path.Combine(_directory, provider + ".bin");
    }
}
