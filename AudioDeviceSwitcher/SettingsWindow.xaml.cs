using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AudioSwitcher.AudioApi.CoreAudio;

using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using KeyEventHandler = System.Windows.Input.KeyEventHandler;
using Brush = System.Windows.Media.Brush;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace AudioDeviceSwitcher
{
    public partial class SettingsWindow : Window
    {
        private AppSettings _settings;
        private AudioDeviceManager _audioManager;

        private class DeviceRowData
        {
            public Guid Id { get; set; }
            public string FullName { get; set; } = string.Empty;
            public string InterfaceName { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public bool IsActive { get; set; }
        }

        private readonly Dictionary<Guid, CheckBox> _quickSwitchChecks = new();
        private readonly Dictionary<Guid, CheckBox> _mixerChecks = new();
        private readonly Dictionary<Guid, TextBox> _labelBoxes = new();
        private readonly Dictionary<Guid, DeviceRowData> _rowDevices = new();

        private string _quickSwitchHotkey = "Ctrl+Shift+S";
        private string _openMixerHotkey = "Ctrl+Shift+M";
        private bool _loading;

        public SettingsWindow(AppSettings settings, AudioDeviceManager audioManager)
        {
            InitializeComponent();
            _settings = settings;
            _audioManager = audioManager;

            this.SourceInitialized += SettingsWindow_SourceInitialized;
            this.KeyDown += (s, e) => { if (e.Key == Key.Escape) this.Close(); };

            LoadSettings();
            CheckUpdates();

            ThemeManager.ThemeChanged += OnThemeChanged;
            this.Closed += (s, e) => ThemeManager.ThemeChanged -= OnThemeChanged;
        }

        private void SettingsWindow_SourceInitialized(object? sender, EventArgs e)
        {
            // Settings is a long-lived base layer → Mica per Fluent materials guidance.
            WindowBackdrop.Apply(this, WindowBackdrop.BackdropKind.Mica, ThemeManager.IsDark,
                legacyTint: unchecked((int)0x40141414));
        }

        private void OnThemeChanged()
        {
            // Re-apply the backdrop so the title bar dark/light flips with the theme.
            WindowBackdrop.Apply(this, WindowBackdrop.BackdropKind.Mica, ThemeManager.IsDark,
                legacyTint: unchecked((int)0x40141414));
        }

        private void LoadSettings()
        {
            _loading = true;

            RunAtStartupCheck.IsChecked = _settings.RunAtStartup;
            EnableHotkeysCheck.IsChecked = _settings.EnableGlobalHotkeys;
            EnableTrayScrollCheck.IsChecked = _settings.EnableTrayScrollVolume;

            _quickSwitchHotkey = GlobalHotkeyManager.Normalize(_settings.QuickSwitchHotkey);
            _openMixerHotkey = GlobalHotkeyManager.Normalize(_settings.OpenMixerHotkey);
            QuickSwitchHotkeyButton.Content = _quickSwitchHotkey;
            OpenMixerHotkeyButton.Content = _openMixerHotkey;

            ThemeCombo.SelectedIndex = _settings.Theme switch
            {
                "Light" => 1,
                "Dark" => 2,
                _ => 0
            };

            BuildDeviceRows();

            _loading = false;
        }

        private void BuildDeviceRows()
        {
            _quickSwitchChecks.Clear();
            _mixerChecks.Clear();
            _labelBoxes.Clear();
            _rowDevices.Clear();
            DevicesPanel.Children.Clear();

            var activeDevices = _audioManager.GetActivePlaybackDevices();
            _settings.SyncActiveDevices(activeDevices);

            int index = 0;
            var processedGuids = new HashSet<Guid>();

            // 1. Build rows for active devices
            foreach (var device in activeDevices)
            {
                processedGuids.Add(device.Id);
                var rowData = new DeviceRowData
                {
                    Id = device.Id,
                    FullName = device.FullName,
                    InterfaceName = device.InterfaceName,
                    Name = device.Name,
                    IsActive = true
                };
                _rowDevices[device.Id] = rowData;

                var conf = _settings.ConfiguredDevices.FirstOrDefault(c =>
                    c.Id == device.Id ||
                    (!string.IsNullOrWhiteSpace(device.FullName) &&
                     string.Equals(c.FullName, device.FullName, StringComparison.OrdinalIgnoreCase)));

                bool isQuickSwitch = conf?.QuickSwitch ?? _settings.SelectedDeviceIds.Contains(device.Id);
                bool isMixer = conf?.Mixer ?? (_settings.MixerDeviceIds.Contains(device.Id) || _settings.SelectedDeviceIds.Contains(device.Id));
                string label = _settings.GetLabelForDevice(device.Id, device.FullName, index);

                DevicesPanel.Children.Add(CreateDeviceRow(rowData, label, isQuickSwitch, isMixer));
                index++;
            }

            // 2. Build rows for configured devices that are currently disconnected
            foreach (var conf in _settings.ConfiguredDevices)
            {
                if (processedGuids.Contains(conf.Id)) continue;
                if (activeDevices.Any(d =>
                    d.Id == conf.Id ||
                    (!string.IsNullOrWhiteSpace(conf.FullName) &&
                     string.Equals(d.FullName, conf.FullName, StringComparison.OrdinalIgnoreCase))))
                {
                    continue;
                }

                // Only show disconnected devices that were actually configured
                if (!conf.QuickSwitch && !conf.Mixer) continue;

                var rowData = new DeviceRowData
                {
                    Id = conf.Id,
                    FullName = conf.FullName,
                    InterfaceName = conf.InterfaceName,
                    Name = conf.Name,
                    IsActive = false
                };
                _rowDevices[conf.Id] = rowData;

                string label = !string.IsNullOrWhiteSpace(conf.CustomLabel)
                    ? conf.CustomLabel
                    : $"AUX {index + 1}";

                DevicesPanel.Children.Add(CreateDeviceRow(rowData, label, conf.QuickSwitch, conf.Mixer));
                index++;
            }
        }

        private Border CreateDeviceRow(DeviceRowData dev, string initialLabel, bool isQuickSwitch, bool isMixer)
        {
            var rowBorder = new Border
            {
                Background = (Brush)FindResource("CardBrush"),
                BorderBrush = (Brush)FindResource("StrokeBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 8),
                Opacity = dev.IsActive ? 1.0 : 0.72
            };

            var outer = new StackPanel();

            // Row 1: glyph + device name (+ disconnected badge if inactive)
            var topGrid = new Grid();
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (!dev.IsActive)
            {
                topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            }

            var iconText = new TextBlock
            {
                Text = "\uE7F5",
                FontFamily = (FontFamily)FindResource("FontFamilyIcons"),
                FontSize = 14,
                Foreground = (Brush)FindResource(dev.IsActive ? "AccentBrush" : "TextTertiaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };

            string displayName = !string.IsNullOrWhiteSpace(dev.FullName) ? dev.FullName : "Audio Device";
            var nameText = new TextBlock
            {
                Text = displayName,
                FontFamily = (FontFamily)FindResource("FontFamilyText"),
                FontSize = 14,
                Foreground = (Brush)FindResource(dev.IsActive ? "TextPrimaryBrush" : "TextSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = displayName
            };

            Grid.SetColumn(iconText, 0);
            Grid.SetColumn(nameText, 1);
            topGrid.Children.Add(iconText);
            topGrid.Children.Add(nameText);

            if (!dev.IsActive)
            {
                var disconnectedBadge = new Border
                {
                    Background = (Brush)FindResource("CardHoverBrush"),
                    BorderBrush = (Brush)FindResource("StrokeStrongBrush"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2, 6, 2),
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = "Disconnected",
                        FontSize = 11,
                        Foreground = (Brush)FindResource("TextTertiaryBrush"),
                        FontFamily = (FontFamily)FindResource("FontFamilyText")
                    }
                };
                Grid.SetColumn(disconnectedBadge, 2);
                topGrid.Children.Add(disconnectedBadge);
            }

            // Row 2: label textbox + checkboxes
            var bottomGrid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var labelBox = new TextBox
            {
                Text = initialLabel,
                FontFamily = (FontFamily)FindResource("FontFamilyText"),
                FontSize = 13,
                Foreground = (Brush)FindResource("TextPrimaryBrush"),
                Background = (Brush)FindResource("CardHoverBrush"),
                BorderBrush = (Brush)FindResource("StrokeStrongBrush"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 4, 8, 4),
                Width = 120,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "Short label shown on the tray icon"
            };
            _labelBoxes[dev.Id] = labelBox;

            var quickSwitchCheck = new CheckBox
            {
                Content = "Quick switch",
                IsChecked = isQuickSwitch,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 12, 0)
            };
            _quickSwitchChecks[dev.Id] = quickSwitchCheck;

            var mixerCheck = new CheckBox
            {
                Content = "Mixer",
                IsChecked = isMixer,
                VerticalAlignment = VerticalAlignment.Center
            };
            _mixerChecks[dev.Id] = mixerCheck;

            Grid.SetColumn(labelBox, 0);
            Grid.SetColumn(quickSwitchCheck, 1);
            Grid.SetColumn(mixerCheck, 2);
            bottomGrid.Children.Add(labelBox);
            bottomGrid.Children.Add(quickSwitchCheck);
            bottomGrid.Children.Add(mixerCheck);

            outer.Children.Add(topGrid);
            outer.Children.Add(bottomGrid);
            rowBorder.Child = outer;
            return rowBorder;
        }

        private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            string pref = ThemeCombo.SelectedIndex switch { 1 => "Light", 2 => "Dark", _ => "System" };
            ThemeManager.SetPreference(pref);
        }

        // ── Hotkey capture ───────────────────────────────────────────────────

        private void CaptureQuickSwitch_Click(object sender, RoutedEventArgs e) =>
            CaptureHotkey(QuickSwitchHotkeyButton, hk => _quickSwitchHotkey = hk);

        private void CaptureOpenMixer_Click(object sender, RoutedEventArgs e) =>
            CaptureHotkey(OpenMixerHotkeyButton, hk => _openMixerHotkey = hk);

        private void CaptureHotkey(Button target, Action<string> assign)
        {
            object original = target.Content;
            target.Content = "Press keys…";

            KeyEventHandler? handler = null;
            handler = (s, ev) =>
            {
                ev.Handled = true;
                var key = ev.Key == Key.System ? ev.SystemKey : ev.Key;

                // Ignore lone modifier presses; wait for a real key.
                if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                    or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
                    return;

                if (key == Key.Escape)
                {
                    target.Content = original;
                    this.PreviewKeyDown -= handler;
                    return;
                }

                var mods = Keyboard.Modifiers;
                var parts = new List<string>();
                if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
                if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
                if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
                if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");

                if (parts.Count == 0)
                {
                    target.Content = "Need a modifier";
                    return; // require at least one modifier; keep listening
                }

                parts.Add(key.ToString());
                string combo = GlobalHotkeyManager.Normalize(string.Join("+", parts));
                target.Content = combo;
                assign(combo);
                this.PreviewKeyDown -= handler;
            };

            this.PreviewKeyDown += handler;
        }

        // ── Save / cancel ────────────────────────────────────────────────────

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            _settings.RunAtStartup = RunAtStartupCheck.IsChecked ?? false;
            _settings.EnableGlobalHotkeys = EnableHotkeysCheck.IsChecked ?? false;
            _settings.EnableTrayScrollVolume = EnableTrayScrollCheck.IsChecked ?? true;

            _settings.QuickSwitchHotkey = _quickSwitchHotkey;
            _settings.OpenMixerHotkey = _openMixerHotkey;
            _settings.Theme = ThemeCombo.SelectedIndex switch { 1 => "Light", 2 => "Dark", _ => "System" };

            // Update each device configuration from the UI rows
            foreach (var kvp in _rowDevices)
            {
                var guid = kvp.Key;
                var dev = kvp.Value;

                bool qs = _quickSwitchChecks.TryGetValue(guid, out var qCheck) && qCheck.IsChecked == true;
                bool mx = _mixerChecks.TryGetValue(guid, out var mCheck) && mCheck.IsChecked == true;
                string lbl = _labelBoxes.TryGetValue(guid, out var lBox) ? (lBox.Text?.Trim() ?? "") : "";

                _settings.UpdateOrAddDevice(guid, dev.FullName, dev.InterfaceName, dev.Name, qs, mx, lbl);
            }

            _settings.Save();
            StartupManager.UpdateStartup(_settings.RunAtStartup);

            this.DialogResult = true;
            this.Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            // Revert any live theme preview to the saved value.
            ThemeManager.SetPreference(_settings.Theme);
            this.DialogResult = false;
            this.Close();
        }

        private string? _updateUrl;

        private async void CheckUpdates()
        {
            _updateUrl = await UpdateChecker.CheckForUpdatesAsync();
            if (_updateUrl != null) UpdateButton.Visibility = Visibility.Visible;
        }

        private async void Update_Click(object sender, RoutedEventArgs e)
        {
            if (_updateUrl != null)
            {
                UpdateButton.Content = "Downloading…";
                UpdateButton.IsEnabled = false;
                await UpdateChecker.DownloadAndInstallUpdateAsync(_updateUrl);
            }
        }
    }
}
