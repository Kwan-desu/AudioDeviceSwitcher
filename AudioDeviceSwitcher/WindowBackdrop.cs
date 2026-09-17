using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AudioDeviceSwitcher
{
    /// <summary>
    /// Centralized DWM interop for Windows 11 window materials.
    ///
    /// Per Microsoft Fluent guidance (learn.microsoft.com .../signature-experiences/materials):
    ///   • Mica    → opaque base-layer material for long-lived windows (e.g. Settings).
    ///   • Acrylic → translucent material for transient, light-dismiss surfaces
    ///               (e.g. the Mixer flyout, OSD, context menus).
    ///
    /// On Windows 11 22H2+ (build 22621) the supported DWMWA_SYSTEMBACKDROP_TYPE is used.
    /// On older Windows 11 / Windows 10 we fall back to the legacy (undocumented)
    /// SetWindowCompositionAttribute accent policy so the app still looks reasonable.
    /// </summary>
    public static class WindowBackdrop
    {
        #region Win32

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("user32.dll")]
        private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        // DWM window attributes
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;   // 1809+
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;  // 22000+
        private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;       // 22621+

        private const int DWMWCP_ROUND = 2;

        // DWM_SYSTEMBACKDROP_TYPE
        private const int DWMSBT_AUTO = 0;
        private const int DWMSBT_NONE = 1;
        private const int DWMSBT_MAINWINDOW = 2;   // Mica
        private const int DWMSBT_TRANSIENTWINDOW = 3; // Acrylic
        private const int DWMSBT_TABBEDWINDOW = 4;   // Mica Alt

        private enum AccentState
        {
            ACCENT_DISABLED = 0,
            ACCENT_ENABLE_GRADIENT = 1,
            ACCENT_ENABLE_TRANSPARENTGRADIENT = 2,
            ACCENT_ENABLE_BLURBEHIND = 3,
            ACCENT_ENABLE_ACRYLICBLURBEHIND = 4,
            ACCENT_ENABLE_HOSTBACKDROP = 5
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AccentPolicy
        {
            public AccentState AccentState;
            public int AccentFlags;
            public int GradientColor;
            public int AnimationId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowCompositionAttributeData
        {
            public int Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        private const int WCA_ACCENT_POLICY = 19;

        #endregion

        public enum BackdropKind
        {
            Mica,
            Acrylic
        }

        private static readonly Version Win10 = new Version(10, 0, 10240);
        private static bool IsWindows11 =>
            Environment.OSVersion.Version.Build >= 22000;
        private static bool SupportsSystemBackdrop =>
            Environment.OSVersion.Version.Build >= 22621;

        /// <summary>
        /// Applies the requested material, rounded corners and dark/light title bar to a window.
        /// Must be called after the HWND exists (e.g. in SourceInitialized).
        /// </summary>
        /// <param name="window">Target WPF window (must be AllowsTransparency=false for Mica to show).</param>
        /// <param name="kind">Mica for base layers, Acrylic for transient surfaces.</param>
        /// <param name="darkMode">Whether to render the immersive dark title bar / dark material.</param>
        /// <param name="legacyTint">ARGB tint used only on the legacy fallback path.</param>
        public static void Apply(Window window, BackdropKind kind, bool darkMode, int legacyTint)
        {
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;

                int dark = darkMode ? 1 : 0;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

                if (IsWindows11)
                {
                    int round = DWMWCP_ROUND;
                    DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
                }

                if (SupportsSystemBackdrop)
                {
                    int backdrop = kind == BackdropKind.Mica ? DWMSBT_MAINWINDOW : DWMSBT_TRANSIENTWINDOW;
                    DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
                }
                else
                {
                    ApplyLegacyAcrylic(hwnd, legacyTint);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WindowBackdrop] Apply failed: {ex.Message}");
            }
        }

        private static void ApplyLegacyAcrylic(IntPtr hwnd, int gradientColor)
        {
            var accent = new AccentPolicy
            {
                AccentState = AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND,
                AccentFlags = 2,
                GradientColor = gradientColor
            };

            int size = Marshal.SizeOf(accent);
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, ptr, false);
                var data = new WindowCompositionAttributeData
                {
                    Attribute = WCA_ACCENT_POLICY,
                    Data = ptr,
                    SizeOfData = size
                };
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
    }
}
