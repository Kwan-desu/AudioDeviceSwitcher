using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AudioDeviceSwitcher
{
    /// <summary>
    /// Extracts high-DPI, alpha-transparent app icons for both packaged MSIX/UWP apps
    /// (via shell:AppsFolder AUMID) and classic Win32 executables.
    /// </summary>
    internal static class AppIconExtractor
    {
        private static readonly Dictionary<string, ImageSource> _iconCache = new(StringComparer.OrdinalIgnoreCase);

        [Flags]
        private enum SIIGBF : uint
        {
            ResizeToFit = 0x00,
            BiggerSizeOk = 0x01,
            MemoryOnly = 0x02,
            IconOnly = 0x04,
            ThumbnailOnly = 0x08,
            InCacheOnly = 0x10,
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE
        {
            public int cx;
            public int cy;
        }

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

        [ComImport]
        [Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig]
            int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            IntPtr pbc,
            ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? ppv);

        [DllImport("gdi32.dll")]
        private static extern int GetObject(IntPtr hObject, int nCount, out BITMAP lpObject);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern int GetDIBits(
            IntPtr hdc,
            IntPtr hbmp,
            uint uStartScan,
            uint cScanLines,
            [Out] byte[] lpvBits,
            ref BITMAPINFOHEADER lpbi,
            uint uUsage);

        private static readonly Guid IID_IShellItemImageFactory = new("BCC18B79-BA16-442F-80C4-8A59C30C463B");

        public static ImageSource? GetAppIcon(string? aumid, string? exePath, string? iconPath)
        {
            string cacheKey = !string.IsNullOrWhiteSpace(aumid)
                ? $"aumid:{aumid}"
                : (!string.IsNullOrWhiteSpace(exePath) ? $"exe:{exePath}" : $"icon:{iconPath}");

            if (string.IsNullOrWhiteSpace(cacheKey) || cacheKey.EndsWith(":", StringComparison.Ordinal))
                return null;

            if (_iconCache.TryGetValue(cacheKey, out var cached))
                return cached;

            ImageSource? result = null;

            // 1. Packaged MSIX / Store app via shell:AppsFolder\<AUMID>
            if (!string.IsNullOrWhiteSpace(aumid))
            {
                string shellPath = $@"shell:AppsFolder\{aumid}";
                result = ExtractViaShellFactory(shellPath, SIIGBF.IconOnly | SIIGBF.BiggerSizeOk)
                      ?? ExtractViaShellFactory(shellPath, SIIGBF.BiggerSizeOk);
            }

            // 2. Classic Win32 executable via IShellItemImageFactory (high-DPI 48px PNG-compressed icons)
            if (result == null && !string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
            {
                result = ExtractViaShellFactory(exePath, SIIGBF.IconOnly | SIIGBF.BiggerSizeOk)
                      ?? ExtractViaAssociatedIcon(exePath);
            }

            // 3. Fallback to explicit iconPath if it points to a local image or executable
            if (result == null && !string.IsNullOrWhiteSpace(iconPath))
            {
                string expanded = Environment.ExpandEnvironmentVariables(iconPath.Trim('@', ' '));
                int comma = expanded.IndexOf(',');
                string candidate = comma > 0 ? expanded.Substring(0, comma) : expanded;
                if (File.Exists(candidate))
                {
                    if (candidate.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                        candidate.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                        candidate.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            result = new BitmapImage(new Uri(candidate));
                        }
                        catch { }
                    }

                    result ??= ExtractViaShellFactory(candidate, SIIGBF.IconOnly | SIIGBF.BiggerSizeOk)
                            ?? ExtractViaAssociatedIcon(candidate);
                }
            }

            if (result != null)
            {
                _iconCache[cacheKey] = result;
            }

            return result;
        }

        private static ImageSource? ExtractViaShellFactory(string parsingPath, SIIGBF flags)
        {
            IShellItemImageFactory? factory = null;
            IntPtr hBitmap = IntPtr.Zero;

            try
            {
                Guid iid = IID_IShellItemImageFactory;
                int hr = SHCreateItemFromParsingName(parsingPath, IntPtr.Zero, ref iid, out factory);
                if (hr != 0 || factory == null)
                    return null;

                hr = factory.GetImage(new SIZE { cx = 48, cy = 48 }, flags, out hBitmap);
                if (hr != 0 || hBitmap == IntPtr.Zero)
                    return null;

                return ConvertHBitmapToWriteableBitmap(hBitmap);
            }
            catch
            {
                return null;
            }
            finally
            {
                if (hBitmap != IntPtr.Zero)
                    DeleteObject(hBitmap);
                if (factory != null)
                    Marshal.ReleaseComObject(factory);
            }
        }

        private static ImageSource? ConvertHBitmapToWriteableBitmap(IntPtr hBitmap)
        {
            if (GetObject(hBitmap, Marshal.SizeOf<BITMAP>(), out var bm) == 0 ||
                bm.bmWidth <= 0 || bm.bmHeight <= 0)
            {
                return null;
            }

            int width = bm.bmWidth;
            int height = bm.bmHeight;
            var pixels = new byte[width * height * 4];

            var bmi = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // top-down DIB matching WriteableBitmap BGRA8
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0 // BI_RGB
            };

            IntPtr hdc = GetDC(IntPtr.Zero);
            try
            {
                int scanLines = GetDIBits(hdc, hBitmap, 0, (uint)height, pixels, ref bmi, 0);
                if (scanLines == 0)
                    return null;
            }
            finally
            {
                if (hdc != IntPtr.Zero)
                    ReleaseDC(IntPtr.Zero, hdc);
            }

            // Guard against legacy 24-bit icons where alpha bytes are all zero
            bool hasAlpha = false;
            bool hasColor = false;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i + 3] != 0)
                {
                    hasAlpha = true;
                    break;
                }
                if (pixels[i] != 0 || pixels[i + 1] != 0 || pixels[i + 2] != 0)
                {
                    hasColor = true;
                }
            }

            if (!hasAlpha && hasColor)
            {
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    if (pixels[i] != 0 || pixels[i + 1] != 0 || pixels[i + 2] != 0)
                    {
                        pixels[i + 3] = 255;
                    }
                }
            }

            var wb = new WriteableBitmap(width, height);
            using (var stream = wb.PixelBuffer.AsStream())
            {
                stream.Write(pixels, 0, pixels.Length);
            }
            wb.Invalidate();
            return wb;
        }

        private static ImageSource? ExtractViaAssociatedIcon(string filePath)
        {
            try
            {
                using var sysIcon = Icon.ExtractAssociatedIcon(filePath);
                if (sysIcon == null) return null;

                using var srcBitmap = sysIcon.ToBitmap();
                using var argbBitmap = srcBitmap.PixelFormat == PixelFormat.Format32bppPArgb
                    ? (Bitmap)srcBitmap.Clone()
                    : srcBitmap.Clone(new Rectangle(0, 0, srcBitmap.Width, srcBitmap.Height), PixelFormat.Format32bppPArgb);

                int width = argbBitmap.Width;
                int height = argbBitmap.Height;
                var data = argbBitmap.LockBits(
                    new Rectangle(0, 0, width, height),
                    ImageLockMode.ReadOnly,
                    PixelFormat.Format32bppPArgb);

                try
                {
                    var pixels = new byte[width * height * 4];
                    for (int row = 0; row < height; row++)
                    {
                        Marshal.Copy(data.Scan0 + (row * data.Stride), pixels, row * width * 4, width * 4);
                    }

                    var wb = new WriteableBitmap(width, height);
                    using (var stream = wb.PixelBuffer.AsStream())
                    {
                        stream.Write(pixels, 0, pixels.Length);
                    }
                    wb.Invalidate();
                    return wb;
                }
                finally
                {
                    argbBitmap.UnlockBits(data);
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
