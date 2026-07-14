using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ExplorerHistoryTracker.Models;
using ExplorerHistoryTracker.Services;
using ExplorerHistoryTracker.ViewModels;

namespace ExplorerHistoryTracker
{
    public partial class MainWindow : Window
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        // ── Win32 subclassing to block window minimization at the OS level ──
        private const int GWL_WNDPROC = -4;
        private const int WM_SYSCOMMAND = 0x0112;
        private const int WM_SHOWWINDOW = 0x0018;
        private const int SC_MINIMIZE = 0xF020;
        private const uint WM_APP = 0x8000;
        private const uint WM_REPOSITION = WM_APP + 1;

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

        private static IntPtr SetWindowLongPtrCompat(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        {
            return IntPtr.Size == 8
                ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
                : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));
        }

        private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        private WndProc? _newWndProc;            // keep alive to prevent GC
        private IntPtr _originalWndProc = IntPtr.Zero;
        private bool _isSubclassed;              // idempotency guard: subclass only once

        // ── One-time initialization guard ──
        // Avalonia calls OnOpened every time Show() is invoked, so we must ensure
        // that event subscriptions and Win32 subclassing happen exactly once.
        private bool _initialized;

        // ── Re-entry guard for Deactivated → Hide() ──
        // Hide() can trigger another Deactivated; this flag prevents recursive hiding.
        private bool _isHiding;
        public bool IsWakingUp { get; set; }

        public MainWindow()
        {
            InitializeComponent();
            Resized += MainWindow_Resized;
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);

            // OnOpened fires on every Show(). Only run one-time setup once.
            if (!_initialized)
            {
                _initialized = true;

                // Install Win32 hook to intercept and block all minimize commands at the OS level
                InstallMinimizeBlocker();

                // Auto hide/exit the application when it loses active focus (clicks elsewhere)
                Deactivated += OnDeactivated;
            }

            // These must run on every Show() since the window needs to be positioned at the cursor each time
            if (DataContext is MainViewModel vmStartup)
            {
                Width = vmStartup.WindowWidth;
                Height = vmStartup.WindowHeight;
            }

            RepositionAtCursor();
        }

        private void OnDeactivated(object? sender, EventArgs e)
        {
            // Prevent re-entry or instant hide during wakeup
            if (_isHiding || IsWakingUp) return;
            _isHiding = true;

            try
            {
                if (DataContext is MainViewModel vm)
                {
                    if (vm.IsTopmost)
                    {
                        // If always-on-top is enabled, do not hide or exit on focus loss
                        return;
                    }

                    if (vm.IsBackgroundMonitorEnabled)
                    {
                        HideAndCollect();
                    }
                    else
                    {
                        Close();
                    }
                }
                else
                {
                    HideAndCollect();
                }
            }
            finally
            {
                _isHiding = false;
            }
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            // Fallback: if WindowState somehow becomes Minimized, immediately correct it
            if (change.Property == WindowStateProperty &&
                change.NewValue is WindowState.Minimized)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    WindowState = WindowState.Normal;
                    PerformHideOrClose();
                });
            }
        }

        /// <summary>
        /// Subclasses the native window to intercept WM_SYSCOMMAND/SC_MINIMIZE
        /// and convert it to a Hide() or Close() instead of allowing minimization.
        /// Idempotent: safe to call multiple times (e.g. from repeated OnOpened).
        /// </summary>
        private void InstallMinimizeBlocker()
        {
            if (_isSubclassed) return;

            try
            {
                var platformHandle = this.TryGetPlatformHandle();
                if (platformHandle == null) return;

                IntPtr hwnd = platformHandle.Handle;
                _newWndProc = new WndProc(CustomWndProc);
                _originalWndProc = SetWindowLongPtrCompat(hwnd, GWL_WNDPROC,
                    Marshal.GetFunctionPointerForDelegate(_newWndProc));
                _isSubclassed = true;
            }
            catch
            {
                // Ignore subclassing failures – OnPropertyChanged fallback still works
            }
        }

        private IntPtr CustomWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (msg == WM_SYSCOMMAND && ((int)wParam & 0xFFF0) == SC_MINIMIZE)
                {
                    // Block minimize → perform hide/close instead
                    Dispatcher.UIThread.Post(PerformHideOrClose);
                    return IntPtr.Zero;
                }

                // WM_SHOWWINDOW fires when the window is shown by ANY means:
                // - Our own Show() call
                // - External tools (Quicker, etc.) calling ShowWindow/SetForegroundWindow
                // - Any Win32 API that makes the hidden window visible
                // wParam != 0 means "being shown".
                //
                // We use PostMessage instead of Dispatcher.UIThread.Post because:
                // 1. The WndProc runs deep inside ShowWindow — Dispatcher.Post may not
                //    execute until too late (or get swallowed if inside a nested loop).
                // 2. PostMessage queues WM_REPOSITION after ALL show-related messages
                //    (WM_SHOWWINDOW, WM_SIZE, WM_MOVE, WM_WINDOWPOSCHANGED, etc.) have
                //    been processed, so the window is fully positioned before we move it.
                if (msg == WM_SHOWWINDOW && wParam != IntPtr.Zero)
                {
                    // INSTANT guard: prevent OnDeactivated from hiding the window
                    // during the brief activation/deactivation flicker that occurs
                    // when an external tool (Quicker) shows a hidden window.
                    // WM_REPOSITION is too late — Deactivated can fire before it.
                    IsWakingUp = true;

                    bool posted = PostMessage(hWnd, WM_REPOSITION, IntPtr.Zero, IntPtr.Zero);
                    // If PostMessage fails (queue full, invalid handle), reset the guard
                    // immediately; otherwise IsWakingUp stays true permanently and the
                    // window never hides on deactivation.
                    if (!posted)
                        IsWakingUp = false;
                }

                // WM_REPOSITION: our own custom message, queued via PostMessage above.
                // Runs in the normal message loop, well after the window is fully shown.
                if (msg == WM_REPOSITION)
                {
                    // Post to Dispatcher to avoid re-entrancy from setting
                    // Topmost / Activate (each triggers more Win32 messages).
                    Dispatcher.UIThread.Post(() =>
                    {
                        try
                        {
                            RepositionAtCursor();
                            if (!IsVisible)
                            {
                                Show();
                            }
                            RepositionAtCursor();
                            Dispatcher.UIThread.Post(() => RepositionAtCursor());
                            ForceActivate();
                        }
                        finally
                        {
                            // Always release the guard, even if reposition or
                            // activation throws — otherwise IsWakingUp stays
                            // true permanently and the window never hides.
                            _ = ResetWakingUpAsync();
                        }
                    });
                    return IntPtr.Zero;
                }
            }
            catch
            {
                // Never let an exception escape a native callback — it would crash the process
            }

            if (_originalWndProc != IntPtr.Zero)
                return CallWindowProc(_originalWndProc, hWnd, msg, wParam, lParam);

            return IntPtr.Zero;
        }

        /// <summary>
        /// Centralised hide-or-close logic based on user settings.
        /// Either the window is visible or hidden — never minimized.
        /// </summary>
        private void PerformHideOrClose()
        {
            if (DataContext is MainViewModel vm)
            {
                if (vm.IsTopmost)
                {
                    // Topmost mode: stay visible, just ensure Normal state
                    WindowState = WindowState.Normal;
                    return;
                }

                if (vm.IsBackgroundMonitorEnabled)
                {
                    HideAndCollect();
                }
                else
                {
                    Close();
                }
            }
            else
            {
                HideAndCollect();
            }
        }

        private void HideAndCollect()
        {
            Hide();

            // Clear the icon cache: disposes all cached Bitmaps (up to 64),
            // freeing the pixel data that was only needed while the UI was visible.
            IconCache.Clear();

            // Force garbage collection to clean up unused memory on the .NET heap
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            // Flush physical memory pages to standby list (trim working set)
            try
            {
                IntPtr handle = Process.GetCurrentProcess().Handle;
                SetProcessWorkingSetSize(handle, (IntPtr)(-1), (IntPtr)(-1));
            }
            catch
            {
                // Best-effort
            }
        }

        private void MainWindow_Resized(object? sender, WindowResizedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.WindowWidth = ClientSize.Width;
                vm.WindowHeight = ClientSize.Height;
                vm.SaveConfig();
            }
        }

        public void RepositionAtCursor()
        {
            if (GetCursorPos(out POINT point))
            {
                double scaling = RenderScaling;

                // Center the window on the current mouse cursor coordinates
                int physicalWidth = (int)(Width * scaling);
                int physicalHeight = (int)(Height * scaling);

                int newX = point.X - (physicalWidth / 2);
                int newY = point.Y - (physicalHeight / 2);

                // Clamp to screen working area bounds (screen where the mouse is)
                var screen = Screens.ScreenFromPoint(new PixelPoint(point.X, point.Y)) ?? Screens.Primary;
                if (screen != null)
                {
                    PixelRect workArea = screen.WorkingArea;
                    if (newX < workArea.X) newX = workArea.X;
                    if (newY < workArea.Y) newY = workArea.Y;
                    if (newX + physicalWidth > workArea.X + workArea.Width) newX = workArea.X + workArea.Width - physicalWidth;
                    if (newY + physicalHeight > workArea.Y + workArea.Height) newY = workArea.Y + workArea.Height - physicalHeight;
                }

                Position = new PixelPoint(newX, newY);
            }
        }

        /// <summary>
        /// Force-activates the window using low-level Win32 thread input attachment.
        /// This bypasses Windows' foreground lock, which normally prevents a background
        /// process from activating its own hidden window without AllowSetForegroundWindow.
        /// </summary>
        private void ForceActivate()
        {
            try
            {
                var handle = this.TryGetPlatformHandle()?.Handle;
                if (handle == null || handle == IntPtr.Zero) return;

                // Get the thread IDs: foreground window's thread and our window's thread
                uint ourThreadId = GetWindowThreadProcessId(handle.Value, out _);
                IntPtr fgHwnd = GetForegroundWindow();
                uint fgThreadId = GetWindowThreadProcessId(fgHwnd, out _);

                // Attach our input state to the foreground thread's input state.
                // This temporarily merges the two threads' input queues, bypassing
                // the foreground lock that SetForegroundWindow checks.
                bool attached = false;
                if (fgThreadId != ourThreadId && fgThreadId != 0)
                {
                    attached = AttachThreadInput(fgThreadId, ourThreadId, true);
                }

                try
                {
                    // Now SetForegroundWindow should succeed since our input queue
                    // is attached to the foreground thread's queue.
                    SetForegroundWindow(handle.Value);
                    BringWindowToTop(handle.Value);
                    SetFocus(handle.Value);
                }
                finally
                {
                    // Detach input queues to restore normal input processing
                    if (attached)
                    {
                        AttachThreadInput(fgThreadId, ourThreadId, false);
                    }
                }
            }
            catch
            {
                // Best-effort — if this fails, fall back to Avalonia's standard activation
                try { Activate(); } catch { }
            }
        }

        /// <summary>
        /// Resets IsWakingUp after a delay, running on the UI thread
        /// to avoid the data-race that Task.Delay(...).ContinueWith
        /// would cause on a thread-pool thread.
        /// </summary>
        private async Task ResetWakingUpAsync()
        {
            await Task.Delay(200);
            IsWakingUp = false;
        }

        private void TitleBar_PointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                BeginMoveDrag(e);
            }
        }

        private void HistoryList_DoubleTapped(object sender, TappedEventArgs e)
        {
            if (sender is ListBox listBox && listBox.SelectedItem is FolderHistoryItem item)
            {
                if (DataContext is MainViewModel vm)
                {
                    vm.OpenFolderCommand.Execute(item);
                }
            }
        }

        private void OpenDataFolderButton_Click(object sender, RoutedEventArgs e)
        {
            string storageDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Tuna"
            );
            try
            {
                if (Directory.Exists(storageDir))
                {
                    Process.Start(new ProcessStartInfo(storageDir) { UseShellExecute = true });
                }
                else
                {
                    Directory.CreateDirectory(storageDir);
                    Process.Start(new ProcessStartInfo(storageDir) { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                if (DataContext is MainViewModel vm)
                {
                    vm.OpenFolderCommand.Execute(null);
                }
                Debug.WriteLine($"无法打开数据文件夹: {ex.Message}");
            }
        }

        private void Resize_PointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (sender is Border border && border.Tag is string edgeStr && Enum.TryParse<WindowEdge>(edgeStr, out var edge))
            {
                BeginResizeDrag(edge, e);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            // Closing the panel should hide it when background monitoring is enabled,
            // including in topmost mode, so the activation shortcut can show it again.
            if (DataContext is MainViewModel vm && vm.IsBackgroundMonitorEnabled)
            {
                HideAndCollect();
                return;
            }

            Close();
        }

        private void ExitApplicationButton_Click(object sender, RoutedEventArgs e)
        {
            Environment.Exit(0);
        }

        private void QQGroupButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string url = "https://qm.qq.com/cgi-bin/qm/qr?_wv=1027&k=7q8lAcCA3xTr-PZ2XG8VA04BIyfKhXeV&authKey=rSwrwGZzcJKWm%2F3zfYeTATxVBt%2B170gK4DbDezYPwKMZGI0BH6VSYUp6PYZXTO%2BC&noverify=0&group_code=453478357";
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch
            {
                // Best-effort
            }
        }

        private void GitHubButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string url = "https://github.com/linhuoxi/tuna";
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch
            {
                // Best-effort
            }
        }
    }
}
