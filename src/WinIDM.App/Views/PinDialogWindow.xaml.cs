using System.Windows;
using WinIDM.Browser;

namespace WinIDM.App.Views;

public enum PinDialogMode { Setup, Unlock, Change }

/// <summary>PIN dialog to set up, unlock, or change the password manager PIN. Password boxes are cleared on close.</summary>
public partial class PinDialogWindow : Window
{
    private readonly PinDialogMode _mode;

    public PinDialogWindow(PinDialogMode mode, string? message = null)
    {
        InitializeComponent();
        _mode = mode;

        var range = $"{PasswordVault.MinPinLength}-{PasswordVault.MaxPinLength} characters";
        switch (mode)
        {
            case PinDialogMode.Setup:
                Title = Heading.Text = "Set up password manager";
                Hint.Text = $"PIN {range}. If the PIN is forgotten, the stored passwords cannot be recovered.";
                OldLabel.Visibility = OldBox.Visibility = Visibility.Collapsed;
                PinLabel.Text = "New PIN";
                break;
            case PinDialogMode.Unlock:
                Title = Heading.Text = "Unlock password manager";
                Hint.Text = $"Enter the PIN ({range}).";
                OldLabel.Visibility = OldBox.Visibility = Visibility.Collapsed;
                ConfirmLabel.Visibility = ConfirmBox.Visibility = Visibility.Collapsed;
                break;
            case PinDialogMode.Change:
                Title = Heading.Text = "Change PIN";
                Hint.Text = $"The new PIN must be {range}.";
                PinLabel.Text = "New PIN";
                break;
        }
        ErrorText.Text = message ?? "";
        Closed += (_, _) =>
        {
            OldBox.Clear();
            PinBox.Clear();
            ConfirmBox.Clear();
        };
        Loaded += (_, _) => (mode == PinDialogMode.Change ? OldBox : PinBox).Focus();
    }

    /// <summary>The PIN that was entered (unlock PIN, or the new PIN for setup/change).</summary>
    public string Pin { get; private set; } = "";

    /// <summary>The current PIN (change-PIN mode only).</summary>
    public string OldPin { get; private set; } = "";

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var pin = PinBox.Password;
        var confirm = ConfirmBox.Password;
        var old = OldBox.Password;
        var range = $"{PasswordVault.MinPinLength}-{PasswordVault.MaxPinLength} characters";

        if (_mode == PinDialogMode.Change && old.Length == 0)
        {
            ErrorText.Text = "Enter the current PIN.";
            return;
        }
        if (pin.Length < PasswordVault.MinPinLength || pin.Length > PasswordVault.MaxPinLength)
        {
            ErrorText.Text = $"The PIN must be {range}.";
            return;
        }
        if (_mode != PinDialogMode.Unlock && pin != confirm)
        {
            ErrorText.Text = "The PIN and confirmation do not match.";
            return;
        }

        Pin = pin;
        OldPin = old;
        DialogResult = true;
    }
}
