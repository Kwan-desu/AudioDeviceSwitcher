using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AudioSwitcher.AudioApi.CoreAudio;

namespace AudioDeviceSwitcher
{
    public partial class SettingsWindow : Window
    {
        private AppSettings _settings;
        private AudioDeviceManager _audioManager;
        
        // Tracking checkboxes
        private Dictionary<Guid, System.Windows.Controls.CheckBox> _quickSwitchChecks = new Dictionary<Guid, System.Windows.Controls.CheckBox>();
        private Dictionary<Guid, System.Windows.Controls.CheckBox> _mixerChecks = new Dictionary<Guid, System.Windows.Controls.CheckBox>();

        public SettingsWindow(AppSettings settings, AudioDeviceManager audioManager)
        {
            InitializeComponent();
            _settings = settings;
            _audioManager = audioManager;
            
            LoadSettings();
            CheckUpdates();
        }

        private void LoadSettings()
        {
            RunAtStartupCheck.IsChecked = _settings.RunAtStartup;
            EnableHotkeysCheck.IsChecked = _settings.EnableGlobalHotkeys;

            var devices = _audioManager.GetActivePlaybackDevices();

            foreach (var device in devices)
            {
                var panel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };

                var quickSwitchCheck = new System.Windows.Controls.CheckBox
                {
                    Content = "Quick Switch",
                    IsChecked = _settings.SelectedDeviceIds.Contains(device.Id),
                    Foreground = System.Windows.Media.Brushes.White,
                    Width = 100,
                    VerticalAlignment = VerticalAlignment.Center
                };
                _quickSwitchChecks[device.Id] = quickSwitchCheck;

                var mixerCheck = new System.Windows.Controls.CheckBox
                {
                    Content = "Mixer",
                    IsChecked = _settings.MixerDeviceIds.Contains(device.Id) || _settings.SelectedDeviceIds.Contains(device.Id),
                    Foreground = System.Windows.Media.Brushes.White,
                    Width = 80,
                    VerticalAlignment = VerticalAlignment.Center
                };
                _mixerChecks[device.Id] = mixerCheck;

                var nameText = new TextBlock
                {
                    Text = device.FullName,
                    Foreground = System.Windows.Media.Brushes.White,
                    VerticalAlignment = VerticalAlignment.Center
                };

                panel.Children.Add(quickSwitchCheck);
                panel.Children.Add(mixerCheck);
                panel.Children.Add(nameText);

                DevicesPanel.Children.Add(panel);
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            _settings.RunAtStartup = RunAtStartupCheck.IsChecked ?? false;
            _settings.EnableGlobalHotkeys = EnableHotkeysCheck.IsChecked ?? false;

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
