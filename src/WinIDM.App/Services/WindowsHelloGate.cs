using System.Runtime.InteropServices;
using Windows.Security.Credentials.UI;

namespace WinIDM.App.Services;

/// <summary>
/// Windows Hello verification through <see cref="UserConsentVerifier"/>. If the device does not support Hello
/// (no PIN, biometrics or security key), <see cref="Outcome.Unavailable"/> is returned
/// and the caller continues with the vault PIN only.
/// </summary>
public static class WindowsHelloGate
{
    public enum Outcome { Verified, Denied, Unavailable }

    public static async Task<Outcome> VerifyAsync(string message)
    {
        try
        {
            var availability = await UserConsentVerifier.CheckAvailabilityAsync();
            if (availability != UserConsentVerifierAvailability.Available) return Outcome.Unavailable;

            var result = await UserConsentVerifier.RequestVerificationAsync(message);
            return result == UserConsentVerificationResult.Verified ? Outcome.Verified : Outcome.Denied;
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or ExternalException or NotSupportedException)
        {
            AppLog.Error("Windows Hello", ex);
            return Outcome.Unavailable;
        }
    }
}
