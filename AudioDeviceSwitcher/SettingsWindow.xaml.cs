using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using AudioSwitcher.AudioApi.CoreAudio;

using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;

namespace AudioDeviceSwitcher
{
    public partial class SettingsWindow : Window
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

        private AppSettings _settings;
        private AudioDeviceManager _audioManager;
        
        private Dictionary<Guid, System.Windows.Controls.CheckBox> _quickSwitchChecks = new Dictionary<Guid, System.Windows.Controls.CheckBox>();
        private Dictionary<Guid, System.Windows.Controls.CheckBox> _mixerChecks = new Dictionary<Guid, System.Windows.Controls.CheckBox>();

        public SettingsWindow(AppSettings settings, AudioDeviceManager audioManager)
        {
            InitializeComponent();
            _settings = settings;
            _audioManager = audioManager;

            this.SourceInitialized += SettingsWindow_SourceInitialized;
            this.MouseDown += (s, e) => { if (e.ChangedButton == MouseButton.Left) this.DragMove(); };
            this.KeyDown += (s, e) => { if (e.Key == Key.Escape) this.Close(); };
            
            LoadSettings();
            CheckUpdates();
        }

        private void SettingsWindow_SourceInitialized(object? sender, EventArgs e)
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
                    GradientColor = unchecked((int)0x40141414)
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

        private void LoadSettings()
        {
            RunAtStartupCheck.IsChecked = _settings.RunAtStartup;
            EnableHotkeysCheck.IsChecked = _settings.EnableGlobalHotkeys;
            EnableTrayScrollCheck.IsChecked = _settings.EnableTrayScrollVolume;

            var devices = _audioManager.GetActivePlaybackDevices();
            DevicesPanel.Children.Clear();

            foreach (var device in devices)
            {
                var rowBorder = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(10, 255, 255, 255)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(14, 255, 255, 255)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(12, 10, 12, 10),
                    Margin = new Thickness(0, 0, 0, 8)
                };

                var rowGrid = new Grid();
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                // Device Glyph
                var iconText = new TextBlock
                {
                    Text = "\uE7F5",
                    FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets, Segoe UI Symbol"),
                    FontSize = 14,
                    Foreground = new SolidColorBrush(Color.FromRgb(96, 205, 255)),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 10, 0)
                };

                // Device Name
                var nameText = new TextBlock
                {
                    Text = device.FullName,
                    FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                    FontSize = 13,
                    Foreground = new SolidColorBrush(Color.FromRgb(240, 240, 240)),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = device.FullName
                };

                // Quick Switch CheckBox
                var quickSwitchCheck = new System.Windows.Controls.CheckBox
                {
                    Content = "Quick Switch",
                    IsChecked = _settings.SelectedDeviceIds.Contains(device.Id),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(12, 0, 12, 0)
                };
                _quickSwitchChecks[device.Id] = quickSwitchCheck;

                // Mixer CheckBox
                var mixerCheck = new System.Windows.Controls.CheckBox
                {
                    Content = "Mixer",
                    IsChecked = _settings.MixerDeviceIds.Contains(device.Id) || _settings.SelectedDeviceIds.Contains(device.Id),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 4, 0)
                };
                _mixerChecks[device.Id] = mixerCheck;

                Grid.SetColumn(iconText, 0);
                Grid.SetColumn(nameText, 1);
                Grid.SetColumn(quickSwitchCheck, 2);
                Grid.SetColumn(mixerCheck, 3);

                rowGrid.Children.Add(iconText);
                rowGrid.Children.Add(nameText);
                rowGrid.Children.Add(quickSwitchCheck);
                rowGrid.Children.Add(mixerCheck);

                rowBorder.Child = rowGrid;
                DevicesPanel.Children.Add(rowBorder);
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            _settings.RunAtStartup = RunAtStartupCheck.IsChecked ?? false;
            _settings.EnableGlobalHotkeys = EnableHotkeysCheck.IsChecked ?? false;
            _settings.EnableTrayScrollVolume = EnableTrayScrollCheck.IsChecked ?? true;

            _settings.SelectedDeviceIds.Clear();
            _settings.MixerDeviceIds.Clear();

            foreach (var kvp in _quickSwitchChecks)
            {
                if (kvp.Value.IsChecked == true)
                    _settings.SelectedDeviceIds.Add(kvp.Key);
            }

            foreach (var kvp in _mixerChecks)
            {
                if (kvp.Value.IsChecked == true)
                    _settings.MixerDeviceIds.Add(kvp.Key);
            }

            _settings.Save();
            
            // Apply startup setting immediately
            StartupManager.UpdateStartup(_settings.RunAtStartup);

            this.DialogResult = true;
            this.Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }

        private string? _updateUrl;

        private async void CheckUpdates()
        {
            _updateUrl = await UpdateChecker.CheckForUpdatesAsync();
            if (_updateUrl != null)
            {
                UpdateButton.Visibility = Visibility.Visible;
            }
        }

        private async void Update_Click(object sender, RoutedEventArgs e)
        {
            if (_updateUrl != null)
            {
                UpdateButton.Content = "Downloading...";
                UpdateButton.IsEnabled = false;
                await UpdateChecker.DownloadAndInstallUpdateAsync(_updateUrl);
            }
        }
    }
}
