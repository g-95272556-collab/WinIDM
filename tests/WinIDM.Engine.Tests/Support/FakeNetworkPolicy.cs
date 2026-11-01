using WinIDM.Engine;

namespace WinIDM.Engine.Tests.Support;

/// <summary>
/// Fake network policy: the state can be changed mid-download. Change a value, then call
/// <see cref="Raise"/> to simulate a Windows event (or let the engine tick re-evaluate).
/// </summary>
public sealed class FakeNetworkPolicy : INetworkPolicy, IDisposable
{
    public bool IsOnline { get; set; } = true;

    public bool IsWifiOrEthernet { get; set; } = true;

    public bool IsMetered { get; set; }

    public bool Disposed { get; private set; }

    public event EventHandler? Changed;

    /// <summary>Raise the <see cref="Changed"/> event.</summary>
    public void Raise() => Changed?.Invoke(this, EventArgs.Empty);

    public void Dispose() => Disposed = true;
}
