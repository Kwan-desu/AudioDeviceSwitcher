using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.System;
using Windows.UI.ViewManagement;
using AudioSwitcher.AudioApi.CoreAudio;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;

namespace AudioDeviceSwitcher
{
    public sealed partial class MixerWindow : Window
    {
        private const int MixerWidthDips = 424;

        private sealed class AppRowState
        {
            public required AudioSessionGroup Group { get; init; }
            public required Border RowBorder { get; init; }
            public required PeakSlider Slider { get; init; }
            public required TextBlock PercentText { get; init; }
            public required Button IconBtn { get; init; }
            public required UIElement IconVisual { get; init; }
            public FontIcon? IconFallbackGlyph { get; init; }
            public required Border MuteBadge { get; init; }
            public required FontIcon MuteBadgeIcon { get; init; }
            public required TextBlock AppTitleText { get; init; }
            public required Ellipse ActiveDot { get; init; }
            public bool IsDragging { get; set; }
            public double SmoothedPeak { get; set; }
        }

        private readonly AudioDeviceManager _audioManager;
        private readonly AppSettings _settings;

        private readonly List<AppRowState> _appRows = new();
        private IAudioMeterInformation? _masterMeter;
        private DispatcherTimer? _liveTimer;
        private double _smoothedMasterPeak;
        private bool _isUpdatingUi = false;
        private bool _isMasterDragging = false;
        private bool _isFlyoutOpen = false;
        private bool _isClosed = false;

        [DllImport("dwmapi.dll", CharSet = CharSet.Unicode)]
        private static extern int DwmSetWindowAttribute(
            IntPtr hwnd, int attribute, ref int pvAttribute, uint cbAttribute);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        private delegate IntPtr SUBCLASSPROC(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool SetWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, UIntPtr uIdSubclass, UIntPtr dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool RemoveWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, UIntPtr uIdSubclass);

        [DllImport("comctl32.dll")]
        private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

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

        private const int WM_ACTIVATE = 0x0006;
        private const int WA_INACTIVE = 0;

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

        private SUBCLASSPROC? _subclassProc;

        public MixerWindow()
        {
            _audioManager = App.CurrentApp.AudioManager;
            _settings = App.CurrentApp.Settings;
            _isUpdatingUi = true;

            InitializeComponent();

            ConfigureWindow();
            ApplyAcrylicBackdrop();

            // Register pointer handlers with handledEventsToo=true because Slider handles PointerPressed internally
            MasterVolumeSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(MasterVolumeSlider_PointerPressed), true);
            MasterVolumeSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(MasterVolumeSlider_PointerReleased), true);
            MasterVolumeSlider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(MasterVolumeSlider_PointerCaptureLost), true);

            RootGrid.Loaded += (s, e) => ResizeWindowToFitContent();
            RootGrid.ActualThemeChanged += (s, e) => RefreshThemeVisuals();

            ReloadDevices();

            MasterVolumeSlider.ValueChanged += MasterVolumeSlider_ValueChanged;
            DeviceSwitcherFlyout.Opened += (s, e) => _isFlyoutOpen = true;
            DeviceSwitcherFlyout.Closed += (s, e) => _isFlyoutOpen = false;

            Activated += MixerWindow_Activated;
            Closed += MixerWindow_Closed;

            StartLiveTimer();
            PlayEntranceAnimation();
        }

        private string GetActiveThemeKey()
        {
            try
            {
                if (new AccessibilitySettings().HighContrast)
                    return "HighContrast";
            }
            catch { }

            return RootGrid.ActualTheme switch
            {
                ElementTheme.Light => "Light",
                ElementTheme.Dark => "Dark",
                _ => Application.Current.RequestedTheme == ApplicationTheme.Dark ? "Dark" : "Light"
            };
        }

        private Brush GetThemeBrush(string key)
        {
            string themeKey = GetActiveThemeKey();

            if (TryLookupThemeBrush(Application.Current.Resources, themeKey, key, out var appBrush))
                return appBrush!;

            foreach (var merged in Application.Current.Resources.MergedDictionaries)
            {
                if (TryLookupThemeBrush(merged, themeKey, key, out var mergedBrush))
                    return mergedBrush!;
            }

            if (Application.Current.Resources.TryGetValue(key, out var directObj) && directObj is Brush directBrush)
                return directBrush;

            return new SolidColorBrush(Colors.Transparent);
        }

        private static bool TryLookupThemeBrush(ResourceDictionary dict, string themeKey, string key, out Brush? brush)
        {
            brush = null;
            if (dict.ThemeDictionaries.TryGetValue(themeKey, out var tdObj) && tdObj is ResourceDictionary themeDict)
            {
                if (themeDict.TryGetValue(key, out var valObj) && valObj is Brush b)
                {
                    brush = b;
                    return true;
                }
            }
            if (themeKey == "Dark" &&
                dict.ThemeDictionaries.TryGetValue("Default", out var defObj) &&
                defObj is ResourceDictionary defDict)
            {
                if (defDict.TryGetValue(key, out var valObj) && valObj is Brush b)
                {
                    brush = b;
                    return true;
                }
            }
            return false;
        }

        private void RefreshThemeVisuals()
        {
            if (_isClosed) return;

            ApplyDwmThemeAttributes();

            var dev = _audioManager.GetDefaultPlaybackDevice();
            if (dev != null)
            {
                UpdateMasterVisuals(MasterVolumeSlider.Value, dev.IsMuted);
            }

            var cardBrush = GetThemeBrush("MixerCardFillBrush");
            var strokeBrush = GetThemeBrush("MixerCardStrokeBrush");
            var primaryTextBrush = GetThemeBrush("TextFillColorPrimaryBrush");
            var activeDotBrush = GetThemeBrush("ActiveStatusBrush");
            var badgeBg = GetThemeBrush("MixerThumbOuterBrush");
            var badgeStroke = GetThemeBrush("MixerThumbBorderBrush");

            foreach (var row in _appRows)
            {
                row.RowBorder.Background = cardBrush;
                row.RowBorder.BorderBrush = strokeBrush;
                row.AppTitleText.Foreground = primaryTextBrush;
                row.ActiveDot.Fill = activeDotBrush;
                row.MuteBadge.Background = badgeBg;
                row.MuteBadge.BorderBrush = badgeStroke;
                row.MuteBadgeIcon.Foreground = primaryTextBrush;
                if (row.IconFallbackGlyph != null)
                {
                    row.IconFallbackGlyph.Foreground = primaryTextBrush;
                }
                UpdateAppRowVisuals(row, (int)Math.Round(row.Slider.Value), row.Group.IsMuted);
            }
        }

        private void PlayEntranceAnimation()
        {
            try
            {
                FlyoutBorder.Opacity = 0;
                FlyoutTranslate.Y = 10;

                var sb = new Storyboard();
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

                var fadeIn = new DoubleAnimation
                {
                    From = 0.0,
                    To = 1.0,
                    Duration = TimeSpan.FromMilliseconds(160),
                    EasingFunction = ease
                };
                Storyboard.SetTarget(fadeIn, FlyoutBorder);
                Storyboard.SetTargetProperty(fadeIn, "Opacity");

                var slideUp = new DoubleAnimation
                {
                    From = 10.0,
                    To = 0.0,
                    Duration = TimeSpan.FromMilliseconds(180),
                    EasingFunction = ease
                };
                Storyboard.SetTarget(slideUp, FlyoutTranslate);
                Storyboard.SetTargetProperty(slideUp, "Y");

                sb.Children.Add(fadeIn);
                sb.Children.Add(slideUp);
                sb.Begin();
            }
            catch
            {
                FlyoutBorder.Opacity = 1;
                FlyoutTranslate.Y = 0;
            }
        }

        private void StartLiveTimer()
        {
            _liveTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(90)
            };
            _liveTimer.Tick += LiveTimer_Tick;
            _liveTimer.Start();
        }

        private void LiveTimer_Tick(object? sender, object e)
        {
            if (_isClosed) return;

            try
            {
                // 1. Update Master Peak Meter & sync external master volume/mute changes
                var dev = _audioManager.GetDefaultPlaybackDevice();
                if (dev != null)
                {
                    if (!_isMasterDragging)
                    {
                        double devVol = Math.Clamp(dev.Volume, 0, 100);
                        bool devMuted = dev.IsMuted;
                        if (Math.Abs(MasterVolumeSlider.Value - devVol) >= 1.0)
                        {
                            _isUpdatingUi = true;
                            MasterVolumeSlider.Value = devVol;
                            _isUpdatingUi = false;
                        }
                        UpdateMasterVisuals(devVol, devMuted);
                    }

                    double rawMasterPeak = 0;
                    if (!dev.IsMuted && dev.Volume > 0 && _masterMeter != null)
                    {
                        try
                        {
                            if (_masterMeter.GetPeakValue(out float p) == 0)
                            {
                                rawMasterPeak = Math.Clamp(p * 100.0, 0, 100);
                            }
                        }
                        catch { }
                    }

                    // Fast attack, smooth decay
                    _smoothedMasterPeak = rawMasterPeak > _smoothedMasterPeak
                        ? rawMasterPeak
                        : _smoothedMasterPeak * 0.72;
                    if (_smoothedMasterPeak < 0.5) _smoothedMasterPeak = 0;
                    MasterVolumeSlider.PeakLevel = _smoothedMasterPeak;
                }

                // 2. Update App Peak Meters & sync external app volume/mute changes
                foreach (var row in _appRows)
                {
                    if (!row.IsDragging && row.Group.RefreshState())
                    {
                        int externalVol = (int)Math.Round(row.Group.Volume * 100);
                        if (Math.Abs(row.Slider.Value - externalVol) >= 1.0)
                        {
                            _isUpdatingUi = true;
                            row.Slider.Value = externalVol;
                            _isUpdatingUi = false;
                        }
                        UpdateAppRowVisuals(row, externalVol, row.Group.IsMuted);
                    }

                    double rawPeak = row.Group.GetRawPeakLevel() * 100.0;
                    row.SmoothedPeak = rawPeak > row.SmoothedPeak
                        ? rawPeak
                        : row.SmoothedPeak * 0.72;
                    if (row.SmoothedPeak < 0.5) row.SmoothedPeak = 0;

                    row.Slider.PeakLevel = row.SmoothedPeak;
                    row.ActiveDot.Visibility = (!row.Group.IsMuted && row.SmoothedPeak > 1.5)
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                }
            }
            catch { }
        }

        private void MixerWindow_Closed(object sender, WindowEventArgs args)
        {
            _isClosed = true;
            if (_liveTimer != null)
            {
                _liveTimer.Stop();
                _liveTimer.Tick -= LiveTimer_Tick;
                _liveTimer = null;
            }
            CleanupSubclass();
        }

        private void MixerWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated)
            {
                if (!_isFlyoutOpen)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (!_isClosed) Close();
                    });
                }
            }
        }

        private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                Close();
            }
            else if (e.Key == VirtualKey.F5)
            {
                e.Handled = true;
                ReloadDevices();
            }
        }

        private void ConfigureWindow()
        {
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);

            if (appWindow != null)
            {
                if (appWindow.Presenter is OverlappedPresenter presenter)
                {
                    presenter.IsAlwaysOnTop = true;
                    presenter.IsResizable = false;
                    presenter.IsMinimizable = false;
                    presenter.IsMaximizable = false;
                    presenter.SetBorderAndTitleBar(false, false);
                }

                try { appWindow.IsShownInSwitchers = false; } catch { }
                PositionNearTray(appWindow, hWnd);
            }

            // Strip WS_DLGFRAME / WS_BORDER / WS_THICKFRAME to kill WASDK 1.6 white border regression
            try
            {
                int style = GetWindowLong(hWnd, GWL_STYLE);
                SetWindowLong(hWnd, GWL_STYLE, style & ~(WS_DLGFRAME | WS_BORDER | WS_THICKFRAME | WS_CAPTION));
                SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            }
            catch { }

            string theme = _settings?.Theme ?? "System";
            if (Content is FrameworkElement root)
            {
                root.RequestedTheme = theme switch
                {
                    "Light" => ElementTheme.Light,
                    "Dark" => ElementTheme.Dark,
                    _ => ElementTheme.Default
                };
            }

            ApplyDwmThemeAttributes();

            try
            {
                _subclassProc = MixerSubclassProc;
                SetWindowSubclass(hWnd, _subclassProc, new UIntPtr(2001), UIntPtr.Zero);
            }
            catch (Exception ex)
            {
                App.Log($"[MixerWindow] SetWindowSubclass failed: {ex.Message}");
            }
        }

        private void ApplyDwmThemeAttributes()
        {
            try
            {
                var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                bool isDark = GetActiveThemeKey() == "Dark";

                int darkVal = isDark ? 1 : 0;
                DwmSetWindowAttribute(hWnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkVal, sizeof(int));
                DwmSetWindowAttribute(hWnd, 19 /* legacy dark mode */, ref darkVal, sizeof(int));

                // Fluent 2 rounded corners (DWMWCP_ROUND = 2: 8px anti-aliased radius)
                int round = 2;
                DwmSetWindowAttribute(hWnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

                // Suppress OS window border (we render our own crisp 1px Fluent 2 FlyoutSurfaceStrokeBrush border)
                int noneBorder = DWMWA_COLOR_NONE;
                int hrBorder = DwmSetWindowAttribute(hWnd, DWMWA_BORDER_COLOR, ref noneBorder, sizeof(int));
                if (hrBorder != 0)
                {
                    int darkBorder = isDark ? 0x00202020 : 0x00E5E5E5;
                    DwmSetWindowAttribute(hWnd, DWMWA_BORDER_COLOR, ref darkBorder, sizeof(int));
                }
            }
            catch (Exception ex)
            {
                App.Log($"[MixerWindow] Configure DWM failed: {ex.Message}");
            }
        }

        private IntPtr MixerSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData)
        {
            if (uMsg == WM_ACTIVATE)
            {
                int wa = (int)(wParam.ToInt64() & 0xFFFF);
                if (wa == WA_INACTIVE && !_isFlyoutOpen)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (!_isClosed) Close();
                    });
                }
            }
            return DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        private void CleanupSubclass()
        {
            try
            {
                var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                if (_subclassProc != null && hWnd != IntPtr.Zero)
                {
                    RemoveWindowSubclass(hWnd, _subclassProc, new UIntPtr(2001));
                }
            }
            catch { }
        }

        private void ResizeWindowToFitContent()
        {
            if (_isClosed) return;

            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            if (appWindow == null) return;

            RootGrid.UpdateLayout();
            RootGrid.Measure(new Size(MixerWidthDips, double.PositiveInfinity));
            double measuredHeight = RootGrid.DesiredSize.Height;

            int desiredDips;
            if (measuredHeight >= 120 && !double.IsInfinity(measuredHeight) && !double.IsNaN(measuredHeight))
            {
                desiredDips = (int)Math.Ceiling(measuredHeight) + 2;
            }
            else
            {
                int appCount = AppSessionsContainer.Children.Count;
                desiredDips = appCount == 0
                    ? 320
                    : 230 + (appCount * 58) + ((appCount - 1) * 4) + 6;
            }

            desiredDips = Math.Clamp(desiredDips, 260, 640);
            PositionNearTray(appWindow, hWnd, desiredDips);
        }

        private void PositionNearTray(AppWindow appWindow, IntPtr hWnd, int? desiredHeightDips = null)
        {
            uint dpi = GetDpiForWindow(hWnd);
            double scale = (dpi > 0 ? dpi : 96) / 96.0;

            int winWidth = (int)Math.Round(MixerWidthDips * scale);
            int dips = desiredHeightDips ?? 360;
            int winHeight = (int)Math.Round(dips * scale);

            GetCursorPos(out POINT pt);
            IntPtr hMon = MonitorFromPoint(pt, 2 /* MONITOR_DEFAULTTONEAREST */);

            var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(hMon, ref mi))
            {
                int workRight = mi.rcWork.Right;
                int workBottom = mi.rcWork.Bottom;
                int workHeight = mi.rcWork.Bottom - mi.rcWork.Top;

                winHeight = Math.Min(winHeight, workHeight - (int)Math.Round(30 * scale));

                int posX = workRight - winWidth - (int)Math.Round(12 * scale);
                int posY = workBottom - winHeight - (int)Math.Round(12 * scale);

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

        private static string GetDeviceGlyph(string deviceName)
        {
            bool isHeadphone = deviceName.Contains("head", StringComparison.OrdinalIgnoreCase) ||
                               deviceName.Contains("ear", StringComparison.OrdinalIgnoreCase) ||
                               deviceName.Contains("phone", StringComparison.OrdinalIgnoreCase);
            return isHeadphone ? "\uE7F6" : "\uE7F5";
        }

        private static string GetVolumeGlyph(double volume, bool isMuted)
        {
            if (isMuted) return "\uE74F";
            if (volume <= 0.5) return "\uE992";
            if (volume <= 33) return "\uE993";
            if (volume <= 66) return "\uE994";
            return "\uE995";
        }

        private void UpdateMasterVisuals(double volume, bool isMuted)
        {
            int volInt = (int)Math.Round(volume);
            string glyph = GetVolumeGlyph(volume, isMuted);
            MasterMuteIcon.Glyph = glyph;
            // Optical horizontal centering for left-weighted speaker-0 / speaker-1 glyphs
            MasterMuteIcon.Margin = glyph is "\uE992" or "\uE993"
                ? new Thickness(3, 0, 0, 0)
                : (glyph == "\uE994" ? new Thickness(1, 0, 0, 0) : new Thickness(0));
            MasterMuteIcon.Foreground = isMuted
                ? GetThemeBrush("TextFillColorSecondaryBrush")
                : GetThemeBrush("TextFillColorPrimaryBrush");

            MasterVolumeSlider.Opacity = isMuted ? 0.45 : 1.0;

            MasterVolumePercentText.Text = $"{volInt}%";
            MasterVolumePercentText.Foreground = isMuted
                ? GetThemeBrush("TextFillColorSecondaryBrush")
                : GetThemeBrush("TextFillColorPrimaryBrush");
            MasterVolumePercentText.Opacity = isMuted ? 0.55 : 1.0;

            string muteLabel = isMuted ? "Unmute master volume" : "Mute master volume";
            AutomationProperties.SetName(MasterMuteButton, muteLabel);
            ToolTipService.SetToolTip(MasterMuteButton, isMuted ? "Unmute" : "Mute");
            ToolTipService.SetToolTip(MasterVolumeSlider, isMuted ? $"{volInt}% (Muted)" : $"{volInt}%");
        }

        private void MasterVolumeSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _isMasterDragging = true;
        }

        private void MasterVolumeSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            _isMasterDragging = false;
        }

        private void MasterVolumeSlider_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            _isMasterDragging = false;
        }

        private void MasterSliderRow_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            var dev = _audioManager.GetDefaultPlaybackDevice();
            if (dev == null) return;

            int delta = e.GetCurrentPoint(MasterSliderRow).Properties.MouseWheelDelta;
            if (delta == 0) return;

            int step = _settings.ScrollVolumeStep > 0 ? _settings.ScrollVolumeStep : 2;
            double newVol = Math.Clamp(MasterVolumeSlider.Value + (delta > 0 ? step : -step), 0, 100);
            MasterVolumeSlider.Value = newVol;
            e.Handled = true;
        }

        private void MasterMuteButton_Click(object sender, RoutedEventArgs e)
        {
            var dev = _audioManager.GetDefaultPlaybackDevice();
            if (dev == null) return;
            bool newMute = !dev.IsMuted;
            dev.Mute(newMute);
            UpdateMasterVisuals(dev.Volume, newMute);
            App.CurrentApp.UpdateTrayState();
        }

        private void MasterVolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_isUpdatingUi) return;
            var dev = _audioManager.GetDefaultPlaybackDevice();
            if (dev == null) return;

            dev.Volume = MasterVolumeSlider.Value;
            if (dev.IsMuted && MasterVolumeSlider.Value > 0)
            {
                dev.Mute(false);
            }
            UpdateMasterVisuals(MasterVolumeSlider.Value, dev.IsMuted);
            App.CurrentApp.UpdateTrayState();
        }

        public void ReloadDevices()
        {
            _isUpdatingUi = true;

            AppSessionsContainer.Children.Clear();
            DeviceListContainer.Children.Clear();
            _appRows.Clear();
            _smoothedMasterPeak = 0;
            MasterVolumeSlider.PeakLevel = 0;

            var activeDevices = _audioManager.GetActivePlaybackDevices();
            var currentDefault = _audioManager.GetDefaultPlaybackDevice();

            if (currentDefault != null)
            {
                MasterDeviceGlyph.Glyph = GetDeviceGlyph(currentDefault.FullName);
                MasterDeviceNameText.Text = currentDefault.FullName;

                int idx = _settings.SelectedDeviceIds.IndexOf(currentDefault.Id);
                string label = _settings.GetLabelForDevice(currentDefault.Id, currentDefault.FullName, Math.Max(0, idx));
                if (!string.IsNullOrWhiteSpace(label) && !label.Equals(currentDefault.FullName, StringComparison.OrdinalIgnoreCase))
                {
                    MasterDeviceLabelText.Text = label;
                    MasterDeviceLabelBadge.Visibility = Visibility.Visible;
                }
                else
                {
                    MasterDeviceLabelBadge.Visibility = Visibility.Collapsed;
                }

                AutomationProperties.SetName(DeviceSelectorButton, $"Output audio device: {currentDefault.FullName}");
                ToolTipService.SetToolTip(DeviceSelectorButton, $"Output: {currentDefault.FullName} (Click to switch)");

                MasterVolumeSlider.Value = currentDefault.Volume;
                UpdateMasterVisuals(currentDefault.Volume, currentDefault.IsMuted);

                _masterMeter = NativeAudioSessionManager.GetDeviceMeter(currentDefault.Id.ToString());

                // Populate DeviceListContainer in the switcher flyout
                foreach (var dev in activeDevices)
                {
                    bool isCur = dev.Id == currentDefault.Id;

                    var itemBtn = new Button
                    {
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Padding = new Thickness(8, 8, 10, 8),
                        Background = isCur
                            ? GetThemeBrush("SubtleFillColorSecondaryBrush")
                            : new SolidColorBrush(Colors.Transparent),
                        BorderThickness = new Thickness(0),
                        CornerRadius = new CornerRadius(5)
                    };

                    var itemGrid = new Grid { ColumnSpacing = 8 };
                    itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 0: Active indicator pill
                    itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 1: Device icon
                    itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 2: Name + badge
                    itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 3: Vol % + check

                    // Active selection pill (Fluent 2)
                    var pill = new Border
                    {
                        Width = 3,
                        Height = 16,
                        CornerRadius = new CornerRadius(1.5),
                        Background = isCur
                            ? GetThemeBrush("AccentFillColorDefaultBrush")
                            : new SolidColorBrush(Colors.Transparent),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetColumn(pill, 0);
                    itemGrid.Children.Add(pill);

                    var itemIcon = new FontIcon
                    {
                        Glyph = GetDeviceGlyph(dev.FullName),
                        FontSize = 14.5,
                        Foreground = isCur
                            ? GetThemeBrush("AccentTextFillColorPrimaryBrush")
                            : GetThemeBrush("TextFillColorSecondaryBrush"),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetColumn(itemIcon, 1);
                    itemGrid.Children.Add(itemIcon);

                    // 2-column Grid so long device names ellipsize cleanly without clipping badge
                    var nameGrid = new Grid { ColumnSpacing = 6, VerticalAlignment = VerticalAlignment.Center };
                    nameGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    nameGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var nameText = new TextBlock
                    {
                        Text = dev.FullName,
                        FontSize = 12.5,
                        FontWeight = isCur ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                        Foreground = GetThemeBrush("TextFillColorPrimaryBrush"),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxLines = 1,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetColumn(nameText, 0);
                    nameGrid.Children.Add(nameText);

                    int dIdx = _settings.SelectedDeviceIds.IndexOf(dev.Id);
                    string dLabel = _settings.GetLabelForDevice(dev.Id, dev.FullName, Math.Max(0, dIdx));
                    if (!string.IsNullOrWhiteSpace(dLabel) && !dLabel.Equals(dev.FullName, StringComparison.OrdinalIgnoreCase))
                    {
                        var badge = new Border
                        {
                            CornerRadius = new CornerRadius(3),
                            Padding = new Thickness(5, 1, 5, 1),
                            Background = GetThemeBrush("MixerBadgeFillBrush"),
                            BorderBrush = GetThemeBrush("CardStrokeColorDefaultBrush"),
                            BorderThickness = new Thickness(1),
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        badge.Child = new TextBlock
                        {
                            Text = dLabel,
                            FontSize = 10,
                            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                            Foreground = GetThemeBrush("TextFillColorSecondaryBrush")
                        };
                        Grid.SetColumn(badge, 1);
                        nameGrid.Children.Add(badge);
                    }

                    Grid.SetColumn(nameGrid, 2);
                    itemGrid.Children.Add(nameGrid);

                    // Right side: device volume % + optional checkmark
                    var rightStack = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 6,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    var devVolText = new TextBlock
                    {
                        Text = $"{(int)Math.Round(dev.Volume)}%",
                        FontSize = 11,
                        Foreground = GetThemeBrush("TextFillColorSecondaryBrush"),
                        Opacity = dev.IsMuted ? 0.55 : 1.0,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Typography.SetNumeralAlignment(devVolText, FontNumeralAlignment.Tabular);
                    rightStack.Children.Add(devVolText);

                    if (isCur)
                    {
                        rightStack.Children.Add(new FontIcon
                        {
                            Glyph = "\uE73E",
                            FontSize = 11.5,
                            Foreground = GetThemeBrush("AccentTextFillColorPrimaryBrush"),
                            VerticalAlignment = VerticalAlignment.Center
                        });
                    }

                    Grid.SetColumn(rightStack, 3);
                    itemGrid.Children.Add(rightStack);

                    itemBtn.Content = itemGrid;

                    itemBtn.Click += (s, e) =>
                    {
                        if (dev.Id == currentDefault.Id)
                        {
                            DeviceSwitcherFlyout.Hide();
                            return;
                        }

                        dev.SetAsDefault();
                        DeviceSwitcherFlyout.Hide();
                        App.CurrentApp.UpdateTrayState();
                        ReloadDevices();

                        App.CurrentApp.ShowOsd("Audio playback switched", dev.FullName, GetDeviceGlyph(dev.FullName));
                    };

                    DeviceListContainer.Children.Add(itemBtn);
                }

                LoadAppSessions(currentDefault);
            }
            else
            {
                MasterDeviceNameText.Text = "No audio device";
                MasterDeviceLabelBadge.Visibility = Visibility.Collapsed;
                _masterMeter = null;
                SetEmptyAppsState(true, "No audio playback device detected");
            }

            _isUpdatingUi = false;
            ResizeWindowToFitContent();
        }

        private void SetEmptyAppsState(bool isEmpty, string message = "No applications playing audio")
        {
            if (EmptyAppsText != null)
            {
                EmptyAppsText.Text = message;
            }
            if (EmptyAppsPanel != null)
            {
                EmptyAppsPanel.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
            }
            if (AppsCountBadge != null)
            {
                AppsCountBadge.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        private void LoadAppSessions(CoreAudioDevice defaultDevice)
        {
            try
            {
                string devId = defaultDevice.Id.ToString();
                var appSessions = NativeAudioSessionManager.GetAudioSessions(devId);

                if (appSessions.Count == 0)
                {
                    SetEmptyAppsState(true);
                    return;
                }

                SetEmptyAppsState(false);
                AppsCountText.Text = appSessions.Count.ToString();
                AppsCountBadge.Visibility = Visibility.Visible;

                foreach (var appGroup in appSessions)
                {
                    try
                    {
                        var row = CreateAppRow(appGroup);
                        AppSessionsContainer.Children.Add(row);
                    }
                    catch (Exception ex)
                    {
                        App.Log($"[MixerWindow] Error adding app session row: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                App.Log($"[MixerWindow] LoadAppSessions failed: {ex.Message}");
                SetEmptyAppsState(true);
            }
        }

        private void UpdateAppRowVisuals(AppRowState row, int volPercent, bool isMuted)
        {
            row.IconVisual.Opacity = isMuted ? 0.45 : 1.0;
            row.MuteBadge.Visibility = isMuted ? Visibility.Visible : Visibility.Collapsed;
            row.Slider.Opacity = isMuted ? 0.45 : 1.0;

            row.PercentText.Text = $"{volPercent}%";
            row.PercentText.Foreground = isMuted
                ? GetThemeBrush("TextFillColorSecondaryBrush")
                : GetThemeBrush("TextFillColorPrimaryBrush");
            row.PercentText.Opacity = isMuted ? 0.55 : 1.0;

            string muteLabel = isMuted
                ? $"Unmute {row.Group.AppName}"
                : $"Mute {row.Group.AppName}";
            AutomationProperties.SetName(row.IconBtn, muteLabel);
            ToolTipService.SetToolTip(row.IconBtn, isMuted
                ? $"{row.Group.AppName} (Muted — click to unmute)"
                : $"{row.Group.AppName} (Click to mute)");
            ToolTipService.SetToolTip(row.Slider, isMuted ? $"{volPercent}% (Muted)" : $"{volPercent}%");
        }

        private UIElement CreateAppRow(AudioSessionGroup appGroup)
        {
            var cardBrush = GetThemeBrush("MixerCardFillBrush");
            var strokeBrush = GetThemeBrush("MixerCardStrokeBrush");
            var primaryTextBrush = GetThemeBrush("TextFillColorPrimaryBrush");

            var rowContainer = new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = strokeBrush,
                Padding = new Thickness(12, 8, 12, 5),
                Background = cardBrush
            };

            // 2-row x 3-column grid (32 | * | 42) aligned vertically with MasterSliderRow above
            var card = new Grid { ColumnSpacing = 8, RowSpacing = 2 };
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
            card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Col 0 (RowSpan=2): Single mute target — App Icon Button with calm monochrome mute badge
            var iconBtn = new Button
            {
                Width = 32,
                Height = 32,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var iconHost = new Grid { Width = 24, Height = 24 };
            UIElement iconVisual;
            FontIcon? iconFallbackGlyph = null;
            var iconSource = AppIconExtractor.GetAppIcon(appGroup.AppUserModelId, appGroup.ExecutablePath, appGroup.IconPath);
            if (iconSource != null)
            {
                iconVisual = new Image
                {
                    Source = iconSource,
                    Width = 20,
                    Height = 20,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }
            else
            {
                iconFallbackGlyph = new FontIcon
                {
                    Glyph = "\uE71D",
                    FontSize = 16,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = primaryTextBrush
                };
                iconVisual = iconFallbackGlyph;
            }
            iconHost.Children.Add(iconVisual);

            var muteBadgeIcon = new FontIcon
            {
                Glyph = "\uE74F",
                FontSize = 7.5,
                Foreground = primaryTextBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var muteBadge = new Border
            {
                Width = 14,
                Height = 14,
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(1),
                Background = GetThemeBrush("MixerThumbOuterBrush"),
                BorderBrush = GetThemeBrush("MixerThumbBorderBrush"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Visibility = appGroup.IsMuted ? Visibility.Visible : Visibility.Collapsed,
                Child = muteBadgeIcon
            };
            iconHost.Children.Add(muteBadge);
            iconBtn.Content = iconHost;
            Grid.SetColumn(iconBtn, 0);
            Grid.SetRow(iconBtn, 0);
            Grid.SetRowSpan(iconBtn, 2);
            card.Children.Add(iconBtn);

            // Col 1, Row 0: App Title + Active Audio Indicator aligned to slider track left edge (x=+4)
            var titleGrid = new Grid
            {
                Margin = new Thickness(4, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var appTitleText = new TextBlock
            {
                Text = appGroup.AppName,
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = primaryTextBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(appTitleText, 0);
            titleGrid.Children.Add(appTitleText);

            var activeDot = new Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = GetThemeBrush("ActiveStatusBrush"),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };
            Grid.SetColumn(activeDot, 1);
            titleGrid.Children.Add(activeDot);

            Grid.SetColumn(titleGrid, 1);
            Grid.SetRow(titleGrid, 0);
            card.Children.Add(titleGrid);

            // Col 1, Row 1: PeakSlider with integrated rail meter
            int currentVolPercent = (int)Math.Round(appGroup.Volume * 100);
            var slider = new PeakSlider
            {
                Style = (Style)RootGrid.Resources["PeakMeterSliderStyle"],
                Minimum = 0,
                Maximum = 100,
                Value = currentVolPercent,
                Height = 26,
                MinHeight = 26,
                Padding = new Thickness(4, 0, 4, 0),
                IsThumbToolTipEnabled = false,
                VerticalAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetName(slider, $"{appGroup.AppName} volume");

            Grid.SetColumn(slider, 1);
            Grid.SetRow(slider, 1);
            card.Children.Add(slider);

            // Col 2, Row 1: Tabular right-aligned percentage aligned vertically with slider track
            var percentText = new TextBlock
            {
                Text = $"{currentVolPercent}%",
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = primaryTextBrush,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            Typography.SetNumeralAlignment(percentText, FontNumeralAlignment.Tabular);
            Grid.SetColumn(percentText, 2);
            Grid.SetRow(percentText, 1);
            card.Children.Add(percentText);

            rowContainer.Child = card;

            var rowState = new AppRowState
            {
                Group = appGroup,
                RowBorder = rowContainer,
                Slider = slider,
                PercentText = percentText,
                IconBtn = iconBtn,
                IconVisual = iconVisual,
                IconFallbackGlyph = iconFallbackGlyph,
                MuteBadge = muteBadge,
                MuteBadgeIcon = muteBadgeIcon,
                AppTitleText = appTitleText,
                ActiveDot = activeDot
            };
            _appRows.Add(rowState);

            UpdateAppRowVisuals(rowState, currentVolPercent, appGroup.IsMuted);

            // Track slider dragging with handledEventsToo=true
            slider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((s, e) => rowState.IsDragging = true), true);
            slider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((s, e) => rowState.IsDragging = false), true);
            slider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((s, e) => rowState.IsDragging = false), true);

            void AdjustAppSliderByWheel(PointerRoutedEventArgs e, UIElement anchor)
            {
                int delta = e.GetCurrentPoint(anchor).Properties.MouseWheelDelta;
                if (delta == 0) return;

                int step = _settings.ScrollVolumeStep > 0 ? _settings.ScrollVolumeStep : 2;
                double newVal = Math.Clamp(slider.Value + (delta > 0 ? step : -step), 0, 100);
                slider.Value = newVal;
                e.Handled = true;
            }

            // Wheel over slider always adjusts volume; wheel elsewhere on card only adjusts when list doesn't scroll
            slider.PointerWheelChanged += (s, e) => AdjustAppSliderByWheel(e, slider);
            rowContainer.PointerWheelChanged += (s, e) =>
            {
                if (AppsScrollViewer.ScrollableHeight <= 1.0)
                {
                    AdjustAppSliderByWheel(e, rowContainer);
                }
            };

            slider.ValueChanged += (s, e) =>
            {
                if (_isUpdatingUi) return;
                int val = (int)Math.Round(slider.Value);
                appGroup.SetVolume(val / 100.0f);
                if (appGroup.IsMuted && val > 0)
                {
                    appGroup.SetMute(false);
                }
                UpdateAppRowVisuals(rowState, val, appGroup.IsMuted);
            };

            iconBtn.Click += (s, e) =>
            {
                bool newMute = !appGroup.IsMuted;
                appGroup.SetMute(newMute);
                int val = (int)Math.Round(slider.Value);
                UpdateAppRowVisuals(rowState, val, newMute);
            };

            return rowContainer;
        }

        private void QuickSwitchButton_Click(object sender, RoutedEventArgs e)
        {
            App.CurrentApp.QuickSwitch();
            ReloadDevices();
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
            App.CurrentApp.ShowSettingsWindow();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            ReloadDevices();
        }

        private void MoreSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
            try
            {
                Process.Start(new ProcessStartInfo("ms-settings:apps-volume") { UseShellExecute = true });
            }
            catch
            {
                App.CurrentApp.ShowSettingsWindow();
            }
        }
    }
}
