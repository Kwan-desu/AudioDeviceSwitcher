using System;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace AudioDeviceSwitcher
{
    public sealed partial class OsdWindow : Window
    {
        private DispatcherTimer? _hideTimer;
        private Storyboard? _entranceStoryboard;
        private Storyboard? _exitStoryboard;
        private Storyboard? _barStoryboard;
        private bool _isClosing = false;
        private bool _isFirstShow = true;
        private IntPtr _hWnd;
        private AppWindow? _appWindow;

        public bool IsClosed { get; private set; }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hWnd);

        [DllImport("dwmapi.dll", CharSet = CharSet.Unicode)]
        private static extern int DwmSetWindowAttribute(
            IntPtr hwnd, int attribute, ref int pvAttribute, uint cbAttribute);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        private const int GWL_STYLE = -16;
        private const int WS_BORDER = 0x00800000;
        private const int WS_DLGFRAME = 0x00400000;
        private const int WS_CAPTION = 0x00C00000;
        private const int WS_THICKFRAME = 0x00040000;

        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_FRAMECHANGED = 0x0020;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MONITORINFO
        {
            public uint cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        public OsdWindow()
        {
            InitializeComponent();

            ConfigureWindow();
            ApplyAcrylicBackdrop();

            Closed += (s, e) => IsClosed = true;

            _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
            _hideTimer.Tick += (s, e) =>
            {
                _hideTimer.Stop();
                PlayExitAnimation();
            };
        }

        private void ConfigureWindow()
        {
            _hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(_hWnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);

            if (_appWindow != null)
            {
                if (_appWindow.Presenter is OverlappedPresenter presenter)
                {
                    presenter.IsAlwaysOnTop = true;
                    presenter.IsResizable = false;
                    presenter.IsMinimizable = false;
                    presenter.IsMaximizable = false;
                    presenter.SetBorderAndTitleBar(false, false);
                }

                _appWindow.IsShownInSwitchers = false;
                PositionBottomCenter(_appWindow, _hWnd);
            }

            // Non-focusable tool window style & strip border styles
            try
            {
                int exStyle = GetWindowLong(_hWnd, GWL_EXSTYLE);
                SetWindowLong(_hWnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

                // Strip WS_DLGFRAME / WS_BORDER / WS_THICKFRAME to kill WASDK 1.6 white border regression
                int style = GetWindowLong(_hWnd, GWL_STYLE);
                SetWindowLong(_hWnd, GWL_STYLE, style & ~(WS_DLGFRAME | WS_BORDER | WS_THICKFRAME | WS_CAPTION));
                SetWindowPos(_hWnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

                bool isDark = true;
                if (App.CurrentApp?.Settings != null)
                {
                    string theme = App.CurrentApp.Settings.Theme;
                    if (Content is FrameworkElement root)
                    {
                        root.RequestedTheme = theme switch
                        {
                            "Light" => ElementTheme.Light,
                            "Dark" => ElementTheme.Dark,
                            _ => ElementTheme.Default
                        };
                    }
                    isDark = theme switch
                    {
                        "Dark" => true,
                        "Light" => false,
                        _ => Application.Current.RequestedTheme == ApplicationTheme.Dark
                    };
                }

                int darkVal = isDark ? 1 : 0;
                DwmSetWindowAttribute(_hWnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkVal, sizeof(int));
                DwmSetWindowAttribute(_hWnd, 19 /* legacy dark mode */, ref darkVal, sizeof(int));

                // Fluent 2 rounded corners (DWMWCP_ROUND = 2: 8px anti-aliased radius)
                int round = 2;
                DwmSetWindowAttribute(_hWnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

                // Suppress OS window border
                int noneBorder = DWMWA_COLOR_NONE;
                int hrBorder = DwmSetWindowAttribute(_hWnd, DWMWA_BORDER_COLOR, ref noneBorder, sizeof(int));
                if (hrBorder != 0)
                {
                    int darkBorder = isDark ? 0x00202020 : 0x00E5E5E5;
                    DwmSetWindowAttribute(_hWnd, DWMWA_BORDER_COLOR, ref darkBorder, sizeof(int));
                }
            }
            catch (Exception ex)
            {
                App.Log($"[OsdWindow] Configure error: {ex.Message}");
            }
        }

        private void PositionBottomCenter(AppWindow appWindow, IntPtr hWnd)
        {
            uint dpi = GetDpiForWindow(hWnd);
            double scale = (dpi > 0 ? dpi : 96) / 96.0;

            int winWidth = (int)(340 * scale);
            int winHeight = (int)(52 * scale);

            GetCursorPos(out POINT pt);
            IntPtr hMon = MonitorFromPoint(pt, 2 /* MONITOR_DEFAULTTONEAREST */);

            var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(hMon, ref mi))
            {
                int workLeft = mi.rcWork.Left;
                int workWidth = mi.rcWork.Right - mi.rcWork.Left;
                int workBottom = mi.rcWork.Bottom;

                int posX = workLeft + (workWidth - winWidth) / 2;
                int posY = workBottom - winHeight - (int)(54 * scale);

                appWindow.MoveAndResize(new Windows.Graphics.RectInt32(posX, posY, winWidth, winHeight));
            }
            else
            {
                appWindow.Resize(new Windows.Graphics.SizeInt32(winWidth, winHeight));
            }
        }

        private void ApplyAcrylicBackdrop()
        {
            if (DesktopAcrylicController.IsSupported())
            {
                SystemBackdrop = new DesktopAcrylicBackdrop();
            }
            else if (MicaController.IsSupported())
            {
                SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
            }
        }

        private void PlayEntranceAnimation()
        {
            _exitStoryboard?.Stop();
            _entranceStoryboard?.Stop();

            _entranceStoryboard = new Storyboard();

            var fadeIn = new DoubleAnimation
            {
                From = 0.0,
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(150),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(fadeIn, RootGrid);
            Storyboard.SetTargetProperty(fadeIn, "Opacity");

            _entranceStoryboard.Children.Add(fadeIn);
            _entranceStoryboard.Completed += (s, e) =>
            {
                RootGrid.Opacity = 1.0;
            };
            _entranceStoryboard.Begin();
        }

        private void PlayExitAnimation()
        {
            if (_isClosing) return;
            _isClosing = true;

            _entranceStoryboard?.Stop();
            _exitStoryboard?.Stop();

            _exitStoryboard = new Storyboard();

            var fadeOut = new DoubleAnimation
            {
                To = 0.0,
                Duration = TimeSpan.FromMilliseconds(180),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            Storyboard.SetTarget(fadeOut, RootGrid);
            Storyboard.SetTargetProperty(fadeOut, "Opacity");

            _exitStoryboard.Children.Add(fadeOut);

            _exitStoryboard.Completed += (s, e) =>
            {
                if (_isClosing)
                {
                    try
                    {
                        Close();
                    }
                    catch { }
                }
            };

            _exitStoryboard.Begin();
        }

        private void AnimateVolumeBar(double targetValue)
        {
            _barStoryboard?.Stop();
            _barStoryboard = new Storyboard();

            var anim = new DoubleAnimation
            {
                To = Math.Clamp(targetValue, 0, 100),
                Duration = TimeSpan.FromMilliseconds(100),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(anim, VolumeProgressBar);
            Storyboard.SetTargetProperty(anim, "Value");

            _barStoryboard.Children.Add(anim);
            _barStoryboard.Begin();
        }

        public void ShowOsd(string title, string deviceName, string glyph, int? volume = null)
        {
            if (volume.HasValue)
            {
                VolumeModeGrid.Visibility = Visibility.Visible;
                SwitchModeGrid.Visibility = Visibility.Collapsed;

                VolumeGlyphIcon.Glyph = glyph;
                VolumeDeviceNameText.Text = deviceName;
                VolumePercentText.Text = glyph == "\uE74F" ? "Muted" : $"{volume.Value}%";

                if (_isFirstShow)
                {
                    VolumeProgressBar.Value = volume.Value;
                }
                else
                {
                    AnimateVolumeBar(volume.Value);
                }
            }
            else
            {
                VolumeModeGrid.Visibility = Visibility.Collapsed;
                SwitchModeGrid.Visibility = Visibility.Visible;

                bool isHeadphone = deviceName.Contains("head", StringComparison.OrdinalIgnoreCase) ||
                                   deviceName.Contains("ear", StringComparison.OrdinalIgnoreCase);
                SwitchGlyphIcon.Glyph = isHeadphone ? "\uE7F6" : "\uE995";
                SwitchTitleText.Text = title;
                SwitchDeviceNameText.Text = deviceName;
            }

            if (_appWindow != null)
            {
                PositionBottomCenter(_appWindow, _hWnd);
            }

            if (_isClosing)
            {
                _isClosing = false;
                PlayEntranceAnimation();
            }
            else if (_isFirstShow || RootGrid.Opacity < 0.95)
            {
                PlayEntranceAnimation();
            }

            _isFirstShow = false;

            _appWindow?.Show();
            Activate();
            if (_hWnd != IntPtr.Zero)
            {
                SetWindowPos(_hWnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW | SWP_NOACTIVATE);
            }

            _hideTimer?.Stop();
            _hideTimer?.Start();
        }
    }
}
