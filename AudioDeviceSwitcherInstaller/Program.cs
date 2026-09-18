using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace AudioDeviceSwitcherInstaller;

static class Program
{
    private const string CurrentVersion = "1.6.0";

    [STAThread]
    static void Main(string[] args)
    {
        string currentExeName = Process.GetCurrentProcess().ProcessName;
        bool isInstallerName = currentExeName.Contains("Installer", StringComparison.OrdinalIgnoreCase) ||
                               currentExeName.Contains("Setup", StringComparison.OrdinalIgnoreCase);

        bool explicitInstall = args.Contains("--install") || args.Contains("/install");
        bool explicitPortable = args.Contains("--portable") || args.Contains("/portable");

        // If running as an installer (named *Installer* / *Setup* or passed --install) and not forced portable
        if ((isInstallerName || explicitInstall) && !explicitPortable)
        {
            ApplicationConfiguration.Initialize();
            bool silent = args.Contains("--silent") || args.Contains("-s") || args.Contains("/S");
            Application.Run(new Form1(silent));
            return;
        }

        // Portable execution mode (running as AudioDeviceSwitcher.exe)
        RunPortable(args);
    }

    private static void RunPortable(string[] args)
    {
        try
        {
            string appDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AudioDeviceSwitcher",
                "app-" + CurrentVersion
            );

            // If a "portable.txt" file exists in the directory of the runner, use a local "bin" folder
            string localPortableMarker = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "portable.txt");
            if (File.Exists(localPortableMarker))
            {
                appDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bin");
            }

            string targetExe = Path.Combine(appDir, "AudioDeviceSwitcher.exe");
            string versionMarker = Path.Combine(appDir, $".version-{CurrentVersion}");

            if (!File.Exists(targetExe) || !File.Exists(versionMarker))
            {
                ExtractPayload(appDir);
                File.WriteAllText(versionMarker, CurrentVersion);
            }

            // Launch target WinUI 3 process
            var psi = new ProcessStartInfo
            {
                FileName = targetExe,
                WorkingDirectory = appDir,
                UseShellExecute = true
            };

            // Pass through command line args (excluding portable flags)
            var passArgs = args.Where(a => a != "--portable" && a != "/portable").ToArray();
            if (passArgs.Length > 0)
            {
                psi.Arguments = string.Join(" ", passArgs.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
            }

            Process.Start(psi);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to launch Audio Device Switcher:\n{ex.Message}",
                            "Audio Device Switcher",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
        }
    }

    public static void ExtractPayload(string destinationDir)
    {
        if (!Directory.Exists(destinationDir))
        {
            Directory.CreateDirectory(destinationDir);
        }

        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("Payload.zip");
        if (stream == null)
        {
            throw new InvalidOperationException("Payload.zip not found in embedded resources.");
        }

        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            string destFile = Path.Combine(destinationDir, entry.FullName);
            string? dir = Path.GetDirectoryName(destFile);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry

            try
            {
                entry.ExtractToFile(destFile, overwrite: true);
            }
            catch (IOException)
            {
                // In case a file is temporarily locked, retry once after short delay
                System.Threading.Thread.Sleep(50);
                try { entry.ExtractToFile(destFile, overwrite: true); } catch { }
            }
        }
    }
}