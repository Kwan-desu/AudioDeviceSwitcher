using System;
using System.IO;
using System.Windows.Forms;
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
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key == null) return;

                    if (enable)
                    {
                        string exePath = Application.ExecutablePath;
                        key.SetValue(AppName, $"\"{exePath}\"");
                    }
                    else
                    {
                        key.DeleteValue(AppName, false);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to update startup registry: {ex.Message}");
            }
        }
    }
}
