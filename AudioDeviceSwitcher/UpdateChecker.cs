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
        private const string CurrentVersion = "v1.0.1";
        private const string RepoApiUrl = "https://api.github.com/repos/Kwan-desu/AudioDeviceSwitcher/releases/latest";
        
        public static async Task<string?> CheckForUpdatesAsync()
        {
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("AudioDeviceSwitcher-Updater");
                var response = await client.GetStringAsync(RepoApiUrl);
                
                using var doc = JsonDocument.Parse(response);
                var latestTag = doc.RootElement.GetProperty("tag_name").GetString();
                
                if (latestTag != null && latestTag != CurrentVersion)
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
                string tempZip = Path.Combine(Path.GetTempPath(), "AudioDeviceSwitcherInstaller.zip");
                string extractFolder = Path.Combine(Path.GetTempPath(), "AudioDeviceSwitcher_Update");
                
                if (File.Exists(tempZip)) File.Delete(tempZip);
                if (Directory.Exists(extractFolder)) Directory.Delete(extractFolder, true);
                
                using (var client = new HttpClient())
                {
                    var response = await client.GetAsync(downloadUrl);
                    using (var fs = new FileStream(tempZip, FileMode.Create))
                    {
                        await response.Content.CopyToAsync(fs);
                    }
                }

                ZipFile.ExtractToDirectory(tempZip, extractFolder);
                string installerExe = Path.Combine(extractFolder, "AudioDeviceSwitcherInstaller.exe");
                
                if (File.Exists(installerExe))
                {
                    // Run the installer silently, it will read the install path from Registry, kill us, overwrite, and restart
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = installerExe,
                        Arguments = "--silent",
                        UseShellExecute = true
                    });
                    
                    System.Windows.Application.Current?.Shutdown();
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Failed to download or run the update:\n" + ex.Message, "Update Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }
    }
}
