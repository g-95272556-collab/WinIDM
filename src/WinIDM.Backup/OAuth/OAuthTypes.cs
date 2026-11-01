namespace WinIDM.Backup;

/// <summary>
/// OAuth2 client configuration. The client id is read from user configuration (no key embedded in code).
/// </summary>
public sealed class OAuthClientOptions
{
    /// <summary>Google Drive scope for files created by the app (default).</summary>
    public const string DriveFileScope = "https://www.googleapis.com/auth/drive.file";

    /// <summary>Google Drive scope for the hidden appDataFolder folder.</summary>
    public const string DriveAppDataScope = "https://www.googleapis.com/auth/drive.appdata";

    public string ClientId { get; init; } = string.Empty;

    /// <summary>Optional. PKCE desktop clients usually do not need a secret.</summary>
    public string? ClientSecret { get; init; }

    public required Uri AuthorizationEndpoint { get; init; }

    public required Uri TokenEndpoint { get; init; }

    public string Scope { get; init; } = string.Empty;

    /// <summary>Extra parameters for the authorization request (for example access_type=offline).</summary>
    public IReadOnlyDictionary<string, string> ExtraAuthorizeParameters { get; init; } =
        new Dictionary<string, string>();

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ClientId))
        {
            throw new InvalidOperationException("The OAuth client id is not configured in user settings.");
        }
    }

    public static OAuthClientOptions ForGoogleDrive(string clientId, string? clientSecret = null, string scope = DriveFileScope) => new()
    {
        ClientId = clientId,
        ClientSecret = clientSecret,
        AuthorizationEndpoint = new Uri("https://accounts.google.com/o/oauth2/v2/auth"),
        TokenEndpoint = new Uri("https://oauth2.googleapis.com/token"),
        Scope = scope,
        ExtraAuthorizeParameters = new Dictionary<string, string>
        {
            ["access_type"] = "offline",
            ["prompt"] = "consent",
        },
    };

    public static OAuthClientOptions ForDropbox(string clientId, string? clientSecret = null) => new()
    {
        ClientId = clientId,
        ClientSecret = clientSecret,
        AuthorizationEndpoint = new Uri("https://www.dropbox.com/oauth2/authorize"),
        TokenEndpoint = new Uri("https://api.dropboxapi.com/oauth2/token"),
        ExtraAuthorizeParameters = new Dictionary<string, string>
        {
            ["token_access_type"] = "offline",
        },
    };
}

/// <summary>Access token. A token within 60 seconds of expiry is treated as expired.</summary>
public sealed record OAuthToken(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresUtc)
{
    public bool IsExpired(DateTimeOffset now) => now >= ExpiresUtc - TimeSpan.FromSeconds(60);

    /// <summary>Prevent tokens from being logged accidentally (the default record reveals all fields).</summary>
    public override string ToString() => $"OAuthToken(expires={ExpiresUtc:O}, secrets=hidden)";
}

/// <summary>
/// Access token source for cloud destinations. <see cref="RefreshAccessTokenAsync"/> is called after the server
/// rejects the token (401); the default implementation just calls <see cref="GetAccessTokenAsync"/>.
/// </summary>
public interface IOAuthTokenProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken ct = default);

    Task<string> RefreshAccessTokenAsync(CancellationToken ct = default) => GetAccessTokenAsync(ct);
}
