using System.Collections;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using WinIDM.App.Services;

namespace WinIDM.App.Views;

/// <summary>
/// IDM+ overlay window: running downloads float above every other window. It never takes focus and stays out of
/// Alt+Tab; size, place, colors and text size come from the overlay settings. Double-click opens WinIDM.
/// </summary>
public partial class OverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(
        nameof(BarBrush), typeof(Brush), typeof(OverlayWindow), new PropertyMetadata(Brushes.LightGreen));

    public OverlayWindow(IEnumerable rows)
    {
        InitializeComponent();
        Rows.ItemsSource = rows;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(hwnd, GwlExStyle, GetWindowLong(hwnd, GwlExStyle) | WsExToolWindow | WsExNoActivate);
        };
    }

    public event Action? OpenRequested;

    public Brush BarBrush
    {
        get => (Brush)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    /// <summary>Reads the overlay settings and places the window on the screen work area.</summary>
    public void Apply(AppSettings s)
    {
        var text = ParseBrush(s.Text(Keys.OverlayTextColor), Colors.White);
        Card.Background = ParseBrush(s.Text(Keys.OverlayBackground), Color.FromArgb(0xCC, 0xF4, 0x6C, 0x36));
        BarBrush = ParseBrush(s.Text(Keys.OverlayProgressColor), Color.FromArgb(0xCC, 0x8C, 0xA6, 0x38));
        SummaryBar.Foreground = BarBrush;
        TextElement.SetForeground(Card, text);
        FontSize = Math.Clamp(s.Int(Keys.OverlayTextSize), 8, 32);

        bool combined = s.Bool(Keys.OverlayCombine);
        CombinedPanel.Visibility = combined ? Visibility.Visible : Visibility.Collapsed;
        ListPanel.Visibility = combined ? Visibility.Collapsed : Visibility.Visible;

        var area = SystemParameters.WorkArea;
        Width = Math.Max(160, area.Width * Math.Clamp(s.Int(Keys.OverlayWidth), 5, 100) / 100.0);
        Height = Math.Max(56, area.Height * Math.Clamp(s.Int(Keys.OverlayHeight), 5, 100) / 100.0);
        double dx = s.Int(Keys.OverlayHOffset), dy = s.Int(Keys.OverlayVOffset);
        Left = s.Text(Keys.OverlayHAlign) switch
        {
            "Left" => area.Left + dx,
            "Center" => area.Left + (area.Width - Width) / 2 + dx,
            _ => area.Right - Width - dx,
        };
        Top = s.Text(Keys.OverlayVAlign) switch
        {
            "Top" => area.Top + dy,
            "Center" => area.Top + (area.Height - Height) / 2 + dy,
            _ => area.Bottom - Height - dy,
        };
    }

    /// <summary>Combined line: "2 active - 45% - 3.1 MB/s" and the overall percentage.</summary>
    public void SetSummary(string? summary, double percent)
    {
        SummaryText.Text = summary ?? "";
        SummaryBar.Value = percent;
    }

    private static Brush ParseBrush(string value, Color fallback)
    {
        try
        {
            if (ColorConverter.ConvertFromString(value.Trim()) is Color c) return Frozen(new SolidColorBrush(c));
        }
        catch (FormatException)
        {
            // fall back below
        }

        return Frozen(new SolidColorBrush(fallback));
    }

    private static Brush Frozen(SolidColorBrush b)
    {
        b.Freeze();
        return b;
    }

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) OpenRequested?.Invoke();
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}
