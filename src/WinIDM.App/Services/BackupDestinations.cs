using System.Diagnostics;
using System.Net;
using System.Net.Http;
using WinIDM.Backup;

namespace WinIDM.App.Services;

public enum BackupDestinationKind { Local, GoogleDrive, Dropbox, Rest }

/// <summary>
/// Builds backup destinations from settings. A network connection is only made when the user
/// connects an account or runs a backup to the chosen destination. Tokens are stored by
/// <see cref="BackupTokenStore"/> (DPAPI) through its own <see cref="LoopbackPkceAuthorizer"/>.
/// </summary>
public sealed class BackupDestinations
{
    public static readonly string[] Choices = { "Local", "Google Drive", "Dropbox", "REST server" };

    /// <summary>Drive folder for backups (drive.file scope: the app creates this folder itself).</summary>
    public const string DriveFolderName = "WinIDM Backups";

    private const string GoogleProvider = "googledrive";
    private const string DropboxProvider = "dropbox";

    private readonly AppSettings _settings;
    private readonly BackupTokenStore _store;
    private readonly DpapiTokenStore _tokens;
    private readonly HttpClient _http;

    public BackupDestinations(AppSettings settings, DpapiTokenStore tokens, HttpClient http)
    {
        _settings = settings;
        _tokens = tokens;
        _store = new BackupTokenStore(tokens);
        _http = http;
    }

    public static BackupDestinationKind KindFromChoice(string? choice) => choice switch
    {
        "Google Drive" => BackupDestinationKind.GoogleDrive,
        "Dropbox" => BackupDestinationKind.Dropbox,
        "REST server" => BackupDestinationKind.Rest,
        _ => BackupDestinationKind.Local,
    };

    public static string ChoiceFromKind(BackupDestinationKind kind) => kind switch
    {
        BackupDestinationKind.GoogleDrive => "Google Drive",
        BackupDestinationKind.Dropbox => "Dropbox",
        BackupDestinationKind.Rest => "REST server",
        _ => "Local",
    };

    public bool IsConnected(BackupDestinationKind kind) => kind switch
    {
        BackupDestinationKind.GoogleDrive => _tokens.Load(GoogleProvider) is not null,
        BackupDestinationKind.Dropbox => _tokens.Load(DropboxProvider) is not null,
        _ => false,
    };

    /// <summary>True when the OAuth client id for this destination is already stored in the user settings.</summary>
    public bool IsClientIdConfigured(BackupDestinationKind kind) => kind switch
    {
        BackupDestinationKind.GoogleDrive => !string.IsNullOrWhiteSpace(_settings.Text(BackupKeys.GoogleClientId)),
        BackupDestinationKind.Dropbox => !string.IsNullOrWhiteSpace(_settings.Text(BackupKeys.DropboxClientId)),
        _ => true,
    };

    /// <summary>Connect a cloud account through OAuth PKCE (loopback server). Tokens are stored DPAPI-protected.</summary>
    public async Task ConnectAsync(BackupDestinationKind kind, CancellationToken ct = default)
    {
        var (_, authorizer) = CreateAuthorizer(kind);
        await authorizer.AuthorizeAsync(ct);
    }

    public void Disconnect(BackupDestinationKind kind)
    {
        if (kind == BackupDestinationKind.GoogleDrive) _tokens.Delete(GoogleProvider);
        if (kind == BackupDestinationKind.Dropbox) _tokens.Delete(DropboxProvider);
    }

    /// <summary>
    /// Build a destination from the current settings. Throws <see cref="InvalidOperationException"/> with a user-facing
    /// message when incomplete. Background token refresh never opens a browser (re-authorization needs user action).
    /// </summary>
    public IBackupDestination Create(BackupDestinationKind kind)
    {
        switch (kind)
        {
            case BackupDestinationKind.Local:
            {
                var folder = _settings.Text(BackupKeys.LocalFolder).Trim();
                if (folder.Length == 0) throw new InvalidOperationException("Select a local backup folder first.");
                return new LocalFolderDestination(folder);
            }
            case BackupDestinationKind.GoogleDrive:
            {
                var (_, authorizer) = CreateAuthorizer(kind);
                if (!IsConnected(kind)) throw new InvalidOperationException("Connect Google Drive first.");
                return new GoogleDriveDestination(authorizer, _http, new GoogleDriveOptions
                {
                    Location = GoogleDriveLocation.NamedFolder,
                    FolderName = DriveFolderName,
                });
            }
            case BackupDestinationKind.Dropbox:
            {
                var (_, authorizer) = CreateAuthorizer(kind);
                if (!IsConnected(kind)) throw new InvalidOperationException("Connect Dropbox first.");
                return new DropboxDestination(authorizer, _http);
            }
            case BackupDestinationKind.Rest:
            {
                var raw = _settings.Text(BackupKeys.RestUrl).Trim();
                if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) throw new InvalidOperationException("Invalid REST server URL.");
                // Privacy: backups go over HTTPS only, except loopback for a local server.
                var isLoopback = uri.Host == "localhost" || (IPAddress.TryParse(uri.Host, out var ip) && IPAddress.IsLoopback(ip));
                if (uri.Scheme != Uri.UriSchemeHttps && !isLoopback)
                    throw new InvalidOperationException("The REST server must use https.");
                var token = _settings.Text(BackupKeys.RestToken);
                return new RestServerDestination(_http, uri, string.IsNullOrEmpty(token) ? null : token);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private (string Provider, LoopbackPkceAuthorizer Authorizer) CreateAuthorizer(BackupDestinationKind kind)
    {
        var secretKey = kind == BackupDestinationKind.GoogleDrive ? BackupKeys.GoogleSecret : BackupKeys.DropboxSecret;
        var clientKey = kind == BackupDestinationKind.GoogleDrive ? BackupKeys.GoogleClientId : BackupKeys.DropboxClientId;
        var clientId = _settings.Text(clientKey).Trim();
        if (clientId.Length == 0)
            throw new InvalidOperationException("Enter your own OAuth client id in the backup window first.");
        var secret = _settings.Text(secretKey).Trim();
        var secretOrNull = secret.Length == 0 ? null : secret;

        var options = kind == BackupDestinationKind.GoogleDrive
            ? OAuthClientOptions.ForGoogleDrive(clientId, secretOrNull)
            : OAuthClientOptions.ForDropbox(clientId, secretOrNull);
        var provider = kind == BackupDestinationKind.GoogleDrive ? GoogleProvider : DropboxProvider;
        var authorizer = new LoopbackPkceAuthorizer(
            options,
            _http,
            OpenInBrowserAsync,
            tokenStore: _store,
            tokenKey: provider,
            allowInteractiveFallback: false);
        return (provider, authorizer);
    }

    private static Task OpenInBrowserAsync(Uri uri, CancellationToken ct)
    {
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        return Task.CompletedTask;
    }
}

/// <summary>Destination resolved on every use, so setting changes apply without a restart.</summary>
public sealed class LazyBackupDestination : IBackupDestination
{
    private readonly Func<IBackupDestination> _factory;

    public LazyBackupDestination(Func<IBackupDestination> factory) => _factory = factory;

    public string DisplayName => "Dynamic destination";

    public Task UploadAsync(string localPath, string remoteName, CancellationToken ct = default) =>
        _factory().UploadAsync(localPath, remoteName, ct);

    public Task<IReadOnlyList<BackupEntry>> ListAsync(CancellationToken ct = default) => _factory().ListAsync(ct);

    public Task DownloadAsync(string remoteName, string localPath, CancellationToken ct = default) =>
        _factory().DownloadAsync(remoteName, localPath, ct);

    public Task DeleteAsync(string remoteName, CancellationToken ct = default) => _factory().DeleteAsync(remoteName, ct);
}
