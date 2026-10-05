using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;
using Microsoft.Win32;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace HevcPlayer;

public partial class MainWindow : Window
{
    private static readonly string[] VideoExtensions =
        [".mp4", ".m4v", ".mkv", ".mov", ".ts", ".m2ts", ".mts", ".hevc", ".h265", ".265", ".avi", ".webm", ".wmv", ".flv", ".3gp"];

    private static readonly string VideoFilter =
        "Video|" + string.Join(";", VideoExtensions.Select(e => "*" + e)) + "|All files|*.*";

    private static readonly float[] Speeds = [0.25f, 0.5f, 0.75f, 1f, 1.25f, 1.5f, 1.75f, 2f, 3f];

    private static readonly (string Label, string? Value)[] AspectRatios =
        [("Auto", null), ("16:9", "16:9"), ("4:3", "4:3"), ("21:9", "21:9"), ("2.39:1", "239:100"), ("1:1", "1:1")];

    private static readonly string SnapshotDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "LePa HEVC Player");

    // Created asynchronously in InitPlayerAsync; every entry point checks _ready first.
    private LibVLC _libVLC = null!;
    private MediaPlayer _player = null!;
    private bool _ready;
    private bool _closed;
    private IReadOnlyList<string>? _pendingOpen;

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly DispatcherTimer _osdTimer = new() { Interval = TimeSpan.FromSeconds(1.4) };
    private readonly DispatcherTimer _hideControlsTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private readonly DispatcherTimer _clickTimer = new() { Interval = TimeSpan.FromMilliseconds(230) };

    private Media? _media;
    private List<string> _playlist = [];
    private int _index = -1;
    private bool _isSeeking;
    private bool _showRemaining;
    private bool _isFullscreen;
    private WindowState _prevState;
    private string? _aspect;

    public MainWindow()
    {
        InitializeComponent();

        VolumeSlider.Value = _settings.Volume;

        _osdTimer.Tick += (_, _) => { Osd.Visibility = Visibility.Collapsed; _osdTimer.Stop(); };
        _hideControlsTimer.Tick += (_, _) => HideFullscreenControls();
        _clickTimer.Tick += (_, _) => { _clickTimer.Stop(); if (_media != null) TogglePlayPause(); };

        // With IsMoveToPointEnabled the Slider marks mouse-down as handled, so listen to handled events too.
        SeekSlider.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(SeekSlider_PreviewMouseLeftButtonDown), true);
        SeekSlider.AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(SeekSlider_PreviewMouseLeftButtonUp), true);

        BuildSpeedItems(SpeedMenu);
        BuildAspectItems(AspectMenu);
        UpdateNavButtons();
    }

    // libVLC raises events on its own thread; never call back into the player from there.
    // Events still queued when the window closes must not touch the disposed player.
    private void Ui(Action action) => Dispatcher.BeginInvoke(() => { if (!_closed) action(); });

    // ===================== Window lifecycle =====================

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        DarkTitleBar.Apply(this);

        // libVLC renders into a native Win32 "Static" child that paints itself light grey and shows
        // through the transparent overlay when nothing is playing. Static controls ask their parent
        // for a background brush via WM_CTLCOLORSTATIC: answer with the window colour.
        var bg = ((SolidColorBrush)FindResource("WindowBg")).Color;
        _videoBackground = NativeMethods.CreateSolidBrush(bg.R | (bg.G << 8) | (bg.B << 16));
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
    }

    private IntPtr _videoBackground;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_CTLCOLORSTATIC = 0x0138;
        if (msg == WM_CTLCOLORSTATIC && _videoBackground != IntPtr.Zero)
        {
            handled = true;
            return _videoBackground;
        }
        return IntPtr.Zero;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (args.Length > 0)
            _pendingOpen = args;

        await InitPlayerAsync();
    }

    private async Task InitPlayerAsync()
    {
        MenuBar.IsEnabled = false;
        ControlBar.IsEnabled = false;
        PlaceholderHint.Text = "Starting video engine…";

        try
        {
            // Creating LibVLC loads ~300 plugin DLLs. On a cold start (first run after install, antivirus
            // scanning new files) this takes several seconds, so keep it off the UI thread.
            // Hardware decoding (D3D11VA/DXVA2) with automatic software fallback; libVLC ships its own
            // HEVC decoder, so the Microsoft Store "HEVC Video Extensions" is not needed.
            _libVLC = await Task.Run(() =>
            {
                Core.Initialize();
                return new LibVLC("--avcodec-hw=any", "--no-video-title-show", "--no-snapshot-preview");
            });
        }
        catch (Exception ex)
        {
            PlaceholderHint.Text = "The video engine failed to start: " + ex.Message;
            throw;
        }

        if (_closed)
        {
            _libVLC.Dispose();
            return;
        }

        _player = new MediaPlayer(_libVLC)
        {
            EnableHardwareDecoding = true,
            EnableMouseInput = false,
            EnableKeyInput = false,
            Volume = (int)VolumeSlider.Value
        };

        _player.Playing += (_, _) => Ui(OnPlaying);
        _player.Paused += (_, _) => Ui(OnPaused);
        _player.Stopped += (_, _) => Ui(OnStopped);
        _player.EndReached += (_, _) => Ui(OnEndReached);
        _player.EncounteredError += (_, _) => Ui(() => ShowOsd("Unable to play this file"));
        _player.LengthChanged += (_, e) => Ui(() =>
        {
            SeekSlider.Maximum = Math.Max(1, e.Length);
            UpdateTimeText(_player.Time);
        });
        _player.TimeChanged += (_, e) => Ui(() =>
        {
            if (_isSeeking) return;
            SeekSlider.Value = e.Time;
            UpdateTimeText(e.Time);
        });

        VideoView.MediaPlayer = _player;
        _ready = true;
        MenuBar.IsEnabled = true;
        ControlBar.IsEnabled = true;
        PlaceholderHint.Text = "Drop a file here or open one from the File menu";
        UpdateNavButtons();

        if (_pendingOpen is { } pending)
        {
            _pendingOpen = null;
            OpenPaths(pending);
        }
    }

    private void Overlay_Loaded(object sender, RoutedEventArgs e)
    {
        // The overlay is hosted in a separate (owned) window above the video: forward its keys too.
        if (Window.GetWindow(Overlay) is { } host && host != this)
        {
            host.PreviewKeyDown -= Window_PreviewKeyDown;
            host.PreviewKeyDown += Window_PreviewKeyDown;
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _settings.Volume = (int)VolumeSlider.Value;
        _settings.Save();

        _closed = true;
        _osdTimer.Stop();
        _hideControlsTimer.Stop();
        _clickTimer.Stop();
        if (!_ready) return; // InitPlayerAsync disposes libVLC itself if it finishes after closing

        _player.Stop();
        _player.Dispose();
        _media?.Dispose();
        _libVLC.Dispose();
    }

    // ===================== Opening =====================

    private void OpenPaths(IReadOnlyList<string> paths)
    {
        if (!_ready)
        {
            _pendingOpen = paths; // opened as soon as the video engine is up
            return;
        }

        if (paths.Count == 1 && Directory.Exists(paths[0]))
        {
            OpenFolder(paths[0]);
            return;
        }

        var files = paths.Where(File.Exists).ToList();
        if (files.Count == 0)
        {
            if (paths.Count == 1 && Uri.TryCreate(paths[0], UriKind.Absolute, out var uri) && !uri.IsFile)
                PlayUrl(uri);
            return;
        }

        if (files.Count == 1)
        {
            // Single file: the playlist is every video in the same folder, so Prev/Next work.
            var file = Path.GetFullPath(files[0]);
            var siblings = VideoFilesIn(Path.GetDirectoryName(file)!);
            if (!siblings.Contains(file, StringComparer.OrdinalIgnoreCase)) siblings.Insert(0, file);
            _playlist = siblings;
            _index = _playlist.FindIndex(f => string.Equals(f, file, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            _playlist = files.Select(Path.GetFullPath).ToList();
            _index = 0;
        }
        PlayCurrent();
    }

    private void OpenFolder(string folder)
    {
        var files = VideoFilesIn(folder);
        if (files.Count == 0)
        {
            ShowOsd("No videos in this folder");
            return;
        }
        _playlist = files;
        _index = 0;
        PlayCurrent();
    }

    private static List<string> VideoFilesIn(string folder)
    {
        try
        {
            var files = Directory.EnumerateFiles(folder)
                .Where(f => VideoExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .ToList();
            files.Sort(NativeMethods.StrCmpLogicalW); // Explorer-style "natural" order
            return files;
        }
        catch (Exception)
        {
            return [];
        }
    }

    private void PlayCurrent()
    {
        if (_index < 0 || _index >= _playlist.Count) return;
        var path = _playlist[_index];
        StartMedia(new Media(_libVLC, path, FromType.FromPath), Path.GetFileName(path));
        _settings.AddRecent(path);
    }

    private void PlayUrl(Uri uri)
    {
        _playlist = [];
        _index = -1;
        StartMedia(new Media(_libVLC, uri), uri.ToString());
        _settings.AddRecent(uri.ToString());
    }

    private void StartMedia(Media media, string displayName)
    {
        var old = _media;
        _media = media;
        _player.Play(_media);
        old?.Dispose();

        if (_aspect != null) _player.AspectRatio = _aspect;
        Title = $"{displayName} — LePa HEVC Player";
        Placeholder.Visibility = Visibility.Collapsed;
        InfoChip.Visibility = Visibility.Collapsed;
        UpdateNavButtons();
    }

    private void CloseMedia()
    {
        _player.Stop();
        _media?.Dispose();
        _media = null;
        _playlist = [];
        _index = -1;
        Title = "LePa HEVC Player";
        Placeholder.Visibility = Visibility.Visible;
        InfoChip.Visibility = Visibility.Collapsed;
        SeekSlider.Maximum = 1;
        UpdateTimeText(0);
        UpdateNavButtons();
    }

    private void ShowOpenDialog()
    {
        var dlg = new OpenFileDialog { Filter = VideoFilter, Title = "Open video", Multiselect = true };
        if (dlg.ShowDialog(this) == true)
            OpenPaths(dlg.FileNames);
    }

    private void ShowOpenFolderDialog()
    {
        var dlg = new OpenFolderDialog { Title = "Open a folder of videos" };
        if (dlg.ShowDialog(this) == true)
            OpenFolder(dlg.FolderName);
    }

    private void ShowOpenUrlDialog()
    {
        var url = UrlDialog.Ask(this);
        if (url == null) return;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            PlayUrl(uri);
        else
            ShowOsd("Invalid URL");
    }

    // ===================== Player state =====================

    private void OnPlaying()
    {
        PlayPauseButton.Content = "";
        Placeholder.Visibility = Visibility.Collapsed;
        var info = DescribeVideoTrack();
        InfoText.Text = info;
        InfoChip.Visibility = string.IsNullOrEmpty(info) ? Visibility.Collapsed : Visibility.Visible;
        ResetHideControlsTimer();
    }

    private void OnPaused()
    {
        PlayPauseButton.Content = "";
        ShowControls();
    }

    private void OnStopped()
    {
        PlayPauseButton.Content = "";
        SeekSlider.Value = 0;
        UpdateTimeText(0);
        ShowControls();
    }

    private void OnEndReached()
    {
        if (RepeatToggle.IsChecked == true && _media != null)
        {
            _player.Stop();
            _player.Play(_media);
        }
        else if (MenuAutoNext.IsChecked && _index >= 0 && _index < _playlist.Count - 1)
        {
            _index++;
            PlayCurrent();
        }
        else
        {
            _player.Stop();
        }
    }

    private void TogglePlayPause()
    {
        if (_media == null) { ShowOpenDialog(); return; }
        if (_player.IsPlaying) _player.Pause();
        else if (_player.State is VLCState.Stopped or VLCState.Ended or VLCState.NothingSpecial or VLCState.Error) _player.Play(_media);
        else _player.SetPause(false);
    }

    private void SeekBy(long ms)
    {
        if (!_player.IsSeekable) return;
        var target = Math.Clamp(_player.Time + ms, 0, Math.Max(0, _player.Length - 500));
        _player.Time = target;
        SeekSlider.Value = target;
        UpdateTimeText(target);
        var sign = ms > 0 ? "+" : "−";
        var amount = Math.Abs(ms) >= 60_000 ? $"{Math.Abs(ms) / 60_000} min" : $"{Math.Abs(ms) / 1000} s";
        ShowOsd($"{sign}{amount}   {FormatTime(target)}");
    }

    private void Navigate(int delta)
    {
        var next = _index + delta;
        if (next < 0 || next >= _playlist.Count) return;
        _index = next;
        PlayCurrent();
        ShowOsd($"{_index + 1}/{_playlist.Count}  {Path.GetFileName(_playlist[_index])}");
    }

    private void UpdateNavButtons()
    {
        PrevButton.IsEnabled = _index > 0;
        NextButton.IsEnabled = _index >= 0 && _index < _playlist.Count - 1;
    }

    private void SetSpeed(float rate)
    {
        _player.SetRate(rate);
        SpeedButton.Content = $"{rate:0.##}×";
        ShowOsd($"Speed {rate:0.##}×");
    }

    private void SetAspect(string? value, string label)
    {
        _aspect = value;
        _player.AspectRatio = value;
        ShowOsd($"Aspect ratio: {label}");
    }

    private void ChangeVolume(int delta) => VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + delta, 0, 100);

    private void ToggleMute()
    {
        _player.Mute = !_player.Mute;
        UpdateVolumeIcon();
        ShowOsd(_player.Mute ? "Muted" : $"Volume {(int)VolumeSlider.Value}%");
    }

    private void UpdateVolumeIcon()
    {
        var v = VolumeSlider.Value;
        MuteButton.Content = _player.Mute || v == 0 ? "" : v < 34 ? "" : v < 67 ? "" : "";
        MenuMute.IsChecked = _player.Mute;
    }

    private void ToggleRepeat()
    {
        RepeatToggle.IsChecked = RepeatToggle.IsChecked != true;
        MenuRepeat.IsChecked = RepeatToggle.IsChecked == true;
        ShowOsd(RepeatToggle.IsChecked == true ? "Repeat: on" : "Repeat: off");
    }

    private void TakeSnapshot()
    {
        if (_media == null || !_player.WillPlay) return;
        Directory.CreateDirectory(SnapshotDir);
        var name = Path.GetFileNameWithoutExtension(Title.Split(" — ")[0]);
        var file = Path.Combine(SnapshotDir, $"{name}_{FormatTime(_player.Time).Replace(':', '-')}_{DateTime.Now:HHmmss}.png");
        ShowOsd(_player.TakeSnapshot(0, file, 0, 0) ? "Screenshot saved to Pictures\\LePa HEVC Player" : "Screenshot failed");
    }

    private void ToggleTopmost()
    {
        Topmost = !Topmost;
        // Keep libVLC's overlay window (controls, OSD) in the same z-order band as the player.
        if (Window.GetWindow(Overlay) is { } overlay && overlay != this) overlay.Topmost = Topmost;
        MenuTopmost.IsChecked = Topmost;
        ShowOsd(Topmost ? "Always on top: on" : "Always on top: off");
    }

    // ===================== Track info =====================

    private string DescribeVideoTrack()
    {
        var track = _media?.Tracks.FirstOrDefault(t => t.TrackType == TrackType.Video);
        if (track is not { } t) return "";
        var h = t.Data.Video.Height;
        var res = h >= 2000 ? "4K" : h > 0 ? $"{h}p" : "";
        return string.Join(" · ", new[] { CodecName(t.Codec), res }.Where(s => s.Length > 0));
    }

    private static string CodecName(uint fourcc)
    {
        var s = Encoding.ASCII.GetString(BitConverter.GetBytes(fourcc)).TrimEnd('\0', ' ').ToUpperInvariant();
        return s switch
        {
            "HEVC" or "H265" or "HVC1" or "HEV1" => "HEVC",
            "H264" or "AVC1" => "H.264",
            "AV01" => "AV1",
            "VP90" => "VP9",
            "MP4A" => "AAC",
            "A52 " or "A52" => "AC-3",
            "EAC3" => "E-AC-3",
            _ => s
        };
    }

    private void ShowMediaInfo()
    {
        if (_media == null) return;
        var sb = new StringBuilder();
        sb.AppendLine(Title.Split(" — ")[0]);
        sb.AppendLine($"Duration: {FormatTime(_player.Length)}");
        sb.AppendLine();
        foreach (var t in _media.Tracks)
        {
            switch (t.TrackType)
            {
                case TrackType.Video:
                    var fps = t.Data.Video.FrameRateDen > 0 ? (double)t.Data.Video.FrameRateNum / t.Data.Video.FrameRateDen : 0;
                    sb.AppendLine($"Video: {CodecName(t.Codec)}  {t.Data.Video.Width}×{t.Data.Video.Height}  {fps:0.##} fps");
                    break;
                case TrackType.Audio:
                    sb.AppendLine($"Audio: {CodecName(t.Codec)}  {t.Data.Audio.Channels} channels  {t.Data.Audio.Rate} Hz  {t.Language}");
                    break;
                case TrackType.Text:
                    sb.AppendLine($"Subtitles: {CodecName(t.Codec)}  {t.Language}");
                    break;
            }
        }
        MessageBox.Show(this, sb.ToString(), "Media information", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ===================== Menu builders =====================

    private void BuildSpeedItems(ItemsControl target)
    {
        target.Items.Clear();
        foreach (var rate in Speeds)
        {
            var item = new MenuItem { Header = rate == 1f ? "Normal" : $"{rate:0.##}×", Tag = rate };
            item.Click += (_, _) => SetSpeed(rate);
            target.Items.Add(item);
        }
        if (target is MenuItem mi) mi.SubmenuOpened += (_, _) => CheckSpeedItems(mi);
    }

    private void CheckSpeedItems(ItemsControl menu)
    {
        foreach (var item in menu.Items.OfType<MenuItem>())
            item.IsChecked = Math.Abs(_player.Rate - (float)item.Tag) < 0.01;
    }

    private void BuildAspectItems(MenuItem target)
    {
        foreach (var (label, value) in AspectRatios)
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => SetAspect(value, label);
            target.Items.Add(item);
        }
        target.SubmenuOpened += (_, _) =>
        {
            for (var i = 0; i < AspectRatios.Length; i++)
                ((MenuItem)target.Items[i]).IsChecked = AspectRatios[i].Value == _aspect;
        };
    }

    private void FillTrackItems(ItemsControl target, TrackDescription[] tracks, int current, Action<int> select, string emptyText)
    {
        target.Items.Clear();
        foreach (var t in tracks)
        {
            var id = t.Id;
            var item = new MenuItem { Header = t.Name, IsChecked = id == current };
            item.Click += (_, _) => select(id);
            target.Items.Add(item);
        }
        if (target.Items.Count == 0)
            target.Items.Add(new MenuItem { Header = emptyText, IsEnabled = false });
    }

    private void FillAudioItems(ItemsControl target) =>
        FillTrackItems(target, _player.AudioTrackDescription, _player.AudioTrack, id => _player.SetAudioTrack(id), "(none)");

    private void FillSubtitleItems(ItemsControl target) =>
        FillTrackItems(target, _player.SpuDescription, _player.Spu, id => _player.SetSpu(id), "(none)");

    private static void OpenPopupMenu(ContextMenu menu, UIElement target)
    {
        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.Top;
        menu.IsOpen = true;
    }

    private ContextMenu BuildVideoContextMenu()
    {
        MenuItem Item(string header, string gesture, Action action, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, InputGestureText = gesture, IsEnabled = enabled };
            mi.Click += (_, _) => action();
            return mi;
        }

        var hasMedia = _media != null;
        var menu = new ContextMenu();
        menu.Items.Add(Item(_player.IsPlaying ? "Pause" : "Play", "Space", TogglePlayPause));
        menu.Items.Add(Item("Open file…", "Ctrl+O", ShowOpenDialog));
        menu.Items.Add(Item("Open folder…", "Ctrl+Shift+O", ShowOpenFolderDialog));
        menu.Items.Add(new Separator());

        var speed = new MenuItem { Header = "Speed", IsEnabled = hasMedia };
        BuildSpeedItems(speed);
        menu.Items.Add(speed);
        var audio = new MenuItem { Header = "Audio track", IsEnabled = hasMedia };
        FillAudioItems(audio);
        menu.Items.Add(audio);
        var subs = new MenuItem { Header = "Subtitles", IsEnabled = hasMedia };
        FillSubtitleItems(subs);
        menu.Items.Add(subs);
        var aspect = new MenuItem { Header = "Aspect ratio", IsEnabled = hasMedia };
        BuildAspectItems(aspect);
        menu.Items.Add(aspect);
        menu.Items.Add(new Separator());

        menu.Items.Add(Item("Screenshot", "Ctrl+S", TakeSnapshot, hasMedia));
        menu.Items.Add(Item(_isFullscreen ? "Exit fullscreen" : "Fullscreen", "F", ToggleFullscreen));
        menu.Items.Add(Item("Media information", "I", ShowMediaInfo, hasMedia));
        return menu;
    }

    // ===================== Menu / button handlers =====================

    private void Open_Click(object sender, RoutedEventArgs e) => ShowOpenDialog();
    private void OpenFolder_Click(object sender, RoutedEventArgs e) => ShowOpenFolderDialog();
    private void OpenUrl_Click(object sender, RoutedEventArgs e) => ShowOpenUrlDialog();
    private void CloseFile_Click(object sender, RoutedEventArgs e) => CloseMedia();
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePlayPause();
    private void Stop_Click(object sender, RoutedEventArgs e) => _player.Stop();
    private void Back10_Click(object sender, RoutedEventArgs e) => SeekBy(-10_000);
    private void Forward30_Click(object sender, RoutedEventArgs e) => SeekBy(30_000);
    private void Back60_Click(object sender, RoutedEventArgs e) => SeekBy(-60_000);
    private void Forward60_Click(object sender, RoutedEventArgs e) => SeekBy(60_000);
    private void NextFrame_Click(object sender, RoutedEventArgs e) => _player.NextFrame();
    private void Prev_Click(object sender, RoutedEventArgs e) => Navigate(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => Navigate(1);
    private void Repeat_Click(object sender, RoutedEventArgs e) => ToggleRepeat();

    private void RepeatButton_Click(object sender, RoutedEventArgs e)
    {
        // The ToggleButton already flipped itself; just sync the menu and notify.
        MenuRepeat.IsChecked = RepeatToggle.IsChecked == true;
        ShowOsd(RepeatToggle.IsChecked == true ? "Repeat: on" : "Repeat: off");
    }

    private void VolumeUp_Click(object sender, RoutedEventArgs e) => ChangeVolume(5);
    private void VolumeDown_Click(object sender, RoutedEventArgs e) => ChangeVolume(-5);
    private void Mute_Click(object sender, RoutedEventArgs e) => ToggleMute();

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();
    private void Topmost_Click(object sender, RoutedEventArgs e) => ToggleTopmost();
    private void Snapshot_Click(object sender, RoutedEventArgs e) => TakeSnapshot();
    private void MediaInfo_Click(object sender, RoutedEventArgs e) => ShowMediaInfo();

    private void OpenSnapshotFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(SnapshotDir);
        Process.Start(new ProcessStartInfo(SnapshotDir) { UseShellExecute = true });
    }

    private void LoadSubtitle_Click(object sender, RoutedEventArgs e)
    {
        if (_media == null) { ShowOsd("Open a video first"); return; }
        var dlg = new OpenFileDialog { Filter = "Subtitles|*.srt;*.ass;*.ssa;*.vtt;*.sub|All files|*.*" };
        if (dlg.ShowDialog(this) == true)
        {
            _player.AddSlave(MediaSlaveType.Subtitle, new Uri(dlg.FileName).AbsoluteUri, true);
            ShowOsd("Subtitles loaded");
        }
    }

    private void PlaybackMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource != sender) return;
        MenuPlayPause.Header = _player.IsPlaying ? "Pause" : "Play";
    }

    private void RecentMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource != sender) return;
        RecentMenu.Items.Clear();
        foreach (var path in _settings.Recent)
        {
            var p = path;
            var item = new MenuItem { Header = Path.GetFileName(p) is { Length: > 0 } n ? n : p, ToolTip = p };
            item.Click += (_, _) => OpenPaths([p]);
            RecentMenu.Items.Add(item);
        }
        if (RecentMenu.Items.Count == 0)
        {
            RecentMenu.Items.Add(new MenuItem { Header = "(empty)", IsEnabled = false });
            return;
        }
        RecentMenu.Items.Add(new Separator());
        var clear = new MenuItem { Header = "Clear list" };
        clear.Click += (_, _) => { _settings.Recent.Clear(); _settings.Save(); };
        RecentMenu.Items.Add(clear);
    }

    private void AudioTrackMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource == sender) FillAudioItems(AudioTrackMenu);
    }

    private void SubtitleTrackMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource == sender) FillSubtitleItems(SubtitleTrackMenu);
    }

    private void SpeedButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        BuildSpeedItems(menu);
        CheckSpeedItems(menu);
        OpenPopupMenu(menu, (UIElement)sender);
    }

    private void AudioButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        FillAudioItems(menu);
        OpenPopupMenu(menu, (UIElement)sender);
    }

    private void SubtitlesButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        FillSubtitleItems(menu);
        menu.Items.Add(new Separator());
        var load = new MenuItem { Header = "Load subtitle file…" };
        load.Click += LoadSubtitle_Click;
        menu.Items.Add(load);
        OpenPopupMenu(menu, (UIElement)sender);
    }

    private void Shortcuts_Click(object sender, RoutedEventArgs e) => ShowShortcuts();

    private void ShowShortcuts() => MessageBox.Show(this,
        "Space / K\tPlay / Pause\n" +
        "← / →\t\tBack / forward 5 s\n" +
        "J / L\t\tBack 10 s / forward 30 s\n" +
        "Ctrl+← / →\tBack / forward 1 min\n" +
        ".\t\tNext frame\n" +
        "PgUp / PgDn\tPrevious / next file\n" +
        "↑ / ↓, wheel\tVolume\n" +
        "M\t\tMute\n" +
        "[ / ]\t\tSpeed − / +\n" +
        "F, double-click\tFullscreen (Esc to exit)\n" +
        "R\t\tRepeat file\n" +
        "T\t\tAlways on top\n" +
        "I\t\tMedia information\n" +
        "Ctrl+S\t\tScreenshot\n" +
        "Ctrl+O\t\tOpen file\n" +
        "Ctrl+Shift+O\tOpen folder\n" +
        "Ctrl+U\t\tOpen URL\n" +
        "Ctrl+W\t\tClose file",
        "Keyboard shortcuts", MessageBoxButton.OK, MessageBoxImage.None);

    private void About_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        $"LePa HEVC Player\n\nOpen-source video player for Windows, powered by libVLC {_libVLC.Version}.\n" +
        "Built-in HEVC/H.265 decoding with hardware acceleration (D3D11VA/DXVA2): " +
        "no paid HEVC Video Extensions from the Microsoft Store required.",
        "About", MessageBoxButton.OK, MessageBoxImage.Information);

    // ===================== Seek bar =====================

    private void SeekSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _isSeeking = true;

    private void SeekSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isSeeking) return;
        _isSeeking = false;
        if (_player.IsSeekable)
            _player.Time = (long)SeekSlider.Value;
    }

    private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isSeeking) return;
        UpdateTimeText((long)e.NewValue);

        // Live preview while dragging, throttled so libVLC is not flooded with seeks.
        if (_player.IsSeekable && DateTime.UtcNow - _lastLiveSeek > TimeSpan.FromMilliseconds(150))
        {
            _lastLiveSeek = DateTime.UtcNow;
            _player.Time = (long)e.NewValue;
        }
    }

    private DateTime _lastLiveSeek;

    private void SeekSlider_MouseMove(object sender, MouseEventArgs e)
    {
        if (_player.Length <= 0) { SeekPreview.IsOpen = false; return; }
        var x = e.GetPosition(SeekSlider).X;
        const double thumb = 14;
        var ratio = Math.Clamp((x - thumb / 2) / Math.Max(1, SeekSlider.ActualWidth - thumb), 0, 1);
        SeekPreviewText.Text = FormatTime((long)(ratio * _player.Length));
        SeekPreview.IsOpen = true;
        var child = (FrameworkElement)SeekPreview.Child;
        child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        SeekPreview.HorizontalOffset = x - child.DesiredSize.Width / 2;
    }

    private void SeekSlider_MouseLeave(object sender, MouseEventArgs e) => SeekPreview.IsOpen = false;

    private void LengthText_Click(object sender, MouseButtonEventArgs e)
    {
        _showRemaining = !_showRemaining;
        UpdateTimeText(_player.Time);
    }

    private void UpdateTimeText(long time)
    {
        var length = Math.Max(0, _player.Length);
        TimeText.Text = FormatTime(time);
        LengthText.Text = _showRemaining ? $" / −{FormatTime(length - time)}" : $" / {FormatTime(length)}";
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_player == null) return; // fired during InitializeComponent
        _player.Volume = (int)e.NewValue;
        if (_player.Mute) _player.Mute = false;
        UpdateVolumeIcon();
        if (IsLoaded) ShowOsd($"Volume {(int)e.NewValue}%");
    }

    // ===================== Mouse / keyboard / drag & drop =====================

    private void Overlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ControlBar.IsMouseOver || e.Handled) return;
        Activate();
        if (e.ClickCount == 2)
        {
            _clickTimer.Stop();
            ToggleFullscreen();
        }
        else if (e.ClickCount == 1)
        {
            _clickTimer.Start(); // single click = play/pause, unless a double click follows
        }
    }

    private void Overlay_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_ready || ControlBar.IsMouseOver) return;
        var menu = BuildVideoContextMenu();
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void Overlay_MouseWheel(object sender, MouseWheelEventArgs e) => ChangeVolume(e.Delta > 0 ? 5 : -5);

    private Point _lastMouse;

    private void Overlay_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isFullscreen) return;
        var p = e.GetPosition(Overlay);
        if ((p - _lastMouse).Length < 2) return; // ignore jitter
        _lastMouse = p;
        ShowControls();
        ResetHideControlsTimer();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_ready || e.OriginalSource is TextBox) return;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        switch (key)
        {
            case Key.O when ctrl && shift: ShowOpenFolderDialog(); break;
            case Key.O when ctrl: ShowOpenDialog(); break;
            case Key.U when ctrl: ShowOpenUrlDialog(); break;
            case Key.W when ctrl: CloseMedia(); break;
            case Key.S when ctrl: TakeSnapshot(); break;
            case Key.Space or Key.K or Key.MediaPlayPause: TogglePlayPause(); break;
            case Key.S or Key.MediaStop: _player.Stop(); break;
            case Key.Left: SeekBy(ctrl ? -60_000 : -5_000); break;
            case Key.Right: SeekBy(ctrl ? 60_000 : 5_000); break;
            case Key.J: SeekBy(-10_000); break;
            case Key.L: SeekBy(30_000); break;
            case Key.OemPeriod: _player.NextFrame(); break;
            case Key.PageUp or Key.MediaPreviousTrack: Navigate(-1); break;
            case Key.PageDown or Key.MediaNextTrack: Navigate(1); break;
            case Key.Up: ChangeVolume(5); break;
            case Key.Down: ChangeVolume(-5); break;
            case Key.M: ToggleMute(); break;
            case Key.OemOpenBrackets: StepSpeed(-1); break;
            case Key.OemCloseBrackets: StepSpeed(1); break;
            case Key.F or Key.Enter: ToggleFullscreen(); break;
            case Key.Escape when _isFullscreen: ToggleFullscreen(); break;
            case Key.R: ToggleRepeat(); break;
            case Key.T: ToggleTopmost(); break;
            case Key.I: ShowMediaInfo(); break;
            case Key.F1: ShowShortcuts(); break;
            default: return;
        }
        e.Handled = true;
    }

    private void StepSpeed(int direction)
    {
        var current = Array.FindIndex(Speeds, s => Math.Abs(s - _player.Rate) < 0.01);
        if (current < 0) current = Array.IndexOf(Speeds, 1f);
        SetSpeed(Speeds[Math.Clamp(current + direction, 0, Speeds.Length - 1)]);
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
        {
            OpenPaths(paths);
            Activate();
        }
        e.Handled = true;
    }

    // ===================== Fullscreen =====================

    private void ToggleFullscreen()
    {
        if (!_isFullscreen)
        {
            _prevState = WindowState;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal; // required so Maximized also covers the taskbar
            WindowState = WindowState.Maximized;
            MenuBar.Visibility = Visibility.Collapsed;

            // Float the controls over the video.
            RootGrid.Children.Remove(ControlBar);
            ControlBar.VerticalAlignment = VerticalAlignment.Bottom;
            ControlBar.Background = new LinearGradientBrush(Colors.Transparent, Color.FromArgb(0xE6, 0, 0, 0), 90);
            ControlBar.Padding = new Thickness(32, 48, 32, 20);
            Overlay.Children.Add(ControlBar);

            FullscreenButton.Content = "";
            _isFullscreen = true;
            ResetHideControlsTimer();
        }
        else
        {
            _hideControlsTimer.Stop();
            Overlay.Children.Remove(ControlBar);
            ControlBar.ClearValue(VerticalAlignmentProperty);
            ControlBar.Background = (Brush)FindResource("BarBg");
            ControlBar.Padding = new Thickness(16, 6, 16, 8);
            RootGrid.Children.Add(ControlBar);

            MenuBar.Visibility = Visibility.Visible;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = _prevState;
            FullscreenButton.Content = "";
            _isFullscreen = false;
            ShowControls();
        }
    }

    private void ShowControls()
    {
        ControlBar.Visibility = Visibility.Visible;
        Overlay.Cursor = null;
    }

    private void HideFullscreenControls()
    {
        _hideControlsTimer.Stop();
        if (!_isFullscreen || !_player.IsPlaying) return;
        if (ControlBar.IsMouseOver || SeekPreview.IsOpen) { ResetHideControlsTimer(); return; }
        ControlBar.Visibility = Visibility.Collapsed;
        Overlay.Cursor = Cursors.None;
    }

    private void ResetHideControlsTimer()
    {
        _hideControlsTimer.Stop();
        if (_isFullscreen) _hideControlsTimer.Start();
    }

    // ===================== Helpers =====================

    private void ShowOsd(string text)
    {
        OsdText.Text = text;
        Osd.Visibility = Visibility.Visible;
        _osdTimer.Stop();
        _osdTimer.Start();
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in FindVisualChildren<T>(child)) yield return nested;
        }
    }

    private static string FormatTime(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
    }
}

internal static class NativeMethods
{
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    public static extern int StrCmpLogicalW(string x, string y);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateSolidBrush(int colorRef);
}

/// <summary>Dark Windows 11 title bar matching the app palette.</summary>
internal static class DarkTitleBar
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var on = 1;
        DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));        // DWMWA_USE_IMMERSIVE_DARK_MODE
        var caption = 0x001A1616;                                    // COLORREF (BGR) of #16161A
        DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));   // DWMWA_CAPTION_COLOR (Win11)
        var border = 0x001A1616;
        DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int));    // DWMWA_BORDER_COLOR (Win11)
    }
}
