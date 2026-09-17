using System;
using System.Windows.Forms;

namespace AudioDeviceSwitcher
{
    internal static class Program
    {
        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            ApplicationConfiguration.Initialize();

            // Cleanup portable update leftover (.old)
            try
            {
                string currentExe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
                string oldExe = currentExe + ".old";
                if (System.IO.File.Exists(oldExe))
                {
                    System.IO.File.Delete(oldExe);
                }
            }
            catch { }

            if (System.Windows.Application.Current == null)
            {
                new System.Windows.Application
                {
                    ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown
                };
            }

            // Build the Fluent design tokens (accent, light/dark, type ramp, radii)
            // before any window is shown so all surfaces share one coherent theme.
            try
            {
                var startupSettings = AppSettings.Load();
                ThemeManager.Initialize(startupSettings.Theme);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Program] Theme init failed: {ex.Message}");
            }

            Application.Run(new TrayApplicationContext());
        }
    }
}