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
        private const int WM_ACTIVATE = 0x0006;
        private const int WM_SYSCOMMAND = 0x0112;
        private const int WM_SHOWWINDOW = 0x0018;
        private const int WM_WINDOWPOSCHANGING = 0x0046;
        private const int WA_ACTIVE = 1;
        private const int SC_MINIMIZE = 0xF020;
        private const int SW_HIDE = 0;
        private const uint WM_APP = 0x8000;
        private const uint WM_REPOSITION = WM_APP + 1;

        // ── Global hidden hotkey: Ctrl+Alt+Shift+Win+F13 ──
        private const int WM_HOTKEY = 0x0312;
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_WIN = 0x0008;
        private const uint MOD_NOREPEAT = 0x4000;
        private const uint VK_F13 = 0x7C;
        private const int HOTKEY_ID = 0xB001;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;

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

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

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
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", EntryPoint = "ShowWindow")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool NativeShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

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

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string? lpszWindow);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, string lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        private const uint WM_SETTEXT = 0x000C;
        private const int VK_RETURN = 0x0D;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        private const uint GW_HWNDNEXT = 2;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        private const uint GW_CHILD = 5;

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

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
        private bool _hotkeyRegistered;          // idempotency guard: register hotkey once
        private IntPtr _hotkeyHwnd = IntPtr.Zero; // hwnd the hotkey was registered on

        // ── One-time initialization guard ──
        // Avalonia calls OnOpened every time Show() is invoked, so we must ensure
        // that event subscriptions and Win32 subclassing happen exactly once.
        private bool _initialized;

        // ── Re-entry guard for Deactivated → Hide() ──
        // Hide() can trigger another Deactivated; this flag prevents recursive hiding.
        private bool _isHiding;
        private bool _repositionMessagePending;
        private bool _isInternalWakeupShow;
        private bool _externalActivationSyncPending;
        private bool _isSynchronizingExternalActivation;
        private PixelPoint? _showTargetPosition;
        private long _lastExternalShowTick;
        private int _showGeneration;
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

            bool firstOpening = !_initialized;

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
                // If it is the old default width/height, automatically upgrade them to include the shadow margins
                if (vmStartup.WindowWidth == 380) vmStartup.WindowWidth = 460;
                if (vmStartup.WindowHeight == 550) vmStartup.WindowHeight = 630;

                Width = vmStartup.WindowWidth;
                Height = vmStartup.WindowHeight;
                vmStartup.IsWindowVisible = true;
            }

            // Native tools can show the HWND without going through Avalonia's Show().
            // OnOpened is the reliable framework-level confirmation of that transition.
            if (!firstOpening && !_isInternalWakeupShow)
            {
                _lastExternalShowTick = Environment.TickCount64;
            }

            ApplyShowTargetOrReposition();
        }

        private void OnDeactivated(object? sender, EventArgs e)
        {
            // Prevent re-entry while an actual hide is in progress.
            if (_isHiding) return;

            // Only suppress hiding while an activation is genuinely in flight. IsWakingUp
            // can leak as a stale 'true' (a missed reset), and if we blindly honored it the
            // window would refuse to hide on focus loss — leaving the native HWND visible so
            // the next ShowWindow becomes a no-op and the panel stops following the cursor.
            // Time-bound the guard: only a very recent external activation blocks the hide.
            if (IsWakingUp && WasExternallyShownRecently(500)) return;

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

                RegisterGlobalHotkey(hwnd);
            }
            catch
            {
                // Ignore subclassing failures – OnPropertyChanged fallback still works
            }
        }

        /// <summary>
        /// Registers the hidden global hotkey (Ctrl+Alt+Shift+Win+F13). This is not shown
        /// anywhere in the UI. Firing it activates the panel at the cursor just like a
        /// Quicker/process-name activation. Registration failure (e.g. keyboard without F13,
        /// or the combo already taken) is silently ignored and does not affect other features.
        /// </summary>
        private void RegisterGlobalHotkey(IntPtr hwnd)
        {
            if (_hotkeyRegistered || hwnd == IntPtr.Zero) return;

            try
            {
                bool ok = RegisterHotKey(
                    hwnd,
                    HOTKEY_ID,
                    MOD_CONTROL | MOD_ALT | MOD_SHIFT | MOD_WIN | MOD_NOREPEAT,
                    VK_F13);

                if (ok)
                {
                    _hotkeyRegistered = true;
                    _hotkeyHwnd = hwnd;
                }
            }
            catch
            {
                // Best-effort — hotkey is an optional convenience.
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            if (_hotkeyRegistered && _hotkeyHwnd != IntPtr.Zero)
            {
                try { UnregisterHotKey(_hotkeyHwnd, HOTKEY_ID); }
                catch { }
                _hotkeyRegistered = false;
            }

            base.OnClosed(e);
        }

        /// <summary>
        /// Shows and activates the panel at the current cursor in response to the hidden
        /// global hotkey. Mirrors the internal wakeup show sequence used for Quicker so the
        /// panel appears at the cursor and takes focus, whether it was hidden or already visible.
        /// </summary>
        private void ActivateFromHotkey()
        {
            // If an activation is already in flight for the same user action, don't double it.
            if (IsWakingUp || WasExternallyShownRecently()) return;

            BeginInternalWakeupShow();
            try
            {
                if (WindowState != WindowState.Normal)
                    WindowState = WindowState.Normal;

                if (!IsVisible)
                    Show();

                bool originalTopmost = false;
                if (DataContext is MainViewModel vmOrig)
                    originalTopmost = vmOrig.IsTopmost;

                // Force Topmost temporarily to bypass Windows' foreground lock.
                Topmost = true;
                Activate();
                ForceActivate();

                if (DataContext is MainViewModel vmRestore)
                    vmRestore.IsTopmost = originalTopmost;
                Topmost = originalTopmost;
            }
            catch
            {
                // Best-effort
            }
            finally
            {
                EndInternalWakeupShow();
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

                // Hidden global hotkey (Ctrl+Alt+Shift+Win+F13): show & activate at cursor.
                if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
                {
                    // Post to the UI thread to avoid re-entering window lifecycle from
                    // inside the raw WndProc callback.
                    Dispatcher.UIThread.Post(ActivateFromHotkey);
                    return IntPtr.Zero;
                }

                // Quicker may activate an HWND that Windows still considers visible. In that
                // case no WM_SHOWWINDOW is sent, so WA_ACTIVE is the only reliable signal.
                // WA_CLICKACTIVE is intentionally ignored so clicking the window never moves it.
                if (msg == WM_ACTIVATE &&
                    ((int)(wParam.ToInt64() & 0xFFFF)) == WA_ACTIVE &&
                    !_isInternalWakeupShow &&
                    !_isSynchronizingExternalActivation)
                {
                    HandleExternalActivation(hWnd, "WM_ACTIVATE");
                }

                // Intercept the native placement before Windows paints a window that is
                // being shown. This prevents a hidden window from flashing at its previous
                // location before the later Avalonia reposition runs.
                if (msg == WM_WINDOWPOSCHANGING && lParam != IntPtr.Zero)
                {
                    var windowPos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                    if ((windowPos.flags & SWP_SHOWWINDOW) != 0 &&
                        (windowPos.flags & SWP_NOMOVE) == 0)
                    {
                        int physicalWidth = windowPos.cx;
                        int physicalHeight = windowPos.cy;
                        ResolveNativeWindowSize(hWnd, ref physicalWidth, ref physicalHeight);

                        EnsureShowTarget(physicalWidth, physicalHeight, !_isInternalWakeupShow && !_isSynchronizingExternalActivation);
                        if (_showTargetPosition is PixelPoint target)
                        {
                            windowPos.x = target.X;
                            windowPos.y = target.Y;
                            Marshal.StructureToPtr(windowPos, lParam, false);
                        }
                    }
                }

                // WM_SHOWWINDOW is sent synchronously while the native window is about to
                // become visible. Move the HWND now, before returning to ShowWindow, rather
                // than waiting until the old position has already been painted.
                if (msg == WM_SHOWWINDOW && wParam != IntPtr.Zero)
                {
                    if (_isInternalWakeupShow || _isSynchronizingExternalActivation)
                    {
                        RepositionNativeAtCursor(hWnd);
                        QueueReposition(hWnd);
                    }
                    else
                    {
                        HandleExternalActivation(hWnd, "WM_SHOWWINDOW");
                    }
                }

                // One post-show correction remains as a compatibility fallback for window
                // managers that alter placement after WM_SHOWWINDOW has completed.
                if (msg == WM_REPOSITION)
                {
                    _repositionMessagePending = false;
                    int generation = _showGeneration;
                    Dispatcher.UIThread.Post(() =>
                    {
                        try
                        {
                            ApplyShowTargetOrReposition();
                            ForceActivate();
                        }
                        finally
                        {
                            _ = ResetWakingUpAsync(generation);
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

            if (DataContext is MainViewModel vm)
            {
                vm.IsWindowVisible = false;
            }

            // External tools show the HWND without updating Avalonia's visibility state.
            // If Avalonia already believes the window is hidden, Hide() can be a no-op;
            // force the native HWND hidden so the next activation starts a real show cycle.
            try
            {
                IntPtr hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                if (hwnd != IntPtr.Zero)
                    NativeShowWindow(hwnd, SW_HIDE);
            }
            catch {}

            // Defer a cleanup that only runs if the window stays hidden.
            ScheduleDeferredCleanup();
        }

        // Cancels a pending deferred cleanup when the window is shown again quickly.
        private int _cleanupGeneration;

        /// <summary>
        /// Schedules a memory cleanup a few seconds after the window is hidden.
        /// If the window is shown again before it fires (the common case), the cleanup is
        /// skipped entirely, keeping re-open instant. When it does run, it uses a forced
        /// compacting GC and trims the process working set to minimize memory usage under 20MB.
        /// </summary>
        private void ScheduleDeferredCleanup()
        {
            int generation = ++_cleanupGeneration;
            _ = Task.Delay(4000).ContinueWith(_ =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    // A newer show/hide happened, or the window is visible again — skip.
                    if (generation != _cleanupGeneration || IsVisible) return;

                    try
                    {
                        IconCache.Clear();
                        
                        // Force GC and wait for finalizers to reclaim all unused memory
                        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                        GC.WaitForPendingFinalizers();
                        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

                        // Trim process working set
                        IntPtr processHandle = System.Diagnostics.Process.GetCurrentProcess().Handle;
                        SetProcessWorkingSetSize(processHandle, (IntPtr)(-1), (IntPtr)(-1));
                    }
                    catch
                    {
                        // Best-effort
                    }
                });
            });
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

        private void ResolveNativeWindowSize(IntPtr hWnd, ref int physicalWidth, ref int physicalHeight)
        {
            if ((physicalWidth <= 0 || physicalHeight <= 0) &&
                GetWindowRect(hWnd, out RECT rect))
            {
                physicalWidth = rect.Right - rect.Left;
                physicalHeight = rect.Bottom - rect.Top;
            }

            double scaling = RenderScaling;
            if (physicalWidth <= 0)
                physicalWidth = Math.Max(1, (int)(Width * scaling));
            if (physicalHeight <= 0)
                physicalHeight = Math.Max(1, (int)(Height * scaling));
        }

        private bool TryCalculateCursorTarget(int physicalWidth, int physicalHeight, out PixelPoint target)
        {
            return TryCalculateCursorTarget(physicalWidth, physicalHeight, out _, out target);
        }

        private bool TryCalculateCursorTarget(int physicalWidth, int physicalHeight, out POINT point, out PixelPoint target)
        {
            target = default;
            if (!GetCursorPos(out point)) return false;

            int newX = point.X - (physicalWidth / 2);
            int newY = point.Y - (physicalHeight / 2);

            var screen = Screens.ScreenFromPoint(new PixelPoint(point.X, point.Y)) ?? Screens.Primary;
            if (screen != null)
            {
                PixelRect workArea = screen.WorkingArea;
                int maxX = workArea.X + Math.Max(0, workArea.Width - physicalWidth);
                int maxY = workArea.Y + Math.Max(0, workArea.Height - physicalHeight);
                newX = Math.Clamp(newX, workArea.X, maxX);
                newY = Math.Clamp(newY, workArea.Y, maxY);
            }

            target = new PixelPoint(newX, newY);
            return true;
        }

        private void HandleExternalActivation(IntPtr hWnd, string source)
        {
            IsWakingUp = true;

            int physicalWidth = 0;
            int physicalHeight = 0;
            ResolveNativeWindowSize(hWnd, ref physicalWidth, ref physicalHeight);

            // Always treat this as an external show. Whether the cursor is re-read or
            // the previous target is reused is decided purely by the time-based
            // de-duplication inside EnsureShowTarget: messages belonging to the same
            // activation arrive within milliseconds and share one target, while a
            // brand-new activation (seconds apart) always recomputes the cursor.
            // This removes the old IsWakingUp/_showTargetPosition "continue cycle"
            // heuristic, which could leak stale state and reuse a stale position.
            EnsureShowTarget(physicalWidth, physicalHeight, true);
            RepositionNativeAtCursor(hWnd);
            QueueReposition(hWnd);
            QueueExternalActivationSync(hWnd);
        }

        private void QueueReposition(IntPtr hWnd)
        {
            // Several framework/native messages may arrive for one activation. Coalesce
            // them into one fallback correction using the target captured for this cycle.
            if (_repositionMessagePending) return;

            _repositionMessagePending = true;
            if (!PostMessage(hWnd, WM_REPOSITION, IntPtr.Zero, IntPtr.Zero))
            {
                _repositionMessagePending = false;
                IsWakingUp = false;
            }
        }

        private void QueueExternalActivationSync(IntPtr hWnd)
        {
            // A process-name activation can show the HWND without taking Avalonia's
            // window lifecycle with it. Coalesce the native messages, then sync Show
            // and focus on the UI thread after the Win32 callback has unwound.
            if (_isInternalWakeupShow || _externalActivationSyncPending) return;

            _externalActivationSyncPending = true;
            Dispatcher.UIThread.Post(() =>
            {
                _externalActivationSyncPending = false;
                if (_isInternalWakeupShow) return;

                _isSynchronizingExternalActivation = true;
                try
                {
                    if (WindowState != WindowState.Normal)
                        WindowState = WindowState.Normal;

                    if (!IsVisible)
                        Show();

                    ApplyShowTargetOrReposition();

                    try { Activate(); } catch { }
                    ForceActivate();
                }
                finally
                {
                    _isSynchronizingExternalActivation = false;
                }
            });
        }

        private void EnsureShowTarget(int physicalWidth, int physicalHeight, bool externalShow)
        {
            long now = Environment.TickCount64;
            if (externalShow)
            {
                // All native messages + the Dispatcher sync belonging to one activation
                // arrive within a short burst, so they share a single target. A brand-new
                // activation is always seconds apart and recomputes the cursor. The window
                // is wide enough (200ms) to cover ShowWindow -> SetForegroundWindow -> the
                // posted UI-thread sync, yet far smaller than the gap between real activations.
                bool sameNativeShow = _showTargetPosition.HasValue &&
                    _lastExternalShowTick != 0 &&
                    now - _lastExternalShowTick >= 0 &&
                    now - _lastExternalShowTick <= 200;

                if (!sameNativeShow)
                {
                    _showGeneration++;
                    _showTargetPosition = null;
                }

                _lastExternalShowTick = now;
            }

            if (!_showTargetPosition.HasValue &&
                TryCalculateCursorTarget(physicalWidth, physicalHeight, out PixelPoint target))
            {
                _showTargetPosition = target;
            }
        }

        public bool WasExternallyShownRecently(int milliseconds = 500)
        {
            if (_lastExternalShowTick == 0) return false;
            long elapsed = Environment.TickCount64 - _lastExternalShowTick;
            return elapsed >= 0 && elapsed <= milliseconds;
        }

        public void BeginInternalWakeupShow()
        {
            _isInternalWakeupShow = true;
            IsWakingUp = true;
            _showGeneration++;
            _showTargetPosition = null;

            int physicalWidth = Math.Max(1, (int)(Width * RenderScaling));
            int physicalHeight = Math.Max(1, (int)(Height * RenderScaling));
            EnsureShowTarget(physicalWidth, physicalHeight, false);
            ApplyShowTargetOrReposition();
        }

        public void EndInternalWakeupShow()
        {
            _isInternalWakeupShow = false;
            _ = ResetWakingUpAsync(_showGeneration);
        }

        private void ApplyShowTargetOrReposition()
        {
            if (_showTargetPosition is PixelPoint target)
            {
                Position = target;
            }
            else
            {
                RepositionAtCursor();
            }
        }

        private void RepositionNativeAtCursor(IntPtr hWnd)
        {
            int physicalWidth = 0;
            int physicalHeight = 0;
            ResolveNativeWindowSize(hWnd, ref physicalWidth, ref physicalHeight);
            EnsureShowTarget(physicalWidth, physicalHeight, false);

            if (_showTargetPosition is PixelPoint target)
            {
                SetWindowPos(
                    hWnd,
                    IntPtr.Zero,
                    target.X,
                    target.Y,
                    0,
                    0,
                    SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            }
        }

        public void RepositionAtCursor()
        {
            int physicalWidth = Math.Max(1, (int)(Width * RenderScaling));
            int physicalHeight = Math.Max(1, (int)(Height * RenderScaling));
            if (TryCalculateCursorTarget(physicalWidth, physicalHeight, out POINT point, out PixelPoint targetPos))
            {
                Position = targetPos;
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

                IntPtr fgHwnd = GetForegroundWindow();
                if (fgHwnd == handle.Value)
                {
                    SetFocus(handle.Value);
                    return;
                }

                uint currentThreadId = GetCurrentThreadId();
                uint fgThreadId = GetWindowThreadProcessId(fgHwnd, out _);

                // Attach this UI thread to the current foreground input queue while
                // requesting foreground focus. This is the documented direction for
                // bypassing the foreground lock, and it is detached immediately after.
                bool attached = false;
                if (fgThreadId != currentThreadId && fgThreadId != 0)
                {
                    attached = AttachThreadInput(currentThreadId, fgThreadId, true);
                }

                try
                {
                    SetForegroundWindow(handle.Value);
                    BringWindowToTop(handle.Value);
                    SetFocus(handle.Value);
                }
                finally
                {
                    if (attached)
                    {
                        AttachThreadInput(currentThreadId, fgThreadId, false);
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
        private async Task ResetWakingUpAsync(int generation)
        {
            await Task.Delay(200);

            // If a newer activation has started, that newer cycle owns the state and will
            // clear it — this stale reset must not touch anything.
            if (generation != _showGeneration) return;

            // This is the latest generation: unconditionally clear the wakeup state so
            // IsWakingUp can never leak as a permanent 'true' and block auto-hide.
            _showTargetPosition = null;
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
                // Try navigating the active file dialog first
                IntPtr dialogHwnd = FindActiveFileDialog();
                if (dialogHwnd != IntPtr.Zero && TryNavigateFileDialog(dialogHwnd, item.Path))
                {
                    HideAndCollect();
                    return;
                }

                // Fallback to normal behavior
                if (DataContext is MainViewModel vm)
                {
                    vm.OpenFolderCommand.Execute(item);
                }
            }
        }

        private IntPtr FindActiveFileDialog()
        {
            try
            {
                var handle = this.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                if (handle == IntPtr.Zero) return IntPtr.Zero;

                IntPtr nextHwnd = GetWindow(handle, GW_HWNDNEXT);
                int searchLimit = 30; // Check top 30 windows in Z-order down
                while (nextHwnd != IntPtr.Zero && searchLimit-- > 0)
                {
                    if (IsWindowVisible(nextHwnd))
                    {
                        System.Text.StringBuilder className = new System.Text.StringBuilder(256);
                        if (GetClassName(nextHwnd, className, className.Capacity) > 0)
                        {
                            string clsName = className.ToString();
                            if (clsName == "#32770")
                            {
                                // Verify it contains controls characteristic of a file dialog (Edit or ComboBoxEx32)
                                IntPtr comboBoxEx = FindChildWindow(nextHwnd, "ComboBoxEx32");
                                IntPtr editHwnd = FindChildWindow(nextHwnd, "Edit");
                                if (comboBoxEx != IntPtr.Zero || editHwnd != IntPtr.Zero)
                                {
                                    return nextHwnd;
                                }
                            }
                        }
                    }
                    nextHwnd = GetWindow(nextHwnd, GW_HWNDNEXT);
                }
            }
            catch
            {
                // Best-effort
            }
            return IntPtr.Zero;
        }

        private IntPtr FindChildWindow(IntPtr parent, string className)
        {
            if (parent == IntPtr.Zero) return IntPtr.Zero;

            try
            {
                // Get the first child of the parent window
                IntPtr child = GetWindow(parent, GW_CHILD);
                while (child != IntPtr.Zero)
                {
                    // Check if this child matches the class name
                    System.Text.StringBuilder sb = new System.Text.StringBuilder(256);
                    if (GetClassName(child, sb, sb.Capacity) > 0 && sb.ToString() == className)
                    {
                        return child;
                    }

                    // Recursively search this child's descendants
                    IntPtr descendent = FindChildWindow(child, className);
                    if (descendent != IntPtr.Zero)
                    {
                        return descendent;
                    }

                    // Move to the next sibling window
                    child = GetWindow(child, GW_HWNDNEXT);
                }
            }
            catch
            {
                // Best-effort
            }
            return IntPtr.Zero;
        }

        private bool TryNavigateFileDialog(IntPtr hwnd, string path)
        {
            if (hwnd == IntPtr.Zero) return false;

            try
            {
                // 1. Check class name of target window to verify it's a dialog
                System.Text.StringBuilder className = new System.Text.StringBuilder(256);
                if (GetClassName(hwnd, className, className.Capacity) == 0) return false;

                if (className.ToString() != "#32770") return false;

                // 2. Locate the Edit control within ComboBoxEx32 -> ComboBox -> Edit
                IntPtr editHwnd = IntPtr.Zero;
                IntPtr comboBoxEx = FindChildWindow(hwnd, "ComboBoxEx32");
                if (comboBoxEx != IntPtr.Zero)
                {
                    IntPtr comboBox = FindWindowEx(comboBoxEx, IntPtr.Zero, "ComboBox", null);
                    if (comboBox != IntPtr.Zero)
                    {
                        editHwnd = FindWindowEx(comboBox, IntPtr.Zero, "Edit", null);
                    }
                }

                // Fallback: search for any Edit control recursively
                if (editHwnd == IntPtr.Zero)
                {
                    editHwnd = FindChildWindow(hwnd, "Edit");
                }

                if (editHwnd == IntPtr.Zero) return false;

                // 3. Populate target path
                SendMessage(editHwnd, WM_SETTEXT, IntPtr.Zero, path);

                // 4. Post Enter key messages to the edit control to initiate navigation asynchronously
                const uint WM_KEYDOWN = 0x0100;
                const uint WM_CHAR = 0x0102;
                const uint WM_KEYUP = 0x0101;

                PostMessage(editHwnd, WM_KEYDOWN, new IntPtr(VK_RETURN), IntPtr.Zero);
                PostMessage(editHwnd, WM_CHAR, new IntPtr(13), IntPtr.Zero);
                PostMessage(editHwnd, WM_KEYUP, new IntPtr(VK_RETURN), IntPtr.Zero);

                return true;
            }
            catch
            {
                return false;
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
