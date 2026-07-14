using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;

namespace ExplorerHistoryTracker.Services
{
    public static class IconHelper
    {
        private static readonly IntPtr _gdiplusToken;
        private static readonly Guid ClsidPng = new Guid("557cf406-1a04-11d3-9a73-0000f81ef32e");

        // Render at 32x32 instead of 64x64 — saves ~75% pixel memory.
        // Icons are displayed at 22x22 in the UI; 32x32 provides crisp results.
        private const int RenderSize = 32;

        [StructLayout(LayoutKind.Sequential)]
        private struct GdiplusStartupInput
        {
            public uint GdiplusVersion;
            public IntPtr DebugEventCallback;
            public int SuppressBackgroundThread;
            public int SuppressExternalCodecs;

            public static GdiplusStartupInput GetDefault()
            {
                return new GdiplusStartupInput
                {
                    GdiplusVersion = 1,
                    DebugEventCallback = IntPtr.Zero,
                    SuppressBackgroundThread = 0,
                    SuppressExternalCodecs = 0
                };
            }
        }

        [DllImport("gdiplus.dll")]
        private static extern int GdiplusStartup(out IntPtr token, ref GdiplusStartupInput input, IntPtr output);

        [DllImport("gdiplus.dll")]
        private static extern void GdiplusShutdown(IntPtr token);

        [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)]
        private static extern int GdipCreateBitmapFromScan0(int width, int height, int stride, int format, IntPtr scan0, out IntPtr bitmap);

        [DllImport("gdiplus.dll")]
        private static extern int GdipGetImageGraphicsContext(IntPtr image, out IntPtr graphics);

        [DllImport("gdiplus.dll")]
        private static extern int GdipDeleteGraphics(IntPtr graphics);

        [DllImport("gdiplus.dll")]
        private static extern int GdipGraphicsClear(IntPtr graphics, uint color);

        [DllImport("gdiplus.dll")]
        private static extern int GdipDrawImageRectI(IntPtr graphics, IntPtr image, int x, int y, int width, int height);

        [DllImport("gdiplus.dll")]
        private static extern int GdipDisposeImage(IntPtr image);

        [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)]
        private static extern int GdipSaveImageToFile(IntPtr image, string filename, ref Guid clsidEncoder, IntPtr encoderParams);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [StructLayout(LayoutKind.Sequential)]
        private struct ICONINFO
        {
            public bool fIcon;
            public int xHotspot;
            public int yHotspot;
            public IntPtr hbmMask;
            public IntPtr hbmColor;
        }

        [DllImport("user32.dll")]
        private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAP
        {
            public int bmType;
            public int bmWidth;
            public int bmHeight;
            public int bmWidthBytes;
            public ushort bmPlanes;
            public ushort bmBitsPixel;
            public IntPtr bmBits;
        }

        [DllImport("gdi32.dll")]
        private static extern int GetObject(IntPtr hObject, int nCount, out BITMAP lpObject);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            public int bmiColors;
        }

        [DllImport("gdi32.dll")]
        private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines, [Out] byte[] lpvBits, ref BITMAPINFO lpbmi, uint uUsage);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        private const uint SHGFI_ICON = 0x100;
        private const uint SHGFI_LARGEICON = 0x0;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
        private const uint DIB_RGB_COLORS = 0;

        static IconHelper()
        {
            try
            {
                var input = GdiplusStartupInput.GetDefault();
                GdiplusStartup(out _gdiplusToken, ref input, IntPtr.Zero);
            }
            catch
            {
                // GDI+ init failed
            }
        }

        /// <summary>
        /// Loads an icon for the given file/folder path.
        /// This is a factory method consumed by IconCache — do not call directly; use IconCache.GetOrCreate instead.
        /// </summary>
        public static Bitmap? GetIcon(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            IntPtr hIcon = IntPtr.Zero;
            IntPtr bmpSrc = IntPtr.Zero;
            IntPtr bmpDst = IntPtr.Zero;
            GCHandle pinnedArray = default;
            string? tempFile = null;
            ICONINFO info = default;

            try
            {
                var shfi = new SHFILEINFO();
                uint flags = SHGFI_ICON | SHGFI_LARGEICON;

                if (!Directory.Exists(path) && !File.Exists(path))
                {
                    flags |= SHGFI_USEFILEATTRIBUTES;
                }

                SHGetFileInfo(path, 0, ref shfi, (uint)Marshal.SizeOf(shfi), flags);
                hIcon = shfi.hIcon;

                if (hIcon == IntPtr.Zero) return null;

                if (!GetIconInfo(hIcon, out info)) return null;

                int width = 32;
                int height = 32;
                var bmp = new BITMAP();
                if (info.hbmColor != IntPtr.Zero)
                {
                    GetObject(info.hbmColor, Marshal.SizeOf(bmp), out bmp);
                    width = bmp.bmWidth;
                    height = bmp.bmHeight;
                }

                int bitsSize = width * height * 4;
                byte[] pixelBits = new byte[bitsSize];

                IntPtr hdcScreen = GetDC(IntPtr.Zero);
                var bmi = new BITMAPINFO();
                bmi.bmiHeader.biSize = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER));
                bmi.bmiHeader.biWidth = width;
                bmi.bmiHeader.biHeight = -height;
                bmi.bmiHeader.biPlanes = 1;
                bmi.bmiHeader.biBitCount = 32;
                bmi.bmiHeader.biCompression = 0;

                GetDIBits(hdcScreen, info.hbmColor, 0, (uint)height, pixelBits, ref bmi, DIB_RGB_COLORS);
                ReleaseDC(IntPtr.Zero, hdcScreen);

                // Check if the alpha channel has any non-zero values
                bool hasAlpha = false;
                for (int i = 3; i < pixelBits.Length; i += 4)
                {
                    if (pixelBits[i] != 0)
                    {
                        hasAlpha = true;
                        break;
                    }
                }

                // If no alpha, set all alpha to 255 (fully opaque) to prevent rendering issues
                if (!hasAlpha)
                {
                    for (int i = 3; i < pixelBits.Length; i += 4)
                    {
                        pixelBits[i] = 255;
                    }
                }

                // Create a WriteableBitmap directly from the pixel buffer — avoids temp file I/O
                var writeableBitmap = new Avalonia.Media.Imaging.WriteableBitmap(
                    new Avalonia.PixelSize(width, height),
                    new Avalonia.Vector(96, 96),
                    Avalonia.Platform.PixelFormat.Bgra8888,
                    Avalonia.Platform.AlphaFormat.Premul);

                using (var frameBuffer = writeableBitmap.Lock())
                {
                    Marshal.Copy(pixelBits, 0, frameBuffer.Address, pixelBits.Length);
                }

                return writeableBitmap;
            }
            catch
            {
                return null;
            }
            finally
            {
                // Clean up all native resources
                if (hIcon != IntPtr.Zero) DestroyIcon(hIcon);
                if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
                if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
                if (bmpSrc != IntPtr.Zero) DeleteObject(bmpSrc);
                if (bmpDst != IntPtr.Zero) DeleteObject(bmpDst);
            }
        }
    }
}
