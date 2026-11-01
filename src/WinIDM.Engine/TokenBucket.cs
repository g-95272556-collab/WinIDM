using System.Diagnostics;

namespace WinIDM.Engine;

/// <summary>
/// Token bucket for the speed limit. The capacity is one second of budget.
/// A limit &lt;= 0 means unlimited. Safe for many concurrent workers.
/// </summary>
public sealed class TokenBucket
{
    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _rate;
    private double _tokens;
    private double _lastRefillSeconds;

    public TokenBucket(long bytesPerSecond = 0)
    {
        _rate = Math.Max(0, bytesPerSecond);
        _lastRefillSeconds = _clock.Elapsed.TotalSeconds;
    }

    /// <summary>Current limit in bytes per second; 0 means unlimited.</summary>
    public long BytesPerSecond
    {
        get
        {
            lock (_gate) return _rate;
        }
        set
        {
            lock (_gate)
            {
                Refill();
                _rate = Math.Max(0, value);
                _tokens = Math.Min(_tokens, _rate);
            }
        }
    }

    /// <summary>
    /// Deducts <paramref name="bytes"/> from the budget and waits until the remaining budget is positive again.
    /// </summary>
    public async Task ConsumeAsync(long bytes, CancellationToken ct = default)
    {
        if (bytes <= 0) return;

        TimeSpan wait;
        lock (_gate)
        {
            if (_rate <= 0) return;

            Refill();
            _tokens -= bytes;
            if (_tokens >= 0) return;

            wait = TimeSpan.FromSeconds(-_tokens / _rate);
        }

        await Task.Delay(wait, ct);
    }

    /// <summary>Refills tokens according to elapsed time. Must be called while holding the lock.</summary>
    private void Refill()
    {
        var now = _clock.Elapsed.TotalSeconds;
        var elapsed = now - _lastRefillSeconds;
        _lastRefillSeconds = now;
        if (_rate <= 0) return;

        _tokens = Math.Min(_rate, _tokens + elapsed * _rate);
    }
}
