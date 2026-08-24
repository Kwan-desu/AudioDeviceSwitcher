using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AudioDeviceSwitcher
{
    public class GlobalHotkeyManager : IDisposable
    {
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private class HotkeyMessageWindow : NativeWindow, IDisposable
        {
            public event Action<int>? HotKeyPressed;
            private const int WM_HOTKEY = 0x0312;

            public HotkeyMessageWindow()
            {
                CreateHandle(new CreateParams());
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_HOTKEY)
                {
                    int id = m.WParam.ToInt32();
                    HotKeyPressed?.Invoke(id);
                }
                base.WndProc(ref m);
            }

            public void Dispose()
            {
                DestroyHandle();
            }
        }

        private readonly HotkeyMessageWindow _window;
        public event Action? QuickSwitchPressed;
        public event Action? OpenMixerPressed;

        private const int ID_QUICKSWITCH = 1;
        private const int ID_OPENMIXER = 2;

        public GlobalHotkeyManager()
        {
            _window = new HotkeyMessageWindow();
            _window.HotKeyPressed += OnHotKeyPressed;
        }

        private void OnHotKeyPressed(int id)
        {
            if (id == ID_QUICKSWITCH) QuickSwitchPressed?.Invoke();
            else if (id == ID_OPENMIXER) OpenMixerPressed?.Invoke();
        }

        public void RegisterHotkeys(bool enable)
        {
            UnregisterHotKey(_window.Handle, ID_QUICKSWITCH);
            UnregisterHotKey(_window.Handle, ID_OPENMIXER);

            if (enable)
            {
                // Register hardcoded default hotkeys for now
                // Quick Switch: Ctrl + Shift + S
                RegisterHotKey(_window.Handle, ID_QUICKSWITCH, 0x0002 | 0x0004, (uint)Keys.S);
                
                // Open Mixer: Ctrl + Shift + M
                RegisterHotKey(_window.Handle, ID_OPENMIXER, 0x0002 | 0x0004, (uint)Keys.M);
            }
        }

        public void Dispose()
        {
            RegisterHotkeys(false);
            _window.Dispose();
        }
    }
}
