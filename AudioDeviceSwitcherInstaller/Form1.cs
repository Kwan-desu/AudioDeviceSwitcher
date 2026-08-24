using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AudioDeviceSwitcherInstaller
{
    public partial class Form1 : Form
    {
        private Button _installBtn;
        private Label _statusLabel;
        private ProgressBar _progressBar;
        private TextBox _pathTextBox;
        private Button _browseBtn;
        
        private bool _isSilent;
        private string _registryKey = @"Software\AudioDeviceSwitcher";

        public Form1(bool silent = false)
        {
            _isSilent = silent;
            SetupUI();
            
            if (_isSilent)
            {
                this.Load += (s, e) => {
                    this.Opacity = 0;
                    this.ShowInTaskbar = false;
                    StartInstall();
                };
            }
        }

        private void SetupUI()
        {
            this.Text = "Audio Device Switcher Setup";
            this.Size = new Size(420, 240);
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
                Location = new Point(20, 15),
                AutoSize = true
            };
            this.Controls.Add(titleLabel);
            
            // Path Selection
            Label pathLabel = new Label
            {
                Text = "Installation Folder:",
                Font = new Font("Segoe UI", 9),
                Location = new Point(22, 55),
                AutoSize = true,
                ForeColor = Color.LightGray
            };
            this.Controls.Add(pathLabel);

            string defaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AudioDeviceSwitcher");
            
            // Try load previous install path from registry
            try 
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(_registryKey))
                {
                    if (key != null)
                    {
                        var savedPath = key.GetValue("InstallDir") as string;
                        if (!string.IsNullOrEmpty(savedPath)) defaultPath = savedPath;
                    }
                }
            } 
            catch { }

            _pathTextBox = new TextBox
            {
                Text = defaultPath,
                Location = new Point(24, 75),
                Size = new Size(270, 25),
                Font = new Font("Segoe UI", 9),
                BackColor = Color.FromArgb(45, 45, 45),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };
            this.Controls.Add(_pathTextBox);

            _browseBtn = new Button
            {
                Text = "Browse...",
                Location = new Point(300, 74),
                Size = new Size(80, 26),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9)
            };
            _browseBtn.FlatAppearance.BorderSize = 0;
            _browseBtn.Click += (s, e) =>
            {
                using (var fbd = new FolderBrowserDialog())
                {
                    fbd.SelectedPath = _pathTextBox.Text;
                    if (fbd.ShowDialog() == DialogResult.OK)
                    {
                        _pathTextBox.Text = Path.Combine(fbd.SelectedPath, "AudioDeviceSwitcher");
                    }
                }
            };
            this.Controls.Add(_browseBtn);

            _statusLabel = new Label
            {
                Text = "Ready to install.",
                Font = new Font("Segoe UI", 9),
                Location = new Point(22, 110),
                AutoSize = true,
                ForeColor = Color.DarkGray
            };
            this.Controls.Add(_statusLabel);

            _progressBar = new ProgressBar
            {
                Location = new Point(24, 135),
                Size = new Size(356, 10),
                Style = ProgressBarStyle.Continuous
            };
            this.Controls.Add(_progressBar);

            _installBtn = new Button
            {
                Text = "Install",
                Location = new Point(280, 160),
                Size = new Size(100, 30),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(0, 120, 215),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9, FontStyle.Bold)
            };
            _installBtn.FlatAppearance.BorderSize = 0;
            _installBtn.Click += (s, ev) => StartInstall();
            this.Controls.Add(_installBtn);
        }

        private async void StartInstall()
        {
            _installBtn.Enabled = false;
            _browseBtn.Enabled = false;
            _pathTextBox.Enabled = false;
            _progressBar.Style = ProgressBarStyle.Marquee;
            _statusLabel.Text = "Installing...";

            string targetDir = _pathTextBox.Text.Trim();

            try
            {
                await Task.Run(() => PerformInstallation(targetDir));
                
                if (_isSilent)
                {
                    // Launch app and exit silently
                    string exePath = Path.Combine(targetDir, "AudioDeviceSwitcher.exe");
                    Process.Start(new ProcessStartInfo { FileName = exePath, UseShellExecute = true });
                    Application.Exit();
                    return;
                }

                _progressBar.Style = ProgressBarStyle.Continuous;
                _progressBar.Value = 100;
                _statusLabel.Text = "Installation Complete!";
                _statusLabel.ForeColor = Color.LightGreen;
                _installBtn.Text = "Finish";
                
                // Clear old handlers
                _installBtn.Click -= (s, ev) => StartInstall();
                _installBtn.Click += (s, ev) => this.Close();
                _installBtn.Enabled = true;

                // Launch the app
                string appPath = Path.Combine(targetDir, "AudioDeviceSwitcher.exe");
                Process.Start(new ProcessStartInfo { FileName = appPath, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                if (_isSilent) Application.Exit();
                
                _progressBar.Style = ProgressBarStyle.Continuous;
                _statusLabel.Text = "Error: " + ex.Message;
                _statusLabel.ForeColor = Color.Red;
                _installBtn.Enabled = true;
                _browseBtn.Enabled = true;
                _pathTextBox.Enabled = true;
            }
        }

        private void PerformInstallation(string installDir)
        {
            // 1. Create Directory
            if (!Directory.Exists(installDir))
            {
                Directory.CreateDirectory(installDir);
            }

            // Save to Registry for updates
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(_registryKey))
                {
                    key.SetValue("InstallDir", installDir);
                }
            }
            catch { }

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
