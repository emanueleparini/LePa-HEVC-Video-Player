# AGENTS.md — LePa HEVC Player

Context for anyone (human or agent) working on this repo. Read it before changing code; update it
when you change a structural decision or discover a new pitfall.

## What this is

A free, open-source **video player for Windows** whose reason to exist is playing **HEVC / H.265**
files without the paid Microsoft Store "HEVC Video Extensions". UI is WPF on .NET 8; decoding and
rendering are delegated to **libVLC 3** via LibVLCSharp (libVLC bundles FFmpeg's HEVC decoder and uses
D3D11VA/DXVA2 on the GPU).

- Public repo: https://github.com/emanueleparini/LePa-HEVC-Video-Player (MIT; libVLC/LibVLCSharp are LGPL 2.1, see THIRD-PARTY-NOTICES.md)
- Brand: LePa. Accent blue `#0A93F6`, brand gradient blue `#0693F6` → lime `#C6E31C`, dark UI.
  The LePa name/logo are not MIT-licensed.
- **All user-facing text is English.** The maintainer may talk to you in Italian; the app, README,
  commits and code comments stay in English.

## Layout

```
src/LePaHevcPlayer/          WPF app (namespace HevcPlayer, assembly "LePa HEVC Player")
  App.xaml                   theme: palette, button/slider/menu/tooltip styles (all custom, dark)
  App.xaml.cs                ShutdownMode, global error handler + log, --build-plugin-cache switch
  MainWindow.xaml(.cs)       the whole player: menus, controls, playlist, fullscreen, shortcuts
  AppSettings.cs             %AppData%\LePa HEVC Player\settings.json (volume, recent files)
  UrlDialog.cs               "Open URL" dialog, built in code
  Assets/                    app.ico, icon-512.png, logo-symbol.png (WPF Resources)
installer/LePaHevcPlayer.iss Inno Setup 6 script
installer/artifact-signing.json  code-signing account/profile (not secret)
build.ps1                    publish → plugin cache → zip → installer, into ./artifacts
.github/workflows/release.yml  tag v* → builds and publishes a GitHub release
docs/images/                 README graphics (banner, screenshots, icon)
PRIVACY.md                   privacy policy (linked from the Microsoft Store listing; keep it true to the code)
```

## Build / run

```powershell
dotnet run --project src/LePaHevcPlayer -c Release   # run
./build.ps1                                          # artifacts/: Setup .exe + portable .zip
./build.ps1 -SkipInstaller                           # without Inno Setup
```

- Version lives **only** in `LePaHevcPlayer.csproj` `<Version>`; build.ps1 and the installer read it.
- Release: bump `<Version>`, commit, `git tag vX.Y.Z && git push --tags` → CI publishes the release.
- Inno Setup installed locally is < 6.3: use `ArchitecturesAllowed=x64`, not `x64compatible`.
- Self-contained win-x64 only (`VlcWindowsX86Enabled=false`). Unused libVLC plugin folders are
  excluded in the csproj (`VlcWindowsX64ExcludeFiles`); if a format stops working, check there first.
- Close any running "LePa HEVC Player.exe" before building, or the output files are locked.
- **Code signing** (`./build.ps1 -Sign`, always on tag builds in CI): Azure Artifact Signing, account
  `lepasigning` (North Europe), certificate profile `LePaPublic`, publisher **LePa s.r.l.** (keep
  `AppPublisher` in the .iss and `<Company>` in the csproj equal to it). Account details are in
  `installer/artifact-signing.json`; the signtool plug-in (dlib) is downloaded into `.tools/`.
  Auth is the Azure CLI only: CI logs in via OIDC (`azure/login`, GitHub environment `release`,
  secrets `AZURE_CLIENT_ID`/`AZURE_TENANT_ID`/`AZURE_SUBSCRIPTION_ID`); locally `az login` with an
  account that has "Artifact Signing Certificate Profile Signer". A 403 means wrong account/role,
  profile name or endpoint region. Our exe/dll are signed **before** the plugin cache is generated
  (see 2.); libVLC's own DLLs are already signed by VideoLAN and must not be touched.

## Architecture decisions and pitfalls (non-obvious, all learned the hard way)

1. **libVLC is created asynchronously** (`InitPlayerAsync`, off the UI thread). Creating `LibVLC`
   can take seconds on a cold start; doing it in the constructor left the app with *no window at all*
   for 7–26 s. Until `_ready` is true, menus/controls are disabled and every entry point
   (`OpenPaths`, key handler, context menu) checks `_ready`; paths opened early go to `_pendingOpen`.
2. **Plugin cache (`plugins.dat`) is generated at build time** by running the exe with
   `--build-plugin-cache` (equivalent of VLC's `vlc-cache-gen`, which the NuGet package lacks).
   Without it libVLC loads every plugin DLL at startup and antivirus scanning of freshly installed
   files made the first launch take ~26 s (2 s with the cache). build.ps1 fails if the cache is missing.
   The cache is tied to file timestamps/sizes: don't post-process the plugin DLLs after generating it.
3. **Airspace / overlay window.** LibVLCSharp.WPF's `VideoView` renders into a native Win32 child and
   hosts its `Content` (our `Overlay` grid: placeholder, OSD, fullscreen controls) in a separate,
   owned, transparent WPF window (`ForegroundWindow`). Consequences:
   - The overlay grid needs a near-transparent background (`#01000000`) to receive mouse input.
   - Keys pressed while the overlay window has focus are forwarded via `Overlay_Loaded`.
   - In fullscreen the `ControlBar` is **re-parented** into the overlay (and back on exit).
   - Changing `Topmost` must also be applied to the overlay window (see `ToggleTopmost`).
4. **Video surface background**: the native child is a Win32 `Static` control that paints light grey.
   It is coloured by answering `WM_CTLCOLORSTATIC` in a `HwndSource` hook (`WndProc`). Setting
   `Background`/`BackColor` via reflection does **not** work (LibVLCSharp 3.10 uses a raw HwndHost,
   not WindowsFormsHost) and bitmap-based backgrounds leave grey bands on resize.
5. **libVLC events arrive on libVLC threads.** Always marshal with `Ui(...)`, which also drops events
   after the window closed (`_closed`) — a queued `Stopped` after `Dispose` crashed the app with an
   AccessViolation. Never call back into the player synchronously from a libVLC event.
6. **Seek slider**: with `IsMoveToPointEnabled` the Slider marks `PreviewMouseLeftButtonDown` as
   handled, so seek handlers are registered with `AddHandler(..., handledEventsToo: true)`.
   XAML-attached handlers silently never fire.
7. `ShutdownMode=OnMainWindowClose`: the overlay window would otherwise keep the process alive.
8. Unhandled UI exceptions are shown and appended to `%AppData%\LePa HEVC Player\error.log` — ask
   for that file when a user reports a crash.

## Verifying changes

- Build must stay at **0 warnings, 0 errors**.
- To check that a file actually plays without GUI interaction, launch the exe with the file as
  argument and poll `Process.MainWindowTitle`: it becomes `"<file name> — LePa HEVC Player"` once
  playback starts. A generated HEVC sample is easy:
  `ffmpeg -f lavfi -i mandelbrot=size=1920x1080:rate=30 -t 20 -c:v libx265 -tag:v hvc1 sample.mp4`
- **Screenshots: never capture the screen** (`CopyFromScreen` etc.). The maintainer's desktop has
  private content and screen captures have leaked it twice, even with overlap checks (the transparent
  overlay window fools hit-testing). Render only the app window with `PrintWindow(hwnd, hdc, 2)`;
  note it does not include the overlay window (placeholder/OSD).
- Posted mouse messages (`PostMessage WM_LBUTTONDOWN`) are ignored by WPF; don't use them to test.
- Test the installer with a per-user silent install into a temp dir and uninstall afterwards:
  `Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /CURRENTUSER /TASKS="" /DIR="<tmp>"`, then `unins000.exe /VERYSILENT`.

## Conventions

- Match the existing style: file-scoped namespace, C# 12 collection expressions, short comments that
  explain *why*, sections in MainWindow.xaml.cs separated by `// ===== Name =====` banners.
- New UI colours/styles go in `App.xaml` resources, not inline.
- Every new action gets a menu entry, a keyboard shortcut and a line in the shortcuts dialog/README table.
- Commit messages in English, imperative mood.
