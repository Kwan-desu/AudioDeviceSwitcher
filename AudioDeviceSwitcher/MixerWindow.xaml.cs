using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AudioSwitcher.AudioApi.CoreAudio;

using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using Image = System.Windows.Controls.Image;

namespace AudioDeviceSwitcher
{
    public partial class MixerWindow : Window
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

        private AudioDeviceManager _audioManager;
        private AppSettings _settings;
        private TrayApplicationContext _context;
        private DispatcherTimer _refreshTimer;

        public MixerWindow(AudioDeviceManager audioManager, AppSettings settings, TrayApplicationContext context)
        {
            InitializeComponent();

            _audioManager = audioManager;
            _settings = settings;
            _context = context;

            PositionWindow();
            LoadDevices();

            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
            _refreshTimer.Tick += (s, e) => RefreshDeviceStates();
            _refreshTimer.Start();

            this.SourceInitialized += MixerWindow_SourceInitialized;
            this.Deactivated += (s, e) => this.Close();
            this.Closed += (s, e) => _refreshTimer.Stop();
            this.KeyDown += (s, e) => { if (e.Key == Key.Escape) this.Close(); };
        }

        private void MixerWindow_SourceInitialized(object? sender, EventArgs e)
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
                    GradientColor = unchecked((int)0x33101010) // 20% dark tint for true glass transparency
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

        private void PositionWindow()
        {
            var workingArea = SystemParameters.WorkArea;
            this.Left = workingArea.Right - this.Width - 10;
            this.Top = workingArea.Bottom - this.Height - 10;
        }

        private void LoadDevices()
        {
            DeviceCardsContainer.Children.Clear();
            AppSessionsContainer.Children.Clear();

            var allDevices = _audioManager.GetActivePlaybackDevices();
            var defaultDevice = _audioManager.GetDefaultPlaybackDevice();

            List<CoreAudioDevice> targetDevices;
            if (_settings.MixerDeviceIds.Count > 0)
            {
                targetDevices = allDevices.Where(d => _settings.MixerDeviceIds.Contains(d.Id)).ToList();
            }
            else if (_settings.SelectedDeviceIds.Count > 0)
            {
                targetDevices = allDevices.Where(d => _settings.SelectedDeviceIds.Contains(d.Id)).ToList();
            }
            else
            {
                targetDevices = allDevices;
            }

            if (targetDevices.Count == 0)
            {
                targetDevices = allDevices;
            }

            foreach (var device in targetDevices)
            {
                bool isDefault = defaultDevice != null && device.Id == defaultDevice.Id;
                DeviceCardsContainer.Children.Add(CreateDeviceCard(device, isDefault));
            }

            // Load Application Sessions for Default Device
            if (defaultDevice != null)
            {
                var sessions = defaultDevice.SessionController.All()
                    .Where(s => !string.IsNullOrEmpty(s.ExecutablePath) && !s.IsSystemSession)
                    .ToList();

                if (sessions.Count > 0)
                {
                    AppsSeparator.Visibility = Visibility.Visible;
                    AppsHeader.Visibility = Visibility.Visible;

                    foreach (var session in sessions)
                    {
                        AppSessionsContainer.Children.Add(CreateAppSessionCard(session));
                    }
                }
                else
                {
                    AppsSeparator.Visibility = Visibility.Collapsed;
                    AppsHeader.Visibility = Visibility.Collapsed;
                }
            }
        }

        private Border CreateAppSessionCard(AudioSwitcher.AudioApi.Session.IAudioSession session)
        {
            var cardBorder = new Border
            {
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Color.FromArgb(15, 255, 255, 255)), // Subtle transparent card
                BorderBrush = new SolidColorBrush(Color.FromArgb(15, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(12, 8, 12, 8),
                Tag = session.Id
            };

            var mainGrid = new Grid();
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // 1. Top Row: App Icon & App Name
            var topGrid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var appIcon = new Image
            {
                Width = 16,
                Height = 16,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Source = GetIconForProcess(session.ExecutablePath)
            };

            string appName = string.IsNullOrWhiteSpace(session.DisplayName) 
                ? System.IO.Path.GetFileNameWithoutExtension(ResolveNtPath(session.ExecutablePath))
                : session.DisplayName;

            var nameText = new TextBlock
            {
                Text = appName,
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(230, 230, 230)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };

            Grid.SetColumn(appIcon, 0);
            Grid.SetColumn(nameText, 1);
            topGrid.Children.Add(appIcon);
            topGrid.Children.Add(nameText);
            Grid.SetRow(topGrid, 0);

            // 2. Bottom Row: Speaker Glyph, Slider, Volume Number
            var bottomGrid = new Grid { Margin = new Thickness(0, 2, 0, 0) };
            bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var muteButton = new Button
            {
                Content = session.IsMuted ? "\uE74F" : "\uE995",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets, Segoe UI Symbol"),
                FontSize = 15,
                Foreground = new SolidColorBrush(session.IsMuted ? Color.FromRgb(255, 90, 90) : Colors.White),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Width = 28,
                Height = 28,
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };

            var slider = new Slider
            {
                Style = (Style)FindResource("Win11SliderStyle"),
                Minimum = 0,
                Maximum = 100,
                Value = Math.Clamp((int)session.Volume, 0, 100),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand
            };

            var volText = new TextBlock
            {
                Text = $"{(int)session.Volume}",
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(240, 245, 252)),
                Width = 36,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };

            muteButton.Click += (s, e) =>
            {
                session.IsMuted = !session.IsMuted;
                muteButton.Content = session.IsMuted ? "\uE74F" : "\uE995";
                muteButton.Foreground = new SolidColorBrush(session.IsMuted ? Color.FromRgb(255, 90, 90) : Colors.White);
            };

            slider.ValueChanged += (s, e) =>
            {
                session.Volume = slider.Value;
                volText.Text = $"{(int)slider.Value}";
                if (session.IsMuted && slider.Value > 0)
                {
                    session.IsMuted = false;
                    muteButton.Content = "\uE995";
                    muteButton.Foreground = Brushes.White;
                }
            };

            Grid.SetColumn(muteButton, 0);
            Grid.SetColumn(slider, 1);
            Grid.SetColumn(volText, 2);

            bottomGrid.Children.Add(muteButton);
            bottomGrid.Children.Add(slider);
            bottomGrid.Children.Add(volText);

            Grid.SetRow(bottomGrid, 1);

            mainGrid.Children.Add(topGrid);
            mainGrid.Children.Add(bottomGrid);

            cardBorder.Child = mainGrid;
            return cardBorder;
        }

        private Border CreateDeviceCard(CoreAudioDevice device, bool isDefault)
        {
            var cardBorder = new Border
            {
                CornerRadius = new CornerRadius(6),
                // Very high-transparency frosted glass cards
                Background = new SolidColorBrush(isDefault ? Color.FromArgb(70, 0, 110, 255) : Color.FromArgb(20, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(isDefault ? Color.FromArgb(140, 0, 132, 255) : Color.FromArgb(20, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(12, 8, 12, 8),
                Cursor = Cursors.Hand,
                Tag = device.Id
            };

            var mainGrid = new Grid();
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // 1. Top Row: Device Name & Active Indicator
            var topGrid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            var nameText = new TextBlock
            {
                Text = (isDefault ? "● " : "") + device.FullName,
                FontSize = 13,
                FontWeight = isDefault ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = new SolidColorBrush(isDefault ? Color.FromRgb(220, 240, 255) : Color.FromRgb(245, 245, 245)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            topGrid.Children.Add(nameText);
            Grid.SetRow(topGrid, 0);

            // 2. Bottom Row: Speaker Glyph, Slider, Volume Number
            var bottomGrid = new Grid { Margin = new Thickness(0, 2, 0, 0) };
            bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var muteButton = new Button
            {
                Content = device.IsMuted ? "\uE74F" : "\uE995",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets, Segoe UI Symbol"),
                FontSize = 15,
                Foreground = new SolidColorBrush(device.IsMuted ? Color.FromRgb(255, 90, 90) : Colors.White),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Width = 28,
                Height = 28,
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };

            var slider = new Slider
            {
                Style = (Style)FindResource("Win11SliderStyle"),
                Minimum = 0,
                Maximum = 100,
                Value = Math.Clamp((int)device.Volume, 0, 100),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand
            };

            var volText = new TextBlock
            {
                Text = $"{(int)device.Volume}",
                FontSize = 13,
                FontWeight = FontWeights.Normal,
                Foreground = new SolidColorBrush(Color.FromRgb(245, 245, 245)),
                Width = 36,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };

            // Event Handlers
            Action makeDefaultAction = () =>
            {
                device.SetAsDefault();
                _context.UpdateTrayText();
                LoadDevices(); // Reload to update both devices AND active app sessions
            };

            topGrid.MouseLeftButtonDown += (s, e) => { e.Handled = true; makeDefaultAction(); };
            cardBorder.MouseLeftButtonDown += (s, e) =>
            {
                if (e.OriginalSource is not Thumb && e.OriginalSource is not Slider)
                {
                    makeDefaultAction();
                }
            };

            muteButton.Click += (s, e) =>
            {
                device.ToggleMute();
                muteButton.Content = device.IsMuted ? "\uE74F" : "\uE995";
                muteButton.Foreground = new SolidColorBrush(device.IsMuted ? Color.FromRgb(255, 90, 90) : Colors.White);
                _context.UpdateTrayText();
            };

            slider.ValueChanged += (s, e) =>
            {
                device.Volume = slider.Value;
                volText.Text = $"{(int)slider.Value}";
                if (device.IsMuted && slider.Value > 0)
                {
                    device.Mute(false);
                    muteButton.Content = "\uE995";
                    muteButton.Foreground = Brushes.White;
                }
                _context.UpdateTrayText();
            };

            Grid.SetColumn(muteButton, 0);
            Grid.SetColumn(slider, 1);
            Grid.SetColumn(volText, 2);

            bottomGrid.Children.Add(muteButton);
            bottomGrid.Children.Add(slider);
            bottomGrid.Children.Add(volText);

            Grid.SetRow(bottomGrid, 1);

            mainGrid.Children.Add(topGrid);
            mainGrid.Children.Add(bottomGrid);

            cardBorder.Child = mainGrid;
            return cardBorder;
        }

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("kernel32.dll")]
        private static extern uint QueryDosDevice(string lpDeviceName, System.Text.StringBuilder lpTargetPath, int ucchMax);

        private string ResolveNtPath(string ntPath)
        {
            if (string.IsNullOrEmpty(ntPath)) return ntPath;
            if (!ntPath.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase)) return ntPath;

            foreach (var drive in System.IO.DriveInfo.GetDrives())
            {
                try
                {
                    string driveLetter = drive.Name.Substring(0, 2);
                    var sb = new System.Text.StringBuilder(512);
                    QueryDosDevice(driveLetter, sb, sb.Capacity);
                    string devicePath = sb.ToString();
                    
                    if (ntPath.StartsWith(devicePath, StringComparison.OrdinalIgnoreCase))
                    {
                        return driveLetter + ntPath.Substring(devicePath.Length);
                    }
                }
                catch { }
            }
            return ntPath;
        }

        private ImageSource? GetIconForProcess(string executablePath)
        {
            try
            {
                string realPath = ResolveNtPath(executablePath);
                using (var sysIcon = System.Drawing.Icon.ExtractAssociatedIcon(realPath))
                {
                    if (sysIcon != null)
                    {
                        using (var bmp = sysIcon.ToBitmap())
                        {
                            IntPtr hBitmap = bmp.GetHbitmap();
                            try
                            {
                                return System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                                    hBitmap,
                                    IntPtr.Zero,
                                    Int32Rect.Empty,
                                    System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                            }
                            finally
                            {
                                DeleteObject(hBitmap);
                            }
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private void RefreshDeviceStates()
        {
            var defaultDevice = _audioManager.GetDefaultPlaybackDevice();
            
            // Update Device Cards
            foreach (var child in DeviceCardsContainer.Children)
            {
                if (child is Border card && card.Tag is Guid deviceId)
                {
                    var dev = _audioManager.GetActivePlaybackDevices().FirstOrDefault(d => d.Id == deviceId);
                    if (dev != null)
                    {
                        bool isDef = defaultDevice != null && dev.Id == defaultDevice.Id;
                        card.Background = new SolidColorBrush(isDef ? Color.FromArgb(70, 0, 110, 255) : Color.FromArgb(20, 255, 255, 255));
                        card.BorderBrush = new SolidColorBrush(isDef ? Color.FromArgb(140, 0, 132, 255) : Color.FromArgb(20, 255, 255, 255));
                    }
                }
            }

            // Update Session States
            if (defaultDevice != null)
            {
                try
                {
                    var sessions = defaultDevice.SessionController.All()
                        .Where(s => !string.IsNullOrEmpty(s.ExecutablePath) && !s.IsSystemSession)
                        .ToList();

                    // If a new session launched or closed, we reload all. 
                    // Otherwise just update volumes.
                    if (sessions.Count != AppSessionsContainer.Children.Count)
                    {
                        LoadDevices();
                        return;
                    }

                    for (int i = 0; i < sessions.Count; i++)
                    {
                        var session = sessions[i];
                        if (AppSessionsContainer.Children[i] is Border card && card.Tag?.ToString() == session.Id)
                        {
                            if (card.Child is Grid grid && grid.Children.Count > 1 && grid.Children[1] is Grid bottomGrid)
                            {
                                var muteBtn = bottomGrid.Children[0] as Button;
                                var slider = bottomGrid.Children[1] as Slider;
                                var volTxt = bottomGrid.Children[2] as TextBlock;

                                if (slider != null && !slider.IsMouseOver && !slider.IsMouseCaptured)
                                {
                                    slider.Value = session.Volume;
                                    if (volTxt != null) volTxt.Text = $"{(int)session.Volume}";
                                }

                                if (muteBtn != null)
                                {
                                    muteBtn.Content = session.IsMuted ? "\uE74F" : "\uE995";
                                    muteBtn.Foreground = new SolidColorBrush(session.IsMuted ? Color.FromRgb(255, 90, 90) : Colors.White);
                                }
                            }
                        }
                        else
                        {
                            // Mismatch, reload
                            LoadDevices();
                            return;
                        }
                    }
                }
                catch { }
            }
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            // Hide first to prevent WPF from tearing down the application 
            // if this was the last window, before we open the Settings window.
            this.Hide();
            
            _context.OpenSettings();
            
            this.Close();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}
