using System.Diagnostics;
using System.IO;
using System.Windows;
using WinIDM.App.Services;

namespace WinIDM.App.Views;

/// <summary>List of pages saved by the browser: open, remove from the list, or delete the file.</summary>
public partial class SavedPagesWindow : Window
{
    private readonly Composition _c;

    public SavedPagesWindow(Composition composition)
    {
        InitializeComponent();
        _c = composition;
        Reload();
    }

    private SavedPage? Selected => List.SelectedItem as SavedPage;

    private void Reload() => List.ItemsSource = _c.SavedPages.All;

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } page) return;
        if (!File.Exists(page.FilePath))
        {
            StatusText.Text = "The file no longer exists at its original location.";
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(page.FilePath) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Error("Open saved page", ex);
            StatusText.Text = "Could not open the file. See the log for details.";
        }
    }

    private void Forget_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } page) return;
        _c.SavedPages.Remove(page.FilePath);
        Reload();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } page) return;
        if (MessageBox.Show(this, $"Delete the file \"{page.FilePath}\" from disk? This action cannot be undone.",
                "Saved pages", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        try
        {
            if (File.Exists(page.FilePath)) File.Delete(page.FilePath);
            _c.SavedPages.Remove(page.FilePath);
            StatusText.Text = "File deleted.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Delete saved page", ex);
            StatusText.Text = "Could not delete the file. See the log for details.";
        }
        Reload();
    }
}
