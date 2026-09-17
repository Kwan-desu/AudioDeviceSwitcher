using System;
using System.Collections.Generic;
using System.Linq;
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

        // fsModifiers flags for RegisterHotKey
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_WIN = 0x0008;
        private const uint MOD_NOREPEAT = 0x4000;

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

        /// <summary>
        /// Registers the user-configured hotkeys. Legacy overload keeps default combos so
        /// existing callers that don't pass settings still work.
        /// </summary>
        public void RegisterHotkeys(bool enable)
        {
            RegisterHotkeys(enable, "Ctrl+Shift+S", "Ctrl+Shift+M");
        }

        /// <summary>
        /// Registers the given quick-switch and open-mixer hotkey strings (e.g. "Ctrl+Shift+S").
        /// Returns which registrations succeeded so the UI can surface conflicts.
        /// </summary>
        public (bool quickSwitchOk, bool openMixerOk) RegisterHotkeys(bool enable, string quickSwitch, string openMixer)
        {
            UnregisterHotKey(_window.Handle, ID_QUICKSWITCH);
            UnregisterHotKey(_window.Handle, ID_OPENMIXER);

            if (!enable) return (true, true);

            bool qOk = true, mOk = true;

            if (TryParse(quickSwitch, out uint qMods, out uint qVk))
            {
                qOk = RegisterHotKey(_window.Handle, ID_QUICKSWITCH, qMods | MOD_NOREPEAT, qVk);
            }
            else qOk = false;

            if (TryParse(openMixer, out uint mMods, out uint mVk))
            {
                mOk = RegisterHotKey(_window.Handle, ID_OPENMIXER, mMods | MOD_NOREPEAT, mVk);
            }
            else mOk = false;

            if (!qOk) System.Diagnostics.Debug.WriteLine($"[Hotkey] quick-switch '{quickSwitch}' failed to register.");
            if (!mOk) System.Diagnostics.Debug.WriteLine($"[Hotkey] open-mixer '{openMixer}' failed to register.");

            return (qOk, mOk);
        }

        /// <summary>
        /// Parses a "Ctrl+Shift+S" style string into Win32 modifier flags + virtual key.
        /// Accepts Ctrl/Control, Shift, Alt, Win/Windows tokens in any order.
        /// </summary>
        public static bool TryParse(string? hotkey, out uint modifiers, out uint vk)
        {
            modifiers = 0;
            vk = 0;
            if (string.IsNullOrWhiteSpace(hotkey)) return false;

            var parts = hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string? keyToken = null;

            foreach (var p in parts)
            {
                switch (p.ToLowerInvariant())
                {
                    case "ctrl":
                    case "control": modifiers |= MOD_CONTROL; break;
                    case "shift": modifiers |= MOD_SHIFT; break;
                    case "alt": modifiers |= MOD_ALT; break;
                    case "win":
                    case "windows":
                    case "meta": modifiers |= MOD_WIN; break;
                    default: keyToken = p; break; // last non-modifier wins
                }
            }

            if (keyToken == null) return false;

            if (Enum.TryParse<Keys>(keyToken, ignoreCase: true, out var key))
            {
                vk = (uint)key;
                return modifiers != 0 && vk != 0; // require at least one modifier
            }

            // Single letter/number fallback (e.g. "S", "1")
            if (keyToken.Length == 1)
            {
                char c = char.ToUpperInvariant(keyToken[0]);
                if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
                {
                    vk = c;
                    return modifiers != 0;
                }
            }

            return false;
        }

        /// <summary>Normalizes a hotkey string for display/storage (e.g. "shift+ctrl+s" → "Ctrl+Shift+S").</summary>
        public static string Normalize(string? hotkey)
        {
            if (!TryParse(hotkey, out uint mods, out uint vk)) return hotkey ?? "";
            var parts = new List<string>();
            if ((mods & MOD_CONTROL) != 0) parts.Add("Ctrl");
            if ((mods & MOD_SHIFT) != 0) parts.Add("Shift");
            if ((mods & MOD_ALT) != 0) parts.Add("Alt");
            if ((mods & MOD_WIN) != 0) parts.Add("Win");
            parts.Add(((Keys)vk).ToString());
            return string.Join("+", parts);
        }

        public void Dispose()
        {
            RegisterHotkeys(false);
            _window.Dispose();
        }
    }
}
