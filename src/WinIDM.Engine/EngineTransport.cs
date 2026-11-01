using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using WinIDM.Core.Contracts;

namespace WinIDM.Engine;

/// <summary>
/// Creates the <see cref="SocketsHttpHandler"/> for downloads: HTTP/HTTPS and SOCKS5 proxies (with auth) from
/// <see cref="ISettingsStore"/>, plus a custom host resolver through <see cref="SocketsHttpHandler.ConnectCallback"/>.
/// </summary>
internal static class EngineTransport
{
    /// <summary>Maximum connections per server (01 section 4.9: pool of 64, limited here to 32 per host).</summary>
    public const int MaxConnectionsPerServer = 32;

    /// <param name="settings">Proxy settings are read from here (03a keys).</param>
    /// <param name="connectTimeout">TCP/TLS connection timeout.</param>
    /// <param name="hostResolver">Optional custom host resolver. Null = system DNS.</param>
    /// <param name="tlsCertificateCallback">
    /// Optional custom TLS certificate validator for TLS connections, including TLS to an HTTPS proxy. Null = the system default validation.
    /// Used in tests only; the App does not set it.
    /// </param>
    /// <exception cref="TransferException">Invalid proxy configuration (code 182).</exception>
    public static SocketsHttpHandler Create(
        ISettingsStore settings,
        TimeSpan connectTimeout,
        Func<string, CancellationToken, Task<IPAddress[]>>? hostResolver,
        RemoteCertificateValidationCallback? tlsCertificateCallback = null)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseProxy = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = MaxConnectionsPerServer,
            ConnectTimeout = connectTimeout,
        };

        if (hostResolver is not null)
        {
            handler.ConnectCallback = (context, ct) => ConnectAsync(context.DnsEndPoint, hostResolver, ct);
        }

        if (tlsCertificateCallback is not null)
        {
            handler.SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = tlsCertificateCallback };
        }

        if (!settings.Get(SettingKeys.ProxyEnabled, SettingDefaults.ProxyEnabled))
        {
            return handler;
        }

        try
        {
            var proxy = BuildProxy(settings);
            handler.UseProxy = true;
            handler.Proxy = proxy;
            return handler;
        }
        catch
        {
            handler.Dispose();
            throw;
        }
    }

    /// <summary>Builds a <see cref="WebProxy"/> from settings. The scheme is <c>http</c> or <c>socks5</c>.</summary>
    internal static WebProxy BuildProxy(ISettingsStore settings)
    {
        var type = settings.Get(SettingKeys.ProxyType, SettingDefaults.ProxyType);
        var scheme = type switch
        {
            ProxyTypes.Http => "http",
            ProxyTypes.Socks5 => "socks5",
            ProxyTypes.HttpsTls => "https",
            _ => throw new TransferException(EngineErrorCodes.InvalidProxy,
                $"Unknown proxy type {type} (0 = HTTP/HTTPS, 1 = SOCKS5, 2 = HTTPS/TLS to a proxy).", retryable: false),
        };

        var host = settings.Get(SettingKeys.ProxyHost, SettingDefaults.ProxyHost);
        var port = settings.Get(SettingKeys.ProxyPort, SettingDefaults.ProxyPort);
        if (string.IsNullOrWhiteSpace(host) || port is < 1 or > 65535)
        {
            throw new TransferException(EngineErrorCodes.InvalidProxy,
                "Invalid proxy host or port (the port must be 1-65535).", retryable: false);
        }

        // The scheme comes from the proxy type, not the host. A host containing "://" is rejected so the scheme is not misread.
        if (host.Contains("://", StringComparison.Ordinal))
        {
            throw new TransferException(EngineErrorCodes.InvalidProxy,
                "The proxy host must not contain a scheme (for example 'https://'). Enter the host only and choose the proxy type.", retryable: false);
        }

        var builder = new UriBuilder(scheme, host.Trim(), port);
        var proxy = new WebProxy(builder.Uri);

        var user = settings.Get(SettingKeys.ProxyUser, SettingDefaults.ProxyUser);
        if (!string.IsNullOrEmpty(user))
        {
            proxy.Credentials = new NetworkCredential(user, settings.Get(SettingKeys.ProxyPass, SettingDefaults.ProxyPass));
        }

        return proxy;
    }

    /// <summary>Connects to an address resolved by <paramref name="resolver"/>; tries each address in order.</summary>
    internal static async ValueTask<Stream> ConnectAsync(DnsEndPoint endpoint, Func<string, CancellationToken, Task<IPAddress[]>> resolver, CancellationToken ct)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(endpoint.Host, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            addresses = await resolver(endpoint.Host, ct) ?? [];
        }

        if (addresses.Length == 0)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }

        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex) when (!ct.IsCancellationRequested)
            {
                socket.Dispose();
                last = ex;
            }
        }

        throw last ?? new SocketException((int)SocketError.HostNotFound);
    }
}
