using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using ExplorerHistoryTracker.Models;
using ExplorerHistoryTracker.Services;

namespace ExplorerHistoryTracker.ViewModels
{
    public class MainViewModel : ViewModelBase
    {
        private readonly ConfigManager _configManager;
        public ConfigManager ConfigManager => _configManager;

        private string _searchText = string.Empty;
        private bool _isSyncing;

        // Custom in-app dialog overlay properties (eliminating system MessageBox dependency)
        private bool _isDialogOpen;
        private string _dialogTitle = string.Empty;
        private string _dialogText = string.Empty;
        private bool _isDialogConfirmVisible;
        private Action? _onDialogConfirm;

        // Observable collections for UI binding
        public ObservableCollection<FolderHistoryItem> RecentItems { get; } = new();
        public ObservableCollection<FolderHistoryItem> RecentAppAndFileItems { get; } = new();
        public ObservableCollection<FolderHistoryItem> CurrentRecentItems { get; } = new();
        public ObservableCollection<FolderHistoryItem> PinnedItems { get; } = new();
        public ObservableCollection<FolderHistoryItem> StatsItems { get; } = new();

        // Commands
        public ICommand OpenFolderCommand { get; }
        public ICommand CopyPathCommand { get; }
        public ICommand PinFolderCommand { get; }
        public ICommand DeleteFolderCommand { get; }
        public ICommand ClearAllCommand { get; }
        public ICommand SwitchTabCommand { get; }
        public ICommand SwitchRecentFilterCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand ConfirmDialogCommand { get; }
        public ICommand CancelDialogCommand { get; }

        // Background monitors variables
        private bool _keepMonitoring = true;
        private readonly HashSet<int> _lastProcessIds = new();
        private readonly HashSet<string> _openFolderPaths = new(StringComparer.OrdinalIgnoreCase);
        private FileSystemWatcher? _recentWatcher;

        public MainViewModel()
        {
            _configManager = new ConfigManager();

            // Load items and update collections
            RefreshFilteredLists();

            // Trigger background sync to pull latest folders
            TriggerBackgroundSync();

            // Start monitors if enabled
            if (IsBackgroundMonitorEnabled)
            {
                StartBackgroundMonitors();
            }

            // Initialize Commands
            OpenFolderCommand = new RelayCommand(param => OpenFolder(param as FolderHistoryItem));
            CopyPathCommand = new RelayCommand(param => CopyPath(param as FolderHistoryItem));
            PinFolderCommand = new RelayCommand(param => PinFolder(param as FolderHistoryItem));
            DeleteFolderCommand = new RelayCommand(param => DeleteFolder(param as FolderHistoryItem));
            ClearAllCommand = new RelayCommand(ClearAllHistory);
            SwitchTabCommand = new RelayCommand(param => CurrentTab = param?.ToString() ?? "Recent");
            SwitchRecentFilterCommand = new RelayCommand(param =>
            {
                string targetFilter = param?.ToString() ?? "All";
                RecentFilter = RecentFilter == targetFilter ? "All" : targetFilter;
            });
            RefreshCommand = new RelayCommand(ExecuteRefresh);
            ConfirmDialogCommand = new RelayCommand(ExecuteConfirmDialog);
            CancelDialogCommand = new RelayCommand(() => IsDialogOpen = false);
        }

        #region Properties

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetProperty(ref _searchText, value))
                {
                    RefreshFilteredLists();
                }
            }
        }

        public string CurrentTab
        {
            get => _configManager.Config.LastActiveTab;
            set
            {
                if (_configManager.Config.LastActiveTab != value)
                {
                    _configManager.Config.LastActiveTab = value;
                    OnPropertyChanged();
                    _configManager.Save();
                }
            }
        }

        public string RecentFilter
        {
            get => _configManager.Config.LastActiveFilter;
            set
            {
                if (_configManager.Config.LastActiveFilter != value)
                {
                    _configManager.Config.LastActiveFilter = value;
                    OnPropertyChanged();
                    _configManager.Save();
                    RefreshFilteredLists();
                }
            }
        }

        public bool IsSyncing
        {
            get => _isSyncing;
            set
            {
                if (SetProperty(ref _isSyncing, value))
                {
                    OnPropertyChanged(nameof(IsSyncButtonEnabled));
                }
            }
        }

        public bool IsSyncButtonEnabled => !IsSyncing;

        public bool IsDialogOpen
        {
            get => _isDialogOpen;
            set => SetProperty(ref _isDialogOpen, value);
        }

        public string DialogTitle
        {
            get => _dialogTitle;
            set => SetProperty(ref _dialogTitle, value);
        }

        public string DialogText
        {
            get => _dialogText;
            set => SetProperty(ref _dialogText, value);
        }

        public bool IsDialogConfirmVisible
        {
            get => _isDialogConfirmVisible;
            set => SetProperty(ref _isDialogConfirmVisible, value);
        }

        // Persisted Configurations
        public double WindowWidth
        {
            get => _configManager.Config.WindowWidth;
            set
            {
                if (Math.Abs(_configManager.Config.WindowWidth - value) > 1.0)
                {
                    _configManager.Config.WindowWidth = value;
                    OnPropertyChanged();
                }
            }
        }

        public double WindowHeight
        {
            get => _configManager.Config.WindowHeight;
            set
            {
                if (Math.Abs(_configManager.Config.WindowHeight - value) > 1.0)
                {
                    _configManager.Config.WindowHeight = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _isWindowVisible = true;
        public bool IsWindowVisible
        {
            get => _isWindowVisible;
            set => SetProperty(ref _isWindowVisible, value);
        }

        public bool IsTopmost
        {
            get => _configManager.Config.IsTopmost;
            set
            {
                if (_configManager.Config.IsTopmost != value)
                {
                    _configManager.Config.IsTopmost = value;
                    OnPropertyChanged();
                    _configManager.Save();
                }
            }
        }

        public bool IsBackgroundMonitorEnabled
        {
            get => _configManager.Config.IsBackgroundMonitorEnabled;
            set
            {
                if (_configManager.Config.IsBackgroundMonitorEnabled != value)
                {
                    _configManager.Config.IsBackgroundMonitorEnabled = value;
                    OnPropertyChanged();
                    _configManager.Save();

                    if (value)
                    {
                        StartBackgroundMonitors();
                    }
                    else
                    {
                        StopBackgroundMonitors();
                    }
                }
            }
        }

        public bool IsSystemTheme
        {
            get => _configManager.Config.ThemeMode == "System";
            set
            {
                if (value)
                {
                    _configManager.Config.ThemeMode = "System";
                    _configManager.Save();
                    App.ApplyTheme("System");
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsLightTheme));
                    OnPropertyChanged(nameof(IsDarkTheme));
                }
            }
        }

        public bool IsLightTheme
        {
            get => _configManager.Config.ThemeMode == "Light";
            set
            {
                if (value)
                {
                    _configManager.Config.ThemeMode = "Light";
                    _configManager.Save();
                    App.ApplyTheme("Light");
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsSystemTheme));
                    OnPropertyChanged(nameof(IsDarkTheme));
                }
            }
        }

        public bool IsDarkTheme
        {
            get => _configManager.Config.ThemeMode == "Dark";
            set
            {
                if (value)
                {
                    _configManager.Config.ThemeMode = "Dark";
                    _configManager.Save();
                    App.ApplyTheme("Dark");
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsSystemTheme));
                    OnPropertyChanged(nameof(IsLightTheme));
                }
            }
        }

        public void SaveConfig()
        {
            _configManager.Save();
        }

        #endregion

        #region Command Methods

        private void ExecuteRefresh()
        {
            TriggerBackgroundSync();
        }

        private void OpenFolder(FolderHistoryItem? item)
        {
            if (item == null || string.IsNullOrEmpty(item.Path)) return;

            Task.Run(() =>
            {
                try
                {
                    if (Directory.Exists(item.Path) || File.Exists(item.Path))
                    {
                        Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
                        
                        Dispatcher.UIThread.Post(() =>
                        {
                            item.VisitCount++;
                            _configManager.Save();
                            RefreshFilteredLists();
                        });
                    }
                    else
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            ShowAlert("打开失败", $"路径不存在:\n{item.Path}");
                        });
                    }
                }
                catch (Exception ex)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        ShowAlert("打开失败", $"无法打开目标:\n{ex.Message}");
                    });
                }
            });
        }

        private void CopyPath(FolderHistoryItem? item)
        {
            if (item == null || string.IsNullOrEmpty(item.Path)) return;

            try
            {
                Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    try
                    {
                        var topLevel = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
                        if (topLevel?.MainWindow?.Clipboard != null)
                        {
                            await topLevel.MainWindow.Clipboard.SetTextAsync(item.Path);
                        }
                    }
                    catch (Exception ex)
                    {
                        ShowAlert("复制失败", $"无法复制到剪贴板:\n{ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                ShowAlert("复制失败", $"无法复制到剪贴板:\n{ex.Message}");
            }
        }

        private void PinFolder(FolderHistoryItem? item)
        {
            if (item == null) return;

            item.IsPinned = !item.IsPinned;
            _configManager.Save();
            RefreshFilteredLists();
        }

        private void DeleteFolder(FolderHistoryItem? item)
        {
            if (item == null) return;

            if (_configManager.Config.FolderHistory.Remove(item) ||
                _configManager.Config.AppAndFileHistory.Remove(item))
            {
                _configManager.Save();
                RefreshFilteredLists();
            }
        }

        private void ClearAllHistory()
        {
            ShowConfirm("确认清空", "确定要清空所有历史访问记录吗？\n(这会同时清除文件夹和软件历史)", () =>
            {
                _configManager.Config.FolderHistory.Clear();
                _configManager.Config.AppAndFileHistory.Clear();
                _configManager.Save();
                RefreshFilteredLists();
            });
        }

        private void ShowAlert(string title, string text)
        {
            DialogTitle = title;
            DialogText = text;
            IsDialogConfirmVisible = false;
            _onDialogConfirm = null;
            IsDialogOpen = true;
        }

        private void ShowConfirm(string title, string text, Action onConfirm)
        {
            DialogTitle = title;
            DialogText = text;
            IsDialogConfirmVisible = true;
            _onDialogConfirm = onConfirm;
            IsDialogOpen = true;
        }

        private void ExecuteConfirmDialog()
        {
            IsDialogOpen = false;
            _onDialogConfirm?.Invoke();
        }

        #endregion

        #region Dual-Channel Background Monitoring

        private void StartBackgroundMonitors()
        {
            StopBackgroundMonitors();

            StartRecentFolderWatcher();
            StartProcessMonitor();
            StartExplorerMonitor();
        }

        private void StopBackgroundMonitors()
        {
            _keepMonitoring = false;

            if (_recentWatcher != null)
            {
                _recentWatcher.Dispose();
                _recentWatcher = null;
            }
        }

        private void StartRecentFolderWatcher()
        {
            try
            {
                string recentPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Microsoft\Windows\Recent"
                );

                if (!Directory.Exists(recentPath)) return;

                _recentWatcher = new FileSystemWatcher(recentPath, "*.lnk")
                {
                    EnableRaisingEvents = true
                };

                _recentWatcher.Created += (s, e) => HandleRecentLnkChange(e.FullPath);
                _recentWatcher.Changed += (s, e) => HandleRecentLnkChange(e.FullPath);
            }
            catch
            {
                // Ignore watcher creation errors
            }
        }

        private void HandleRecentLnkChange(string lnkPath)
        {
            // Short delay to let OS finish writing shortcut metadata
            Thread.Sleep(100);

            try
            {
                string? target = ResolveShortcutTarget(lnkPath);
                if (string.IsNullOrEmpty(target)) return;

                Dispatcher.UIThread.Post(() =>
                {
                    bool isChanged = false;
                    if (Directory.Exists(target))
                    {
                        _configManager.AddOrUpdateFolder(target);
                        isChanged = true;
                    }
                    else if (File.Exists(target))
                    {
                        // Filter out shortcuts that target DLLs or system stuff
                        if (!target.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                            !target.EndsWith(".sys", StringComparison.OrdinalIgnoreCase) &&
                            !target.Contains("System32", StringComparison.OrdinalIgnoreCase))
                        {
                            _configManager.AddOrUpdateAppOrFile(target);
                            isChanged = true;
                        }
                    }

                    if (isChanged)
                    {
                        RefreshFilteredLists();
                    }
                });
            }
            catch
            {
                // Ignore parsing errors
            }
        }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

    private void TrimWorkingSet()
    {
        try
        {
            SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1));
        }
        catch { }
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(
        [In, MarshalAs(UnmanagedType.LPStruct)] Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        out IntPtr ppv);

    [DllImport("ole32.dll")]
    private static extern int CoInitialize(IntPtr pvReserved);
    
    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    private static readonly Guid CLSID_ShellLink = new Guid("00021401-0000-0000-C000-000000000046");
    private static readonly Guid IID_IShellLinkW = new Guid("000214F9-0000-0000-C000-000000000046");
    private static readonly Guid IID_IPersistFile = new Guid("0000010b-0000-0000-C000-000000000046");
    private static readonly Guid CLSID_ShellWindows = new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid IID_IShellWindows = new Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85");
    private static readonly Guid IID_IWebBrowser2 = new Guid("D30C1661-CDAF-11D0-8A3E-00C04FC9E26E");

    [StructLayout(LayoutKind.Sequential)]
    internal struct VARIANT
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public IntPtr value64;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryInterfaceDelegate(IntPtr pUnk, ref Guid riid, out IntPtr ppv);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ReleaseDelegate(IntPtr pUnk);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int IShellWindows_get_CountDelegate(IntPtr pShellWindows, out int count);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int IShellWindows_ItemDelegate(IntPtr pShellWindows, VARIANT index, out IntPtr ppdisp);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int IWebBrowser_get_LocationURLDelegate(IntPtr pWebBrowser, out IntPtr pbstrUrl);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int IPersistFile_LoadDelegate(IntPtr pPersistFile, IntPtr pszFileName, uint dwMode);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int IShellLinkW_GetPathDelegate(IntPtr pShellLink, IntPtr pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);

    internal static class ComVHelper
    {
        public static int CallQueryInterface(IntPtr pUnk, ref Guid riid, out IntPtr ppv)
        {
            unsafe
            {
                var vtable = *(IntPtr**)pUnk;
                var fn = vtable[0];
                var del = (QueryInterfaceDelegate)Marshal.GetDelegateForFunctionPointer(fn, typeof(QueryInterfaceDelegate));
                return del(pUnk, ref riid, out ppv);
            }
        }

        public static int CallRelease(IntPtr pUnk)
        {
            if (pUnk == IntPtr.Zero) return 0;
            unsafe
            {
                var vtable = *(IntPtr**)pUnk;
                var fn = vtable[2];
                var del = (ReleaseDelegate)Marshal.GetDelegateForFunctionPointer(fn, typeof(ReleaseDelegate));
                return del(pUnk);
            }
        }

        public static int CallIShellWindows_get_Count(IntPtr pShellWindows, out int count)
        {
            unsafe
            {
                var vtable = *(IntPtr**)pShellWindows;
                var fn = vtable[7];
                var del = (IShellWindows_get_CountDelegate)Marshal.GetDelegateForFunctionPointer(fn, typeof(IShellWindows_get_CountDelegate));
                return del(pShellWindows, out count);
            }
        }

        public static int CallIShellWindows_Item(IntPtr pShellWindows, VARIANT index, out IntPtr ppdisp)
        {
            unsafe
            {
                var vtable = *(IntPtr**)pShellWindows;
                var fn = vtable[8];
                var del = (IShellWindows_ItemDelegate)Marshal.GetDelegateForFunctionPointer(fn, typeof(IShellWindows_ItemDelegate));
                return del(pShellWindows, index, out ppdisp);
            }
        }

        public static int CallIWebBrowser_get_LocationURL(IntPtr pWebBrowser, out IntPtr pbstrUrl)
        {
            unsafe
            {
                var vtable = *(IntPtr**)pWebBrowser;
                var fn = vtable[30];
                var del = (IWebBrowser_get_LocationURLDelegate)Marshal.GetDelegateForFunctionPointer(fn, typeof(IWebBrowser_get_LocationURLDelegate));
                return del(pWebBrowser, out pbstrUrl);
            }
        }

        public static int CallIPersistFile_Load(IntPtr pPersistFile, IntPtr pszFileName, uint dwMode)
        {
            unsafe
            {
                var vtable = *(IntPtr**)pPersistFile;
                var fn = vtable[5];
                var del = (IPersistFile_LoadDelegate)Marshal.GetDelegateForFunctionPointer(fn, typeof(IPersistFile_LoadDelegate));
                return del(pPersistFile, pszFileName, dwMode);
            }
        }

        public static int CallIShellLinkW_GetPath(IntPtr pShellLink, IntPtr pszFile, int cchMaxPath, IntPtr pfd, uint fFlags)
        {
            unsafe
            {
                var vtable = *(IntPtr**)pShellLink;
                var fn = vtable[3];
                var del = (IShellLinkW_GetPathDelegate)Marshal.GetDelegateForFunctionPointer(fn, typeof(IShellLinkW_GetPathDelegate));
                return del(pShellLink, pszFile, cchMaxPath, pfd, fFlags);
            }
        }
    }

    private string? ResolveShortcutTarget(string shortcutPath)
    {
        IntPtr pShellLink = IntPtr.Zero;
        IntPtr pPersistFile = IntPtr.Zero;
        IntPtr pFileName = IntPtr.Zero;
        IntPtr pszFile = IntPtr.Zero;
        try
        {
            int hr = CoCreateInstance(CLSID_ShellLink, IntPtr.Zero, 1 /* CLSCTX_INPROC_SERVER */, IID_IShellLinkW, out pShellLink);
            if (hr < 0) return null;

            var riidIPersistFile = IID_IPersistFile;
            hr = ComVHelper.CallQueryInterface(pShellLink, ref riidIPersistFile, out pPersistFile);
            if (hr < 0) return null;

            pFileName = Marshal.StringToCoTaskMemUni(shortcutPath);
            hr = ComVHelper.CallIPersistFile_Load(pPersistFile, pFileName, 0);
            if (hr < 0) return null;

            const int MAX_PATH = 512;
            pszFile = Marshal.AllocCoTaskMem(MAX_PATH * sizeof(char));
            hr = ComVHelper.CallIShellLinkW_GetPath(pShellLink, pszFile, MAX_PATH, IntPtr.Zero, 0);
            if (hr < 0) return null;

            return Marshal.PtrToStringUni(pszFile);
        }
        catch (Exception ex)
        {
            File.AppendAllText("trace_log.txt", $"{DateTime.Now}: ResolveShortcutTarget Error for {shortcutPath}: {ex}\n");
            return null;
        }
        finally
        {
            if (pFileName != IntPtr.Zero) Marshal.FreeCoTaskMem(pFileName);
            if (pszFile != IntPtr.Zero) Marshal.FreeCoTaskMem(pszFile);
            ComVHelper.CallRelease(pPersistFile);
            ComVHelper.CallRelease(pShellLink);
        }
    }

        private void StartProcessMonitor()
        {
            _keepMonitoring = true;
            Task.Run(() =>
            {
                try
                {
                    foreach (var proc in Process.GetProcesses())
                    {
                        _lastProcessIds.Add(proc.Id);
                    }
                }
                catch { }

                int iterations = 0;
                while (_keepMonitoring)
                {
                    try
                    {
                        var currentProcesses = Process.GetProcesses();
                        var currentIds = new HashSet<int>();

                        foreach (var proc in currentProcesses)
                        {
                            currentIds.Add(proc.Id);
                            if (!_lastProcessIds.Contains(proc.Id))
                            {
                                try
                                {
                                    string? path = proc.MainModule?.FileName;
                                    if (!string.IsNullOrEmpty(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                                    {
                                        if (!IsSystemProcess(path))
                                        {
                                            Dispatcher.UIThread.Post(() =>
                                            {
                                                _configManager.AddOrUpdateAppOrFile(path);
                                                RefreshFilteredLists();
                                            });
                                        }
                                    }
                                }
                                catch
                                {
                                    // Ignore access denied for elevated processes
                                }
                            }
                        }

                        _lastProcessIds.Clear();
                        foreach (var id in currentIds)
                        {
                            _lastProcessIds.Add(id);
                        }
                    }
                    catch
                    {
                        // Ignore process querying errors
                    }

                    if (!IsWindowVisible)
                    {
                        iterations++;
                        if (iterations >= 30) // every 60 seconds (loop runs every 2s)
                        {
                            iterations = 0;
                            try
                            {
                                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                                GC.WaitForPendingFinalizers();
                                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                                TrimWorkingSet();
                            }
                            catch { }
                        }
                    }
                    else
                    {
                        iterations = 0; // reset counter when window becomes visible
                    }

                    Thread.Sleep(2000);
                }
            });
        }

        private void StartExplorerMonitor()
        {
            _keepMonitoring = true;
            Task.Run(() =>
            {
                CoInitialize(IntPtr.Zero);
                try
                {
                    while (_keepMonitoring)
                    {
                        IntPtr pShellWindows = IntPtr.Zero;
                        try
                        {
                            int hr = CoCreateInstance(CLSID_ShellWindows, IntPtr.Zero, 4 /* CLSCTX_LOCAL_SERVER */, IID_IShellWindows, out pShellWindows);
                            if (hr >= 0 && pShellWindows != IntPtr.Zero)
                            {
                                int count = 0;
                                hr = ComVHelper.CallIShellWindows_get_Count(pShellWindows, out count);
                                if (hr >= 0)
                                {
                                    var currentPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                                    for (int i = 0; i < count; i++)
                                    {
                                        VARIANT index = new VARIANT { vt = 3, value64 = (IntPtr)i };
                                        IntPtr pDisp = IntPtr.Zero;
                                        hr = ComVHelper.CallIShellWindows_Item(pShellWindows, index, out pDisp);
                                        if (hr >= 0 && pDisp != IntPtr.Zero)
                                        {
                                            IntPtr pWebBrowser = IntPtr.Zero;
                                            var riidWebBrowser = IID_IWebBrowser2;
                                            hr = ComVHelper.CallQueryInterface(pDisp, ref riidWebBrowser, out pWebBrowser);
                                            if (hr >= 0 && pWebBrowser != IntPtr.Zero)
                                            {
                                                IntPtr pbstrUrl = IntPtr.Zero;
                                                hr = ComVHelper.CallIWebBrowser_get_LocationURL(pWebBrowser, out pbstrUrl);
                                                if (hr >= 0 && pbstrUrl != IntPtr.Zero)
                                                {
                                                    string? url = Marshal.PtrToStringBSTR(pbstrUrl);
                                                    Marshal.FreeBSTR(pbstrUrl);

                                                    if (!string.IsNullOrEmpty(url) && url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                                                    {
                                                        try
                                                        {
                                                            var uri = new Uri(url);
                                                            string path = uri.LocalPath;
                                                            if (Directory.Exists(path))
                                                            {
                                                                // Normalize trailing backslashes to match config manager format
                                                                path = path.Trim().TrimEnd('\\');
                                                                if (path.EndsWith(":")) path += "\\";

                                                                currentPaths.Add(path);
                                                            }
                                                        }
                                                        catch {}
                                                    }
                                                }
                                                ComVHelper.CallRelease(pWebBrowser);
                                            }
                                            ComVHelper.CallRelease(pDisp);
                                        }
                                    }

                                    bool isChanged = false;

                                    // Detect folders transitioning from 0 to 1 open windows
                                    foreach (var path in currentPaths)
                                    {
                                        if (!_openFolderPaths.Contains(path))
                                        {
                                            // Went from 0 open windows to at least 1 open window!
                                            Dispatcher.UIThread.Post(() =>
                                            {
                                                _configManager.AddOrUpdateFolder(path);
                                            });
                                            isChanged = true;
                                        }
                                    }

                                    // Reset active paths tracking to match current state
                                    _openFolderPaths.Clear();
                                    foreach (var path in currentPaths)
                                    {
                                        _openFolderPaths.Add(path);
                                    }

                                    if (isChanged)
                                    {
                                        Dispatcher.UIThread.Post(() =>
                                        {
                                            RefreshFilteredLists();
                                        });
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            File.AppendAllText("trace_log.txt", $"{DateTime.Now}: ExplorerMonitor Error: {ex}\n");
                        }
                        finally
                        {
                            if (pShellWindows != IntPtr.Zero)
                            {
                                ComVHelper.CallRelease(pShellWindows);
                            }
                        }

                        Thread.Sleep(2000);
                    }
                }
                finally
                {
                    CoUninitialize();
                }
            });
        }

        private static bool IsSystemProcess(string path)
        {
            string name = Path.GetFileName(path);
            return name.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("conhost.exe", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("taskhostw.exe", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("wsl.exe", StringComparison.OrdinalIgnoreCase) ||
                   path.Contains(@"\Windows\System32", StringComparison.OrdinalIgnoreCase) ||
                   path.Contains(@"\Windows\SysWOW64", StringComparison.OrdinalIgnoreCase) ||
                   path.Contains("ExplorerHistoryTracker", StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region Asynchronous System Recent Items Sync

        private void TriggerBackgroundSync()
        {
            if (IsSyncing) return;
            IsSyncing = true;

            Thread syncThread = new Thread(SyncRecentFoldersInternal)
            {
                IsBackground = true,
                Name = "RecentItemsSyncWorker"
            };
            syncThread.SetApartmentState(ApartmentState.STA);
            syncThread.Start();
        }

    private void SyncRecentFoldersInternal()
    {
        CoInitialize(IntPtr.Zero);
        try
        {
            string recentPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Recent");
            if (!Directory.Exists(recentPath)) return;

            var systemRecentPaths = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            var lnkFiles = Directory.GetFiles(recentPath, "*.lnk");

            foreach (var lnk in lnkFiles)
            {
                try
                {
                    string? targetPath = ResolveShortcutTarget(lnk);
                    if (!string.IsNullOrEmpty(targetPath) && Directory.Exists(targetPath))
                    {
                        targetPath = targetPath.TrimEnd('\\');
                        if (targetPath.EndsWith(":")) targetPath += "\\";

                        DateTime lastVisited = File.GetLastWriteTime(lnk);
                        if (!systemRecentPaths.TryGetValue(targetPath, out DateTime existingTime) || lastVisited > existingTime)
                        {
                            systemRecentPaths[targetPath] = lastVisited;
                        }
                    }
                }
                catch
                {
                    // Ignore unparseable links
                }
            }

                Dispatcher.UIThread.Post(() =>
                {
                    bool isChanged = false;
                    foreach (var kvp in systemRecentPaths)
                    {
                        string path = kvp.Key;
                        DateTime systemTime = kvp.Value;

                        var existingItem = _configManager.Config.FolderHistory.FirstOrDefault(i => i.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
                        if (existingItem != null)
                        {
                            if (systemTime > existingItem.LastVisited)
                            {
                                existingItem.LastVisited = systemTime;
                                isChanged = true;
                            }
                        }
                        else
                        {
                            string name = Path.GetFileName(path);
                            if (string.IsNullOrEmpty(name)) name = path;

                            _configManager.Config.FolderHistory.Add(new FolderHistoryItem
                            {
                                Path = path,
                                Name = name,
                                LastVisited = systemTime,
                                VisitCount = 1,
                                IsPinned = false
                            });
                            isChanged = true;
                        }
                    }

                    // Limit count of unpinned items
                    const int MaxHistoryItems = 300;
                    var pinned = _configManager.Config.FolderHistory.Where(i => i.IsPinned).ToList();
                    var unpinned = _configManager.Config.FolderHistory.Where(i => !i.IsPinned)
                                                   .OrderByDescending(i => i.LastVisited)
                                                   .ToList();

                    if (unpinned.Count > MaxHistoryItems)
                    {
                        var keep = unpinned.Take(MaxHistoryItems).ToList();
                        _configManager.Config.FolderHistory.Clear();
                        _configManager.Config.FolderHistory.AddRange(pinned);
                        _configManager.Config.FolderHistory.AddRange(keep);
                        isChanged = true;
                    }

                    if (isChanged)
                    {
                        _configManager.Save();
                    }

                    RefreshFilteredLists();
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    ShowAlert("同步失败", $"同步系统历史记录失败:\n{ex.Message}");
                });
            }
            finally
            {
                CoUninitialize();
                Dispatcher.UIThread.Post(() =>
                {
                    IsSyncing = false;
                });
            }
        }

        #endregion

        #region Helper Methods

        private void RefreshFilteredLists()
        {
            // 1. Folders History filtering
            var queryFolders = _configManager.Config.FolderHistory.AsEnumerable();

            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                queryFolders = queryFolders.Where(i => i.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                                                       i.Path.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
            }

            // Sync Recent Folders
            var recentFolders = queryFolders.OrderByDescending(i => i.LastVisited).ToList();
            UpdateCollection(RecentItems, recentFolders);

            // Sync Pinned Folders
            var pinnedFolders = queryFolders.Where(i => i.IsPinned).OrderByDescending(i => i.LastVisited).ToList();
            UpdateCollection(PinnedItems, pinnedFolders);

            // Sync Stats Folders
            var statsFolders = queryFolders.OrderByDescending(i => i.VisitCount).Take(50).ToList();
            UpdateCollection(StatsItems, statsFolders);

            // 2. Apps & Files History filtering
            var queryApps = _configManager.Config.AppAndFileHistory.AsEnumerable();

            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                queryApps = queryApps.Where(i => i.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                                                 i.Path.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
            }

            var recentApps = queryApps.OrderByDescending(i => i.LastVisited).ToList();
            UpdateCollection(RecentAppAndFileItems, recentApps);

            var selectedItems = RecentFilter switch
            {
                "Folders" => recentFolders,
                "Applications" => recentApps.Where(IsApplication).ToList(),
                "Files" => recentApps.Where(item => !IsApplication(item)).ToList(),
                _ => recentFolders.Concat(recentApps)
                                  .OrderByDescending(item => item.LastVisited)
                                  .ToList()
            };
            UpdateCollection(CurrentRecentItems, selectedItems);
        }

        private static bool IsApplication(FolderHistoryItem item) =>
            Path.GetExtension(item.Path).Equals(".exe", StringComparison.OrdinalIgnoreCase);

        private static void UpdateCollection<T>(ObservableCollection<T> target, List<T> source) where T : class
        {
            // 1. Remove items from target that are not in source
            for (int i = target.Count - 1; i >= 0; i--)
            {
                if (!source.Contains(target[i]))
                {
                    target.RemoveAt(i);
                }
            }

            // 2. Add or move items to match source
            for (int i = 0; i < source.Count; i++)
            {
                var item = source[i];
                int targetIdx = target.IndexOf(item);
                if (targetIdx == -1)
                {
                    if (i < target.Count)
                        target.Insert(i, item);
                    else
                        target.Add(item);
                }
                else if (targetIdx != i)
                {
                    target.Move(targetIdx, i);
                }
            }
        }

        #endregion
    }
}
