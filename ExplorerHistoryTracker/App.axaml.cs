using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ExplorerHistoryTracker.Services;
using ExplorerHistoryTracker.ViewModels;

namespace ExplorerHistoryTracker
{
    public partial class App : Application
    {
        private static MainViewModel? _sharedViewModel;
        public static MainViewModel SharedViewModel => _sharedViewModel ??= new MainViewModel();

        private static MainWindow? _mainWindow;
        private static FloatBallWindow? _floatBallWindow;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private void PreinstallPets(ConfigManager configManager)
        {
            try
            {
                string appDataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Tuna"
                );
                string petsDir = Path.Combine(appDataDir, "pets");
                Directory.CreateDirectory(petsDir);

                bool isFirstBuiltInInstall = !configManager.Config.BuiltInPetsInitialized;
                const int currentDefaultPetAssetVersion = 3;
                const int currentBuiltInPetCatalogVersion = 1;
                bool refreshDefaultPet =
                    configManager.Config.BuiltInPetAssetVersion < currentDefaultPetAssetVersion;
                bool refreshBuiltInCatalog =
                    configManager.Config.BuiltInPetCatalogVersion < currentBuiltInPetCatalogVersion;

                // 资源目录名可以与安装后的宠物包目录名不同。
                (string PetId, string AssetFolder)[] allBuiltInPets =
                {
                    ("douya-chick", "douya-chick.codex-pet"),
                    ("doro", "doro"),
                    ("endminguga", "endminguga"),
                    ("ikkun", "ikkun.codex-pet")
                };

                // 首次安装或内置库升级时写入全部宠物；以后只恢复不可删除的小黄鸡。
                (string PetId, string AssetFolder)[] builtInPets =
                    isFirstBuiltInInstall || refreshBuiltInCatalog
                        ? allBuiltInPets
                        : new[] { allBuiltInPets[0] };

                bool builtInCopySucceeded = true;
                bool defaultPetCopySucceeded = true;

                foreach ((string petName, string assetFolder) in builtInPets)
                {
                    string targetPetDir = Path.Combine(petsDir, petName + ".codex-pet");
                    Directory.CreateDirectory(targetPetDir);

                    foreach (string assetName in new[] { "pet.json", "spritesheet.webp" })
                    {
                        string targetFile = Path.Combine(targetPetDir, assetName);
                        bool shouldRefresh =
                            refreshBuiltInCatalog ||
                            (petName == "douya-chick" && refreshDefaultPet);
                        if (File.Exists(targetFile) &&
                            !shouldRefresh)
                        {
                            continue;
                        }

                        try
                        {
                            var uri = new Uri($"avares://Tuna/Assets/Pets/{assetFolder}/{assetName}");
                            using var srcStream = Avalonia.Platform.AssetLoader.Open(uri);
                            using var destStream = File.Create(targetFile);
                            srcStream.CopyTo(destStream);
                        }
                        catch
                        {
                            builtInCopySucceeded = false;
                            if (petName == "douya-chick")
                                defaultPetCopySucceeded = false;
                        }
                    }
                }

                if (isFirstBuiltInInstall && builtInCopySucceeded)
                {
                    configManager.Config.BuiltInPetsInitialized = true;
                    configManager.Save();
                }

                if (refreshDefaultPet && defaultPetCopySucceeded)
                {
                    configManager.Config.BuiltInPetAssetVersion = currentDefaultPetAssetVersion;
                    configManager.Save();
                }

                if (refreshBuiltInCatalog && builtInCopySucceeded)
                {
                    // 清理已从新版内置库退役的旧宠物包，不影响其他自定义宠物。
                    string[] retiredBuiltInPets = { "tuna", "xiaoda", "yuno" };
                    foreach (string retiredPetId in retiredBuiltInPets)
                    {
                        string retiredPetDir = Path.Combine(petsDir, retiredPetId + ".codex-pet");
                        if (Directory.Exists(retiredPetDir))
                            Directory.Delete(retiredPetDir, true);
                    }

                    if (Array.IndexOf(retiredBuiltInPets, configManager.Config.SelectedPetId) >= 0)
                        configManager.Config.SelectedPetId = "douya-chick";

                    configManager.Config.BuiltInPetCatalogVersion = currentBuiltInPetCatalogVersion;
                    configManager.Save();
                }

                // One-time upgrade: replace the retired Tuna default with Douya.
                // Future launches preserve whatever pet the user chooses.
                if (!configManager.Config.DefaultPetMigratedToDouya)
                {
                    configManager.Config.SelectedPetId = "douya-chick";
                    configManager.Config.DefaultPetMigratedToDouya = true;
                    configManager.Save();
                }
            }
            catch { }
        }

        public override void OnFrameworkInitializationCompleted()
        {
            // Capture before initialization work can change the foreground window. Only a
            // verified standard Open/Save dialog is retained by MainWindow.
            IntPtr startupTarget = GetForegroundWindow();
            PreinstallPets(new ConfigManager());

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var vm = SharedViewModel;
                ApplyTheme(vm.ConfigManager.Config.ThemeMode);

                _mainWindow = new MainWindow
                {
                    DataContext = vm
                };
                _mainWindow.CaptureDialogTarget(
                    startupTarget,
                    FileDialogActivationSource.Startup);
                _mainWindow.Closed += (s, ev) => _mainWindow = null;

                // Monitor visibility property change to show/hide float ball
                _mainWindow.PropertyChanged += (s, ev) =>
                {
                    if (ev.Property == Window.IsVisibleProperty)
                    {
                        UpdateFloatBall();
                    }
                };

                desktop.MainWindow = _mainWindow;

                vm.PropertyChanged += (s, ev) =>
                {
                    if (ev.PropertyName == nameof(MainViewModel.IsFloatBallEnabled) ||
                        ev.PropertyName == nameof(MainViewModel.IsBackgroundMonitorEnabled))
                    {
                        UpdateFloatBall();
                    }
                };

                StartWakeupListener();
                UpdateFloatBall();
            }

            base.OnFrameworkInitializationCompleted();
        }

        public static void ApplyTheme(string themeMode)
        {
            if (Current == null) return;
            
            if (themeMode == "Dark")
            {
                Current.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
            }
            else if (themeMode == "Light")
            {
                Current.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
            }
            else
            {
                Current.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Default;
            }
        }

        public static void ShowMainWindow(
            IntPtr activationTarget = default,
            FileDialogActivationSource activationSource = FileDialogActivationSource.WakeupEvent)
        {
            // Capture the HWND value on the calling thread, but validate/install the target
            // session only after activation de-duplication. A rejected duplicate wakeup must
            // never replace or clear the session that owns the visible panel.
            if (activationTarget == IntPtr.Zero)
            {
                activationTarget = GetForegroundWindow();
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (_mainWindow == null) return;

                if (_mainWindow.IsWakingUp || _mainWindow.WasExternallyShownRecently())
                {
                    return;
                }

                _mainWindow.CaptureDialogTarget(activationTarget, activationSource);
                _mainWindow.BeginInternalWakeupShow();
                try
                {
                    if (_mainWindow.WindowState != WindowState.Normal)
                        _mainWindow.WindowState = WindowState.Normal;

                    _mainWindow.Show();

                    bool originalTopmost = false;
                    if (_mainWindow.DataContext is MainViewModel vmOrig)
                        originalTopmost = vmOrig.IsTopmost;

                    _mainWindow.Topmost = true;
                    _mainWindow.Activate();
                    _mainWindow.ForceActivate();

                    if (_mainWindow.DataContext is MainViewModel vmRestore)
                        vmRestore.IsTopmost = originalTopmost;
                    _mainWindow.Topmost = originalTopmost;
                }
                finally
                {
                    _mainWindow.EndInternalWakeupShow();
                }
            });
        }

        public static void HideMainWindow()
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_mainWindow == null) return;

                _mainWindow.Hide();
                _mainWindow.ClearDialogTarget();

                if (_mainWindow.DataContext is MainViewModel vm)
                {
                    vm.IsWindowVisible = false;
                }

                // Force native hide to be absolutely sure it doesn't leak into Alt+Tab
                try
                {
                    IntPtr hwnd = _mainWindow.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                    if (hwnd != IntPtr.Zero)
                        MainWindow.NativeShowWindow(hwnd, MainWindow.SW_HIDE);
                }
                catch {}
            });
        }

        public static void ToggleMainWindow(
            IntPtr activationTarget = default,
            FileDialogActivationSource activationSource = FileDialogActivationSource.FloatBall)
        {
            if (activationTarget == IntPtr.Zero)
            {
                activationTarget = GetForegroundWindow();
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (_mainWindow == null) return;

                if (_mainWindow.IsVisible)
                {
                    HideMainWindow();
                }
                else
                {
                    ShowMainWindow(activationTarget, activationSource);
                }
            });
        }

        public static void UpdateFloatBall()
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    var vm = SharedViewModel;

                    bool isMainWindowVisible = _mainWindow != null && _mainWindow.IsVisible;
                    bool shouldShow = vm.IsFloatBallEnabled && vm.IsBackgroundMonitorEnabled && !isMainWindowVisible;

                    if (shouldShow)
                    {
                        if (_floatBallWindow == null)
                        {
                            _floatBallWindow = new FloatBallWindow();
                            _floatBallWindow.Closed += (s, ev) => _floatBallWindow = null;
                        }
                        if (!_floatBallWindow.IsVisible)
                        {
                            _floatBallWindow.Show();
                        }
                    }
                    else
                    {
                        if (_floatBallWindow != null && _floatBallWindow.IsVisible)
                        {
                            _floatBallWindow.Hide();
                        }
                    }
                }
            });
        }

        public static void Shutdown()
        {
            if (_floatBallWindow != null)
            {
                try { _floatBallWindow.Close(); } catch { }
                _floatBallWindow = null;
            }
            if (_mainWindow != null)
            {
                try { _mainWindow.Close(); } catch { }
                _mainWindow = null;
            }
            Environment.Exit(0);
        }

        private void StartWakeupListener()
        {
            EventWaitHandle wakeupEvent;
            try
            {
                wakeupEvent = new EventWaitHandle(
                    false,
                    EventResetMode.AutoReset,
                    Program.WakeupEventName);
            }
            catch
            {
                try
                {
                    wakeupEvent = EventWaitHandle.OpenExisting(Program.WakeupEventName);
                }
                catch
                {
                    return;
                }
            }

            ThreadPool.RegisterWaitForSingleObject(
                wakeupEvent,
                (_, _) =>
                {
                    IntPtr activationTarget = Program.ReadWakeupTarget();
                    if (activationTarget == IntPtr.Zero)
                    {
                        activationTarget = GetForegroundWindow();
                    }
                    ShowMainWindow(
                        activationTarget,
                        FileDialogActivationSource.WakeupEvent);
                },
                null,
                Timeout.InfiniteTimeSpan,
                false);
        }
    }
}
