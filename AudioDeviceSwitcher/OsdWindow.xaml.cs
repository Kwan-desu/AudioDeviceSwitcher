using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;

namespace AudioDeviceSwitcher
{
    public partial class OsdWindow : Window
    {
        #region Acrylic Blur Interop
        [DllImport("user32.dll")]
        private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;

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

        public OsdWindow()
        {
            InitializeComponent();
            
            this.SourceInitialized += OsdWindow_SourceInitialized;

            // Position at bottom center of primary screen
            var screen = System.Windows.Forms.Screen.PrimaryScreen;
            if (screen != null)
            {
                this.Left = screen.WorkingArea.Left + (screen.WorkingArea.Width - this.Width) / 2;
                this.Top = screen.WorkingArea.Bottom - this.Height - 60;
            }
        }

        private void OsdWindow_SourceInitialized(object? sender, EventArgs e)
        {
            try
            {
                var handle = new WindowInteropHelper(this).Handle;

                int darkMode = 1;
                DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

                int cornerPreference = DWMWCP_ROUND;
                DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPreference, sizeof(int));

                var accent = new AccentPolicy
                {
                    AccentState = AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND,
                    AccentFlags = 2,
                    GradientColor = unchecked((int)0x35121212)
                };

                int accentStructSize = Marshal.SizeOf(accent);
                IntPtr accentPtr = Marshal.AllocHGlobal(accentStructSize);
                Marshal.StructureToPtr(accent, accentPtr, false);

                var data = new WindowCompositionAttributeData
                {
                    Attribute = WCA_ACCENT_POLICY,
                    Data = accentPtr,
                    SizeOfData = accentStructSize
                };

                SetWindowCompositionAttribute(handle, ref data);
                Marshal.FreeHGlobal(accentPtr);
            }
            catch { }
        }

        private System.Threading.CancellationTokenSource? _hideCts;

        public void ShowOsd(string deviceName)
        {
            ShowOsd("AUDIO PLAYBACK SWITCHED", deviceName, "\uE995");
        }

        public async void ShowOsd(string title, string text, string glyph = "\uE995")
        {
            TitleText.Text = title;
            DeviceNameText.Text = text;
            GlyphText.Text = glyph;

            _hideCts?.Cancel();
            _hideCts = new System.Threading.CancellationTokenSource();
            var token = _hideCts.Token;

            this.BeginAnimation(UIElement.OpacityProperty, null);
            this.Opacity = 1;
            this.Show();
            
            try
            {
                // Stay on screen for 1.5 seconds then fade out smoothly
                await Task.Delay(1500, token);
                if (token.IsCancellationRequested) return;

                var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180));
                fadeOut.Completed += (s, e) =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        this.Close();
                    }
                };
                this.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            }
            catch (TaskCanceledException) { }
            catch
            {
                this.Close();
            }
        }
    }
}
