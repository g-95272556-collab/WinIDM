using System.Windows;
using System.Windows.Controls;

namespace WinIDM.App.Views;

/// <summary>User choice on the SSL error warning.</summary>
public enum SslDecision
{
    /// <summary>Abort the connection (default and recommended).</summary>
    Cancel,
    /// <summary>Continue for this host only until the browser window closes. Not saved to disk.</summary>
    AllowForSession,
}

/// <summary>
/// SSL certificate error warning. The default is cancel. Continuing is temporary (this window session) and is
/// recorded in the "Site permissions" list so it stays visible.
/// </summary>
public static class BrowserSslDialog
{
    public static SslDecision Ask(Window owner, string host, string errorStatus)
    {
        var window = new Window
        {
            Title = "SSL certificate warning",
            Width = 500,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner,
        };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = $"The SSL certificate for {host} could not be verified.",
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"Reason: {errorStatus}. An attacker may be able to read or change the data being sent. " +
                   "Continue only if you are sure about this site.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 14),
        });

        var decision = SslDecision.Cancel;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel (recommended)", Padding = new Thickness(12, 4, 12, 4), IsDefault = true, IsCancel = true };
        cancel.Click += (_, _) => window.Close();
        var allow = new Button { Content = "Continue temporarily", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0) };
        allow.Click += (_, _) => { decision = SslDecision.AllowForSession; window.Close(); };
        buttons.Children.Add(cancel);
        buttons.Children.Add(allow);
        panel.Children.Add(buttons);
        window.Content = panel;
        window.ShowDialog();
        return decision;
    }
}
