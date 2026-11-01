namespace WinIDM.Backup;

/// <summary>
/// OAuth token store (access and refresh). App implements it with DPAPI; tests use
/// <see cref="InMemoryTokenStore"/>. Implementations must never log token values.
/// </summary>
public interface ITokenStore
{
    OAuthToken? Load(string key);

    void Save(string key, OAuthToken token);

    void Delete(string key);
}

/// <summary>In-memory token store (for tests and contexts without disk access).</summary>
public sealed class InMemoryTokenStore : ITokenStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, OAuthToken> _tokens = new(StringComparer.Ordinal);

    public OAuthToken? Load(string key)
    {
        lock (_gate)
        {
            return _tokens.TryGetValue(key, out var token) ? token : null;
        }
    }

    public void Save(string key, OAuthToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(token);
        lock (_gate)
        {
            _tokens[key] = token;
        }
    }

    public void Delete(string key)
    {
        lock (_gate)
        {
            _tokens.Remove(key);
        }
    }
}
