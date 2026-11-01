using System.Net;

namespace WinIDM.Browser;

/// <summary>Supported proxy types.</summary>
public enum ProxyMode
{
    Direct,
    Http,
    Socks5,
}

/// <summary>
/// Proxy configuration: manual (HTTP), SOCKS5, or Tor (SOCKS5 127.0.0.1:9050).
/// HTTP mode can be used as an <see cref="IWebProxy"/>. SOCKS5 and Tor modes need
/// <see cref="CreateSocketsHandler"/> because SocketsHttpHandler connects through SOCKS5 directly,
/// and the host name is sent to the proxy (no local DNS resolution).
/// The proxy password is kept in memory only and is never logged.
/// </summary>
public sealed class ProxyConfig : IWebProxy
{
    public const int TorDefaultPort = 9050;

    private static readonly string[] DefaultBypass = { "localhost", "127.0.0.1", "::1" };

    private ProxyConfig(ProxyMode mode, string? host, int port, string? userName, string? password, IEnumerable<string>? bypass)
    {
        Mode = mode;
        Host = host;
        Port = port;
        UserName = userName;
        Password = password;
        BypassList = (bypass ?? Array.Empty<string>()).Concat(DefaultBypass).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public ProxyMode Mode { get; }
    public string? Host { get; }
    public int Port { get; }
    public string? UserName { get; }

    /// <summary>Proxy password. Never exposed through ToString or logs.</summary>
    internal string? Password { get; }

    public IReadOnlyList<string> BypassList { get; }

    /// <inheritdoc />
    public ICredentials? Credentials { get; set; }

    public static ProxyConfig Direct() => new(ProxyMode.Direct, null, 0, null, null, null);

    public static ProxyConfig Http(string host, int port, string? userName = null, string? password = null, IEnumerable<string>? bypass = null)
    {
        ValidateEndpoint(host, port);
        return new ProxyConfig(ProxyMode.Http, host, port, userName, password, bypass);
    }

    public static ProxyConfig Socks5(string host, int port, string? userName = null, string? password = null)
    {
        ValidateEndpoint(host, port);
        return new ProxyConfig(ProxyMode.Socks5, host, port, userName, password, null);
    }

    /// <summary>Local Tor proxy. Default port 9050 (Tor service), or 9150 for Tor Browser.</summary>
    public static ProxyConfig Tor(int port = TorDefaultPort) => Socks5("127.0.0.1", port);

    /// <summary>Returns the HTTP proxy URI, or the destination when no HTTP proxy is used.</summary>
    public Uri? GetProxy(Uri destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (Mode != ProxyMode.Http || IsBypassed(destination)) return destination;
        return new Uri($"http://{Host}:{Port.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
    }

    /// <summary>True if the destination host does not need to go through the proxy.</summary>
    public bool IsBypassed(Uri destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (Mode == ProxyMode.Direct) return true;
        string host = destination.Host;
        return BypassList.Any(b => string.Equals(b, host, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Build a SocketsHttpHandler that honors this configuration. Cookies are disabled and automatic
    /// redirects are left enabled (redirects do not change the proxy).
    /// </summary>
    public SocketsHttpHandler CreateSocketsHandler()
    {
        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };

        switch (Mode)
        {
            case ProxyMode.Direct:
                handler.UseProxy = false;
                break;

            case ProxyMode.Http:
                handler.UseProxy = true;
                handler.Proxy = this;
                break;

            case ProxyMode.Socks5:
                handler.UseProxy = false;
                string host = Host!;
                int port = Port;
                string? user = UserName;
                string? pass = Password;
                handler.ConnectCallback = async (context, token) =>
                    await Socks5Client.ConnectAsync(host, port, context.DnsEndPoint.Host, context.DnsEndPoint.Port, user, pass, token)
                        .ConfigureAwait(false);
                break;
        }
        return handler;
    }

    public override string ToString() => Mode switch
    {
        ProxyMode.Direct => "Direct",
        _ => $"{Mode} {Host}:{Port}",
    };

    private static void ValidateEndpoint(string host, int port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port), "Invalid proxy port.");
    }
}
