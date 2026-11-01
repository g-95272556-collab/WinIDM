using System.Windows;

namespace WinIDM.App.Views;

/// <summary>Read-only text view (properties, checksum, long messages) with a copy button.</summary>
public partial class TextInfoWindow : Window
{
    public TextInfoWindow(string title, string text)
    {
        InitializeComponent();
        Title = title;
        Body.Text = text;
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => Clipboard.SetText(Body.Text);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
