using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AudioDeviceSwitcher
{
    public class GlobalHotkeyManager : IDisposable
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass, UIntPtr dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass);

        [DllImport("comctl32.dll")]
        private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        public delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData);

        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_WIN = 0x0008;
        private const uint MOD_NOREPEAT = 0x4000;

        private const int WM_HOTKEY = 0x0312;

        public const int ID_QUICKSWITCH = 1;
        public const int ID_OPENMIXER = 2;

        private readonly IntPtr _hostHwnd;
        private readonly SubclassProc _subclassProc;

        public event Action? QuickSwitchPressed;
        public event Action? OpenMixerPressed;

        public GlobalHotkeyManager(IntPtr hostHwnd)
        {
            _hostHwnd = hostHwnd;
            _subclassProc = SubclassWndProc;

            if (_hostHwnd != IntPtr.Zero)
            {
                SetWindowSubclass(_hostHwnd, _subclassProc, new UIntPtr(2001), UIntPtr.Zero);
                App.Log($"[GlobalHotkeyManager] Subclassed host window 0x{_hostHwnd:X} for WM_HOTKEY");
            }
            else
            {
                App.Log("[GlobalHotkeyManager] WARNING: hostHwnd is IntPtr.Zero");
            }
        }

        private IntPtr SubclassWndProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData)
        {
            if (uMsg == WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                App.Log($"[GlobalHotkeyManager] WM_HOTKEY received: id={id}");
                if (id == ID_QUICKSWITCH) QuickSwitchPressed?.Invoke();
                else if (id == ID_OPENMIXER) OpenMixerPressed?.Invoke();
                return IntPtr.Zero;
            }
            return DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        public (bool quickSwitchOk, bool openMixerOk) RegisterHotkeys(bool enable, string quickSwitch, string openMixer)
        {
            if (_hostHwnd == IntPtr.Zero) return (false, false);

            UnregisterHotKey(_hostHwnd, ID_QUICKSWITCH);
            UnregisterHotKey(_hostHwnd, ID_OPENMIXER);

            if (!enable)
            {
                App.Log("[GlobalHotkeyManager] Hotkeys disabled by settings.");
                return (true, true);
            }

            bool qOk = false, mOk = false;

            if (TryParse(quickSwitch, out uint qMods, out uint qVk))
            {
                qOk = RegisterHotKey(_hostHwnd, ID_QUICKSWITCH, qMods | MOD_NOREPEAT, qVk);
                if (!qOk)
                {
                    int err = Marshal.GetLastWin32Error();
                    App.Log($"[GlobalHotkeyManager] RegisterHotKey for QuickSwitch '{quickSwitch}' failed (Error {err})");
                }
                else
                {
                    App.Log($"[GlobalHotkeyManager] Registered QuickSwitch hotkey: '{quickSwitch}'");
                }
            }
            else
            {
                App.Log($"[GlobalHotkeyManager] Failed to parse QuickSwitch hotkey: '{quickSwitch}'");
            }

            if (TryParse(openMixer, out uint mMods, out uint mVk))
            {
                mOk = RegisterHotKey(_hostHwnd, ID_OPENMIXER, mMods | MOD_NOREPEAT, mVk);
                if (!mOk)
                {
                    int err = Marshal.GetLastWin32Error();
                    App.Log($"[GlobalHotkeyManager] RegisterHotKey for OpenMixer '{openMixer}' failed (Error {err})");
                }
                else
                {
                    App.Log($"[GlobalHotkeyManager] Registered OpenMixer hotkey: '{openMixer}'");
                }
            }
            else
            {
                App.Log($"[GlobalHotkeyManager] Failed to parse OpenMixer hotkey: '{openMixer}'");
            }

            return (qOk, mOk);
        }

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
                    default: keyToken = p; break;
                }
            }

            if (keyToken == null) return false;

            // Single letter or number
            if (keyToken.Length == 1)
            {
                char c = char.ToUpperInvariant(keyToken[0]);
                if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
                {
                    vk = c;
                    return modifiers != 0;
                }
            }

            // Function keys F1-F24 (standalone or with modifiers)
            if (keyToken.StartsWith("F", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(keyToken.Substring(1), out int fNum) && fNum >= 1 && fNum <= 24)
            {
                vk = (uint)(0x70 + (fNum - 1));
                return true;
            }

            // Numpad 0-9
            if (keyToken.StartsWith("Num", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(keyToken.Substring(3), out int numVal) && numVal >= 0 && numVal <= 9)
            {
                vk = (uint)(0x60 + numVal);
                return modifiers != 0;
            }

            // Named keys
            switch (keyToken.ToLowerInvariant())
            {
                case "space": vk = 0x20; return modifiers != 0;
                case "tab": vk = 0x09; return modifiers != 0;
                case "enter": case "return": vk = 0x0D; return modifiers != 0;
                case "escape": case "esc": vk = 0x1B; return modifiers != 0;
                case "insert": case "ins": vk = 0x2D; return modifiers != 0;
                case "delete": case "del": vk = 0x2E; return modifiers != 0;
                case "home": vk = 0x24; return modifiers != 0;
                case "end": vk = 0x23; return modifiers != 0;
                case "pageup": case "pgup": vk = 0x21; return modifiers != 0;
                case "pagedown": case "pgdn": vk = 0x22; return modifiers != 0;
                case "up": vk = 0x26; return modifiers != 0;
                case "down": vk = 0x28; return modifiers != 0;
                case "left": vk = 0x25; return modifiers != 0;
                case "right": vk = 0x27; return modifiers != 0;

                // Symbols
                case ";": vk = 0xBA; return modifiers != 0;
                case "=": vk = 0xBB; return modifiers != 0;
                case ",": vk = 0xBC; return modifiers != 0;
                case "-": vk = 0xBD; return modifiers != 0;
                case ".": vk = 0xBE; return modifiers != 0;
                case "/": vk = 0xBF; return modifiers != 0;
                case "`": vk = 0xC0; return modifiers != 0;
                case "[": vk = 0xDB; return modifiers != 0;
                case "\\": vk = 0xDC; return modifiers != 0;
                case "]": vk = 0xDD; return modifiers != 0;
                case "'": vk = 0xDE; return modifiers != 0;
            }

            return false;
        }

        public static string Normalize(string? hotkey)
        {
            if (!TryParse(hotkey, out uint mods, out uint vk)) return hotkey ?? "";
            var parts = new List<string>();
            if ((mods & MOD_CONTROL) != 0) parts.Add("Ctrl");
            if ((mods & MOD_ALT) != 0) parts.Add("Alt");
            if ((mods & MOD_SHIFT) != 0) parts.Add("Shift");
            if ((mods & MOD_WIN) != 0) parts.Add("Win");

            if (vk >= 'A' && vk <= 'Z') parts.Add(((char)vk).ToString());
            else if (vk >= '0' && vk <= '9') parts.Add(((char)vk).ToString());
            else if (vk >= 0x70 && vk <= 0x87) parts.Add($"F{vk - 0x70 + 1}");
            else if (vk >= 0x60 && vk <= 0x69) parts.Add($"Num{vk - 0x60}");
            else
            {
                string name = vk switch
                {
                    0x20 => "Space",
                    0x09 => "Tab",
                    0x0D => "Enter",
                    0x1B => "Esc",
                    0x2D => "Insert",
                    0x2E => "Delete",
                    0x24 => "Home",
                    0x23 => "End",
                    0x21 => "PageUp",
                    0x22 => "PageDown",
                    0x26 => "Up",
                    0x28 => "Down",
                    0x25 => "Left",
                    0x27 => "Right",
                    0xBA => ";",
                    0xBB => "=",
                    0xBC => ",",
                    0xBD => "-",
                    0xBE => ".",
                    0xBF => "/",
                    0xC0 => "`",
                    0xDB => "[",
                    0xDC => "\\",
                    0xDD => "]",
                    0xDE => "'",
                    _ => $"0x{vk:X}"
                };
                parts.Add(name);
            }

            return string.Join("+", parts);
        }

        public void Dispose()
        {
            if (_hostHwnd != IntPtr.Zero)
            {
                UnregisterHotKey(_hostHwnd, ID_QUICKSWITCH);
                UnregisterHotKey(_hostHwnd, ID_OPENMIXER);
                RemoveWindowSubclass(_hostHwnd, _subclassProc, new UIntPtr(2001));
            }
        }
    }
}
