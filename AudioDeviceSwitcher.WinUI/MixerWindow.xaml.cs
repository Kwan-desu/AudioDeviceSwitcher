using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Windows.Storage.Streams;
using AudioSwitcher.AudioApi;
using AudioSwitcher.AudioApi.CoreAudio;
using AudioSwitcher.AudioApi.Session;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AudioDeviceSwitcher
{
    public sealed partial class MixerWindow : Window
    {
        private readonly AudioDeviceManager _audioManager;
        private readonly AppSettings _settings;
        private static readonly Dictionary<string, ImageSource> _iconCache = new(StringComparer.OrdinalIgnoreCase);
        private bool _isUpdatingUi = false;

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

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

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
        private bool _isFlyoutOpen = false;

        public MixerWindow()
        {
            _audioManager = App.CurrentApp.AudioManager;
            _settings = App.CurrentApp.Settings;
            _isUpdatingUi = true;

            InitializeComponent();

            ConfigureWindow();
            ApplyAcrylicBackdrop();

            ReloadDevices();

            MasterVolumeSlider.ValueChanged += MasterVolumeSlider_ValueChanged;
            DeviceSwitcherFlyout.Opened += (s, e) => _isFlyoutOpen = true;
            DeviceSwitcherFlyout.Closed += (s, e) => _isFlyoutOpen = false;

            Activated += MixerWindow_Activated;
            Closed += (s, e) => CleanupSubclass();
        }

        private void MixerWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated)
            {
                if (!_isFlyoutOpen)
                {
                    DispatcherQueue.TryEnqueue(() => Close());
                }
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

                // Suppress or darken OS window border
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
                    DispatcherQueue.TryEnqueue(() => Close());
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

            int winWidth = (int)(365 * scale);
            int dips = desiredHeightDips ?? 350;
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

        private void MasterMuteButton_Click(object sender, RoutedEventArgs e)
        {
            var dev = _audioManager.GetDefaultPlaybackDevice();
            if (dev == null) return;
            bool newMute = !dev.IsMuted;
            dev.Mute(newMute);
            bool isHeadphone = dev.FullName.Contains("head", StringComparison.OrdinalIgnoreCase) ||
                               dev.FullName.Contains("ear", StringComparison.OrdinalIgnoreCase);
            string activeGlyph = isHeadphone ? "\uE7F6" : "\uE995";
            MasterMuteIcon.Glyph = newMute ? "\uE74F" : activeGlyph;
            MasterVolumePercentText.Text = newMute ? "Muted" : $"{(int)dev.Volume}%";
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
            bool isHeadphone = dev.FullName.Contains("head", StringComparison.OrdinalIgnoreCase) ||
                               dev.FullName.Contains("ear", StringComparison.OrdinalIgnoreCase);
            string activeGlyph = isHeadphone ? "\uE7F6" : "\uE995";
            MasterMuteIcon.Glyph = dev.IsMuted ? "\uE74F" : activeGlyph;
            MasterVolumePercentText.Text = dev.IsMuted ? "Muted" : $"{(int)MasterVolumeSlider.Value}%";
            ToolTipService.SetToolTip(MasterVolumeSlider, $"{(int)MasterVolumeSlider.Value}%");
            App.CurrentApp.UpdateTrayState();
        }

        public void ReloadDevices()
        {
            _isUpdatingUi = true;

            AppSessionsContainer.Children.Clear();
            DeviceListContainer.Children.Clear();

            var activeDevices = _audioManager.GetActivePlaybackDevices();
            var currentDefault = _audioManager.GetDefaultPlaybackDevice();

            if (currentDefault != null)
            {
                bool isHeadphone = currentDefault.FullName.Contains("head", StringComparison.OrdinalIgnoreCase) ||
                                   currentDefault.FullName.Contains("ear", StringComparison.OrdinalIgnoreCase);

                string activeGlyph = isHeadphone ? "\uE7F6" : "\uE995";
                MasterDeviceGlyph.Glyph = activeGlyph;
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

                ToolTipService.SetToolTip(DeviceSelectorButton, $"Audio output: {currentDefault.FullName} (Click to switch)");

                MasterMuteIcon.Glyph = currentDefault.IsMuted ? "\uE74F" : activeGlyph;
                MasterVolumeSlider.Value = currentDefault.Volume;
                MasterVolumePercentText.Text = currentDefault.IsMuted ? "Muted" : $"{(int)currentDefault.Volume}%";
                ToolTipService.SetToolTip(MasterVolumeSlider, $"{(int)currentDefault.Volume}%");

                // Populate DeviceListContainer in the switcher flyout
                foreach (var dev in activeDevices)
                {
                    bool isCur = dev.Id == currentDefault.Id;
                    bool devIsHeadphone = dev.FullName.Contains("head", StringComparison.OrdinalIgnoreCase) ||
                                          dev.FullName.Contains("ear", StringComparison.OrdinalIgnoreCase);

                    var itemBtn = new Button
                    {
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Padding = new Thickness(10, 7, 10, 7),
                        Background = isCur
                            ? (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"]
                            : new SolidColorBrush(Colors.Transparent),
                        BorderThickness = new Thickness(0),
                        CornerRadius = new CornerRadius(5)
                    };

                    var itemGrid = new Grid();
                    itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var itemIcon = new FontIcon
                    {
                        Glyph = devIsHeadphone ? "\uE7F6" : "\uE995",
                        FontSize = 14,
                        Foreground = isCur
                            ? (Brush)Application.Current.Resources["SystemAccentColorBrush"]
                            : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                        Margin = new Thickness(0, 0, 10, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetColumn(itemIcon, 0);
                    itemGrid.Children.Add(itemIcon);

                    var nameStack = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 6,
                        VerticalAlignment = VerticalAlignment.Center
                    };

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
                    nameStack.Children.Add(nameText);

                    int dIdx = _settings.SelectedDeviceIds.IndexOf(dev.Id);
                    string dLabel = _settings.GetLabelForDevice(dev.Id, dev.FullName, Math.Max(0, dIdx));
                    if (!string.IsNullOrWhiteSpace(dLabel) && !dLabel.Equals(dev.FullName, StringComparison.OrdinalIgnoreCase))
                    {
                        var badge = new Border
                        {
                            CornerRadius = new CornerRadius(3),
                            Padding = new Thickness(4, 1, 4, 1),
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
                        nameStack.Children.Add(badge);
                    }

                    Grid.SetColumn(nameStack, 1);
                    itemGrid.Children.Add(nameStack);

                    if (isCur)
                    {
                        var checkIcon = new FontIcon
                        {
                            Glyph = "\uE73E",
                            FontSize = 12,
                            Foreground = (Brush)Application.Current.Resources["SystemAccentColorBrush"],
                            Margin = new Thickness(8, 0, 0, 0),
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        Grid.SetColumn(checkIcon, 2);
                        itemGrid.Children.Add(checkIcon);
                    }

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

                        bool newIsHead = dev.FullName.Contains("head", StringComparison.OrdinalIgnoreCase) ||
                                         dev.FullName.Contains("ear", StringComparison.OrdinalIgnoreCase);
                        App.CurrentApp.ShowOsd("Audio playback switched", dev.FullName, newIsHead ? "\uE7F6" : "\uE995");
                    };

                    DeviceListContainer.Children.Add(itemBtn);
                }

                LoadAppSessions(currentDefault);
            }
            else
            {
                MasterDeviceNameText.Text = "No audio device";
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
                int baseHeight = 230;
                int computedDips = appCount == 0
                    ? baseHeight + 55
                    : baseHeight + (appCount * 54) + 12;
                computedDips = Math.Clamp(computedDips, 285, 540);
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
        }

        private void LoadAppSessions(CoreAudioDevice defaultDevice)
        {
            try
            {
                var sessionController = defaultDevice.SessionController;
                if (sessionController == null)
                {
                    SetEmptyAppsState(true);
                    return;
                }

                var sessions = sessionController.All()
                    .Where(s => !s.IsSystemSession && (s.Volume > 0 || !string.IsNullOrWhiteSpace(s.DisplayName)))
                    .OrderBy(s => s.DisplayName)
                    .ToList();

                if (sessions.Count == 0)
                {
                    SetEmptyAppsState(true);
                    return;
                }

                SetEmptyAppsState(false);

                foreach (var session in sessions)
                {
                    try
                    {
                        string displayName = !string.IsNullOrWhiteSpace(session.DisplayName)
                            ? session.DisplayName
                            : (!string.IsNullOrWhiteSpace(session.ExecutablePath) ? System.IO.Path.GetFileNameWithoutExtension(session.ExecutablePath) : "Application");

                        var row = CreateAppRow(session, displayName);
                        AppSessionsContainer.Children.Add(row);
                    }
                    catch (Exception ex)
                    {
                        App.Log($"[MixerWindow] Error adding app session: {ex.Message}");
                    }
                }
            }
            catch
            {
                SetEmptyAppsState(true);
            }
        }

        private UIElement CreateAppRow(IAudioSession session, string displayName)
        {
            var card = new Grid
            {
                ColumnSpacing = 10,
                Padding = new Thickness(0, 3, 0, 3)
            };
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 1. App Icon / Mute Button
            var iconBtn = new Button
            {
                Width = 32,
                Height = 32,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center
            };

            var iconSource = GetAppIcon(session.ExecutablePath);
            if (iconSource != null)
            {
                var appImg = new Image
                {
                    Source = iconSource,
                    Width = 22,
                    Height = 22,
                    VerticalAlignment = VerticalAlignment.Center
                };
                iconBtn.Content = appImg;
            }
            else
            {
                var fallbackIcon = new FontIcon
                {
                    Glyph = "\uE71D",
                    FontSize = 16,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]
                };
                iconBtn.Content = fallbackIcon;
            }

            ToolTipService.SetToolTip(iconBtn, $"{displayName} (Click to mute)");
            Grid.SetColumn(iconBtn, 0);
            card.Children.Add(iconBtn);

            // 2. Middle Column: App Display Name + Volume Slider
            var middleStack = new StackPanel
            {
                Spacing = 2,
                VerticalAlignment = VerticalAlignment.Center
            };

            var appTitleText = new TextBlock
            {
                Text = displayName,
                FontSize = 11.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1
            };
            middleStack.Children.Add(appTitleText);

            var slider = new Slider
            {
                Minimum = 0,
                Maximum = 100,
                Value = session.Volume,
                Height = 28,
                Margin = new Thickness(0, -2, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTipService.SetToolTip(slider, $"{(int)session.Volume}%");
            middleStack.Children.Add(slider);

            Grid.SetColumn(middleStack, 1);
            card.Children.Add(middleStack);

            // 3. Right Column: Volume Percentage Text
            var percentText = new TextBlock
            {
                Text = session.IsMuted ? "0%" : $"{(int)session.Volume}%",
                Width = 42,
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(percentText, 2);
            card.Children.Add(percentText);

            iconBtn.Opacity = session.IsMuted ? 0.35 : 1.0;

            slider.ValueChanged += (s, e) =>
            {
                if (_isUpdatingUi) return;
                session.Volume = slider.Value;
                if (session.IsMuted && slider.Value > 0) session.IsMuted = false;
                percentText.Text = session.IsMuted ? "0%" : $"{(int)slider.Value}%";
                ToolTipService.SetToolTip(slider, $"{(int)slider.Value}%");
                iconBtn.Opacity = session.IsMuted ? 0.35 : 1.0;
            };

            iconBtn.Click += (s, e) =>
            {
                session.IsMuted = !session.IsMuted;
                iconBtn.Opacity = session.IsMuted ? 0.35 : 1.0;
                percentText.Text = session.IsMuted ? "0%" : $"{(int)session.Volume}%";
            };

            return card;
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

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
            App.CurrentApp.ShowSettingsWindow();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void MoreSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
            App.CurrentApp.ShowSettingsWindow();
        }
    }
}
