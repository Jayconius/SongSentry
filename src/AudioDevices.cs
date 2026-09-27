using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SongSentry
{
    /// Friendly names for Windows audio endpoint IDs (the device_id OBS stores), via the MMDevice API.
    static class AudioDevices
    {
        [StructLayout(LayoutKind.Sequential)]
        struct PropKey { public Guid fmtid; public int pid; }

        [StructLayout(LayoutKind.Sequential)]
        struct PropVariant { public ushort vt; public ushort r1, r2, r3; public IntPtr p1; public IntPtr p2; }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int flow, uint stateMask, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        }

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCo { }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int Item(uint index, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o);
            [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore props);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out uint state);
        }

        [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint i, out PropKey key);
            [PreserveSig] int GetValue(ref PropKey key, out PropVariant value);
        }

        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionManager2
        {
            [PreserveSig] int GetAudioSessionControl(IntPtr guid, uint flags, out IntPtr ctl);
            [PreserveSig] int GetSimpleAudioVolume(IntPtr guid, uint flags, out IntPtr vol);
            [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator e);
        }

        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionEnumerator
        {
            [PreserveSig] int GetCount(out int n);
            [PreserveSig] int GetSession(int i, [MarshalAs(UnmanagedType.IUnknown)] out object session);
        }

        [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionControl2
        {
            [PreserveSig] int GetState(out int state);
            void _GetDisplayName(); void _SetDisplayName(); void _GetIconPath(); void _SetIconPath();
            void _GetGroupingParam(); void _SetGroupingParam(); void _Register(); void _Unregister();
            void _GetSessionIdentifier(); void _GetSessionInstanceIdentifier();
            [PreserveSig] int GetProcessId(out uint pid);
            [PreserveSig] int IsSystemSoundsSession();
        }

        static readonly Dictionary<string, KeyValuePair<DateTime, List<string>>> sessionCache = new Dictionary<string, KeyValuePair<DateTime, List<string>>>();
        static readonly HashSet<string> ignoreApps = new HashSet<string> { "", "obs64", "obs32", "obs", "songsentry", "audiodg", "system", "idle" };

        /// Apps (AppKeys) that Windows plays through the output device behind an OBS device source, active ones first.
        /// Output capture = that render device ("default" = the default one). Input capture = the render device with the same
        /// name (M-Game / GoXLR style loopback channels) or its Output/Input twin (VB-Cable, VoiceMeeter).
        public static List<string> AppsBehind(string deviceId, bool sourceIsOutput)
        {
            string key = (sourceIsOutput ? "out|" : "in|") + (deviceId ?? "default");
            lock (sessionCache)
            {
                KeyValuePair<DateTime, List<string>> hit;
                if (sessionCache.TryGetValue(key, out hit) && (DateTime.UtcNow - hit.Key).TotalSeconds < 5) return hit.Value;
                var apps = new List<string>();
                try
                {
                    string render = RenderEndpointFor(deviceId, sourceIsOutput);
                    if (render != null) apps = SessionApps(render);
                }
                catch (Exception e) { Log.Write("audio sessions: " + e.Message); }
                sessionCache[key] = new KeyValuePair<DateTime, List<string>>(DateTime.UtcNow, apps);
                return apps;
            }
        }

        static string RenderEndpointFor(string deviceId, bool sourceIsOutput)
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
            IMMDevice d;
            if (string.IsNullOrEmpty(deviceId) || deviceId == "default")
            {
                if (!sourceIsOutput) return null;   // default microphone: nothing is "played" into it
                if (en.GetDefaultAudioEndpoint(0 /*eRender*/, 0 /*eConsole*/, out d) != 0) return null;
                string id; d.GetId(out id); return id;
            }
            if (sourceIsOutput) return deviceId;
            string name = Name(deviceId);
            string twin = name.Replace("Output", "Input");
            IMMDeviceCollection col;
            if (en.EnumAudioEndpoints(0 /*eRender*/, 1 /*ACTIVE*/, out col) != 0) return null;
            uint n; col.GetCount(out n);
            string sameName = null, twinName = null;
            for (uint i = 0; i < n; i++)
            {
                IMMDevice r; col.Item(i, out r);
                string rid; r.GetId(out rid);
                string rn = Name(rid);
                if (rn == name) sameName = rid;
                else if (rn == twin) twinName = rid;
            }
            return sameName ?? twinName;
        }

        static List<string> SessionApps(string renderId)
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
            IMMDevice d;
            if (en.GetDevice(renderId, out d) != 0) return new List<string>();
            var iid = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
            object o;
            if (d.Activate(ref iid, 23, IntPtr.Zero, out o) != 0) return new List<string>();
            var mgr = (IAudioSessionManager2)o;
            IAudioSessionEnumerator e;
            if (mgr.GetSessionEnumerator(out e) != 0) return new List<string>();
            int count; e.GetCount(out count);
            var active = new List<string>(); var inactive = new List<string>();
            for (int i = 0; i < count; i++)
            {
                object s; if (e.GetSession(i, out s) != 0) continue;
                var c2 = s as IAudioSessionControl2;
                if (c2 == null || c2.IsSystemSoundsSession() == 0) continue;
                int state; uint pid;
                c2.GetState(out state); c2.GetProcessId(out pid);
                if (state == 2 || pid == 0) continue;   // expired
                string app;
                try { app = AppKey.Of(System.Diagnostics.Process.GetProcessById((int)pid).ProcessName); } catch { continue; }
                if (ignoreApps.Contains(app)) continue;
                var list = state == 1 ? active : inactive;
                if (!active.Contains(app) && !inactive.Contains(app)) list.Add(app);
            }
            active.AddRange(inactive);
            return active;
        }

        static readonly Dictionary<string, string> cache = new Dictionary<string, string>();

        public static string Name(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId) || deviceId == "default") return "Default device";
            lock (cache)
            {
                string n;
                if (cache.TryGetValue(deviceId, out n)) return n;
                n = Lookup(deviceId) ?? "Unknown device (unplugged?)";
                cache[deviceId] = n;
                return n;
            }
        }

        static string Lookup(string id)
        {
            try
            {
                var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
                IMMDevice d;
                if (en.GetDevice(id, out d) != 0 || d == null) return null;
                IPropertyStore ps;
                if (d.OpenPropertyStore(0, out ps) != 0) return null;
                var key = new PropKey { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };   // PKEY_Device_FriendlyName
                PropVariant v;
                if (ps.GetValue(ref key, out v) == 0 && v.vt == 31) return Marshal.PtrToStringUni(v.p1);
            }
            catch { }
            return null;
        }
    }
}
