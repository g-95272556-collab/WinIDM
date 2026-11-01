using System.Windows;

namespace WinIDM.App.Views;

/// <summary>Single-line input box (for example a new URL for refreshing a link).</summary>
public partial class TextPromptWindow : Window
{
    public TextPromptWindow(string title, string label, string initial)
    {
        InitializeComponent();
        Title = title;
        PromptLabel.Text = label;
        Input.Text = initial;
        Loaded += (_, _) => { Input.Focus(); Input.SelectAll(); };
    }

    /// <summary>The value entered by the user (when OK is pressed).</summary>
    public string Value { get; private set; } = "";

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Value = Input.Text.Trim();
        DialogResult = true;
    }
}
