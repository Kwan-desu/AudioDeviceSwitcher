using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace AudioDeviceSwitcher
{
    public class AppSettings
    {
        public List<Guid> SelectedDeviceIds { get; set; } = new List<Guid>();
        public List<Guid> MixerDeviceIds { get; set; } = new List<Guid>();
        public Dictionary<string, string> DeviceLabels { get; set; } = new Dictionary<string, string>();

        // New Publication Features
        public bool RunAtStartup { get; set; } = false;
        public bool EnableGlobalHotkeys { get; set; } = false;
        
        // Stored as Keys enum or strings. We'll use strings for easy JSON serialization of WPF Key/Modifier keys
        public string QuickSwitchHotkey { get; set; } = "Ctrl+Shift+S";
        public string OpenMixerHotkey { get; set; } = "Ctrl+Shift+M";

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
            if (File.Exists(path))
            {
                try
                {
                    string json = File.ReadAllText(path);
                    return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                }
                catch
                {
                    return new AppSettings();
                }
            }
            return new AppSettings();
        }

        public void Save()
        {
            string path = GetConfigPath();
            string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }

        public string GetLabelForDevice(Guid deviceId, int fallbackIndex)
        {
            string key = deviceId.ToString();
            if (DeviceLabels.TryGetValue(key, out string? customLabel) && !string.IsNullOrWhiteSpace(customLabel))
            {
                return customLabel.Trim();
            }
            return $"AUX {fallbackIndex + 1}";
        }
    }
}
