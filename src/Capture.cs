using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace SongSentry
{
    /// Captures what one OBS source carries: per-process loopback (Application Audio Capture, Windows 10 2004+),
    /// output-device loopback, or an input device. Keeps a rolling buffer of the last seconds as 8 kHz mono,
    /// which is all the recognisers need. Hand-written WASAPI interop (no NAudio), C# 5.
    public sealed class AudioCapture : IDisposable
    {
        public const int Rate = 8000;
        const int BufferSeconds = 20;
        const uint LOOPBACK = 0x00020000, EVENTCALLBACK = 0x00040000, AUTOCONVERTPCM = 0x80000000, SRC_DEFAULT_QUALITY = 0x08000000;
        static Guid IID_IAudioClient = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
        static Guid IID_IAudioCaptureClient = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

        IAudioClient client;
        IAudioCaptureClient capture;
        Thread thread;
        volatile bool running;
        readonly AutoResetEvent evt = new AutoResetEvent(false);
        int srcRate, channels, bits; bool isFloat;

        readonly short[] ring = new short[Rate * BufferSeconds];
        long written;                        // total samples written (ring position = written % length)
        DateTime lastWrite = DateTime.UtcNow;
        double phase, acc; int accN;         // box-filter decimation state
        readonly object gate = new object();

        public string Description { get; private set; }
        public int ProcessId { get; private set; }

        // ------------------------------------------------------------------ factories

        /// Loopback of one app, including its child processes (Spotify, browsers and games spawn several).
        public static AudioCapture ForProcess(int pid, string label)
        {
            IntPtr prm = Marshal.AllocHGlobal(12), pvp = IntPtr.Zero;
            try
            {
                Marshal.WriteInt32(prm, 0, 1); Marshal.WriteInt32(prm, 4, pid); Marshal.WriteInt32(prm, 8, 0);   // PROCESS_LOOPBACK, pid, INCLUDE_TREE
                var pv = new PropVariant { vt = 65 /*VT_BLOB*/, p1 = (IntPtr)12, p2 = prm };
                pvp = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(PropVariant)));
                Marshal.StructureToPtr(pv, pvp, false);
                var h = new ActivationHandler();
                IActivateAudioInterfaceAsyncOperation op;
                Check(ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref IID_IAudioClient, pvp, h, out op));
                if (!h.Done.WaitOne(5000)) throw new TimeoutException("process audio capture didn't start");
                Check(h.Hr);
                var c = new AudioCapture { client = (IAudioClient)h.Iface, Description = label + " (app)", ProcessId = pid };
                c.srcRate = 48000; c.channels = 2; c.bits = 16; c.isFloat = false;
                IntPtr fmt = Marshal.AllocHGlobal(18);
                try
                {
                    Marshal.WriteInt16(fmt, 0, 1); Marshal.WriteInt16(fmt, 2, 2); Marshal.WriteInt32(fmt, 4, 48000);
                    Marshal.WriteInt32(fmt, 8, 48000 * 4); Marshal.WriteInt16(fmt, 12, 4); Marshal.WriteInt16(fmt, 14, 16); Marshal.WriteInt16(fmt, 16, 0);
                    Check(c.client.Initialize(0, LOOPBACK | EVENTCALLBACK | AUTOCONVERTPCM | SRC_DEFAULT_QUALITY, 2000000, 0, fmt, IntPtr.Zero));
                }
                finally { Marshal.FreeHGlobal(fmt); }
                c.Finish();
                return c;
            }
            finally
            {
                Marshal.FreeHGlobal(prm);
                if (pvp != IntPtr.Zero) Marshal.FreeHGlobal(pvp);
            }
        }

        /// deviceId as OBS stores it ("default" = the default device). Output devices are captured by loopback.
        public static AudioCapture ForDevice(string deviceId, bool output)
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
            IMMDevice d;
            if (string.IsNullOrEmpty(deviceId) || deviceId == "default") Check(en.GetDefaultAudioEndpoint(output ? 0 : 1, 0, out d));
            else Check(en.GetDevice(deviceId, out d));
            object o; Check(d.Activate(ref IID_IAudioClient, 23, IntPtr.Zero, out o));
            var c = new AudioCapture { client = (IAudioClient)o, Description = (output ? "output " : "input ") + AudioDevices.Name(deviceId) };
            IntPtr fmt; Check(c.client.GetMixFormat(out fmt));
            try
            {
                ushort tag = (ushort)Marshal.ReadInt16(fmt, 0);
                c.channels = Marshal.ReadInt16(fmt, 2); c.srcRate = Marshal.ReadInt32(fmt, 4); c.bits = Marshal.ReadInt16(fmt, 14);
                if (tag == 0xFFFE) { var sub = new byte[16]; Marshal.Copy(fmt + 24, sub, 0, 16); c.isFloat = new Guid(sub) == new Guid("00000003-0000-0010-8000-00aa00389b71"); }
                else c.isFloat = tag == 3;
                Check(c.client.Initialize(0, (output ? LOOPBACK : 0) | EVENTCALLBACK, 2000000, 0, fmt, IntPtr.Zero));
            }
            finally { Marshal.FreeCoTaskMem(fmt); }
            c.Finish();
            return c;
        }

        void Finish()
        {
            Check(client.SetEventHandle(evt.SafeWaitHandle.DangerousGetHandle()));
            object o; Check(client.GetService(ref IID_IAudioCaptureClient, out o));
            capture = (IAudioCaptureClient)o;
        }

        public void Start()
        {
            running = true;
            Check(client.Start());
            thread = new Thread(Loop) { IsBackground = true, Name = "capture", Priority = ThreadPriority.AboveNormal };
            thread.Start();
        }

        public bool Alive { get { return running && thread != null && thread.IsAlive; } }

        void Loop()
        {
            try
            {
                while (running)
                {
                    evt.WaitOne(200);
                    uint next;
                    while (running && capture.GetNextPacketSize(out next) == 0 && next > 0)
                    {
                        IntPtr data; uint frames, flags; ulong pos, qpc;
                        if (capture.GetBuffer(out data, out frames, out flags, out pos, out qpc) != 0) break;
                        Consume(data, (int)frames, (flags & 2) != 0);
                        capture.ReleaseBuffer(frames);
                    }
                }
            }
            catch (Exception e) { Log.Write("capture " + Description + " stopped: " + e.Message); running = false; }
        }

        unsafe void Consume(IntPtr data, int frames, bool silent)
        {
            lock (gate)
            {
                PadSilence();
                for (int i = 0; i < frames; i++)
                {
                    double s = 0;
                    if (!silent)
                    {
                        if (isFloat && bits == 32) { float* p = (float*)data + i * channels; for (int c = 0; c < channels; c++) s += p[c]; }
                        else if (bits == 16) { short* p = (short*)data + i * channels; for (int c = 0; c < channels; c++) s += p[c] / 32768.0; }
                        else if (bits == 32) { int* p = (int*)data + i * channels; for (int c = 0; c < channels; c++) s += p[c] / 2147483648.0; }
                        s /= channels;
                    }
                    // box-filter decimation to 8 kHz (works for any source rate)
                    acc += s; accN++; phase += Rate;
                    if (phase >= srcRate)
                    {
                        phase -= srcRate;
                        double v = acc / accN * 32767;
                        ring[written % ring.Length] = (short)Math.Max(-32768, Math.Min(32767, v));
                        written++; acc = 0; accN = 0;
                    }
                }
                lastWrite = DateTime.UtcNow;
            }
        }

        /// Loopback delivers no packets while nothing plays: fill that gap with silence so the timeline stays true.
        void PadSilence()
        {
            double gap = (DateTime.UtcNow - lastWrite).TotalSeconds;
            if (gap < 0.25) return;
            long n = Math.Min(ring.Length, (long)(gap * Rate));
            for (long k = 0; k < n; k++) ring[(written + k) % ring.Length] = 0;
            written += n;
            lastWrite = DateTime.UtcNow;
        }

        /// The last `seconds` of audio (8 kHz mono). Shorter at the very start.
        public short[] Last(double seconds)
        {
            lock (gate)
            {
                PadSilence();
                long n = Math.Min(Math.Min(written, ring.Length), (long)(seconds * Rate));
                var o = new short[n];
                long start = written - n;
                for (long k = 0; k < n; k++) o[k] = ring[(start + k) % ring.Length];
                return o;
            }
        }

        public void Dispose()
        {
            running = false;
            if (thread != null) thread.Join(500);
            try { if (client != null) client.Stop(); } catch { }
            if (capture != null) Marshal.ReleaseComObject(capture);
            if (client != null) Marshal.ReleaseComObject(client);
            capture = null; client = null;
        }

        static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }

        // ------------------------------------------------------------------ process lookup (Toolhelp, no WMI)

        /// The root process of an app's process tree (e.g. the main Spotify.exe, not its helpers), or 0 if not running.
        public static int RootProcess(string appKey)
        {
            var byPid = new Dictionary<int, KeyValuePair<int, string>>();   // pid -> (parent, exe)
            IntPtr snap = CreateToolhelp32Snapshot(2 /*TH32CS_SNAPPROCESS*/, 0);
            if (snap == (IntPtr)(-1)) return 0;
            try
            {
                var pe = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32)) };
                for (bool ok = Process32FirstW(snap, ref pe); ok; ok = Process32NextW(snap, ref pe))
                    byPid[(int)pe.th32ProcessID] = new KeyValuePair<int, string>((int)pe.th32ParentProcessID, pe.szExeFile);
            }
            finally { CloseHandle(snap); }
            int fallback = 0;
            foreach (var kv in byPid)
            {
                if (AppKey.Of(kv.Value.Value) != appKey) continue;
                KeyValuePair<int, string> parent;
                bool parentSame = byPid.TryGetValue(kv.Value.Key, out parent) && AppKey.Of(parent.Value) == appKey;
                if (!parentSame) return kv.Key;
                fallback = kv.Key;
            }
            return fallback;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PROCESSENTRY32
        {
            public uint dwSize, cntUsage, th32ProcessID; public IntPtr th32DefaultHeapID; public uint th32ModuleID, cntThreads, th32ParentProcessID;
            public int pcPriClassBase; public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32 pe);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32 pe);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        // ------------------------------------------------------------------ COM interop

        [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
        static extern int ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid riid, IntPtr activationParams,
            IActivateAudioInterfaceCompletionHandler handler, out IActivateAudioInterfaceAsyncOperation op);

        [StructLayout(LayoutKind.Sequential)] struct PropVariant { public ushort vt; public ushort r1, r2, r3; public IntPtr p1; public IntPtr p2; }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        }
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCo { }
        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o);
        }
        [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioClient
        {
            [PreserveSig] int Initialize(int shareMode, uint flags, long buffer, long period, IntPtr format, IntPtr session);
            [PreserveSig] int GetBufferSize(out uint frames);
            [PreserveSig] int GetStreamLatency(out long latency);
            [PreserveSig] int GetCurrentPadding(out uint padding);
            [PreserveSig] int IsFormatSupported(int mode, IntPtr format, out IntPtr closest);
            [PreserveSig] int GetMixFormat(out IntPtr format);
            [PreserveSig] int GetDevicePeriod(out long def, out long min);
            [PreserveSig] int Start();
            [PreserveSig] int Stop();
            [PreserveSig] int Reset();
            [PreserveSig] int SetEventHandle(IntPtr handle);
            [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        }
        [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioCaptureClient
        {
            [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
            [PreserveSig] int ReleaseBuffer(uint frames);
            [PreserveSig] int GetNextPacketSize(out uint frames);
        }
        [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IActivateAudioInterfaceAsyncOperation { [PreserveSig] int GetActivateResult(out int hr, [MarshalAs(UnmanagedType.IUnknown)] out object iface); }
        [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IActivateAudioInterfaceCompletionHandler { [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation op); }
        [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAgileObject { }

        sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
        {
            public readonly ManualResetEvent Done = new ManualResetEvent(false);
            public int Hr; public object Iface;
            public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation op)
            {
                int hr; object o;
                int r = op.GetActivateResult(out hr, out o);
                Hr = r != 0 ? r : hr; Iface = o;
                Done.Set();
                return 0;
            }
        }
    }
}
