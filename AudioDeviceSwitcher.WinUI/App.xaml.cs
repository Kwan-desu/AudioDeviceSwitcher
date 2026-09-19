using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AudioSwitcher.AudioApi;
using AudioSwitcher.AudioApi.CoreAudio;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace AudioDeviceSwitcher
{
    public partial class App : Application
    {
        public static App CurrentApp => (App)Current;

        public AudioDeviceManager AudioManager { get; private set; } = null!;
        public AppSettings Settings { get; private set; } = null!;
        public GlobalHotkeyManager HotkeyManager { get; private set; } = null!;
        public TrayIconManager TrayManager { get; private set; } = null!;

        public SettingsWindow? CurrentSettingsWindow { get; private set; }
        public MixerWindow? CurrentMixerWindow { get; private set; }
        public OsdWindow? CurrentOsdWindow { get; private set; }

        private DispatcherQueue _dispatcherQueue = null!;
        private System.Threading.Timer? _pollTimer;

        public static void Log(string message)
        {
            try
            {
                var dir = AppContext.BaseDirectory;
                var logFile = System.IO.Path.Combine(dir, "app_debug.log");
                System.IO.File.AppendAllText(logFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}\r\n");
            }
            catch { }
        }

        public App()
        {
            Log("App() initializing...");
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Log($"[AppDomain UNHANDLED] {e.ExceptionObject}");
            };
            this.UnhandledException += (s, e) =>
            {
                Log($"[WinUI UNHANDLED] {e.Message}\r\nException: {e.Exception}\r\nStackTrace: {e.Exception?.StackTrace}");
                e.Handled = true;
            };

            try
            {
                InitializeComponent();
                Log("App InitializeComponent() succeeded.");
            }
            catch (Exception ex)
            {
                Log($"[App InitializeComponent EXCEPTION] {ex}");
                throw;
            }
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            Log("OnLaunched() started.");
            try
            {
                _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

                Log("Loading AppSettings...");
                Settings = AppSettings.Load();
                Log("Initializing AudioDeviceManager...");
                AudioManager = new AudioDeviceManager();

                Log("Creating SettingsWindow...");
                CurrentSettingsWindow = new SettingsWindow();
                var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(CurrentSettingsWindow);
                Log($"SettingsWindow created. HWND = 0x{hWnd:X}");

                Log("Initializing GlobalHotkeyManager...");
                HotkeyManager = new GlobalHotkeyManager(hWnd);
                HotkeyManager.QuickSwitchPressed += () => _dispatcherQueue.TryEnqueue(QuickSwitch);
                HotkeyManager.OpenMixerPressed += () => _dispatcherQueue.TryEnqueue(ShowMixerWindow);
                HotkeyManager.RegisterHotkeys(Settings.EnableGlobalHotkeys, Settings.QuickSwitchHotkey, Settings.OpenMixerHotkey);

                Log("Initializing TrayIconManager...");
                TrayManager = new TrayIconManager(hWnd);
                TrayManager.EnableScrollVolume = Settings.EnableTrayScrollVolume;
                TrayManager.QuickSwitchRequested += () => _dispatcherQueue.TryEnqueue(QuickSwitch);
                TrayManager.OpenMixerRequested += () => _dispatcherQueue.TryEnqueue(ShowMixerWindow);
                TrayManager.OpenSettingsRequested += () => _dispatcherQueue.TryEnqueue(ShowSettingsWindow);
                TrayManager.VolumeScrolled += (dir) => _dispatcherQueue.TryEnqueue(() => HandleTrayScrollVolume(dir));
                TrayManager.ExitRequested += () => _dispatcherQueue.TryEnqueue(ExitApp);
                Log("TrayIconManager initialized.");

                AudioManager.DevicesChanged += () =>
                {
                    _dispatcherQueue.TryEnqueue(() =>
                    {
                        UpdateTrayState();
                        CurrentMixerWindow?.ReloadDevices();
                        CurrentSettingsWindow?.ReloadDevices();
                    });
                };

                Log("Calling UpdateTrayState()...");
                UpdateTrayState();
                Log("UpdateTrayState() completed.");

                // Background polling timer for volume changes from other apps
                _pollTimer = new System.Threading.Timer(_ =>
                {
                    _dispatcherQueue.TryEnqueue(UpdateTrayState);
                }, null, 1000, 1000);

                if (Settings.RunAtStartup)
                {
                    StartupManager.UpdateStartup(true);
                }

                string[] cmdArgs = Environment.GetCommandLineArgs();
                bool startMinimized = cmdArgs.Any(a => a.Equals("--startup", StringComparison.OrdinalIgnoreCase) ||
                                                       a.Equals("--minimized", StringComparison.OrdinalIgnoreCase) ||
                                                       a.Equals("/startup", StringComparison.OrdinalIgnoreCase) ||
                                                       a.Equals("/minimized", StringComparison.OrdinalIgnoreCase)) ||
                                      Settings.StartMinimized;

                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
                var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

                if (startMinimized)
                {
                    Log("Start minimized requested - keeping window hidden in tray.");
                    appWindow?.Hide();
                }
                else
                {
                    Log("Showing and activating SettingsWindow...");
                    appWindow?.Show();
                    CurrentSettingsWindow.Activate();
                }
                Log("OnLaunched() finished successfully.");
            }
            catch (Exception ex)
            {
                Log($"[OnLaunched EXCEPTION] {ex}");
                throw;
            }
        }

        public void UpdateTrayState()
        {
            try
            {
                var activeDevices = AudioManager.GetActivePlaybackDevices();
                Settings.SyncActiveDevices(activeDevices);

                var currentDefault = AudioManager.GetDefaultPlaybackDevice();
                string fullName = currentDefault?.FullName ?? "Unknown";

                string label = "AUX 1";
                int volume = 50;
                bool isMuted = false;

                if (currentDefault != null)
                {
                    int index = Settings.SelectedDeviceIds.IndexOf(currentDefault.Id);
                    label = Settings.GetLabelForDevice(currentDefault.Id, currentDefault.FullName, Math.Max(0, index));
                    volume = Math.Clamp((int)currentDefault.Volume, 0, 100);
                    isMuted = currentDefault.IsMuted;
                }

                TrayManager.UpdateIcon(label, fullName, volume, isMuted);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[App] UpdateTrayState error: {ex.Message}");
            }
        }

        public void QuickSwitch()
        {
            Log("[App] QuickSwitch called.");
            var result = AudioManager.SwitchToNextDevice(Settings);
            Log($"[App] QuickSwitch result: Status={result.Status}, SwitchedTo={result.SwitchedToDevice?.FullName}");
            UpdateTrayState();

            switch (result.Status)
            {
                case SwitchStatus.Success:
                    if (result.SwitchedToDevice != null)
                    {
                        bool isHeadphone = result.SwitchedToDevice.FullName.Contains("head", StringComparison.OrdinalIgnoreCase) ||
                                           result.SwitchedToDevice.FullName.Contains("ear", StringComparison.OrdinalIgnoreCase);
                        string switchGlyph = isHeadphone ? "\uE7F6" : "\uE995";
                        ShowOsd("Audio playback switched", result.SwitchedToDevice.FullName, switchGlyph);
                    }
                    break;

                case SwitchStatus.TargetDeviceDisconnected:
                    ShowOsd("Device is disconnected", result.DisconnectedDeviceName ?? "Other audio device", "\uE7BA");
                    break;

                case SwitchStatus.NeedMoreDevicesConfigured:
                    ShowSettingsWindow();
                    break;
            }
        }

        public void HandleTrayScrollVolume(int direction)
        {
            var currentDefault = AudioManager.GetDefaultPlaybackDevice();
            if (currentDefault != null)
            {
                double currentVol = currentDefault.Volume;
                int step = Settings.ScrollVolumeStep > 0 ? Settings.ScrollVolumeStep : 2;
                double newVol = Math.Clamp(currentVol + (direction * step), 0, 100);

                if (direction > 0 && currentDefault.IsMuted)
                {
                    currentDefault.Mute(false);
                }

                currentDefault.Volume = newVol;
                UpdateTrayState();

                string glyph = currentDefault.IsMuted || (int)newVol == 0 ? "\uE74F" : (newVol > 66 ? "\uE995" : (newVol > 33 ? "\uE994" : "\uE993"));

                ShowOsd("Volume adjusted", currentDefault.FullName, glyph, currentDefault.IsMuted ? 0 : (int)newVol);
            }
        }

        public void ShowSettingsWindow()
        {
            if (CurrentSettingsWindow == null)
            {
                CurrentSettingsWindow = new SettingsWindow();
                var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(CurrentSettingsWindow);
                TrayManager = new TrayIconManager(hWnd);
            }

            CurrentSettingsWindow.ShowAndActivate();
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private DateTime _lastMixerClosedTime = DateTime.MinValue;

        public void ShowMixerWindow()
        {
            var now = DateTime.UtcNow;
            if (CurrentMixerWindow != null)
            {
                CurrentMixerWindow.Close();
                CurrentMixerWindow = null;
                _lastMixerClosedTime = now;
                return;
            }

            if ((now - _lastMixerClosedTime).TotalMilliseconds < 800)
            {
                Log("[App] ShowMixerWindow skipped (recently closed/toggled via tray click)");
                return;
            }

            try
            {
                CurrentMixerWindow = new MixerWindow();
                CurrentMixerWindow.Closed += (s, e) =>
                {
                    CurrentMixerWindow = null;
                    _lastMixerClosedTime = DateTime.UtcNow;
                };
                CurrentMixerWindow.Activate();
                try
                {
                    var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(CurrentMixerWindow);
                    SetForegroundWindow(hWnd);
                }
                catch { }
            }
            catch (Exception ex)
            {
                Log($"[App] ShowMixerWindow error: {ex}");
            }
        }

        public void ShowOsd(string title, string deviceName, string glyph, int? volume = null)
        {
            Log($"[App] ShowOsd called: title='{title}', device='{deviceName}', glyph='{glyph}', vol={volume}");
            try
            {
                if (CurrentOsdWindow == null || CurrentOsdWindow.IsClosed)
                {
                    Log("[App] Creating new OsdWindow instance.");
                    CurrentOsdWindow = new OsdWindow();
                    CurrentOsdWindow.Closed += (s, e) => CurrentOsdWindow = null;
                }

                CurrentOsdWindow.ShowOsd(title, deviceName, glyph, volume);
            }
            catch (Exception ex)
            {
                Log($"[App] ShowOsd error: {ex.Message}");
                try
                {
                    CurrentOsdWindow = new OsdWindow();
                    CurrentOsdWindow.Closed += (s, e) => CurrentOsdWindow = null;
                    CurrentOsdWindow.ShowOsd(title, deviceName, glyph, volume);
                }
                catch (Exception ex2)
                {
                    Log($"[App] ShowOsd retry error: {ex2.Message}");
                }
            }
        }

        public void ExitApp()
        {
            _pollTimer?.Dispose();
            TrayManager?.Dispose();
            HotkeyManager?.Dispose();
            AudioManager?.Dispose();

            CurrentMixerWindow?.Close();
            CurrentOsdWindow?.Close();

            Environment.Exit(0);
        }
    }
}
