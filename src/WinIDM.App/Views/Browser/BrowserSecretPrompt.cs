using System.Windows;
using System.Windows.Controls;

namespace WinIDM.App.Views;

/// <summary>
/// Password box (PasswordBox) for the export passphrase. The value is not displayed and is not stored
/// after the dialog closes. Returns null if canceled.
/// </summary>
public static class BrowserSecretPrompt
{
    public static string? Ask(Window owner, string title, string label)
    {
        var window = new Window
        {
            Title = title,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner,
        };
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
        var box = new PasswordBox { Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(box);

        string? result = null;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "OK", Padding = new Thickness(14, 3, 14, 3), IsDefault = true };
        ok.Click += (_, _) => { result = box.Password; window.Close(); };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        cancel.Click += (_, _) => window.Close();
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        window.Content = panel;
        window.Loaded += (_, _) => box.Focus();
        window.ShowDialog();
        return result;
    }
}
