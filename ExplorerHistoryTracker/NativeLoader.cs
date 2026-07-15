using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;

namespace ExplorerHistoryTracker
{
    /// <summary>
    /// 启动期原生 dll 内嵌加载器。
    ///
    /// NativeAOT 单 exe 发布后，3 个 Avalonia 原生依赖（libSkiaSharp / libHarfBuzzSharp / av_libglesv2）
    /// 以 gzip 压缩形式内嵌在 exe 资源中。本类在进程启动时（Main 第一行、任何 Avalonia 初始化之前）
    /// 将它们释放到 %APPDATA%\ExplorerHistoryTracker\native 并完成预加载。
    ///
    /// 加载策略：
    ///  1. SetDllDirectoryW 把释放目录加入进程 dll 搜索路径
    ///  2. LoadLibraryExW 预加载 3 个 dll（Windows 对同名 dll 去重，后续 Avalonia 的 LoadLibrary 直接命中）
    ///
    /// 全程 try/catch 兜底：失败只记录，不抛异常。资源缺失时退回默认 dll 搜索路径。
    /// </summary>
    internal static class NativeLoader
    {
        private const string ResourcePrefix = "ExplorerHistoryTracker.Native.";

        private static readonly string[] NativeFileNames =
        {
            "libSkiaSharp.dll",
            "libHarfBuzzSharp.dll",
        };

        private static string? _extractDir;
        private static int _initialized;

        /// <summary>
        /// 入口：必须在 Main 第一行、任何 Avalonia 初始化之前调用。幂等。
        /// </summary>
        public static void Initialize()
        {
            if (System.Threading.Interlocked.CompareExchange(ref _initialized, 1, 0) != 0) return;

            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                _extractDir = ResolveExtractDirectory();

                var extracted = ExtractAll(assembly, _extractDir);
                if (extracted.Count == 0)
                {
                    // 没有内嵌资源——非单 exe 发布，直接退出走默认机制
                    return;
                }

                // 释放目录加入进程 dll 搜索路径
                if (!SetDllDirectoryW(_extractDir))
                {
                    System.Diagnostics.Debug.WriteLine("NativeLoader: SetDllDirectoryW failed");
                }

                // 预加载所有 dll，让它们先进进程模块表
                foreach (var file in NativeFileNames)
                {
                    PreLoadFromDisk(file);
                }

                System.Diagnostics.Debug.WriteLine("NativeLoader: initialized, dir=" + _extractDir);
            }
            catch (Exception ex)
            {
                // 致命兜底：加载器自身崩溃绝不能让主程序起不来
                System.Diagnostics.Debug.WriteLine("NativeLoader: " + ex.Message);
            }
        }

        private static string ResolveExtractDirectory()
        {
            // 释放到应用数据目录下的 native 子目录，避免被系统清理工具清除
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Tuna",
                "native");
        }

        /// <summary>
        /// 把所有内嵌原生资源释放到 destDir。已存在且未过期的文件跳过。
        /// </summary>
        private static List<string> ExtractAll(Assembly assembly, string destDir)
        {
            var done = new List<string>();
            try
            {
                if (!Directory.Exists(destDir)) Directory.CreateDirectory(destDir);

                string[] allNames = assembly.GetManifestResourceNames();

                foreach (string fileName in NativeFileNames)
                {
                    string resourceName = ResourcePrefix + fileName;
                    int idx = Array.IndexOf(allNames, resourceName);
                    if (idx < 0) continue;

                    string destFile = Path.Combine(destDir, fileName);
                    if (NeedsRefresh(destFile))
                    {
                        using Stream? rs = assembly.GetManifestResourceStream(resourceName);
                        if (rs == null) continue;
                        using var gs = new GZipStream(rs, CompressionMode.Decompress);
                        using var fs = new FileStream(destFile, FileMode.Create, FileAccess.Write);
                        gs.CopyTo(fs);
                    }
                    done.Add(destFile);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("NativeLoader ExtractAll: " + ex.Message);
            }
            return done;
        }

        /// <summary>如果目标文件不存在，或当前运行的 exe 写入时间新于已释放的 DLL，则需要重新释放。</summary>
        private static bool NeedsRefresh(string destFile)
        {
            try
            {
                if (!File.Exists(destFile)) return true;

                string? exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath))
                {
                    if (File.GetLastWriteTime(exePath) > File.GetLastWriteTime(destFile))
                        return true;
                }
                return false;
            }
            catch
            {
                return true;
            }
        }

        private static IntPtr PreLoadFromDisk(string fileName)
        {
            try
            {
                if (_extractDir == null) return IntPtr.Zero;
                string full = Path.Combine(_extractDir, fileName);
                if (!File.Exists(full)) return IntPtr.Zero;

                IntPtr h = LoadLibraryExW(full, IntPtr.Zero, LOAD_WITH_ALTERED_SEARCH_PATH);
                if (h == IntPtr.Zero)
                {
                    System.Diagnostics.Debug.WriteLine("NativeLoader: LoadLibraryExW failed: " + fileName);
                }
                return h;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("NativeLoader PreLoad: " + fileName + " - " + ex.Message);
                return IntPtr.Zero;
            }
        }

        #region Win32 P/Invoke

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetDllDirectoryW(string lpPathName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryExW(string lpLibFileName, IntPtr hFile, uint dwFlags);

        private const uint LOAD_WITH_ALTERED_SEARCH_PATH = 0x00000008;

        #endregion
    }
}
