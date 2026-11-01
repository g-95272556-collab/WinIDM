using System.Globalization;
using WinIDM.Core.Contracts;

namespace WinIDM.Engine;

/// <summary>
/// Network state the engine reads to decide whether downloads are allowed. Implemented by the App
/// (Windows.Networking.Connectivity) or by tests (a fake implementation).
/// </summary>
public interface INetworkPolicy
{
    /// <summary>True if there is an Internet connection.</summary>
    bool IsOnline { get; }

    /// <summary>
    /// True if the connection is Wi-Fi or Ethernet (not mobile data). The <see cref="SettingKeys.WifiOnly"/> setting
    /// ('Wi-Fi only', 03a: "Ignore mobile internet") needs this value.
    /// </summary>
    bool IsWifiOrEthernet { get; }

    /// <summary>True if the connection is marked metered (fixed cost, variable cost, or a data limit nearly/totally reached).</summary>
    bool IsMetered { get; }

    /// <summary>Raised when the network state changes. The engine also re-evaluates periodically (about 1 second).</summary>
    event EventHandler? Changed;
}

/// <summary>
/// Default policy when the App supplies none: no connection restrictions, no metered detection.
/// Only <see cref="IsWifiOrEthernet"/> is read from <see cref="NetworkStatus"/> (for 'Wi-Fi only').
/// </summary>
public sealed class DefaultNetworkPolicy : INetworkPolicy
{
    public bool IsOnline => true;

    public bool IsWifiOrEthernet => NetworkStatus.IsWifiOrEthernetConnected();

    public bool IsMetered => false;

    public event EventHandler? Changed
    {
        add { }
        remove { }
    }
}

/// <summary>
/// Download byte counts on metered networks, for the daily and monthly limits. Counts are stored in
/// <see cref="ISettingsStore"/> so they persist across restarts. Writes are batched and saved during <see cref="Flush"/>
/// (called every tick), because <see cref="JsonSettingsStore.Set{T}"/> writes the file every time.
/// </summary>
internal sealed class MeteredUsage
{
    private const long Mib = 1024 * 1024;

    private readonly ISettingsStore _settings;
    private readonly object _gate = new();
    private string _day;
    private long _dayBytes;
    private string _month;
    private long _monthBytes;
    private bool _dirty;

    public MeteredUsage(ISettingsStore settings, DateTimeOffset now)
    {
        _settings = settings;
        var today = DayKey(now);
        var thisMonth = MonthKey(now);

        // A stored value is only valid if its date/month matches the current one; otherwise the count starts at zero.
        _day = today;
        _dayBytes = settings.Get(SettingKeys.MeteredUsageDay, string.Empty) == today
            ? settings.Get(SettingKeys.MeteredUsageDayBytes, 0L)
            : 0L;

        _month = thisMonth;
        _monthBytes = settings.Get(SettingKeys.MeteredUsageMonth, string.Empty) == thisMonth
            ? settings.Get(SettingKeys.MeteredUsageMonthBytes, 0L)
            : 0L;
    }

    /// <summary>Adds bytes transferred over a metered connection.</summary>
    public void Add(long bytes, DateTimeOffset now)
    {
        if (bytes <= 0) return;

        lock (_gate)
        {
            Roll(now);
            _dayBytes += bytes;
            _monthBytes += bytes;
            _dirty = true;
        }
    }

    /// <summary>True if the daily or monthly limit (MiB, 0 = no limit) has been reached or exceeded.</summary>
    public bool IsOverQuota(DateTimeOffset now)
    {
        var daily = _settings.Get(SettingKeys.MeteredDailyLimitMb, SettingDefaults.MeteredDailyLimitMb);
        var monthly = _settings.Get(SettingKeys.MeteredMonthlyLimitMb, SettingDefaults.MeteredMonthlyLimitMb);

        lock (_gate)
        {
            Roll(now);
            return (daily > 0 && _dayBytes >= daily * Mib) || (monthly > 0 && _monthBytes >= monthly * Mib);
        }
    }

    /// <summary>Metered bytes so far for the current day and month.</summary>
    public (long Day, long Month) Totals(DateTimeOffset now)
    {
        lock (_gate)
        {
            Roll(now);
            return (_dayBytes, _monthBytes);
        }
    }

    /// <summary>Saves the counts to settings if anything changed since the last save.</summary>
    public void Flush(DateTimeOffset now)
    {
        lock (_gate)
        {
            Roll(now);
            if (!_dirty) return;

            _settings.Set(SettingKeys.MeteredUsageDay, _day);
            _settings.Set(SettingKeys.MeteredUsageDayBytes, _dayBytes);
            _settings.Set(SettingKeys.MeteredUsageMonth, _month);
            _settings.Set(SettingKeys.MeteredUsageMonthBytes, _monthBytes);
            _dirty = false;
        }
    }

    /// <summary>Resets the day/month counts if the date or month changed. Must be called while holding <see cref="_gate"/>.</summary>
    private void Roll(DateTimeOffset now)
    {
        var today = DayKey(now);
        if (today != _day)
        {
            _day = today;
            _dayBytes = 0;
            _dirty = true;
        }

        var thisMonth = MonthKey(now);
        if (thisMonth != _month)
        {
            _month = thisMonth;
            _monthBytes = 0;
            _dirty = true;
        }
    }

    /// <summary>Local (user) date for the daily limit.</summary>
    internal static string DayKey(DateTimeOffset now) =>
        now.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Local (user) month for the monthly limit.</summary>
    internal static string MonthKey(DateTimeOffset now) =>
        now.ToLocalTime().ToString("yyyy-MM", CultureInfo.InvariantCulture);
}
