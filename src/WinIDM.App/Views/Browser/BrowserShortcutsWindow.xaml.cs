using System.Windows;
using WinIDM.App.Services;

namespace WinIDM.App.Views;

/// <summary>Edit home page shortcuts (up to 40): add, edit, delete, and reorder.</summary>
public partial class BrowserShortcutsWindow : Window
{
    private sealed record Row(int Position, string Title, string Url);

    private readonly BrowserShortcutStore _store;
    private readonly Action _onChanged;
    private bool _ready;

    public BrowserShortcutsWindow(BrowserShortcutStore store, Action onChanged)
    {
        InitializeComponent();
        _store = store;
        _onChanged = onChanged;
        Refresh();
        _ready = true;
    }

    private int SelectedIndex => List.SelectedIndex;

    private void Refresh(int select = -1)
    {
        var items = _store.All;
        List.ItemsSource = items.Select((s, i) => new Row(i + 1, s.Title, s.Url)).ToList();
        CountText.Text = $"{items.Count} of {BrowserShortcutStore.Max} shortcuts.";
        if (select >= 0 && select < items.Count) List.SelectedIndex = select;
        // The home page is refreshed only after an actual change, not while the window is opening.
        if (_ready) _onChanged();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_store.All.Count >= BrowserShortcutStore.Max)
        {
            StatusText.Text = $"The limit of {BrowserShortcutStore.Max} shortcuts has been reached.";
            return;
        }
        if (Ask("Add shortcut", "HTTP(S) URL", "https://", out var title, out var url) && _store.Add(title, url))
        {
            Refresh(_store.All.Count - 1);
            StatusText.Text = "Shortcut added.";
        }
        else if (url is not null)
        {
            StatusText.Text = "Invalid URL. Use http:// or https://.";
        }
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedIndex < 0) return;
        var current = _store.All[SelectedIndex];
        if (Ask("Edit shortcut", "HTTP(S) URL", current.Url, out var title, out var url, current.Title)
            && _store.Update(SelectedIndex, title, url))
        {
            Refresh(SelectedIndex);
            StatusText.Text = "Shortcut updated.";
        }
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedIndex < 0) return;
        _store.Remove(SelectedIndex);
        Refresh();
    }

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedIndex < 0) return;
        int i = SelectedIndex;
        _store.Move(i, -1);
        Refresh(Math.Max(0, i - 1));
    }

    private void Down_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedIndex < 0) return;
        int i = SelectedIndex;
        _store.Move(i, 1);
        Refresh(Math.Min(_store.All.Count - 1, i + 1));
    }

    /// <summary>Two steps: URL, then title. Returns false if canceled.</summary>
    private bool Ask(string title, string label, string initialUrl, out string shortTitle,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? url, string initialTitle = "")
    {
        shortTitle = "";
        url = null;
        var urlPrompt = new TextPromptWindow(title, label, initialUrl) { Owner = this };
        if (urlPrompt.ShowDialog() != true) return false;
        url = urlPrompt.Value.Trim();

        var titlePrompt = new TextPromptWindow(title, "Title (empty = use URL)", initialTitle) { Owner = this };
        if (titlePrompt.ShowDialog() != true) return false;
        shortTitle = titlePrompt.Value.Trim();
        return true;
    }
}
