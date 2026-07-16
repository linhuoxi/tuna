using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerHistoryTracker.Services
{
    public enum FileDialogActivationSource
    {
        Startup,
        FloatBall,
        GlobalHotkey,
        WakeupEvent,
        ExternalShow,
        ExternalActivate
    }

    internal enum FileDialogNavigationStatus
    {
        Success,
        NoTarget,
        InvalidPath,
        TargetExpired,
        NotStandardDialog,
        FileNameControlNotFound,
        ConfirmButtonNotFound,
        WriteFailed,
        CommandFailed,
        DialogClosed,
        NavigationNotObserved
    }

    internal readonly struct FileDialogNavigationResult
    {
        public FileDialogNavigationResult(
            FileDialogNavigationStatus status,
            IntPtr dialogHandle,
            string message,
            int targetGeneration)
        {
            Status = status;
            DialogHandle = dialogHandle;
            Message = message;
            TargetGeneration = targetGeneration;
        }

        public FileDialogNavigationStatus Status { get; }
        public IntPtr DialogHandle { get; }
        public string Message { get; }
        internal int TargetGeneration { get; }
        public bool IsSuccess => Status == FileDialogNavigationStatus.Success;
        public bool ShouldOpenNormally => Status == FileDialogNavigationStatus.NoTarget;
    }

    /// <summary>
    /// Navigates a standard Windows Open/Save dialog without global keyboard input.
    /// A target is captured before Tuna takes focus, then revalidated by HWND, process,
    /// thread, class and standard control IDs before any cross-process message is sent.
    /// </summary>
    internal sealed class FileDialogNavigationService
    {
        private const int GA_ROOT = 2;
        private const uint GW_CHILD = 5;
        private const uint GW_HWNDNEXT = 2;

        private const int IDC_FILENAME_COMBO = 0x047C;
        private const int IDC_FILENAME_EDIT = 0x0480;
        private const int IDC_MODERN_FILENAME_EDIT = 0x03E9;
        private const int IDOK = 1;

        private const uint WM_SETTEXT = 0x000C;
        private const uint WM_GETTEXT = 0x000D;
        private const uint WM_GETTEXTLENGTH = 0x000E;
        private const uint WM_COMMAND = 0x0111;

        private const uint SMTO_ABORTIFHUNG = 0x0002;
        private const uint SMTO_ERRORONEXIT = 0x0020;
        private const uint SendMessageFlags = SMTO_ABORTIFHUNG | SMTO_ERRORONEXIT;
        private const uint SendMessageTimeoutMilliseconds = 750;
        private const int PathResolutionTimeoutMilliseconds = 2500;
        private const int NavigationObservationTimeoutMilliseconds = 4000;
        private const int NavigationStateCheckIntervalMilliseconds = 100;

        private const uint EVENT_OBJECT_CREATE = 0x8000;
        private const uint EVENT_OBJECT_DESTROY = 0x8001;
        private const uint EVENT_OBJECT_REORDER = 0x8004;
        private const uint EVENT_OBJECT_VALUECHANGE = 0x800E;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

        private readonly object _targetLock = new();
        private TargetSnapshot _target;
        private TargetLifetimeWatcher? _targetLifetimeWatcher;
        private int _generation;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsChild(IntPtr parent, IntPtr child);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassNameW(IntPtr hWnd, StringBuilder className, int maxCount);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDlgItem(IntPtr hDlg, int controlId);

        [DllImport("user32.dll")]
        private static extern int GetDlgCtrlID(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint command);

        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SendMessageTimeoutText(
            IntPtr hWnd,
            uint message,
            IntPtr wParam,
            string lParam,
            uint flags,
            uint timeout,
            out IntPtr messageResult);

        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SendMessageTimeoutBuffer(
            IntPtr hWnd,
            uint message,
            IntPtr wParam,
            StringBuilder lParam,
            uint flags,
            uint timeout,
            out IntPtr messageResult);

        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
        private static extern IntPtr SendMessageTimeoutPointer(
            IntPtr hWnd,
            uint message,
            IntPtr wParam,
            IntPtr lParam,
            uint flags,
            uint timeout,
            out IntPtr messageResult);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void WinEventProc(
            IntPtr hook,
            uint eventType,
            IntPtr hWnd,
            int objectId,
            int childId,
            uint eventThread,
            uint eventTime);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWinEventHook(
            uint eventMin,
            uint eventMax,
            IntPtr eventHookModule,
            WinEventProc eventHook,
            uint processId,
            uint threadId,
            uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWinEvent(IntPtr hook);

        public bool HasTarget
        {
            get
            {
                lock (_targetLock)
                {
                    return !_target.IsEmpty;
                }
            }
        }

        /// <summary>
        /// Captures only a verified standard file dialog. An own-process candidate is
        /// ignored so duplicate activation messages cannot erase a valid external target.
        /// A different external, non-file window starts a clean session.
        /// </summary>
        public bool CaptureTarget(IntPtr candidate, FileDialogActivationSource source)
        {
            if (candidate == IntPtr.Zero)
            {
                candidate = GetForegroundWindow();
            }

            IntPtr root = NormalizeRoot(candidate);
            if (root == IntPtr.Zero || !IsWindow(root))
            {
                ClearTarget();
                return false;
            }

            uint threadId = GetWindowThreadProcessId(root, out uint processId);
            if (processId == (uint)Environment.ProcessId)
            {
                return false;
            }

            if (processId == 0 || threadId == 0 ||
                !TryResolveStandardDialog(root, out _, out _, out _, out _))
            {
                ClearTarget();
                return false;
            }

            TargetLifetimeWatcher? previousWatcher;
            TargetSnapshot snapshot;
            lock (_targetLock)
            {
                previousWatcher = _targetLifetimeWatcher;
                _targetLifetimeWatcher = null;
                _generation++;
                snapshot = new TargetSnapshot(
                    root,
                    processId,
                    threadId,
                    _generation,
                    source);
                _target = snapshot;
            }
            previousWatcher?.Dispose();

            TargetLifetimeWatcher? lifetimeWatcher = new TargetLifetimeWatcher(
                snapshot.Handle,
                snapshot.ProcessId,
                snapshot.ThreadId,
                snapshot.Generation,
                ClearTargetIfGeneration);
            lifetimeWatcher.Start();

            lock (_targetLock)
            {
                if (_target.Generation == snapshot.Generation)
                {
                    _targetLifetimeWatcher = lifetimeWatcher;
                    lifetimeWatcher = null;
                }
            }
            lifetimeWatcher?.Dispose();

            return true;
        }

        public void ClearTarget()
        {
            TargetLifetimeWatcher? watcher;
            lock (_targetLock)
            {
                watcher = _targetLifetimeWatcher;
                _targetLifetimeWatcher = null;
                _generation++;
                _target = default;
            }
            watcher?.Dispose();
        }

        public bool TryActivateTarget(FileDialogNavigationResult result)
        {
            if (!result.IsSuccess || result.TargetGeneration == 0)
            {
                return false;
            }

            TargetSnapshot snapshot;
            lock (_targetLock)
            {
                snapshot = _target;
            }

            return snapshot.Generation == result.TargetGeneration &&
                   snapshot.Handle == result.DialogHandle &&
                   ValidateSnapshot(snapshot) == TargetValidationState.Valid &&
                   TryResolveStandardDialog(
                       snapshot.Handle,
                       out _,
                       out _,
                       out _,
                       out _) &&
                   SetForegroundWindow(snapshot.Handle);
        }

        public async Task<FileDialogNavigationResult> NavigateAsync(string requestedPath)
        {
            Task<string?> pathResolutionTask = Task.Run(() =>
                TryResolveNavigationDirectory(requestedPath, out string resolvedPath)
                    ? resolvedPath
                    : null);
            Task completedPathTask = await Task.WhenAny(
                pathResolutionTask,
                Task.Delay(PathResolutionTimeoutMilliseconds));

            if (completedPathTask != pathResolutionTask)
            {
                return Result(
                    FileDialogNavigationStatus.InvalidPath,
                    IntPtr.Zero,
                    $"检查目标路径超时，请确认网络位置或磁盘是否可用：\n{requestedPath}");
            }

            string? navigationPath = await pathResolutionTask;
            if (string.IsNullOrEmpty(navigationPath))
            {
                return Result(
                    FileDialogNavigationStatus.InvalidPath,
                    IntPtr.Zero,
                    $"路径不存在或无法取得所在文件夹：\n{requestedPath}");
            }

            if (!TryGetValidatedTarget(
                    out TargetSnapshot snapshot,
                    out IntPtr fileNameControl,
                    out IntPtr confirmButton,
                    out IntPtr shellView,
                    out FileDialogNavigationResult validationFailure))
            {
                return validationFailure;
            }

            using var watcher = new NavigationEventWatcher(
                snapshot.Handle,
                shellView,
                snapshot.ProcessId,
                snapshot.ThreadId);
            watcher.Start();

            CommandDispatchResult dispatchResult = await Task.Run(() =>
                DispatchNavigationCommand(
                    snapshot,
                    fileNameControl,
                    confirmButton,
                    navigationPath,
                    watcher));

            if (dispatchResult != CommandDispatchResult.Sent)
            {
                return dispatchResult switch
                {
                    CommandDispatchResult.TargetExpired => Result(
                        FileDialogNavigationStatus.TargetExpired,
                        snapshot.Handle,
                        "原来的打开/另存为窗口已经失效。"),
                    CommandDispatchResult.WriteFailed => Result(
                        FileDialogNavigationStatus.WriteFailed,
                        snapshot.Handle,
                        "无法把目录写入目标文件对话框，可能存在权限隔离。"),
                    _ => Result(
                        FileDialogNavigationStatus.CommandFailed,
                        snapshot.Handle,
                        "目标文件对话框没有响应目录切换命令。")
                };
            }

            long deadline = Environment.TickCount64 + NavigationObservationTimeoutMilliseconds;
            while (true)
            {
                TargetValidationState targetState = ValidateSnapshot(snapshot);
                if (watcher.DialogDestroyed || !IsWindow(snapshot.Handle))
                {
                    ClearTargetIfGeneration(snapshot.Generation);
                    return Result(
                        FileDialogNavigationStatus.DialogClosed,
                        snapshot.Handle,
                        "目标打开/另存为窗口在切换过程中已关闭。面板没有自动隐藏。");
                }

                if (targetState == TargetValidationState.Expired)
                {
                    return Result(
                        FileDialogNavigationStatus.TargetExpired,
                        snapshot.Handle,
                        "目录切换目标已被新的面板激活会话替换，当前操作已停止。" );
                }

                if (targetState == TargetValidationState.NotStandardDialog)
                {
                    ClearTargetIfGeneration(snapshot.Generation);
                    return Result(
                        FileDialogNavigationStatus.NotStandardDialog,
                        snapshot.Handle,
                        "目标窗口已不再是受支持的标准打开/另存为窗口。" );
                }

                DialogObservation observation = await Task.Run(() =>
                {
                    if (!TryResolveStandardDialog(
                            snapshot.Handle,
                            out IntPtr currentFileNameControl,
                            out _,
                            out IntPtr currentShellView,
                            out _))
                    {
                        return new DialogObservation(false, string.Empty, IntPtr.Zero);
                    }

                    bool textRead = TryReadControlText(
                        currentFileNameControl,
                        out string currentText);
                    return new DialogObservation(
                        textRead,
                        currentText,
                        currentShellView);
                });

                if (observation.ShellView != IntPtr.Zero &&
                    observation.ShellView != shellView)
                {
                    watcher.MarkShellViewChanged();
                }

                // A consumed filename alone is not enough: validation errors may also clear
                // it. Require an observable change in the standard Shell folder view as well.
                if (observation.TextRead &&
                    !PathsEquivalent(observation.Text, navigationPath) &&
                    watcher.ShellViewChanged)
                {
                    return Result(
                        FileDialogNavigationStatus.Success,
                        snapshot.Handle,
                        string.Empty,
                        snapshot.Generation);
                }

                int remaining = (int)Math.Max(0, deadline - Environment.TickCount64);
                if (remaining == 0)
                {
                    return Result(
                        FileDialogNavigationStatus.NavigationNotObserved,
                        snapshot.Handle,
                        "目录命令已经发送，但未能确认文件对话框完成切换。面板已保留，请检查目标窗口。" );
                }

                int waitMilliseconds = Math.Min(
                    NavigationStateCheckIntervalMilliseconds,
                    remaining);
                await watcher.WaitForChangeAsync(waitMilliseconds);
            }
        }

        private CommandDispatchResult DispatchNavigationCommand(
            TargetSnapshot snapshot,
            IntPtr fileNameControl,
            IntPtr confirmButton,
            string navigationPath,
            NavigationEventWatcher watcher)
        {
            if (ValidateSnapshot(snapshot) != TargetValidationState.Valid)
            {
                return CommandDispatchResult.TargetExpired;
            }

            if (!TryResolveStandardDialog(
                    snapshot.Handle,
                    out fileNameControl,
                    out confirmButton,
                    out _,
                    out _))
            {
                return CommandDispatchResult.TargetExpired;
            }

            if (!TrySetControlText(fileNameControl, navigationPath) ||
                !TryReadControlText(fileNameControl, out string writtenText) ||
                !PathsEquivalent(writtenText, navigationPath))
            {
                return CommandDispatchResult.WriteFailed;
            }

            // Ignore the value-change event caused by our own WM_SETTEXT. Only changes
            // observed after this point can be evidence that the dialog consumed the path.
            watcher.Arm();

            IntPtr commandWParam = new IntPtr(IDOK); // MAKEWPARAM(IDOK, BN_CLICKED=0)
            IntPtr delivered = SendMessageTimeoutPointer(
                snapshot.Handle,
                WM_COMMAND,
                commandWParam,
                confirmButton,
                SendMessageFlags,
                SendMessageTimeoutMilliseconds,
                out _);

            return delivered != IntPtr.Zero
                ? CommandDispatchResult.Sent
                : CommandDispatchResult.CommandFailed;
        }

        private bool TryGetValidatedTarget(
            out TargetSnapshot snapshot,
            out IntPtr fileNameControl,
            out IntPtr confirmButton,
            out IntPtr shellView,
            out FileDialogNavigationResult failure)
        {
            lock (_targetLock)
            {
                snapshot = _target;
            }

            fileNameControl = IntPtr.Zero;
            confirmButton = IntPtr.Zero;
            shellView = IntPtr.Zero;

            if (snapshot.IsEmpty)
            {
                failure = Result(
                    FileDialogNavigationStatus.NoTarget,
                    IntPtr.Zero,
                    string.Empty);
                return false;
            }

            TargetValidationState targetState = ValidateSnapshot(snapshot);
            if (targetState == TargetValidationState.Expired)
            {
                ClearTargetIfGeneration(snapshot.Generation);
                failure = Result(
                    FileDialogNavigationStatus.TargetExpired,
                    snapshot.Handle,
                    "原来的打开/另存为窗口已经关闭或被替换。" );
                return false;
            }

            if (targetState == TargetValidationState.NotStandardDialog)
            {
                ClearTargetIfGeneration(snapshot.Generation);
                failure = Result(
                    FileDialogNavigationStatus.NotStandardDialog,
                    snapshot.Handle,
                    "当前目标不是受支持的 Windows 标准打开/另存为窗口。" );
                return false;
            }

            if (!TryResolveStandardDialog(
                    snapshot.Handle,
                    out fileNameControl,
                    out confirmButton,
                    out shellView,
                    out FileDialogNavigationStatus resolveFailure))
            {
                failure = Result(
                    resolveFailure,
                    snapshot.Handle,
                    resolveFailure == FileDialogNavigationStatus.ConfirmButtonNotFound
                        ? "没有找到目标文件对话框的标准确认按钮。"
                        : "没有找到目标文件对话框的标准文件名控件。" );
                return false;
            }

            failure = default;
            return true;
        }

        private TargetValidationState ValidateSnapshot(TargetSnapshot snapshot)
        {
            lock (_targetLock)
            {
                if (_target.Generation != snapshot.Generation ||
                    _target.Handle != snapshot.Handle)
                {
                    return TargetValidationState.Expired;
                }
            }

            if (snapshot.IsEmpty || !IsWindow(snapshot.Handle))
            {
                return TargetValidationState.Expired;
            }

            uint threadId = GetWindowThreadProcessId(snapshot.Handle, out uint processId);
            if (processId != snapshot.ProcessId || threadId != snapshot.ThreadId)
            {
                return TargetValidationState.Expired;
            }

            if (!IsWindowVisible(snapshot.Handle) ||
                !HasWindowClass(snapshot.Handle, "#32770"))
            {
                return TargetValidationState.NotStandardDialog;
            }

            return TargetValidationState.Valid;
        }

        private static bool TryResolveStandardDialog(
            IntPtr dialogHandle,
            out IntPtr fileNameControl,
            out IntPtr confirmButton,
            out IntPtr shellView,
            out FileDialogNavigationStatus failure)
        {
            fileNameControl = IntPtr.Zero;
            confirmButton = IntPtr.Zero;
            shellView = IntPtr.Zero;
            failure = FileDialogNavigationStatus.NotStandardDialog;

            if (dialogHandle == IntPtr.Zero ||
                !IsWindow(dialogHandle) ||
                !IsWindowVisible(dialogHandle) ||
                !HasWindowClass(dialogHandle, "#32770"))
            {
                return false;
            }

            uint dialogThread = GetWindowThreadProcessId(
                dialogHandle,
                out uint dialogProcess);
            if (dialogProcess == 0 || dialogThread == 0)
            {
                return false;
            }

            IntPtr modernFileNameHost = GetDlgItem(dialogHandle, IDC_FILENAME_COMBO);
            if (modernFileNameHost != IntPtr.Zero)
            {
                // Some Common Item Dialog builds expose the documented filename combo ID.
                fileNameControl = FindDescendantByClass(
                    modernFileNameHost,
                    "Edit");
            }

            if (fileNameControl == IntPtr.Zero)
            {
                // Current Windows 10/11 dialogs host the filename ComboBox inside the
                // DirectUI view. Its real Edit child uses ID 1001; limiting the search to
                // DUIViewWndClassName excludes the address bar and search box.
                IntPtr directUiView = FindDescendantByClass(
                    dialogHandle,
                    "DUIViewWndClassName");
                if (directUiView != IntPtr.Zero)
                {
                    IntPtr directUiEdit = FindDescendantByClassAndId(
                        directUiView,
                        "Edit",
                        IDC_MODERN_FILENAME_EDIT);
                    IntPtr comboParent = directUiEdit != IntPtr.Zero
                        ? GetParent(directUiEdit)
                        : IntPtr.Zero;
                    if (comboParent != IntPtr.Zero &&
                        HasWindowClass(comboParent, "ComboBox"))
                    {
                        fileNameControl = directUiEdit;
                    }
                }
            }

            if (fileNameControl == IntPtr.Zero)
            {
                IntPtr legacyEdit = GetDlgItem(dialogHandle, IDC_FILENAME_EDIT);
                if (legacyEdit != IntPtr.Zero && HasWindowClass(legacyEdit, "Edit"))
                {
                    fileNameControl = legacyEdit;
                }
            }

            if (fileNameControl == IntPtr.Zero ||
                !BelongsToDialog(
                    fileNameControl,
                    dialogHandle,
                    dialogProcess,
                    dialogThread))
            {
                failure = FileDialogNavigationStatus.FileNameControlNotFound;
                return false;
            }

            confirmButton = GetDlgItem(dialogHandle, IDOK);
            if (confirmButton == IntPtr.Zero ||
                !HasWindowClass(confirmButton, "Button") ||
                !BelongsToDialog(
                    confirmButton,
                    dialogHandle,
                    dialogProcess,
                    dialogThread))
            {
                failure = FileDialogNavigationStatus.ConfirmButtonNotFound;
                return false;
            }

            // The Shell view distinguishes a real Explorer-style Open/Save dialog from an
            // arbitrary #32770 dialog that happens to reuse common control IDs.
            shellView = FindDescendantByClass(dialogHandle, "SHELLDLL_DefView");
            if (shellView == IntPtr.Zero ||
                !BelongsToDialog(
                    shellView,
                    dialogHandle,
                    dialogProcess,
                    dialogThread))
            {
                failure = FileDialogNavigationStatus.NotStandardDialog;
                return false;
            }

            failure = FileDialogNavigationStatus.Success;
            return true;
        }

        private static bool BelongsToDialog(
            IntPtr controlHandle,
            IntPtr dialogHandle,
            uint dialogProcess,
            uint dialogThread)
        {
            if (!IsWindow(controlHandle) ||
                NormalizeRoot(controlHandle) != dialogHandle)
            {
                return false;
            }

            uint controlThread = GetWindowThreadProcessId(
                controlHandle,
                out uint controlProcess);
            return controlProcess == dialogProcess &&
                   controlThread == dialogThread;
        }

        private static IntPtr FindDescendantByClass(IntPtr parent, string expectedClass)
        {
            IntPtr child = GetWindow(parent, GW_CHILD);
            while (child != IntPtr.Zero)
            {
                if (HasWindowClass(child, expectedClass))
                {
                    return child;
                }

                IntPtr descendant = FindDescendantByClass(child, expectedClass);
                if (descendant != IntPtr.Zero)
                {
                    return descendant;
                }

                child = GetWindow(child, GW_HWNDNEXT);
            }

            return IntPtr.Zero;
        }

        private static IntPtr FindDescendantByClassAndId(
            IntPtr parent,
            string expectedClass,
            int expectedControlId)
        {
            IntPtr child = GetWindow(parent, GW_CHILD);
            while (child != IntPtr.Zero)
            {
                if (GetDlgCtrlID(child) == expectedControlId &&
                    HasWindowClass(child, expectedClass))
                {
                    return child;
                }

                IntPtr descendant = FindDescendantByClassAndId(
                    child,
                    expectedClass,
                    expectedControlId);
                if (descendant != IntPtr.Zero)
                {
                    return descendant;
                }

                child = GetWindow(child, GW_HWNDNEXT);
            }

            return IntPtr.Zero;
        }

        private static bool HasWindowClass(IntPtr hWnd, string expectedClass)
        {
            var className = new StringBuilder(128);
            return GetClassNameW(hWnd, className, className.Capacity) > 0 &&
                   string.Equals(
                       className.ToString(),
                       expectedClass,
                       StringComparison.Ordinal);
        }

        private static IntPtr NormalizeRoot(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            IntPtr root = GetAncestor(hWnd, GA_ROOT);
            return root != IntPtr.Zero ? root : hWnd;
        }

        private static bool TryResolveNavigationDirectory(
            string requestedPath,
            out string directoryPath)
        {
            directoryPath = string.Empty;
            if (string.IsNullOrWhiteSpace(requestedPath))
            {
                return false;
            }

            try
            {
                string candidate;
                if (Directory.Exists(requestedPath))
                {
                    candidate = requestedPath;
                }
                else if (File.Exists(requestedPath))
                {
                    candidate = Path.GetDirectoryName(requestedPath) ?? string.Empty;
                }
                else
                {
                    return false;
                }

                if (string.IsNullOrWhiteSpace(candidate) || !Directory.Exists(candidate))
                {
                    return false;
                }

                directoryPath = Path.GetFullPath(candidate);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TrySetControlText(IntPtr controlHandle, string text)
        {
            IntPtr delivered = SendMessageTimeoutText(
                controlHandle,
                WM_SETTEXT,
                IntPtr.Zero,
                text,
                SendMessageFlags,
                SendMessageTimeoutMilliseconds,
                out IntPtr messageResult);

            return delivered != IntPtr.Zero && messageResult != IntPtr.Zero;
        }

        private static bool TryReadControlText(IntPtr controlHandle, out string text)
        {
            text = string.Empty;

            IntPtr lengthDelivered = SendMessageTimeoutPointer(
                controlHandle,
                WM_GETTEXTLENGTH,
                IntPtr.Zero,
                IntPtr.Zero,
                SendMessageFlags,
                SendMessageTimeoutMilliseconds,
                out IntPtr lengthResult);

            if (lengthDelivered == IntPtr.Zero)
            {
                return false;
            }

            long length64 = lengthResult.ToInt64();
            if (length64 < 0 || length64 > 32767)
            {
                return false;
            }

            int capacity = (int)length64 + 1;
            var buffer = new StringBuilder(Math.Max(capacity, 1));
            IntPtr textDelivered = SendMessageTimeoutBuffer(
                controlHandle,
                WM_GETTEXT,
                new IntPtr(buffer.Capacity),
                buffer,
                SendMessageFlags,
                SendMessageTimeoutMilliseconds,
                out _);

            if (textDelivered == IntPtr.Zero)
            {
                return false;
            }

            text = buffer.ToString();
            return true;
        }

        private static bool PathsEquivalent(string left, string right)
        {
            return string.Equals(
                NormalizePathText(left),
                NormalizePathText(right),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizePathText(string value)
        {
            string normalized = value.Trim().Trim('"');
            try
            {
                normalized = Path.GetFullPath(normalized);
            }
            catch
            {
                // Preserve the control text if it is temporarily not a valid path.
            }

            string root = string.Empty;
            try
            {
                root = Path.GetPathRoot(normalized) ?? string.Empty;
            }
            catch
            {
            }

            if (!string.Equals(normalized, root, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
            }

            return normalized;
        }

        private void ClearTargetIfGeneration(int generation)
        {
            TargetLifetimeWatcher? watcher = null;
            lock (_targetLock)
            {
                if (_target.Generation == generation)
                {
                    watcher = _targetLifetimeWatcher;
                    _targetLifetimeWatcher = null;
                    _generation++;
                    _target = default;
                }
            }
            watcher?.Dispose();
        }

        private static FileDialogNavigationResult Result(
            FileDialogNavigationStatus status,
            IntPtr dialogHandle,
            string message,
            int targetGeneration = 0)
        {
            return new FileDialogNavigationResult(
                status,
                dialogHandle,
                message,
                targetGeneration);
        }

        private enum TargetValidationState
        {
            Valid,
            Expired,
            NotStandardDialog
        }

        private enum CommandDispatchResult
        {
            Sent,
            TargetExpired,
            WriteFailed,
            CommandFailed
        }

        private readonly struct DialogObservation
        {
            public DialogObservation(
                bool textRead,
                string text,
                IntPtr shellView)
            {
                TextRead = textRead;
                Text = text;
                ShellView = shellView;
            }

            public bool TextRead { get; }
            public string Text { get; }
            public IntPtr ShellView { get; }
        }

        private readonly struct TargetSnapshot
        {
            public TargetSnapshot(
                IntPtr handle,
                uint processId,
                uint threadId,
                int generation,
                FileDialogActivationSource source)
            {
                Handle = handle;
                ProcessId = processId;
                ThreadId = threadId;
                Generation = generation;
                Source = source;
            }

            public IntPtr Handle { get; }
            public uint ProcessId { get; }
            public uint ThreadId { get; }
            public int Generation { get; }
            public FileDialogActivationSource Source { get; }
            public bool IsEmpty => Handle == IntPtr.Zero;
        }

        private sealed class TargetLifetimeWatcher : IDisposable
        {
            private readonly IntPtr _dialogHandle;
            private readonly uint _processId;
            private readonly uint _threadId;
            private readonly int _generation;
            private readonly Action<int> _onDestroyed;
            private readonly WinEventProc _callback;
            private IntPtr _hook;
            private int _disposed;
            private int _notified;

            public TargetLifetimeWatcher(
                IntPtr dialogHandle,
                uint processId,
                uint threadId,
                int generation,
                Action<int> onDestroyed)
            {
                _dialogHandle = dialogHandle;
                _processId = processId;
                _threadId = threadId;
                _generation = generation;
                _onDestroyed = onDestroyed;
                _callback = OnWinEvent;
            }

            public void Start()
            {
                _hook = SetWinEventHook(
                    EVENT_OBJECT_DESTROY,
                    EVENT_OBJECT_DESTROY,
                    IntPtr.Zero,
                    _callback,
                    _processId,
                    _threadId,
                    WINEVENT_OUTOFCONTEXT);
            }

            private void OnWinEvent(
                IntPtr hook,
                uint eventType,
                IntPtr hWnd,
                int objectId,
                int childId,
                uint eventThread,
                uint eventTime)
            {
                if (Volatile.Read(ref _disposed) != 0 ||
                    hWnd != _dialogHandle ||
                    Interlocked.Exchange(ref _notified, 1) != 0)
                {
                    return;
                }

                ThreadPool.QueueUserWorkItem(
                    _ => _onDestroyed(_generation));
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {
                    return;
                }

                if (_hook != IntPtr.Zero)
                {
                    UnhookWinEvent(_hook);
                    _hook = IntPtr.Zero;
                }
            }
        }

        private sealed class NavigationEventWatcher : IDisposable
        {
            private readonly IntPtr _dialogHandle;
            private readonly IntPtr _initialShellView;
            private readonly uint _processId;
            private readonly uint _threadId;
            private readonly SemaphoreSlim _signal = new(0, 1);
            private readonly WinEventProc _callback;
            private IntPtr _hook;
            private int _armed;
            private int _disposed;
            private int _dialogDestroyed;
            private int _shellViewChanged;

            public NavigationEventWatcher(
                IntPtr dialogHandle,
                IntPtr initialShellView,
                uint processId,
                uint threadId)
            {
                _dialogHandle = dialogHandle;
                _initialShellView = initialShellView;
                _processId = processId;
                _threadId = threadId;
                _callback = OnWinEvent;
            }

            public bool DialogDestroyed =>
                Volatile.Read(ref _dialogDestroyed) != 0;

            public bool ShellViewChanged =>
                Volatile.Read(ref _shellViewChanged) != 0;

            public void MarkShellViewChanged()
            {
                Interlocked.Exchange(ref _shellViewChanged, 1);
                Signal();
            }

            public void Start()
            {
                _hook = SetWinEventHook(
                    EVENT_OBJECT_CREATE,
                    EVENT_OBJECT_VALUECHANGE,
                    IntPtr.Zero,
                    _callback,
                    _processId,
                    _threadId,
                    WINEVENT_OUTOFCONTEXT);
            }

            public void Arm()
            {
                Interlocked.Exchange(ref _armed, 1);
            }

            public async Task<bool> WaitForChangeAsync(int timeoutMilliseconds)
            {
                if (timeoutMilliseconds <= 0)
                {
                    return false;
                }

                if (_hook == IntPtr.Zero)
                {
                    await Task.Delay(timeoutMilliseconds);
                    return false;
                }

                return await _signal.WaitAsync(timeoutMilliseconds);
            }

            private void OnWinEvent(
                IntPtr hook,
                uint eventType,
                IntPtr hWnd,
                int objectId,
                int childId,
                uint eventThread,
                uint eventTime)
            {
                if (Volatile.Read(ref _disposed) != 0 ||
                    Volatile.Read(ref _armed) == 0 ||
                    hWnd == IntPtr.Zero)
                {
                    return;
                }

                if (eventType == EVENT_OBJECT_DESTROY && hWnd == _dialogHandle)
                {
                    Interlocked.Exchange(ref _dialogDestroyed, 1);
                    Signal();
                    return;
                }

                IntPtr root = NormalizeRoot(hWnd);
                if (root != _dialogHandle)
                {
                    return;
                }

                if ((eventType == EVENT_OBJECT_CREATE ||
                     eventType == EVENT_OBJECT_DESTROY ||
                     eventType == EVENT_OBJECT_REORDER) &&
                    (hWnd == _initialShellView ||
                     IsChild(_initialShellView, hWnd)))
                {
                    Interlocked.Exchange(ref _shellViewChanged, 1);
                }

                Signal();
            }

            private void Signal()
            {
                try
                {
                    if (_signal.CurrentCount == 0)
                    {
                        _signal.Release();
                    }
                }
                catch (SemaphoreFullException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {
                    return;
                }

                if (_hook != IntPtr.Zero)
                {
                    UnhookWinEvent(_hook);
                    _hook = IntPtr.Zero;
                }
            }
        }
    }
}
