using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls;

namespace ExplorerHistoryTracker
{
    class Program
    {
        private static Mutex? _mutex;

        /// <summary>
        /// Named kernel event used for cross-process wakeup signaling.
        /// Uses Local\ prefix (not Global\) to avoid requiring elevated permissions.
        /// </summary>
        internal const string WakeupEventName = @"Local\TunaWakeup";

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AllowSetForegroundWindow(uint dwProcessId);
        private const uint ASFW_ANY = unchecked((uint)-1);

        [STAThread]
        public static void Main(string[] args)
        {
            try
            {
                _mutex = new Mutex(true, @"Local\TunaSingleInstanceMutex", out bool createdNew);
                if (!createdNew)
                {
                    // Another instance is already running — signal it to show its window, then exit.
                    try
                    {
                        if (EventWaitHandle.TryOpenExisting(WakeupEventName, out var wakeupEvent))
                        {
                            // Grant foreground focus stealing permission to any process (the background instance)
                            AllowSetForegroundWindow(ASFW_ANY);
                            
                            wakeupEvent.Set();
                            wakeupEvent.Dispose();
                        }
                    }
                    catch
                    {
                    }
                    return;
                }

                NativeLoader.Initialize();

                try
                {
                    BuildAvaloniaApp()
                        .StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
                }
                finally
                {
                    _mutex.ReleaseMutex();
                    _mutex.Dispose();
                }
            }
            catch (Exception ex)
            {
                try
                {
                    File.AppendAllText("crash_log.txt", $"{DateTime.Now}: {ex}\n");
                }
                catch { }
            }
        }

        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .LogToTrace();
    }
}
