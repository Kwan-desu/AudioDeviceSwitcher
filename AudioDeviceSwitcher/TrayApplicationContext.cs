using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using System.Threading;
using System.Threading.Tasks;

namespace AudioDeviceSwitcher
{
    public class TrayApplicationContext : ApplicationContext
    {
        private NotifyIcon _trayIcon;
        private AudioDeviceManager _audioManager;
        private AppSettings _settings;
        private MixerWindow? _currentMixerWindow;
        private System.Windows.Forms.Timer _pollTimer;
        private GlobalHotkeyManager _hotkeyManager;

        public TrayApplicationContext()
        {
            _audioManager = new AudioDeviceManager();
            _settings = AppSettings.Load();

            _hotkeyManager = new GlobalHotkeyManager();
            _hotkeyManager.QuickSwitchPressed += () => QuickSwitch();
            _hotkeyManager.OpenMixerPressed += () => ShowMixer();
            _hotkeyManager.RegisterHotkeys(_settings.EnableGlobalHotkeys);

            _trayIcon = new NotifyIcon()
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application,
                ContextMenuStrip = new ContextMenuStrip(),
                Visible = true,
                Text = "Audio Device Switcher"
            };

            _trayIcon.MouseUp += TrayIcon_MouseUp;

            _trayIcon.ContextMenuStrip.Items.Add("🎚️ Volume Mixer", null, (s, e) => ShowMixer());
            _trayIcon.ContextMenuStrip.Items.Add("🔄 Quick Switch Device", null, (s, e) => QuickSwitch());
            _trayIcon.ContextMenuStrip.Items.Add(new ToolStripSeparator());
            _trayIcon.ContextMenuStrip.Items.Add("⚙️ Settings", null, Settings_Click);
            _trayIcon.ContextMenuStrip.Items.Add("❌ Exit", null, Exit_Click);

            UpdateTrayText();

            _pollTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _pollTimer.Tick += (s, e) => UpdateTrayText();
            _pollTimer.Start();
        }

        private CancellationTokenSource? _singleClickCts;
        private DateTime _lastLeftClickTime = DateTime.MinValue;

        private async void TrayIcon_MouseUp(object? sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                var clickTime = DateTime.Now;
                var timeSinceLast = (clickTime - _lastLeftClickTime).TotalMilliseconds;

                if (timeSinceLast <= SystemInformation.DoubleClickTime)
                {
                    // Double Click detected!
                    _lastLeftClickTime = DateTime.MinValue; // Reset to prevent triple-clicks
                    _singleClickCts?.Cancel();              // Abort the single-click switch
                    ShowMixer();
                }
                else
                {
                    // Single Click detected (so far)
                    _lastLeftClickTime = clickTime;
                    
                    _singleClickCts?.Cancel();
                    _singleClickCts = new CancellationTokenSource();
                    var token = _singleClickCts.Token;

                    try
                    {
                        // Add a 50ms buffer to the double-click time to prevent race conditions
                        await Task.Delay(SystemInformation.DoubleClickTime + 50, token);
                        
                        if (!token.IsCancellationRequested)
                        {
                            QuickSwitch();
                        }
                    }
                    catch (TaskCanceledException) { }
                }
            }
            else if (e.Button == MouseButtons.Middle)
            {
                ShowMixer();
            }
        }

        private void QuickSwitch()
        {
            if (_settings.SelectedDeviceIds.Count < 2)
            {
                _trayIcon.ShowBalloonTip(3000, "Setup Required", "Please open Settings to select at least 2 devices for quick switching.", ToolTipIcon.Info);
                return;
            }

            _audioManager.SwitchToNextDevice(_settings.SelectedDeviceIds);
            UpdateTrayText();

            // Show OSD Notification
            var currentDevice = _audioManager.GetDefaultPlaybackDevice();
            if (currentDevice != null)
            {
                var osd = new OsdWindow();
                osd.ShowOsd(currentDevice.FullName);
            }
        }

        public void ShowMixer()
        {
            if (_currentMixerWindow != null && _currentMixerWindow.IsLoaded)
            {
                _currentMixerWindow.Close();
                _currentMixerWindow = null;
                return;
            }

            _currentMixerWindow = new MixerWindow(_audioManager, _settings, this);
            _currentMixerWindow.Show();
            _currentMixerWindow.Activate();
        }

        public void UpdateTrayText()
        {
            var currentDefault = _audioManager.GetDefaultPlaybackDevice();
            string fullName = currentDefault?.FullName ?? "Unknown";

            string label = "AUX 1";
            int volume = 50;
            bool isMuted = false;

            if (currentDefault != null)
            {
                int index = _settings.SelectedDeviceIds.IndexOf(currentDefault.Id);
                if (index >= 0)
                {
                    label = _settings.GetLabelForDevice(currentDefault.Id, index);
                }
                else
                {
                    label = "AUX ?";
                }

                volume = Math.Clamp((int)currentDefault.Volume, 0, 100);
                isMuted = currentDefault.IsMuted;
            }

            string muteText = isMuted ? " (Muted)" : "";
            string tipText = $"{label}: {fullName}\nVolume: {volume}%{muteText}";

            if (tipText.Length > 63)
            {
                tipText = tipText.Substring(0, 60) + "...";
            }

            _trayIcon.Text = tipText;
            _trayIcon.Icon = GenerateGiantTaskbarIcon(label, volume, isMuted);
        }

        private Icon GenerateGiantTaskbarIcon(string label, int volume, bool isMuted)
        {
            // 32x32 pixel canvas with 100% edge-to-edge drawing
            Bitmap bmp = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.Clear(Color.Transparent);

                // 1. Draw GIANT Speaker (fills from Y=1 to Y=31, X=0 to X=31)
                using (Pen pen = new Pen(Color.White, 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                using (Pen redPen = new Pen(Color.FromArgb(255, 75, 75), 3.0f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                using (SolidBrush fillBrush = new SolidBrush(Color.White))
                {
                    // Large solid speaker body (Height: 28px)
                    Point[] speakerPts = {
                        new Point(1, 10),
                        new Point(8, 10),
                        new Point(15, 2),
                        new Point(15, 30),
                        new Point(8, 22),
                        new Point(1, 22)
                    };
                    g.FillPolygon(fillBrush, speakerPts);

                    // Sound Waves / Mute X (Spanning edge-to-edge)
                    if (isMuted || volume == 0)
                    {
                        g.DrawLine(redPen, 18, 9, 29, 23);
                        g.DrawLine(redPen, 29, 9, 18, 23);
                    }
                    else
                    {
                        // Wave 1
                        g.DrawArc(pen, 11, 8, 10, 16, -45, 90);

                        // Wave 2 (> 33%)
                        if (volume > 33)
                        {
                            g.DrawArc(pen, 7, 4, 18, 24, -45, 90);
                        }

                        // Wave 3 (> 66%)
                        if (volume > 66)
                        {
                            g.DrawArc(pen, 3, 0, 26, 32, -45, 90);
                        }
                    }
                }

                // 2. High-Contrast Device Number Badge (e.g. 1, 2, 3 or A1)
                string badge = label.Trim();
                if (badge.StartsWith("AUX ", StringComparison.OrdinalIgnoreCase))
                {
                    badge = badge.Substring(4).Trim();
                }

                if (!string.IsNullOrEmpty(badge))
                {
                    using (Font badgeFont = new Font("Segoe UI", badge.Length > 1 ? 8.5F : 10.5F, FontStyle.Bold, GraphicsUnit.Pixel))
                    {
                        SizeF size = g.MeasureString(badge, badgeFont);
                        int badgeW = Math.Max((int)size.Width + 4, 14);
                        int badgeH = 14;
                        int badgeX = 32 - badgeW;
                        int badgeY = 32 - badgeH;

                        // Dark rounded container
                        using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(245, 10, 15, 22)))
                        using (Pen borderPen = new Pen(Color.FromArgb(0, 160, 255), 1.4f))
                        using (GraphicsPath path = GetRoundedRect(new Rectangle(badgeX, badgeY, badgeW - 1, badgeH - 1), 3))
                        {
                            g.FillPath(bgBrush, path);
                            g.DrawPath(borderPen, path);
                        }

                        // Vibrant Blue/White number text
                        using (SolidBrush textBrush = new SolidBrush(Color.FromArgb(0, 215, 255)))
                        {
                            float tx = badgeX + (badgeW - size.Width) / 2f;
                            float ty = badgeY + (badgeH - size.Height) / 2f - 0.5f;
                            g.DrawString(badge, badgeFont, textBrush, tx, ty);
                        }
                    }
                }
            }

            IntPtr hIcon = bmp.GetHicon();
            return Icon.FromHandle(hIcon);
        }

        private GraphicsPath GetRoundedRect(Rectangle bounds, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int diameter = radius * 2;
            Rectangle arc = new Rectangle(bounds.Location, new Size(diameter, diameter));

            path.AddArc(arc, 180, 90);
            arc.X = bounds.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = bounds.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = bounds.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }

        public void OpenSettings()
        {
            var settingsWindow = new SettingsWindow(_settings, _audioManager);
            settingsWindow.ShowDialog();
            
            // Re-apply hotkeys based on updated settings
            _hotkeyManager?.RegisterHotkeys(_settings.EnableGlobalHotkeys);
            
            UpdateTrayText();
        }

        private void Settings_Click(object? sender, EventArgs e)
        {
            OpenSettings();
        }

        private void Exit_Click(object? sender, EventArgs e)
        {
            _pollTimer.Stop();
            _trayIcon.Visible = false;
            _hotkeyManager?.Dispose();
            System.Windows.Application.Current?.Shutdown();
            Application.Exit();
        }
    }
}
