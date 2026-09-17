using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace AudioDeviceSwitcher
{
    public class TrayIconManager : IDisposable
    {
        #region Win32 Definitions
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATA lpData);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass, UIntPtr dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass);

        [DllImport("comctl32.dll")]
        private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);

        [DllImport("user32.dll")]
        private static extern uint TrackPopupMenuEx(IntPtr hMenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

        [DllImport("user32.dll")]
        private static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("uxtheme.dll", EntryPoint = "#135", SetLastError = true)]
        private static extern int SetPreferredAppMode(int preferredAppMode);

        [DllImport("uxtheme.dll", EntryPoint = "#136", SetLastError = true)]
        private static extern void FlushMenuThemes();

        [DllImport("uxtheme.dll", EntryPoint = "#133", SetLastError = true)]
        private static extern bool AllowDarkModeForWindow(IntPtr hWnd, bool allow);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string? pszSubIdList);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool SetMenuDefaultItem(IntPtr hMenu, uint uItem, uint fByPos);

        [DllImport("user32.dll")]
        private static extern int GetDoubleClickTime();

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern uint RegisterWindowMessage(string lpString);

        [DllImport("user32.dll")]
        private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

        private const int WH_MOUSE_LL = 14;

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("shell32.dll", SetLastError = true)]
        private static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT iconLocation);

        private const uint NIM_ADD = 0x00000000;
        private const uint NIM_MODIFY = 0x00000001;
        private const uint NIM_DELETE = 0x00000002;
        private const uint NIM_SETVERSION = 0x00000004;

        private const uint NIF_MESSAGE = 0x00000001;
        private const uint NIF_ICON = 0x00000002;
        private const uint NIF_TIP = 0x00000004;
        private const uint NIF_SHOWTIP = 0x00000080;

        private const uint NOTIFYICON_VERSION_4 = 4;

        private const int WM_USER = 0x0400;
        private const int WM_TRAYCALLBACK = WM_USER + 100;
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_LBUTTONDBLCLK = 0x0203;
        private const int WM_RBUTTONUP = 0x0205;
        private const int WM_MBUTTONUP = 0x0208;
        private const int WM_MOUSEWHEEL = 0x020A;
        private const int WM_CONTEXTMENU = 0x007B;
        private const int NIN_SELECT = WM_USER + 0;
        private const int NIN_KEYSELECT = WM_USER + 1;

        private const uint MF_STRING = 0x00000000;
        private const uint MF_SEPARATOR = 0x00000800;
        private const uint TPM_RETURNCMD = 0x0100;
        private const uint TPM_RIGHTBUTTON = 0x0002;

        private const uint CMD_MIXER = 1001;
        private const uint CMD_SWITCH = 1002;
        private const uint CMD_SETTINGS = 1003;
        private const uint CMD_EXIT = 1004;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct NOTIFYICONIDENTIFIER
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public Guid guidItem;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public int ptX, ptY;
            public uint mouseData;
            public uint flags, time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public uint uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        public delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData);
        #endregion

        private readonly IntPtr _hostHwnd;
        private readonly SubclassProc _subclassProc;
        private readonly LowLevelMouseProc _mouseHookProc;
        private readonly uint _wmTaskbarCreated;
        private NOTIFYICONDATA _nid;
        private IntPtr _currentHIcon = IntPtr.Zero;
        private IntPtr _mouseHookId = IntPtr.Zero;
        private bool _isDisposed;
        private volatile bool _isCursorOverIcon = false;
        private System.Threading.Timer? _hoverClearTimer;
        private POINT _lastHoverPoint = new POINT();
        private RECT _lastKnownTrayRect = new RECT();
        private bool _hasKnownTrayRect = false;

        private DateTime _lastLeftClickTime = DateTime.MinValue;
        private System.Threading.Timer? _singleClickTimer;

        public event Action? QuickSwitchRequested;
        public event Action? OpenMixerRequested;
        public event Action? OpenSettingsRequested;
        public event Action? ExitRequested;
        public event Action<int>? VolumeScrolled; // +1 or -1

        private bool _enableScrollVolume = true;
        public bool EnableScrollVolume
        {
            get => _enableScrollVolume;
            set
            {
                _enableScrollVolume = value;
                UpdateHookState();
            }
        }

        public TrayIconManager(IntPtr hostHwnd)
        {
            _hostHwnd = hostHwnd;
            _subclassProc = SubclassWndProc;
            _mouseHookProc = MouseHookCallback;
            _wmTaskbarCreated = RegisterWindowMessage("TaskbarCreated");

            ApplyMenuTheme();

            SetWindowSubclass(_hostHwnd, _subclassProc, new UIntPtr(1001), UIntPtr.Zero);

            _currentHIcon = GenerateTaskbarIconHandle("1", 50, false);
            if (_currentHIcon == IntPtr.Zero)
            {
                _currentHIcon = LoadIcon(IntPtr.Zero, (IntPtr)32512 /* IDI_APPLICATION */);
            }

            _nid = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA)),
                hWnd = _hostHwnd,
                uID = 1001,
                uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                uCallbackMessage = WM_TRAYCALLBACK,
                hIcon = _currentHIcon,
                szTip = "Audio Device Switcher"
            };

            bool addOk = Shell_NotifyIconW(NIM_ADD, ref _nid);
            int addErr = Marshal.GetLastWin32Error();
            App.Log($"[TrayIconManager] NIM_ADD result: {addOk}, Error: {addErr}, HWND: 0x{_hostHwnd:X}, hIcon: 0x{_currentHIcon:X}");

            _nid.uTimeoutOrVersion = NOTIFYICON_VERSION_4;
            bool verOk = Shell_NotifyIconW(NIM_SETVERSION, ref _nid);
            App.Log($"[TrayIconManager] NIM_SETVERSION result: {verOk}");

            _hoverClearTimer = new System.Threading.Timer(_ => _isCursorOverIcon = false, null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            UpdateHookState();
        }

        private void UpdateHookState()
        {
            if (_enableScrollVolume && _mouseHookId == IntPtr.Zero)
            {
                try
                {
                    using var curProcess = Process.GetCurrentProcess();
                    using var curModule = curProcess.MainModule;
                    IntPtr hMod = curModule != null ? GetModuleHandle(curModule.ModuleName) : IntPtr.Zero;
                    _mouseHookId = SetWindowsHookEx(WH_MOUSE_LL, _mouseHookProc, hMod, 0);
                    App.Log($"[TrayIconManager] SetWindowsHookEx(WH_MOUSE_LL) = 0x{_mouseHookId:X}");
                }
                catch (Exception ex)
                {
                    App.Log($"[TrayIconManager] SetWindowsHookEx error: {ex.Message}");
                }
            }
            else if (!_enableScrollVolume && _mouseHookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_mouseHookId);
                _mouseHookId = IntPtr.Zero;
                _isCursorOverIcon = false;
            }
        }

        private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && _enableScrollVolume)
            {
                int msg = (int)wParam;
                if (msg == WM_MOUSEWHEEL)
                {
                    try
                    {
                        var s = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                        bool isOver = _isCursorOverIcon || IsCursorOverTrayIcon() || IsPointNearTray(s.ptX, s.ptY);
                        if (isOver)
                        {
                            short delta = (short)((s.mouseData >> 16) & 0xFFFF);
                            if (delta != 0)
                            {
                                int dir = delta > 0 ? 1 : -1;
                                App.Log($"[TrayIconManager] Tray scroll captured: dir={dir}, x={s.ptX}, y={s.ptY}");
                                VolumeScrolled?.Invoke(dir);
                                return (IntPtr)1; // Consume event so behind windows don't scroll
                            }
                        }
                    }
                    catch { }
                }
                else if (msg == WM_MOUSEMOVE && _isCursorOverIcon)
                {
                    try
                    {
                        var s = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                        if (!IsPointNearTray(s.ptX, s.ptY))
                        {
                            _isCursorOverIcon = false;
                        }
                    }
                    catch { }
                }
            }
            return CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
        }

        private bool IsPointNearTray(int x, int y)
        {
            if (_hasKnownTrayRect)
            {
                if (x >= _lastKnownTrayRect.Left - 6 && x <= _lastKnownTrayRect.Right + 6 &&
                    y >= _lastKnownTrayRect.Top - 6 && y <= _lastKnownTrayRect.Bottom + 6)
                {
                    return true;
                }
            }

            if (_lastHoverPoint.X != 0 || _lastHoverPoint.Y != 0)
            {
                if (Math.Abs(x - _lastHoverPoint.X) <= 36 && Math.Abs(y - _lastHoverPoint.Y) <= 36)
                {
                    return true;
                }
            }

            return false;
        }

        private void UpdateTrayRect()
        {
            try
            {
                var nid = new NOTIFYICONIDENTIFIER
                {
                    cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
                    hWnd = _hostHwnd,
                    uID = 1001
                };

                if (Shell_NotifyIconGetRect(ref nid, out RECT r) == 0) // S_OK
                {
                    _lastKnownTrayRect = r;
                    _hasKnownTrayRect = true;
                }
            }
            catch { }
        }

        private bool IsCursorOverTrayIcon()
        {
            try
            {
                UpdateTrayRect();
                if (_hasKnownTrayRect && GetCursorPos(out POINT p))
                {
                    return p.X >= _lastKnownTrayRect.Left && p.X <= _lastKnownTrayRect.Right &&
                           p.Y >= _lastKnownTrayRect.Top && p.Y <= _lastKnownTrayRect.Bottom;
                }
            }
            catch { }
            return false;
        }

        private IntPtr SubclassWndProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData)
        {
            if (uMsg == WM_TRAYCALLBACK)
            {
                uint eventMsg = (uint)(lParam.ToInt64() & 0xFFFF);

                if (eventMsg == WM_MOUSEMOVE)
                {
                    _isCursorOverIcon = true;
                    if (GetCursorPos(out POINT pt))
                    {
                        _lastHoverPoint = pt;
                    }
                    UpdateTrayRect();
                    _hoverClearTimer?.Change(5000, System.Threading.Timeout.Infinite);
                }

                switch (eventMsg)
                {
                    case WM_LBUTTONUP:
                        HandleLeftClick();
                        break;

                    case WM_LBUTTONDBLCLK:
                        HandleDoubleClick();
                        break;

                    case NIN_SELECT:
                    case NIN_KEYSELECT:
                        // Keyboard tray activation (Enter/Space on tray icon)
                        HandleLeftClick();
                        break;

                    case WM_MBUTTONUP:
                        OpenMixerRequested?.Invoke();
                        break;

                    case WM_RBUTTONUP:
                    case WM_CONTEXTMENU:
                        ShowContextMenu();
                        break;

                    case WM_MOUSEWHEEL:
                        if (EnableScrollVolume)
                        {
                            short delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                            if (delta != 0)
                            {
                                int dir = delta > 0 ? 1 : -1;
                                VolumeScrolled?.Invoke(dir);
                            }
                        }
                        break;
                }
                return IntPtr.Zero;
            }
            else if (_wmTaskbarCreated != 0 && uMsg == _wmTaskbarCreated)
            {
                // Explorer restarted - re-register tray icon
                Shell_NotifyIconW(NIM_ADD, ref _nid);
                _nid.uTimeoutOrVersion = NOTIFYICON_VERSION_4;
                Shell_NotifyIconW(NIM_SETVERSION, ref _nid);
            }

            return DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        private readonly object _clickLock = new object();
        private DateTime _lastDoubleClickTime = DateTime.MinValue;

        private void HandleLeftClick()
        {
            lock (_clickLock)
            {
                int dblTime = Math.Max(250, Math.Min(600, GetDoubleClickTime()));
                var now = DateTime.Now;

                // Ignore trailing release from a double-click
                if ((now - _lastDoubleClickTime).TotalMilliseconds < (dblTime + 200))
                {
                    App.Log("[TrayIconManager] Left click ignored (trailing release from double-click)");
                    return;
                }

                var elapsed = (now - _lastLeftClickTime).TotalMilliseconds;

                // Ignore duplicate shell messages dispatched within 60ms of the same physical click
                if (elapsed < 60)
                {
                    return;
                }

                if (elapsed <= dblTime)
                {
                    // Double Click: Quick switch playback device!
                    App.Log($"[TrayIconManager] Double Click detected ({elapsed:F0}ms) -> Quick Switch Device");
                    _lastDoubleClickTime = now;
                    _lastLeftClickTime = DateTime.MinValue;
                    _singleClickTimer?.Dispose();
                    _singleClickTimer = null;
                    QuickSwitchRequested?.Invoke();
                }
                else
                {
                    // Single Click: Start timer to open Volume Mixer if no second click follows
                    _lastLeftClickTime = now;
                    _singleClickTimer?.Dispose();
                    _singleClickTimer = new System.Threading.Timer(_ =>
                    {
                        lock (_clickLock)
                        {
                            _lastLeftClickTime = DateTime.MinValue;
                            _singleClickTimer?.Dispose();
                            _singleClickTimer = null;
                        }
                        App.Log("[TrayIconManager] Single Click confirmed -> Open Volume Mixer");
                        OpenMixerRequested?.Invoke();
                    }, null, dblTime + 40, System.Threading.Timeout.Infinite);
                }
            }
        }

        private void HandleDoubleClick()
        {
            lock (_clickLock)
            {
                App.Log("[TrayIconManager] WM_LBUTTONDBLCLK received -> Quick Switch Device");
                _lastDoubleClickTime = DateTime.Now;
                _singleClickTimer?.Dispose();
                _singleClickTimer = null;
                _lastLeftClickTime = DateTime.MinValue;
                QuickSwitchRequested?.Invoke();
            }
        }

        private void ApplyMenuTheme()
        {
            try
            {
                bool isDark = true;
                if (App.CurrentApp?.Settings != null)
                {
                    string theme = App.CurrentApp.Settings.Theme;
                    isDark = theme switch
                    {
                        "Light" => false,
                        "Dark" => true,
                        _ => Microsoft.UI.Xaml.Application.Current?.RequestedTheme == Microsoft.UI.Xaml.ApplicationTheme.Dark
                    };
                }

                int mode = isDark ? 2 /* ForceDark */ : 3 /* ForceLight */;
                SetPreferredAppMode(mode);
                AllowDarkModeForWindow(_hostHwnd, isDark);
                SetWindowTheme(_hostHwnd, isDark ? "DarkMode_Explorer" : "Explorer", null);
                FlushMenuThemes();
            }
            catch (Exception ex)
            {
                App.Log($"[TrayIconManager] Error applying menu theme: {ex.Message}");
            }
        }

        private void ShowContextMenu()
        {
            ApplyMenuTheme();
            SetForegroundWindow(_hostHwnd);

            IntPtr hMenu = CreatePopupMenu();
            AppendMenu(hMenu, MF_STRING, CMD_MIXER, "Volume mixer");
            AppendMenu(hMenu, MF_STRING, CMD_SWITCH, "Quick switch device");
            AppendMenu(hMenu, MF_SEPARATOR, 0, string.Empty);
            AppendMenu(hMenu, MF_STRING, CMD_SETTINGS, "Settings");
            AppendMenu(hMenu, MF_STRING, CMD_EXIT, "Exit");

            SetMenuDefaultItem(hMenu, CMD_MIXER, 0);

            GetCursorPos(out POINT pt);
            uint selected = TrackPopupMenuEx(hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, _hostHwnd, IntPtr.Zero);
            DestroyMenu(hMenu);
            PostMessage(_hostHwnd, 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero);

            switch (selected)
            {
                case CMD_MIXER:
                    OpenMixerRequested?.Invoke();
                    break;
                case CMD_SWITCH:
                    QuickSwitchRequested?.Invoke();
                    break;
                case CMD_SETTINGS:
                    OpenSettingsRequested?.Invoke();
                    break;
                case CMD_EXIT:
                    ExitRequested?.Invoke();
                    break;
            }
        }

        public void UpdateIcon(string label, string fullName, int volume, bool isMuted)
        {
            if (_isDisposed) return;

            string muteText = isMuted ? " (Muted)" : "";
            string tipText = $"{label}: {fullName}\nVolume: {volume}%{muteText}";
            if (tipText.Length > 127) tipText = tipText.Substring(0, 124) + "...";

            _nid.szTip = tipText;

            IntPtr oldHIcon = _currentHIcon;
            _currentHIcon = GenerateTaskbarIconHandle(label, volume, isMuted);
            _nid.hIcon = _currentHIcon;

            bool modOk = Shell_NotifyIconW(NIM_MODIFY, ref _nid);
            if (!modOk)
            {
                int modErr = Marshal.GetLastWin32Error();
                App.Log($"[TrayIconManager] NIM_MODIFY failed (Error {modErr}). Attempting NIM_ADD fallback...");
                bool addOk = Shell_NotifyIconW(NIM_ADD, ref _nid);
                _nid.uTimeoutOrVersion = NOTIFYICON_VERSION_4;
                Shell_NotifyIconW(NIM_SETVERSION, ref _nid);
                App.Log($"[TrayIconManager] Fallback NIM_ADD result: {addOk}");
            }

            if (oldHIcon != IntPtr.Zero)
            {
                DestroyIcon(oldHIcon);
            }
        }

        private IntPtr GenerateTaskbarIconHandle(string label, int volume, bool isMuted)
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.Clear(Color.Transparent);

                    // 1. Draw Speaker Body
                    using (var pen = new Pen(Color.White, 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    using (var redPen = new Pen(Color.FromArgb(255, 75, 75), 3.0f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    using (var fillBrush = new SolidBrush(Color.White))
                    {
                        Point[] speakerPts = {
                            new Point(1, 10),
                            new Point(8, 10),
                            new Point(15, 2),
                            new Point(15, 30),
                            new Point(8, 22),
                            new Point(1, 22)
                        };
                        g.FillPolygon(fillBrush, speakerPts);

                        if (isMuted || volume == 0)
                        {
                            g.DrawLine(redPen, 18, 9, 29, 23);
                            g.DrawLine(redPen, 29, 9, 18, 23);
                        }
                        else
                        {
                            g.DrawArc(pen, 11, 8, 10, 16, -45, 90);
                            if (volume > 33) g.DrawArc(pen, 7, 4, 18, 24, -45, 90);
                            if (volume > 66) g.DrawArc(pen, 3, 0, 26, 32, -45, 90);
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
                        using (var badgeFont = new Font("Segoe UI", badge.Length > 1 ? 8.5F : 10.5F, FontStyle.Bold, GraphicsUnit.Pixel))
                        {
                            SizeF size = g.MeasureString(badge, badgeFont);
                            int badgeW = Math.Max((int)size.Width + 4, 14);
                            int badgeH = 14;
                            int badgeX = 32 - badgeW;
                            int badgeY = 32 - badgeH;

                            using (var bgBrush = new SolidBrush(Color.FromArgb(245, 10, 15, 22)))
                            using (var borderPen = new Pen(Color.FromArgb(0, 160, 255), 1.4f))
                            using (var path = GetRoundedRect(new Rectangle(badgeX, badgeY, badgeW - 1, badgeH - 1), 3))
                            {
                                g.FillPath(bgBrush, path);
                                g.DrawPath(borderPen, path);
                            }

                            using (var textBrush = new SolidBrush(Color.FromArgb(0, 215, 255)))
                            {
                                float tx = badgeX + (badgeW - size.Width) / 2f;
                                float ty = badgeY + (badgeH - size.Height) / 2f - 0.5f;
                                g.DrawString(badge, badgeFont, textBrush, tx, ty);
                            }
                        }
                    }
                }

                return bmp.GetHicon();
            }
        }

        private GraphicsPath GetRoundedRect(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            int diameter = radius * 2;
            var arc = new Rectangle(bounds.Location, new Size(diameter, diameter));

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

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            if (_mouseHookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_mouseHookId);
                _mouseHookId = IntPtr.Zero;
            }
            _hoverClearTimer?.Dispose();

            _singleClickTimer?.Dispose();
            Shell_NotifyIconW(NIM_DELETE, ref _nid);

            RemoveWindowSubclass(_hostHwnd, _subclassProc, new UIntPtr(1001));

            if (_currentHIcon != IntPtr.Zero)
            {
                DestroyIcon(_currentHIcon);
                _currentHIcon = IntPtr.Zero;
            }
        }
    }
}
