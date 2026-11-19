using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using AudioSwitcher.AudioApi.CoreAudio;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AudioDeviceSwitcher
{
    public sealed partial class SettingsWindow : Window
    {
        private readonly AppSettings _settings;
        private readonly AudioDeviceManager _audioManager;
        private bool _isInitializing = true;
        private bool _isUpdatingHero = false;
        private UpdateInfo? _pendingUpdate;

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("dwmapi.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void DwmSetWindowAttribute(
            IntPtr hwnd, int attribute, ref int pvAttribute, uint cbAttribute);

        public SettingsWindow()
        {
            App.Log("[SettingsWindow] Constructor started.");
            try
            {
                InitializeComponent();
                App.Log("[SettingsWindow] InitializeComponent succeeded.");

                _settings = App.CurrentApp.Settings;
                _audioManager = App.CurrentApp.AudioManager;

                ConfigureTitleBar();
                ConfigureDwmChrome();

                ApplyTheme(_settings.Theme);
                ApplyBackdrop(_settings.Backdrop);

                LoadSettingsUi();
                ReloadDevices();

                _isInitializing = false;
                App.Log("[SettingsWindow] Constructor completed.");

                CheckUpdatesSilent();
            }
            catch (Exception ex)
            {
                App.Log($"[SettingsWindow EXCEPTION] {ex}");
                throw;
            }
        }

        private void ConfigureTitleBar()
        {
            try
            {
                var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
                var appWindow = AppWindow.GetFromWindowId(windowId);

                if (AppWindowTitleBar.IsCustomizationSupported())
                {
                    if (appWindow?.TitleBar is { } titleBar)
                    {
                        titleBar.ExtendsContentIntoTitleBar = true;
                        titleBar.ButtonBackgroundColor = Colors.Transparent;
                        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
                    }
                }
                else
                {
                    ExtendsContentIntoTitleBar = true;
                    SetTitleBar(AppTitleBar);
                }

                // Set DPI-scaled default window size (840x640 DIPs)
                if (appWindow != null)
                {
                    uint dpi = GetDpiForWindow(hWnd);
                    double scale = (dpi > 0 ? dpi : 96) / 96.0;
                    appWindow.Resize(new Windows.Graphics.SizeInt32((int)(840 * scale), (int)(640 * scale)));

                    appWindow.Closing += (s, e) =>
                    {
                        e.Cancel = true;
                        appWindow.Hide();
                    };
                }
            }
            catch (Exception ex)
            {
                App.Log($"[SettingsWindow ConfigureTitleBar error] {ex.Message}");
            }
        }

        public void ShowAndActivate()
        {
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            ReloadDevices();
            appWindow?.Show();
            Activate();
        }

        private void ConfigureDwmChrome()
        {
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            try
            {
                int round = 2; // DWMWCP_ROUND (8px)
                DwmSetWindowAttribute(hWnd, 33, ref round, sizeof(int));
            }
            catch { }
        }

        private void ApplyTheme(string theme)
        {
            if (Content is FrameworkElement root)
            {
                root.RequestedTheme = theme switch
                {
                    "Light" => ElementTheme.Light,
                    "Dark" => ElementTheme.Dark,
                    _ => ElementTheme.Default
                };

                bool isDark = theme switch
                {
                    "Dark" => true,
                    "Light" => false,
                    _ => Application.Current.RequestedTheme == ApplicationTheme.Dark
                };

                try
                {
                    var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                    int darkVal = isDark ? 1 : 0;
                    DwmSetWindowAttribute(hWnd, 20, ref darkVal, sizeof(int));
                }
                catch { }
            }
        }

        public void ApplyBackdrop(string backdrop)
        {
            switch (backdrop)
            {
                case "MicaAlt":
                    if (MicaController.IsSupported())
                        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
                    break;

                case "Acrylic":
                    if (DesktopAcrylicController.IsSupported())
                        SystemBackdrop = new DesktopAcrylicBackdrop();
                    break;

                default: // "Mica"
                    if (MicaController.IsSupported())
                        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
                    break;
            }
        }

        private void MainNavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (DevicesSectionPanel == null || MixerSectionPanel == null ||
                ShortcutsSectionPanel == null || AppearanceSectionPanel == null)
            {
                return;
            }

            string tag = (args.SelectedItem as NavigationViewItem)?.Tag?.ToString() ?? "devices";

            DevicesSectionPanel.Visibility = tag == "devices" ? Visibility.Visible : Visibility.Collapsed;
            MixerSectionPanel.Visibility = tag == "mixer" ? Visibility.Visible : Visibility.Collapsed;
            ShortcutsSectionPanel.Visibility = tag == "shortcuts" ? Visibility.Visible : Visibility.Collapsed;
            AppearanceSectionPanel.Visibility = tag == "appearance" ? Visibility.Visible : Visibility.Collapsed;

            ContentScrollViewer?.ChangeView(null, 0, null, true);

            if (tag == "devices")
            {
                UpdateHeroDeviceCard();
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

        private void UpdateHeroDeviceCard()
        {
            if (HeroDeviceNameText == null) return;
            _isUpdatingHero = true;
            try
            {
                var currentDefault = _audioManager.GetDefaultPlaybackDevice();
                if (currentDefault != null)
                {
                    HeroDeviceGlyph.Glyph = GetDeviceGlyph(currentDefault.FullName);
                    HeroDeviceNameText.Text = currentDefault.FullName;

                    int idx = _settings.SelectedDeviceIds.IndexOf(currentDefault.Id);
                    string label = _settings.GetLabelForDevice(currentDefault.Id, currentDefault.FullName, Math.Max(0, idx));
                    if (!string.IsNullOrWhiteSpace(label) && !label.Equals(currentDefault.FullName, StringComparison.OrdinalIgnoreCase))
                    {
                        HeroDeviceBadgeText.Text = label;
                        HeroDeviceBadge.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        HeroDeviceBadge.Visibility = Visibility.Collapsed;
                    }

                    double vol = Math.Clamp(currentDefault.Volume, 0, 100);
                    bool muted = currentDefault.IsMuted;
                    HeroVolumeSlider.IsEnabled = true;
                    HeroMuteButton.IsEnabled = true;
                    HeroVolumeSlider.Value = vol;
                    HeroVolumeSlider.Opacity = muted ? 0.45 : 1.0;
                    HeroMuteIcon.Glyph = GetVolumeGlyph(vol, muted);
                    HeroMuteIcon.Foreground = muted
                        ? (Brush)Application.Current.Resources["MutedStatusBrush"]
                        : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
                    HeroVolumePercentText.Text = muted ? "Muted" : $"{(int)Math.Round(vol)}%";
                    HeroVolumePercentText.Foreground = muted
                        ? (Brush)Application.Current.Resources["MutedStatusBrush"]
                        : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
                }
                else
                {
                    HeroDeviceGlyph.Glyph = "\uE74F";
                    HeroDeviceNameText.Text = "No active playback device";
                    HeroDeviceBadge.Visibility = Visibility.Collapsed;
                    HeroVolumeSlider.IsEnabled = false;
                    HeroMuteButton.IsEnabled = false;
                    HeroVolumeSlider.Value = 0;
                    HeroVolumePercentText.Text = "--";
                }
            }
            catch { }
            finally
            {
                _isUpdatingHero = false;
            }
        }

        private void HeroQuickSwitchButton_Click(object sender, RoutedEventArgs e)
        {
            App.CurrentApp.QuickSwitch();
            ReloadDevices();
        }

        private void HeroOpenMixerButton_Click(object sender, RoutedEventArgs e)
        {
            App.CurrentApp.ShowMixerWindow();
        }

        private void HeroMuteButton_Click(object sender, RoutedEventArgs e)
        {
            var dev = _audioManager.GetDefaultPlaybackDevice();
            if (dev == null) return;
            dev.Mute(!dev.IsMuted);
            UpdateHeroDeviceCard();
            App.CurrentApp.UpdateTrayState();
        }

        private void HeroVolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_isUpdatingHero || _isInitializing) return;
            var dev = _audioManager.GetDefaultPlaybackDevice();
            if (dev == null) return;

            dev.Volume = HeroVolumeSlider.Value;
            if (dev.IsMuted && HeroVolumeSlider.Value > 0)
            {
                dev.Mute(false);
            }
            UpdateHeroDeviceCard();
            App.CurrentApp.UpdateTrayState();
        }

        private void PreviewVolumeOsd_Click(object sender, RoutedEventArgs e)
        {
            var dev = _audioManager.GetDefaultPlaybackDevice();
            string name = dev?.FullName ?? "Headphones (Preview)";
            int vol = dev != null ? Math.Clamp((int)Math.Round(dev.Volume), 0, 100) : 64;
            bool muted = dev?.IsMuted ?? false;
            App.CurrentApp.ShowOsd("Volume adjusted", name, GetVolumeGlyph(vol, muted), muted ? 0 : vol);
        }

        private void PreviewSwitchOsd_Click(object sender, RoutedEventArgs e)
        {
            var dev = _audioManager.GetDefaultPlaybackDevice();
            string name = dev?.FullName ?? "Headphones (Preview)";
            App.CurrentApp.ShowOsd("Audio playback switched", name, GetDeviceGlyph(name));
        }

        private void LoadSettingsUi()
        {
            StartupToggle.IsOn = _settings.RunAtStartup;
            StartMinimizedToggle.IsOn = _settings.StartMinimized;
            HotkeysToggle.IsOn = _settings.EnableGlobalHotkeys;
            TrayScrollToggle.IsOn = _settings.EnableTrayScrollVolume;

            VersionTextBlock.Text = $"Version {UpdateChecker.GetCurrentVersion()} (WinUI 3 Fluent 2)";

            // Scroll Step Combo
            foreach (var item in ScrollStepCombo.Items)
            {
                if (item is ComboBoxItem cbi && int.TryParse(cbi.Tag?.ToString(), out int val) && val == _settings.ScrollVolumeStep)
                {
                    ScrollStepCombo.SelectedItem = cbi;
                    break;
                }
            }
            if (ScrollStepCombo.SelectedItem == null) ScrollStepCombo.SelectedIndex = 1; // Default 2%

            // OSD Duration Combo
            foreach (var item in OsdDurationCombo.Items)
            {
                if (item is ComboBoxItem cbi && int.TryParse(cbi.Tag?.ToString(), out int val) && val == _settings.OsdDurationMs)
                {
                    OsdDurationCombo.SelectedItem = cbi;
                    break;
                }
            }
            if (OsdDurationCombo.SelectedItem == null) OsdDurationCombo.SelectedIndex = 3; // Default 1500ms

            QuickSwitchHotkeyButton.Content = _settings.QuickSwitchHotkey;
            OpenMixerHotkeyButton.Content = _settings.OpenMixerHotkey;

            // Theme Combo
            foreach (var item in ThemeCombo.Items)
            {
                if (item is ComboBoxItem cbi && (string)cbi.Tag == _settings.Theme)
                {
                    ThemeCombo.SelectedItem = cbi;
                    break;
                }
            }
            if (ThemeCombo.SelectedItem == null) ThemeCombo.SelectedIndex = 0;

            // Backdrop Combo
            foreach (var item in BackdropCombo.Items)
            {
                if (item is ComboBoxItem cbi && (string)cbi.Tag == _settings.Backdrop)
                {
                    BackdropCombo.SelectedItem = cbi;
                    break;
                }
            }
            if (BackdropCombo.SelectedItem == null) BackdropCombo.SelectedIndex = 0;
        }

        public void ReloadDevices()
        {
            DevicesContainer.Children.Clear();

            var activeDevices = _audioManager.GetActivePlaybackDevices();
            _settings.SyncActiveDevices(activeDevices);
            var currentDefault = _audioManager.GetDefaultPlaybackDevice();

            UpdateHeroDeviceCard();

            if (_settings.ConfiguredDevices.Count == 0)
            {
                NoDevicesTextBlock.Visibility = Visibility.Visible;
                return;
            }

            NoDevicesTextBlock.Visibility = Visibility.Collapsed;

            int totalCount = _settings.ConfiguredDevices.Count;
            for (int i = 0; i < totalCount; i++)
            {
                var conf = _settings.ConfiguredDevices[i];
                var activeMatch = activeDevices.FirstOrDefault(d =>
                    d.Id == conf.Id ||
                    (!string.IsNullOrWhiteSpace(conf.FullName) &&
                     string.Equals(d.FullName, conf.FullName, StringComparison.OrdinalIgnoreCase)));

                bool isActive = activeMatch != null;
                bool isDefault = activeMatch != null && currentDefault != null && activeMatch.Id == currentDefault.Id;
                Guid effectiveId = activeMatch?.Id ?? conf.Id;
                string fullName = !string.IsNullOrWhiteSpace(activeMatch?.FullName) ? activeMatch.FullName : conf.FullName;
                string interfaceName = !string.IsNullOrWhiteSpace(activeMatch?.InterfaceName) ? activeMatch.InterfaceName : conf.InterfaceName;
                string name = !string.IsNullOrWhiteSpace(activeMatch?.Name) ? activeMatch.Name : conf.Name;

                var card = CreateDeviceCard(effectiveId, fullName, interfaceName, name, isActive, isDefault, activeMatch, conf, i, totalCount);
                DevicesContainer.Children.Add(card);
            }
        }

        private UIElement CreateDeviceCard(Guid id, string fullName, string interfaceName, string name, bool isActive, bool isDefault, CoreAudioDevice? activeDevice, ConfiguredDevice? conf, int index, int totalCount)
        {
            var border = new Border
            {
                Style = (Style)Application.Current.Resources["FluentCardStyle"],
                Opacity = isActive ? 1.0 : 0.68,
                CanDrag = true,
                AllowDrop = true,
                IsTabStop = true
            };

            // Mouse Drag and Drop support
            border.DragStarting += (s, args) =>
            {
                args.Data.Properties["DeviceIndex"] = index;
                args.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
            };

            border.DragOver += (s, args) =>
            {
                args.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
                args.DragUIOverride.IsCaptionVisible = false;
                args.DragUIOverride.IsGlyphVisible = true;
            };

            border.Drop += (s, args) =>
            {
                if (args.DataView.Properties.TryGetValue("DeviceIndex", out var val) && val is int srcIdx)
                {
                    if (srcIdx != index && _settings.MoveDevice(srcIdx, index))
                    {
                        ReloadDevices();
                        App.CurrentApp.UpdateTrayState();
                    }
                }
            };

            // Keyboard Arrow key support when card has focus
            border.KeyDown += (s, args) =>
            {
                if (args.Key == Windows.System.VirtualKey.Up)
                {
                    if (index > 0 && _settings.MoveDeviceUp(index))
                    {
                        ReloadDevices();
                        App.CurrentApp.UpdateTrayState();
                        args.Handled = true;
                    }
                }
                else if (args.Key == Windows.System.VirtualKey.Down)
                {
                    if (index < totalCount - 1 && _settings.MoveDeviceDown(index))
                    {
                        ReloadDevices();
                        App.CurrentApp.UpdateTrayState();
                        args.Handled = true;
                    }
                }
            };

            var grid = new Grid
            {
                ColumnSpacing = 14
            };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Col 0: Reorder buttons
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Col 1: Icon
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Col 2: Info
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Col 3: Controls

            // 0. Reorder Buttons (Move Up & Move Down arrows)
            var reorderStack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Spacing = 2,
                VerticalAlignment = VerticalAlignment.Center
            };

            var upBtn = new Button
            {
                Content = new FontIcon { Glyph = "\uE70E", FontSize = 10 },
                Padding = new Thickness(6, 2, 6, 2),
                MinHeight = 0,
                MinWidth = 0,
                CornerRadius = new CornerRadius(3),
                IsEnabled = index > 0
            };
            ToolTipService.SetToolTip(upBtn, "Move up in cycle (arrow / mouse)");
            upBtn.Click += (s, e) =>
            {
                if (_settings.MoveDeviceUp(index))
                {
                    ReloadDevices();
                    App.CurrentApp.UpdateTrayState();
                }
            };

            var downBtn = new Button
            {
                Content = new FontIcon { Glyph = "\uE70D", FontSize = 10 },
                Padding = new Thickness(6, 2, 6, 2),
                MinHeight = 0,
                MinWidth = 0,
                CornerRadius = new CornerRadius(3),
                IsEnabled = index < totalCount - 1
            };
            ToolTipService.SetToolTip(downBtn, "Move down in cycle (arrow / mouse)");
            downBtn.Click += (s, e) =>
            {
                if (_settings.MoveDeviceDown(index))
                {
                    ReloadDevices();
                    App.CurrentApp.UpdateTrayState();
                }
            };

            reorderStack.Children.Add(upBtn);
            reorderStack.Children.Add(downBtn);
            Grid.SetColumn(reorderStack, 0);
            grid.Children.Add(reorderStack);

            // 1. Device Icon
            var icon = new FontIcon
            {
                Glyph = GetDeviceGlyph(fullName),
                FontSize = 20,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = isDefault
                    ? (Brush)Application.Current.Resources["SystemAccentColorBrush"]
                    : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]
            };
            Grid.SetColumn(icon, 1);
            grid.Children.Add(icon);

            // 2. Info Stack
            var infoStack = new StackPanel
            {
                Spacing = 2,
                VerticalAlignment = VerticalAlignment.Center
            };

            var titleRow = new Grid
            {
                ColumnSpacing = 8
            };
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            string displayName = !string.IsNullOrWhiteSpace(fullName) ? fullName : (!string.IsNullOrWhiteSpace(name) ? name : "Audio Device");
            var nameText = new TextBlock
            {
                Text = displayName,
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(nameText, 0);
            titleRow.Children.Add(nameText);

            if (isDefault)
            {
                var activeBadge = new Border
                {
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(35, 108, 203, 95)),
                    BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(110, 108, 203, 95)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 1, 6, 1),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = "Active",
                        FontSize = 10.5,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        Foreground = (Brush)Application.Current.Resources["ActiveStatusBrush"],
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };
                Grid.SetColumn(activeBadge, 1);
                titleRow.Children.Add(activeBadge);
            }
            else if (!isActive)
            {
                var disconnectedBadge = new Border
                {
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 255, 140, 0)),
                    BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(120, 255, 140, 0)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 1, 6, 1),
                    VerticalAlignment = VerticalAlignment.Center
                };
                var badgeContent = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4
                };
                badgeContent.Children.Add(new FontIcon
                {
                    Glyph = "\uE7BA",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 160, 20)),
                    VerticalAlignment = VerticalAlignment.Center
                });
                badgeContent.Children.Add(new TextBlock
                {
                    Text = "Disconnected",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 160, 20)),
                    VerticalAlignment = VerticalAlignment.Center
                });
                disconnectedBadge.Child = badgeContent;
                Grid.SetColumn(disconnectedBadge, 1);
                titleRow.Children.Add(disconnectedBadge);
            }

            infoStack.Children.Add(titleRow);

            var descText = new TextBlock
            {
                Text = !string.IsNullOrWhiteSpace(interfaceName) ? interfaceName : "Audio Endpoint",
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1
            };
            infoStack.Children.Add(descText);

            Grid.SetColumn(infoStack, 2);
            grid.Children.Add(infoStack);

            // 3. Right Controls: Optional Set Default / Remove + Quick Switch toggle + Mixer toggle + Label text box
            var controlsStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 14,
                VerticalAlignment = VerticalAlignment.Center
            };

            if (isActive && !isDefault && activeDevice != null)
            {
                var setDefaultBtn = new Button
                {
                    Content = "Set default",
                    FontSize = 11.5,
                    Padding = new Thickness(10, 4, 10, 5),
                    VerticalAlignment = VerticalAlignment.Center
                };
                setDefaultBtn.Click += (s, e) =>
                {
                    activeDevice.SetAsDefault();
                    App.CurrentApp.UpdateTrayState();
                    ReloadDevices();
                    App.CurrentApp.ShowOsd("Audio playback switched", activeDevice.FullName, GetDeviceGlyph(activeDevice.FullName));
                };
                controlsStack.Children.Add(setDefaultBtn);
            }
            else if (!isActive)
            {
                var removeBtn = new Button
                {
                    Content = new FontIcon { Glyph = "\uE74D", FontSize = 13 },
                    Padding = new Thickness(8, 6, 8, 6),
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(4),
                    VerticalAlignment = VerticalAlignment.Center
                };
                ToolTipService.SetToolTip(removeBtn, "Remove disconnected device");
                removeBtn.Click += (s, e) =>
                {
                    if (_settings.RemoveDevice(id, fullName))
                    {
                        ReloadDevices();
                        App.CurrentApp.UpdateTrayState();
                    }
                };
                controlsStack.Children.Add(removeBtn);
            }

            // Quick Switch Toggle
            bool isQuickSwitch = conf?.QuickSwitch ?? _settings.SelectedDeviceIds.Contains(id);
            var qsStack = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center };
            qsStack.Children.Add(new TextBlock { Text = "Switch", Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], HorizontalAlignment = HorizontalAlignment.Center });
            var qsToggle = new ToggleSwitch
            {
                IsOn = isQuickSwitch,
                OnContent = "",
                OffContent = "",
                MinWidth = 0
            };
            qsStack.Children.Add(qsToggle);
            controlsStack.Children.Add(qsStack);

            // Mixer Toggle
            bool isMixer = conf?.Mixer ?? _settings.MixerDeviceIds.Contains(id);
            var mxStack = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center };
            mxStack.Children.Add(new TextBlock { Text = "Mixer", Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], HorizontalAlignment = HorizontalAlignment.Center });
            var mxToggle = new ToggleSwitch
            {
                IsOn = isMixer,
                OnContent = "",
                OffContent = "",
                MinWidth = 0
            };
            mxStack.Children.Add(mxToggle);
            controlsStack.Children.Add(mxStack);

            // Label TextBox
            string curLabel = conf?.CustomLabel ?? _settings.GetLabelForDevice(id, fullName, index);
            var lblStack = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center };
            lblStack.Children.Add(new TextBlock { Text = "Badge", Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], HorizontalAlignment = HorizontalAlignment.Center });
            var lblBox = new TextBox
            {
                Text = curLabel,
                Width = 76,
                MaxLength = 6,
                PlaceholderText = $"AUX {index + 1}"
            };
            lblStack.Children.Add(lblBox);
            controlsStack.Children.Add(lblStack);

            // Event handlers for instant auto-save
            qsToggle.Toggled += (s, e) =>
            {
                if (_isInitializing) return;
                _settings.UpdateOrAddDevice(id, fullName, interfaceName, name, qsToggle.IsOn, mxToggle.IsOn, lblBox.Text);
                _settings.Save();
                App.CurrentApp.UpdateTrayState();
            };

            mxToggle.Toggled += (s, e) =>
            {
                if (_isInitializing) return;
                _settings.UpdateOrAddDevice(id, fullName, interfaceName, name, qsToggle.IsOn, mxToggle.IsOn, lblBox.Text);
                _settings.Save();
                App.CurrentApp.UpdateTrayState();
            };

            lblBox.TextChanged += (s, e) =>
            {
                if (_isInitializing) return;
                _settings.UpdateOrAddDevice(id, fullName, interfaceName, name, qsToggle.IsOn, mxToggle.IsOn, lblBox.Text);
                _settings.Save();
                App.CurrentApp.UpdateTrayState();
            };

            Grid.SetColumn(controlsStack, 3);
            grid.Children.Add(controlsStack);

            border.Child = grid;
            return border;
        }

        private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing) return;

            if (ThemeCombo.SelectedItem is ComboBoxItem cbi && cbi.Tag is string theme)
            {
                _settings.Theme = theme;
                _settings.Save();
                ApplyTheme(theme);
            }
        }

        private void BackdropCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing) return;

            if (BackdropCombo.SelectedItem is ComboBoxItem cbi && cbi.Tag is string backdrop)
            {
                _settings.Backdrop = backdrop;
                _settings.Save();
                ApplyBackdrop(backdrop);
            }
        }

        private void StartupToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            _settings.RunAtStartup = StartupToggle.IsOn;
            _settings.Save();
            StartupManager.UpdateStartup(_settings.RunAtStartup);
        }

        private void StartMinimizedToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            _settings.StartMinimized = StartMinimizedToggle.IsOn;
            _settings.Save();
        }

        private void HotkeysToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            _settings.EnableGlobalHotkeys = HotkeysToggle.IsOn;
            _settings.Save();
            App.CurrentApp.HotkeyManager.RegisterHotkeys(_settings.EnableGlobalHotkeys, _settings.QuickSwitchHotkey, _settings.OpenMixerHotkey);
        }

        private void TrayScrollToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            _settings.EnableTrayScrollVolume = TrayScrollToggle.IsOn;
            _settings.Save();
            App.CurrentApp.TrayManager.EnableScrollVolume = _settings.EnableTrayScrollVolume;
        }

        private void ScrollStepCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing) return;
            if (ScrollStepCombo.SelectedItem is ComboBoxItem cbi && int.TryParse(cbi.Tag?.ToString(), out int val))
            {
                _settings.ScrollVolumeStep = val;
                _settings.Save();
            }
        }

        private void OsdDurationCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing) return;
            if (OsdDurationCombo.SelectedItem is ComboBoxItem cbi && int.TryParse(cbi.Tag?.ToString(), out int val))
            {
                _settings.OsdDurationMs = val;
                _settings.Save();
            }
        }

        private async void QuickSwitchHotkeyButton_Click(object sender, RoutedEventArgs e)
        {
            await RecordShortcutAsync("Quick switch shortcut", _settings.QuickSwitchHotkey, (newHotkey) =>
            {
                _settings.QuickSwitchHotkey = newHotkey;
                if (!_settings.EnableGlobalHotkeys)
                {
                    _settings.EnableGlobalHotkeys = true;
                    HotkeysToggle.IsOn = true;
                }
                _settings.Save();
                QuickSwitchHotkeyButton.Content = newHotkey;
                App.CurrentApp.HotkeyManager.RegisterHotkeys(_settings.EnableGlobalHotkeys, _settings.QuickSwitchHotkey, _settings.OpenMixerHotkey);
            });
        }

        private async void OpenMixerHotkeyButton_Click(object sender, RoutedEventArgs e)
        {
            await RecordShortcutAsync("Open volume mixer shortcut", _settings.OpenMixerHotkey, (newHotkey) =>
            {
                _settings.OpenMixerHotkey = newHotkey;
                if (!_settings.EnableGlobalHotkeys)
                {
                    _settings.EnableGlobalHotkeys = true;
                    HotkeysToggle.IsOn = true;
                }
                _settings.Save();
                OpenMixerHotkeyButton.Content = newHotkey;
                App.CurrentApp.HotkeyManager.RegisterHotkeys(_settings.EnableGlobalHotkeys, _settings.QuickSwitchHotkey, _settings.OpenMixerHotkey);
            });
        }

        private async System.Threading.Tasks.Task RecordShortcutAsync(string title, string current, Action<string> onConfirmed)
        {
            var recordedKeys = new List<string>();
            uint recordedMods = 0;
            uint recordedVk = 0;

            var instructionText = new TextBlock
            {
                Text = "Press up to 3 keys on your keyboard for this combination.",
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            };

            var keysPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var placeholderPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var kbIcon = new FontIcon
            {
                Glyph = "\uE765",
                FontSize = 18,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            };

            var placeholderText = new TextBlock
            {
                Text = "Press shortcut keys on keyboard...",
                FontSize = 13.5,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                VerticalAlignment = VerticalAlignment.Center
            };
            placeholderPanel.Children.Add(kbIcon);
            placeholderPanel.Children.Add(placeholderText);

            var recorderBox = new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1.5),
                BorderBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
                Background = (Brush)Application.Current.Resources["LayerOnMicaBaseAltFillColorDefaultBrush"],
                Padding = new Thickness(16, 12, 16, 12),
                MinHeight = 56,
                Margin = new Thickness(0, 8, 0, 8),
                Child = placeholderPanel
            };

            var statusText = new TextBlock
            {
                Text = "Keys: 0 / 3 (Press modifier keys like Ctrl, Shift, Alt, then a key)",
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var clearBtn = new Button
            {
                Content = "Clear / Re-record",
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0)
            };

            var contentStack = new StackPanel
            {
                Spacing = 8,
                Children = { instructionText, recorderBox, statusText, clearBtn }
            };

            var dialog = new ContentDialog
            {
                Title = title,
                Content = contentStack,
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                IsPrimaryButtonEnabled = false,
                XamlRoot = this.Content.XamlRoot
            };

            void UpdateRecordedUi()
            {
                keysPanel.Children.Clear();
                if (recordedKeys.Count == 0)
                {
                    recorderBox.Child = placeholderPanel;
                    statusText.Text = "Keys: 0 / 3 (Press modifier keys like Ctrl, Shift, Alt, then a key)";
                    dialog.IsPrimaryButtonEnabled = false;
                    return;
                }

                for (int i = 0; i < recordedKeys.Count; i++)
                {
                    if (i > 0)
                    {
                        keysPanel.Children.Add(new TextBlock
                        {
                            Text = "+",
                            FontSize = 13,
                            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                            VerticalAlignment = VerticalAlignment.Center
                        });
                    }

                    var keyBorder = new Border
                    {
                        Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
                        BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(10, 4, 10, 5)
                    };
                    keyBorder.Child = new TextBlock
                    {
                        Text = recordedKeys[i],
                        FontSize = 13,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]
                    };
                    keysPanel.Children.Add(keyBorder);
                }

                recorderBox.Child = keysPanel;

                bool hasPrimary = recordedVk != 0;
                bool hasMod = recordedMods != 0;
                bool isFuncKey = recordedVk >= 0x70 && recordedVk <= 0x87;
                bool isValid = hasPrimary && (hasMod || isFuncKey);

                if (isValid)
                {
                    dialog.IsPrimaryButtonEnabled = true;
                    statusText.Text = recordedKeys.Count == 3
                        ? "Shortcut complete! (3 / 3 keys maximum reached)"
                        : $"Shortcut complete! ({recordedKeys.Count} / 3 keys) - Click Save to apply.";
                }
                else
                {
                    dialog.IsPrimaryButtonEnabled = false;
                    statusText.Text = $"Recording: {recordedKeys.Count} / 3 keys - Now press a primary key (letter, number, or F-key)...";
                }
            }

            clearBtn.Click += (s, e) =>
            {
                recordedKeys.Clear();
                recordedMods = 0;
                recordedVk = 0;
                UpdateRecordedUi();
            };

            IntPtr hookId = IntPtr.Zero;
            LowLevelKeyboardProc hookProc = (nCode, wParam, lParam) =>
            {
                if (nCode >= 0)
                {
                    int msg = wParam.ToInt32();
                    if (msg == 0x0100 /* WM_KEYDOWN */ || msg == 0x0104 /* WM_SYSKEYDOWN */)
                    {
                        try
                        {
                            uint vk = (uint)Marshal.ReadInt32(lParam);

                            if (vk == 0x1B && recordedKeys.Count == 0)
                            {
                                return CallNextHookEx(hookId, nCode, wParam, lParam);
                            }

                            if (vk == 0x08)
                            {
                                DispatcherQueue.TryEnqueue(() =>
                                {
                                    recordedKeys.Clear();
                                    recordedMods = 0;
                                    recordedVk = 0;
                                    UpdateRecordedUi();
                                });
                                return (IntPtr)1;
                            }

                            var (keyName, modFlag, isMod) = MapVkToKey(vk);
                            if (!string.IsNullOrEmpty(keyName))
                            {
                                DispatcherQueue.TryEnqueue(() =>
                                {
                                    if (recordedKeys.Count >= 3)
                                    {
                                        return;
                                    }

                                    if (recordedKeys.Contains(keyName))
                                    {
                                        return;
                                    }

                                    if (isMod)
                                    {
                                        if (recordedVk == 0 && recordedKeys.Count < 3)
                                        {
                                            recordedKeys.Add(keyName);
                                            recordedMods |= modFlag;
                                            UpdateRecordedUi();
                                        }
                                    }
                                    else
                                    {
                                        if (recordedVk == 0 && recordedKeys.Count < 3)
                                        {
                                            recordedKeys.Add(keyName);
                                            recordedVk = vk;
                                            UpdateRecordedUi();
                                        }
                                    }
                                });
                                return (IntPtr)1;
                            }
                        }
                        catch { }
                    }
                }
                return CallNextHookEx(hookId, nCode, wParam, lParam);
            };

            try
            {
                IntPtr hMod = GetModuleHandle(null);
                hookId = SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, hookProc, hMod, 0);
                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary && recordedKeys.Count > 0)
                {
                    string combined = string.Join("+", recordedKeys);
                    onConfirmed(combined);
                }
            }
            finally
            {
                if (hookId != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(hookId);
                    hookId = IntPtr.Zero;
                }
            }
        }

        private static (string name, uint mod, bool isModifier) MapVkToKey(uint vk)
        {
            switch (vk)
            {
                case 0x11: // VK_CONTROL
                case 0xA2: // VK_LCONTROL
                case 0xA3: // VK_RCONTROL
                    return ("Ctrl", 0x0002, true);

                case 0x12: // VK_MENU
                case 0xA4: // VK_LMENU
                case 0xA5: // VK_RMENU
                    return ("Alt", 0x0001, true);

                case 0x10: // VK_SHIFT
                case 0xA0: // VK_LSHIFT
                case 0xA1: // VK_RSHIFT
                    return ("Shift", 0x0004, true);

                case 0x5B: // VK_LWIN
                case 0x5C: // VK_RWIN
                    return ("Win", 0x0008, true);

                case uint f when f >= 0x70 && f <= 0x87:
                    return ($"F{f - 0x70 + 1}", 0, false);

                case uint c when c >= 0x41 && c <= 0x5A:
                    return (((char)c).ToString(), 0, false);

                case uint n when n >= 0x30 && n <= 0x39:
                    return (((char)n).ToString(), 0, false);

                case uint np when np >= 0x60 && np <= 0x69:
                    return ($"Num{np - 0x60}", 0, false);

                case 0x20: return ("Space", 0, false);
                case 0x09: return ("Tab", 0, false);
                case 0x0D: return ("Enter", 0, false);
                case 0x21: return ("PageUp", 0, false);
                case 0x22: return ("PageDown", 0, false);
                case 0x23: return ("End", 0, false);
                case 0x24: return ("Home", 0, false);
                case 0x25: return ("Left", 0, false);
                case 0x26: return ("Up", 0, false);
                case 0x27: return ("Right", 0, false);
                case 0x28: return ("Down", 0, false);
                case 0x2D: return ("Insert", 0, false);
                case 0x2E: return ("Delete", 0, false);

                case 0xBA: return (";", 0, false);
                case 0xBB: return ("=", 0, false);
                case 0xBC: return (",", 0, false);
                case 0xBD: return ("-", 0, false);
                case 0xBE: return (".", 0, false);
                case 0xBF: return ("/", 0, false);
                case 0xC0: return ("`", 0, false);
                case 0xDB: return ("[", 0, false);
                case 0xDC: return ("\\", 0, false);
                case 0xDD: return ("]", 0, false);
                case 0xDE: return ("'", 0, false);

                default:
                    return ("", 0, false);
            }
        }

        private async void CheckUpdatesSilent()
        {
            try
            {
                _pendingUpdate = await UpdateChecker.CheckForUpdatesAsync();
                if (_pendingUpdate != null)
                {
                    UpdateBadgeButton.Visibility = Visibility.Visible;
                }
            }
            catch { }
        }

        private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
        {
            var btn = (Button)sender;
            btn.IsEnabled = false;
            try
            {
                _pendingUpdate = await UpdateChecker.CheckForUpdatesAsync();
                if (_pendingUpdate != null)
                {
                    UpdateBadgeButton.Visibility = Visibility.Visible;
                    await ShowUpdatePromptDialogAsync(_pendingUpdate);
                }
                else
                {
                    var dialog = new ContentDialog
                    {
                        Title = "Up to Date",
                        Content = $"You are using the latest version of Audio Device Switcher (v{UpdateChecker.GetCurrentVersion()}).",
                        CloseButtonText = "OK",
                        XamlRoot = this.Content.XamlRoot
                    };
                    await dialog.ShowAsync();
                }
            }
            finally
            {
                btn.IsEnabled = true;
            }
        }

        private async void UpdateBadgeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_pendingUpdate != null)
            {
                await ShowUpdatePromptDialogAsync(_pendingUpdate);
            }
            else
            {
                CheckUpdates_Click(sender, e);
            }
        }

        private async Task ShowUpdatePromptDialogAsync(UpdateInfo info)
        {
            var stack = new StackPanel { Spacing = 12 };
            var introText = new TextBlock
            {
                Text = $"Version {info.VersionTag} is available! Would you like to automatically download and install it now?",
                TextWrapping = TextWrapping.Wrap,
                Style = (Style)Application.Current.Resources["BodyTextBlockStyle"]
            };
            stack.Children.Add(introText);

            if (!string.IsNullOrWhiteSpace(info.ReleaseNotes))
            {
                var notesExpander = new Expander
                {
                    Header = "Release notes",
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Content = new ScrollViewer
                    {
                        MaxHeight = 140,
                        Content = new TextBlock
                        {
                            Text = info.ReleaseNotes,
                            TextWrapping = TextWrapping.Wrap,
                            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"]
                        }
                    }
                };
                stack.Children.Add(notesExpander);
            }

            var dialog = new ContentDialog
            {
                Title = $"Update Available ({info.VersionTag})",
                Content = stack,
                PrimaryButtonText = "Update Automatically",
                SecondaryButtonText = "View on GitHub",
                CloseButtonText = "Later",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.Content.XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                await PerformAutomaticUpdateAsync(info);
            }
            else if (result == ContentDialogResult.Secondary)
            {
                if (!string.IsNullOrEmpty(info.ReleaseUrl))
                {
                    Process.Start(new ProcessStartInfo { FileName = info.ReleaseUrl, UseShellExecute = true });
                }
            }
        }

        private async Task PerformAutomaticUpdateAsync(UpdateInfo info)
        {
            var progressStack = new StackPanel { Spacing = 12 };
            var statusText = new TextBlock
            {
                Text = "Connecting to server...",
                Style = (Style)Application.Current.Resources["BodyTextBlockStyle"]
            };
            var pBar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                IsIndeterminate = true,
                Height = 8,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var percentText = new TextBlock
            {
                Text = "Starting download...",
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            };

            progressStack.Children.Add(statusText);
            progressStack.Children.Add(pBar);
            progressStack.Children.Add(percentText);

            var progressDialog = new ContentDialog
            {
                Title = $"Updating to {info.VersionTag}",
                Content = progressStack,
                CloseButtonText = "Cancel",
                XamlRoot = this.Content.XamlRoot
            };

            var cts = new System.Threading.CancellationTokenSource();
            progressDialog.CloseButtonClick += (s, e) =>
            {
                cts.Cancel();
            };

            var progress = new Progress<double>(pct =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    pBar.IsIndeterminate = false;
                    pBar.Value = pct;
                    statusText.Text = "Downloading update package...";
                    percentText.Text = $"{pct:F0}% downloaded";
                    if (pct >= 99.9)
                    {
                        statusText.Text = "Installing update & restarting...";
                        percentText.Text = "Please wait a moment...";
                    }
                });
            });

            var updateTask = Task.Run(async () =>
            {
                await UpdateChecker.DownloadAndInstallUpdateAsync(info, progress, cts.Token);
            });

            var dialogTask = progressDialog.ShowAsync().AsTask();
            var completedTask = await Task.WhenAny(updateTask, dialogTask);

            if (completedTask == updateTask)
            {
                try
                {
                    await updateTask;
                }
                catch (Exception ex)
                {
                    progressDialog.Hide();
                    if (!cts.IsCancellationRequested)
                    {
                        var errDialog = new ContentDialog
                        {
                            Title = "Update Failed",
                            Content = $"Failed to install update: {ex.Message}\n\nWould you like to download it manually from GitHub?",
                            PrimaryButtonText = "Open GitHub",
                            CloseButtonText = "Close",
                            XamlRoot = this.Content.XamlRoot
                        };
                        if (await errDialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrEmpty(info.ReleaseUrl))
                        {
                            Process.Start(new ProcessStartInfo { FileName = info.ReleaseUrl, UseShellExecute = true });
                        }
                    }
                }
            }
        }
    }
}
