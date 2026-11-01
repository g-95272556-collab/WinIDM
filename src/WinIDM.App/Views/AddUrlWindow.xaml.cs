using System.Windows;
using WinIDM.App.ViewModels;
using WinIDM.Core.Models;

namespace WinIDM.App.Views;

public partial class AddUrlWindow : Window
{
    private readonly AddUrlViewModel _vm;

    public AddUrlWindow(AddUrlViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Loaded += (_, _) => UrlBox.Focus();
    }

    /// <summary>Validated request. Null if canceled.</summary>
    public DownloadRequest? Result { get; private set; }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose save folder",
            InitialDirectory = _vm.Folder,
        };
        if (dlg.ShowDialog(this) == true) _vm.Folder = dlg.FolderName;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        _vm.Password = PwdBox.Password;
        var req = _vm.Build();
        if (req is null) return;
        Result = req;
        DialogResult = true;
    }
}
