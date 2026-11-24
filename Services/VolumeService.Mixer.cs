using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace DesktopBar.Services
{
    public sealed partial class VolumeService
    {
        internal sealed class ApplicationVolume : IDisposable
        {
            private readonly List<object> _sessions = new();
            internal string Name { get; }
            internal float Volume { get; private set; }
            internal bool Muted { get; private set; } = true;
            internal bool Active { get; private set; }

            internal ApplicationVolume(string name) => Name = name;

            internal void Add(object session, float volume, bool muted, bool active)
            {
                if (_sessions.Count == 0 || active && !Active) Volume = volume;
                Muted &= muted;
                Active |= active;
                _sessions.Add(session);
            }

            internal void Write(float? level, bool? muted)
            {
                int failure = 0;
                foreach (var item in _sessions)
                {
                    if (item is not ISimpleAudioVolume volume) continue;
                    if (level.HasValue) failure = Math.Min(failure, volume.SetMasterVolume(Math.Clamp(level.Value, 0, 1), IntPtr.Zero));
                    if (muted.HasValue) failure = Math.Min(failure, volume.SetMute(muted.Value, IntPtr.Zero));
                }
                Check(failure, "设置应用音量或静音");
            }

            public void Dispose()
            {
                foreach (var item in _sessions) Release(item);
                _sessions.Clear();
            }
        }

        internal ApplicationVolume[] ReadApplicationVolumes()
        {
            object manager = null;
            IAudioSessionEnumerator sessions = null;
            var groups = new Dictionary<uint, ApplicationVolume>();
            try
            {
                Ensure();
                var iid = typeof(IAudioSessionManager2).GUID;
                Check(_device.Activate(ref iid, CLSCTX.ALL, IntPtr.Zero, out manager), "读取应用音量");
                Check(((IAudioSessionManager2)manager).GetSessionEnumerator(out sessions), "读取音频会话");
                Check(sessions.GetCount(out int count), "读取会话数量");
                for (int i = 0; i < count; i++)
                {
                    if (sessions.GetSession(i, out var item) < 0) continue;
                    bool retained = false;
                    try
                    {
                        if (item is not IAudioSessionControl2 control || control.GetState(out int state) < 0 || state is < 0 or > 1
                            || control.GetProcessId(out uint pid) < 0 || item is not ISimpleAudioVolume volume
                            || volume.GetMasterVolume(out float level) < 0 || volume.GetMute(out bool muted) < 0) continue;
                        if (!groups.TryGetValue(pid, out var group))
                            groups[pid] = group = new ApplicationVolume(SessionName(pid));
                        group.Add(item, level, muted, state == 1);
                        retained = true;
                    }
                    finally { if (!retained) Release(item); }
                }
                return groups.Values.OrderByDescending(group => group.Active)
                    .ThenBy(group => group.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            }
            catch (Exception ex)
            {
                foreach (var group in groups.Values) group.Dispose();
                App.Log("读取应用音量失败: " + ex.Message);
                return null;
            }
            finally { Release(sessions); Release(manager); }
        }

        private static string SessionName(uint pid)
        {
            if (pid == 0) return "系统声音";
            try
            {
                using var process = Process.GetProcessById((int)pid);
                return process.ProcessName;
            }
            catch (ArgumentException) { return "应用 " + pid; }
        }

        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionManager2
        {
            [PreserveSig] int GetAudioSessionControl(IntPtr session, uint flags, out IntPtr control);
            [PreserveSig] int GetSimpleAudioVolume(IntPtr session, uint flags, out IntPtr volume);
            [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
        }

        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionEnumerator
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object control);
        }

        [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl2
        {
            [PreserveSig] int GetState(out int state);
            [PreserveSig] int GetDisplayName(out IntPtr name);
            [PreserveSig] int SetDisplayName(IntPtr name, IntPtr context);
            [PreserveSig] int GetIconPath(out IntPtr path);
            [PreserveSig] int SetIconPath(IntPtr path, IntPtr context);
            [PreserveSig] int GetGroupingParam(out Guid group);
            [PreserveSig] int SetGroupingParam(IntPtr group, IntPtr context);
            [PreserveSig] int RegisterAudioSessionNotification(IntPtr events);
            [PreserveSig] int UnregisterAudioSessionNotification(IntPtr events);
            [PreserveSig] int GetSessionIdentifier(out IntPtr id);
            [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
            [PreserveSig] int GetProcessId(out uint processId);
        }

        [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ISimpleAudioVolume
        {
            [PreserveSig] int SetMasterVolume(float value, IntPtr context);
            [PreserveSig] int GetMasterVolume(out float value);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, IntPtr context);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
        }
    }
}
