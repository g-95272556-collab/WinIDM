using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WinIDM.App.Services;
using WinIDM.App.ViewModels;
using WinIDM.Core.Models;

namespace WinIDM.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly IShell _shell;
    private readonly Composition _c;

    public MainWindow(MainViewModel vm, IShell shell, Composition composition)
    {
        InitializeComponent();
        _vm = vm;
        _shell = shell;
        _c = composition;
        DataContext = vm;
        Drop += OnDropUrl;

        SourceInitialized += (_, _) => TitleBarTheme.Apply(this);
        Loaded += (_, _) => TitleBarTheme.Apply(this);
        ThemeService.Changed += OnThemeChanged;
        Closed += (_, _) => ThemeService.Changed -= OnThemeChanged;
    }

    private void OnThemeChanged() =>
        Dispatcher.BeginInvoke(new Action(() => TitleBarTheme.Apply(this)));

    private void DownloadList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _vm.SelectedRows.Clear();
        foreach (var item in DownloadList.SelectedItems.OfType<DownloadRowViewModel>())
            _vm.SelectedRows.Add(item);
    }

    private void DownloadList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DownloadList.SelectedItem is DownloadRowViewModel row && row.IsCompleted)
            _vm.OpenFileCommand.Execute(row);
    }

    private void OnDropUrl(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.UnicodeText)) return;
        var text = e.Data.GetData(DataFormats.UnicodeText) as string;
        var url = UrlHelper.FirstLine(text);
        if (url is null || !UrlHelper.IsDownloadable(url)) return;
        _ = _shell.AddDownloadAsync(new DownloadRequest { Url = url, SaveDirectory = _c.DefaultDownloadFolder() });
    }
}
