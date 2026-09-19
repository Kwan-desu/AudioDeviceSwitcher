using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AudioDeviceSwitcher
{
    public class UpdateInfo
    {
        public string VersionTag { get; set; } = string.Empty;
        public Version Version { get; set; } = new Version(0, 0);
        public string ReleaseUrl { get; set; } = string.Empty;
        public string ReleaseNotes { get; set; } = string.Empty;
        public string InstallerUrl { get; set; } = string.Empty;
        public string PortableUrl { get; set; } = string.Empty;
    }

    public class UpdateChecker
    {
        private const string RepoApiUrl = "https://api.github.com/repos/Kwan-desu/AudioDeviceSwitcher/releases/latest";

        public static Version GetCurrentVersion()
        {
            var ver = typeof(UpdateChecker).Assembly.GetName().Version;
            return ver != null ? new Version(ver.Major, ver.Minor, Math.Max(0, ver.Build)) : new Version(1, 6, 1);
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

        public static async Task<UpdateInfo?> CheckForUpdatesAsync()
        {
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("AudioDeviceSwitcher-Updater");
                var response = await client.GetStringAsync(RepoApiUrl);

                using var doc = JsonDocument.Parse(response);
                var root = doc.RootElement;
                var latestTag = root.GetProperty("tag_name").GetString();

                if (latestTag != null && IsNewerVersion(latestTag))
                {
                    var info = new UpdateInfo
                    {
                        VersionTag = latestTag,
                        ReleaseUrl = root.TryGetProperty("html_url", out var urlEl) ? urlEl.GetString() ?? "" : "",
                        ReleaseNotes = root.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() ?? "" : ""
                    };

                    string cleanTag = latestTag.Trim().TrimStart('v', 'V');
                    int dashIndex = cleanTag.IndexOf('-');
                    if (dashIndex > 0) cleanTag = cleanTag.Substring(0, dashIndex);
                    if (Version.TryParse(cleanTag, out var ver)) info.Version = ver;

                    if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var asset in assets.EnumerateArray())
                        {
                            var name = asset.GetProperty("name").GetString() ?? "";
                            var dlUrl = asset.GetProperty("browser_download_url").GetString() ?? "";

                            if (name.Contains("Installer", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            {
                                info.InstallerUrl = dlUrl;
                            }
                            else if (name.Equals("AudioDeviceSwitcher.exe", StringComparison.OrdinalIgnoreCase))
                            {
                                info.PortableUrl = dlUrl;
                            }
                        }
                    }

                    // Fallback to portable if installer not found, or vice versa
                    if (string.IsNullOrEmpty(info.InstallerUrl)) info.InstallerUrl = info.PortableUrl;
                    if (string.IsNullOrEmpty(info.PortableUrl)) info.PortableUrl = info.InstallerUrl;

                    return info;
                }
            }
            catch (Exception ex)
            {
                App.Log($"[UpdateChecker] CheckForUpdatesAsync error: {ex.Message}");
            }
            return null;
        }

        public static async Task DownloadAndInstallUpdateAsync(UpdateInfo info, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            try
            {
                string downloadUrl = !string.IsNullOrEmpty(info.InstallerUrl) ? info.InstallerUrl : info.PortableUrl;
                if (string.IsNullOrEmpty(downloadUrl))
                {
                    throw new InvalidOperationException("No valid download URL found in release.");
                }

                string tempInstaller = Path.Combine(Path.GetTempPath(), $"AudioDeviceSwitcherInstaller_Update_{info.VersionTag}.exe");
                if (File.Exists(tempInstaller))
                {
                    try { File.Delete(tempInstaller); } catch { }
                }

                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("AudioDeviceSwitcher-Updater");
                    using var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    response.EnsureSuccessStatusCode();

                    long totalBytes = response.Content.Headers.ContentLength ?? -1L;
                    using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    using var fileStream = new FileStream(tempInstaller, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

                    byte[] buffer = new byte[81920];
                    long totalRead = 0;
                    int bytesRead;

                    while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                    {
                        await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                        totalRead += bytesRead;
                        if (totalBytes > 0 && progress != null)
                        {
                            progress.Report((double)totalRead / totalBytes * 100.0);
                        }
                    }
                }

                // Verify downloaded file is valid
                if (!File.Exists(tempInstaller) || new FileInfo(tempInstaller).Length < 1000000)
                {
                    throw new InvalidOperationException("Downloaded update package is incomplete or corrupted.");
                }

                // Launch installer with silent upgrade flags
                var psi = new ProcessStartInfo
                {
                    FileName = tempInstaller,
                    Arguments = "--install --silent",
                    UseShellExecute = true
                };

                Process.Start(psi);

                // Exit the running app so the installer can cleanly overwrite files and relaunch
                App.CurrentApp.ExitApp();
            }
            catch (Exception ex)
            {
                App.Log($"[UpdateChecker] Update failed: {ex.Message}");
                throw;
            }
        }
    }
}
