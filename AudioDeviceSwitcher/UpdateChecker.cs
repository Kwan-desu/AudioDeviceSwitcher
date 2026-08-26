using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace AudioDeviceSwitcher
{
    public class UpdateChecker
    {
        private const string RepoApiUrl = "https://api.github.com/repos/Kwan-desu/AudioDeviceSwitcher/releases/latest";
        
        public static Version GetCurrentVersion()
        {
            var ver = typeof(UpdateChecker).Assembly.GetName().Version;
            return ver != null ? new Version(ver.Major, ver.Minor, Math.Max(0, ver.Build)) : new Version(1, 0, 0);
        }

        public static bool IsNewerVersion(string? latestTag)
        {
            if (string.IsNullOrWhiteSpace(latestTag)) return false;

            string cleanTag = latestTag.Trim().TrimStart('v', 'V');
            
            // Handle versions like "1.4.1-hotfix" by stripping suffix
            int dashIndex = cleanTag.IndexOf('-');
            if (dashIndex > 0)
            {
                cleanTag = cleanTag.Substring(0, dashIndex);
            }

            if (Version.TryParse(cleanTag, out var remoteVersion))
            {
                var currentVersion = GetCurrentVersion();
                return remoteVersion > currentVersion;
            }

            return false;
        }

        public static async Task<string?> CheckForUpdatesAsync()
        {
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("AudioDeviceSwitcher-Updater");
                var response = await client.GetStringAsync(RepoApiUrl);
                
                using var doc = JsonDocument.Parse(response);
                var latestTag = doc.RootElement.GetProperty("tag_name").GetString();
                
                if (latestTag != null && IsNewerVersion(latestTag))
                {
                    // Check if there is an asset
                    var assets = doc.RootElement.GetProperty("assets");
                    if (assets.GetArrayLength() > 0)
                    {
                        var downloadUrl = assets[0].GetProperty("browser_download_url").GetString();
                        return downloadUrl;
                    }
                }
            }
            catch { }
            return null;
        }

        public static async Task DownloadAndInstallUpdateAsync(string downloadUrl)
        {
            try
            {
                string tempExe = Path.Combine(Path.GetTempPath(), "AudioDeviceSwitcher_Update.exe");
                
                if (File.Exists(tempExe)) File.Delete(tempExe);
                
                using (var client = new HttpClient())
                {
                    var response = await client.GetAsync(downloadUrl);
                    using (var fs = new FileStream(tempExe, FileMode.Create))
                    {
                        await response.Content.CopyToAsync(fs);
                    }
                }

                // Portable Self-Update Magic
                string currentExe = Process.GetCurrentProcess().MainModule?.FileName ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
                string oldExe = currentExe + ".old";

                // Remove previous old file if it exists
                if (File.Exists(oldExe)) File.Delete(oldExe);

                // Windows allows renaming running executables, but not overwriting them
                File.Move(currentExe, oldExe);
                File.Move(tempExe, currentExe);

                // Restart app
                Process.Start(new ProcessStartInfo
                {
                    FileName = currentExe,
                    UseShellExecute = true
                });
                
                System.Windows.Application.Current?.Shutdown();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Failed to download or run the update:\n" + ex.Message, "Update Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }
    }
}
