using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace WinIDM.App.Views;

/// <summary>One entry in the picker list (bookmark or history).</summary>
public sealed record PickItem(string Title, string Url, string Meta);

/// <summary>
/// Generic picker list for bookmarks and history. The Remove and Clear all buttons are
/// shown only when the corresponding handler is supplied.
/// </summary>
public partial class ListPickerWindow : Window
{
    private readonly Action<string> _open;
    private readonly Action<string>? _remove;
    private readonly Action? _clear;

    private readonly Func<string, IEnumerable<PickItem>?>? _extra;

    /// <param name="extra">Optional action on the selected item (label, run); it returns the refreshed list, or null to keep it.</param>
    public ListPickerWindow(string title, string hint, IEnumerable<PickItem> items,
                            Action<string> open, Action<string>? remove = null, Action? clear = null,
                            (string Label, Func<string, IEnumerable<PickItem>?> Run)? extra = null)
    {
        InitializeComponent();
        Title = title;
        Hint.Text = hint;
        Items.ItemsSource = items.ToList();
        _open = open;
        _remove = remove;
        _clear = clear;
        _extra = extra?.Run;
        RemoveButton.Visibility = remove is null ? Visibility.Collapsed : Visibility.Visible;
        ClearButton.Visibility = clear is null ? Visibility.Collapsed : Visibility.Visible;
        if (extra is { } x)
        {
            ExtraButton.Content = x.Label;
            ExtraButton.Visibility = Visibility.Visible;
        }
    }

    private void Extra_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } s || _extra is null) return;
        if (_extra(s.Url) is { } refreshed) Items.ItemsSource = refreshed.ToList();
    }

    private PickItem? Selected => Items.SelectedItem as PickItem;

    private void Items_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Selected is { } s)
        {
            _open(s.Url);
            Close();
        }
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } s || _remove is null) return;
        _remove(s.Url);
        Items.ItemsSource = Items.ItemsSource.Cast<PickItem>().Where(i => i.Url != s.Url).ToList();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, $"Delete every item in {Title}? This cannot be undone.", Title,
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _clear?.Invoke();
        Items.ItemsSource = new List<PickItem>();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
