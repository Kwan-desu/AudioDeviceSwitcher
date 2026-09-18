using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace AudioDeviceSwitcher
{
    public static class StartupManager
    {
        private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "AudioDeviceSwitcher";

        public static void UpdateStartup(bool enable)
        {
            try
            {
                using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key == null) return;

                    if (enable)
                    {
                        string exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
                        if (!string.IsNullOrEmpty(exePath))
                        {
                            key.SetValue(AppName, $"\"{exePath}\" --startup");
                        }
                    }
                    else
                    {
                        key.DeleteValue(AppName, false);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[StartupManager] Failed to update startup registry: {ex.Message}");
            }
        }
    }
}
