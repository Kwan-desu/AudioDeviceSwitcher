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
        private TrayScrollManager _trayScrollManager;
        private OsdWindow? _currentOsd;

        public TrayApplicationContext()
        {
            _audioManager = new AudioDeviceManager();
            _settings = AppSettings.Load();

            _audioManager.DevicesChanged += () =>
            {
                try
                {
                    _trayIcon?.ContextMenuStrip?.BeginInvoke(new Action(() => UpdateTrayText()));
                }
                catch { }
            };

            _hotkeyManager = new GlobalHotkeyManager();
            _hotkeyManager.QuickSwitchPressed += () => QuickSwitch();
            _hotkeyManager.OpenMixerPressed += () => ShowMixer();
            _hotkeyManager.RegisterHotkeys(_settings.EnableGlobalHotkeys, _settings.QuickSwitchHotkey, _settings.OpenMixerHotkey);

            _trayIcon = new NotifyIcon()
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application,
                ContextMenuStrip = new ContextMenuStrip(),
                Visible = true,
                Text = "Audio Device Switcher"
            };

            _trayIcon.MouseUp += TrayIcon_MouseUp;

            _trayScrollManager = new TrayScrollManager(_trayIcon);
            _trayScrollManager.Scrolled += TrayScrollManager_Scrolled;
            _trayScrollManager.SetEnabled(_settings.EnableTrayScrollVolume);

            var menu = _trayIcon.ContextMenuStrip;
            menu.Renderer = new FluentMenuRenderer();
            menu.Font = new Font("Segoe UI Variable Text", 9.5f);
            menu.ImageScalingSize = new Size(16, 16);
            menu.ShowImageMargin = true;
            menu.Padding = new Padding(4);
            // Round the menu window + apply dark mode each time it opens (Win11).
            menu.HandleCreated += (s, e) => StyleMenuWindow(menu.Handle);
            menu.Opened += (s, e) => StyleMenuWindow(menu.Handle);

            menu.Items.Add(MakeMenuItem("Volume mixer", "\uE9E9", (s, e) => ShowMixer()));
            menu.Items.Add(MakeMenuItem("Quick switch device", "\uE895", (s, e) => QuickSwitch()));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(MakeMenuItem("Settings", "\uE713", Settings_Click));
            menu.Items.Add(MakeMenuItem("Exit", "\uE711", Exit_Click));

            UpdateTrayText();

            _pollTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _pollTimer.Tick += (s, e) => UpdateTrayText();
            _pollTimer.Start();
        }

        private ToolStripMenuItem MakeMenuItem(string text, string glyph, EventHandler onClick)
        {
            var item = new ToolStripMenuItem(text, RenderGlyph(glyph), onClick)
            {
                Padding = new Padding(4, 3, 4, 3)
            };
            return item;
        }

        private Image RenderGlyph(string glyph)
        {
            var bmp = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                g.Clear(Color.Transparent);
                var a = ThemeManager.Accent;
                var accentGdi = Color.FromArgb(a.R, a.G, a.B);
                using var font = new Font("Segoe Fluent Icons", 11f, FontStyle.Regular, GraphicsUnit.Pixel);
                using var brush = new SolidBrush(accentGdi);
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(glyph, font, brush, new RectangleF(0, 0, 16, 16), sf);
            }
            return bmp;
        }

        private void TrayScrollManager_Scrolled(int direction)        {
            var currentDefault = _audioManager.GetDefaultPlaybackDevice();
            if (currentDefault != null)
            {
                double currentVol = currentDefault.Volume;
                double newVol = Math.Clamp(currentVol + (direction * 2), 0, 100);

                if (direction > 0 && currentDefault.IsMuted)
                {
                    currentDefault.Mute(false);
                }

                currentDefault.Volume = newVol;
                UpdateTrayText();

                ShowVolumeOsd(currentDefault.FullName, (int)newVol, currentDefault.IsMuted);
            }
        }

        private void ShowVolumeOsd(string deviceName, int volume, bool isMuted)
        {
            try
            {
                string status = isMuted ? $"{deviceName}  •  Muted" : $"{deviceName}  •  {volume}%";
                string glyph = isMuted || volume == 0 ? "\uE74F" : (volume > 66 ? "\uE995" : (volume > 33 ? "\uE994" : "\uE993"));

                if (_currentOsd == null || !_currentOsd.IsLoaded)
                {
                    _currentOsd = new OsdWindow();
                    _currentOsd.Closed += (s, e) => _currentOsd = null;
                }

                _currentOsd.ShowOsd("Volume adjusted", status, glyph, isMuted ? 0 : volume);
            }
            catch { }
        }

        private void ShowDeviceSwitchOsd(string deviceName)
        {
            try
            {
                if (_currentOsd == null || !_currentOsd.IsLoaded)
                {
                    _currentOsd = new OsdWindow();
                    _currentOsd.Closed += (s, e) => _currentOsd = null;
                }

                _currentOsd.ShowOsd("Audio playback switched", deviceName, "\uE995");
            }
            catch { }
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
            var result = _audioManager.SwitchToNextDevice(_settings);
            UpdateTrayText();

            switch (result.Status)
            {
                case SwitchStatus.Success:
                    if (result.SwitchedToDevice != null)
                    {
                        ShowDeviceSwitchOsd(result.SwitchedToDevice.FullName);
                    }
                    break;

                case SwitchStatus.TargetDeviceDisconnected:
                    ShowDisconnectedOsd(result.DisconnectedDeviceName ?? "Other audio device");
                    break;

                case SwitchStatus.NeedMoreDevicesConfigured:
                    _trayIcon.ShowBalloonTip(3000, "Setup Required", "Please open Settings to select at least 2 devices for quick switching.", ToolTipIcon.Info);
                    break;

                case SwitchStatus.Failed:
                    break;
            }
        }

        private void ShowDisconnectedOsd(string deviceName)
        {
            try
            {
                if (_currentOsd == null || !_currentOsd.IsLoaded)
                {
                    _currentOsd = new OsdWindow();
                    _currentOsd.Closed += (s, e) => _currentOsd = null;
                }

                _currentOsd.ShowOsd("Device is disconnected", deviceName, "\uE7BA");
            }
            catch { }
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
            var activeDevices = _audioManager.GetActivePlaybackDevices();
            _settings.SyncActiveDevices(activeDevices);

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
                    label = _settings.GetLabelForDevice(currentDefault.Id, currentDefault.FullName, index);
                }
                else
                {
                    label = _settings.GetLabelForDevice(currentDefault.Id, currentDefault.FullName, 0);
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
            var oldIcon = _trayIcon.Icon;
            _trayIcon.Icon = GenerateGiantTaskbarIcon(label, volume, isMuted);
            if (oldIcon != null)
            {
                // Note: Application.ExecutablePath icon is shared, so don't dispose it if it's the default
                oldIcon.Dispose();
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        extern static bool DestroyIcon(IntPtr handle);

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

        private void StyleMenuWindow(IntPtr hwnd)
        {
            try
            {
                if (hwnd == IntPtr.Zero) return;
                int dark = ThemeManager.IsDark ? 1 : 0;
                DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));   // immersive dark mode
                int round = 2;                                            // DWMWCP_ROUND
                DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));  // corner preference
            }
            catch { }
        }

        private Icon GenerateGiantTaskbarIcon(string label, int volume, bool isMuted)
        {
            // 32x32 pixel canvas with 100% edge-to-edge drawing
            using (Bitmap bmp = new Bitmap(32, 32))
            {
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
                Icon newIcon;
                using (Icon tmpIcon = Icon.FromHandle(hIcon))
                {
                    newIcon = (Icon)tmpIcon.Clone();
                }
                DestroyIcon(hIcon);
                return newIcon;
            }
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
            
            // Re-apply settings
            _hotkeyManager?.RegisterHotkeys(_settings.EnableGlobalHotkeys, _settings.QuickSwitchHotkey, _settings.OpenMixerHotkey);
            _trayScrollManager?.SetEnabled(_settings.EnableTrayScrollVolume);
            
            UpdateTrayText();
        }

        private void Settings_Click(object? sender, EventArgs e)
        {
            OpenSettings();
        }

        private void Exit_Click(object? sender, EventArgs e)
        {
            _pollTimer.Stop();
            _trayScrollManager?.Dispose();
            _trayIcon.Visible = false;
            _hotkeyManager?.Dispose();
            _audioManager?.Dispose();
            System.Windows.Application.Current?.Shutdown();
            Application.Exit();
        }
    }
}
