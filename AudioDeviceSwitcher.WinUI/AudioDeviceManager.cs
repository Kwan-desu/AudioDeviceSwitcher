using System;
using System.Collections.Generic;
using System.Linq;
using AudioSwitcher.AudioApi;
using AudioSwitcher.AudioApi.CoreAudio;
using AudioSwitcher.AudioApi.Observables;

namespace AudioDeviceSwitcher
{
    public enum SwitchStatus
    {
        Success,
        NeedMoreDevicesConfigured,
        TargetDeviceDisconnected,
        Failed
    }

    public class SwitchResult
    {
        public SwitchStatus Status { get; set; }
        public CoreAudioDevice? SwitchedToDevice { get; set; }
        public string? DisconnectedDeviceName { get; set; }
    }

    public class AudioDeviceManager : IDisposable
    {
        private readonly CoreAudioController _controller;
        private readonly IDisposable? _deviceChangeSubscription;

        public event Action? DevicesChanged;
        public event Action<CoreAudioDevice>? DefaultDeviceChanged;

        public AudioDeviceManager()
        {
            _controller = new CoreAudioController();

            try
            {
                _deviceChangeSubscription = _controller.AudioDeviceChanged.Subscribe(OnAudioDeviceChanged);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AudioDeviceManager] Subscription failed: {ex.Message}");
            }
        }

        private void OnAudioDeviceChanged(DeviceChangedArgs args)
        {
            try
            {
                if (args is DefaultDeviceChangedArgs defArgs && defArgs.Device is CoreAudioDevice dev)
                {
                    DefaultDeviceChanged?.Invoke(dev);
                }
                DevicesChanged?.Invoke();
            }
            catch { }
        }

        public List<CoreAudioDevice> GetActivePlaybackDevices()
        {
            try
            {
                return _controller.GetPlaybackDevices(DeviceState.Active).ToList();
            }
            catch
            {
                return new List<CoreAudioDevice>();
            }
        }

        public CoreAudioDevice? GetDefaultPlaybackDevice()
        {
            try
            {
                return _controller.DefaultPlaybackDevice;
            }
            catch
            {
                return null;
            }
        }

        public SwitchResult SwitchToNextDevice(AppSettings settings)
        {
            if (settings == null)
            {
                return new SwitchResult { Status = SwitchStatus.NeedMoreDevicesConfigured };
            }

            var activeDevices = GetActivePlaybackDevices();
            settings.SyncActiveDevices(activeDevices);

            var configuredQuickSwitch = settings.ConfiguredDevices
                .Where(d => d.QuickSwitch)
                .ToList();

            if (configuredQuickSwitch.Count == 0 && settings.SelectedDeviceIds.Count > 0)
            {
                foreach (var gid in settings.SelectedDeviceIds)
                {
                    configuredQuickSwitch.Add(new ConfiguredDevice { Id = gid, QuickSwitch = true });
                }
            }

            if (configuredQuickSwitch.Count < 2)
            {
                return new SwitchResult { Status = SwitchStatus.NeedMoreDevicesConfigured };
            }

            // Map configured quick-switch entries to active devices
            var switchableDevices = new List<CoreAudioDevice>();
            foreach (var conf in configuredQuickSwitch)
            {
                var match = activeDevices.FirstOrDefault(d =>
                    d.Id == conf.Id ||
                    (!string.IsNullOrWhiteSpace(conf.FullName) &&
                     string.Equals(d.FullName, conf.FullName, StringComparison.OrdinalIgnoreCase)));

                if (match != null && !switchableDevices.Any(d => d.Id == match.Id))
                {
                    switchableDevices.Add(match);
                }
            }

            if (switchableDevices.Count < 2)
            {
                var missing = configuredQuickSwitch.FirstOrDefault(conf =>
                    !switchableDevices.Any(d =>
                        d.Id == conf.Id ||
                        (!string.IsNullOrWhiteSpace(conf.FullName) &&
                         string.Equals(d.FullName, conf.FullName, StringComparison.OrdinalIgnoreCase))));

                string missingName = !string.IsNullOrWhiteSpace(missing?.FullName)
                    ? missing.FullName
                    : (!string.IsNullOrWhiteSpace(missing?.CustomLabel) ? missing.CustomLabel : "Earphone / Other device");

                return new SwitchResult
                {
                    Status = SwitchStatus.TargetDeviceDisconnected,
                    DisconnectedDeviceName = missingName
                };
            }

            var currentDefaultId = GetDefaultPlaybackDevice()?.Id;
            int currentIndex = switchableDevices.FindIndex(d => d.Id == currentDefaultId);
            int nextIndex = (currentIndex + 1) % switchableDevices.Count;

            var nextDevice = switchableDevices[nextIndex];
            bool ok = nextDevice.SetAsDefault();

            return new SwitchResult
            {
                Status = ok ? SwitchStatus.Success : SwitchStatus.Failed,
                SwitchedToDevice = ok ? nextDevice : null
            };
        }

        public void SwitchToNextDevice(List<Guid> selectedDeviceIds)
        {
            if (selectedDeviceIds == null || selectedDeviceIds.Count < 2)
            {
                return;
            }

            var activeDevices = GetActivePlaybackDevices();
            var currentDefaultId = GetDefaultPlaybackDevice()?.Id;

            var switchableDevices = activeDevices
                .Where(d => selectedDeviceIds.Contains(d.Id))
                .ToList();

            if (switchableDevices.Count < 2)
            {
                return;
            }

            int currentIndex = switchableDevices.FindIndex(d => d.Id == currentDefaultId);
            int nextIndex = (currentIndex + 1) % switchableDevices.Count;

            var nextDevice = switchableDevices[nextIndex];
            nextDevice.SetAsDefault();
        }

        public void Dispose()
        {
            _deviceChangeSubscription?.Dispose();
            _controller?.Dispose();
        }
    }
}
