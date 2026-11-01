using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using WinIDM.App.ViewModels;
using WinIDM.Torrent;

namespace WinIDM.App.Views;

public partial class TorrentWindow : Window
{
    private readonly TorrentViewModel _vm;

    public TorrentWindow(TorrentViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Closed += (_, _) => _vm.Dispose();
    }

    /// <summary>Fill the source box (magnet or .torrent path) from outside, e.g. a command-line argument.</summary>
    public void SetSource(string source) => _vm.SetSource(source);

    private void PickTorrent_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose torrent file",
            Filter = "Torrent files (*.torrent)|*.torrent",
        };
        if (dlg.ShowDialog(this) == true) _vm.SetSource(dlg.FileName);
    }

    /// <summary>File priority is changed in the cell ComboBox. The call is made only when the value actually changes.</summary>
    private void FilePriority_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { DataContext: TorrentFileRowViewModel file } combo) return;
        if (combo.SelectedValue is not TorrentFilePriority chosen || file.Priority == chosen) return;
        _ = _vm.SetFilePriorityAsync(file, chosen);
    }

    private void SaveTorrent_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Selected is null) return;
        var invalid = Path.GetInvalidFileNameChars();
        var baseName = new string(_vm.Selected.Name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save torrent file",
            Filter = "Torrent files (*.torrent)|*.torrent",
            FileName = string.IsNullOrWhiteSpace(baseName) ? "torrent.torrent" : baseName + ".torrent",
        };
        if (dlg.ShowDialog(this) == true) _ = _vm.SaveTorrentCopyAsync(dlg.FileName);
    }
}
