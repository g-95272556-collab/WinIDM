using WinIDM.Backup;

namespace WinIDM.App.Services;

/// <summary>
/// Implements <see cref="ITokenStore"/> for backups with DPAPI (<see cref="DpapiTokenStore"/>).
/// Tokens are stored protected for the current Windows account and never reach settings or logs.
/// </summary>
public sealed class BackupTokenStore : ITokenStore
{
    private readonly DpapiTokenStore _inner;

    public BackupTokenStore(DpapiTokenStore inner) => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public OAuthToken? Load(string key) => _inner.Load(key);

    public void Save(string key, OAuthToken token) => _inner.Save(key, token);

    public void Delete(string key) => _inner.Delete(key);
}
