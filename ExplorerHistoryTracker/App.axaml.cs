using System;
using System.IO;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ExplorerHistoryTracker.ViewModels;

namespace ExplorerHistoryTracker
{
    public partial class App : Application
    {
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var vm = new MainViewModel();
                ApplyTheme(vm.ConfigManager.Config.ThemeMode);

                desktop.MainWindow = new MainWindow
                {
                    DataContext = vm
                };
 
                StartWakeupListener();
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

        /// <summary>
        /// Uses a named kernel EventWaitHandle + ThreadPool wait for ultra-low-latency
        /// wakeup signaling. Replaces the old Named Pipe approach which had:
        ///   - 500ms Connect timeout
        ///   - Server recreation gap after each message
        ///   - StreamReader/Writer overhead
        /// EventWaitHandle.Set() is a single kernel call (&lt;1ms).
        /// </summary>
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

            // ThreadPool.RegisterWaitForSingleObject is more efficient than a dedicated thread:
            // it uses I/O completion ports internally and doesn't consume a thread while waiting.
            ThreadPool.RegisterWaitForSingleObject(
                wakeupEvent,
                (_, _) =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                        {
                            var window = desktop.MainWindow as MainWindow;
                            
                            try
                            {
                                if (window == null)
                                {
                                    window = new MainWindow { DataContext = new MainViewModel() };
                                    desktop.MainWindow = window;
                                }
                                // Quicker can show the existing HWND and also cause a second-instance
                                // wakeup signal for the same user action. The native show already fixed
                                // the target position, so do not activate and reposition a second time.
                                if (window.IsWakingUp || window.WasExternallyShownRecently())
                                {
                                    return;
                                }

                                window.BeginInternalWakeupShow();
                                try
                                {
                                    if (window.WindowState != WindowState.Normal)
                                        window.WindowState = WindowState.Normal;

                                    window.Show();

                                    // Cache the user's original topmost setting before we mess with it
                                    bool originalTopmost = false;
                                    if (window.DataContext is MainViewModel vmOrig)
                                        originalTopmost = vmOrig.IsTopmost;

                                    // Force Topmost temporarily to bypass Windows foreground lock
                                    window.Topmost = true;
                                    window.Activate();

                                    // Restore original Topmost state to both ViewModel and Window
                                    if (window.DataContext is MainViewModel vmRestore)
                                        vmRestore.IsTopmost = originalTopmost;
                                    window.Topmost = originalTopmost;
                                }
                                finally
                                {
                                    window.EndInternalWakeupShow();
                                }
                            }
                            catch
                            {
                                // If the window was previously Closed (not Hidden), calling Show() throws an exception.
                                // In this case, we recreate the window from scratch.
                                window = new MainWindow { DataContext = new MainViewModel() };
                                desktop.MainWindow = window;
                                
                                window.BeginInternalWakeupShow();
                                try
                                {
                                    window.Show();

                                    bool originalTopmost = false;
                                    if (window.DataContext is MainViewModel vmOrig) originalTopmost = vmOrig.IsTopmost;

                                    window.Topmost = true;
                                    window.Activate();

                                    if (window.DataContext is MainViewModel vmRestore) vmRestore.IsTopmost = originalTopmost;
                                    window.Topmost = originalTopmost;
                                }
                                finally
                                {
                                    window.EndInternalWakeupShow();
                                }
                            }
                        }
                    });
                },
                null,
                Timeout.InfiniteTimeSpan,
                false);  // false = keep listening after each signal (auto-reset event)
        }
    }
}
