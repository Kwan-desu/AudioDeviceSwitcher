using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace AudioDeviceSwitcher
{
    public class UpdateChecker
    {
        private const string RepoApiUrl = "https://api.github.com/repos/Kwan-desu/AudioDeviceSwitcher/releases/latest";

        public static Version GetCurrentVersion()
        {
            var ver = typeof(UpdateChecker).Assembly.GetName().Version;
            return ver != null ? new Version(ver.Major, ver.Minor, Math.Max(0, ver.Build)) : new Version(1, 6, 0);
        }

        public static bool IsNewerVersion(string? latestTag)
        {
            if (string.IsNullOrWhiteSpace(latestTag)) return false;

            string cleanTag = latestTag.Trim().TrimStart('v', 'V');
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

                string currentExe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
                string oldExe = currentExe + ".old";

                if (File.Exists(oldExe)) File.Delete(oldExe);

                File.Move(currentExe, oldExe);
                File.Move(tempExe, currentExe);

                Process.Start(new ProcessStartInfo
                {
                    FileName = currentExe,
                    UseShellExecute = true
                });

                Microsoft.UI.Xaml.Application.Current?.Exit();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UpdateChecker] Update failed: {ex.Message}");
            }
        }
    }
}
