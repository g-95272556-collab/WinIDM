using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using WinIDM.App.Services;
using WinIDM.App.ViewModels;

namespace WinIDM.App.Views;

public partial class ThemeEditorWindow : Window
{
    private readonly ThemeEditorViewModel _vm;
    private bool _saved;

    public ThemeEditorWindow(ThemeEditorViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Closed += (_, _) =>
        {
            // An unsaved preview is reverted to the real settings.
            if (!_saved) ThemeService.Apply(ThemeService.Choice, ThemeService.CustomJson);
        };
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ThemeEditorViewModel.Status) && _vm.Status.StartsWith("Custom theme saved", StringComparison.Ordinal))
                _saved = true;
        };
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Import WinIDM theme", Filter = "WinIDM theme (*.json)|*.json|All files|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(dlg.FileName).Length > ThemePalette.MaxJsonBytes)
            {
                _vm.Status = "Import canceled: the file is too large (max 64 KB).";
                return;
            }

            _vm.ImportJson(File.ReadAllText(dlg.FileName));
        }
        catch (InvalidDataException ex)
        {
            _vm.Status = $"Import failed: {ex.Message}";
        }
        catch (IOException ex)
        {
            AppLog.Error("Theme import", ex);
            _vm.Status = "Import failed: the file could not be read.";
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Export WinIDM theme",
            Filter = "WinIDM theme (*.json)|*.json",
            FileName = "winidm-theme.json",
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, _vm.ExportJson());
            _vm.Status = "Theme exported.";
        }
        catch (IOException ex)
        {
            AppLog.Error("Theme export", ex);
            _vm.Status = "Export failed: the file could not be written.";
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}

/// <summary>Converts HEX text (#RRGGBB or #AARRGGBB) to a color; invalid values render as a red square.</summary>
public sealed class HexBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var hex = value as string;
        if (!ThemePalette.IsValidColor(hex)) return new SolidColorBrush(Colors.Red);
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex!));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
