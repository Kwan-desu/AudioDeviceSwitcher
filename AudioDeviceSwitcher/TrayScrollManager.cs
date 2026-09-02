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

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct NOTIFYICONIDENTIFIER
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public Guid guidItem;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }
        #endregion

        private readonly NotifyIcon _notifyIcon;
        private IntPtr _hookId = IntPtr.Zero;
        private readonly LowLevelMouseProc _proc;
        private bool _isEnabled;

        public event Action<int>? Scrolled; // +1 for up, -1 for down

        public TrayScrollManager(NotifyIcon notifyIcon)
        {
            _notifyIcon = notifyIcon;
            _proc = HookCallback;
        }

        public void SetEnabled(bool enabled)
        {
            _isEnabled = enabled;
            if (enabled && _hookId == IntPtr.Zero)
            {
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
            if (nCode >= 0 && (int)wParam == WM_MOUSEWHEEL && _isEnabled)
            {
                MSLLHOOKSTRUCT hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                if (IsCursorOverTrayIcon(hookStruct.pt))
                {
                    short delta = (short)((hookStruct.mouseData >> 16) & 0xFFFF);
                    if (delta != 0)
                    {
                        int direction = delta > 0 ? 1 : -1;
                        Scrolled?.Invoke(direction);
                        return (IntPtr)1; // Handled, prevent propagating scroll
                    }
                }
            }

            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        private bool IsCursorOverTrayIcon(POINT pt)
        {
            var (hWnd, uID) = GetNotifyIconIdentifier(_notifyIcon);
            if (hWnd == IntPtr.Zero) return false;

            var nid = new NOTIFYICONIDENTIFIER
            {
                cbSize = (uint)Marshal.SizeOf(typeof(NOTIFYICONIDENTIFIER)),
                hWnd = hWnd,
                uID = uID,
                guidItem = Guid.Empty
            };

            int hr = Shell_NotifyIconGetRect(ref nid, out RECT rect);
            if (hr == 0) // S_OK
            {
                return pt.X >= rect.Left && pt.X <= rect.Right &&
                       pt.Y >= rect.Top && pt.Y <= rect.Bottom;
            }

            return false;
        }

        private static (IntPtr hWnd, uint uID) GetNotifyIconIdentifier(NotifyIcon notifyIcon)
        {
            IntPtr hWnd = IntPtr.Zero;
            uint uID = 0;

            try
            {
                var type = typeof(NotifyIcon);
                var windowField = type.GetField("window", BindingFlags.NonPublic | BindingFlags.Instance)
                               ?? type.GetField("_window", BindingFlags.NonPublic | BindingFlags.Instance);
                if (windowField?.GetValue(notifyIcon) is NativeWindow nw)
                {
                    hWnd = nw.Handle;
                }

                var idField = type.GetField("id", BindingFlags.NonPublic | BindingFlags.Instance)
                           ?? type.GetField("_id", BindingFlags.NonPublic | BindingFlags.Instance);
                if (idField?.GetValue(notifyIcon) is int id)
                {
                    uID = (uint)id;
                }
            }
            catch { }

            return (hWnd, uID);
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
