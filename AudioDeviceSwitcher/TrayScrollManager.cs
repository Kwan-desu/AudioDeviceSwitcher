using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace AudioDeviceSwitcher
{
    /// <summary>
    /// Installs a global low-level mouse hook and fires <see cref="Scrolled"/>
    /// when the user scrolls while the cursor is over the notification-area tray icon.
    ///
    /// Hover detection uses <see cref="NotifyIcon.MouseMove"/> (reliable, no
    /// fragile Shell_NotifyIconGetRect P/Invoke). A background timer clears the
    /// hover flag 1.5 s after the last MouseMove fires so slow scrollers still work.
    /// </summary>
    public class TrayScrollManager : IDisposable
    {
        #region Win32 P/Invoke
        private const int WH_MOUSE_LL = 14;
        private const int WM_MOUSEWHEEL = 0x020A;

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

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public int ptX, ptY;
            public uint mouseData;
            public uint flags, time;
            public IntPtr dwExtraInfo;
        }
        #endregion

        private readonly NotifyIcon _notifyIcon;
        private readonly System.Windows.Threading.Dispatcher _uiDispatcher;
        private readonly LowLevelMouseProc _proc; // must be kept alive to prevent GC

        private IntPtr _hookId = IntPtr.Zero;
        private bool _isEnabled;

        // True while the cursor is hovering over the tray icon.
        // Set by NotifyIcon.MouseMove; cleared by a 1.5 s timer after no movement.
        private volatile bool _isCursorOverIcon = false;
        private System.Threading.Timer? _hoverClearTimer;

        public event Action<int>? Scrolled; // +1 louder, -1 quieter

        public TrayScrollManager(NotifyIcon notifyIcon)
        {
            _notifyIcon = notifyIcon;
            _proc = HookCallback;
            _uiDispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;

            // MouseMove fires whenever the cursor is on the icon — no HWND magic needed
            _notifyIcon.MouseMove += NotifyIcon_MouseMove;
        }

        // ── Hover tracking ──────────────────────────────────────────────────────

        private void NotifyIcon_MouseMove(object? sender, MouseEventArgs e)
        {
            _isCursorOverIcon = true;
            // Extend/reset the auto-clear timer on every move event
            _hoverClearTimer?.Change(1500, System.Threading.Timeout.Infinite);
        }

        private void ClearHover()
        {
            _isCursorOverIcon = false;
        }

        // ── Hook lifecycle ───────────────────────────────────────────────────────

        public void SetEnabled(bool enabled)
        {
            _isEnabled = enabled;

            if (enabled && _hookId == IntPtr.Zero)
            {
                // Lazily create the timer (fires once after 1.5 s of no MouseMove)
                _hoverClearTimer ??= new System.Threading.Timer(_ => ClearHover(),
                                                                null,
                                                                System.Threading.Timeout.Infinite,
                                                                System.Threading.Timeout.Infinite);

                using var curProcess = Process.GetCurrentProcess();
                using var curModule  = curProcess.MainModule;
                IntPtr hMod = curModule != null ? GetModuleHandle(curModule.ModuleName) : IntPtr.Zero;
                _hookId = SetWindowsHookEx(WH_MOUSE_LL, _proc, hMod, 0);
            }
            else if (!enabled && _hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
                _isCursorOverIcon = false;
            }
        }

        // ── Hook callback (runs on the Windows hook thread — NOT the UI thread) ─

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (int)wParam == WM_MOUSEWHEEL && _isEnabled && _isCursorOverIcon)
            {
                try
                {
                    var s = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    short delta = (short)((s.mouseData >> 16) & 0xFFFF);
                    if (delta != 0)
                    {
                        int direction = delta > 0 ? 1 : -1;
                        // Marshal to UI thread before touching any WPF / audio objects
                        _uiDispatcher.BeginInvoke(() => Scrolled?.Invoke(direction));
                        return (IntPtr)1; // Consume — prevent the event reaching other windows
                    }
                }
                catch { /* never throw inside a hook callback */ }
            }

            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        // ── IDisposable ──────────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }

            _hoverClearTimer?.Dispose();
            _notifyIcon.MouseMove -= NotifyIcon_MouseMove;
        }
    }
}
