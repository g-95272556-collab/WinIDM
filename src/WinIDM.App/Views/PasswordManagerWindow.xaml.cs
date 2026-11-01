using System.Windows;
using WinIDM.App.Services;
using WinIDM.App.ViewModels;

namespace WinIDM.App.Views;

public partial class PasswordManagerWindow : Window
{
    private readonly Composition _c;
    private readonly PasswordManagerViewModel _vm;

    public PasswordManagerWindow(Composition composition, PasswordManagerViewModel vm)
    {
        InitializeComponent();
        _c = composition;
        _vm = vm;
        DataContext = vm;
        Loaded += async (_, _) =>
        {
            await _vm.RefreshAsync();
            if (_vm.ShowUnlock && await VaultPrompts.UnlockAsync(_c, this)) await _vm.RefreshAsync();
        };
        Closed += (_, _) =>
        {
            PwBox.Clear();
            _vm.Dispose();
        };
    }

    private async void Setup_Click(object sender, RoutedEventArgs e)
    {
        if (await VaultPrompts.SetupAsync(_c, this)) await _vm.RefreshAsync();
    }

    private async void Unlock_Click(object sender, RoutedEventArgs e)
    {
        if (await VaultPrompts.UnlockAsync(_c, this)) await _vm.RefreshAsync();
    }

    private async void ChangePin_Click(object sender, RoutedEventArgs e)
    {
        await VaultPrompts.ChangePinAsync(_c, this);
        await _vm.RefreshAsync();
    }

    private void Lock_Click(object sender, RoutedEventArgs e)
    {
        PwBox.Clear();
        _vm.Lock();
    }

    private void NewEntry_Click(object sender, RoutedEventArgs e)
    {
        _vm.ClearForm();
        PwBox.Clear();
    }

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Selected is not { } row) return;
        var password = await _vm.BeginEditAsync(row);
        if (password is not null) PwBox.Password = password;
    }

    private async void Copy_Click(object sender, RoutedEventArgs e) => await _vm.CopySelectedAsync();

    private async void Delete_Click(object sender, RoutedEventArgs e) => await _vm.DeleteSelectedAsync();

    private void Generate_Click(object sender, RoutedEventArgs e)
    {
        var generated = _vm.GeneratePassword();
        if (generated is not null) PwBox.Password = generated;
    }

    private void ClearForm_Click(object sender, RoutedEventArgs e)
    {
        _vm.ClearForm();
        PwBox.Clear();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (await _vm.SaveAsync(PwBox.Password)) PwBox.Clear();
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import passwords (CSV)",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) == true) await _vm.ImportCsvAsync(dlg.FileName);
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export passwords (CSV, plain text)",
            Filter = "CSV files (*.csv)|*.csv",
            FileName = "winidm-passwords.csv",
        };
        if (dlg.ShowDialog(this) == true) await _vm.ExportCsvAsync(dlg.FileName);
    }
}
