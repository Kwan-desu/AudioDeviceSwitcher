using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AudioDeviceSwitcherInstaller
{
    public partial class Form1 : Form
    {
        private Button _installBtn;
        private Label _statusLabel;
        private ProgressBar _progressBar;

        public Form1()
        {
            SetupUI();
        }

        private void SetupUI()
        {
            this.Text = "Audio Device Switcher Setup";
            this.Size = new Size(400, 200);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(30, 30, 30);
            this.ForeColor = Color.White;

            try
            {
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico"))
                {
                    if (stream != null) this.Icon = new Icon(stream);
                }
            }
            catch { }

            Label titleLabel = new Label
            {
                Text = "Install Audio Device Switcher",
                Font = new Font("Segoe UI", 14, FontStyle.Bold),
                Location = new Point(20, 20),
                AutoSize = true
            };
            this.Controls.Add(titleLabel);

            _statusLabel = new Label
            {
                Text = "Ready to install.",
                Font = new Font("Segoe UI", 9),
                Location = new Point(22, 60),
                AutoSize = true,
                ForeColor = Color.DarkGray
            };
            this.Controls.Add(_statusLabel);

            _progressBar = new ProgressBar
            {
                Location = new Point(20, 85),
                Size = new Size(340, 10),
                Style = ProgressBarStyle.Continuous
            };
            this.Controls.Add(_progressBar);

            _installBtn = new Button
            {
                Text = "Install",
                Location = new Point(260, 115),
                Size = new Size(100, 30),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(0, 120, 215),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9, FontStyle.Bold)
            };
            _installBtn.FlatAppearance.BorderSize = 0;
            _installBtn.Click += InstallBtn_Click;
            this.Controls.Add(_installBtn);
        }

        private async void InstallBtn_Click(object? sender, EventArgs e)
        {
            _installBtn.Enabled = false;
            _progressBar.Style = ProgressBarStyle.Marquee;

            try
            {
                await Task.Run(() => PerformInstallation());
                _progressBar.Style = ProgressBarStyle.Continuous;
                _progressBar.Value = 100;
                _statusLabel.Text = "Installation Complete!";
                _statusLabel.ForeColor = Color.LightGreen;
                _installBtn.Text = "Finish";
                _installBtn.Click -= InstallBtn_Click;
                _installBtn.Click += (s, ev) => this.Close();
                _installBtn.Enabled = true;

                // Launch the app
                string installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AudioDeviceSwitcher");
                string exePath = Path.Combine(installDir, "AudioDeviceSwitcher.exe");
                Process.Start(new ProcessStartInfo { FileName = exePath, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _progressBar.Style = ProgressBarStyle.Continuous;
                _statusLabel.Text = "Error: " + ex.Message;
                _statusLabel.ForeColor = Color.Red;
                _installBtn.Enabled = true;
            }
        }

        private void PerformInstallation()
        {
            // 1. Create Directory
            string installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AudioDeviceSwitcher");
            if (!Directory.Exists(installDir))
            {
                Directory.CreateDirectory(installDir);
            }

            // 2. Kill existing process if running
            foreach (var proc in Process.GetProcessesByName("AudioDeviceSwitcher"))
            {
                try { proc.Kill(); proc.WaitForExit(); } catch { }
            }

            // 3. Extract payload
            string exePath = Path.Combine(installDir, "AudioDeviceSwitcher.exe");
            using (var resourceStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Payload.exe"))
            {
                if (resourceStream == null) throw new Exception("Payload not found.");
                using (var fileStream = new FileStream(exePath, FileMode.Create, FileAccess.Write))
                {
                    resourceStream.CopyTo(fileStream);
                }
            }

            // 4. Create Shortcuts using dynamic WshShell to avoid COM references
            CreateShortcut(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Audio Device Switcher.lnk"),
                exePath);

            CreateShortcut(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "Audio Device Switcher.lnk"),
                exePath);
        }

        private void CreateShortcut(string shortcutPath, string targetPath)
        {
            try
            {
                Type? wshShellType = Type.GetTypeFromProgID("WScript.Shell");
                if (wshShellType == null) return;

                object? wshShell = Activator.CreateInstance(wshShellType);
                if (wshShell == null) return;

                object? shortcut = wshShellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, wshShell, new object[] { shortcutPath });
                if (shortcut == null) return;

                shortcut.GetType().InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { targetPath });
                shortcut.GetType().InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { Path.GetDirectoryName(targetPath)! });
                shortcut.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            }
            catch { }
        }
    }
}
