using System.Windows;
using WinIDM.App.Views;
using WinIDM.Browser;

namespace WinIDM.App.Services;

/// <summary>
/// Shared PIN flow for the password manager and the browser: set up, unlock, change PIN.
/// All dialogs are modal windows owned by <paramref name="owner"/> (may be null).
/// </summary>
public static class VaultPrompts
{
    /// <summary>Set up the vault if it does not exist yet. Returns true if the vault is now open.</summary>
    public static async Task<bool> SetupAsync(Composition c, Window? owner)
    {
        if (c.Vault.IsInitialized) return await UnlockAsync(c, owner);

        var dlg = new PinDialogWindow(PinDialogMode.Setup) { Owner = owner };
        if (dlg.ShowDialog() != true) return false;
        try
        {
            await c.Vault.SetupAsync(dlg.Pin);
            return c.Vault.IsUnlocked;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            MessageBox.Show(owner, ex.Message, "Password manager", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    /// <summary>
    /// Unlock the vault with the PIN. Stops when it succeeds, the user cancels, or a lockout is active.
    /// Returns true if the vault is open.
    /// </summary>
    public static async Task<bool> UnlockAsync(Composition c, Window? owner)
    {
        if (!c.Vault.IsInitialized) return await SetupAsync(c, owner);

        // Windows Hello (optional): an extra gate before the PIN. The vault key is still derived from the PIN,
        // so the PIN is still required after a successful Hello verification.
        if (!c.Vault.IsUnlocked && c.Settings.Bool(Keys.WindowsHello))
        {
            var hello = await WindowsHelloGate.VerifyAsync("Verify your identity to open the WinIDM password vault");
            if (hello == WindowsHelloGate.Outcome.Denied) return false;
        }

        string? message = null;
        while (true)
        {
            if (c.Vault.IsUnlocked) return true;

            var wait = c.Vault.LockoutRemaining;
            if (wait > TimeSpan.Zero)
            {
                MessageBox.Show(owner,
                    $"Too many failed PIN attempts. Try again in {Math.Ceiling(wait.TotalSeconds)} seconds.",
                    "Password manager", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            var dlg = new PinDialogWindow(PinDialogMode.Unlock, message) { Owner = owner };
            if (dlg.ShowDialog() != true) return false;

            try
            {
                if (await c.Vault.UnlockAsync(dlg.Pin)) return true;
                message = "Incorrect PIN.";
            }
            catch (InvalidOperationException ex)
            {
                // For example: the vault was created by another Windows account (DPAPI does not match).
                MessageBox.Show(owner, ex.Message, "Password manager", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }
    }

    /// <summary>Change the PIN. The vault must be unlocked first. Returns true on success.</summary>
    public static async Task<bool> ChangePinAsync(Composition c, Window? owner)
    {
        if (!c.Vault.IsUnlocked && !await UnlockAsync(c, owner)) return false;

        var dlg = new PinDialogWindow(PinDialogMode.Change) { Owner = owner };
        if (dlg.ShowDialog() != true) return false;
        try
        {
            await c.Vault.ChangePinAsync(dlg.OldPin, dlg.Pin);
            MessageBox.Show(owner, "PIN changed successfully.", "Password manager", MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            MessageBox.Show(owner, ex.Message, "Password manager", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }
}
