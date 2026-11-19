using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Windows.Storage.Streams;
using Windows.System;
using AudioSwitcher.AudioApi;
using AudioSwitcher.AudioApi.CoreAudio;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;

namespace AudioDeviceSwitcher
{
    public sealed partial class MixerWindow : Window
    {
        private sealed class AppRowState
        {
            public required AudioSessionGroup Group { get; init; }
            public required Slider Slider { get; init; }
            public required ProgressBar PeakBar { get; init; }
            public required TextBlock PercentText { get; init; }
            public required Button IconBtn { get; init; }
            public required UIElement IconVisual { get; init; }
            public required Border MuteBadge { get; init; }
            public required Ellipse ActiveDot { get; init; }
            public bool IsDragging { get; set; }
            public double SmoothedPeak { get; set; }
        }

        private readonly AudioDeviceManager _audioManager;
        private readonly AppSettings _settings;
        private static readonly Dictionary<string, ImageSource> _iconCache = new(StringComparer.OrdinalIgnoreCase);

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

            ReloadDevices();

            MasterVolumeSlider.ValueChanged += MasterVolumeSlider_ValueChanged;
            DeviceSwitcherFlyout.Opened += (s, e) => _isFlyoutOpen = true;
            DeviceSwitcherFlyout.Closed += (s, e) => _isFlyoutOpen = false;

            Activated += MixerWindow_Activated;
            Closed += MixerWindow_Closed;

            StartLiveTimer();
            PlayEntranceAnimation();
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
                                rawMasterPeak = Math.Clamp(p * dev.Volume, 0, 100);
                            }
                        }
                        catch { }
                    }

                    // Fast attack, smooth decay
                    _smoothedMasterPeak = rawMasterPeak > _smoothedMasterPeak
                        ? rawMasterPeak
                        : _smoothedMasterPeak * 0.72;
                    if (_smoothedMasterPeak < 0.5) _smoothedMasterPeak = 0;
                    MasterPeakBar.Value = _smoothedMasterPeak;
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

                    double rawPeak = row.Group.GetPeakLevel() * 100.0;
                    row.SmoothedPeak = rawPeak > row.SmoothedPeak
                        ? rawPeak
                        : row.SmoothedPeak * 0.72;
                    if (row.SmoothedPeak < 0.5) row.SmoothedPeak = 0;

                    row.PeakBar.Value = row.SmoothedPeak;
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

                appWindow.IsShownInSwitchers = false;
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

            // DWM Dark Mode & Native Hardware Anti-Aliased Window Rounding
            try
            {
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

                bool isDark = theme switch
                {
                    "Dark" => true,
                    "Light" => false,
                    _ => Application.Current.RequestedTheme == ApplicationTheme.Dark
                };

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

        private void PositionNearTray(AppWindow appWindow, IntPtr hWnd, int? desiredHeightDips = null)
        {
            uint dpi = GetDpiForWindow(hWnd);
            double scale = (dpi > 0 ? dpi : 96) / 96.0;

            int winWidth = (int)(396 * scale);
            int dips = desiredHeightDips ?? 360;
            int winHeight = (int)(dips * scale);

            GetCursorPos(out POINT pt);
            IntPtr hMon = MonitorFromPoint(pt, 2 /* MONITOR_DEFAULTTONEAREST */);

            var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(hMon, ref mi))
            {
                int workRight = mi.rcWork.Right;
                int workBottom = mi.rcWork.Bottom;
                int workHeight = mi.rcWork.Bottom - mi.rcWork.Top;

                winHeight = Math.Min(winHeight, workHeight - (int)(30 * scale));

                int posX = workRight - winWidth - (int)(12 * scale);
                int posY = workBottom - winHeight - (int)(12 * scale);

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
            if (isMuted || volume <= 0.5) return "\uE74F";
            if (volume <= 33) return "\uE993";
            if (volume <= 66) return "\uE994";
            return "\uE995";
        }

        private void UpdateMasterVisuals(double volume, bool isMuted)
        {
            MasterMuteIcon.Glyph = GetVolumeGlyph(volume, isMuted);
            MasterMuteIcon.Foreground = isMuted
                ? (Brush)Application.Current.Resources["MutedStatusBrush"]
                : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];

            MasterVolumeSlider.Opacity = isMuted ? 0.45 : 1.0;

            MasterVolumePercentText.Text = isMuted ? "Muted" : $"{(int)Math.Round(volume)}%";
            MasterVolumePercentText.Foreground = isMuted
                ? (Brush)Application.Current.Resources["MutedStatusBrush"]
                : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];

            ToolTipService.SetToolTip(MasterMuteButton, isMuted ? "Unmute" : "Mute");
            ToolTipService.SetToolTip(MasterVolumeSlider, isMuted ? "Muted" : $"{(int)Math.Round(volume)}%");
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
            MasterPeakBar.Value = 0;

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
                            ? (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"]
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
                            ? (Brush)Application.Current.Resources["SystemAccentColorBrush"]
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
                            ? (Brush)Application.Current.Resources["SystemAccentColorBrush"]
                            : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
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
                        Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
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
                            Background = (Brush)Application.Current.Resources["LayerOnMicaBaseAltFillColorDefaultBrush"],
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        badge.Child = new TextBlock
                        {
                            Text = dLabel,
                            FontSize = 10,
                            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
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
                    rightStack.Children.Add(new TextBlock
                    {
                        Text = dev.IsMuted ? "Muted" : $"{(int)Math.Round(dev.Volume)}%",
                        FontSize = 11,
                        Foreground = dev.IsMuted
                            ? (Brush)Application.Current.Resources["MutedStatusBrush"]
                            : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                        VerticalAlignment = VerticalAlignment.Center
                    });

                    if (isCur)
                    {
                        rightStack.Children.Add(new FontIcon
                        {
                            Glyph = "\uE73E",
                            FontSize = 11.5,
                            Foreground = (Brush)Application.Current.Resources["SystemAccentColorBrush"],
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

            // Dynamically adjust window height near tray to avoid empty space / clipping
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            if (appWindow != null)
            {
                int appCount = AppSessionsContainer.Children.Count;
                int baseHeight = 262;
                int computedDips = appCount == 0
                    ? baseHeight + 68
                    : baseHeight + (appCount * 62) + 6;
                computedDips = Math.Clamp(computedDips, 315, 570);
                PositionNearTray(appWindow, hWnd, computedDips);
            }
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

        private static void UpdateAppRowVisuals(AppRowState row, int volPercent, bool isMuted)
        {
            row.IconVisual.Opacity = isMuted ? 0.4 : 1.0;
            row.MuteBadge.Visibility = isMuted ? Visibility.Visible : Visibility.Collapsed;
            row.Slider.Opacity = isMuted ? 0.45 : 1.0;

            row.PercentText.Text = isMuted ? "Muted" : $"{volPercent}%";
            row.PercentText.Foreground = isMuted
                ? (Brush)Application.Current.Resources["MutedStatusBrush"]
                : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

            ToolTipService.SetToolTip(row.IconBtn, isMuted
                ? $"{row.Group.AppName} (Muted — click to unmute)"
                : $"{row.Group.AppName} (Click to mute)");
            ToolTipService.SetToolTip(row.Slider, isMuted ? "Muted" : $"{volPercent}%");
        }

        private UIElement CreateAppRow(AudioSessionGroup appGroup)
        {
            var transparentBrush = new SolidColorBrush(Colors.Transparent);

            var rowContainer = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 7, 8, 6),
                Background = transparentBrush
            };

            rowContainer.PointerEntered += (s, e) =>
            {
                rowContainer.Background = (Brush)Application.Current.Resources["CardBackgroundHoverBrush"];
            };
            rowContainer.PointerExited += (s, e) =>
            {
                rowContainer.Background = transparentBrush;
            };

            var card = new Grid
            {
                ColumnSpacing = 10
            };
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // 1. App Icon / Mute Button with muted corner badge
            var iconBtn = new Button
            {
                Width = 36,
                Height = 36,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center
            };

            var iconHost = new Grid { Width = 32, Height = 32 };
            UIElement iconVisual;
            var iconSource = GetAppIcon(appGroup.ExecutablePath);
            if (iconSource != null)
            {
                iconVisual = new Image
                {
                    Source = iconSource,
                    Width = 22,
                    Height = 22,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }
            else
            {
                iconVisual = new FontIcon
                {
                    Glyph = "\uE71D",
                    FontSize = 17,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]
                };
            }
            iconHost.Children.Add(iconVisual);

            var muteBadge = new Border
            {
                Width = 14,
                Height = 14,
                CornerRadius = new CornerRadius(7),
                Background = (Brush)Application.Current.Resources["MutedStatusBrush"],
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Visibility = appGroup.IsMuted ? Visibility.Visible : Visibility.Collapsed,
                Child = new FontIcon
                {
                    Glyph = "\uE74F",
                    FontSize = 8,
                    Foreground = new SolidColorBrush(Colors.White),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            iconHost.Children.Add(muteBadge);

            iconBtn.Content = iconHost;
            Grid.SetColumn(iconBtn, 0);
            card.Children.Add(iconBtn);

            // 2. Right Column: Top Header (Active dot + App Name + Percentage) + Full-Width Slider & Peak Bar
            var rightGrid = new Grid
            {
                VerticalAlignment = VerticalAlignment.Center
            };
            rightGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rightGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Top row: Active dot + App Name (left) + Percentage / Muted (right, aligned to slider end)
            var titleRow = new Grid { Margin = new Thickness(2, 0, 2, 0) };
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var activeDot = new Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = (Brush)Application.Current.Resources["ActiveStatusBrush"],
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };
            Grid.SetColumn(activeDot, 0);
            titleRow.Children.Add(activeDot);

            var appTitleText = new TextBlock
            {
                Text = appGroup.AppName,
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(appTitleText, 1);
            titleRow.Children.Add(appTitleText);

            int currentVolPercent = (int)Math.Round(appGroup.Volume * 100);
            var percentText = new TextBlock
            {
                Text = appGroup.IsMuted ? "Muted" : $"{currentVolPercent}%",
                FontSize = 11.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextAlignment = TextAlignment.Right,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(percentText, 2);
            titleRow.Children.Add(percentText);

            Grid.SetRow(titleRow, 0);
            rightGrid.Children.Add(titleRow);

            // Bottom row: Full-width Slider + 2px live peak meter bar
            var sliderStack = new StackPanel { Spacing = 0 };
            var slider = new Slider
            {
                Minimum = 0,
                Maximum = 100,
                Value = currentVolPercent,
                Height = 26,
                Margin = new Thickness(0, -1, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            var peakBar = new ProgressBar
            {
                Height = 2,
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                CornerRadius = new CornerRadius(1),
                Margin = new Thickness(4, -2, 4, 0),
                Opacity = 0.8,
                Foreground = (Brush)Application.Current.Resources["SystemAccentColorBrush"],
                Background = (Brush)Application.Current.Resources["PeakMeterTrackBrush"]
            };

            sliderStack.Children.Add(slider);
            sliderStack.Children.Add(peakBar);

            Grid.SetRow(sliderStack, 1);
            rightGrid.Children.Add(sliderStack);

            Grid.SetColumn(rightGrid, 1);
            card.Children.Add(rightGrid);

            rowContainer.Child = card;

            var rowState = new AppRowState
            {
                Group = appGroup,
                Slider = slider,
                PeakBar = peakBar,
                PercentText = percentText,
                IconBtn = iconBtn,
                IconVisual = iconVisual,
                MuteBadge = muteBadge,
                ActiveDot = activeDot
            };
            _appRows.Add(rowState);

            UpdateAppRowVisuals(rowState, currentVolPercent, appGroup.IsMuted);

            // Track slider dragging with handledEventsToo=true
            slider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((s, e) => rowState.IsDragging = true), true);
            slider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((s, e) => rowState.IsDragging = false), true);
            slider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((s, e) => rowState.IsDragging = false), true);

            // Mouse wheel scroll over an app row adjusts that app's volume directly
            rowContainer.PointerWheelChanged += (s, e) =>
            {
                int delta = e.GetCurrentPoint(rowContainer).Properties.MouseWheelDelta;
                if (delta == 0) return;

                int step = _settings.ScrollVolumeStep > 0 ? _settings.ScrollVolumeStep : 2;
                double newVal = Math.Clamp(slider.Value + (delta > 0 ? step : -step), 0, 100);
                slider.Value = newVal;
                e.Handled = true;
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

        private static ImageSource? GetAppIcon(string? exePath)
        {
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                return null;

            if (_iconCache.TryGetValue(exePath, out var cached))
                return cached;

            try
            {
                using var sysIcon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                if (sysIcon == null) return null;

                using var bitmap = sysIcon.ToBitmap();
                var ms = new MemoryStream();
                bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                ms.Position = 0;

                var ras = ms.AsRandomAccessStream();
                var bitmapImage = new BitmapImage();
                bitmapImage.SetSource(ras);
                _iconCache[exePath] = bitmapImage;
                return bitmapImage;
            }
            catch
            {
                return null;
            }
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
