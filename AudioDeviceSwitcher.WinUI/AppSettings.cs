using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AudioSwitcher.AudioApi.CoreAudio;

namespace AudioDeviceSwitcher
{
    public class ConfiguredDevice
    {
        public Guid Id { get; set; }
        public string FullName { get; set; } = string.Empty;
        public string InterfaceName { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string CustomLabel { get; set; } = string.Empty;
        public bool QuickSwitch { get; set; }
        public bool Mixer { get; set; }
    }

    public class AppSettings
    {
        public List<ConfiguredDevice> ConfiguredDevices { get; set; } = new List<ConfiguredDevice>();
        public List<Guid> SelectedDeviceIds { get; set; } = new List<Guid>();
        public List<Guid> MixerDeviceIds { get; set; } = new List<Guid>();
        public Dictionary<string, string> DeviceLabels { get; set; } = new Dictionary<string, string>();

        public bool RunAtStartup { get; set; } = false;
        public bool StartMinimized { get; set; } = false;
        public bool EnableGlobalHotkeys { get; set; } = true;
        public bool EnableTrayScrollVolume { get; set; } = true;
        public int OsdDurationMs { get; set; } = 1500;
        public int ScrollVolumeStep { get; set; } = 2;

        public string QuickSwitchHotkey { get; set; } = "Ctrl+Shift+S";
        public string OpenMixerHotkey { get; set; } = "Ctrl+Shift+M";

        // Theme preference: "System" (default), "Light", or "Dark".
        public string Theme { get; set; } = "System";

        // Backdrop preference: "Mica" (default), "MicaAlt", or "Acrylic"
        public string Backdrop { get; set; } = "Mica";

        private static string GetConfigPath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string folder = Path.Combine(appData, "AudioDeviceSwitcher");
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }
            return Path.Combine(folder, "config.json");
        }

        public static AppSettings Load()
        {
            string path = GetConfigPath();
            AppSettings settings;
            if (File.Exists(path))
            {
                try
                {
                    string json = File.ReadAllText(path);
                    settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                }
                catch
                {
                    settings = new AppSettings();
                }
            }
            else
            {
                settings = new AppSettings();
            }

            settings.EnsureMigrated();
            return settings;
        }

        public void EnsureMigrated()
        {
            ConfiguredDevices ??= new List<ConfiguredDevice>();

            // If ConfiguredDevices is empty, migrate from legacy collections
            if (ConfiguredDevices.Count == 0)
            {
                var allIds = new HashSet<Guid>(SelectedDeviceIds);
                foreach (var id in MixerDeviceIds) allIds.Add(id);
                foreach (var key in DeviceLabels.Keys)
                {
                    if (Guid.TryParse(key, out var gid)) allIds.Add(gid);
                }

                foreach (var id in allIds)
                {
                    string label = DeviceLabels.TryGetValue(id.ToString(), out var l) ? l : "";
                    ConfiguredDevices.Add(new ConfiguredDevice
                    {
                        Id = id,
                        QuickSwitch = SelectedDeviceIds.Contains(id),
                        Mixer = MixerDeviceIds.Contains(id),
                        CustomLabel = label
                    });
                }
            }
            else
            {
                ConfiguredDevices.RemoveAll(d => string.IsNullOrWhiteSpace(d.FullName) && string.IsNullOrWhiteSpace(d.Name));
                SyncLegacyCollections();
            }
        }

        public void SyncLegacyCollections()
        {
            SelectedDeviceIds.Clear();
            MixerDeviceIds.Clear();

            foreach (var dev in ConfiguredDevices)
            {
                if (dev.QuickSwitch && !SelectedDeviceIds.Contains(dev.Id))
                {
                    SelectedDeviceIds.Add(dev.Id);
                }
                if (dev.Mixer && !MixerDeviceIds.Contains(dev.Id))
                {
                    MixerDeviceIds.Add(dev.Id);
                }
                if (!string.IsNullOrWhiteSpace(dev.CustomLabel))
                {
                    DeviceLabels[dev.Id.ToString()] = dev.CustomLabel.Trim();
                }
            }
        }

        public void Save()
        {
            SyncLegacyCollections();
            string path = GetConfigPath();
            string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }

        public bool SyncActiveDevices(IEnumerable<CoreAudioDevice> activeDevices)
        {
            if (activeDevices == null) return false;
            bool changed = false;

            foreach (var activeDev in activeDevices)
            {
                if (activeDev == null) continue;

                // 1. Exact GUID match
                var match = ConfiguredDevices.FirstOrDefault(c => c.Id == activeDev.Id);

                // 2. FullName match (case-insensitive)
                if (match == null && !string.IsNullOrWhiteSpace(activeDev.FullName))
                {
                    match = ConfiguredDevices.FirstOrDefault(c =>
                        !string.IsNullOrWhiteSpace(c.FullName) &&
                        string.Equals(c.FullName, activeDev.FullName, StringComparison.OrdinalIgnoreCase));
                }

                // 3. InterfaceName + Name match
                if (match == null && !string.IsNullOrWhiteSpace(activeDev.InterfaceName))
                {
                    match = ConfiguredDevices.FirstOrDefault(c =>
                        !string.IsNullOrWhiteSpace(c.InterfaceName) &&
                        string.Equals(c.InterfaceName, activeDev.InterfaceName, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(c.Name, activeDev.Name, StringComparison.OrdinalIgnoreCase));
                }

                if (match != null)
                {
                    // If device reconnected with a different endpoint GUID, update it
                    if (match.Id != activeDev.Id)
                    {
                        Guid oldId = match.Id;
                        match.Id = activeDev.Id;

                        int selIdx = SelectedDeviceIds.IndexOf(oldId);
                        if (selIdx >= 0)
                        {
                            SelectedDeviceIds[selIdx] = activeDev.Id;
                        }
                        else if (match.QuickSwitch && !SelectedDeviceIds.Contains(activeDev.Id))
                        {
                            SelectedDeviceIds.Add(activeDev.Id);
                        }

                        int mixIdx = MixerDeviceIds.IndexOf(oldId);
                        if (mixIdx >= 0)
                        {
                            MixerDeviceIds[mixIdx] = activeDev.Id;
                        }
                        else if (match.Mixer && !MixerDeviceIds.Contains(activeDev.Id))
                        {
                            MixerDeviceIds.Add(activeDev.Id);
                        }

                        if (DeviceLabels.Remove(oldId.ToString(), out var oldLabel) && string.IsNullOrWhiteSpace(match.CustomLabel))
                        {
                            match.CustomLabel = oldLabel;
                        }
                        if (!string.IsNullOrWhiteSpace(match.CustomLabel))
                        {
                            DeviceLabels[activeDev.Id.ToString()] = match.CustomLabel;
                        }

                        changed = true;
                    }

                    // Populate descriptors if missing or changed
                    if (string.IsNullOrWhiteSpace(match.FullName) && !string.IsNullOrWhiteSpace(activeDev.FullName))
                    {
                        match.FullName = activeDev.FullName;
                        changed = true;
                    }
                    if (string.IsNullOrWhiteSpace(match.InterfaceName) && !string.IsNullOrWhiteSpace(activeDev.InterfaceName))
                    {
                        match.InterfaceName = activeDev.InterfaceName;
                        changed = true;
                    }
                    if (string.IsNullOrWhiteSpace(match.Name) && !string.IsNullOrWhiteSpace(activeDev.Name))
                    {
                        match.Name = activeDev.Name;
                        changed = true;
                    }
                }
                else
                {
                    var newConf = new ConfiguredDevice
                    {
                        Id = activeDev.Id,
                        FullName = activeDev.FullName ?? string.Empty,
                        InterfaceName = activeDev.InterfaceName ?? string.Empty,
                        Name = activeDev.Name ?? string.Empty,
                        QuickSwitch = true,
                        Mixer = true,
                        CustomLabel = string.Empty
                    };
                    ConfiguredDevices.Add(newConf);
                    changed = true;
                }
            }

            if (changed)
            {
                Save();
            }

            return changed;
        }

        public bool MoveDevice(int fromIndex, int toIndex)
        {
            if (fromIndex < 0 || fromIndex >= ConfiguredDevices.Count ||
                toIndex < 0 || toIndex >= ConfiguredDevices.Count ||
                fromIndex == toIndex)
            {
                return false;
            }

            var item = ConfiguredDevices[fromIndex];
            ConfiguredDevices.RemoveAt(fromIndex);
            ConfiguredDevices.Insert(toIndex, item);

            SyncLegacyCollections();
            Save();
            return true;
        }

        public bool MoveDeviceUp(int index)
        {
            return MoveDevice(index, index - 1);
        }

        public bool MoveDeviceDown(int index)
        {
            return MoveDevice(index, index + 1);
        }

        public void UpdateOrAddDevice(Guid id, string fullName, string interfaceName, string name, bool quickSwitch, bool mixer, string customLabel)
        {
            var match = ConfiguredDevices.FirstOrDefault(c =>
                c.Id == id ||
                (!string.IsNullOrWhiteSpace(fullName) && !string.IsNullOrWhiteSpace(c.FullName) &&
                 string.Equals(c.FullName, fullName, StringComparison.OrdinalIgnoreCase)));

            if (match == null)
            {
                match = new ConfiguredDevice
                {
                    Id = id,
                    FullName = fullName,
                    InterfaceName = interfaceName,
                    Name = name,
                    QuickSwitch = quickSwitch,
                    Mixer = mixer,
                    CustomLabel = customLabel
                };
                ConfiguredDevices.Add(match);
            }
            else
            {
                match.Id = id;
                if (!string.IsNullOrWhiteSpace(fullName)) match.FullName = fullName;
                if (!string.IsNullOrWhiteSpace(interfaceName)) match.InterfaceName = interfaceName;
                if (!string.IsNullOrWhiteSpace(name)) match.Name = name;
                match.QuickSwitch = quickSwitch;
                match.Mixer = mixer;
                match.CustomLabel = customLabel;
            }
        }

        public string GetLabelForDevice(Guid deviceId, int fallbackIndex)
        {
            return GetLabelForDevice(deviceId, null, fallbackIndex);
        }

        public string GetLabelForDevice(Guid deviceId, string? fullName, int fallbackIndex)
        {
            string key = deviceId.ToString();
            if (DeviceLabels.TryGetValue(key, out string? customLabel) && !string.IsNullOrWhiteSpace(customLabel))
            {
                return customLabel.Trim();
            }

            var dev = ConfiguredDevices.FirstOrDefault(d =>
                d.Id == deviceId ||
                (!string.IsNullOrWhiteSpace(fullName) && !string.IsNullOrWhiteSpace(d.FullName) &&
                 string.Equals(d.FullName, fullName, StringComparison.OrdinalIgnoreCase)));

            if (dev != null && !string.IsNullOrWhiteSpace(dev.CustomLabel))
            {
                return dev.CustomLabel.Trim();
            }

            return $"AUX {fallbackIndex + 1}";
        }
    }
}
