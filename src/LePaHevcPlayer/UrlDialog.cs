using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace HevcPlayer;

/// <summary>Small dark dialog asking for a network URL (http, https, rtsp…).</summary>
public static class UrlDialog
{
    public static string? Ask(Window owner)
    {
        var dialog = new Window
        {
            Title = "Open URL",
            Owner = owner,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = (System.Windows.Media.Brush)owner.FindResource("BarBg"),
            FontFamily = (System.Windows.Media.FontFamily)owner.FindResource("UiFont")
        };
        dialog.SourceInitialized += (_, _) => DarkTitleBar.Apply(dialog);

        var input = new TextBox();
        if (Clipboard.ContainsText() && Uri.TryCreate(Clipboard.GetText().Trim(), UriKind.Absolute, out var clip) && !clip.IsFile)
            input.Text = clip.ToString();

        var ok = new Button { Content = "Open", Style = (Style)owner.FindResource("AccentPillButton"), IsDefault = true, Height = 34 };
        var cancel = new Button { Content = "Cancel", Style = (Style)owner.FindResource("PillButton"), IsCancel = true, Height = 34, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => dialog.DialogResult = true;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock
        {
            Text = "Video or stream address (http, https, rtsp…)",
            Foreground = (System.Windows.Media.Brush)owner.FindResource("TextSecondary"),
            Margin = new Thickness(0, 0, 0, 8)
        });
        root.Children.Add(input);
        root.Children.Add(buttons);
        dialog.Content = root;

        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); Keyboard.Focus(input); };
        return dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(input.Text) ? input.Text.Trim() : null;
    }
}
