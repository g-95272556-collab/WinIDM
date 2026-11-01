using System.Windows;
using WinIDM.App.ViewModels;

namespace WinIDM.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
