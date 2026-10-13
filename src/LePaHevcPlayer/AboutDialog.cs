using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HevcPlayer;

/// <summary>About dialog with version info and links to the LePa website and the source code.</summary>
public static class AboutDialog
{
    // utm_* only tells the website that the visit came from the app; nothing about the user is sent.
    private const string WebsiteUrl = "https://lepa.ai/?utm_source=lepa-hevc-player&utm_medium=app&utm_campaign=about";
    private const string SourceUrl = "https://github.com/emanueleparini/LePa-HEVC-Video-Player";

    public static void Show(Window owner, string libVlcVersion)
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "";
        Brush Res(string key) => (Brush)owner.FindResource(key);

        var dialog = new Window
        {
            Title = "About LePa HEVC Player",
            Owner = owner,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = Res("BarBg"),
            Foreground = Res("TextPrimary"),
            FontFamily = (FontFamily)owner.FindResource("UiFont")
        };
        dialog.SourceInitialized += (_, _) => DarkTitleBar.Apply(dialog);

        var root = new StackPanel { Margin = new Thickness(28, 24, 28, 20) };
        root.Children.Add(new Image
        {
            Source = new BitmapImage(new Uri("pack://application:,,,/Assets/logo-symbol.png")),
            Width = 72,
            Height = 72,
            Margin = new Thickness(0, 0, 0, 14)
        });
        root.Children.Add(new TextBlock
        {
            Text = "LePa HEVC Player",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        root.Children.Add(new TextBlock
        {
            Text = $"Version {version} · libVLC {libVlcVersion}",
            Foreground = Res("TextSecondary"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 16)
        });
        root.Children.Add(new TextBlock
        {
            Text = "Free, open-source video player with built-in HEVC / H.265 decoding and hardware " +
                   "acceleration: no paid HEVC Video Extensions required.",
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Foreground = Res("TextSecondary"),
            Margin = new Thickness(0, 0, 0, 18)
        });
        root.Children.Add(LinkLine(owner, "Made by LePa s.r.l. · ", "lepa.ai", WebsiteUrl));
        root.Children.Add(LinkLine(owner, "MIT licensed · ", "Source code on GitHub", SourceUrl));

        var close = new Button
        {
            Content = "Close",
            Style = (Style)owner.FindResource("AccentPillButton"),
            IsDefault = true,
            IsCancel = true,
            Height = 34,
            MinWidth = 96,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 20, 0, 0)
        };
        close.Click += (_, _) => dialog.Close();
        root.Children.Add(close);

        dialog.Content = root;
        dialog.ShowDialog();
    }

    private static TextBlock LinkLine(Window owner, string prefix, string text, string url)
    {
        var link = new Hyperlink(new Run(text))
        {
            NavigateUri = new Uri(url),
            Foreground = (Brush)owner.FindResource("Accent"),
            TextDecorations = null,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        link.RequestNavigate += (_, e) =>
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        };
        var line = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = (Brush)owner.FindResource("TextSecondary"),
            Margin = new Thickness(0, 0, 0, 6)
        };
        line.Inlines.Add(prefix);
        line.Inlines.Add(link);
        return line;
    }
}
