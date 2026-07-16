using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls;

namespace ExplorerHistoryTracker
{
    class Program
    {
        private static Mutex? _mutex;
        private static MemoryMappedFile? _wakeupTargetMap;

        /// <summary>
        /// Named kernel objects used for cross-process wakeup signaling. The event wakes
        /// the first instance; shared memory carries the exact foreground HWND captured by
        /// the second instance before any scheduling or foreground-window race can occur.
        /// </summary>
        internal const string WakeupEventName = @"Local\TunaWakeup";
        private const string WakeupTargetMapName = @"Local\TunaWakeupTarget";
        private const long WakeupTargetMapCapacity = 16;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AllowSetForegroundWindow(uint dwProcessId);
        private const uint ASFW_ANY = unchecked((uint)-1);

        private static void InitializeWakeupTargetChannel()
        {
            try
            {
                _wakeupTargetMap = MemoryMappedFile.CreateOrOpen(
                    WakeupTargetMapName,
                    WakeupTargetMapCapacity,
                    MemoryMappedFileAccess.ReadWrite);
                using var view = _wakeupTargetMap.CreateViewAccessor(
                    0,
                    WakeupTargetMapCapacity,
                    MemoryMappedFileAccess.ReadWrite);
                view.Write(0, 0L);
                view.Write(8, 0L);
                view.Flush();
            }
            catch
            {
                _wakeupTargetMap?.Dispose();
                _wakeupTargetMap = null;
            }
        }

        private static void WriteWakeupTarget(IntPtr targetWindow)
        {
            try
            {
                using var map = MemoryMappedFile.OpenExisting(
                    WakeupTargetMapName,
                    MemoryMappedFileRights.ReadWrite);
                using var view = map.CreateViewAccessor(
                    0,
                    WakeupTargetMapCapacity,
                    MemoryMappedFileAccess.ReadWrite);
                view.Write(0, targetWindow.ToInt64());
                view.Write(8, DateTime.UtcNow.Ticks);
                view.Flush();
            }
            catch
            {
                // The named event remains a compatible wakeup fallback.
            }
        }

        internal static IntPtr ReadWakeupTarget()
        {
            try
            {
                if (_wakeupTargetMap == null)
                {
                    return IntPtr.Zero;
                }

                using var view = _wakeupTargetMap.CreateViewAccessor(
                    0,
                    WakeupTargetMapCapacity,
                    MemoryMappedFileAccess.ReadWrite);
                long handleValue = view.ReadInt64(0);
                long capturedUtcTicks = view.ReadInt64(8);

                // Consume once so a later event can never reuse a stale dialog target.
                view.Write(0, 0L);
                view.Write(8, 0L);
                view.Flush();

                if (handleValue == 0 || capturedUtcTicks == 0)
                {
                    return IntPtr.Zero;
                }

                long ageTicks = DateTime.UtcNow.Ticks - capturedUtcTicks;
                if (ageTicks < 0 || ageTicks > TimeSpan.FromSeconds(5).Ticks)
                {
                    return IntPtr.Zero;
                }

                return new IntPtr(handleValue);
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

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
                            // Capture in the invoking process before the event wakes a thread-pool
                            // callback in the first instance.
                            WriteWakeupTarget(GetForegroundWindow());

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

                InitializeWakeupTargetChannel();
                NativeLoader.Initialize();

                try
                {
                    BuildAvaloniaApp()
                        .StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
                }
                finally
                {
                    _wakeupTargetMap?.Dispose();
                    _wakeupTargetMap = null;
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
