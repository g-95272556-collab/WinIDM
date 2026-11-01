using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using WinIDM.App.Services;
using WinIDM.App.ViewModels;

namespace WinIDM.App.Views;

public partial class BackupWindow : Window
{
    private readonly BackupViewModel _vm;

    public BackupWindow(Composition composition, IShell shell)
    {
        InitializeComponent();
        _vm = new BackupViewModel(composition, shell);
        DataContext = _vm;
        // Passwords are read from the PasswordBox before commands run (a PasswordBox cannot be bound).
        PwdBox.PasswordChanged += (_, _) => _vm.Password = PwdBox.Password;
        SchedPwdBox.PasswordChanged += (_, _) => _vm.SchedulePassword = SchedPwdBox.Password;
        RestTokenBox.PasswordChanged += (_, _) => _vm.RestToken = RestTokenBox.Password;
        GoogleSecretBox.PasswordChanged += (_, _) => _vm.GoogleSecret = GoogleSecretBox.Password;
        DropboxSecretBox.PasswordChanged += (_, _) => _vm.DropboxSecret = DropboxSecretBox.Password;
    }

    private void PickSave_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Save backup",
            Filter = "WinIDM backup (*.zip)|*.zip",
            FileName = $"winidm-backup-{DateTime.Now:yyyyMMdd-HHmm}.zip",
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(this) == true) _vm.FilePath = dlg.FileName;
    }

    private void PickOpen_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choose backup file",
            Filter = "WinIDM backup (*.zip)|*.zip|All files|*.*",
        };
        if (dlg.ShowDialog(this) == true) _vm.FilePath = dlg.FileName;
    }

    private void PickFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose local backup folder" };
        if (dlg.ShowDialog(this) == true) _vm.LocalFolder = dlg.FolderName;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
