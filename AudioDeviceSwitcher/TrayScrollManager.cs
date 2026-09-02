using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AudioDeviceSwitcher
{
    public class TrayScrollManager : IDisposable
    {
        #region Win32 P/Invoke & Structures
        private const int WH_MOUSE_LL = 14;
        private const int WM_MOUSEWHEEL = 0x020A;

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("shell32.dll", SetLastError = true)]
        private static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT iconLocation);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

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
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }
        #endregion

        private readonly NotifyIcon _notifyIcon;
        private readonly System.Windows.Threading.Dispatcher _uiDispatcher;
        private IntPtr _hookId = IntPtr.Zero;
        private readonly LowLevelMouseProc _proc; // Must hold reference to prevent GC collection
        private bool _isEnabled;

        // Cached tray icon handle (extracted once to avoid per-scroll reflection)
        private IntPtr _cachedHwnd = IntPtr.Zero;
        private uint _cachedUid = 0;

        public event Action<int>? Scrolled; // +1 = louder, -1 = quieter

        public TrayScrollManager(NotifyIcon notifyIcon)
        {
            _notifyIcon = notifyIcon;
            _proc = HookCallback;
            // Capture current dispatcher (UI thread) so we can marshal events back
            _uiDispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;

            RefreshIconHandle();
        }

        /// <summary>Extract the NotifyIcon's hidden HWND and icon-ID via reflection.</summary>
        private void RefreshIconHandle()
        {
            try
            {
                var type = typeof(NotifyIcon);

                // 'window' is of type NotifyIcon+NotifyIconNativeWindow which inherits NativeWindow
                var windowField = type.GetField("window", BindingFlags.NonPublic | BindingFlags.Instance);
                var windowObj = windowField?.GetValue(_notifyIcon);
                if (windowObj is NativeWindow nw && nw.Handle != IntPtr.Zero)
                {
                    _cachedHwnd = nw.Handle;
                }

                var idField = type.GetField("id", BindingFlags.NonPublic | BindingFlags.Instance);
                if (idField?.GetValue(_notifyIcon) is int id)
                {
                    _cachedUid = (uint)id;
                }
            }
            catch { }
        }

        public void SetEnabled(bool enabled)
        {
            _isEnabled = enabled;

            if (enabled && _hookId == IntPtr.Zero)
            {
                if (_cachedHwnd == IntPtr.Zero)
                    RefreshIconHandle();

                using var curProcess = Process.GetCurrentProcess();
                using var curModule = curProcess.MainModule;
                IntPtr hMod = curModule != null ? GetModuleHandle(curModule.ModuleName) : IntPtr.Zero;
                _hookId = SetWindowsHookEx(WH_MOUSE_LL, _proc, hMod, 0);
            }
            else if (!enabled && _hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            // *** This runs on the system hook thread — NOT the UI thread. ***
            // Keep processing minimal; dispatch all UI work via _uiDispatcher.
            if (nCode >= 0 && (int)wParam == WM_MOUSEWHEEL && _isEnabled)
            {
                try
                {
                    MSLLHOOKSTRUCT hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);

                    if (IsCursorOverTrayIcon(hookStruct.pt))
                    {
                        short delta = (short)((hookStruct.mouseData >> 16) & 0xFFFF);
                        if (delta != 0)
                        {
                            int direction = delta > 0 ? 1 : -1;

                            // Marshal to UI thread before touching any WPF/WinForms objects
                            _uiDispatcher.BeginInvoke(() => Scrolled?.Invoke(direction));

                            return (IntPtr)1; // Consume this wheel message
                        }
                    }
                }
                catch { }
            }

            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        private bool IsCursorOverTrayIcon(POINT pt)
        {
            if (_cachedHwnd == IntPtr.Zero)
                RefreshIconHandle();

            if (_cachedHwnd == IntPtr.Zero)
                return false;

            var nid = new NOTIFYICONIDENTIFIER
            {
                cbSize = (uint)Marshal.SizeOf(typeof(NOTIFYICONIDENTIFIER)),
                hWnd = _cachedHwnd,
                uID = _cachedUid,
                guidItem = Guid.Empty
            };

            int hr = Shell_NotifyIconGetRect(ref nid, out RECT rect);
            if (hr == 0) // S_OK — icon is visible in the notification area
            {
                return pt.X >= rect.Left && pt.X <= rect.Right &&
                       pt.Y >= rect.Top  && pt.Y <= rect.Bottom;
            }

            // S_FALSE (0x1) means icon is hidden in overflow chevron — not visible
            return false;
        }

        public void Dispose()
        {
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
        }
    }
}
