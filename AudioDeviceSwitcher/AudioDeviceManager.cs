using System;
using System.Collections.Generic;
using System.Linq;
using AudioSwitcher.AudioApi;
using AudioSwitcher.AudioApi.CoreAudio;

namespace AudioDeviceSwitcher
{
    public class AudioDeviceManager
    {
        private CoreAudioController _controller;

        public AudioDeviceManager()
        {
            _controller = new CoreAudioController();
        }

        public List<CoreAudioDevice> GetActivePlaybackDevices()
        {
            return _controller.GetPlaybackDevices(DeviceState.Active).ToList();
        }

        public CoreAudioDevice GetDefaultPlaybackDevice()
        {
            return _controller.DefaultPlaybackDevice;
        }

        public void SwitchToNextDevice(List<Guid> selectedDeviceIds)
        {
            if (selectedDeviceIds == null || selectedDeviceIds.Count < 2)
            {
                return; // Need at least 2 devices to switch between
            }

            var activeDevices = GetActivePlaybackDevices();
            var currentDefaultId = GetDefaultPlaybackDevice()?.Id;

            // Filter active devices to only those in the user's selected list
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
    }
}
