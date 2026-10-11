using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AudioDeviceSwitcher
{
    #region Native WASAPI COM Interfaces
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IntPtr ppDevices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
        [PreserveSig] int GetDevice(string pwstrId, out IMMDevice ppDevice);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr pClient);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr pClient);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        [PreserveSig] int OpenPropertyStore(int stgmAccess, out IntPtr ppProperties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
        [PreserveSig] int GetState(out int pdwState);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(ref Guid AudioSessionGuid, uint StreamFlags, out IntPtr SessionControl);
        [PreserveSig] int GetSimpleAudioVolume(ref Guid AudioSessionGuid, uint StreamFlags, out IntPtr AudioVolume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator SessionEnum);
        [PreserveSig] int RegisterSessionNotification(IntPtr NewSessionNotifications);
        [PreserveSig] int UnregisterSessionNotification(IntPtr NewSessionNotifications);
        [PreserveSig] int RegisterDuckNotification(string sessionID, IntPtr duckNotification);
        [PreserveSig] int UnregisterDuckNotification(IntPtr duckNotification);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int SessionCount);
        [PreserveSig] int GetSession(int SessionIndex, out IAudioSessionControl Session);
    }

    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl
    {
        [PreserveSig] int GetState(out int pRetVal);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] int SetDisplayName(string Value, ref Guid EventContext);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] int SetIconPath(string Value, ref Guid EventContext);
        [PreserveSig] int GetGroupingParam(out Guid pRetVal);
        [PreserveSig] int SetGroupingParam(ref Guid Override, ref Guid EventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr NewNotifications);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr NewNotifications);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl2 : IAudioSessionControl
    {
        [PreserveSig] new int GetState(out int pRetVal);
        [PreserveSig] new int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] new int SetDisplayName(string Value, ref Guid EventContext);
        [PreserveSig] new int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] new int SetIconPath(string Value, ref Guid EventContext);
        [PreserveSig] new int GetGroupingParam(out Guid pRetVal);
        [PreserveSig] new int SetGroupingParam(ref Guid Override, ref Guid EventContext);
        [PreserveSig] new int RegisterAudioSessionNotification(IntPtr NewNotifications);
        [PreserveSig] new int UnregisterAudioSessionNotification(IntPtr NewNotifications);
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] int GetProcessId(out uint pRetVal);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference(bool optOut);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float fLevel, ref Guid EventContext);
        [PreserveSig] int GetMasterVolume(out float pfLevel);
        [PreserveSig] int SetMute(bool bMute, ref Guid EventContext);
        [PreserveSig] int GetMute(out bool pbMute);
    }
    #endregion

    /// <summary>
    /// Represents a consolidated audio application item in the Volume Mixer.
    /// Multi-process or multi-session applications (e.g. Discord, Chrome) are merged into one entry.
    /// </summary>
    public class AudioSessionGroup
    {
        public string AppName { get; set; } = "";
        public string ExecutablePath { get; set; } = "";
        public uint ProcessId { get; set; }
        public float Volume { get; set; } = 1.0f; // 0.0 to 1.0
        public bool IsMuted { get; set; }

        public List<ISimpleAudioVolume> VolumeControls { get; } = new();

        public void SetVolume(float newVolume)
        {
            Volume = Math.Clamp(newVolume, 0f, 1f);
            var guid = Guid.Empty;
            foreach (var ctrl in VolumeControls)
            {
                try
                {
                    ctrl.SetMasterVolume(Volume, ref guid);
                }
                catch { }
            }
        }

        public void SetMute(bool mute)
        {
            IsMuted = mute;
            var guid = Guid.Empty;
            foreach (var ctrl in VolumeControls)
            {
                try
                {
                    ctrl.SetMute(mute, ref guid);
                }
                catch { }
            }
        }
    }

    public static class NativeAudioSessionManager
    {
        private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
        private static readonly Guid IID_IAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint QueryDosDevice(string lpDeviceName, StringBuilder lpTargetPath, int ucchMax);

        private static string ResolveNtPath(string ntPath)
        {
            if (string.IsNullOrEmpty(ntPath)) return ntPath;
            if (!ntPath.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase)) return ntPath;

            try
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    try
                    {
                        string driveLetter = drive.Name.Substring(0, 2);
                        var sb = new StringBuilder(512);
                        if (QueryDosDevice(driveLetter, sb, sb.Capacity) > 0)
                        {
                            string devicePath = sb.ToString();
                            if (ntPath.StartsWith(devicePath, StringComparison.OrdinalIgnoreCase))
                            {
                                return driveLetter + ntPath.Substring(devicePath.Length);
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return ntPath;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, uint processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        private static string GetProcessPath(uint pid)
        {
            if (pid == 0) return "";
            IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProcess == IntPtr.Zero) return "";

            try
            {
                var sb = new StringBuilder(1024);
                uint size = (uint)sb.Capacity;
                if (QueryFullProcessImageName(hProcess, 0, sb, ref size))
                {
                    return sb.ToString();
                }
            }
            catch { }
            finally
            {
                CloseHandle(hProcess);
            }
            return "";
        }

        public static List<AudioSessionGroup> GetAudioSessions(string? targetDeviceId = null)
        {
            var result = new List<AudioSessionGroup>();
            try
            {
                Type? comType = Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator);
                if (comType == null) return result;

                var enumerator = (IMMDeviceEnumerator?)Activator.CreateInstance(comType);
                if (enumerator == null) return result;

                IMMDevice? device = null;
                if (!string.IsNullOrEmpty(targetDeviceId))
                {
                    int hr = enumerator.GetDevice(targetDeviceId, out device);
                    if (hr != 0 || device == null)
                    {
                        enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 0 /* eConsole */, out device);
                    }
                }
                else
                {
                    enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 0 /* eConsole */, out device);
                }

                if (device == null) return result;

                Guid iidManager = IID_IAudioSessionManager2;
                int actHr = device.Activate(ref iidManager, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out object mgrObj);
                if (actHr != 0 || mgrObj is not IAudioSessionManager2 mgr) return result;

                int enumHr = mgr.GetSessionEnumerator(out var sessionEnum);
                if (enumHr != 0 || sessionEnum == null) return result;

                sessionEnum.GetCount(out int count);
                var groups = new Dictionary<string, AudioSessionGroup>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        sessionEnum.GetSession(i, out var sessionCtrl);
                        if (sessionCtrl is not IAudioSessionControl2 c2) continue;

                        c2.GetState(out int state);
                        // State: 0 = Inactive, 1 = Active, 2 = Expired.
                        // Skip expired sessions only! Active and Inactive (e.g. paused games/pets) must be displayed.
                        if (state == 2) continue;

                        // IsSystemSoundsSession returns S_OK (0) for Windows System Sounds
                        if (c2.IsSystemSoundsSession() == 0) continue;

                        c2.GetProcessId(out uint pid);
                        c2.GetDisplayName(out string dispName);
                        c2.GetIconPath(out string iconPath);
                        c2.GetSessionIdentifier(out string sessId);

                        float vol = 1.0f;
                        bool muted = false;
                        if (sessionCtrl is ISimpleAudioVolume sav)
                        {
                            sav.GetMasterVolume(out vol);
                            sav.GetMute(out muted);
                        }

                        string appName = dispName;
                        string exePath = "";

                        // 1. Try extracting executable path from SessionIdentifier (often contains \Device\HarddiskVolume...\app.exe)
                        if (!string.IsNullOrEmpty(sessId) && sessId.Contains(@"\Device\"))
                        {
                            int start = sessId.IndexOf(@"\Device\");
                            int end = sessId.IndexOf('%', start);
                            if (end > start)
                            {
                                exePath = ResolveNtPath(sessId.Substring(start, end - start));
                            }
                            else
                            {
                                exePath = ResolveNtPath(sessId.Substring(start));
                            }
                        }

                        // 2. Try IconPath if it points to an .exe or icon resource (e.g. C:\path\app.exe,-101)
                        if (string.IsNullOrEmpty(exePath) && !string.IsNullOrEmpty(iconPath))
                        {
                            string rawIcon = iconPath.Trim('@', ' ');
                            int comma = rawIcon.IndexOf(',');
                            string candidate = comma > 0 ? rawIcon.Substring(0, comma) : rawIcon;
                            if (candidate.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase))
                            {
                                candidate = ResolveNtPath(candidate);
                            }
                            if (File.Exists(candidate))
                            {
                                exePath = candidate;
                            }
                        }

                        // 3. Fallback to Process ID via QueryFullProcessImageName or Process.GetProcessById
                        if (pid > 0)
                        {
                            if (string.IsNullOrEmpty(exePath))
                            {
                                exePath = GetProcessPath(pid);
                            }

                            try
                            {
                                using var p = Process.GetProcessById((int)pid);
                                if (string.IsNullOrEmpty(exePath))
                                {
                                    try { exePath = p.MainModule?.FileName ?? ""; } catch { }
                                }

                                if (string.IsNullOrWhiteSpace(appName))
                                {
                                    try
                                    {
                                        string desc = p.MainModule?.FileVersionInfo.FileDescription ?? "";
                                        appName = !string.IsNullOrWhiteSpace(desc) ? desc : p.ProcessName;
                                    }
                                    catch
                                    {
                                        appName = p.ProcessName;
                                    }
                                }
                            }
                            catch { }
                        }

                        if (string.IsNullOrWhiteSpace(appName) && !string.IsNullOrEmpty(exePath))
                        {
                            appName = Path.GetFileNameWithoutExtension(exePath);
                        }

                        if (string.IsNullOrWhiteSpace(appName))
                        {
                            appName = "Application";
                        }

                        // Grouping Key: Deduplicate multi-process applications (Discord, Chrome) by exe name or PID
                        string groupKey = !string.IsNullOrEmpty(exePath)
                            ? Path.GetFileName(exePath).ToLowerInvariant()
                            : (pid > 0 ? $"pid_{pid}" : appName.ToLowerInvariant());

                        if (!groups.TryGetValue(groupKey, out var grp))
                        {
                            grp = new AudioSessionGroup
                            {
                                AppName = appName,
                                ExecutablePath = exePath,
                                ProcessId = pid,
                                Volume = vol,
                                IsMuted = muted
                            };
                            groups[groupKey] = grp;
                        }
                        else
                        {
                            // If this subsequent session has volume or path info and existing didn't, augment it
                            if (string.IsNullOrEmpty(grp.ExecutablePath) && !string.IsNullOrEmpty(exePath))
                            {
                                grp.ExecutablePath = exePath;
                            }
                            if (grp.AppName.Equals("Application", StringComparison.OrdinalIgnoreCase) && !appName.Equals("Application", StringComparison.OrdinalIgnoreCase))
                            {
                                grp.AppName = appName;
                            }
                        }

                        if (sessionCtrl is ISimpleAudioVolume vCtrl)
                        {
                            grp.VolumeControls.Add(vCtrl);
                        }
                    }
                    catch (Exception ex)
                    {
                        App.Log($"[NativeAudioSessionManager] Session parsing error at index {i}: {ex.Message}");
                    }
                }

                result.AddRange(groups.Values);
                result.Sort((a, b) => string.Compare(a.AppName, b.AppName, StringComparison.CurrentCultureIgnoreCase));
            }
            catch (Exception ex)
            {
                App.Log($"[NativeAudioSessionManager] GetAudioSessions error: {ex.Message}");
            }

            return result;
        }
    }
}
