using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace HevcPlayer;

public partial class App : Application
{
    public static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LePa HEVC Player", "error.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains("--build-plugin-cache"))
        {
            // Build step (see build.ps1): write libvlc/win-x64/plugins/plugins.dat, like VLC's
            // vlc-cache-gen. Without it libVLC loads every plugin DLL at startup, which on a fresh
            // install (antivirus scanning each new file) can take tens of seconds.
            LibVLCSharp.Shared.Core.Initialize();
            using (new LibVLCSharp.Shared.LibVLC("--reset-plugins-cache")) { }
            Environment.Exit(0);
        }

        // The libVLC overlay is a separate window: without this the process could outlive the main window.
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}\n\n");
        }
        catch (Exception)
        {
            // Logging is best effort.
        }
        MessageBox.Show(e.Exception.Message, "LePa HEVC Player — error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
