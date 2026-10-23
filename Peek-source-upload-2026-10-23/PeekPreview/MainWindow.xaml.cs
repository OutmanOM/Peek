using Microsoft.UI.Dispatching;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using SharpCompress.Archives;
using Windows.Media.Core;
using Windows.Media.Playback;
using WinRT.Interop;

namespace PeekPreview
{
    public sealed partial class MainWindow : Window
    {
        private const int WhKeyboardLl = 13;
        private const int WmKeyDown = 0x0100;
        private const int WmKeyUp = 0x0101;
        private const int WmSysKeyDown = 0x0104;
        private const int WmSysKeyUp = 0x0105;
        private const int WmAppTray = 0x8001;
        private const int WmClose = 0x0010;
        private const int WmSysCommand = 0x0112;
        private const int WmActivate = 0x0006;
        private const int ScClose = 0xF060;
        private const int WmLButtonUp = 0x0202;
        private const int WmLButtonDoubleClick = 0x0203;
        private const int WmRButtonUp = 0x0205;
        private const int WmContextMenu = 0x007B;
        private const int GwlStyle = -16;
        private const int SwpNoMove = 0x0002;
        private const int SwpNoSize = 0x0001;
        private const int SwpNoZOrder = 0x0004;
        private const int SwpFrameChanged = 0x0020;
        private const long WsCaption = 0x00C00000L;
        private const long WsSystemMenu = 0x00080000L;
        private const long WsMinimizeBox = 0x00020000L;
        private const long WsMaximizeBox = 0x00010000L;
        private const int VkSpace = 0x20;
        private const int VkEscape = 0x1B;
        private const int VkLeft = 0x25;
        private const int VkRight = 0x27;

        private const uint NimAdd = 0x00000000;
        private const uint NimDelete = 0x00000002;
        private const uint NifMessage = 0x00000001;
        private const uint NifIcon = 0x00000002;
        private const uint NifTip = 0x00000004;
        private const uint NotifyIconVersion4 = 4;
        private const uint MfString = 0x00000000;
        private const uint TpmRightButton = 0x0002;
        private const uint TpmReturnCommand = 0x0100;
        private const uint TrayExitCommand = 1003;
        private const uint ImageIcon = 1;
        private const uint LrLoadFromFile = 0x0010;
        private const uint LrDefaultSize = 0x0040;
        private const string ActivationMessageName = "PeekPreview.Activate.2026";

        private readonly AppWindow _appWindow;
        private readonly DispatcherQueue _dispatcherQueue;
        private readonly LowLevelKeyboardProc _keyboardProc;
        private readonly WindowSubclassProc _windowSubclassProc;
        private readonly List<string> _files = new();
        private readonly HashSet<int> _navigationKeysDown = new();
        private readonly IntPtr _windowHandle;
        private NotifyIconData _notifyIconData;
        private IntPtr _keyboardHook;
        private IntPtr _trayIconHandle;
        private uint _taskbarCreatedMessage;
        private uint _activateMessage;
        private bool _trayIconAdded;
        private bool _spaceIsDown;
        private bool _suppressSpaceUntilKeyUp;
        private bool _isEnabled = true;
        private bool _isExiting;
        private bool _isPreviewVisible;
        private bool _showingTrayMenu;
        private int _focusLossCheckId;
        private int _currentIndex;
        private int _previewRequestId;
        private MediaPlayer? _mediaPlayer;
        private int _activeMediaRequestId;
        private string? _activeMediaExtension;
        private object? _shellApplication;

        public MainWindow()
        {
            InitializeComponent();

            _windowHandle = WindowNative.GetWindowHandle(this);
            _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_windowHandle));
            _appWindow.IsShownInSwitchers = false;   // no taskbar button / Alt-Tab entry
            _appWindow.SetPresenter(CreateWindowPresenter());
            _appWindow.Closing += PreviewWindow_Closing;
            RemoveWindowChrome();
            PositionWindow();
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            Activated += MainWindow_Activated;
            Closed += MainWindow_Closed;

            _keyboardProc = KeyboardHookCallback;
            _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, IntPtr.Zero, 0);

            _mediaPlayer = new MediaPlayer { AutoPlay = true };
            _mediaPlayer.MediaFailed += (_, _) => _dispatcherQueue.TryEnqueue(() =>
            {
                if (_activeMediaRequestId != _previewRequestId || !_isPreviewVisible) return;
                RecordPreviewTelemetry(string.Empty, _activeMediaExtension ?? "media", 0, "media-error");
                EmptyState.Visibility = Visibility.Visible;
                EmptyTitle.Text = "Couldn't play this file";
                EmptyDescription.Text = "The media decoder reported a playback error.";
            });
            MediaPreview.SetMediaPlayer(_mediaPlayer);

            _windowSubclassProc = WindowSubclassCallback;
            SetWindowSubclass(_windowHandle, _windowSubclassProc, new UIntPtr(1), UIntPtr.Zero);
            InitializeTrayIcon();

            // Start with the window hidden off-screen; it will be shown when Space is pressed in Explorer.
            MoveOffscreen();
        }

        internal void LaunchFromCommandLine(string[] arguments)
        {
            string[] paths = arguments.Where(File.Exists).ToArray();
            if (paths.Length > 0)
            {
                ShowFiles(paths);
            }
            else if (arguments.Any(argument => string.Equals(argument, "--show", StringComparison.OrdinalIgnoreCase)))
            {
                ShowHomeWindow();
            }
            else if (arguments.Any(argument => string.Equals(argument, "--tray", StringComparison.OrdinalIgnoreCase)))
            {
                EnterTrayMode();
            }
            else
            {
                EnterTrayMode();
            }
        }

        private void RemoveWindowChrome()
        {
            if (AppWindowTitleBar.IsCustomizationSupported())
            {
                _appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
                _appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;
                _appWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            }

            long style = GetWindowLongPtr(_windowHandle, GwlStyle).ToInt64();
            style &= ~(WsCaption | WsSystemMenu | WsMinimizeBox | WsMaximizeBox);
            SetWindowLongPtr(_windowHandle, GwlStyle, new IntPtr(style));
            SetWindowPos(_windowHandle, IntPtr.Zero, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoZOrder | SwpFrameChanged);
        }

        private static OverlappedPresenter CreateWindowPresenter()
        {
            OverlappedPresenter presenter = OverlappedPresenter.Create();
            presenter.IsResizable = true;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
            return presenter;
        }

        private void PositionWindow()
        {
            DisplayArea display = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Nearest);
            RectInt32 workArea = display.WorkArea;
            int width = Math.Min(1360, (int)(workArea.Width * 0.82));
            int height = Math.Min(900, (int)(workArea.Height * 0.84));
            _appWindow.Resize(new SizeInt32(width, height));
            _appWindow.Move(new PointInt32(
                workArea.X + Math.Max(0, (workArea.Width - width) / 2),
                workArea.Y + Math.Max(0, (workArea.Height - height) / 2)));
        }

        private void EnterTrayMode()
        {
            _isPreviewVisible = false;
            Activate();
            MoveOffscreen();
        }

        private void MoveOffscreen()
        {
            Root.Opacity = 0;
            _appWindow.Hide();
        }

        private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            // WinUI can restore caption styles when the native window is activated; keep
            // the preview surface frameless after that transition as well.
            RemoveWindowChrome();
            if (args.WindowActivationState == WindowActivationState.Deactivated
                && _isPreviewVisible
                && !_showingTrayMenu)
            {
                QueueFocusLossCheck();
            }
        }

        private void PreviewWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            RecordLifecycle("appwindow-closing");
            if (_isExiting)
            {
                return;
            }

            args.Cancel = true;
            HidePreview();
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            RecordLifecycle("window-closed");
            if (_keyboardHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_keyboardHook);
                _keyboardHook = IntPtr.Zero;
            }

            RemoveWindowSubclass(_windowHandle, _windowSubclassProc, new UIntPtr(1));
            if (_trayIconAdded)
            {
                Shell_NotifyIcon(NimDelete, ref _notifyIconData);
                _trayIconAdded = false;
            }
            if (_trayIconHandle != IntPtr.Zero)
            {
                DestroyIcon(_trayIconHandle);
                _trayIconHandle = IntPtr.Zero;
            }

            StopMediaPreview();
            try
            {
                _mediaPlayer?.Dispose();
            }
            catch (COMException)
            {
            }
            _mediaPlayer = null;
            if (_shellApplication is not null && Marshal.IsComObject(_shellApplication))
            {
                Marshal.FinalReleaseComObject(_shellApplication);
                _shellApplication = null;
            }
        }

        private IntPtr KeyboardHookCallback(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code < 0)
            {
                return CallNextHookEx(_keyboardHook, code, wParam, lParam);
            }

            int message = wParam.ToInt32();
            int virtualKey = Marshal.ReadInt32(lParam);
            bool keyDown = message is WmKeyDown or WmSysKeyDown;
            bool keyUp = message is WmKeyUp or WmSysKeyUp;

            // Consume navigation keys here so a physical press is handled once.
            // Previously the hook and Root_KeyDown both advanced the file list.
            if (virtualKey is VkLeft or VkRight)
            {
                if (keyUp)
                {
                    bool wasHandled = _navigationKeysDown.Remove(virtualKey);
                    return wasHandled ? new IntPtr(1) : CallNextHookEx(_keyboardHook, code, wParam, lParam);
                }

                if (keyDown && _isEnabled && _isPreviewVisible
                    && GetForegroundWindow() == _windowHandle && !IsTextPreviewFocused())
                {
                    if (_navigationKeysDown.Add(virtualKey))
                    {
                        int offset = virtualKey == VkLeft ? -1 : 1;
                        _dispatcherQueue.TryEnqueue(() => ShowRelativeFile(offset));
                    }
                    return new IntPtr(1);
                }
            }

            if (virtualKey == VkSpace && keyUp)
            {
                _spaceIsDown = false;
                bool suppress = _suppressSpaceUntilKeyUp;
                _suppressSpaceUntilKeyUp = false;
                return suppress ? new IntPtr(1) : CallNextHookEx(_keyboardHook, code, wParam, lParam);
            }

            if (!_isEnabled)
            {
                return CallNextHookEx(_keyboardHook, code, wParam, lParam);
            }

            if (virtualKey == VkSpace && keyDown)
            {
                if (_spaceIsDown)
                {
                    return _suppressSpaceUntilKeyUp
                        ? new IntPtr(1)
                        : CallNextHookEx(_keyboardHook, code, wParam, lParam);
                }

                _spaceIsDown = true;
                IntPtr foregroundWindow = GetForegroundWindow();

                if (foregroundWindow == _windowHandle && _isPreviewVisible && !IsTextPreviewFocused())
                {
                    _suppressSpaceUntilKeyUp = true;
                    _dispatcherQueue.TryEnqueue(HidePreview);
                    return new IntPtr(1);
                }

                if (IsExplorerWindow(foregroundWindow) && !IsTextInputFocused(foregroundWindow))
                {
                    // Shell COM calls are deferred until this input-synchronous hook returns.
                    _suppressSpaceUntilKeyUp = true;
                    IntPtr explorerWindow = foregroundWindow;
                    _dispatcherQueue.TryEnqueue(() =>
                    {
                        List<string> selectedFiles = GetExplorerSelectedFiles(explorerWindow, out List<string> viewOrder);
                        if (selectedFiles.Count > 0)
                        {
                            ShowFiles(selectedFiles, viewOrder);
                        }
                    });
                    return new IntPtr(1);
                }
            }

            if (keyDown && !_spaceIsDown && _isPreviewVisible && GetForegroundWindow() == _windowHandle
                && !IsTextPreviewFocused())
            {
                if (virtualKey == VkEscape)
                {
                    _dispatcherQueue.TryEnqueue(HidePreview);
                    return CallNextHookEx(_keyboardHook, code, wParam, lParam);
                }

            }

            return CallNextHookEx(_keyboardHook, code, wParam, lParam);
        }

        private IntPtr WindowSubclassCallback(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam, UIntPtr subclassId, UIntPtr referenceData)
        {
            if (_activateMessage != 0 && message == _activateMessage)
            {
                ShowHomeWindow();
                return IntPtr.Zero;
            }

            if (_taskbarCreatedMessage != 0 && message == _taskbarCreatedMessage)
            {
                _trayIconAdded = false;
                AddTrayIcon();
                return IntPtr.Zero;
            }

            // Observe the native activation transition too. WinUI's Activated event can
            // be missed while the topmost preview is being shown/hidden quickly.
            if (message == WmActivate
                && (wParam.ToUInt64() & 0xFFFF) == 0 // WA_INACTIVE
                && _isPreviewVisible
                && !_showingTrayMenu)
            {
                _dispatcherQueue.TryEnqueue(QueueFocusLossCheck);
            }

            if (message == WmAppTray)
            {
                int notification = unchecked((int)(lParam.ToInt64() & 0xFFFF));
                if (notification is WmLButtonUp or WmLButtonDoubleClick or 0x0400 or 0x0401)
                {
                    ToggleEnabled();
                    return IntPtr.Zero;
                }

                if (notification is WmRButtonUp or WmContextMenu)
                {
                    ShowTrayMenu();
                    return IntPtr.Zero;
                }
            }

            if ((message == WmClose || message == WmSysCommand && (wParam.ToUInt64() & 0xFFF0) == ScClose) && !_isExiting)
            {
                HidePreview();
                return IntPtr.Zero;
            }

            return DefSubclassProc(hwnd, message, wParam, lParam);
        }

        private void InitializeTrayIcon()
        {
            _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
            _activateMessage = RegisterWindowMessage(ActivationMessageName);
            string iconPath = Path.Combine(AppContext.BaseDirectory, "PeekPreview.ico");
            if (File.Exists(iconPath))
            {
                _trayIconHandle = LoadImage(IntPtr.Zero, iconPath, ImageIcon, 0, 0, LrLoadFromFile | LrDefaultSize);
            }
            if (_trayIconHandle == IntPtr.Zero)
            {
                _trayIconHandle = LoadImage(GetModuleHandle(null), new IntPtr(32512), ImageIcon, 0, 0, LrDefaultSize);
            }
            _notifyIconData = new NotifyIconData
            {
                cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
                hWnd = _windowHandle,
                uID = 1,
                uFlags = NifMessage | NifIcon | NifTip,
                uCallbackMessage = WmAppTray,
                hIcon = _trayIconHandle,
                szTip = "Peek — Space preview is on",
                szInfo = string.Empty,
                szInfoTitle = string.Empty
            };

            AddTrayIcon();
        }

        private void AddTrayIcon()
        {
            if (_trayIconHandle == IntPtr.Zero) return;
            _notifyIconData.hIcon = _trayIconHandle;
            _trayIconAdded = Shell_NotifyIcon(NimAdd, ref _notifyIconData);
            if (_trayIconAdded)
            {
                _notifyIconData.uVersion = NotifyIconVersion4;
                Shell_NotifyIcon(0x00000004, ref _notifyIconData); // NIM_SETVERSION
            }
        }

        private void ToggleEnabled()
        {
            _isEnabled = !_isEnabled;
            if (!_isEnabled)
            {
                HidePreview();
            }
            UpdateTrayTooltip();
        }

        private void UpdateTrayTooltip()
        {
            if (!_trayIconAdded)
            {
                return;
            }

            _notifyIconData.szTip = "Peek — Space preview is on";
            Shell_NotifyIcon(0x00000001, ref _notifyIconData); // NIM_MODIFY
        }

        private void ShowTrayMenu()
        {
            IntPtr menu = CreatePopupMenu();
            if (menu == IntPtr.Zero)
            {
                return;
            }

            AppendMenu(menu, MfString, new UIntPtr(TrayExitCommand), "Exit Peek");

            GetCursorPos(out NativePoint cursor);
            _showingTrayMenu = true;
            SetForegroundWindow(_windowHandle);
            uint command = TrackPopupMenu(menu, TpmRightButton | TpmReturnCommand, cursor.X, cursor.Y, 0, _windowHandle, IntPtr.Zero);
            DestroyMenu(menu);
            PostMessage(_windowHandle, 0, IntPtr.Zero, IntPtr.Zero);
            _showingTrayMenu = false;

            if (command == TrayExitCommand)
            {
                _isExiting = true;
                Close();
            }
        }

        private void ShowHomeWindow()
        {
            Title = "Peek";
            _previewRequestId++;
            _files.Clear();
            _currentIndex = 0;
            StopMediaPreview();
            if (!_isEnabled)
            {
                EmptyTitle.Text = "Peek is off";
                EmptyDescription.Text = "Click the Peek tray icon to turn Space preview on.";
            }
            else
            {
                EmptyTitle.Text = "Peek";
                EmptyDescription.Text = "Select a file in File Explorer or on the desktop and press Space.";
            }

            EmptyState.Visibility = Visibility.Visible;
            FileBadge.Visibility = Visibility.Collapsed;
            ImagePreview.Visibility = Visibility.Collapsed;
            MediaPreview.Visibility = Visibility.Collapsed;
            WebPreview.Visibility = Visibility.Collapsed;
            TextPreview.Visibility = Visibility.Collapsed;
            ArchivePreview.Visibility = Visibility.Collapsed;
            PositionWindow();
            _isPreviewVisible = true;
            RevealWindow();
        }

        private void Root_KeyDown(object sender, KeyRoutedEventArgs args)
        {
            if (IsTextPreviewFocused()) return;
            switch (args.Key)
            {
                case Windows.System.VirtualKey.Escape:
                case Windows.System.VirtualKey.Space:
                    HidePreview();
                    args.Handled = true;
                    break;
            }
        }

        private void ShowRelativeFile(int offset)
        {
            if (_files.Count < 2)
            {
                return;
            }

            _currentIndex = (_currentIndex + offset + _files.Count) % _files.Count;
            _ = ShowCurrentFileAsync();
        }

        private void RevealWindow()
        {
            Root.Opacity = 1;
            _appWindow.Show();
            ForceSetForegroundWindow(_windowHandle);
            Activate();
        }

        private void ShowFiles(IEnumerable<string> paths, IReadOnlyList<string>? viewOrder = null)
        {
            if (!_isEnabled)
            {
                return;
            }

            // Invalidate delayed deactivation work from the previous window state.
            _focusLossCheckId++;

            _files.Clear();
            _files.AddRange(paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase));
            _currentIndex = 0;

            if (viewOrder is { Count: > 1 } && _files.Count == 1)
            {
                string selected = _files[0];
                int index = -1;
                for (int i = 0; i < viewOrder.Count; i++)
                {
                    if (string.Equals(viewOrder[i], selected, StringComparison.OrdinalIgnoreCase)) { index = i; break; }
                }
                if (index >= 0)
                {
                    _files.Clear();
                    _files.AddRange(viewOrder);
                    _currentIndex = index;
                }
            }

            if (_files.Count == 0)
            {
                return;
            }

            PositionWindow();
            _isPreviewVisible = true;
            _ = ShowCurrentFileAsync();
            if (_files.Count == 1) _ = LoadSiblingFilesAsync(_files[0]);
        }

        private async Task LoadSiblingFilesAsync(string selectedPath)
        {
            List<string> siblings;
            try
            {
                string? directory = Path.GetDirectoryName(selectedPath);
                if (string.IsNullOrEmpty(directory)) return;
                siblings = await Task.Run(() => Directory.EnumerateFiles(directory)
                    .Where(p => (File.GetAttributes(p) & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                    .Where(IsPreviewSupported)
                    .OrderBy(path => Path.GetFileName(path), Comparer<string>.Create(StrCmpLogicalW))
                    .ToList());
            }
            catch { return; }

            _dispatcherQueue.TryEnqueue(() =>
            {
                if (!_isPreviewVisible || _files.Count != 1
                    || !string.Equals(_files[0], selectedPath, StringComparison.OrdinalIgnoreCase)) return;
                _files.Clear();
                _files.AddRange(siblings);
                _currentIndex = _files.FindIndex(path => string.Equals(path, selectedPath, StringComparison.OrdinalIgnoreCase));
                if (_currentIndex < 0)
                {
                    _files.Insert(0, selectedPath);
                    _currentIndex = 0;
                }
            });
        }

        private async Task ShowCurrentFileAsync()
        {
            if (_currentIndex < 0 || _currentIndex >= _files.Count)
            {
                return;
            }

            int requestId = ++_previewRequestId;
            string path = _files[_currentIndex];
            Title = $"Peek — {Path.GetFileName(path)}";
            string extension = Path.GetExtension(path).ToLowerInvariant();
            var previewTimer = Stopwatch.StartNew();
            string previewOutcome = "ok";
            string previewStage = "starting";
            FileName.Text = Path.GetFileName(path);
            FileBadge.Visibility = Visibility.Visible;
            EmptyState.Visibility = Visibility.Collapsed;
            ImagePreview.Visibility = Visibility.Collapsed;
            ImagePreview.Source = null;
            MediaPreview.Visibility = Visibility.Collapsed;
            WebPreview.Visibility = Visibility.Collapsed;
            ClearWebPreviewSource();
            TextPreview.Visibility = Visibility.Collapsed;
            ArchivePreview.Visibility = Visibility.Collapsed;

            StopMediaPreview();
            _activeMediaRequestId = requestId;
            _activeMediaExtension = extension;

            try
            {
                if (IsImage(extension))
                {
                    previewStage = "load-image";
                    var image = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage
                    {
                        DecodePixelWidth = Math.Clamp((int)(Root.ActualWidth * 2), 800, 4096)
                    };
                    image.UriSource = new Uri(path);
                    ImagePreview.Source = image;
                    ImagePreview.Visibility = Visibility.Visible;
                    return;
                }

                if (IsWebDocument(extension))
                {
                    previewStage = "initialize-webview";
                    await WebPreview.EnsureCoreWebView2Async();
                    if (requestId != _previewRequestId)
                    {
                        return;
                    }

                    WebPreview.Source = new Uri(Path.GetFullPath(path));
                    WebPreview.Visibility = Visibility.Visible;
                    return;
                }

                if (extension == ".txt")
                {
                    previewStage = "read-text";
                    TextPreview.Text = await ReadTextPreviewAsync(path);
                    if (requestId != _previewRequestId)
                    {
                        return;
                    }

                    TextPreview.Visibility = Visibility.Visible;
                    return;
                }

                if (IsOfficeDocument(extension))
                {
                    EmptyState.Visibility = Visibility.Visible;
                    EmptyTitle.Text = "Office preview is paused";
                    EmptyDescription.Text = "Word, PowerPoint, and Excel previews are temporarily disabled while stability is improved.";
                    previewOutcome = "office-paused";
                    return;
                }

                if (extension == ".pdf")
                {
                    previewStage = "initialize-pdf-webview";
                    await WebPreview.EnsureCoreWebView2Async();
                    if (requestId != _previewRequestId)
                    {
                        return;
                    }
                    WebPreview.Source = new Uri(Path.GetFullPath(path));
                    WebPreview.Visibility = Visibility.Visible;
                    return;
                }

                if (IsArchive(extension))
                {
                    previewStage = "read-archive";
                    var items = await Task.Run(() => ReadArchiveEntries(path));
                    if (requestId != _previewRequestId)
                    {
                        return;
                    }
                    ArchivePreview.ItemsSource = items;
                    ArchivePreview.Visibility = Visibility.Visible;
                    return;
                }

                if (IsVideo(extension) || IsAudio(extension))
                {
                    previewStage = "create-media-uri";
                    Uri mediaUri = new(Path.GetFullPath(path));
                    if (requestId != _previewRequestId)
                    {
                        return;
                    }

                    _mediaPlayer ??= new MediaPlayer { AutoPlay = true };
                    MediaSource mediaSource = MediaSource.CreateFromUri(mediaUri);
                    previewStage = "assign-media-source";
                    _mediaPlayer.Source = mediaSource;
                    previewStage = "start-media";
                    _mediaPlayer.Play();
                    MediaPreview.Visibility = Visibility.Visible;
                    return;
                }

                ShowUnsupportedFileMessage();
            }
            catch (Exception ex)
            {
                previewOutcome = $"error:{previewStage}:{ex.GetType().Name}:0x{ex.HResult:X8}";
                if (requestId != _previewRequestId)
                {
                    return;
                }

                FileName.Text = Path.GetFileName(path);
                EmptyState.Visibility = Visibility.Visible;
                EmptyTitle.Text = "Couldn't preview this file";
                EmptyDescription.Text = $"Preview failed at {previewStage} ({ex.GetType().Name}, 0x{ex.HResult:X8}).";
            }
            finally
            {
                previewTimer.Stop();
                if (requestId != _previewRequestId && previewOutcome == "ok")
                {
                    previewOutcome = "superseded";
                }
                RecordPreviewTelemetry(path, extension, previewTimer.ElapsedMilliseconds, previewOutcome);

                // Reveal the window once content has been loaded, avoiding the black/empty flash.
                if (requestId == _previewRequestId && _isPreviewVisible)
                {
                    _dispatcherQueue.TryEnqueue(RevealWindow);
                }
            }
        }

        private static void RecordPreviewTelemetry(string path, string extension, long elapsedMilliseconds, string outcome)
        {
            try
            {
                long fileBytes = string.IsNullOrEmpty(path) ? 0 : new FileInfo(path).Length;
                using Process currentProcess = Process.GetCurrentProcess();
                long workingSetBytes = currentProcess.WorkingSet64;
                double cpuSeconds = currentProcess.TotalProcessorTime.TotalSeconds;
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Peek");
                Directory.CreateDirectory(folder);
                string telemetryPath = Path.Combine(folder, "telemetry.csv");
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        lock (TelemetryFileLock)
                        {
                            if (File.Exists(telemetryPath) && new FileInfo(telemetryPath).Length > 1_000_000)
                                File.Delete(telemetryPath);
                            if (!File.Exists(telemetryPath))
                                File.WriteAllText(telemetryPath, "Utc,Type,SizeBytes,ElapsedMs,WorkingSetBytes,CpuSeconds,Outcome" + Environment.NewLine);
                            File.AppendAllText(telemetryPath, $"{DateTimeOffset.UtcNow:O},{extension},{fileBytes},{elapsedMilliseconds},{workingSetBytes},{cpuSeconds:F2},{outcome}{Environment.NewLine}");
                        }
                    }
                    catch { }
                });
            }
            catch { }
        }

        private static readonly object TelemetryFileLock = new();

        private async Task WarmWebViewAsync()
        {
            try
            {
                await WebPreview.EnsureCoreWebView2Async();
            }
            catch
            {
                // WebView2 may not be available on machines without its runtime installed.
            }
        }

        private void QueueFocusLossCheck()
        {
            int checkId = ++_focusLossCheckId;
            _ = CheckFocusLossAsync(checkId);
        }

        private async Task CheckFocusLossAsync(int checkId)
        {
            await Task.Delay(260);
            if (checkId == _focusLossCheckId
                && _isPreviewVisible
                && !_showingTrayMenu
                && GetForegroundWindow() != _windowHandle)
            {
                HidePreview();
            }
        }

        private static async Task<string> ReadTextPreviewAsync(string path)
        {
            const int maxCharacters = 1_000_000;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, useAsync: true);
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[maxCharacters];
            int read = await reader.ReadBlockAsync(buffer.AsMemory(0, buffer.Length));
            string text = new string(buffer, 0, read);
            return stream.Position < stream.Length ? text + "\n\n… Preview limited to the first 1,000,000 characters." : text;
        }

        private sealed record ArchiveEntryItem(string Name, string Size);

        private static List<ArchiveEntryItem> ReadArchiveEntries(string path)
        {
            using var archive = ArchiveFactory.OpenArchive(path);
            return archive.Entries.Take(5000).Select(entry => new ArchiveEntryItem(
                entry.Key ?? "(unnamed)",
                entry.IsDirectory ? "Folder" : FormatFileSize(entry.Size))).ToList();
        }

        private static string FormatFileSize(long size) => size < 1024 ? $"{size} B" : $"{size / 1024d:0.#} KB";

        private void ShowUnsupportedFileMessage()
        {
            EmptyState.Visibility = Visibility.Visible;
            EmptyTitle.Text = "Preview not available";
            EmptyDescription.Text = "This file type is not supported yet.";
        }

        private void HidePreview()
        {
            RecordLifecycle("hide-preview");
            _focusLossCheckId++;
            _previewRequestId++;
            StopMediaPreview();
            ClearWebPreviewSource();
            _isPreviewVisible = false;
            MoveOffscreen();
        }

        private void StopMediaPreview()
        {
            try
            {
                _mediaPlayer?.Pause();
            }
            catch (COMException)
            {
                RecordPreviewTelemetry(string.Empty, _activeMediaExtension ?? "media", 0, "media-pause-aborted");
            }

            try
            {
                if (_mediaPlayer is not null)
                {
                    _mediaPlayer.Source = null;
                }
            }
            catch (COMException)
            {
                RecordPreviewTelemetry(string.Empty, _activeMediaExtension ?? "media", 0, "media-detach-aborted");
            }
        }

        private void ClearWebPreviewSource()
        {
            try
            {
                WebPreview.Source = null;
            }
            catch (COMException)
            {
                RecordPreviewTelemetry(string.Empty, _activeMediaExtension ?? "web", 0, "web-detach-aborted");
            }
        }

        private static void RecordLifecycle(string state)
        {
            try
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Peek");
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, "lifecycle.log"), $"[{DateTimeOffset.Now:O}] {state}{Environment.NewLine}");
            }
            catch
            {
            }
        }

        private static bool IsPreviewSupported(string path)
        {
            string extension = Path.GetExtension(path).ToLowerInvariant();
            return IsImage(extension) || IsArchive(extension) || IsWebDocument(extension)
                || extension is ".txt" or ".pdf" || IsVideo(extension) || IsAudio(extension);
        }

        private bool IsTextPreviewFocused() => TextPreview.Visibility == Visibility.Visible
            && TextPreview.FocusState != Microsoft.UI.Xaml.FocusState.Unfocused;


        private static bool IsImage(string extension) => extension is
            ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff" or ".ico" or ".wdp" or ".webp";

        private static bool IsArchive(string extension) => extension is ".zip" or ".rar" or ".7z" or ".cbr" or ".cbz";

        private static bool IsWebDocument(string extension) => extension is ".html" or ".htm" or ".xhtml" or ".mht" or ".mhtml";

        private static bool IsOfficeDocument(string extension) => extension is
            ".doc" or ".docx" or ".docm" or ".rtf"
            or ".ppt" or ".pptx" or ".pptm" or ".pps" or ".ppsx" or ".ppsm" or ".pot" or ".potx" or ".potm"
            or ".xls" or ".xlsx" or ".xlsm" or ".xlsb" or ".csv";

        private static bool IsVideo(string extension) => extension is
            ".mp4" or ".m4v" or ".mov" or ".wmv" or ".avi" or ".mkv" or ".webm" or ".mpeg" or ".mpg" or ".3gp";

        private static bool IsAudio(string extension) => extension is
            ".mp3" or ".m4a" or ".aac" or ".wav" or ".wma" or ".flac" or ".ogg" or ".opus";

        private bool IsExplorerWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            GetWindowThreadProcessId(hwnd, out uint processId);
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById((int)processId);
                return string.Equals(process.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private bool IsTextInputFocused(IntPtr hwnd)
        {
            uint threadId = GetWindowThreadProcessId(hwnd, out _);
            var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
            if (!GetGUIThreadInfo(threadId, ref info) || info.Focus == IntPtr.Zero)
            {
                return false;
            }

            var className = new StringBuilder(128);
            GetClassName(info.Focus, className, className.Capacity);
            string name = className.ToString();
            return name.Contains("Edit", StringComparison.OrdinalIgnoreCase)
                || name.Contains("RichEdit", StringComparison.OrdinalIgnoreCase)
                || name.Contains("SearchBox", StringComparison.OrdinalIgnoreCase);
        }

        private static List<string> GetViewOrderPaths(dynamic shellWindow)
        {
            var result = new List<string>();
            try
            {
                dynamic items = shellWindow.Document.Folder.Items();
                int count = Convert.ToInt32(items.Count);
                if (count > 3000) return result;   // too slow via dynamic COM; caller falls back
                for (int i = 0; i < count; i++)
                {
                    dynamic item = items.Item(i);
                    string path = Convert.ToString(item.Path) ?? string.Empty;
                    if (File.Exists(path)) result.Add(path);   // File.Exists skips folders; do NOT use item.IsFolder (it is true for .zip)
                }
            }
            catch { }
            return result;
        }

        private List<string> GetExplorerSelectedFiles(IntPtr foregroundWindow, out List<string> viewOrder)
        {
            var paths = new List<string>();
            viewOrder = new List<string>();
            try
            {
                try
                {
                    string dbgFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Peek");
                    Directory.CreateDirectory(dbgFolder);
                    File.AppendAllText(Path.Combine(dbgFolder, "debug.log"),
                        $"[{DateTimeOffset.Now:O}] GetExplorerSelectedFiles called. foreground=0x{foregroundWindow.ToInt64():X}\n");
                }
                catch { }
                if (_shellApplication is null)
                {
                    Type? shellType = Type.GetTypeFromProgID("Shell.Application");
                    if (shellType is null)
                    {
                        return paths;
                    }
                    _shellApplication = Activator.CreateInstance(shellType);
                }

                dynamic shell = _shellApplication!;
                dynamic windows = shell.Windows();
                int windowCount = Convert.ToInt32(windows.Count);
                bool desktopForeground = IsDesktopShellWindow(foregroundWindow);
                object? desktopWindow = desktopForeground ? TryGetDesktopShellWindow(windows) : null;
                try
                {
                    string dbgFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Peek");
                    File.AppendAllText(Path.Combine(dbgFolder, "debug.log"),
                        $"[{DateTimeOffset.Now:O}] desktopForeground={desktopForeground} desktopWindowFound={desktopWindow is not null}{Environment.NewLine}");
                }
                catch { }
                for (int i = desktopWindow is null ? 0 : -1; i < windowCount; i++)
                {
                    bool isDesktopEntry = i < 0;
                    dynamic shellWindow = isDesktopEntry ? desktopWindow! : windows.Item(i);
                    bool isTarget;
                    if (isDesktopEntry)
                    {
                        isTarget = true;
                    }
                    else
                    {
                        IntPtr shellWindowHandle = new IntPtr(Convert.ToInt64(shellWindow.HWND));
                        isTarget = shellWindowHandle == foregroundWindow
                            || shellWindowHandle == GetAncestor(foregroundWindow, 2);
                    }
                    if (!isTarget)
                    {
                        continue;
                    }

                    dynamic document = shellWindow.Document;
                    dynamic selection = document.SelectedItems();
                    int selectedCount = Convert.ToInt32(selection.Count);
                    var selectedPaths = new List<string>(selectedCount);
                    for (int itemIndex = 0; itemIndex < selectedCount; itemIndex++)
                    {
                        dynamic item = selection.Item(itemIndex);
                        string itemPath = Convert.ToString(item.Path) ?? string.Empty;
                        if (File.Exists(itemPath)
                            && !selectedPaths.Contains(itemPath, StringComparer.OrdinalIgnoreCase))
                        {
                            selectedPaths.Add(itemPath);
                        }
                    }

                    // SelectedItems() is a collection, not an indication of which item
                    // has the Explorer caret. With multi-select, its first element can be
                    // a different file from the one the user is currently on. Preview the
                    // focused (caret) file first, then preserve the shell selection order.
                    string focusedPath = string.Empty;
                    try
                    {
                        dynamic focusedItem = document.FocusedItem;
                        focusedPath = Convert.ToString(focusedItem.Path) ?? string.Empty;
                        int focusedIndex = selectedPaths.FindIndex(path =>
                            string.Equals(path, focusedPath, StringComparison.OrdinalIgnoreCase));
                        if (focusedIndex > 0)
                        {
                            selectedPaths.RemoveAt(focusedIndex);
                            selectedPaths.Insert(0, focusedPath);
                        }
                    }
                    catch
                    {
                        // Some shell views don't expose a focused item; keep selection order.
                    }

                    // Emit debug info about the selection and focus to help diagnose ordering issues
                    try
                    {
                        string dbgFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Peek");
                        Directory.CreateDirectory(dbgFolder);
                        var sb = new StringBuilder();
                        sb.AppendLine($"[{DateTimeOffset.Now:O}] Explorer window handle=0x{foregroundWindow.ToInt64():X}");
                        sb.AppendLine($"  desktopForeground={IsDesktopShellWindow(foregroundWindow)}");
                        sb.AppendLine($"  focusedPath={focusedPath}");
                        sb.AppendLine("  selectedPaths:");
                        foreach (var p in selectedPaths) sb.AppendLine($"    {p}");
                        File.AppendAllText(Path.Combine(dbgFolder, "debug.log"), sb.ToString());
                    }
                    catch { }

                    if (selectedPaths.Count > 0)
                    {
                        if (selectedPaths.Count == 1)
                        {
                            viewOrder = GetViewOrderPaths(shellWindow);
                            try
                            {
                                string dbgFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Peek");
                                File.AppendAllText(Path.Combine(dbgFolder, "debug.log"),
                                    $"[{DateTimeOffset.Now:O}] viewOrder count={viewOrder.Count} first5={string.Join(" | ", viewOrder.Take(5).Select(Path.GetFileName))}{Environment.NewLine}");
                            }
                            catch { }
                        }
                        paths.AddRange(selectedPaths);
                    }
                    break;
                }
            }
            catch
            {
                // Explorer selection may be unavailable for virtual items or protected locations.
            }

            return paths;
        }

        private static bool IsDesktopShellWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            IntPtr root = GetAncestor(hwnd, 2);
            if (hwnd == GetShellWindow() || root == GetShellWindow()) return true;
            var className = new StringBuilder(128);
            GetClassName(root == IntPtr.Zero ? hwnd : root, className, className.Capacity);
            return className.ToString() is "Progman" or "WorkerW";
        }

        private static object? TryGetDesktopShellWindow(dynamic windows)
        {
            try
            {
                object location = 0;             // CSIDL_DESKTOP
                object unused = new object();
                int hwnd;
                // SWC_DESKTOP = 8, SWFO_NEEDDISPATCH = 1
                return windows.FindWindowSW(ref location, ref unused, 8, out hwnd, 1);
            }
            catch
            {
                return null;
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NotifyIconData
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public uint uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GuiThreadInfo
        {
            public int Size;
            public uint Flags;
            public IntPtr Active;
            public IntPtr Focus;
            public IntPtr Capture;
            public IntPtr MenuOwner;
            public IntPtr MoveSize;
            public IntPtr Caret;
            public NativeRect CaretRect;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);
        private delegate IntPtr WindowSubclassProc(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam, UIntPtr subclassId, UIntPtr referenceData);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc callback, IntPtr module, uint threadId);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);


        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);



        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern int StrCmpLogicalW(string x, string y);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "LoadImageW")]
        private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "LoadImageW")]
        private static extern IntPtr LoadImage(IntPtr instance, IntPtr name, uint type, int width, int height, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string message);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr icon);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? moduleName);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool SetWindowSubclass(IntPtr hwnd, WindowSubclassProc callback, UIntPtr subclassId, UIntPtr referenceData);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool RemoveWindowSubclass(IntPtr hwnd, WindowSubclassProc callback, UIntPtr subclassId);

        [DllImport("comctl32.dll")]
        private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr itemId, string? itemText);

        [DllImport("user32.dll")]
        private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rectangle);

        [DllImport("user32.dll")]
        private static extern bool DestroyMenu(IntPtr menu);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out NativePoint point);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        private static void ForceSetForegroundWindow(IntPtr hwnd)
        {
            try
            {
                IntPtr foreground = GetForegroundWindow();
                if (foreground == hwnd) return;
                uint fgThread = GetWindowThreadProcessId(foreground, out _);
                uint thisThread = GetCurrentThreadId();
                // Attach input to the foreground thread so we can set foreground.
                if (AttachThreadInput(fgThread, thisThread, true))
                {
                    BringWindowToTop(hwnd);
                    SetForegroundWindow(hwnd);
                    AttachThreadInput(fgThread, thisThread, false);
                }
                else
                {
                    // Fallback: try SetForegroundWindow directly.
                    SetForegroundWindow(hwnd);
                }
            }
            catch { SetForegroundWindow(hwnd); }
        }

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    }
}
