using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ExplorerHistoryTracker.ViewModels;
using ExplorerHistoryTracker.Models;
using ExplorerHistoryTracker.Services;

namespace ExplorerHistoryTracker
{
    public partial class FloatBallWindow : Window
    {
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int GWL_WNDPROC = -4;
        private const uint WM_SYSCOMMAND = 0x0112;
        private const int SC_MINIMIZE = 0xF020;
        private const uint WM_WINDOWPOSCHANGING = 0x0046;

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPOS
        {
            public IntPtr hwnd;
            public IntPtr hwndInsertAfter;
            public int x;
            public int y;
            public int cx;
            public int cy;
            public uint flags;
        }

        private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
        {
            return IntPtr.Size == 8
                ? GetWindowLongPtr64(hWnd, nIndex)
                : new IntPtr(GetWindowLong32(hWnd, nIndex));
        }

        private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        {
            return IntPtr.Size == 8
                ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
                : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));
        }

        private bool _isDragging;
        private Point _startMouseScreenPos;
        private PixelPoint _startWindowPos;
        private bool _dragTriggered;
        private long _pressTime;
        private DispatcherTimer? _longPressTimer;
        private IPointer? _capturedPointer;
        private IntPtr _activationTargetHwnd;
        private int _lastDragPointerX;
        private int _dragHorizontalAccumulator;

        private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        private WndProc? _newWndProc;
        private IntPtr _originalWndProc = IntPtr.Zero;
        private bool _isSubclassed;

        // Codex Pet rendering and animation variables
        private Bitmap? _spritesheetBitmap;
        private CroppedBitmap?[,]? _frameCache;
        private DispatcherTimer? _animationTimer;
        private int _currentFrame;
        private int _frameCount;
        private int _availableColumns;
        private int _availableRows;
        private int _activeRow;
        private int _lastActionRow = -1;
        private bool _isPlayingAction;
        private DateTime _nextActionAt;
        private int _animationIntervalMs = 150;
        private bool _isPointerTracking;
        private DateTime _lookTrackingStartsAt;
        private static readonly int[] AmbientActionRows = { 3, 4, 6, 7, 8 };
        private DispatcherTimer? _contextMenuFocusTimer;
        private IntPtr _contextMenuForegroundHwnd;
        private DateTime _contextMenuFocusArmedAt;

        public FloatBallWindow()
        {
            InitializeComponent();

            PointerPressed += OnPointerPressed;
            PointerMoved += OnPointerMoved;
            PointerReleased += OnPointerReleased;
            PointerEntered += OnPointerEntered;
            PointerExited += OnPointerExited;

            LoadPetStyle();

            var vm = App.SharedViewModel;
            vm.PropertyChanged += Vm_PropertyChanged;
        }

        public new void Hide()
        {
            ResetDragState();
            ClosePetContextMenu();
            _isPointerTracking = false;
            _animationTimer?.Stop();
            base.Hide();
        }

        private void ResetDragState()
        {
            _isDragging = false;
            _dragTriggered = false;
            _dragHorizontalAccumulator = 0;
            _activationTargetHwnd = IntPtr.Zero;
            _longPressTimer?.Stop();
            if (_capturedPointer != null)
            {
                try
                {
                    _capturedPointer.Capture(null);
                }
                catch { }
                _capturedPointer = null;
            }
        }

        private void Vm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.SelectedPetId))
            {
                Dispatcher.UIThread.Post(LoadPetStyle);
            }
        }

        private void DisposePetFrames()
        {
            _animationTimer?.Stop();
            PetImage.Source = null;

            if (_frameCache != null)
            {
                foreach (var frame in _frameCache)
                {
                    frame?.Dispose();
                }
                _frameCache = null;
            }

            _spritesheetBitmap?.Dispose();
            _spritesheetBitmap = null;
        }

        private static CroppedBitmap?[,] BuildFrameCache(
            Bitmap source,
            int frameWidth,
            int frameHeight,
            int rows,
            int columns)
        {
            var frames = new CroppedBitmap?[rows, columns];
            try
            {
                for (int row = 0; row < rows; row++)
                {
                    for (int column = 0; column < columns; column++)
                    {
                        var rect = new PixelRect(
                            column * frameWidth,
                            row * frameHeight,
                            frameWidth,
                            frameHeight);
                        frames[row, column] = new CroppedBitmap(source, rect);
                    }
                }
                return frames;
            }
            catch
            {
                foreach (var frame in frames)
                {
                    frame?.Dispose();
                }
                source.Dispose();
                throw;
            }
        }

        private void LoadPetStyle()
        {
            DisposePetFrames();

            var vm = App.SharedViewModel;
            var config = vm.ConfigManager.Config;

            if (string.IsNullOrEmpty(config.SelectedPetId) || config.SelectedPetId == "default")
            {
                MainBorder.IsVisible = true;
                PetImage.IsVisible = false;
            }
            else
            {
                string appDataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Tuna"
                );
                string petsDir = Path.Combine(appDataDir, "pets");
                string petDir = Path.Combine(petsDir, config.SelectedPetId + ".codex-pet");
                if (!Directory.Exists(petDir))
                {
                    // Fallback search in pets directory
                    petDir = "";
                    if (Directory.Exists(petsDir))
                    {
                        foreach (var d in Directory.GetDirectories(petsDir))
                        {
                            if (Path.GetFileName(d).StartsWith(config.SelectedPetId, StringComparison.OrdinalIgnoreCase))
                            {
                                petDir = d;
                                break;
                            }
                        }
                    }
                }

                bool loaded = false;
                if (!string.IsNullOrEmpty(petDir) && Directory.Exists(petDir))
                {
                    string petJsonPath = Path.Combine(petDir, "pet.json");
                    if (File.Exists(petJsonPath))
                    {
                        try
                        {
                            string json = File.ReadAllText(petJsonPath);
                            var pet = System.Text.Json.JsonSerializer.Deserialize(json, JsonContext.Default.CodexPet);
                            if (pet != null)
                            {
                                string fullSpritesheetPath = Path.Combine(petDir, pet.SpritesheetPath);
                                if (File.Exists(fullSpritesheetPath))
                                {
                                    var bitmap = new Bitmap(fullSpritesheetPath);
                                    int frameWidth = pet.FrameWidth > 0 ? pet.FrameWidth : 192;
                                    int frameHeight = pet.FrameHeight > 0 ? pet.FrameHeight : 208;
                                    int availableColumns = bitmap.PixelSize.Width / frameWidth;
                                    int availableRows = bitmap.PixelSize.Height / frameHeight;
                                    if (availableColumns > 0 && availableRows > 0)
                                    {
                                        // Pre-create lightweight crop views once. Timer ticks only switch
                                        // references and never allocate or dispose rendering objects.
                                        var frameCache = BuildFrameCache(
                                            bitmap,
                                            frameWidth,
                                            frameHeight,
                                            availableRows,
                                            availableColumns);
                                        _spritesheetBitmap = bitmap;
                                        _frameCache = frameCache;
                                        _availableColumns = availableColumns;
                                        _availableRows = availableRows;
                                        _animationIntervalMs = Math.Clamp(
                                            pet.AnimationIntervalMs,
                                            16,
                                            10000);
                                        _currentFrame = 0;
                                        _activeRow = 0;
                                        _frameCount = Math.Min(
                                            availableColumns,
                                            GetFrameCountForRow(0));
                                        _lastActionRow = -1;
                                        _isPlayingAction = false;
                                        _nextActionAt = GetNextActionTime();

                                        MainBorder.IsVisible = false;
                                        PetImage.IsVisible = true;

                                        if (_animationTimer == null)
                                        {
                                            _animationTimer = new DispatcherTimer();
                                            _animationTimer.Tick += (s, ev) => UpdatePetFrame();
                                        }
                                        _animationTimer.Interval = TimeSpan.FromMilliseconds(
                                            _animationIntervalMs);

                                        UpdatePetFrame(); // Show first frame immediately
                                        _animationTimer.Start();
                                        loaded = true;
                                    }
                                    else
                                    {
                                        bitmap.Dispose();
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }

                if (!loaded)
                {
                    // Fallback to default bubble style
                    MainBorder.IsVisible = true;
                    PetImage.IsVisible = false;
                }
            }
        }

        private void UpdatePetFrame()
        {
            if (_frameCache == null || _frameCount <= 0) return;
            if (_isPointerTracking && !_isDragging) return;

            var frame = _frameCache[_activeRow, _currentFrame];
            if (frame != null)
            {
                PetImage.Source = frame;
            }

            _currentFrame++;
            if (_currentFrame < _frameCount) return;

            _currentFrame = 0;
            if (_isPlayingAction)
            {
                // A non-idle row is played once, then the pet resumes its normal idle loop.
                ResumeIdle();
            }
            else if (_availableRows > 1 && DateTime.UtcNow >= _nextActionAt)
            {
                StartRandomAction();
            }
        }

        private DateTime GetNextActionTime() =>
            DateTime.UtcNow.AddMilliseconds(Random.Shared.Next(6000, 15001));

        private void StartRandomAction()
        {
            int eligibleCount = 0;
            foreach (int row in AmbientActionRows)
            {
                if (row < _availableRows) eligibleCount++;
            }

            if (eligibleCount == 0) return;
            int actionRow;
            do
            {
                actionRow = AmbientActionRows[Random.Shared.Next(eligibleCount)];
            }
            while (eligibleCount > 1 && actionRow == _lastActionRow);

            StartAction(actionRow, true);
        }

        private static int GetFrameCountForRow(int row) => row switch
        {
            0 => 6, // idle
            1 => 8, // running right
            2 => 8, // running left
            3 => 4, // waving
            4 => 5, // jumping
            5 => 8, // failed
            6 => 6, // waiting
            7 => 6, // working
            8 => 6, // review
            9 => 8, // look directions 000..157.5
            10 => 8, // look directions 180..337.5
            _ => 1
        };

        private void StartAction(int row, bool restart = false)
        {
            if (_frameCache == null || row < 0 || row >= _availableRows) return;
            if (!restart && _activeRow == row && _isPlayingAction) return;

            _activeRow = row;
            _frameCount = Math.Min(_availableColumns, GetFrameCountForRow(row));
            _currentFrame = 0;
            _lastActionRow = row;
            _isPlayingAction = row != 0;
        }

        private void ResumeIdle()
        {
            _isPointerTracking = false;
            _activeRow = 0;
            _frameCount = Math.Min(_availableColumns, GetFrameCountForRow(0));
            _currentFrame = 0;
            _isPlayingAction = false;
            _nextActionAt = GetNextActionTime();
        }

        private void ShowLookDirection(Point position)
        {
            if (_frameCache == null || _availableRows < 11 || _availableColumns < 8) return;

            double dx = position.X - Bounds.Width / 2;
            double dy = position.Y - Bounds.Height / 2;
            if (dx * dx + dy * dy < 36) return;

            double degrees = Math.Atan2(dx, -dy) * 180.0 / Math.PI;
            if (degrees < 0) degrees += 360.0;
            int direction = ((int)Math.Round(degrees / 22.5)) % 16;
            int row = direction < 8 ? 9 : 10;
            int column = direction < 8 ? direction : direction - 8;

            _isPointerTracking = true;
            PetImage.Source = _frameCache[row, column];
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            _animationTimer?.Start();

            // Apply WS_EX_TOOLWINDOW style at the OS level to exclude the window from the Alt+Tab menu
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    var platformHandle = this.TryGetPlatformHandle();
                    if (platformHandle != null && platformHandle.Handle != IntPtr.Zero)
                    {
                        IntPtr hwnd = platformHandle.Handle;
                        IntPtr exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
                        IntPtr newExStyle = new IntPtr(exStyle.ToInt64() | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
                        SetWindowLongPtr(hwnd, GWL_EXSTYLE, newExStyle);

                        InstallMinimizeBlocker();
                    }
                }
                catch
                {
                    // Best effort
                }
            }

            // Position restoration or default positioning
            var vm = App.SharedViewModel;
            var config = vm.ConfigManager.Config;
            if (config.FloatBallX >= 0 && config.FloatBallY >= 0)
            {
                Position = new PixelPoint(config.FloatBallX, config.FloatBallY);
            }
            else
            {
                // Position bottom right on the primary screen by default
                var screen = Screens.Primary;
                if (screen != null)
                {
                    var workArea = screen.WorkingArea;
                    int defaultX = workArea.X + workArea.Width - 150;
                    int defaultY = workArea.Y + workArea.Height - 200;
                    Position = new PixelPoint(defaultX, defaultY);
                }
            }
        }

        private void InstallMinimizeBlocker()
        {
            if (_isSubclassed) return;

            try
            {
                var platformHandle = this.TryGetPlatformHandle();
                if (platformHandle == null) return;

                IntPtr hwnd = platformHandle.Handle;
                _newWndProc = new WndProc(CustomWndProc);
                _originalWndProc = SetWindowLongPtr(hwnd, GWL_WNDPROC,
                    Marshal.GetFunctionPointerForDelegate(_newWndProc));
                _isSubclassed = true;
            }
            catch
            {
                // Ignore subclassing failures
            }
        }

        private IntPtr CustomWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (msg == WM_SYSCOMMAND && ((int)wParam & 0xFFF0) == SC_MINIMIZE)
                {
                    // Block minimization entirely
                    return IntPtr.Zero;
                }

                if (msg == WM_WINDOWPOSCHANGING && lParam != IntPtr.Zero)
                {
                    var windowPos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                    // Force the window to remain Topmost at all times
                    windowPos.hwndInsertAfter = new IntPtr(-1); // HWND_TOPMOST
                    Marshal.StructureToPtr(windowPos, lParam, false);
                }
            }
            catch
            {
                // Never let an exception escape native callback
            }

            if (_originalWndProc != IntPtr.Zero)
                return CallWindowProc(_originalWndProc, hWnd, msg, wParam, lParam);

            return IntPtr.Zero;
        }

        protected override void OnClosed(EventArgs e)
        {
            ResetDragState();

            var vm = App.SharedViewModel;
            vm.PropertyChanged -= Vm_PropertyChanged;

            DisposePetFrames();

            if (_isSubclassed)
            {
                try
                {
                    var platformHandle = this.TryGetPlatformHandle();
                    if (platformHandle != null && _originalWndProc != IntPtr.Zero)
                    {
                        SetWindowLongPtr(platformHandle.Handle, GWL_WNDPROC, _originalWndProc);
                    }
                }
                catch { }
                _isSubclassed = false;
            }

            base.OnClosed(e);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == WindowStateProperty &&
                change.NewValue is WindowState.Minimized)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    WindowState = WindowState.Normal;
                });
            }
        }

        private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                _isPointerTracking = false;
                // WS_EX_NOACTIVATE keeps the original file dialog in front, so capture it
                // immediately instead of waiting for the UI Dispatcher after release.
                _activationTargetHwnd = GetForegroundWindow();
                _pressTime = Environment.TickCount64;
                if (GetCursorPos(out var pt))
                {
                    _startMouseScreenPos = new Point(pt.X, pt.Y);
                    _lastDragPointerX = pt.X;
                }
                _dragHorizontalAccumulator = 0;
                _startWindowPos = Position;
                _dragTriggered = false;
                _isDragging = true;
                _capturedPointer = e.Pointer;

                // Start 250ms long press timer to detect drag intent
                if (_longPressTimer == null)
                {
                    _longPressTimer = new DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(250)
                    };
                    _longPressTimer.Tick += LongPressTimer_Tick;
                }
                _longPressTimer.Start();
            }
        }

        private void LongPressTimer_Tick(object? sender, EventArgs e)
        {
            _longPressTimer?.Stop();
            if (_isDragging)
            {
                _dragTriggered = true;
                _activationTargetHwnd = IntPtr.Zero;
                _capturedPointer?.Capture(this); // Capture pointer only when drag mode is officially active
            }
        }

        private void OnPointerMoved(object? sender, PointerEventArgs e)
        {
            if (_isDragging)
            {
                if (GetCursorPos(out var pt))
                {
                    _dragHorizontalAccumulator += pt.X - _lastDragPointerX;
                    _lastDragPointerX = pt.X;
                    var currentMouseScreenPos = new Point(pt.X, pt.Y);
                    var delta = currentMouseScreenPos - _startMouseScreenPos;

                    // If moved beyond 8 pixels, force enter drag mode
                    if (!_dragTriggered && Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y) > 8)
                    {
                        _longPressTimer?.Stop();
                        _dragTriggered = true;
                        _activationTargetHwnd = IntPtr.Zero;
                        _capturedPointer?.Capture(this); // Capture pointer only when drag mode is officially active
                    }

                    if (_dragTriggered)
                    {
                        if (Math.Abs(_dragHorizontalAccumulator) >= 3)
                        {
                            int movementRow = _dragHorizontalAccumulator < 0 ? 2 : 1;
                            StartAction(movementRow);
                            _dragHorizontalAccumulator = 0;
                        }
                        Position = new PixelPoint(_startWindowPos.X + (int)delta.X, _startWindowPos.Y + (int)delta.Y);
                    }
                }
            }
            else if (!_isDragging)
            {
                if (DateTime.UtcNow >= _lookTrackingStartsAt)
                {
                    ShowLookDirection(e.GetPosition(this));
                }
            }
        }

        private void OnPointerEntered(object? sender, PointerEventArgs e)
        {
            if (!_isDragging)
            {
                _lookTrackingStartsAt = DateTime.UtcNow.AddMilliseconds(550);
                StartAction(3, true);
            }
        }

        private void OnPointerExited(object? sender, PointerEventArgs e)
        {
            if (!_isDragging)
            {
                ResumeIdle();
            }
        }

        private void HideMenuItem_Click(object? sender, RoutedEventArgs e)
        {
            ClosePetContextMenu();
            var vm = App.SharedViewModel;
            vm.IsFloatBallEnabled = false;
            App.UpdateFloatBall();
        }

        private void SettingsMenuItem_Click(object? sender, RoutedEventArgs e)
        {
            ClosePetContextMenu();
            App.SharedViewModel.CurrentTab = "Settings";
            App.ShowMainWindow(
                GetForegroundWindow(),
                FileDialogActivationSource.FloatBall);
        }

        private void ExitMenuItem_Click(object? sender, RoutedEventArgs e)
        {
            ClosePetContextMenu();
            App.Shutdown();
        }

        private void PetContextMenu_Opening(object? sender, CancelEventArgs e)
        {
            _contextMenuForegroundHwnd = GetForegroundWindow();
            _contextMenuFocusArmedAt = DateTime.UtcNow.AddMilliseconds(250);

            if (_contextMenuFocusTimer == null)
            {
                _contextMenuFocusTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(100)
                };
                _contextMenuFocusTimer.Tick += ContextMenuFocusTimer_Tick;
            }

            _contextMenuFocusTimer.Start();
        }

        private void PetContextMenu_Closing(object? sender, CancelEventArgs e)
        {
            _contextMenuFocusTimer?.Stop();
            _contextMenuForegroundHwnd = IntPtr.Zero;
        }

        private void ContextMenuFocusTimer_Tick(object? sender, EventArgs e)
        {
            IntPtr foreground = GetForegroundWindow();
            if (DateTime.UtcNow < _contextMenuFocusArmedAt)
            {
                // Let the popup finish activating before choosing its foreground HWND.
                if (foreground != IntPtr.Zero)
                {
                    _contextMenuForegroundHwnd = foreground;
                }
                return;
            }

            if (_contextMenuForegroundHwnd != IntPtr.Zero &&
                foreground != _contextMenuForegroundHwnd)
            {
                ClosePetContextMenu();
            }
        }

        private void ClosePetContextMenu()
        {
            _contextMenuFocusTimer?.Stop();
            if (PetContextMenu.IsOpen)
            {
                PetContextMenu.Close();
            }
        }

        private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (_isDragging)
            {
                _isDragging = false;
                _dragHorizontalAccumulator = 0;
                _longPressTimer?.Stop();
                if (_capturedPointer != null)
                {
                    try { _capturedPointer.Capture(null); } catch { }
                    _capturedPointer = null;
                }

                var elapsed = Environment.TickCount64 - _pressTime;

                if (GetCursorPos(out var pt))
                {
                    var currentMouseScreenPos = new Point(pt.X, pt.Y);
                    var delta = currentMouseScreenPos - _startMouseScreenPos;
                    var dist = Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y);

                    // If released within 300ms and didn't move much, treat as a single click
                    if (!_dragTriggered && dist < 8 && elapsed < 300)
                    {
                        IntPtr activationTarget = _activationTargetHwnd;
                        _activationTargetHwnd = IntPtr.Zero;
                        App.ToggleMainWindow(
                            activationTarget,
                            FileDialogActivationSource.FloatBall);
                    }
                    else if (_dragTriggered)
                    {
                        // Save coordinates
                        var vm = App.SharedViewModel;
                        vm.ConfigManager.Config.FloatBallX = Position.X;
                        vm.ConfigManager.Config.FloatBallY = Position.Y;
                        vm.ConfigManager.Save();
                        StartAction(4, true);
                    }
                }
            }
        }
    }
}
