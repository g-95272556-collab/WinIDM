using System.Net;
using WinIDM.Browser;
using WinIDM.Core.Contracts;

namespace WinIDM.App.Services;

/// <summary>
/// Host resolver for the download engine. Settings are re-read on every call, so
/// a DNS mode change takes effect for new connections without a restart.
/// Mode 0 (system) uses the Windows DNS. Modes 1 (DoH) and 2 (DoT) use <see cref="DnsResolver"/> from WinIDM.Browser.
/// If the DoH/DoT configuration is invalid, the system DNS is used and an error is logged (without host details).
/// </summary>
public sealed class DnsHostResolver : IDisposable
{
    private readonly ISettingsStore _settings;
    private readonly object _gate = new();
    private string _configKey = "";
    private DnsResolver? _resolver;
    private bool _disposed;

    public DnsHostResolver(ISettingsStore settings) => _settings = settings;

    public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        var resolver = CurrentResolver();
        if (resolver is null) return await Dns.GetHostAddressesAsync(host, ct);

        try
        {
            var list = await resolver.ResolveAsync(host, ct);
            return list.ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Error("Custom DNS resolution", ex);
            return await Dns.GetHostAddressesAsync(host, ct);
        }
    }

    private DnsResolver? CurrentResolver()
    {
        var mode = _settings.Get(Keys.DnsMode, 0);
        var doh = _settings.Get(Keys.DnsDohUrl, "");
        var dot = _settings.Get(Keys.DnsDotHost, "");
        var key = $"{mode}|{doh}|{dot}";

        lock (_gate)
        {
            if (_disposed) return null;
            if (key == _configKey) return _resolver;

            _resolver?.Dispose();
            _resolver = null;
            _configKey = key;

            if (mode == 1 && Uri.TryCreate(doh, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            {
                _resolver = new DnsResolver(new DohUpstream(uri));
            }
            else if (mode == 2 && !string.IsNullOrWhiteSpace(dot))
            {
                _resolver = new DnsResolver(new DotUpstream(dot.Trim()));
            }
            else if (mode is 1 or 2)
            {
                AppLog.Info("Invalid custom DNS: using the system DNS.");
            }
            return _resolver;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _resolver?.Dispose();
            _resolver = null;
        }
    }
}
