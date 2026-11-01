using System.Text;
using System.Windows;
using System.Windows.Controls;
using WinIDM.Browser;

namespace WinIDM.App.Views;

/// <summary>Helper dialogs for the script manager: install confirmation and permission selection.</summary>
public static class BrowserScriptDialogs
{
    /// <summary>Shows the install summary. The user must confirm before the script is saved.</summary>
    public static bool ConfirmInstall(Window owner, UserScriptPreview preview)
    {
        var m = preview.Meta;
        var sb = new StringBuilder();
        sb.AppendLine($"Name: {m.Name}");
        sb.AppendLine($"Version: {m.Version}");
        if (!string.IsNullOrWhiteSpace(m.Author)) sb.AppendLine($"Author: {m.Author}");
        if (!string.IsNullOrWhiteSpace(m.Description)) sb.AppendLine($"Description: {Truncate(m.Description!, 300)}");
        sb.AppendLine();
        sb.AppendLine($"Target sites: {m.Matches.Count} @match, {m.Includes.Count} @include");
        sb.AppendLine($"Declared permissions: {(m.Grants.Count == 0 ? "none" : string.Join(", ", m.Grants))}");
        sb.AppendLine($"@connect hosts: {(m.Connects.Count == 0 ? "none" : string.Join(", ", m.Connects))}");
        sb.AppendLine($"@require/@resource SHA-256 pins: {preview.Pins.Count}");
        sb.AppendLine($"Source SHA-256: {preview.SourceSha256[..Math.Min(16, preview.SourceSha256.Length)]}...");
        if (!string.IsNullOrEmpty(preview.SourceUrl)) sb.AppendLine($"Source: {preview.SourceUrl}");
        sb.AppendLine();
        sb.AppendLine("Sensitive permissions are not granted automatically. You can manage them after installation.");
        sb.AppendLine("Install this script?");
        return MessageBox.Show(owner, sb.ToString(), "Install user script",
                               MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    /// <summary>
    /// Per-script permission dialog. Only sensitive permissions declared by the script are shown.
    /// Returns the selected permission list, or null if canceled.
    /// </summary>
    public static IReadOnlyList<string>? PickPermissions(Window owner, UserScriptMeta meta, IReadOnlyList<string> approved)
    {
        var candidates = meta.Grants.Where(UserScriptPermissions.IsSensitive).Distinct().ToList();
        if (meta.Connects.Contains("*")) candidates.Add(UserScriptPermissions.ConnectWildcard);

        if (candidates.Count == 0)
        {
            MessageBox.Show(owner, "This script declares no sensitive permissions.", "Permissions",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }

        var window = new Window
        {
            Title = "Script permissions: " + meta.Name,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner,
        };
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock
        {
            Text = "Grant only the permissions you need. Sensitive permissions can be changed at any time.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        });
        var boxes = new List<(string Permission, CheckBox Box)>();
        foreach (var permission in candidates)
        {
            var box = new CheckBox
            {
                Content = permission == UserScriptPermissions.ConnectWildcard
                    ? "Connect to any host (@connect *)"
                    : permission,
                IsChecked = approved.Contains(permission, StringComparer.Ordinal),
                Margin = new Thickness(0, 4, 0, 4),
            };
            boxes.Add((permission, box));
            panel.Children.Add(box);
        }

        bool? ok = null;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var save = new Button { Content = "Save", Padding = new Thickness(14, 3, 14, 3), IsDefault = true };
        save.Click += (_, _) => { ok = true; window.Close(); };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        cancel.Click += (_, _) => window.Close();
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        window.Content = panel;
        window.ShowDialog();

        if (ok != true) return null;
        return boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Permission).ToList();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
