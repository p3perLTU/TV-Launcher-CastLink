// CastLink PC sender ("CastLink"): sends this PC's screen and sound to the TV receiver
// app ("CastLink") with the same UDP protocol as the phone
// (see ../common/src/lt/tvcast/common/Proto.java). Unit tests: ../../tests/cs.
//
//   screen  DXGI Desktop Duplication -> CPU copy (+ mouse pointer) -> NV12
//   video   Media Foundation H.264 encoder MFT (hardware: NVIDIA/AMD/Intel, else software)
//   sound   WASAPI loopback -> 16-bit PCM, 5 ms packets
//
// Built with the C# 5 compiler that ships with Windows (.NET Framework 4.x), so everything
// native is called through raw COM vtables: slot numbers are the method's position in the
// interface, IUnknown's three methods included.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

// native DLLs only from System32: a planted dxgi.dll / mfplat.dll / wlanapi.dll next to the exe
// (e.g. in Downloads) would otherwise be loaded first
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace TVCast
{
    static class Proto
    {
        public const int PORT = 47800;
        public const byte CONFIG = 1, VIDEO = 2, AUDIO = 3, KEYREQ = 10, BYE = 11, PING = 12, PONG = 13, INPUT = 14, WHO = 15, NAME = 16, INFO = 17;
        /// <summary>a PC receiver's answer to WHO (Receiver.cs): like NAME, so the window lists PCs apart</summary>
        public const byte PCNAME = 18, PHONENAME = 19;
        public const int CONFIG_CONTROL = 1, TOKEN_BYTES = 8, CONFIG_LONG = 14 + 1 + TOKEN_BYTES;
        public const int FLAG_KEY = 1;
        public const int VIDEO_HEADER = 1 + 1 + 4 + 2 + 2 + 8;
        public const int MAX_PAYLOAD = 1400;

        /// <summary>VIDEO packets for a frame of len bytes, -1 if it does not fit the count field</summary>
        public static int VideoChunks(int len)
        {
            int count = (len + MAX_PAYLOAD - 1) / MAX_PAYLOAD;
            return count > 0xFFFF ? -1 : count;
        }

        /// <summary>the VIDEO header in pkt[0..VIDEO_HEADER), same bytes as Proto.putVideoHeader in Java</summary>
        public static void PutVideoHeader(byte[] pkt, bool key, int frame, int chunk, int count, long pts)
        {
            pkt[0] = VIDEO;
            pkt[1] = (byte)(key ? FLAG_KEY : 0);
            PutInt(pkt, 2, frame);
            pkt[6] = (byte)(chunk >> 8); pkt[7] = (byte)chunk;
            pkt[8] = (byte)(count >> 8); pkt[9] = (byte)count;
            PutLong(pkt, 10, pts);
        }

        public static void PutInt(byte[] b, int o, int v)
        {
            b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
        }

        public static int GetInt(byte[] b, int o)
        {
            return (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];
        }

        public static void PutLong(byte[] b, int o, long v)
        {
            PutInt(b, o, (int)(v >> 32));
            PutInt(b, o + 4, (int)v);
        }

        public static long GetLong(byte[] b, int o)
        {
            long v = 0;
            for (int i = 0; i < 8; i++) v = (v << 8) | b[o + i];
            return v;
        }

        /// <summary>private IPv4 ranges and link-local (same rule as Proto.isLanV4 in Java)</summary>
        public static bool IsLanV4(byte[] a)
        {
            if (a == null || a.Length != 4) return false;
            return a[0] == 10 || (a[0] == 172 && a[1] >= 16 && a[1] <= 31) || (a[0] == 192 && a[1] == 168)
                || (a[0] == 169 && a[1] == 254);
        }
    }

    /// <summary>QueryPerformanceCounter based. PONG answers in this clock and video pts use it,
    /// so the TV can show the real capture-to-screen delay.</summary>
    static class Clock
    {
        static readonly double NsPerTick = 1e9 / Stopwatch.Frequency;
        public static long Nanos() { return (long)(Stopwatch.GetTimestamp() * NsPerTick); }
        public static long Micros() { return Nanos() / 1000; }
        public static long QpcToMicros(long qpc) { return (long)(qpc * NsPerTick / 1000); }
        public static long Ms() { return Nanos() / 1000000; }
    }

    // ---- COM by vtable ------------------------------------------------------------------------

    static class Com
    {
        public static T F<T>(IntPtr obj, int slot) where T : class
        {
            IntPtr vt = Marshal.ReadIntPtr(obj);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vt, slot * IntPtr.Size), typeof(T));
        }

        public static void Release(ref IntPtr p)
        {
            if (p != IntPtr.Zero) { Marshal.Release(p); p = IntPtr.Zero; }
        }

        public static IntPtr QI(IntPtr p, Guid iid)
        {
            IntPtr r;
            return Marshal.QueryInterface(p, ref iid, out r) < 0 ? IntPtr.Zero : r;
        }

        public static void Check(int hr, string what)
        {
            if (hr < 0) throw new COMException(string.Format("{0}: 0x{1:X8}", what, hr), hr);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_IdxOut(IntPtr self, uint i, out IntPtr o);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_Buf(IntPtr self, IntPtr buf);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_Out(IntPtr self, out IntPtr o);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_Self(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_Dup(IntPtr self, IntPtr device, out IntPtr dup);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_Dup1(IntPtr self, IntPtr device, uint flags, uint count, int[] formats, out IntPtr dup);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_Acquire(IntPtr self, uint timeout, IntPtr info, out IntPtr res);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_CreateTex(IntPtr self, IntPtr desc, IntPtr init, out IntPtr tex);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void D_Copy(IntPtr self, IntPtr dst, IntPtr src);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_Map(IntPtr self, IntPtr res, uint sub, int type, uint flags, IntPtr mapped);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void D_Unmap(IntPtr self, IntPtr res, uint sub);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_SetGuid(IntPtr self, ref Guid key, ref Guid val);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_SetU32(IntPtr self, ref Guid key, uint v);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_SetU64(IntPtr self, ref Guid key, ulong v);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_GetU32(IntPtr self, ref Guid key, out uint v);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_GetAlloc(IntPtr self, ref Guid key, out IntPtr p, out uint len);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_Activate(IntPtr self, ref Guid iid, out IntPtr o);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_SetType(IntPtr self, uint stream, IntPtr type, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_GetType(IntPtr self, uint stream, out IntPtr type);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_AvailType(IntPtr self, uint stream, uint idx, out IntPtr type);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_StreamInfo(IntPtr self, uint stream, IntPtr info);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_Msg(IntPtr self, int msg, IntPtr param);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_PIn(IntPtr self, uint stream, IntPtr sample, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_POut(IntPtr self, uint flags, uint count, IntPtr buffers, out uint status);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_GetEvent(IntPtr self, uint flags, out IntPtr ev);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_GetEvType(IntPtr self, out uint t);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_SetI64(IntPtr self, long v);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_GetI64(IntPtr self, out long v);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_Lock(IntPtr self, out IntPtr p, out uint max, out uint cur);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_SetLen(IntPtr self, uint len);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_CodecSet(IntPtr self, ref Guid api, IntPtr variant);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_DefEndpoint(IntPtr self, int flow, int role, out IntPtr dev);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_DevActivate(IntPtr self, ref Guid iid, uint ctx, IntPtr p, out IntPtr o);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_AcInit(IntPtr self, int share, uint flags, long buf, long period, IntPtr fmt, IntPtr session);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_GetService(IntPtr self, ref Guid iid, out IntPtr o);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_CapGet(IntPtr self, out IntPtr data, out uint frames, out uint flags, out ulong pos, out ulong qpc);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_CapRel(IntPtr self, uint frames);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_CapNext(IntPtr self, out uint n);

    static class Native
    {
        [DllImport("dxgi.dll")] public static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);
        [DllImport("d3d11.dll")]
        public static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
            IntPtr featureLevels, uint numLevels, uint sdk, out IntPtr device, out int level, out IntPtr context);
        [DllImport("mfplat.dll")] public static extern int MFStartup(uint version, uint flags);
        [DllImport("mfplat.dll")] public static extern int MFCreateMediaType(out IntPtr type);
        [DllImport("mfplat.dll")] public static extern int MFCreateSample(out IntPtr sample);
        [DllImport("mfplat.dll")] public static extern int MFCreateMemoryBuffer(uint len, out IntPtr buf);
        [DllImport("mfplat.dll")]
        public static extern int MFTEnumEx(Guid category, uint flags, IntPtr input, IntPtr output, out IntPtr activates, out uint count);
        [DllImport("ole32.dll")] public static extern int CoInitializeEx(IntPtr reserved, uint model);
        [DllImport("ole32.dll")] public static extern void CoTaskMemFree(IntPtr p);
        [DllImport("ole32.dll")]
        public static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint ctx, ref Guid iid, out IntPtr o);
        [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
        [DllImport("wlanapi.dll")] public static extern int WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr handle);
        [DllImport("wlanapi.dll")] public static extern int WlanCloseHandle(IntPtr handle, IntPtr reserved);
        [DllImport("wlanapi.dll")] public static extern int WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
        [DllImport("wlanapi.dll")] public static extern void WlanFreeMemory(IntPtr p);
        [DllImport("wlanapi.dll")]
        public static extern int WlanSetInterface(IntPtr handle, ref Guid iface, int opcode, uint size, IntPtr data, IntPtr reserved);
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool GetCursorInfo(ref CURSORINFO ci);
        [DllImport("user32.dll")] public static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);
        [DllImport("user32.dll")]
        public static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int w, int h, uint step, IntPtr brush, uint flags);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool GdiFlush();
        [DllImport("gdi32.dll")] public static extern int GetObject(IntPtr obj, int size, out BITMAP bmp);
        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public POINT pt; }
    [StructLayout(LayoutKind.Sequential)] struct ICONINFO { public int fIcon, xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }
    [StructLayout(LayoutKind.Sequential)] struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public short bmPlanes, bmBitsPixel; public IntPtr bmBits; }
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    // ---- screen ------------------------------------------------------------------------------

    /// <summary>one monitor of this PC as DXGI sees it (also a virtual one from a virtual display
    /// driver or an HDMI dummy plug)</summary>
    sealed class ScreenInfo
    {
        public string Device;   // the name Windows keeps for this monitor, e.g. DISPLAY2
        public string Monitor;  // e.g. "Virtual Display Driver", from EnumDisplayDevices
        public string MonitorId = ""; // the monitor's model, e.g. MONITOR\ABC1234 (Capture.HardwareId)
        /// <summary>what is saved as the choice: Windows renames DISPLAYn when a (virtual) monitor
        /// is added again or its driver restarts, the model stays</summary>
        public string Key { get { return Device + "|" + MonitorId; } }
        public int Left, Top, Width, Height;
        public bool Primary;
        public int Number;      // 1, 2, ... in the order DXGI lists them

        public override string ToString()
        {
            return Number + " · " + Width + "×" + Height + (Primary ? " · " + Texts.ScreenMain : "")
                + (Monitor.Length > 0 ? " · " + Monitor : "");
        }
    }

    /// <summary>Desktop Duplication of the primary monitor, copied to a BGRA array.</summary>
    sealed class WrongGpuException : Exception
    {
        /// <summary>the screen to capture is driven by the NVIDIA card (e.g. a monitor on the HDMI
        /// port of a hybrid laptop), not by the integrated one</summary>
        public readonly bool NvidiaDrivesScreen;
        public WrongGpuException(bool nvidiaDrivesScreen) : base(Texts.WrongGpu) { NvidiaDrivesScreen = nvidiaDrivesScreen; }
    }

    sealed class Capture : IDisposable
    {
        static int AdapterCount(IntPtr factory)
        {
            int n = 0;
            for (uint a = 0; ; a++)
            {
                IntPtr adapter;
                if (Com.F<D_IdxOut>(factory, 12)(factory, a, out adapter) < 0) return n; // EnumAdapters1
                Com.Release(ref adapter);
                n++;
            }
        }

        static Guid IID_Factory1 = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        static Guid IID_Output1 = new Guid("00cddea8-939b-4b83-a340-a685226666cc");
        static Guid IID_Output5 = new Guid("80a07424-ab52-42eb-833c-0c42fd282d98");
        static Guid IID_Texture2D = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
        const int WAIT_TIMEOUT = unchecked((int)0x887A0027);
        public const int ACCESS_LOST = unchecked((int)0x887A0026);

        /// <summary>the monitor to capture (ScreenInfo.Device); empty or gone: the primary one</summary>
        public static string Screen = "";

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool EnumDisplayDevices(string device, uint index, ref DISPLAY_DEVICE dd, uint flags);

        /// <summary>the name of the monitor on an output ("Generic PnP Monitor", "Virtual Display ...")</summary>
        static string MonitorName(string device)
        {
            DISPLAY_DEVICE dd = new DISPLAY_DEVICE();
            dd.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
            try { return EnumDisplayDevices(device, 0, ref dd, 0) && dd.DeviceString != null ? dd.DeviceString.Trim() : ""; }
            catch (Exception) { return ""; }
        }

        /// <summary>
        /// The screen is driven by the NVIDIA card, by Windows' list of display adapters. DXGI is
        /// not reliable for this on a hybrid laptop: in an app that Windows runs on the integrated
        /// card it lists the NVIDIA card's screen under the integrated one.
        /// </summary>
        static bool NvidiaDrives(string device)
        {
            DISPLAY_DEVICE dd = new DISPLAY_DEVICE();
            dd.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
            try
            {
                for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++)
                    if (string.Equals(dd.DeviceName, device, StringComparison.OrdinalIgnoreCase)) return IsNvidiaDeviceId(dd.DeviceID);
            }
            catch (Exception) { }
            return false;
        }

        /// <summary>"PCI\VEN_10DE&amp;DEV_28A0&amp;..." is an NVIDIA card (unit-tested)</summary>
        internal static bool IsNvidiaDeviceId(string id)
        {
            return !string.IsNullOrEmpty(id) && id.IndexOf("VEN_10DE", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>the model of the monitor on an output, "" if unknown</summary>
        static string MonitorId(string device)
        {
            DISPLAY_DEVICE dd = new DISPLAY_DEVICE();
            dd.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
            try { return EnumDisplayDevices(device, 0, ref dd, 0) ? HardwareId(dd.DeviceID) : ""; }
            catch (Exception) { return ""; }
        }

        /// <summary>"MONITOR\ABC1234\{class}\0003" -> "MONITOR\ABC1234": the instance number at
        /// the end changes with the port or a driver restart</summary>
        internal static string HardwareId(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId)) return "";
            string[] parts = deviceId.Split('\\');
            return parts.Length >= 2 ? parts[0] + "\\" + parts[1] : "";
        }

        /// <summary>every monitor that is part of the desktop, on every graphics card</summary>
        public static List<ScreenInfo> ListScreens()
        {
            List<ScreenInfo> all = new List<ScreenInfo>();
            IntPtr factory;
            if (Native.CreateDXGIFactory1(ref IID_Factory1, out factory) < 0) return all;
            IntPtr desc = Marshal.AllocHGlobal(512);
            try
            {
                for (uint a = 0; ; a++)
                {
                    IntPtr adapter;
                    if (Com.F<D_IdxOut>(factory, 12)(factory, a, out adapter) < 0) break; // EnumAdapters1
                    for (uint o = 0; ; o++)
                    {
                        IntPtr output;
                        if (Com.F<D_IdxOut>(adapter, 7)(adapter, o, out output) < 0) break; // EnumOutputs
                        Com.F<D_Buf>(output, 7)(output, desc); // DXGI_OUTPUT_DESC
                        Com.Release(ref output);
                        ScreenInfo s = ReadOutputDesc(desc);
                        if (s == null || all.Exists(x => x.Device == s.Device)) continue;
                        s.Number = all.Count + 1;
                        s.Monitor = MonitorName(s.Device);
                        s.MonitorId = MonitorId(s.Device);
                        all.Add(s);
                    }
                    Com.Release(ref adapter);
                }
            }
            finally { Marshal.FreeHGlobal(desc); Com.Release(ref factory); }
            return all;
        }

        /// <summary>DXGI_OUTPUT_DESC: DeviceName WCHAR[32], DesktopCoordinates RECT at 64,
        /// AttachedToDesktop at 80; null for an output that is not part of the desktop</summary>
        static ScreenInfo ReadOutputDesc(IntPtr desc)
        {
            if (Marshal.ReadInt32(desc, 80) == 0) return null;
            int l = Marshal.ReadInt32(desc, 64), t = Marshal.ReadInt32(desc, 68), r = Marshal.ReadInt32(desc, 72), b = Marshal.ReadInt32(desc, 76);
            return new ScreenInfo { Device = Marshal.PtrToStringUni(desc), Left = l, Top = t, Width = r - l, Height = b - t, Primary = l == 0 && t == 0 };
        }

        /// <summary>which of the listed screens to capture: the one named, else the primary one,
        /// else the first (a pure choice, unit-tested)</summary>
        /// A screen is "device|model" (ScreenInfo.Key; a bare device name in old settings): the
        /// same device and model first, else the same model under a new device name.
        internal static int PickScreen(IList<string> devices, IList<bool> primary, string wanted)
        {
            if (!string.IsNullOrEmpty(wanted))
            {
                int bar = wanted.IndexOf('|');
                string model = bar >= 0 ? wanted.Substring(bar + 1) : "";
                if (model.Length == 0)
                {
                    string dev = bar >= 0 ? wanted.Substring(0, bar) : wanted;
                    for (int i = 0; i < devices.Count; i++) if (devices[i].Split('|')[0] == dev) return i;
                }
                else
                {
                    for (int i = 0; i < devices.Count; i++) if (devices[i] == wanted) return i;
                    for (int i = 0; i < devices.Count; i++) if (devices[i].EndsWith("|" + model)) return i;
                }
            }
            for (int i = 0; i < devices.Count; i++) if (primary[i]) return i;
            return devices.Count > 0 ? 0 : -1;
        }

        IntPtr device, ctx, dup, staging;
        readonly IntPtr frameInfo = Marshal.AllocHGlobal(48);
        readonly IntPtr mapped = Marshal.AllocHGlobal(16);
        public int W, H, OriginX, OriginY;
        public string ScreenDevice = "";
        public byte[] Bgra;
        public long LastPresentMicros;
        public string AdapterName = "";

        public Capture()
        {
            IntPtr factory;
            Com.Check(Native.CreateDXGIFactory1(ref IID_Factory1, out factory), "CreateDXGIFactory1");
            IntPtr pickAdapter = IntPtr.Zero, pickOutput = IntPtr.Zero;
            IntPtr desc = Marshal.AllocHGlobal(512);
            try
            {
                // every desktop output on every adapter, then the one chosen in the window (Screen),
                // else the primary one (also when the chosen monitor was unplugged since)
                List<IntPtr> outs = new List<IntPtr>(), adapters = new List<IntPtr>();
                List<ScreenInfo> infos = new List<ScreenInfo>();
                for (uint a = 0; ; a++)
                {
                    IntPtr adapter;
                    if (Com.F<D_IdxOut>(factory, 12)(factory, a, out adapter) < 0) break; // EnumAdapters1
                    for (uint o = 0; ; o++)
                    {
                        IntPtr output;
                        if (Com.F<D_IdxOut>(adapter, 7)(adapter, o, out output) < 0) break; // EnumOutputs
                        Com.F<D_Buf>(output, 7)(output, desc); // DXGI_OUTPUT_DESC
                        ScreenInfo s = ReadOutputDesc(desc);
                        if (s == null) { Com.Release(ref output); continue; }
                        Marshal.AddRef(adapter);
                        outs.Add(output); adapters.Add(adapter); infos.Add(s);
                    }
                    Com.Release(ref adapter);
                }
                int pick = PickScreen(infos.ConvertAll(x => x.Device + "|" + MonitorId(x.Device)), infos.ConvertAll(x => x.Primary), Screen);
                for (int i = 0; i < outs.Count; i++)
                {
                    IntPtr o = outs[i], ad = adapters[i];
                    if (i == pick) { pickOutput = o; pickAdapter = ad; OriginX = infos[i].Left; OriginY = infos[i].Top; ScreenDevice = infos[i].Device; }
                    else { Com.Release(ref o); Com.Release(ref ad); }
                }
                if (pickOutput == IntPtr.Zero) throw new Exception(Texts.NoScreen);
                Com.F<D_Buf>(pickAdapter, 8)(pickAdapter, desc); // DXGI_ADAPTER_DESC, Description first
                AdapterName = Marshal.PtrToStringUni(desc);
                bool nvidiaScreen = NvidiaDrives(ScreenDevice) || (uint)Marshal.ReadInt32(desc, 256) == 0x10DE; // DXGI_ADAPTER_DESC.VendorId as a fallback

                int level;
                // driver type UNKNOWN because the adapter is given; BGRA support
                Com.Check(Native.D3D11CreateDevice(pickAdapter, 0, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7,
                    out device, out level, out ctx), "D3D11CreateDevice");
                // The process is per-monitor DPI aware (so the pointer lands right on a scaled
                // screen), and for such processes IDXGIOutput1::DuplicateOutput fails with
                // DXGI_ERROR_UNSUPPORTED on a scaled (e.g. 125 %) screen: IDXGIOutput5::
                // DuplicateOutput1 (Windows 10 1703+) is the one meant for them; the old call stays
                // as the fallback.
                int hr = -1;
                IntPtr out5 = Com.QI(pickOutput, IID_Output5);
                if (out5 != IntPtr.Zero)
                {
                    hr = Com.F<D_Dup1>(out5, 26)(out5, device, 0, 1, new[] { 87 /*B8G8R8A8_UNORM*/ }, out dup); // DuplicateOutput1
                    Com.Release(ref out5);
                }
                if (hr < 0)
                {
                    IntPtr out1 = Com.QI(pickOutput, IID_Output1);
                    if (out1 == IntPtr.Zero) throw new Exception(Texts.NoOutput1);
                    hr = Com.F<D_Dup>(out1, 22)(out1, device, out dup); // DuplicateOutput
                    Com.Release(ref out1);
                }
                // DXGI_ERROR_UNSUPPORTED on a laptop with two GPUs: Windows runs this process on
                // the GPU that does not drive the screen (NVIDIA set to "High performance" while
                // the integrated GPU drives the panel); duplication cannot cross adapters
                if (hr == unchecked((int)0x887A0004) && AdapterCount(factory) > 1) throw new WrongGpuException(nvidiaScreen);
                Com.Check(hr, "DuplicateOutput");

                Com.F<D_Buf>(dup, 7)(dup, desc); // DXGI_OUTDUPL_DESC
                W = Marshal.ReadInt32(desc, 0);
                H = Marshal.ReadInt32(desc, 4);
                int format = Marshal.ReadInt32(desc, 16);
                if (format != 87) throw new Exception(string.Format(Texts.ScreenFormat, format));

                Marshal.Copy(new int[] { W, H, 1, 1, 87, 1, 0, 3 /*STAGING*/, 0, 0x20000 /*CPU READ*/, 0 }, 0, desc, 11);
                Com.Check(Com.F<D_CreateTex>(device, 5)(device, desc, IntPtr.Zero, out staging), "CreateTexture2D");
                Bgra = new byte[W * H * 4];
            }
            finally
            {
                Marshal.FreeHGlobal(desc);
                Com.Release(ref pickOutput);
                Com.Release(ref pickAdapter);
                Com.Release(ref factory);
            }
        }

        /// <returns>1 new picture in Bgra, 0 nothing new, throws COMException on loss</returns>
        public int Acquire(int timeoutMs)
        {
            IntPtr res;
            int hr = Com.F<D_Acquire>(dup, 8)(dup, (uint)timeoutMs, frameInfo, out res);
            if (hr == WAIT_TIMEOUT) return 0;
            Com.Check(hr, "AcquireNextFrame");
            try
            {
                long present = Marshal.ReadInt64(frameInfo, 0);
                int accumulated = Marshal.ReadInt32(frameInfo, 16);
                if (accumulated == 0) return 0; // only the pointer moved
                IntPtr tex = Com.QI(res, IID_Texture2D);
                Com.F<D_Copy>(ctx, 47)(ctx, staging, tex); // CopyResource
                Com.Release(ref tex);
                Com.Check(Com.F<D_Map>(ctx, 14)(ctx, staging, 0, 1 /*READ*/, 0, mapped), "Map");
                IntPtr p = Marshal.ReadIntPtr(mapped, 0);
                int pitch = Marshal.ReadInt32(mapped, IntPtr.Size);
                int row = W * 4;
                for (int y = 0; y < H; y++) Marshal.Copy(p + y * pitch, Bgra, y * row, row);
                Com.F<D_Unmap>(ctx, 15)(ctx, staging, 0);
                LastPresentMicros = present != 0 ? Clock.QpcToMicros(present) : Clock.Micros();
                return 1;
            }
            finally
            {
                Com.Release(ref res);
                Com.F<D_Self>(dup, 14)(dup); // ReleaseFrame
            }
        }

        public void Dispose()
        {
            Com.Release(ref staging);
            Com.Release(ref dup);
            Com.Release(ref ctx);
            Com.Release(ref device);
        }
    }

    /// <summary>Desktop Duplication leaves the mouse pointer out; it is drawn into the picture.</summary>
    sealed class Pointer
    {
        public int X, Y, W, H;
        public bool Visible;
        public byte[] Px; // premultiplied BGRA
        IntPtr handle;

        /// <returns>true if position, shape or visibility changed</returns>
        public bool Update(int originX, int originY)
        {
            CURSORINFO ci = new CURSORINFO();
            ci.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
            if (!Native.GetCursorInfo(ref ci)) return false;
            bool vis = (ci.flags & 1) != 0 && ci.hCursor != IntPtr.Zero;
            bool changed = vis != Visible;
            Visible = vis;
            if (!vis) return changed;
            int hx = 0, hy = 0;
            if (ci.hCursor != handle)
            {
                handle = ci.hCursor;
                Render(ci.hCursor, out hx, out hy);
                hotX = hx;
                hotY = hy;
                changed = true;
            }
            int x = ci.pt.X - originX - hotX, y = ci.pt.Y - originY - hotY;
            if (x != X || y != Y) changed = true;
            X = x;
            Y = y;
            return changed;
        }

        int hotX, hotY;

        // drawn once on black and once on white: alpha and colour follow from the difference
        void Render(IntPtr cursor, out int hx, out int hy)
        {
            ICONINFO ii;
            hx = hy = 0;
            if (!Native.GetIconInfo(cursor, out ii)) { Px = null; return; }
            hx = ii.xHotspot;
            hy = ii.yHotspot;
            BITMAP bm;
            int w = 32, h = 32;
            if (ii.hbmColor != IntPtr.Zero && Native.GetObject(ii.hbmColor, Marshal.SizeOf(typeof(BITMAP)), out bm) != 0) { w = bm.bmWidth; h = bm.bmHeight; }
            else if (ii.hbmMask != IntPtr.Zero && Native.GetObject(ii.hbmMask, Marshal.SizeOf(typeof(BITMAP)), out bm) != 0) { w = bm.bmWidth; h = bm.bmHeight / 2; }
            if (ii.hbmMask != IntPtr.Zero) Native.DeleteObject(ii.hbmMask);
            if (ii.hbmColor != IntPtr.Zero) Native.DeleteObject(ii.hbmColor);
            if (w <= 0 || h <= 0 || w > 256 || h > 256) { Px = null; return; }

            byte[][] layers = new byte[2][];
            IntPtr dc = Native.CreateCompatibleDC(IntPtr.Zero);
            for (int l = 0; l < 2; l++)
            {
                BITMAPINFOHEADER bi = new BITMAPINFOHEADER();
                bi.biSize = 40; bi.biWidth = w; bi.biHeight = -h; bi.biPlanes = 1; bi.biBitCount = 32;
                IntPtr bits;
                IntPtr bmp = Native.CreateDIBSection(dc, ref bi, 0, out bits, IntPtr.Zero, 0);
                IntPtr old = Native.SelectObject(dc, bmp);
                byte[] fill = new byte[w * h * 4];
                if (l == 1) for (int i = 0; i < fill.Length; i++) fill[i] = 255;
                Marshal.Copy(fill, 0, bits, fill.Length);
                Native.DrawIconEx(dc, 0, 0, cursor, w, h, 0, IntPtr.Zero, 3 /*DI_NORMAL*/);
                Native.GdiFlush();
                Marshal.Copy(bits, fill, 0, fill.Length);
                layers[l] = fill;
                Native.SelectObject(dc, old);
                Native.DeleteObject(bmp);
            }
            Native.DeleteDC(dc);
            byte[] px = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++)
            {
                int o = i * 4;
                int diff = ((layers[1][o] - layers[0][o]) + (layers[1][o + 1] - layers[0][o + 1]) + (layers[1][o + 2] - layers[0][o + 2])) / 3;
                int a = 255 - diff;
                if (a < 0) a = 255; // inverting (XOR) pixels: shown as drawn on black
                if (a > 255) a = 255;
                byte b = layers[0][o], g = layers[0][o + 1], r = layers[0][o + 2];
                if (diff < 0) { b = layers[1][o]; g = layers[1][o + 1]; r = layers[1][o + 2]; b = (byte)(255 - b); g = (byte)(255 - g); r = (byte)(255 - r); }
                px[o] = b; px[o + 1] = g; px[o + 2] = r; px[o + 3] = (byte)a;
            }
            W = w;
            H = h;
            Px = px;
        }
    }

    static class Nv12
    {
        /// <summary>BGRA (sw x sh) -> NV12 (dw x dh, nearest-neighbour scaling), BT.709 limited
        /// range, with the pointer blended in.</summary>
        public static unsafe void Convert(byte[] src, int sw, int sh, byte[] dst, int dw, int dh, Pointer p)
        {
            int[] xs = new int[dw], ys = new int[dh];
            for (int x = 0; x < dw; x++) xs[x] = (int)((long)x * sw / dw);
            for (int y = 0; y < dh; y++) ys[y] = (int)((long)y * sh / dh);
            GCHandle hs = GCHandle.Alloc(src, GCHandleType.Pinned), hd = GCHandle.Alloc(dst, GCHandleType.Pinned);
            IntPtr ps = hs.AddrOfPinnedObject(), pd = hd.AddrOfPinnedObject();
            bool cur = p != null && p.Visible && p.Px != null;
            int cx = cur ? p.X : 0, cy = cur ? p.Y : 0, cw = cur ? p.W : 0, ch = cur ? p.H : 0;
            byte[] cpx = cur ? p.Px : null;
            try
            {
                Parallel.For(0, dh / 2, by =>
                {
                    byte* s = (byte*)ps;
                    byte* d = (byte*)pd;
                    int uvRow = dw * dh + by * dw;
                    for (int bx = 0; bx < dw / 2; bx++)
                    {
                        int rs = 0, gs = 0, bs = 0;
                        for (int k = 0; k < 4; k++)
                        {
                            int x = bx * 2 + (k & 1), y = by * 2 + (k >> 1);
                            int sx = xs[x], sy = ys[y];
                            byte* q = s + ((long)sy * sw + sx) * 4;
                            int b = q[0], g = q[1], r = q[2];
                            if (cur && sx >= cx && sy >= cy && sx < cx + cw && sy < cy + ch)
                            {
                                int o = ((sy - cy) * cw + (sx - cx)) * 4;
                                int inv = 255 - cpx[o + 3];
                                b = cpx[o] + b * inv / 255;
                                g = cpx[o + 1] + g * inv / 255;
                                r = cpx[o + 2] + r * inv / 255;
                            }
                            d[y * dw + x] = (byte)((47 * r + 157 * g + 16 * b + 4096 + 128) >> 8);
                            rs += r; gs += g; bs += b;
                        }
                        rs >>= 2; gs >>= 2; bs >>= 2;
                        d[uvRow + bx * 2] = (byte)((-26 * rs - 87 * gs + 112 * bs + 32768 + 128) >> 8);
                        d[uvRow + bx * 2 + 1] = (byte)((112 * rs - 102 * gs - 10 * bs + 32768 + 128) >> 8);
                    }
                });
            }
            finally
            {
                hs.Free();
                hd.Free();
            }
        }
    }

    // ---- H.264 encoder (Media Foundation transform) --------------------------------------------

    sealed class Encoder : IDisposable
    {
        static Guid CAT_VIDEO_ENCODER = new Guid("f79eac7d-e545-4387-bdee-d647d7bde42a");
        static Guid MF_MT_MAJOR_TYPE = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        static Guid MF_MT_SUBTYPE = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        static Guid MFMediaType_Video = new Guid("73646976-0000-0010-8000-00AA00389B71");
        static Guid MFVideoFormat_H264 = new Guid("34363248-0000-0010-8000-00AA00389B71");
        static Guid MFVideoFormat_NV12 = new Guid("3231564E-0000-0010-8000-00AA00389B71");
        static Guid MF_MT_FRAME_SIZE = new Guid("1652c33d-d6b2-4012-b834-72030849a37d");
        static Guid MF_MT_FRAME_RATE = new Guid("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        static Guid MF_MT_AVG_BITRATE = new Guid("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
        static Guid MF_MT_INTERLACE_MODE = new Guid("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
        static Guid MF_MT_PIXEL_ASPECT_RATIO = new Guid("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
        static Guid MF_MT_MPEG2_PROFILE = new Guid("ad76a80b-2d5c-4e0b-b375-64e520137036");
        static Guid MF_MT_DEFAULT_STRIDE = new Guid("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
        static Guid MF_MT_YUV_MATRIX = new Guid("3e23d450-2c75-4d25-a00e-b91670d12327");
        static Guid MF_MT_VIDEO_NOMINAL_RANGE = new Guid("c21b8ee5-b956-4071-8daf-325edf5cab11");
        static Guid MF_MT_MPEG_SEQUENCE_HEADER = new Guid("3c036de7-3ad0-4c9e-9216-ee6d6ac21cb3");
        static Guid MFT_FRIENDLY_NAME = new Guid("314ffbae-5b41-4c95-9c19-4e7d586face3");
        static Guid MF_TRANSFORM_ASYNC_UNLOCK = new Guid("e5666d6b-3422-4eb6-a421-da7db1f8e207");
        static Guid MF_LOW_LATENCY = new Guid("9c27891a-ed7a-40e1-88e8-b22727a024ee");
        static Guid MFSampleExtension_CleanPoint = new Guid("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");
        static Guid IID_IMFTransform = new Guid("bf94c121-5b05-4e6f-8000-ba598961414d");
        static Guid IID_ICodecAPI = new Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");
        static Guid IID_EventGenerator = new Guid("2CD0BD52-BCD5-4B89-B62C-EADC0C031E7D");
        static Guid API_LowLatency = new Guid("9c27891a-ed7a-40e1-88e8-b22727a024ee");
        static Guid API_RateControl = new Guid("1c0608e9-370c-4710-8a58-cb6181c42423");
        static Guid API_MeanBitRate = new Guid("f7222374-2144-4815-b550-a37f8e12ee52");
        static Guid API_GopSize = new Guid("95f31b26-95a4-41aa-9303-246a7fc6eef1");
        static Guid API_BFrames = new Guid("8d390aac-dc5c-4200-b57f-814d04babab2");
        static Guid API_ForceKey = new Guid("398c1b98-8353-475a-9ef2-8f265d260345");
        static Guid API_QualityVsSpeed = new Guid("98332df8-03cd-476b-89fa-3f9e442dec9f"); // 0 = fastest

        const int MF_E_NO_EVENTS = unchecked((int)0xC00D3E80);
        const int MF_E_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
        const int MF_E_STREAM_CHANGE = unchecked((int)0xC00D6D61);

        IntPtr mft, events, codec;
        readonly IntPtr variant = Marshal.AllocHGlobal(24);
        bool async;
        bool providesSamples;
        uint outSize;
        readonly int w, h, fps;
        int credits;
        byte[] csd;
        public readonly string Name;
        public readonly bool Hardware;

        /// <summary>Tries the hardware encoders (NVIDIA first), then the Windows software one.
        /// On a hybrid laptop only the encoder of the GPU this process runs on may start (the
        /// NVIDIA one refuses with E_UNEXPECTED in a process on the integrated GPU).</summary>
        /// <param name="profile">H.264 profile: 100 High (TV), 66 Baseline (browsers: WebRTC
        /// negotiates Constrained Baseline everywhere, High was refused)</param>
        public Encoder(int w, int h, int fps, int bitrate, int profile = 100)
        {
            this.w = w; this.h = h; this.fps = fps;
            List<KeyValuePair<IntPtr, string>> hw = Find(0x4 | 0x2 | 0x40), sw = Find(0x1 | 0x40); // hardware async / software
            Exception last = null;
            for (int i = 0; i < hw.Count + sw.Count; i++)
            {
                KeyValuePair<IntPtr, string> c = i < hw.Count ? hw[i] : sw[i - hw.Count];
                if (mft == IntPtr.Zero)
                {
                    try
                    {
                        Open(c.Key, bitrate, profile);
                        Name = c.Value;
                        Hardware = i < hw.Count;
                    }
                    catch (Exception e)
                    {
                        last = e;
                        Com.Release(ref events);
                        Com.Release(ref codec);
                        Com.Release(ref mft);
                        csd = null;
                    }
                }
                Marshal.Release(c.Key);
            }
            if (mft == IntPtr.Zero) throw last ?? new Exception(Texts.NoEncoder);
        }

        void Open(IntPtr act, int bitrate, int profile)
        {
            Guid iid = IID_IMFTransform;
            int hr = Com.F<D_Activate>(act, 33)(act, ref iid, out mft);
            Com.Check(hr, "ActivateObject");

            IntPtr attrs;
            if (Com.F<D_Out>(mft, 8)(mft, out attrs) >= 0 && attrs != IntPtr.Zero)
            {
                uint isAsync = 0;
                Guid k = new Guid("f81a699a-649a-497d-8c73-29f8fed6ad7a"); // MF_TRANSFORM_ASYNC
                Com.F<D_GetU32>(attrs, 7)(attrs, ref k, out isAsync);
                async = isAsync != 0;
                if (async) Com.F<D_SetU32>(attrs, 21)(attrs, ref MF_TRANSFORM_ASYNC_UNLOCK, 1);
                Com.F<D_SetU32>(attrs, 21)(attrs, ref MF_LOW_LATENCY, 1);
                Com.Release(ref attrs);
            }
            if (async) events = Com.QI(mft, IID_EventGenerator);
            codec = Com.QI(mft, IID_ICodecAPI);
            SetCodec(API_LowLatency, 11, -1);
            SetCodec(API_RateControl, 19, 0); // CBR
            SetCodec(API_MeanBitRate, 19, bitrate);
            SetCodec(API_GopSize, 19, fps * 2); // safety-net keyframe every 2 s; more on request
            SetCodec(API_BFrames, 19, 0);
            // speed over quality: the AMD encoder needed ~9 ms a 2340x1080 frame, which held a
            // phone's extended screen at ~75 fps instead of 120 (the bitrate keeps the quality)
            SetCodec(API_QualityVsSpeed, 19, 0);

            IntPtr outType = VideoType(MFVideoFormat_H264);
            Com.F<D_SetU32>(outType, 21)(outType, ref MF_MT_AVG_BITRATE, (uint)bitrate);
            Com.F<D_SetU32>(outType, 21)(outType, ref MF_MT_MPEG2_PROFILE, (uint)profile); // 100 High, 66 Baseline
            hr = Com.F<D_SetType>(mft, 16)(mft, 0, outType, 0);
            Com.Release(ref outType);
            Com.Check(hr, "SetOutputType");
            IntPtr inType = VideoType(MFVideoFormat_NV12);
            Com.F<D_SetU32>(inType, 21)(inType, ref MF_MT_DEFAULT_STRIDE, (uint)w);
            hr = Com.F<D_SetType>(mft, 15)(mft, 0, inType, 0);
            Com.Release(ref inType);
            Com.Check(hr, "SetInputType");

            IntPtr cur;
            if (Com.F<D_GetType>(mft, 18)(mft, 0, out cur) >= 0)
            {
                IntPtr blob; uint len;
                if (Com.F<D_GetAlloc>(cur, 16)(cur, ref MF_MT_MPEG_SEQUENCE_HEADER, out blob, out len) >= 0 && len > 0)
                {
                    csd = new byte[len];
                    Marshal.Copy(blob, csd, 0, (int)len);
                    Native.CoTaskMemFree(blob);
                }
                Com.Release(ref cur);
            }

            IntPtr info = Marshal.AllocHGlobal(12);
            Com.F<D_StreamInfo>(mft, 7)(mft, 0, info);
            uint flags = (uint)Marshal.ReadInt32(info, 0);
            outSize = (uint)Marshal.ReadInt32(info, 4);
            Marshal.FreeHGlobal(info);
            providesSamples = (flags & 0x300) != 0;
            if (outSize == 0) outSize = (uint)(w * h * 2);

            Com.F<D_Msg>(mft, 23)(mft, 0x10000000, IntPtr.Zero); // BEGIN_STREAMING
            Com.F<D_Msg>(mft, 23)(mft, 0x10000003, IntPtr.Zero); // START_OF_STREAM
        }

        /// <summary>the encoders for these flags with their names, NVIDIA first (usually the
        /// fastest); the caller releases every activate</summary>
        static List<KeyValuePair<IntPtr, string>> Find(uint flags)
        {
            List<KeyValuePair<IntPtr, string>> list = new List<KeyValuePair<IntPtr, string>>();
            IntPtr inInfo = TypeInfo(MFVideoFormat_NV12), outInfo = TypeInfo(MFVideoFormat_H264);
            IntPtr acts; uint n;
            int hr = Native.MFTEnumEx(CAT_VIDEO_ENCODER, flags, inInfo, outInfo, out acts, out n);
            Marshal.FreeHGlobal(inInfo);
            Marshal.FreeHGlobal(outInfo);
            if (hr < 0 || n == 0) return list;
            for (int i = 0; i < n; i++)
            {
                IntPtr a = Marshal.ReadIntPtr(acts, i * IntPtr.Size);
                string nm = "";
                IntPtr s; uint len;
                if (Com.F<D_GetAlloc>(a, 13)(a, ref MFT_FRIENDLY_NAME, out s, out len) >= 0)
                {
                    nm = Marshal.PtrToStringUni(s);
                    Native.CoTaskMemFree(s);
                }
                KeyValuePair<IntPtr, string> e = new KeyValuePair<IntPtr, string>(a, nm);
                if (nm.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0) list.Insert(0, e);
                else list.Add(e);
            }
            Native.CoTaskMemFree(acts);
            return list;
        }

        static IntPtr TypeInfo(Guid sub)
        {
            IntPtr p = Marshal.AllocHGlobal(32);
            Marshal.Copy(MFMediaType_Video.ToByteArray(), 0, p, 16);
            Marshal.Copy(sub.ToByteArray(), 0, p + 16, 16);
            return p;
        }

        IntPtr VideoType(Guid sub)
        {
            IntPtr t;
            Com.Check(Native.MFCreateMediaType(out t), "MFCreateMediaType");
            Com.F<D_SetGuid>(t, 24)(t, ref MF_MT_MAJOR_TYPE, ref MFMediaType_Video);
            Com.F<D_SetGuid>(t, 24)(t, ref MF_MT_SUBTYPE, ref sub);
            Com.F<D_SetU64>(t, 22)(t, ref MF_MT_FRAME_SIZE, ((ulong)w << 32) | (uint)h);
            Com.F<D_SetU64>(t, 22)(t, ref MF_MT_FRAME_RATE, ((ulong)fps << 32) | 1);
            Com.F<D_SetU64>(t, 22)(t, ref MF_MT_PIXEL_ASPECT_RATIO, (1UL << 32) | 1);
            Com.F<D_SetU32>(t, 21)(t, ref MF_MT_INTERLACE_MODE, 2); // progressive
            Com.F<D_SetU32>(t, 21)(t, ref MF_MT_YUV_MATRIX, 1); // BT.709
            Com.F<D_SetU32>(t, 21)(t, ref MF_MT_VIDEO_NOMINAL_RANGE, 2); // 16..235
            return t;
        }

        void SetCodec(Guid api, short vt, int value)
        {
            if (codec == IntPtr.Zero) return;
            byte[] z = new byte[24];
            Marshal.Copy(z, 0, variant, 24);
            Marshal.WriteInt16(variant, 0, vt);
            if (vt == 11) Marshal.WriteInt16(variant, 8, (short)value);
            else Marshal.WriteInt32(variant, 8, value);
            Com.F<D_CodecSet>(codec, 9)(codec, ref api, variant);
        }

        /// <summary>An async (hardware) encoder takes a frame only after asking for one.</summary>
        public bool CanTakeInput { get { return !async || credits > 0; } }

        public void Input(byte[] nv12, long ptsMicros, bool forceKey)
        {
            IntPtr buf, sample;
            Com.Check(Native.MFCreateMemoryBuffer((uint)nv12.Length, out buf), "MFCreateMemoryBuffer");
            IntPtr p; uint max, cur;
            Com.F<D_Lock>(buf, 3)(buf, out p, out max, out cur);
            Marshal.Copy(nv12, 0, p, nv12.Length);
            Com.F<D_Self>(buf, 4)(buf);
            Com.F<D_SetLen>(buf, 6)(buf, (uint)nv12.Length);
            Native.MFCreateSample(out sample);
            Com.F<D_Buf>(sample, 42)(sample, buf);
            Com.F<D_SetI64>(sample, 36)(sample, ptsMicros * 10);
            Com.F<D_SetI64>(sample, 38)(sample, 10000000L / fps);
            if (forceKey) SetCodec(API_ForceKey, 19, 1);
            int hr = Com.F<D_PIn>(mft, 24)(mft, 0, sample, 0);
            Com.Release(ref sample);
            Com.Release(ref buf);
            Com.Check(hr, "ProcessInput");
            if (async) credits--;
        }

        public delegate void FrameHandler(byte[] data, int len, bool key, long ptsMicros);

        /// <summary>Hands every finished frame to onFrame. Never blocks.</summary>
        public void Pump(FrameHandler onFrame)
        {
            if (!async)
            {
                while (Drain(onFrame) >= 0) { }
                return;
            }
            while (true)
            {
                IntPtr ev;
                int hr = Com.F<D_GetEvent>(events, 3)(events, 1 /*NO_WAIT*/, out ev);
                if (hr == MF_E_NO_EVENTS) return;
                Com.Check(hr, "GetEvent");
                uint type;
                Com.F<D_GetEvType>(ev, 33)(ev, out type);
                Com.Release(ref ev);
                if (type == 601) credits++; // METransformNeedInput
                else if (type == 602) Drain(onFrame); // METransformHaveOutput
                else if (type == 1) throw new Exception(Texts.EncoderError);
            }
        }

        byte[] outBuf = new byte[1 << 20];

        int Drain(FrameHandler onFrame)
        {
            IntPtr db = Marshal.AllocHGlobal(32);
            for (int i = 0; i < 32; i += 8) Marshal.WriteInt64(db, i, 0);
            IntPtr mine = IntPtr.Zero, mineBuf = IntPtr.Zero;
            if (!providesSamples)
            {
                Native.MFCreateSample(out mine);
                Native.MFCreateMemoryBuffer(outSize, out mineBuf);
                Com.F<D_Buf>(mine, 42)(mine, mineBuf);
                Marshal.WriteIntPtr(db, 8, mine);
            }
            uint status;
            int hr = Com.F<D_POut>(mft, 25)(mft, 0, 1, db, out status);
            IntPtr sample = Marshal.ReadIntPtr(db, 8);
            IntPtr evs = Marshal.ReadIntPtr(db, 24);
            Marshal.FreeHGlobal(db);
            Com.Release(ref evs);
            try
            {
                if (hr == MF_E_STREAM_CHANGE)
                {
                    IntPtr t;
                    if (Com.F<D_AvailType>(mft, 14)(mft, 0, 0, out t) >= 0)
                    {
                        Com.F<D_SetType>(mft, 16)(mft, 0, t, 0);
                        Com.Release(ref t);
                    }
                    return 0;
                }
                if (hr < 0 || sample == IntPtr.Zero) return hr < 0 ? hr : -1;
                IntPtr cb;
                Com.Check(Com.F<D_Out>(sample, 41)(sample, out cb), "ConvertToContiguousBuffer");
                IntPtr p; uint max, len;
                Com.F<D_Lock>(cb, 3)(cb, out p, out max, out len);
                uint key = 0;
                Com.F<D_GetU32>(sample, 7)(sample, ref MFSampleExtension_CleanPoint, out key);
                long t100;
                Com.F<D_GetI64>(sample, 35)(sample, out t100);
                int pre = 0;
                if (key != 0)
                {
                    byte[] head = new byte[Math.Min((int)len, 256)];
                    Marshal.Copy(p, head, 0, head.Length);
                    if (HasSps(head)) csd = null; // the stream carries its own headers
                    else if (csd != null) pre = csd.Length;
                }
                if (outBuf.Length < pre + len) outBuf = new byte[pre + len];
                if (pre > 0) Buffer.BlockCopy(csd, 0, outBuf, 0, pre);
                Marshal.Copy(p, outBuf, pre, (int)len);
                Com.F<D_Self>(cb, 4)(cb);
                Com.Release(ref cb);
                onFrame(outBuf, pre + (int)len, key != 0, t100 / 10);
                return 0;
            }
            finally
            {
                if (sample != mine) Com.Release(ref sample);
                Com.Release(ref mine);
                Com.Release(ref mineBuf);
            }
        }

        static bool HasSps(byte[] b)
        {
            for (int i = 0; i + 4 < b.Length; i++)
                if (b[i] == 0 && b[i + 1] == 0 && b[i + 2] == 1 && (b[i + 3] & 0x1F) == 7) return true;
            return false;
        }

        public void Dispose()
        {
            if (mft != IntPtr.Zero)
            {
                Com.F<D_Msg>(mft, 23)(mft, 0x10000001, IntPtr.Zero); // END_OF_STREAM
                Com.F<D_Msg>(mft, 23)(mft, 0x10000004, IntPtr.Zero); // END_STREAMING
            }
            Com.Release(ref codec);
            Com.Release(ref events);
            Com.Release(ref mft);
        }
    }

    // ---- the cast ------------------------------------------------------------------------------

    sealed class Caster
    {
        /// <summary>The TV panel is 60 Hz and its decoder tops out near 90-100 fps (1080p less), so 60.</summary>
        public static int FPS = 60;
        static int BITRATE { get { return FPS > 60 ? 30000000 : 20000000; } }
        /// <summary>to an iPad over Wi-Fi (its Wi-Fi saves power: ping 10-600 ms even on 5 GHz).
        /// 20 Mb/s stalled every few seconds; 1600x900 at 5 Mb/s was smooth but blurry on the
        /// iPad's screen; 1080p at 10 Mb/s is the middle</summary>
        public static int WebBitrate = 10000000, WebMaxW = 1920, WebMaxH = 1080;
        public static int MaxW = 1920, MaxH = 1080;
        /// <summary>--test-pattern: a moving picture instead of the screen (testing without screen capture)</summary>
        public static bool TestPattern;

        IPAddress tv;
        /// <summary>the receiver's name from the TV list: when it goes quiet (the USB cable was
        /// pulled) it is looked up again and the cast moves to its other address (Wi-Fi)</summary>
        public string TvName;
        volatile bool restartVideo; // USB <-> Wi-Fi changes the bitrate and size: start the encoder again
        readonly bool withAudio;
        volatile Socket sock;
        volatile bool on;
        /// <summary>a phone that shows this cast may control the PC (the window's tick)</summary>
        public volatile bool AllowControl;
        // random per cast, sent in CONFIG: INPUT must carry it (the socket is connected to the
        // receiver anyway, so only it can answer; the token keeps a spoofed source address out)
        readonly byte[] token = NewToken();

        static byte[] NewToken()
        {
            byte[] t = new byte[Proto.TOKEN_BYTES];
            using (System.Security.Cryptography.RNGCryptoServiceProvider r = new System.Security.Cryptography.RNGCryptoServiceProvider()) r.GetBytes(t);
            return t;
        }

        /// <summary>an INPUT packet with this cast's token: the input message in it, else null</summary>
        internal static byte[] InputMessage(byte[] pkt, int n, byte[] token)
        {
            if (n < 2 + Proto.TOKEN_BYTES || pkt[0] != Proto.INPUT) return null;
            int diff = 0;
            for (int i = 0; i < Proto.TOKEN_BYTES; i++) diff |= pkt[1 + i] ^ token[i];
            if (diff != 0) return null;
            byte[] m = new byte[n - 1 - Proto.TOKEN_BYTES];
            Buffer.BlockCopy(pkt, 1 + Proto.TOKEN_BYTES, m, 0, m.Length);
            return m;
        }
        Thread video, audio, control;
        volatile bool keyWanted = true;
        volatile int vw, vh, audioRate;
        int frameId;
        readonly object sendLock = new object();

        public volatile string Status = "";
        // the encrypted link (SecureLink.cs): handshake state, the receiver's key and name, pairing
        volatile SecureLink.Session link;
        volatile bool linkReady;
        CngKeyHolder eph;
        byte[] myNonce, helloPkt, peerStatic;
        long helloSince, lastHello, lastSealedHeard, lastPairShow;
        /// <summary>the 6-digit code while the user should compare it with the receiver's screen</summary>
        public volatile string PairCode;
        /// <summary>0 not answered, 1 the codes match, -1 they do not (the cast then stops)</summary>
        public volatile int PairAnswer;
        public volatile bool PairRefused;
        public volatile string PeerName = "";
        /// <summary>pairing / an old receiver: shown in the window apart from Status</summary>
        public volatile string LinkStatus = "";
        [ThreadStatic] static byte[] sealBuf;
        /// <summary>capture failed because Windows runs this app on the wrong GPU</summary>
        public volatile bool WrongGpu;
        /// <summary>with WrongGpu: the screen is on the NVIDIA card (wants "High performance")</summary>
        public volatile bool WrongGpuNvidia;
        public volatile string EncoderName = "";
        public long Frames, Bytes;
        // diagnostics: encoder latency (input -> output, same pts) and NV12 conversion time, summed
        public long EncNs, EncN, ConvNs, ConvN;
        public volatile string FirstKeyHex = "";
        readonly Dictionary<long, long> inputAt = new Dictionary<long, long>();

        public Caster(IPAddress tv, bool withAudio)
        {
            this.tv = tv;
            this.withAudio = withAudio;
            // a phone on a USB cable (USB tethering): hundreds of Mb/s and no Wi-Fi in between,
            // so the best picture: higher bitrate and up to 2560 x 1600
            Usb = WebCast.IsUsbAddress(tv);
        }

        /// <summary>the receiver is a phone on a USB cable</summary>
        public volatile bool Usb;

        /// <summary>the name part of a TV list entry: "Living room TV (address)" -> "Living room TV"</summary>
        internal static string NameOf(string entry)
        {
            if (string.IsNullOrEmpty(entry)) return null;
            int open = entry.LastIndexOf(" (", StringComparison.Ordinal);
            return open > 0 && entry.EndsWith(")") ? entry.Substring(0, open) : null;
        }

        public int Moves;    // how often the cast moved between USB and Wi-Fi (for the log)
        public volatile string RouteNote = "-";
        public long Heard; public volatile string RecvError = "-"; // packets from the receiver, last error (log) // the last search of RouteLoop (for the log)
        long lastHeard;      // Clock.Ms() of the last packet from the receiver
        IPAddress moveTo;    // set by RouteLoop, applied by ControlLoop (which owns the socket)

        /// <summary>Keeps a phone on the best link: when it goes quiet for 3 s (it pings twice a
        /// second) the USB cable was pulled, so the same name is looked up and the cast moves to its
        /// Wi-Fi address; when a USB tethering cable appears while on Wi-Fi, it moves to the cable.</summary>
        void RouteLoop()
        {
            long lastTry = 0;
            while (on)
            {
                Thread.Sleep(1000);
                long now = Clock.Ms();
                bool quiet = now - Interlocked.Read(ref lastHeard) > 3000;
                bool cable = !Usb && WebCast.HasUsbTethering();
                if (!(quiet || cable) || now - lastTry < 4000 || moveTo != null) continue;
                lastTry = now;
                List<KeyValuePair<string, IPAddress>> found = Discovery.Find(1500);
                IPAddress other = PickRoute(found, TvName, tv, quiet);
                RouteNote = (quiet ? "quiet" : "cable") + ", found " + found.Count + (other != null ? ", moving" : "");
                if (other != null && on) Interlocked.Exchange(ref moveTo, other);
            }
        }

        /// <summary>the same receiver at another address: on the cable when there is one, else
        /// (only when the current one went quiet) any other</summary>
        internal static IPAddress PickRoute(List<KeyValuePair<string, IPAddress>> found, string name,
            IPAddress current, bool quiet)
        {
            IPAddress any = null;
            foreach (KeyValuePair<string, IPAddress> f in found)
            {
                if (f.Key != name || f.Value.Equals(current)) continue;
                if (WebCast.IsUsbAddress(f.Value)) return f.Value;
                if (any == null) any = f.Value;
            }
            return quiet ? any : null;
        }
        int UsbBitrate { get { return Fps > 60 ? 80000000 : 50000000; } }
        /// <summary>frames a second for this cast: a phone on the USB cable always gets 120 (its
        /// screen is 120 Hz and the cable has room), otherwise the "120" box decides</summary>
        public const int UsbFps = 120;
        public int Fps { get { return web == null && Usb ? Math.Max(FPS, UsbFps) : FPS; } }
        public static int UsbMaxW = 2560, UsbMaxH = 1600;
        int Bitrate { get { return web != null ? WebBitrate : Usb ? UsbBitrate : BITRATE; } }

        /// <summary>the receiver's screen (INFO), 0 until an INFO came (older receivers send none)</summary>
        public volatile int RecvW, RecvH;
        /// <summary>a PC receiver's refresh rate (INFO), 0 for a TV or phone</summary>
        public volatile int RecvHz;

        /// <summary>frames a second to a PC receiver: as many as both screens show, at most 240
        /// (unit-tested)</summary>
        internal static int PcFps(int receiverHz, int ownHz)
        {
            return Math.Max(30, Math.Min(240, Math.Min(receiverHz, ownHz)));
        }

        /// <summary>bits a second to a PC receiver: 20 Mb/s per 60 frames of 1080p, more pixels a
        /// little more, at most 80 Mb/s (unit-tested)</summary>
        internal static int PcBitrate(int fps, int w, int h)
        {
            double k = fps / 60.0 * Math.Max(1.0, Math.Min(1.6, (double)w * h / (1920.0 * 1080.0)));
            return (int)Math.Min(80000000, 20000000 * k);
        }
        public const int MaxSendW = 2560, MaxSendH = 1600;

        /// <summary>the largest picture to send: the receiver's own screen when it said (a phone gets
        /// 2340 x 1080 instead of a 1920-wide picture it stretches), else the old limits</summary>
        internal static void SizeLimit(bool toWeb, bool usb, int recvW, int recvH, out int maxW, out int maxH)
        {
            if (toWeb) { maxW = Math.Min(MaxW, WebMaxW); maxH = Math.Min(MaxH, WebMaxH); return; }
            if (recvW > 0 && recvH > 0) { maxW = Math.Min(recvW, MaxSendW); maxH = Math.Min(recvH, MaxSendH); return; }
            maxW = usb ? Math.Max(MaxW, UsbMaxW) : MaxW;
            maxH = usb ? Math.Max(MaxH, UsbMaxH) : MaxH;
        }

        /// <summary>more pixels need more bits: the Wi-Fi rate grows with the picture (1080p = 1x),
        /// up to 1.6x; USB has room for its own high rate anyway</summary>
        internal static int ScaledBitrate(int baseRate, int w, int h, bool usb)
        {
            if (usb) return baseRate;
            double k = Math.Max(1.0, Math.Min(1.6, (double)w * h / (1920.0 * 1080.0)));
            return (int)(baseRate * k);
        }

        /// <summary>to a browser (iPad) instead of the TV app: same capture and encoder, the
        /// frames and sound go to the WebCast server</summary>
        public Caster(WebCast web, bool withAudio)
        {
            this.web = web;
            this.withAudio = withAudio;
            long lastKey = 0;
            web.KeyRequest = delegate
            {
                long now = Clock.Ms();
                if (now - Interlocked.Read(ref lastKey) < 150) return;
                Interlocked.Exchange(ref lastKey, now);
                keyWanted = true;
            };
        }

        readonly WebCast web;

        sealed class CngKeyHolder : IDisposable
        {
            public readonly System.Security.Cryptography.CngKey Key;
            public CngKeyHolder(System.Security.Cryptography.CngKey k) { Key = k; }
            public void Dispose() { Key.Dispose(); }
        }

        static Socket NewSocket(IPAddress to)
        {
            Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.SendBufferSize = 4 << 20;
            s.ReceiveTimeout = 250;
            // otherwise a closed TV app (ICMP port unreachable) makes Receive throw forever
            s.IOControl(-1744830452 /*SIO_UDP_CONNRESET*/, new byte[] { 0, 0, 0, 0 }, null);
            s.Connect(new IPEndPoint(to, Proto.PORT));
            return s;
        }

        public void Start()
        {
            if (web == null)
            {
                sock = NewSocket(tv);
            }
            wifi = new WifiStreamingMode();
            on = true;
            video = new Thread(VideoLoop) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "video" };
            video.SetApartmentState(ApartmentState.MTA);
            video.Start();
            if (withAudio)
            {
                audio = new Thread(AudioLoop) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "audio" };
                audio.SetApartmentState(ApartmentState.MTA);
                audio.Start();
            }
            if (web != null) return; // keyframe requests come from the WebCast server
            control = new Thread(ControlLoop) { IsBackground = true, Name = "control" };
            control.Start();
        }

        public void Stop()
        {
            if (!on) return;
            on = false;
            if (video != null) video.Join(1000);
            if (audio != null) audio.Join(1000);
            if (control != null) control.Join(1000);
            RemoteInput.ReleaseButtons(); // a PC receiver's held Ctrl / Alt / mouse button must not stay down
            if (sock != null)
            {
                for (int i = 0; i < 3; i++) SendSealed(new byte[] { Proto.BYE }, 1);
                sock.Close();
            }
            wifi.Dispose();
        }

        WifiStreamingMode wifi;

        /// <summary>a packet of the cast: sealed, and only once the receiver is confirmed</summary>
        void Send(byte[] b, int len)
        {
            if (!linkReady) return;
            SendSealed(b, len);
        }

        void SendSealed(byte[] b, int len)
        {
            SecureLink.Session l = link;
            if (l == null) return;
            if (sealBuf == null || sealBuf.Length < len + SecureLink.OVERHEAD) sealBuf = new byte[Math.Max(2048, len + SecureLink.OVERHEAD)];
            int n = l.Seal(b, len, sealBuf);
            SendRaw(sealBuf, n);
        }

        void SendRaw(byte[] b, int len)
        {
            Socket s = sock; // ControlLoop may swap it (USB <-> Wi-Fi)
            if (s == null) return;
            try { s.Send(b, 0, len, SocketFlags.None); }
            catch (SocketException) { } // the TV app may be closed for a moment
            catch (ObjectDisposedException) { }
        }

        /// <summary>a new handshake: new ephemeral key and nonce (also after the receiver restarted)</summary>
        void NewHello()
        {
            RemoteInput.ReleaseButtons(); // the receiver went quiet: nothing it pressed stays down
            SecureLink.Session old = link;
            link = null;
            linkReady = false;
            if (old != null) old.Dispose();
            if (eph != null) eph.Dispose();
            eph = new CngKeyHolder(SecureLink.NewKey(false));
            myNonce = SecureLink.RandomNonce();
            helloPkt = SecureLink.MakeHello(myNonce, SecureLink.PointOf(eph.Key), SecureLink.PointOf(SecureLink.Identity()), Environment.MachineName);
            helloSince = Clock.Ms();
            lastHello = 0;
        }

        /// <summary>the receiver's HELLO_OK: keys; straight on if both know each other, else the code</summary>
        void OnHelloOk(byte[] b, int n)
        {
            SecureLink.Hello h = SecureLink.Parse(b, n);
            if (h == null || h.Echo == null || link != null || !SecureLink.Same(h.Echo, myNonce)) return;
            SecureLink.Session l;
            try
            {
                l = SecureLink.Derive(true, SecureLink.Identity(), eph.Key, myNonce, h.Nonce,
                    SecureLink.PointOf(eph.Key), h.Ephemeral, SecureLink.PointOf(SecureLink.Identity()), h.Static);
            }
            catch (System.Security.Cryptography.CryptographicException) { return; } // a point off the curve
            peerStatic = h.Static;
            PeerName = h.Name;
            lastSealedHeard = Clock.Ms();
            link = l;
            if (SecureLink.Trusted(h.Static) && h.KnowsYou)
            {
                LinkStatus = "";
                linkReady = true;
            }
            else
            {
                PairAnswer = 0;
                PairCode = l.CodeText; // the window asks the user to compare it with the receiver's
                LinkStatus = string.Format(Texts.PairWaiting, h.Name.Length > 0 ? h.Name : tv.ToString());
            }
        }

        void SendFrame(byte[] data, int len, bool key, long pts)
        {
            long t0;
            if (inputAt.TryGetValue(pts, out t0))
            {
                inputAt.Remove(pts);
                Interlocked.Add(ref EncNs, Clock.Nanos() - t0);
                Interlocked.Increment(ref EncN);
            }
            if (inputAt.Count > 100) inputAt.Clear();
            if (key && FirstKeyHex.Length == 0)
            {
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < Math.Min(len, 64); i++) sb.Append(data[i].ToString("x2"));
                FirstKeyHex = sb.ToString();
            }
            if (web != null)
            {
                web.Video(data, len, key, pts);
                Interlocked.Increment(ref Frames);
                Interlocked.Add(ref Bytes, len);
                return;
            }
            int count = Proto.VideoChunks(len);
            if (count < 0) return;
            byte[] pkt = new byte[Proto.VIDEO_HEADER + Proto.MAX_PAYLOAD];
            lock (sendLock)
            {
                int id = frameId++;
                for (int c = 0; c < count; c++)
                {
                    int o = c * Proto.MAX_PAYLOAD, l = Math.Min(Proto.MAX_PAYLOAD, len - o);
                    Proto.PutVideoHeader(pkt, key, id, c, count, pts);
                    Buffer.BlockCopy(data, o, pkt, Proto.VIDEO_HEADER, l);
                    Send(pkt, Proto.VIDEO_HEADER + l);
                }
            }
            Interlocked.Increment(ref Frames);
            Interlocked.Add(ref Bytes, len);
        }

        void VideoLoop()
        {
            Native.CoInitializeEx(IntPtr.Zero, 0); // MTA
            Native.MFStartup(0x20070, 0);
            Native.timeBeginPeriod(1);
            while (on)
            {
                try { RunVideo(); }
                catch (Exception e)
                {
                    Status = e.Message;
                    if (e is WrongGpuException) { WrongGpuNvidia = ((WrongGpuException)e).NvidiaDrivesScreen; WrongGpu = true; }
                    Thread.Sleep(500); // screen mode change, UAC prompt, ...: start over
                }
            }
        }

        /// <summary>--test-pattern: moving bars at 60 fps through the same encoder and outputs</summary>
        void RunPattern()
        {
            int w = 1280, h = 720;
            byte[] nv12 = new byte[w * h * 3 / 2];
            int fps = Fps;
            using (Encoder enc = new Encoder(w, h, fps, Bitrate, web != null ? 66 : 100))
            {
                EncoderName = enc.Name + " (test pattern)";
                vw = w; vh = h;
                if (web != null) web.SetSize(w, h);
                keyWanted = true;
                Status = "";
                long next = Clock.Micros(), n = 0;
                while (on && !restartVideo)
                {
                    enc.Pump(SendFrame);
                    long now = Clock.Micros();
                    if (now < next || !enc.CanTakeInput) { Thread.Sleep(1); continue; }
                    next += 1000000 / fps;
                    int x0 = (int)(n++ * 8 % w);
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                            nv12[y * w + x] = (byte)(((x + x0) / 80 % 2 == 0) ? 200 : 40);
                    for (int i = w * h; i < nv12.Length; i++) nv12[i] = 128;
                    bool key = keyWanted;
                    keyWanted = false;
                    enc.Input(nv12, now, key);
                    enc.Pump(SendFrame);
                }
            }
        }

        void RunVideo()
        {
            restartVideo = false;
            if (TestPattern) { RunPattern(); return; }
            using (Capture cap = new Capture())
            {
                RemoteInput.SetTarget(cap.OriginX, cap.OriginY, cap.W, cap.H); // touches land on this screen
                int maxW, maxH;
                SizeLimit(web != null, Usb, RecvW, RecvH, out maxW, out maxH);
                double s = Math.Min(1.0, Math.Min((double)maxW / cap.W, (double)maxH / cap.H));
                int w = (int)(cap.W * s) & ~1, h = (int)(cap.H * s) & ~1;
                byte[] nv12 = new byte[w * h * 3 / 2];
                Pointer ptr = new Pointer();
                bool toPc = web == null && RecvHz > 0;
                int fps = toPc ? PcFps(RecvHz, DisplayHz.Of(cap.ScreenDevice)) : Fps;
                int rate = web != null ? Bitrate : toPc ? PcBitrate(fps, w, h) : ScaledBitrate(Bitrate, w, h, Usb);
                using (Encoder enc = new Encoder(w, h, fps, rate, web != null ? 66 : 100))
                {
                    EncoderName = enc.Name + (enc.Hardware ? "" : Texts.Software);
                    vw = w;
                    vh = h;
                    if (web != null) web.SetSize(w, h);
                    keyWanted = true;
                    Status = "";
                    bool dirty = false, have = false;
                    long nextSlot = 0, lastEncode = 0, pts = 0;
                    while (on && !restartVideo)
                    {
                        int got = cap.Acquire(2);
                        if (got == 1) { dirty = true; have = true; pts = cap.LastPresentMicros; }
                        if (ptr.Update(cap.OriginX, cap.OriginY) && have) { dirty = true; pts = Clock.Micros(); }
                        enc.Pump(SendFrame);
                        long now = Clock.Micros();
                        // a still screen sends nothing; when the TV needs a keyframe, encode the last picture again
                        bool keyIdle = keyWanted && have && now - lastEncode > 100000;
                        // To the iPad, a still screen still gets 10 frames a second (tiny "nothing
                        // changed" frames): with 5-6 fps Safari's player ran out of picture and stopped
                        // for up to 10 s, waiting for enough to play.
                        bool webIdle = web != null && have && now - lastEncode > 100000;
                        if (((dirty && now >= nextSlot) || keyIdle || webIdle) && enc.CanTakeInput)
                        {
                            long c0 = Clock.Nanos();
                            Nv12.Convert(cap.Bgra, cap.W, cap.H, nv12, w, h, ptr);
                            long c1 = Clock.Nanos();
                            Interlocked.Add(ref ConvNs, c1 - c0);
                            Interlocked.Increment(ref ConvN);
                            if (!dirty) pts = Clock.Micros();
                            bool key = keyWanted;
                            keyWanted = false;
                            inputAt[pts] = c1;
                            enc.Input(nv12, pts, key);
                            dirty = false;
                            lastEncode = now;
                            // even spacing: running late skips a slot instead of sending two frames
                            // close together (a burst leaves the TV decoder one frame behind for seconds)
                            nextSlot += 1000000 / fps;
                            if (nextSlot < now) nextSlot = now + 1000000 / fps;
                            enc.Pump(SendFrame);
                        }
                    }
                }
            }
        }

        void ControlLoop()
        {
            byte[] raw = new byte[2048];
            byte[] inb = new byte[2048]; // the opened (inner) packet
            NewHello();
            byte[] cfg = new byte[Proto.CONFIG_LONG];
            Buffer.BlockCopy(token, 0, cfg, 15, Proto.TOKEN_BYTES);
            byte[] pong = new byte[17];
            long lastCfg = 0, lastKey = 0;
            Interlocked.Exchange(ref lastHeard, Clock.Ms());
            bool moved = false;
            if (!string.IsNullOrEmpty(TvName))
                new Thread(RouteLoop) { IsBackground = true, Name = "route" }.Start();
            while (on)
            {
                long now = Clock.Ms();
                IPAddress other = Interlocked.Exchange(ref moveTo, null);
                if (other != null)
                {
                    try
                    {
                        // a new socket: .NET refuses to connect one again once its network
                        // went away with the cable ("can only reconnect asynchronously")
                        Socket old = sock;
                        sock = NewSocket(other);
                        old.Close(); // a Send still on it gets ObjectDisposedException (caught)
                        tv = other;
                        bool usb = WebCast.IsUsbAddress(other);
                        if (usb != Usb) { Usb = usb; restartVideo = true; } // other bitrate and size
                        keyWanted = true;
                        lastCfg = 0; // CONFIG at once: the receiver follows it (same token)
                        // an older receiver takes the new address only after 3 s of silence: wait
                        // longer than that before calling it quiet again (no USB <-> Wi-Fi ping-pong)
                        Interlocked.Exchange(ref lastHeard, now + 4000);
                        moved = true;
                        Interlocked.Increment(ref Moves);
                        Status = string.Format(Texts.Moved, usb ? Texts.UsbCable.Trim('(', ')') : "Wi-Fi");
                    }
                    catch (Exception e) { Status = e.Message; }
                }
                // the encrypted link: HELLO until answered; the code while the user compares it
                if (link == null && now - lastHello >= 1000)
                {
                    lastHello = now;
                    SendRaw(helloPkt, helloPkt.Length);
                    if (now - helloSince > 5000) LinkStatus = Texts.OldReceiver; // an old receiver ignores HELLO
                }
                else if (link != null && !linkReady)
                {
                    if (PairAnswer == 1)
                    {
                        SecureLink.Trust(peerStatic, PeerName);
                        PairCode = null;
                        LinkStatus = "";
                        linkReady = true;
                        lastCfg = 0;
                        keyWanted = true;
                    }
                    else if (PairAnswer == -1) { PairCode = null; PairRefused = true; }
                    else if (now - lastPairShow >= 1000)
                    {
                        lastPairShow = now;
                        SendSealed(new byte[] { SecureLink.PAIR_SHOW }, 1);
                    }
                }
                else if (linkReady && now - lastSealedHeard > 4000)
                {
                    NewHello(); // the receiver went quiet (restarted?): shake hands again, it knows us
                }
                if (linkReady && now - lastCfg >= 1000 && vw > 0)
                {
                    lastCfg = now;
                    cfg[0] = Proto.CONFIG;
                    Proto.PutInt(cfg, 1, vw);
                    Proto.PutInt(cfg, 5, vh);
                    Proto.PutInt(cfg, 9, audioRate > 0 ? audioRate : 48000);
                    cfg[13] = 2;
                    cfg[14] = (byte)((AllowControl ? Proto.CONFIG_CONTROL : 0) | SecureLink.CONFIG_PAIRED);
                    Send(cfg, cfg.Length);
                }
                int n;
                try { n = sock.Receive(raw); }
                catch (SocketException e) { if (e.SocketErrorCode != SocketError.TimedOut) RecvError = e.SocketErrorCode.ToString(); continue; }
                catch (ObjectDisposedException) { return; }
                if (n > 0 && raw[0] == SecureLink.HELLO_OK) { OnHelloOk(raw, n); continue; }
                // everything else from the receiver is sealed with this cast's keys
                SecureLink.Session sl = link;
                n = n > 0 && raw[0] == SecureLink.SEALED && sl != null ? sl.Open(raw, n, inb) : -1;
                if (n < 1) continue;
                lastSealedHeard = now;
                if (n > 0)
                {
                    Interlocked.Increment(ref Heard);
                    Interlocked.Exchange(ref lastHeard, now);
                    if (moved && n >= 9 && inb[0] == Proto.PING) { moved = false; Status = ""; } // it shows again
                }
                if (n >= 1 && inb[0] == Proto.KEYREQ && now - lastKey > 150)
                {
                    lastKey = now;
                    keyWanted = true;
                }
                else if (n >= 2 && inb[0] == Proto.INPUT)
                {
                    // touches from a phone that shows this screen
                    byte[] m = AllowControl && linkReady ? InputMessage(inb, n, token) : null;
                    InputEvent e = m == null ? null : RemoteInput.Decode(m, m.Length);
                    // raw keys (shortcuts) only from a PC receiver: it reported its refresh rate
                    // in INFO, a TV or phone never does
                    if (e != null && (e.Kind != InputKind.KeyRaw || RecvHz > 0)) RemoteInput.Apply(e);
                }
                else if (n >= 9 && inb[0] == Proto.INFO)
                {
                    int w = Proto.GetInt(inb, 1), h = Proto.GetInt(inb, 5);
                    if (w >= 320 && h >= 240 && w <= 7680 && h <= 4320 && (w != RecvW || h != RecvH))
                    {
                        RecvW = w; RecvH = h;
                        restartVideo = true; // the encoder starts again at the receiver's size
                    }
                    // a PC receiver adds its screen's refresh rate (TVs and phones do not)
                    int hz = n >= 13 ? Proto.GetInt(inb, 9) : 0;
                    if (hz >= 24 && hz <= 1000 && hz != RecvHz) { RecvHz = hz; restartVideo = true; }
                }
                else if (n >= 9 && inb[0] == Proto.PING)
                {
                    pong[0] = Proto.PONG;
                    Buffer.BlockCopy(inb, 1, pong, 1, 8);
                    Proto.PutLong(pong, 9, Clock.Nanos());
                    Send(pong, pong.Length);
                }
            }
        }

        // ---- sound: WASAPI loopback of the default output device ----

        static Guid CLSID_Enumerator = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        static Guid IID_Enumerator = new Guid("A95664D2-9614-4F35-A746-DE8DB63617E6");
        static Guid IID_AudioClient = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
        static Guid IID_CaptureClient = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
        static Guid KSDATAFORMAT_FLOAT = new Guid("00000003-0000-0010-8000-00aa00389b71");

        void AudioLoop()
        {
            Native.CoInitializeEx(IntPtr.Zero, 0);
            Native.timeBeginPeriod(1);
            while (on)
            {
                try { RunAudio(); }
                catch (Exception) { Thread.Sleep(1000); } // device changed (headphones, ...): reopen
            }
        }

        void RunAudio()
        {
            IntPtr en = IntPtr.Zero, dev = IntPtr.Zero, client = IntPtr.Zero, cap = IntPtr.Zero, fmt = IntPtr.Zero;
            try
            {
                Com.Check(Native.CoCreateInstance(ref CLSID_Enumerator, IntPtr.Zero, 0x17, ref IID_Enumerator, out en), "MMDeviceEnumerator");
                Com.Check(Com.F<D_DefEndpoint>(en, 4)(en, 0 /*render*/, 0 /*console*/, out dev), "GetDefaultAudioEndpoint");
                Com.Check(Com.F<D_DevActivate>(dev, 3)(dev, ref IID_AudioClient, 0x17, IntPtr.Zero, out client), "Activate");
                Com.Check(Com.F<D_Out>(client, 8)(client, out fmt), "GetMixFormat");
                int tag = (ushort)Marshal.ReadInt16(fmt, 0), ch = Marshal.ReadInt16(fmt, 2);
                int rate = Marshal.ReadInt32(fmt, 4), bits = Marshal.ReadInt16(fmt, 14);
                bool isFloat = tag == 3;
                if (tag == 0xFFFE)
                {
                    byte[] g = new byte[16];
                    Marshal.Copy(fmt + 24, g, 0, 16);
                    isFloat = new Guid(g) == KSDATAFORMAT_FLOAT;
                }
                if (rate < 8000 || rate > 48000 || (isFloat ? bits != 32 : bits != 16)) return; // unusual device: no sound
                Com.Check(Com.F<D_AcInit>(client, 3)(client, 0, 0x20000 /*LOOPBACK*/, 200000, 0, fmt, IntPtr.Zero), "Initialize");
                Com.Check(Com.F<D_GetService>(client, 14)(client, ref IID_CaptureClient, out cap), "GetService");
                audioRate = rate;
                if (web != null) web.SetAudioRate(rate);
                Com.Check(Com.F<D_Self>(client, 10)(client), "Start");

                int chunkFrames = rate / 200; // 5 ms
                byte[] pkt = new byte[5 + chunkFrames * 4];
                int filled = 0, seq = 0;
                while (on)
                {
                    Thread.Sleep(2);
                    uint next;
                    while (Com.F<D_CapNext>(cap, 5)(cap, out next) >= 0 && next > 0)
                    {
                        IntPtr data; uint frames, flags; ulong pos, qpc;
                        Com.Check(Com.F<D_CapGet>(cap, 3)(cap, out data, out frames, out flags, out pos, out qpc), "GetBuffer");
                        bool silent = (flags & 2) != 0;
                        for (int f = 0; f < frames; f++)
                        {
                            short l = 0, r = 0;
                            if (!silent)
                            {
                                if (isFloat)
                                {
                                    l = ToShort(ReadFloat(data, (f * ch) * 4));
                                    r = ch > 1 ? ToShort(ReadFloat(data, (f * ch + 1) * 4)) : l;
                                }
                                else
                                {
                                    l = Marshal.ReadInt16(data, (f * ch) * 2);
                                    r = ch > 1 ? Marshal.ReadInt16(data, (f * ch + 1) * 2) : l;
                                }
                            }
                            int o = 5 + filled * 4;
                            pkt[o] = (byte)l; pkt[o + 1] = (byte)(l >> 8);
                            pkt[o + 2] = (byte)r; pkt[o + 3] = (byte)(r >> 8);
                            if (++filled == chunkFrames)
                            {
                                pkt[0] = Proto.AUDIO;
                                Proto.PutInt(pkt, 1, seq++);
                                if (web != null) web.Audio(pkt, 5, pkt.Length - 5);
                                else Send(pkt, pkt.Length);
                                filled = 0;
                            }
                        }
                        Com.F<D_CapRel>(cap, 4)(cap, frames);
                    }
                }
                Com.F<D_Self>(client, 11)(client); // Stop
            }
            finally
            {
                if (fmt != IntPtr.Zero) Native.CoTaskMemFree(fmt);
                Com.Release(ref cap);
                Com.Release(ref client);
                Com.Release(ref dev);
                Com.Release(ref en);
            }
        }

        static float ReadFloat(IntPtr p, int offset)
        {
            return BitConverter.ToSingle(BitConverter.GetBytes(Marshal.ReadInt32(p, offset)), 0);
        }

        static short ToShort(float v)
        {
            int i = (int)(v * 32767f);
            return (short)(i > 32767 ? 32767 : i < -32768 ? -32768 : i);
        }
    }

    /// <summary>
    /// Laptop Wi-Fi saves power between packets and scans for other networks now and then,
    /// which showed as ping spikes up to 250 ms. Media streaming mode turns that off and
    /// background scans are paused; both last only while this handle is open.
    /// </summary>
    sealed class WifiStreamingMode : IDisposable
    {
        IntPtr handle;

        public WifiStreamingMode()
        {
            uint ver;
            if (Native.WlanOpenHandle(2, IntPtr.Zero, out ver, out handle) != 0) { handle = IntPtr.Zero; return; }
            IntPtr list;
            if (Native.WlanEnumInterfaces(handle, IntPtr.Zero, out list) != 0) return;
            int n = Marshal.ReadInt32(list, 0);
            IntPtr on = Marshal.AllocHGlobal(4), off = Marshal.AllocHGlobal(4);
            Marshal.WriteInt32(on, 1);
            Marshal.WriteInt32(off, 0);
            for (int i = 0; i < n; i++)
            {
                byte[] g = new byte[16];
                Marshal.Copy(list + 8 + i * 532, g, 0, 16);
                Guid iface = new Guid(g);
                Native.WlanSetInterface(handle, ref iface, 3 /*media_streaming_mode*/, 4, on, IntPtr.Zero);
                Native.WlanSetInterface(handle, ref iface, 2 /*background_scan_enabled*/, 4, off, IntPtr.Zero);
            }
            Marshal.FreeHGlobal(on);
            Marshal.FreeHGlobal(off);
            Native.WlanFreeMemory(list);
        }

        public void Dispose()
        {
            if (handle != IntPtr.Zero) Native.WlanCloseHandle(handle, IntPtr.Zero);
            handle = IntPtr.Zero;
        }
    }

    // ---- finding the TV: one mDNS question for _tvcast._udp.local ----------------------------

    static class Discovery
    {
        // addresses that answered as a PC receiver (PCNAME): the window lists those apart from TVs and phones
        static readonly HashSet<IPAddress> pcs = new HashSet<IPAddress>(), phones = new HashSet<IPAddress>();
        public static bool IsPhone(IPAddress a) { lock (phones) return a != null && phones.Contains(a); }

        /// <summary>this address answered as a PC receiver</summary>
        public static bool IsPc(IPAddress a) { lock (pcs) return a != null && pcs.Contains(a); }

        public static List<KeyValuePair<string, IPAddress>> Find(int ms)
        {
            List<KeyValuePair<string, IPAddress>> found = new List<KeyValuePair<string, IPAddress>>();
            List<IPAddress> own = WebCast.UpIPv4(false);
            using (Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                s.Bind(new IPEndPoint(IPAddress.Any, 0));
                s.ReceiveTimeout = 200;
                s.EnableBroadcast = true; // WHO to the home network for PC receivers
                MemoryStream q = new MemoryStream();
                q.Write(new byte[] { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 }, 0, 12);
                foreach (string label in new[] { "_tvcast", "_udp", "local" })
                {
                    byte[] b = Encoding.ASCII.GetBytes(label);
                    q.WriteByte((byte)b.Length);
                    q.Write(b, 0, b.Length);
                }
                q.Write(new byte[] { 0, 0, 12, 0x80, 1 }, 0, 5); // PTR, IN with "unicast reply" bit
                byte[] query = q.ToArray();
                IPEndPoint mdns = new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353);
                // Windows sends multicast only on the lowest-metric adapter, which is the phone's USB
                // tethering when a cable is in: ask on every network so both the cable and Wi-Fi answer
                List<IPAddress> nets = WebCast.UpIPv4(false);
                Action ask = delegate
                {
                    bool sent = false;
                    foreach (IPAddress a in nets)
                    {
                        try
                        {
                            s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, a.GetAddressBytes());
                            s.SendTo(query, mdns);
                            sent = true;
                        }
                        catch (SocketException) { }
                    }
                    if (!sent) try { s.SendTo(query, mdns); } catch (SocketException) { }
                    // Android's mDNS does not answer on USB tethering: ask the phone on the cable
                    // (the gateway of that network) itself
                    foreach (IPAddress gw in WebCast.UsbGateways())
                        try { s.SendTo(new[] { Proto.WHO }, new IPEndPoint(gw, Proto.PORT)); } catch (SocketException) { }
                    // a PC receiver (Receiver.cs) answers WHO too: ask the whole home network
                    foreach (IPAddress bc in LanBroadcasts())
                        try { s.SendTo(new[] { Proto.WHO }, new IPEndPoint(bc, Proto.PORT)); } catch (SocketException) { }
                };
                ask();
                long end = Clock.Ms() + ms, resend = Clock.Ms() + 400;
                byte[] buf = new byte[9000];
                while (Clock.Ms() < end)
                {
                    if (Clock.Ms() > resend) { ask(); resend = long.MaxValue; }
                    EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    int n;
                    try { n = s.ReceiveFrom(buf, ref from); }
                    catch (SocketException) { continue; }
                    IPEndPoint src = (IPEndPoint)from;
                    string name = src.Port == Proto.PORT ? NameReply(buf, n) : InstanceName(buf, n);
                    if (name == null) continue;
                    IPAddress ip = src.Address;
                    bool pc = src.Port == Proto.PORT && buf[0] == Proto.PCNAME;
                    if (pc && own.Contains(ip)) continue; // this PC's own receiver
                    if (pc) lock (pcs) pcs.Add(ip);
                    if (src.Port == Proto.PORT && buf[0] == Proto.PHONENAME) lock (phones) phones.Add(ip);
                    bool dup = false;
                    foreach (KeyValuePair<string, IPAddress> f in found) dup |= f.Value.Equals(ip);
                    if (!dup) found.Add(new KeyValuePair<string, IPAddress>(name, ip));
                }
            }
            return found;
        }

        /// <summary>the broadcast address of every home network this PC is on (private IPv4)</summary>
        static List<IPAddress> LanBroadcasts()
        {
            List<IPAddress> list = new List<IPAddress>();
            try
            {
                foreach (System.Net.NetworkInformation.NetworkInterface ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    foreach (System.Net.NetworkInformation.UnicastIPAddressInformation u in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (u.Address.AddressFamily != AddressFamily.InterNetwork || u.IPv4Mask == null) continue;
                        IPAddress bc = Broadcast(u.Address, u.IPv4Mask);
                        if (bc != null && Proto.IsLanV4(u.Address.GetAddressBytes()) && !list.Contains(bc)) list.Add(bc);
                    }
                }
            }
            catch (System.Net.NetworkInformation.NetworkInformationException) { }
            return list;
        }

        /// <summary>192.168.0.20 / 255.255.255.0 -> 192.168.0.255 (unit-tested); null for a /32</summary>
        internal static IPAddress Broadcast(IPAddress a, IPAddress mask)
        {
            byte[] b = a.GetAddressBytes(), m = mask.GetAddressBytes();
            if (b.Length != 4 || m.Length != 4 || (m[0] & m[1] & m[2] & m[3]) == 255) return null;
            for (int i = 0; i < 4; i++) b[i] = (byte)(b[i] | ~m[i]);
            return new IPAddress(b);
        }

        /// <summary>a receiver's NAME answer to WHO, null if the packet is something else</summary>
        internal static string NameReply(byte[] b, int n)
        {
            if (n < 2 || (b[0] != Proto.NAME && b[0] != Proto.PCNAME && b[0] != Proto.PHONENAME)) return null;
            string name = Encoding.UTF8.GetString(b, 1, n - 1).Trim();
            return name.Length > 0 ? name : null;
        }

        // the first label of a PTR answer for our service type, null if the packet is something else
        internal static string InstanceName(byte[] b, int n)
        {
            try
            {
                int qd = (b[4] << 8) | b[5], an = (b[6] << 8) | b[7];
                int p = 12;
                for (int i = 0; i < qd; i++) { ReadName(b, ref p); p += 4; }
                for (int i = 0; i < an; i++)
                {
                    List<string> owner = ReadName(b, ref p);
                    int type = (b[p] << 8) | b[p + 1];
                    int rdlen = (b[p + 8] << 8) | b[p + 9];
                    p += 10;
                    if (type == 12 && owner.Count > 0 && owner[0] == "_tvcast")
                    {
                        int r = p;
                        List<string> target = ReadName(b, ref r);
                        if (target.Count > 0) return target[0];
                    }
                    p += rdlen;
                }
            }
            // a truncated or hostile packet: a label running past the end throws one of these
            // (found by tests/cs: TestDiscoverySurvivesTruncatedPackets)
            catch (IndexOutOfRangeException) { }
            catch (ArgumentException) { }
            return null;
        }

        static List<string> ReadName(byte[] b, ref int p)
        {
            List<string> labels = new List<string>();
            int pos = p, jumps = 0;
            bool jumped = false;
            while (true)
            {
                int len = b[pos];
                if (len == 0) { pos++; break; }
                if ((len & 0xC0) == 0xC0)
                {
                    int target = ((len & 0x3F) << 8) | b[pos + 1];
                    if (!jumped) p = pos + 2;
                    jumped = true;
                    pos = target;
                    if (++jumps > 20) break;
                    continue;
                }
                labels.Add(Encoding.UTF8.GetString(b, pos + 1, len));
                pos += 1 + len;
            }
            if (!jumped) p = pos;
            return labels;
        }
    }

    // ---- texts --------------------------------------------------------------------------------

    /// <summary>Every text the user can see, in English and Lithuanian. The Windows display
    /// language picks one (Lithuanian for "lt", English for everything else), like the
    /// Android apps; --lang en|lt overrides it. Format strings must keep the same {n} in both.</summary>
    static class Texts
    {
        public static string Title, TvLabel, Find, Sound, Fps120, Show, Stop, ShowingWord;
        public static string AppHint, WebHint, ScreensSection, ScreensHint;
        public static string Subtitle, TvSection, IpadSection, OpenSafari, CodeCaption, ScreenLabel, ScreenMain, UsbCable, Moved;
        public static string PairAsk, PairWaiting, PairRefused, OldReceiver, WindowsRefused;
        public static string Searching, Found, NotFound, Stopped, EnterIp, Showing, Software;
        public static string NoScreen, NoOutput1, ScreenFormat, NoEncoder, EncoderError;
        public static string ShowIpad, AllowControl, IpadOpen, IpadCode, IpadWaiting, IpadConnected, PortBusy;
        public static string WrongGpuAskNvidia, WrongGpuStuck;
        public static string NoDecoder, ReceiverPortBusy, ReceiveSection, ReceiveHint, PcPageTitle, ReceiveStart, ReceiveStop,
            ReceiveWaiting, ReceiveShowing, ReceiveCodeAsk, ReceiveAddress, ReceiveKeys, ReceiveOff, ReceivePackets,
            SendHeader, GetHeader, ShowPc, SendSub, GetSub, PcControl, PcSound, PcSearching, TvSearching, TypeAddress;
        public static string AddScreen, DisplaySettings, ExtendTitle, ExtendSteps, UsbInternetAsk, UsbInternetDone, WrongGpu, WrongGpuAsk, FastGpuAsk, ViewerSlow, ViewerLeft, CodeWrong, CodeLocked, CaPrint;

        static Texts() { Use(System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName); }

        public static void Use(string lang)
        {
            bool lt = lang == "lt";
            Title = lt ? "CastLink" : "CastLink";
            TvLabel = lt ? "Įrenginys (pavadinimas arba IP adresas)" : "Device (name or IP address)";
            Find = lt ? "Ieškoti" : "Find";
            Sound = lt ? "Siųsti garsą" : "Send sound";
            // the Sony X80J shows 60 Hz and decodes ~90 fps at most: there 120 only adds delay
            Fps120 = lt ? "120 kadr./s per Wi-Fi (120 Hz ekranams; USB laidu visada 120)"
                        : "120 frames/s over Wi-Fi (120 Hz screens; always 120 over USB)";
            Show = lt ? "Rodyti" : "Show";
            ShowIpad = lt ? "Rodyti naršyklėje" : "Show in a browser";
            AllowControl = lt ? "Leisti valdyti lietimu, pele ir klaviatūra" : "Allow control by touch, mouse and keyboard";
            IpadOpen = lt ? "Naršyklėje atidarykite: {0}" : "In the browser open: {0}";
            IpadCode = lt ? "Kodas: {0}" : "Code: {0}";
            IpadWaiting = lt ? "Laukiama naršyklės…" : "Waiting for a browser…";
            IpadConnected = lt ? "Prisijungė: {0}" : "Connected: {0}";
            WrongGpu = lt ? "Windows paleido programą kitoje vaizdo plokštėje nei ta, kuri rodo ekraną."
                          : "Windows runs this app on a different graphics card than the one driving the screen.";
            AddScreen = lt ? "Atsisiųsti tvarkyklę" : "Download the driver";
            DisplaySettings = lt ? "Windows ekrano nustatymai" : "Windows display settings";
            ExtendTitle = lt ? "Telefonas ar planšetė kaip antras ekranas" : "A phone or tablet as a second screen";
            ExtendSteps = lt
                ? "1.  Atsisiųskite ir įdiekite nemokamą „Virtual Display Driver“. Atidarykite „VDD Control“ ir spauskite „Install Driver“.\n2.  Paspauskite Win+P → „Išplėsti“. Ekrano nustatymuose naujam ekranui nustatykite įrenginio skiriamąją gebą (pvz., 2340×1080) ir mastelį 200 %.\n3.  Čia, sąraše „Rodomas ekranas“, pasirinkite naują ekraną ir rodykite kaip įprastai."
                : "1.  Download and install the free “Virtual Display Driver”. Open “VDD Control” and press “Install Driver”.\n2.  Press Win+P → “Extend”. In the display settings give the new screen the device's resolution (e.g. 2340×1080) and 200 % scale.\n3.  Here, pick the new screen under “Screen to show” and show it as usual.";
            UsbInternetAsk = lt
                ? "Telefonas prijungtas USB laidu. Windows per jį leidžia ir kompiuterio internetą, todėl gali būti naudojami telefono mobilieji duomenys (ir tada, kai Wi-Fi ar LAN dingsta).\n\nNaudoti laidą tik vaizdui ir niekada kompiuterio internetui? (Windows paprašys leidimo vieną kartą.)"
                : "The phone is on the USB cable. Windows also sends the PC's internet through it, so the phone's mobile data can be used (also when Wi-Fi or LAN go away).\n\nUse the cable only for the picture, never for the PC's internet? (Windows asks for permission once.)";
            UsbInternetDone = lt ? "Laidas – tik vaizdui, telefono internetas nenaudojamas." : "The cable is only for the picture, the phone's internet is not used.";
            FastGpuAsk = lt ? "Virtualų ekraną greičiau koduoja NVIDIA vaizdo plokštė (iki ~100 kadr./s vietoj ~75), bet dabar Windows programai nustatė „Energijos taupymas“.\n\nNustatyti programai „Didelis našumas“ ir paleisti ją iš naujo? (Grįžus prie pagrindinio ekrano programa pati pasiūlys grįžti.)"
                            : "The NVIDIA graphics card encodes the virtual screen faster (up to ~100 frames/s instead of ~75), but Windows now runs this app on “Power saving”.\n\nSet this app to “High performance” and restart it? (Back on the main screen the app offers to switch back.)";
            WrongGpuAsk = lt ? "Šiame nešiojamame kompiuteryje ekraną rodo integruota vaizdo plokštė, o Windows programą paleido kitoje, todėl ekrano nufilmuoti negalima.\n\nNustatyti programai „Energijos taupymas“ (tas pats kaip Parametrai → Ekranas → Grafika) ir paleisti ją iš naujo?"
                             : "On this laptop the screen is driven by the integrated graphics, but Windows started this app on the other card, so the screen cannot be captured.\n\nSet this app to “Power saving” (the same as Settings → Display → Graphics) and restart it?";
            WrongGpuAskNvidia = lt ? "Šį ekraną (pvz. monitorių per HDMI) rodo NVIDIA vaizdo plokštė, o Windows programą paleido integruotoje, todėl ekrano nufilmuoti negalima.\n\nNustatyti programai „Didelis našumas“ (tas pats kaip Parametrai → Ekranas → Grafika) ir paleisti ją iš naujo? (Grįžus prie nešiojamo ekrano programa pati pasiūlys grįžti.)"
                                   : "This screen (e.g. a monitor on HDMI) is driven by the NVIDIA graphics card, but Windows started this app on the integrated one, so the screen cannot be captured.\n\nSet this app to “High performance” (the same as Settings → Display → Graphics) and restart it? (Back on the laptop screen the app offers to switch back.)";
            WrongGpuStuck = lt ? "Ekrano nufilmuoti nepavyksta net su tinkama vaizdo plokšte. Parametrai → Ekranas → Grafika: CastLink nustatyk „Leisti Windows nuspręsti“ ir paleisk iš naujo."
                               : "The screen still cannot be captured on the right graphics card. Settings → Display → Graphics: set CastLink to “Let Windows decide” and restart it.";
            ViewerSlow = lt ? "Žiūrovas atsijungė: Wi-Fi nespėjo priimti vaizdo" : "The viewer disconnected: the Wi-Fi could not keep up";
            ViewerLeft = lt ? "Žiūrovas atsijungė" : "The viewer disconnected";
            CodeWrong = lt ? "Neteisingas kodas iš {0}" : "Wrong code from {0}";
            CodeLocked = lt ? "5 neteisingi kodai (paskutinis iš {0}): 5 min. užrakinta, kodas pakeistas" : "5 wrong codes (last from {0}): locked for 5 min, new code";
            CaPrint = lt ? "Sertifikato SHA-256 pradžia: {0}" : "Certificate SHA-256 starts with: {0}";
            PortBusy = lt ? "Prievadas {0} užimtas (gal programa jau atidaryta?)" : "Port {0} is busy (is the app already open?)";
            Stop = lt ? "Sustabdyti" : "Stop";
            Searching = lt ? "Ieškoma įrenginių… (juose turi būti atidaryta „CastLink“)"
                           : "Searching… (“CastLink” must be open on the device)";
            Found = lt ? "Rasta įrenginių: {0}. Spauskite „Rodyti“." : "Devices found: {0}. Press “Show”.";
            NotFound = lt ? "Nieko nerasta. Įrenginyje atidarykite „CastLink“ arba įrašykite jo IP adresą."
                          : "Nothing found. Open “CastLink” on the device or type its IP address.";
            Stopped = lt ? "Sustabdyta." : "Stopped.";
            EnterIp = lt ? "Įrašykite įrenginio IP adresą (jį rodo „CastLink“)."
                         : "Type the device's IP address (“CastLink” on it shows it).";
            ShowingWord = lt ? "Rodoma" : "Showing";
            Showing = ShowingWord + " · {0:0} " + (lt ? "kadr./s" : "frames/s") + " · {1:0.0} Mb/s\n{2}";
            Subtitle = lt ? "Kompiuterio ekranas telefone, televizoriuje ar naršyklėje" : "Your PC's screen on a phone, a TV or in a browser";
            TvSection = lt ? "CastLink programa" : "CastLink app";
            AppHint = lt ? "Android telefonas, planšetė ar televizorius su „CastLink“ programa. USB laidu – greičiausia ir ryškiausia."
                         : "An Android phone, tablet or TV with the “CastLink” app. A USB cable is the fastest and sharpest.";
            WebHint = lt ? "„Mac“, kitas kompiuteris ar iPad – be programos: naršyklėje atidarykite kompiuterio IP adresą."
                         : "A Mac, another PC or an iPad – no app: open this PC's IP address in the browser.";
            ScreensSection = lt ? "Ekranai" : "Screens";
            ScreensHint = lt ? "Pagrindinis ekranas rodomas kaip kopija. Virtualus ekranas paverčia telefoną ar planšetę atskiru, išplėstu ekranu."
                             : "The main screen is shown as a copy. A virtual screen makes the phone or tablet a separate, extended screen.";
            IpadSection = lt ? "Naršyklė" : "Browser";
            OpenSafari = lt ? "Įrenginio naršyklėje atidarykite" : "In the device's browser open";
            CodeCaption = lt ? "Kodas" : "Code";
            ScreenLabel = lt ? "Rodomas ekranas" : "Screen to show";
            UsbCable = lt ? "(USB laidas)" : "(USB cable)";
            Moved = lt ? "Ryšys dingo, jungiamasi per {0}…" : "Connection lost, switching to {0}…";
            PairAsk = lt ? "Pirmas kartas su „{0}“.\n\nAr jo ekrane rodomas šis kodas?\n\n{1}\n\nTaip – kodas sutampa (įrenginys bus įsimintas).\nNe – nesutampa: gal kažkas kitas bando prisijungti."
                : "First time with \u201c{0}\u201d.\n\nDoes its screen show this code?\n\n{1}\n\nYes: the code matches (the device will be remembered).\nNo: it does not, someone else may be trying to connect.";
            PairWaiting = lt ? "Palygink kodą „{0}“ ekrane" : "Compare the code on the screen of \u201c{0}\u201d";
            PairRefused = lt ? "Kodas nesutapo: rodymas sustabdytas" : "The code did not match: casting stopped";
            OldReceiver = lt ? "Imtuvas neatsako. Jei jame sena CastLink versija, atnaujink ją (ji nešifruoja)."
                : "The receiver does not answer. If it has an older CastLink, update it (that one does not encrypt).";
            WindowsRefused = lt ? "Windows neleido perkelti {0} lang. į pagrindinį ekraną (programos paleistos administratoriaus teisėmis). Perkelk jas Win+Shift+←."
                : "Windows did not let {0} window(s) move to the main screen (programs run as administrator). Move them with Win+Shift+←.";
            ScreenMain = lt ? "pagrindinis" : "main";
            Software = lt ? " (programinis)" : " (software)";
            NoScreen = lt ? "Nerastas ekranas" : "No screen found";
            NoOutput1 = lt ? "IDXGIOutput1 nepalaikomas" : "IDXGIOutput1 is not supported";
            ScreenFormat = lt ? "Ekrano formatas {0} nepalaikomas (išjunkite HDR)" : "Screen format {0} is not supported (turn HDR off)";
            NoEncoder = lt ? "Nerastas H.264 koderis" : "No H.264 encoder found";
            NoDecoder = lt ? "Nerastas H.264 dekoderis" : "No H.264 decoder found";
            ReceiverPortBusy = lt ? "Prievadas 47800 užimtas: gal CastLink jau priima kitame lange?" : "Port 47800 is busy: is CastLink already receiving in another window?";
            ReceiveSection = lt ? "Šis kompiuteris" : "This PC";
            ReceiveHint = lt ? "Pirmame spausk „Laukti ryšio“, antrame pasirink pirmąjį ir spausk „Rodyti šį ekraną“."
                             : "On the first press “Wait for a connection”, on the second pick the first and press “Show this screen”.";
            PcPageTitle = lt ? "Iš kompiuterio į kompiuterį" : "From PC to PC";
            ReceiveStart = lt ? "Laukti ryšio" : "Wait for a connection";
            ReceiveStop = lt ? "Nebelaukti" : "Stop receiving";
            ReceiveWaiting = lt ? "Laukiama ryšio\nAntrame kompiuteryje CastLink pasirink „{0}“ ir spausk „Rodyti šį ekraną“."
                                : "Waiting for a connection\nIn CastLink on the second PC pick “{0}” and press “Show this screen”.";
            ReceiveShowing = lt ? "Rodoma iš {0} · {1:0} kadr./s · {2}×{3}" : "Showing {0} · {1:0} frames/s · {2}×{3}";
            ReceiveCodeAsk = lt ? "Patvirtink kodą kitame kompiuteryje" : "Confirm the code on the other PC";
            ReceiveAddress = lt ? "Antrame kompiuteryje CastLink pasirink „{0}“." : "In CastLink on the second PC pick “{0}”.";
            ReceiveKeys = lt ? "Pelė ir klaviatūra valdo kitą kompiuterį (jei jame leista). Ctrl+Alt+Enter – langas."
                             : "Mouse and keyboard control the other PC (if it allows it). Ctrl+Alt+Enter: window.";
            ReceiveOff = lt ? "Šis kompiuteris ryšio nelaukia" : "This PC is not receiving";
            ReceivePackets = lt ? "gauta {0}, atmesta {1}" : "received {0}, refused {1}";
            SendHeader = lt ? "Antras kompiuteris" : "Second PC";
            GetHeader = lt ? "Pirmas kompiuteris" : "First PC";
            ShowPc = lt ? "Rodyti šį ekraną" : "Show this screen";
            TvSearching = lt ? "Ieškoma… Atidaryk CastLink telefone ar televizoriuje." : "Looking… Open CastLink on the phone or TV.";
            TypeAddress = lt ? "Įvesti pavadinimą ar adresą ranka" : "Type a name or address by hand";
            PcSearching = lt ? "Ieškoma… Pirmame kompiuteryje spausk „Laukti ryšio“." : "Looking… On the first PC press “Wait for a connection”.";
            SendSub = lt ? "Jo ekraną matysi pirmame" : "Its screen shows on the first";
            GetSub = lt ? "Prie jo sėdi ir matai antrą" : "You sit here and see the second";
            PcControl = lt ? "Leisti valdyti" : "Allow control";
            PcSound = lt ? "Leisti garsą" : "Allow sound";
            EncoderError = lt ? "Koderio klaida" : "Encoder error";
        }
    }

    // ---- window --------------------------------------------------------------------------------

    /// <summary>sizes for the hand-made layout: the app is DPI aware, so 96-dpi pixels are
    /// scaled here (fonts in points scale by themselves)</summary>
    static class Ui
    {
        public static float Scale = 1f;
        public static int S(int px) { return (int)Math.Round(px * Scale); }
        public static Point P(int x, int y) { return new Point(S(x), S(y)); }

        public static System.Drawing.Drawing2D.GraphicsPath Round(RectangleF r, float radius)
        {
            float d = radius * 2;
            System.Drawing.Drawing2D.GraphicsPath p = new System.Drawing.Drawing2D.GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // colours of the current Windows theme (Load), Windows 11 style
        public static bool Dark;
        public static Color Page, CardBg, Border, Ink, Muted, Accent, AccentText, Danger, Good, Waiting, Idle,
            Subtle, Field, Off, OffText;
        /// <summary>--theme light|dark (testing); null = follow Windows</summary>
        public static string Forced;

        public static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        static double Luma(Color c) { return (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255; }

        /// <summary>reads "Choose your app mode" (light / dark) and the accent colour from the
        /// registry, the same values Settings -> Personalization -> Colors writes</summary>
        public static void Load()
        {
            bool dark = false;
            Color accent = Color.Empty;
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    dark = v is int && (int)v == 0;
                }
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM"))
                {
                    object v = k == null ? null : k.GetValue("AccentColor");
                    if (v is int) { int c = (int)v; accent = Color.FromArgb(c & 0xFF, (c >> 8) & 0xFF, (c >> 16) & 0xFF); } // stored as ABGR
                }
            }
            catch (Exception) { } // no registry access: light theme, default blue
            Apply(Forced != null ? Forced == "dark" : dark, accent);
        }

        internal static void Apply(bool dark, Color accent)
        {
            Dark = dark;
            if (accent.IsEmpty) accent = Color.FromArgb(0, 103, 192);
            if (dark)
            {
                Page = Color.FromArgb(32, 32, 32); CardBg = Color.FromArgb(43, 43, 43); Border = Color.FromArgb(58, 58, 58);
                Ink = Color.White; Muted = Color.FromArgb(178, 178, 178); Field = Color.FromArgb(56, 56, 56);
                Subtle = Color.FromArgb(61, 61, 61); Off = Color.FromArgb(50, 50, 50); OffText = Color.FromArgb(120, 120, 120);
                // like Windows in dark mode: a lighter shade of the accent, dark text on it
                Accent = Luma(accent) < 0.55 ? Mix(accent, Color.White, 0.45f) : accent;
                Danger = Color.FromArgb(255, 153, 164); Good = Color.FromArgb(108, 203, 95); Waiting = Color.FromArgb(252, 200, 0);
                Idle = Color.FromArgb(130, 130, 130);
            }
            else
            {
                Page = Color.FromArgb(243, 243, 243); CardBg = Color.White; Border = Color.FromArgb(229, 229, 229);
                Ink = Color.FromArgb(27, 27, 27); Muted = Color.FromArgb(96, 96, 96); Field = Color.White;
                Subtle = Color.FromArgb(240, 240, 240); Off = Color.FromArgb(240, 240, 240); OffText = Color.FromArgb(160, 160, 160);
                // a pale accent would make white text unreadable: darken it
                Accent = Luma(accent) > 0.45 ? Mix(accent, Color.Black, 0.35f) : accent;
                Danger = Color.FromArgb(196, 43, 28); Good = Color.FromArgb(15, 123, 15); Waiting = Color.FromArgb(202, 128, 0);
                Idle = Color.FromArgb(160, 160, 160);
            }
            AccentText = Luma(Accent) > 0.5 ? Color.Black : Color.White;
        }

        static Ui() { Apply(false, Color.Empty); }
    }

    /// <summary>a Windows 11 style check box: the stock one stays light in a dark window</summary>
    sealed class FlatCheck : CheckBox
    {
        public FlatCheck()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AutoSize = false;
        }

        /// <summary>an icon (Icons.*) between the box and the text, or null</summary>
        public string Glyph;
        int TextX { get { return Ui.S(Glyph == null ? 28 : 54); } }

        public override Size GetPreferredSize(Size proposed)
        {
            Size t = TextRenderer.MeasureText(Text, Font);
            return new Size(TextX + t.Width, Math.Max(Ui.S(24), t.Height + Ui.S(4)));
        }

        protected override void OnTextChanged(EventArgs e) { Size = GetPreferredSize(Size.Empty); base.OnTextChanged(e); }
        protected override void OnFontChanged(EventArgs e) { Size = GetPreferredSize(Size.Empty); base.OnFontChanged(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Ui.CardBg);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float s = Ui.S(18), y = (Height - s) / 2f;
            RectangleF box = new RectangleF(1.5f, y, s, s);
            using (System.Drawing.Drawing2D.GraphicsPath p = Ui.Round(box, Ui.S(4)))
            {
                if (Checked)
                {
                    using (SolidBrush b = new SolidBrush(Enabled ? Ui.Accent : Ui.OffText)) g.FillPath(b, p);
                    using (Pen pen = new Pen(Enabled ? Ui.AccentText : Ui.Off, Ui.S(2)))
                        g.DrawLines(pen, new[] { new PointF(box.X + s * 0.24f, y + s * 0.52f), new PointF(box.X + s * 0.42f, y + s * 0.70f), new PointF(box.X + s * 0.76f, y + s * 0.32f) });
                }
                else
                    using (Pen pen = new Pen(Enabled ? Ui.Muted : Ui.OffText, 1.2f)) g.DrawPath(pen, p);
                if (Focused && ShowFocusCues)
                    using (System.Drawing.Drawing2D.GraphicsPath f = Ui.Round(new RectangleF(box.X - 2, box.Y - 2, s + 4, s + 4), Ui.S(5)))
                    using (Pen pen = new Pen(Ui.Ink, 1f)) g.DrawPath(pen, f);
            }
            if (Glyph != null)
                TextRenderer.DrawText(g, Glyph, Icons.Font(Font.SizeInPoints + 1f), new Rectangle(Ui.S(28), 0, Ui.S(22), Height),
                    Enabled ? ForeColor : Ui.OffText, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
            Rectangle text = new Rectangle(TextX, 0, Width - TextX, Height);
            TextRenderer.DrawText(g, Text, Font, text, Enabled ? ForeColor : Ui.OffText, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>a white card with rounded corners; its children get the white background</summary>
    sealed class Card : Panel
    {
        public Card()
        {
            BackColor = Ui.CardBg;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Ui.Page);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (System.Drawing.Drawing2D.GraphicsPath p = Ui.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Ui.S(8)))
            using (SolidBrush b = new SolidBrush(BackColor))
            using (Pen pen = new Pen(Ui.Border))
            {
                g.FillPath(b, p);
                g.DrawPath(pen, p);
            }
        }
    }

    /// <summary>a flat, rounded button (the stock one is square and grey)</summary>
    sealed class FlatButton : Button
    {
        public Color Fill = Ui.Accent, TextColor = Color.White;
        /// <summary>an icon (Icons.*) drawn left of the text, or null</summary>
        public string Glyph;
        bool hover, down;

        public FlatButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            Cursor = Cursors.Hand;
        }

        public void Style(Color fill, Color text) { Fill = fill; TextColor = text; Invalidate(); }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Ui.CardBg);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Color fill = !Enabled ? Ui.Off : down ? Ui.Mix(Fill, Ui.Dark ? Color.White : Color.Black, 0.15f) : hover ? Ui.Mix(Fill, Ui.Dark ? Color.Black : Color.White, 0.12f) : Fill;
            using (System.Drawing.Drawing2D.GraphicsPath p = Ui.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Ui.S(6)))
            using (SolidBrush b = new SolidBrush(fill))
                g.FillPath(b, p);
            if (Focused && ShowFocusCues && Enabled)
                using (System.Drawing.Drawing2D.GraphicsPath p = Ui.Round(new RectangleF(2.5f, 2.5f, Width - 5.5f, Height - 5.5f), Ui.S(4)))
                using (Pen pen = new Pen(Ui.Mix(fill, TextColor, 0.6f), 1.5f))
                    g.DrawPath(pen, p);
            Color fg = Enabled ? TextColor : Ui.OffText;
            if (Glyph == null)
            {
                TextRenderer.DrawText(g, Text, Font, ClientRectangle, fg, fill,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                return;
            }
            Font icon = Icons.Font(Font.SizeInPoints + 1f);
            Size gs = TextRenderer.MeasureText(g, Glyph, icon, Size.Empty, TextFormatFlags.NoPadding);
            Size ts = TextRenderer.MeasureText(g, Text, Font, Size.Empty, TextFormatFlags.NoPadding);
            int gap = Ui.S(8), total = gs.Width + gap + ts.Width, x = Math.Max(Ui.S(8), (Width - total) / 2);
            TextRenderer.DrawText(g, Glyph, icon, new Rectangle(x, 0, gs.Width, Height), fg, fill, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(x + gs.Width + gap, 0, Width - x - gs.Width - gap, Height), fg, fill,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>Icons from the Windows icon font (Segoe Fluent Icons on Windows 11, Segoe MDL2
    /// Assets on Windows 10): drawn as text, so they follow the theme colours and stay sharp at
    /// any scaling, with no image files.</summary>
    static class Icons
    {
        public const string Phone = "\uE8EA", Globe = "\uE774", Monitor = "\uE7F4", Settings = "\uE713",
            Play = "\uE768", Stop = "\uE71A", Search = "\uE721", Add = "\uE710",
            Volume = "\uE767", Touch = "\uE7C9", Speed = "\uE916", Lock = "\uE72E", Link = "\uE71B", Download = "\uE896", Upload = "\uE898",
            Pc = "\uE977", Edit = "\uE70F";
        static string family;
        static readonly Dictionary<float, Font> cache = new Dictionary<float, Font>();

        public static Font Font(float points)
        {
            if (family == null)
            {
                family = "Segoe MDL2 Assets";
                using (System.Drawing.Text.InstalledFontCollection all = new System.Drawing.Text.InstalledFontCollection())
                    foreach (FontFamily f in all.Families) if (f.Name == "Segoe Fluent Icons") { family = f.Name; break; }
            }
            Font font;
            if (!cache.TryGetValue(points, out font)) cache[points] = font = new Font(family, points);
            return font;
        }
    }

    /// <summary>one found device: icon tile, name and address, highlighted when picked</summary>
    sealed class DeviceRow : Control
    {
        public string Title = "", Sub = "", Glyph = Icons.Monitor;
        bool selected, hover;
        public bool Selected { get { return selected; } set { selected = value; Invalidate(); } }

        public DeviceRow()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Ui.Page);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Color fill = selected ? Ui.Mix(Ui.CardBg, Ui.Accent, 0.22f) : hover && Enabled ? Ui.Mix(Ui.CardBg, Ui.Ink, 0.06f) : Ui.CardBg;
            using (System.Drawing.Drawing2D.GraphicsPath p = Ui.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Ui.S(8)))
            using (SolidBrush b = new SolidBrush(fill))
            using (Pen pen = new Pen(selected ? Ui.Accent : Ui.Border, selected ? 1.5f : 1f))
            {
                g.FillPath(b, p);
                g.DrawPath(pen, p);
            }
            int t = Ui.S(30), x = Ui.S(8), y = (Height - t) / 2;
            using (System.Drawing.Drawing2D.GraphicsPath p = Ui.Round(new RectangleF(x, y, t, t), t * 0.26f))
            using (System.Drawing.Drawing2D.LinearGradientBrush b = new System.Drawing.Drawing2D.LinearGradientBrush(
                new PointF(x, y), new PointF(x + t, y + t), Color.FromArgb(56, 132, 255), Color.FromArgb(124, 77, 255)))
                g.FillPath(b, p);
            TextRenderer.DrawText(g, Glyph, Icons.Font(11f), new Rectangle(x, y, t, t), Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            int tx = x + t + Ui.S(10), tw = Width - tx - Ui.S(8);
            using (Font bold = new Font("Segoe UI Semibold", 9.5f))
                TextRenderer.DrawText(g, Title, bold, new Rectangle(tx, Ui.S(5), tw, Ui.S(19)), Enabled ? Ui.Ink : Ui.Muted,
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            using (Font small = new Font("Segoe UI", 8.5f))
                TextRenderer.DrawText(g, Sub, small, new Rectangle(tx, Ui.S(24), tw, Ui.S(16)), Ui.Muted,
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }
    }

    /// <summary>a rounded colour tile with a white icon, the badge of a card's title</summary>
    sealed class GlyphTile : Control
    {
        public string Glyph;
        public Color From, To;
        public Font GlyphFont; // null = the icon font

        public GlyphTile()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Ui.Page);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (System.Drawing.Drawing2D.GraphicsPath p = Ui.Round(new RectangleF(0, 0, Width - 1, Height - 1), Width * 0.26f))
            using (System.Drawing.Drawing2D.LinearGradientBrush b = new System.Drawing.Drawing2D.LinearGradientBrush(
                new PointF(0, 0), new PointF(Width, Height), From, To))
                g.FillPath(b, p);
            TextRenderer.DrawText(g, Glyph, GlyphFont ?? Icons.Font(13f), ClientRectangle, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    /// <summary>a row of the left menu: icon and name, a soft accent plate when selected</summary>
    sealed class NavItem : Control
    {
        public string Glyph;
        bool selected, hover;
        public bool Selected { get { return selected; } set { if (value != selected) { selected = value; Invalidate(); } } }

        public NavItem()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) { OnClick(EventArgs.Empty); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Color back = Parent != null ? Parent.BackColor : Ui.Page;
            g.Clear(back);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            if (selected || hover)
            {
                Color plate = selected ? Ui.Mix(back, Ui.Accent, Ui.Dark ? 0.28f : 0.14f) : Ui.Mix(back, Ui.Ink, 0.06f);
                using (System.Drawing.Drawing2D.GraphicsPath p = Ui.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Ui.S(6)))
                using (SolidBrush b = new SolidBrush(plate)) g.FillPath(b, p);
                if (selected)
                    using (System.Drawing.Drawing2D.GraphicsPath p = Ui.Round(new RectangleF(0, Height * 0.28f, Ui.S(3), Height * 0.44f), Ui.S(1)))
                    using (SolidBrush b = new SolidBrush(Ui.Accent)) g.FillPath(b, p);
            }
            Color fg = selected ? (Ui.Dark ? Color.White : Ui.Mix(Ui.Accent, Color.Black, 0.25f)) : Ui.Ink;
            TextRenderer.DrawText(g, Glyph, Icons.Font(12f), new Rectangle(Ui.S(12), 0, Ui.S(24), Height), fg,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(Ui.S(44), 0, Width - Ui.S(48), Height), fg,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            if (Focused && ShowFocusCues)
                using (System.Drawing.Drawing2D.GraphicsPath p = Ui.Round(new RectangleF(1.5f, 1.5f, Width - 3.5f, Height - 3.5f), Ui.S(5)))
                using (Pen pen = new Pen(Ui.Ink, 1f)) g.DrawPath(pen, p);
        }
    }

    /// <summary>the coloured state dot: green = running, amber = waiting, red = problem</summary>
    sealed class Dot : Control
    {
        Color color = Ui.Idle;
        public Dot() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true); }
        public Color Color { get { return color; } set { if (value != color) { color = value; Invalidate(); } } }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent != null ? Parent.BackColor : Ui.CardBg);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (SolidBrush b = new SolidBrush(color)) e.Graphics.FillEllipse(b, 0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        }
    }

    sealed class MainForm : Form
    {
        readonly ComboBox tvBox = new ComboBox(), screenBox = new ComboBox();
        Label screenLabel;
        string savedScreen = "";
        readonly FlatButton findBtn = new FlatButton(), goBtn = new FlatButton(), ipadBtn = new FlatButton();
        // the virtual screen: the driver's page and the Windows display settings
        readonly FlatButton addScreenBtn = new FlatButton(), displayBtn = new FlatButton();
        Label extendTitle, extendSteps;
        readonly FlatCheck soundBox = new FlatCheck(), fpsBox = new FlatCheck(), controlBox = new FlatCheck();
        // the browser page's own choices (sound and control can differ per way of showing)
        readonly FlatCheck webSoundBox = new FlatCheck(), webControlBox = new FlatCheck();
        // left menu and its four pages; only the selected page is visible
        Card appPage, webPage, screensPage, recvPage;
        // this PC as a receiver (Receiver.cs): the page's button, name and code, the picture window
        readonly FlatButton recvBtn = new FlatButton();
        readonly Label recvName = new Label(), recvCode = new Label(), recvKeys = new Label();
        // ... and the top half: send this screen to another PC (PCs found by the search are listed only here)
        readonly ComboBox pcBox = new ComboBox();
        readonly Panel pcList = new Panel(), tvList = new Panel();
        readonly FlatButton typeBtn = new FlatButton();
        readonly FlatButton pcFindBtn = new FlatButton(), pcGoBtn = new FlatButton();
        readonly FlatCheck pcSoundBox = new FlatCheck(), pcControlBox = new FlatCheck();
        readonly Label sendHeader = new Label(), getHeader = new Label(), sendDir = new Label(), getDir = new Label();
        readonly Card sendCard = new Card(), getCard = new Card();
        bool castToPc;
        PcReceiver receiver;
        ViewerForm viewer;
        int lastShown;
        long lastRecvTick;
        Card[] pages;
        NavItem[] nav;
        Label subtitle, tvLabel, appHint, webHint, screensHint, recvHint;
        WebCast web;
        string ipadCode;
        // the status card: the iPad's address and code (iPad mode only), then a dot with the state
        // line and grey details below. The player diagnostics ("iPad: video: ...") go to the log only.
        readonly Card statusCard = new Card();
        readonly Label urlCaption = new Label(), urlLabel = new Label(), codeCaption = new Label(), codeLabel = new Label(), printLabel = new Label();
        readonly Label stateLabel = new Label(), detailLabel = new Label();
        readonly Label urlIcon = new Label(), codeIcon = new Label();
        readonly Dot dot = new Dot();
        string ipadMode = "";
        int formWidth;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        // portable: settings live next to the exe; %APPDATA% only if that folder is read-only
        // (Program Files, a locked USB stick) or for settings saved by older versions
        static readonly string LocalSettings = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CastLink-settings.txt");
        static readonly string AppDataSettings = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TVCast", "settings.txt");
        // where versions up to 1.4 kept them; read when the new files do not exist yet
        // (and up to 1.4 of the bilingual app: TVCast-PC-settings.txt, before the CastLink name)
        static readonly string[] OldSettings = {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TVCast-PC-settings.txt"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RodytiTV-nustatymai.txt"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RodytiTV", "nustatymai.txt") };

        /// <summary>the first settings file that exists, newest location first; null if none</summary>
        internal static string FindSettings(string local, string appData, string[] old)
        {
            if (File.Exists(local)) return local;
            if (File.Exists(appData)) return appData;
            foreach (string o in old) if (File.Exists(o)) return o;
            return null;
        }
        Caster caster;
        long lastFrames, lastBytes, lastTick;
        // command line, for testing: --tv <ip> --start --log <file> --find --fps N --size WxH --lang en|lt
        //   --ipad (start the iPad mode) --control on|off --port N --no-ask
        readonly string logFile;
        readonly bool autoStart, findOnly, autoIpad, autoReceive;
        readonly string argControl;
        bool argFps;
        int startPage;

        public MainForm(string[] args)
        {
            string argTv = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--tv" && i + 1 < args.Length) argTv = args[++i];
                else if (args[i] == "--log" && i + 1 < args.Length) { logFile = args[++i]; RtcGlue.LogFile = logFile; }
                else if (args[i] == "--start") autoStart = true;
                else if (args[i] == "--ipad") autoIpad = true;
                else if (args[i] == "--no-ask") noAsk = true; // tests: never pop up the GPU question
                else if (args[i] == "--theme" && i + 1 < args.Length) Ui.Forced = args[++i]; // tests: light | dark
                else if (args[i] == "--test-pattern") Caster.TestPattern = true;
                else if (args[i] == "--drop" && i + 1 < args.Length) RtcGlue.DropEvery = int.Parse(args[++i]);
                else if (args[i] == "--port" && i + 1 < args.Length) WebCast.PORT = int.Parse(args[++i]);
                else if (args[i] == "--control" && i + 1 < args.Length) argControl = args[++i];
                else if (args[i] == "--lang" && i + 1 < args.Length) Texts.Use(args[++i]);
                else if (args[i] == "--find") findOnly = true;
                else if (args[i] == "--receive") autoReceive = true; // tests: start as a receiver
                else if (args[i] == "--page" && i + 1 < args.Length) startPage = int.Parse(args[++i]); // screenshots
                else if (args[i] == "--fps" && i + 1 < args.Length) { Caster.FPS = int.Parse(args[++i]); argFps = true; }
                else if (args[i] == "--size" && i + 1 < args.Length)
                {
                    string[] wh = args[++i].Split('x');
                    Caster.MaxW = int.Parse(wh[0]);
                    Caster.MaxH = int.Parse(wh[1]);
                }
            }
            Text = Texts.Title;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            Font = new Font("Segoe UI", 10f);
            Ui.Load();
            // laid out by hand in 96-dpi pixels times Ui.Scale (no AutoScale: the status card
            // changes its height at run time)
            AutoScaleMode = AutoScaleMode.None;
            using (Graphics g = CreateGraphics()) Ui.Scale = g.DpiX / 96f;
            const int W = 760, M = 16; // window width, margin

            // the icon embedded by build.ps1 (all sizes, so the title bar and taskbar stay sharp)
            try
            {
                using (Stream ico = typeof(MainForm).Assembly.GetManifestResourceStream("TVCast.app.ico"))
                    if (ico != null) Icon = new Icon(ico);
            }
            catch (Exception) { }
            // left: logo and the menu; right: the selected page; bottom right: the status card
            const int H = 480, SB = 200, PX = SB, PW = W - SB - M, PH = H - 2 * M - 84;
            PictureBox logo = new PictureBox { Image = AppIcon.Render(Ui.S(36), false), Location = Ui.P(16, 18), Size = new Size(Ui.S(36), Ui.S(36)) };
            Label title = new Label { Text = "CastLink", Font = new Font("Segoe UI Semibold", 14f), Location = Ui.P(60, 20), AutoSize = true };
            subtitle = new Label { Text = Texts.Subtitle, Location = Ui.P(16, 64), Size = new Size(Ui.S(SB - 28), Ui.S(40)), Font = new Font("Segoe UI", 8.5f) };
            string[] names = { Texts.TvSection, Texts.IpadSection, Texts.ScreensSection, Texts.ReceiveSection };
            string[] glyphs = { Icons.Phone, Icons.Globe, Icons.Monitor, Icons.Download };
            nav = new NavItem[4];
            for (int n = 0; n < 4; n++)
            {
                int k = n;
                nav[n] = new NavItem { Text = names[n], Glyph = glyphs[n], Size = new Size(Ui.S(SB - 24), Ui.S(38)) };
                nav[n].Location = Ui.P(12, 116 + n * 42);
                nav[n].Click += delegate { ShowPage(k); };
            }

            appPage = Page(Texts.TvSection, PX, PW, PH, out appHint, Texts.AppHint);
            webPage = Page(Texts.IpadSection, PX, PW, PH, out webHint, Texts.WebHint);
            screensPage = Page(Texts.ScreensSection, PX, PW, PH, out screensHint, Texts.ScreensHint);
            recvPage = Page(Texts.PcPageTitle, PX, PW, PH, out recvHint, Texts.ReceiveHint);
            recvHint.Height = Ui.S(22); // one line: which PC sends and which receives
            pages = new[] { appPage, webPage, screensPage, recvPage };

            // CastLink app (Android phone, tablet or TV)
            // found phones and TVs as tiles in two columns; the pencil swaps the tiles for a box
            // to type a name or address by hand
            tvLabel = new Label { Text = Texts.TvLabel, Location = Ui.P(16, 100), AutoSize = true, Visible = false };
            tvBox.Location = Ui.P(16, 104);
            tvBox.Width = Ui.S(PW - 32);
            tvBox.Visible = false;
            tvList.SetBounds(Ui.S(16), Ui.S(100), Ui.S(PW - 32), Ui.S(100));
            tvList.AutoScroll = true;
            tvBox.SelectedIndexChanged += delegate { ShowTvRows(); };
            findBtn.Text = "";
            findBtn.Glyph = Icons.Search;
            findBtn.Location = Ui.P(16, PH - 16 - 42);
            findBtn.Size = new Size(Ui.S(42), Ui.S(42));
            findBtn.Click += delegate { Search(); };
            new ToolTip().SetToolTip(findBtn, Texts.Find);
            typeBtn.Text = "";
            typeBtn.Glyph = Icons.Edit;
            typeBtn.Location = Ui.P(16 + 50, PH - 16 - 42);
            typeBtn.Size = findBtn.Size;
            typeBtn.Click += delegate { TypeByHand(!tvBox.Visible); };
            new ToolTip().SetToolTip(typeBtn, Texts.TypeAddress);
            fpsBox.Text = Texts.Fps120;
            fpsBox.Location = Ui.P(16, 208);
            fpsBox.Glyph = Icons.Speed;
            goBtn.Text = Texts.Show;
            goBtn.Glyph = Icons.Play;
            goBtn.Font = new Font("Segoe UI Semibold", 10.5f);
            goBtn.Location = Ui.P(16 + 100, PH - 16 - 42);
            goBtn.Size = new Size(Ui.S(PW - 32 - 100), Ui.S(42));
            goBtn.Click += delegate { Toggle(); };
            soundBox.Text = webSoundBox.Text = Texts.Sound;
            controlBox.Text = webControlBox.Text = Texts.AllowControl;
            soundBox.Glyph = webSoundBox.Glyph = Icons.Volume;
            controlBox.Glyph = webControlBox.Glyph = Icons.Touch;
            soundBox.Checked = controlBox.Checked = webSoundBox.Checked = webControlBox.Checked = true;
            soundBox.Location = Ui.P(16, 236);
            controlBox.Location = Ui.P(16, 264);
            // the browser page: at the bottom, under the address and the code
            webSoundBox.Location = Ui.P(16, PH - 16 - 42 - 80);
            webControlBox.Location = Ui.P(16, PH - 16 - 42 - 44);
            controlBox.CheckedChanged += delegate { if (caster != null && web == null) caster.AllowControl = controlBox.Checked; };
            webControlBox.CheckedChanged += delegate { if (web != null) web.AllowControl = webControlBox.Checked; };
            appPage.Controls.AddRange(new Control[] { tvLabel, tvBox, tvList, findBtn, typeBtn, fpsBox, soundBox, controlBox, goBtn });

            // browser (Mac, another PC, iPad): address and code while it runs
            urlCaption.Text = Texts.OpenSafari;
            codeCaption.Text = Texts.CodeCaption;
            foreach (Label l in new[] { urlCaption, codeCaption, printLabel }) l.AutoSize = false;
            printLabel.Font = new Font("Segoe UI", 8.5f);
            urlLabel.Font = new Font("Segoe UI Semibold", 12f);
            urlLabel.AutoSize = false;
            codeLabel.Font = new Font("Segoe UI Semibold", 22f);
            codeLabel.AutoSize = true;
            ipadBtn.Text = Texts.ShowIpad;
            ipadBtn.Glyph = Icons.Play;
            ipadBtn.Font = goBtn.Font;
            ipadBtn.Location = Ui.P(16, PH - 16 - 42);
            ipadBtn.Size = new Size(Ui.S(PW - 32), Ui.S(42));
            ipadBtn.Click += delegate { ToggleIpad(); };
            foreach (Label l in new[] { urlIcon, codeIcon }) { l.Font = Icons.Font(10f); l.AutoSize = false; l.TextAlign = ContentAlignment.MiddleCenter; captionIcons.Add(l); }
            urlIcon.Text = Icons.Link;
            codeIcon.Text = Icons.Lock;
            webPage.Controls.AddRange(new Control[] { urlIcon, urlCaption, urlLabel, codeIcon, codeCaption, codeLabel, printLabel, webSoundBox, webControlBox, ipadBtn });

            // screens: which monitor, and how to add a virtual one (the phone or tablet as a
            // second screen)
            screenLabel = new Label { Text = Texts.ScreenLabel, Location = Ui.P(16, 116), AutoSize = true };
            screenBox.DropDownStyle = ComboBoxStyle.DropDownList;
            screenBox.Location = Ui.P(16, 140);
            screenBox.Width = Ui.S(PW - 32);
            screenBox.DropDown += delegate { FillScreens(); }; // a monitor added meanwhile shows up
            screenBox.SelectionChangeCommitted += delegate { SaveAll(); };
            // how to add a virtual screen, always in view: Windows lets no app create a monitor
            // without a display driver, so the user installs a free one
            extendTitle = new Label { Text = Texts.ExtendTitle, Font = new Font("Segoe UI Semibold", 10.5f), Location = Ui.P(16, 188), AutoSize = true };
            extendSteps = new Label { Text = Texts.ExtendSteps, Location = Ui.P(16, 214), Size = new Size(Ui.S(PW - 32), Ui.S(PH - 214 - 16 - 36 - 10)) };
            addScreenBtn.Text = Texts.AddScreen;
            addScreenBtn.Glyph = Icons.Download;
            addScreenBtn.Location = Ui.P(16, PH - 16 - 36);
            addScreenBtn.Size = new Size(Ui.S((PW - 32 - 12) / 2), Ui.S(36));
            addScreenBtn.Click += delegate { Open(VirtualDisplayUrl); };
            displayBtn.Text = Texts.DisplaySettings;
            displayBtn.Glyph = Icons.Monitor;
            displayBtn.Location = Ui.P(16 + (PW - 32 - 12) / 2 + 12, PH - 16 - 36);
            displayBtn.Size = addScreenBtn.Size;
            displayBtn.Click += delegate { Open("ms-settings:display"); };
            screensPage.Controls.AddRange(new Control[] { CaptionIcon(Icons.Monitor, screenLabel), screenLabel, screenBox,
                extendTitle, extendSteps, addScreenBtn, displayBtn });

            // PC to PC: two cards side by side, left = the first PC (you sit at it, it receives),
            // right = the second PC (its screen is shown on the first)
            const int CW = (PW - 32 - 12) / 2, CY = 76, CH = PH - CY - 16, CI = CW - 28;
            Font headerFont = new Font("Segoe UI Semibold", 12.5f), dirFont = new Font("Segoe UI", 9f);
            sendCard.Location = Ui.P(16 + CW + 12, CY);
            sendCard.Size = new Size(Ui.S(CW), Ui.S(CH));
            getCard.Location = Ui.P(16, CY);
            getCard.Size = sendCard.Size;
            sendHeader.Text = Texts.SendHeader;
            sendHeader.Font = headerFont;
            sendHeader.Location = Ui.P(62, 12);
            sendHeader.AutoSize = true;
            sendDir.Font = dirFont;
            sendDir.AutoEllipsis = true;
            sendDir.SetBounds(Ui.S(62), Ui.S(36), Ui.S(CI - 48), Ui.S(20));
            // found PCs as tiles (icon, name, address) like in the phone app; the hidden box
            // keeps the picked entry for the cast code
            pcBox.Visible = false;
            pcList.SetBounds(Ui.S(14), Ui.S(64), Ui.S(CI), Ui.S(94));
            pcList.AutoScroll = true;
            pcBox.SelectedIndexChanged += delegate { ShowPcRows(); };
            pcFindBtn.Text = "";
            pcFindBtn.Glyph = Icons.Search;
            pcFindBtn.Location = Ui.P(14, CH - 14 - 38);
            pcFindBtn.Size = new Size(Ui.S(38), Ui.S(38));
            pcFindBtn.Click += delegate { Search(); };
            new ToolTip().SetToolTip(pcFindBtn, Texts.Find);
            pcSoundBox.Text = Texts.PcSound;
            pcSoundBox.Glyph = Icons.Volume;
            pcSoundBox.Location = Ui.P(14, 164);
            pcControlBox.Text = Texts.PcControl;
            pcControlBox.Glyph = Icons.Touch;
            pcControlBox.Location = Ui.P(14, 190);
            pcSoundBox.Checked = pcControlBox.Checked = true;
            pcControlBox.CheckedChanged += delegate { if (caster != null && castToPc) caster.AllowControl = pcControlBox.Checked; };
            pcBox.TextChanged += delegate { ShowDirections(); };
            pcGoBtn.Text = Texts.ShowPc;
            pcGoBtn.Glyph = Icons.Play;
            pcGoBtn.Font = goBtn.Font;
            pcGoBtn.Location = Ui.P(14 + 46, CH - 14 - 38);
            pcGoBtn.Size = new Size(Ui.S(CI - 46), Ui.S(38));
            pcGoBtn.Click += delegate { Toggle(true); };
            GlyphTile sendTile = new GlyphTile { Glyph = "2", From = Color.FromArgb(56, 132, 255), To = Color.FromArgb(124, 77, 255) };
            sendTile.GlyphFont = new Font("Segoe UI Semibold", 15f);
            sendTile.SetBounds(Ui.S(14), Ui.S(14), Ui.S(40), Ui.S(40));
            GlyphTile getTile = new GlyphTile { Glyph = "1", From = Color.FromArgb(0, 170, 140), To = Color.FromArgb(40, 130, 230) };
            getTile.GlyphFont = sendTile.GlyphFont;
            getTile.SetBounds(Ui.S(14), Ui.S(14), Ui.S(40), Ui.S(40));
            sendCard.Controls.AddRange(new Control[] { sendTile, sendHeader, sendDir, pcBox, pcList, pcFindBtn, pcSoundBox, pcControlBox, pcGoBtn });
            getHeader.Text = Texts.GetHeader;
            getHeader.Font = headerFont;
            getHeader.Location = Ui.P(62, 12);
            getHeader.AutoSize = true;
            getDir.Font = dirFont;
            getDir.AutoEllipsis = true;
            getDir.SetBounds(Ui.S(62), Ui.S(36), Ui.S(CI - 48), Ui.S(20));
            recvName.Text = string.Format(Texts.ReceiveAddress, Environment.MachineName);
            recvName.SetBounds(Ui.S(14), Ui.S(68), Ui.S(CI), Ui.S(40));
            recvCode.SetBounds(Ui.S(14), Ui.S(104), Ui.S(CI), Ui.S(40));
            recvCode.TextAlign = ContentAlignment.MiddleLeft;
            recvCode.Font = new Font("Segoe UI Semibold", 20f);
            recvKeys.Text = Texts.ReceiveKeys;
            recvKeys.Font = new Font("Segoe UI", 8.5f);
            recvKeys.SetBounds(Ui.S(14), Ui.S(146), Ui.S(CI), Ui.S(48));
            recvBtn.Text = Texts.ReceiveStart;
            recvBtn.Glyph = Icons.Play;
            recvBtn.Font = goBtn.Font;
            recvBtn.Location = Ui.P(14, CH - 14 - 38);
            recvBtn.Size = new Size(Ui.S(CI), Ui.S(38));
            recvBtn.Click += delegate { ToggleReceive(); };
            getCard.Controls.AddRange(new Control[] { getTile, getHeader, getDir, recvName, recvCode, recvKeys, recvBtn });
            recvPage.Controls.AddRange(new Control[] { sendCard, getCard });
            ShowPcRows();
            ShowDirections();


            // status card: a dot with the state line, grey details below
            statusCard.SetBounds(Ui.S(PX), Ui.S(M + PH + 12), Ui.S(PW), Ui.S(72));
            stateLabel.Font = new Font("Segoe UI Semibold", 10f);
            stateLabel.AutoSize = false;
            stateLabel.AutoEllipsis = true;
            detailLabel.AutoSize = false;
            detailLabel.AutoEllipsis = true;
            dot.Size = new Size(Ui.S(10), Ui.S(10));
            statusCard.Controls.AddRange(new Control[] { dot, stateLabel, detailLabel });

            Controls.AddRange(new Control[] { logo, title, subtitle });
            Controls.AddRange(nav);
            Controls.AddRange(pages);
            Controls.Add(statusCard);
            formWidth = Ui.S(W);
            ClientSize = new Size(formWidth, Ui.S(H));
            ShowPage(Math.Max(0, Math.Min(3, startPage)));
            LayoutStatus();
            ApplyTheme();
            // follow Settings -> Personalization -> Colors while the app is open
            Microsoft.Win32.UserPreferenceChangedEventHandler themeChanged = delegate(object s, Microsoft.Win32.UserPreferenceChangedEventArgs e)
            {
                if (e.Category == Microsoft.Win32.UserPreferenceCategory.General && IsHandleCreated)
                    BeginInvoke((MethodInvoker)delegate { Ui.Load(); ApplyTheme(); });
            };
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += themeChanged;
            FormClosed += delegate { Microsoft.Win32.SystemEvents.UserPreferenceChanged -= themeChanged; };

            try
            {
                string found = FindSettings(LocalSettings, AppDataSettings, OldSettings);
                string[] lines = found != null ? File.ReadAllLines(found) : new string[0];
                // line 1 (the device) is not read back: the field starts empty every time, like the
                // phone app (the user picks from a fresh search, no old addresses)
                if (lines.Length > 1) soundBox.Checked = lines[1] != "0";
                if (lines.Length > 2) fpsBox.Checked = lines[2] == "120";
                if (lines.Length > 3) controlBox.Checked = lines[3] != "0";
                if (lines.Length > 4) ipadCode = lines[4];
                if (lines.Length > 5) savedScreen = lines[5];
                webSoundBox.Checked = lines.Length > 6 ? lines[6] != "0" : soundBox.Checked;
                webControlBox.Checked = lines.Length > 7 ? lines[7] != "0" : controlBox.Checked;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            FillScreens(); // after the settings: the saved screen is selected
            if (argTv != null) tvBox.Text = argTv;
            if (argFps) fpsBox.Checked = Caster.FPS >= 120; // --fps wins over the saved choice
            if (argControl != null) controlBox.Checked = webControlBox.Checked = argControl != "off";

            timer.Interval = 1000;
            timer.Tick += delegate { Tick(); };
            // a window opened from the phone / TV / iPad (its taskbar, Start) goes to the shown screen
            IntPtr fgHook = WindowMover.WatchForeground(OnForeground);
            IntPtr clickHook = WindowMover.WatchRealClicks(); // ... and one the PC's own mouse picks comes to it
            FormClosed += delegate { WindowMover.Unwatch(fgHook); WindowMover.UnwatchClicks(clickHook); };
            timer.Start();
            Shown += delegate { if (autoReceive) { ShowPage(3); ToggleReceive(); } else if (autoIpad) ToggleIpad(); else if (autoStart) Toggle(); else Search(findOnly); };
            FormClosing += delegate { MoveWindowsHome(); StopCast(); StopReceive(); };
        }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);

        /// <summary>a running cast's button is red ("Stop"), the others use the accent colour</summary>
        void StyleButtons()
        {
            foreach (Control c in pcList.Controls) c.Enabled = !(caster != null && castToPc); // no other pick while casting to a PC
            foreach (Control c in tvList.Controls) c.Enabled = !(caster != null && !castToPc);
            typeBtn.Enabled = caster == null;
            findBtn.Style(Ui.Subtle, Ui.Ink);
            addScreenBtn.Style(Ui.Subtle, Ui.Ink);
            displayBtn.Style(Ui.Subtle, Ui.Ink);
            pcFindBtn.Style(Ui.Subtle, Ui.Ink);
            bool tv = caster != null && web == null && !castToPc, pc = caster != null && web == null && castToPc, ipad = web != null, rx = receiver != null;
            goBtn.Glyph = tv ? Icons.Stop : Icons.Play;
            ipadBtn.Glyph = ipad ? Icons.Stop : Icons.Play;
            pcGoBtn.Glyph = pc ? Icons.Stop : Icons.Play;
            recvBtn.Glyph = rx ? Icons.Stop : Icons.Play;
            Color stopInk = Ui.Dark ? Color.Black : Color.White;
            goBtn.Style(tv ? Ui.Danger : Ui.Accent, tv ? stopInk : Ui.AccentText);
            ipadBtn.Style(ipad ? Ui.Danger : Ui.Accent, ipad ? stopInk : Ui.AccentText);
            pcGoBtn.Style(pc ? Ui.Danger : Ui.Accent, pc ? stopInk : Ui.AccentText);
            recvBtn.Style(rx ? Ui.Danger : Ui.Accent, rx ? stopInk : Ui.AccentText);
        }

        /// <summary>colours of every control from Ui (after Ui.Load), dark title bar and combo box</summary>
        void ApplyTheme()
        {
            BackColor = Ui.Page;
            ForeColor = Ui.Ink;
            foreach (Card c in pages) c.BackColor = Ui.CardBg;
            statusCard.BackColor = Ui.CardBg;
            foreach (Control c in new Control[] { subtitle, tvLabel, fpsBox, urlCaption, codeCaption, printLabel, detailLabel,
                appHint, webHint, screensHint, screenLabel, extendSteps }) c.ForeColor = Ui.Muted;
            foreach (Control c in new Control[] { soundBox, controlBox, webSoundBox, webControlBox, codeLabel, stateLabel, extendTitle,
                pcSoundBox, pcControlBox, recvName, recvCode, sendHeader, getHeader }) c.ForeColor = Ui.Ink;
            foreach (Control c in new Control[] { sendDir, getDir, recvKeys }) c.ForeColor = Ui.Muted;
            sendCard.BackColor = getCard.BackColor = pcList.BackColor = Ui.Field;
            tvList.BackColor = Ui.CardBg;
            typeBtn.Style(Ui.Subtle, Ui.Ink);
            ShowPcRows();
            ShowTvRows();
            urlLabel.ForeColor = Ui.Accent;
            foreach (Label l in captionIcons) l.ForeColor = Ui.Muted;
            tvBox.BackColor = screenBox.BackColor = pcBox.BackColor = Ui.Field;
            tvBox.ForeColor = screenBox.ForeColor = pcBox.ForeColor = Ui.Ink;
            StyleButtons();
            if (IsHandleCreated) ThemeWindow();
            foreach (Control c in Controls) c.Invalidate(true);
            Invalidate(true);
        }

        void ThemeWindow()
        {
            int on = Ui.Dark ? 1 : 0;
            // DWMWA_USE_IMMERSIVE_DARK_MODE: 20 on Windows 10 2004+ and 11, 19 before that
            try { if (DwmSetWindowAttribute(Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(Handle, 19, ref on, 4); }
            catch (DllNotFoundException) { }
            // the combo box's arrow and list follow the "DarkMode_CFD" theme
            try { foreach (ComboBox cb in new[] { tvBox, screenBox, pcBox }) SetWindowTheme(cb.Handle, Ui.Dark ? "DarkMode_CFD" : null, null); }
            catch (DllNotFoundException) { }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ThemeWindow();
        }

        readonly List<Label> captionIcons = new List<Label>();

        /// <summary>a small grey icon in front of a caption; the caption moves right to make room</summary>
        Label CaptionIcon(string glyph, Label caption)
        {
            Label icon = new Label { Text = glyph, Font = Icons.Font(10f), AutoSize = false, TextAlign = ContentAlignment.MiddleCenter };
            icon.SetBounds(caption.Left, caption.Top, Ui.S(18), Math.Max(Ui.S(20), caption.Height));
            caption.Left += Ui.S(24);
            captionIcons.Add(icon);
            return icon;
        }

        /// <summary>a page of the right side: a card with its title and a grey hint</summary>
        static Card Page(string title, int x, int w, int h, out Label hint, string hintText)
        {
            Card c = new Card { Location = Ui.P(x, 16), Size = new Size(Ui.S(w), Ui.S(h)), Visible = false };
            c.Controls.Add(new Label { Text = title, Font = new Font("Segoe UI Semibold", 13f), Location = Ui.P(16, 14), AutoSize = true });
            hint = new Label { Text = hintText, Location = Ui.P(16, 48), Size = new Size(Ui.S(w - 32), Ui.S(44)) };
            c.Controls.Add(hint);
            return c;
        }

        /// <summary>shows one page; a running cast keeps running on every page</summary>
        void ShowPage(int n)
        {
            for (int i = 0; i < pages.Length; i++) { pages[i].Visible = i == n; nav[i].Selected = i == n; }
            if (n == 2) FillScreens();
            if (caster == null && receiver == null && !searching
                && ((n == 3 && pcBox.Items.Count == 0) || (n == 0 && tvBox.Items.Count == 0 && !tvBox.Visible))) Search(false, true);
        }

        /// <summary>first line = the state next to the dot, the rest = grey details</summary>
        void Say(string text, Color state)
        {
            int nl = text.IndexOf('\n');
            stateLabel.Text = nl < 0 ? text : text.Substring(0, nl);
            detailLabel.Text = nl < 0 ? "" : text.Substring(nl + 1).Trim('\n');
            dot.Color = state;
            LayoutStatus();
        }

        /// <summary>the status card's two lines, and the browser page's address and code (shown
        /// only while the browser mode runs)</summary>
        void LayoutStatus()
        {
            int w = statusCard.Width - Ui.S(32), lineH = TextRenderer.MeasureText("Ag", stateLabel.Font).Height;
            int y = Ui.S(14);
            dot.Location = new Point(Ui.S(16), y + (lineH - dot.Height) / 2);
            stateLabel.SetBounds(Ui.S(34), y, w - Ui.S(18), lineH);
            detailLabel.SetBounds(Ui.S(16), y + lineH + Ui.S(6), w, statusCard.Height - y - lineH - Ui.S(12));

            bool on = web != null;
            foreach (Control c in new Control[] { urlIcon, urlCaption, urlLabel, codeIcon, codeCaption, codeLabel, printLabel }) c.Visible = on;
            webHint.Visible = !on;
            if (!on) return;
            int pw = webPage.Width - Ui.S(32);
            y = Ui.S(52);
            urlIcon.SetBounds(Ui.S(16), y, Ui.S(18), Ui.S(20));
            urlCaption.SetBounds(Ui.S(40), y, pw - Ui.S(24), Ui.S(20));
            y += Ui.S(20);
            int h = TextRenderer.MeasureText(urlLabel.Text.Length > 0 ? urlLabel.Text : "x", urlLabel.Font, new Size(pw, 1000), TextFormatFlags.WordBreak).Height;
            h = Math.Max(Ui.S(20), Math.Min(h, ipadBtn.Top - y - Ui.S(100))); // never over the button
            urlLabel.SetBounds(Ui.S(16), y, pw, h);
            y += h + Ui.S(10);
            codeIcon.SetBounds(Ui.S(16), y, Ui.S(18), Ui.S(20));
            codeCaption.SetBounds(Ui.S(40), y, Ui.S(140), Ui.S(20));
            codeLabel.Location = new Point(Ui.S(12), y + Ui.S(16));
            int px = Ui.S(16) + Math.Max(Ui.S(160), codeLabel.PreferredSize.Width + Ui.S(16));
            printLabel.SetBounds(px, y + Ui.S(22), webPage.Width - px - Ui.S(16), Ui.S(36));
        }

        /// <param name="pick">true when the user pressed Find: the first device found is picked;
        /// the search when the app opens only fills the list, the field stays empty</param>
        bool searching;
        int pcSearchWait;

        /// <summary>draws the found PCs as tiles; a click picks one, a double click shows this screen there</summary>
        void ShowPcRows()
        {
            ShowRows(pcBox, pcList, 1, Texts.PcSearching, caster != null && castToPc, delegate { Toggle(true); });
        }

        /// <summary>the same for phones and TVs on the CastLink app page, two per row</summary>
        void ShowTvRows()
        {
            ShowRows(tvBox, tvList, 2, Texts.TvSearching, caster != null && !castToPc, delegate { Toggle(); });
        }

        /// <summary>swaps the tiles for the old box to type a name or address</summary>
        void TypeByHand(bool on)
        {
            tvBox.Visible = on;
            tvList.Visible = !on;
            if (on) { tvBox.Focus(); tvBox.SelectAll(); }
            else ShowTvRows();
        }

        static string GlyphFor(string name, bool pc)
        {
            if (pc) return Icons.Pc;
            int open = name.LastIndexOf(" (", StringComparison.Ordinal);
            IPAddress ip;
            if (open > 0 && IPAddress.TryParse(name.Substring(open + 2).TrimEnd(')'), out ip) && Discovery.IsPhone(ip)) return Icons.Phone;
            string n = name.ToUpperInvariant();
            return n.StartsWith("SM-") || n.Contains("PHONE") || n.Contains("PIXEL") || n.Contains("TELEFON") || n.Contains("USB") ? Icons.Phone : Icons.Monitor;
        }

        void ShowRows(ComboBox box, Panel list, int columns, string emptyText, bool locked, MethodInvoker start)
        {
            list.SuspendLayout();
            Control[] old = new Control[list.Controls.Count];
            list.Controls.CopyTo(old, 0);
            foreach (Control c in old) c.Dispose();
            if (box.Items.Count == 0)
            {
                Label empty = new Label { Text = emptyText, ForeColor = Ui.Muted, AutoSize = false };
                empty.SetBounds(0, Ui.S(6), list.Width, Ui.S(60));
                list.Controls.Add(empty);
            }
            int rows = (box.Items.Count + columns - 1) / columns;
            int inner = list.Width - (rows * Ui.S(50) > list.Height ? SystemInformation.VerticalScrollBarWidth : 0);
            int gap = Ui.S(8), w = (inner - gap * (columns - 1)) / columns;
            for (int i = 0; i < box.Items.Count; i++)
            {
                string entry = box.Items[i].ToString();
                string name = Caster.NameOf(entry) ?? entry;
                int open = entry.LastIndexOf(" (", StringComparison.Ordinal);
                DeviceRow row = new DeviceRow
                {
                    Title = name,
                    Sub = open > 0 ? entry.Substring(open + 2).TrimEnd(')') : "",
                    Glyph = GlyphFor(entry, box == pcBox),
                    Selected = i == box.SelectedIndex,
                    Enabled = !locked
                };
                row.SetBounds((i % columns) * (w + gap), (i / columns) * Ui.S(50), w, Ui.S(44));
                int index = i;
                row.Click += delegate { if (box.SelectedIndex != index) box.SelectedIndex = index; };
                row.DoubleClick += delegate { if (caster == null && receiver == null) { box.SelectedIndex = index; start(); } };
                list.Controls.Add(row);
            }
            list.ResumeLayout();
        }

        void AutoFindPcs()
        {
            bool empty = recvPage.Visible ? pcBox.Items.Count == 0 : appPage.Visible && tvBox.Items.Count == 0 && !tvBox.Visible;
            if (caster != null || receiver != null || searching || !empty) { pcSearchWait = 0; return; }
            if (++pcSearchWait < 3) return;
            pcSearchWait = 0;
            Search(false, true);
        }

        /// <summary>quiet = a background look: the buttons and the status line stay as they are
        /// unless something new turns up</summary>
        void Search(bool pick = true, bool quiet = false)
        {
            searching = true;
            if (!quiet)
            {
                findBtn.Enabled = pcFindBtn.Enabled = false;
                Say(Texts.Searching, Ui.Waiting);
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<KeyValuePair<string, IPAddress>> found = Discovery.Find(1500);
                BeginInvoke((MethodInvoker)delegate
                {
                    searching = false;
                    if (quiet && found.Count == 0) return;
                    if (caster == null) findBtn.Enabled = pcFindBtn.Enabled = true;
                    // PCs go to the "This PC" page, TVs and phones to the CastLink app page
                    string prevPc = pcBox.Text, prevTv = tvBox.Text;
                    tvBox.Items.Clear();
                    pcBox.Items.Clear();
                    foreach (KeyValuePair<string, IPAddress> f in found)
                        (Discovery.IsPc(f.Value) ? pcBox : tvBox).Items.Add(f.Key + " (" + f.Value + ")");
                    if (tvBox.Items.Count > 0 && caster == null && !tvBox.Visible)
                    {
                        int keepTv = tvBox.Items.IndexOf(prevTv);
                        tvBox.SelectedIndex = keepTv >= 0 ? keepTv : 0;
                    }
                    // a PC that was found just now is picked by itself (the one picked before, if still there)
                    if (pcBox.Items.Count > 0 && !(caster != null && castToPc))
                    {
                        int keep = pcBox.Items.IndexOf(prevPc);
                        pcBox.SelectedIndex = keep >= 0 ? keep : 0;
                    }
                    ShowPcRows();
                    ShowTvRows();
                    if (caster == null)
                        Say(found.Count > 0 ? string.Format(Texts.Found, found.Count) : Texts.NotFound,
                            found.Count > 0 ? Ui.Good : Ui.Waiting);
                    if (findOnly)
                    {
                        if (logFile != null)
                        {
                            StringBuilder sb = new StringBuilder("found " + found.Count + ":");
                            foreach (KeyValuePair<string, IPAddress> f in found) sb.Append(" [" + f.Key + " " + f.Value + "]");
                            try { File.AppendAllText(logFile, sb + Environment.NewLine); }
                            catch (IOException) { }
                        }
                        Close();
                    }
                });
            });
        }

        static void SaveSettings(string[] lines)
        {
            try
            {
                File.WriteAllLines(LocalSettings, lines);
                return;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(AppDataSettings));
                File.WriteAllLines(AppDataSettings, lines);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        internal static IPAddress ParseTv(string text)
        {
            string t = text.Trim();
            int open = t.LastIndexOf('('), close = t.LastIndexOf(')');
            if (open >= 0 && close > open) t = t.Substring(open + 1, close - open - 1);
            IPAddress ip;
            if (IPAddress.TryParse(t, out ip)) return ip;
            try
            {
                foreach (IPAddress a in Dns.GetHostAddresses(t)) if (a.AddressFamily == AddressFamily.InterNetwork) return a;
            }
            catch (SocketException) { }
            return null;
        }

        /// <summary>Same as Settings -> Display -> Graphics -> this exe -> Power saving (a per-user
        /// value for this exe path only), then starts the app again with the same arguments.</summary>
        /// <summary>this exe's Windows graphics preference: 1 power saving, 2 high performance, 0 not set</summary>
        static int GpuPreference()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences"))
                {
                    string v = k == null ? null : k.GetValue(Application.ExecutablePath) as string;
                    return ParseGpuPreference(v);
                }
            }
            catch (Exception) { return 0; }
        }

        /// <summary>"GpuPreference=1;" -> 1 (unit-tested)</summary>
        internal static int ParseGpuPreference(string v)
        {
            if (string.IsNullOrEmpty(v)) return 0;
            int i = v.IndexOf("GpuPreference=", StringComparison.Ordinal);
            if (i < 0 || i + 14 >= v.Length) return 0;
            char c = v[i + 14];
            return c >= '0' && c <= '2' ? c - '0' : 0;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_AdapterAt(IntPtr self, uint i, out IntPtr o);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_Desc(IntPtr self, IntPtr desc);

        /// <summary>an NVIDIA card in this PC (DXGI adapter vendor 0x10DE)</summary>
        static bool HasNvidia()
        {
            Guid iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
            IntPtr factory;
            if (Native.CreateDXGIFactory1(ref iid, out factory) < 0) return false;
            IntPtr desc = Marshal.AllocHGlobal(320);
            try
            {
                for (uint a = 0; ; a++)
                {
                    IntPtr adapter;
                    if (Com.F<D_AdapterAt>(factory, 12)(factory, a, out adapter) < 0) return false; // EnumAdapters1
                    try
                    {
                        // IDXGIAdapter1::GetDesc1 (slot 10): VendorId at byte 256
                        if (Com.F<D_Desc>(adapter, 10)(adapter, desc) >= 0 && (uint)Marshal.ReadInt32(desc, 256) == 0x10DE) return true;
                    }
                    finally { Com.Release(ref adapter); }
                }
            }
            finally { Marshal.FreeHGlobal(desc); Com.Release(ref factory); }
        }

        bool askedFastGpu;

        /// <summary>A virtual (not the main) screen with the app on "Power saving": offer the NVIDIA
        /// card, which encodes it faster (the main screen of this hybrid laptop needs power saving,
        /// see AskGpuFix). Yes: set "High performance" and restart, starting the same way again.
        /// Returns true when the app restarts.</summary>
        bool AskFastGpu(string startFlag)
        {
            if (noAsk || askedFastGpu) return false;
            ScreenInfo s = screenBox.SelectedItem as ScreenInfo;
            // decided by the encoder actually running: with no preference set Windows may still
            // put the app on the integrated card
            string enc = caster != null ? caster.EncoderName ?? "" : "";
            if (s == null || s.Primary || enc.Length == 0 || enc.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            askedFastGpu = true; // from here on asked at most once per start
            if (GpuPreference() == 2 || !HasNvidia()) return false;
            if (MessageBox.Show(this, Texts.FastGpuAsk, Texts.Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return false;
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences"))
                    k.SetValue(Application.ExecutablePath, "GpuPreference=2;");
            }
            catch (Exception e) { Say(e.Message, Ui.Danger); return false; }
            SaveAll();
            StopCast();
            string[] args = Environment.GetCommandLineArgs();
            StringBuilder a = new StringBuilder();
            for (int i = 1; i < args.Length; i++)
                if (args[i] != "--start" && args[i] != "--ipad") a.Append(" \"").Append(args[i].Replace("\"", "")).Append('"');
            a.Append(' ').Append(startFlag);
            Process.Start(Application.ExecutablePath, a.ToString());
            Close();
            return true;
        }

        /// <summary>the Windows graphics preference that puts the app on the card that drives the
        /// screen: 2 "High performance" for the NVIDIA card (a monitor on a hybrid laptop's HDMI
        /// port), 1 "Power saving" for the integrated one (the laptop panel). Unit-tested.</summary>
        internal static int WantedGpuPreference(bool nvidiaDrivesScreen)
        {
            return nvidiaDrivesScreen ? 2 : 1;
        }

        void AskGpuFix(bool nvidiaDrivesScreen)
        {
            int want = WantedGpuPreference(nvidiaDrivesScreen);
            if (GpuPreference() == want)
            {
                // already set: restarting again would only bring the same question back
                Say(Texts.WrongGpuStuck, Ui.Danger);
                return;
            }
            if (MessageBox.Show(this, nvidiaDrivesScreen ? Texts.WrongGpuAskNvidia : Texts.WrongGpuAsk, Texts.Title,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences"))
                    k.SetValue(Application.ExecutablePath, "GpuPreference=" + want + ";");
            }
            catch (Exception e) { Say(e.Message, Ui.Danger); return; }
            StopCast();
            string[] args = Environment.GetCommandLineArgs();
            StringBuilder a = new StringBuilder();
            for (int i = 1; i < args.Length; i++) a.Append(" \"").Append(args[i].Replace("\"", "")).Append('"');
            Process.Start(Application.ExecutablePath, a.ToString());
            Close();
        }

        /// <summary>
        /// Windows opens a new window on the main screen (or where it was last), also when it was
        /// started from the shown screen's taskbar by the phone / TV / iPad: such a window is moved
        /// to the shown screen, once at once and again a moment later (some apps place themselves
        /// after they appear).
        /// </summary>
        void OnForeground(IntPtr hwnd)
        {
            if (caster == null || hwnd == IntPtr.Zero || hwnd == Handle) return;
            int[] t = RemoteInput.Target;
            System.Drawing.Rectangle shown = t[2] > 0 ? new System.Drawing.Rectangle(t[0], t[1], t[2], t[3]) : Screen.PrimaryScreen.Bounds;
            Point cur = Cursor.Position, realAt;
            int sinceReal = WindowMover.SinceRealClick(out realAt), sinceRemote = RemoteInput.SinceRemoteClick();
            // the window goes to whoever picked it: the phone / TV / iPad (its screen) or the PC's
            // own mouse (the screen it clicked on), whichever clicked last
            System.Drawing.Rectangle dest;
            string who;
            if (sinceRemote <= sinceReal && RemoteInput.JustClickedRemotely(4000, shown.Contains(cur))) { dest = shown; who = "remote click"; }
            else if (sinceReal <= 1500) { dest = Screen.FromPoint(realAt).Bounds; who = "PC mouse click"; }
            else return;
            string r = WindowMover.MoveToScreen(hwnd, dest.Left, dest.Top, dest.Width, dest.Height);
            if (r.EndsWith("already on the shown screen")) return; // nothing to do, nothing to log
            WindowLog("active window after a " + who + ", to " + dest + ": " + r);
            // a new window may get its title and its own position only a moment later
            foreach (int ms in new[] { 300, 1000 })
            {
                var again = new System.Windows.Forms.Timer { Interval = ms };
                again.Tick += delegate
                {
                    again.Stop();
                    again.Dispose();
                    WindowLog(" after " + again.Interval + " ms: " + WindowMover.MoveToScreen(hwnd, dest.Left, dest.Top, dest.Width, dest.Height));
                };
                again.Start();
            }
        }

        /// <summary>diagnostics for moving windows to the shown screen, only with --log (window
        /// titles can be private: an e-mail subject, a document name), into that log file</summary>
        void WindowLog(string line)
        {
            if (logFile == null) return;
            try { File.AppendAllText(logFile, DateTime.Now.ToString("HH:mm:ss.fff ") + "windows: " + line + Environment.NewLine); }
            catch (Exception) { }
        }

        /// <summary>casting from a second (virtual) screen ends: its windows go to the main screen,
        /// otherwise they stay on the now unseen monitor (it remains switched on in Windows)</summary>
        string homeRefused; // set by MoveWindowsHome, shown after "Stopped"
        // the second screen whose cast stopped: nobody sees it now, so while CastLink is open and
        // casts nothing, a window that lands there again (Chrome leaving full screen goes back to
        // where it was before, a program opening there) is sent home by Tick
        int[] homeGuard;

        void GuardHome()
        {
            try
            {
                WindowMover.HomeResult r = WindowMover.MoveHome(homeGuard[0], homeGuard[1], homeGuard[2], homeGuard[3]);
                foreach (string line in r.Log) WindowLog("came back to the unseen screen, sent home: " + line);
            }
            catch (Exception) { } // a window that went away meanwhile; tried again in a second
        }

        void MoveWindowsHome()
        {
            ScreenInfo s = screenBox.SelectedItem as ScreenInfo;
            if (caster == null || s == null || s.Primary) return;
            homeGuard = new[] { s.Left, s.Top, s.Width, s.Height };
            try
            {
                WindowMover.HomeResult r = WindowMover.MoveHome(s.Left, s.Top, s.Width, s.Height);
                foreach (string line in r.Log) WindowLog("back to the main screen: " + line);
                if (r.Refused > 0) homeRefused = string.Format(Texts.WindowsRefused, r.Refused);
            }
            catch (Exception e) { Say(e.Message, Ui.Danger); }
        }

        /// <summary>this PC as a receiver on / off (Receiver.cs)</summary>
        /// <summary>each card names both ends: "this PC -> picked PC" and "other PC -> this PC"</summary>
        void ShowDirections()
        {
            sendDir.Text = Texts.SendSub;
            getDir.Text = Texts.GetSub;
        }

        void ToggleReceive()
        {
            if (receiver != null) { StopReceive(); Say(Texts.ReceiveOff, Ui.Idle); return; }
            receiver = new PcReceiver();
            receiver.Start();
            if (receiver.Error != null) { Say(receiver.Error, Ui.Danger); receiver.Dispose(); receiver = null; return; }
            recvBtn.Text = Texts.ReceiveStop;
            StyleButtons();
            Say(string.Format(Texts.ReceiveWaiting, Environment.MachineName), Ui.Waiting);
        }

        void StopReceive()
        {
            CloseViewer();
            if (receiver != null) receiver.Dispose();
            receiver = null;
            recvCode.Text = "";
            recvBtn.Text = Texts.ReceiveStart;
            ShowDirections();
            StyleButtons();
        }

        void CloseViewer()
        {
            ViewerForm v = viewer;
            viewer = null;
            if (receiver != null) receiver.Draw = null;
            if (v != null && !v.IsDisposed) v.Close();
        }

        /// <summary>every second from Tick: the picture window while a cast arrives, the state line</summary>
        void ReceiveTick()
        {
            PcReceiver r = receiver;
            if (r == null) return;
            string code = r.PairCode;
            recvCode.Text = code ?? "";
            ShowDirections();
            if (r.Streaming && viewer == null)
            {
                viewer = new ViewerForm("CastLink · " + r.CasterName, r);
                ViewerForm v = viewer;
                v.FormClosed += delegate
                {
                    // the user closed the picture: this cast ends here (the other PC sees it stop)
                    if (viewer == v) { viewer = null; StopReceive(); Say(Texts.ReceiveOff, Ui.Idle); }
                };
                v.Show();
                v.Activate();
                r.Draw = v.ShowFrame;
            }
            else if (!r.Streaming && viewer != null) CloseViewer();
            long now = Clock.Ms();
            int shown = r.ShownFrames;
            double fps = (shown - lastShown) / Math.Max(0.5, (now - lastRecvTick) / 1000.0);
            lastShown = shown;
            lastRecvTick = now;
            if (r.Error != null) Say(r.Error, Ui.Danger);
            else if (code != null) Say(Texts.ReceiveCodeAsk + "\n" + code, Ui.Waiting);
            else if (r.Streaming) Say(string.Format(Texts.ReceiveShowing, r.CasterName, fps, r.Width, r.Height), Ui.Good);
            else
            {
                // what arrived so far, to tell a blocked network from a refused connection
                string[] wait = string.Format(Texts.ReceiveWaiting, Environment.MachineName).Split('\n');
                string seen = string.Format(Texts.ReceivePackets, r.Packets, r.Rejected);
                Say(wait[0] + " · " + seen + "\n" + (r.LastEvent.Length > 0 ? r.LastEvent : wait[1]), Ui.Waiting);
            }
            if (logFile != null && r.Streaming)
            {
                long n = Interlocked.Exchange(ref r.TimedFrames, 0);
                double k = 1000.0 / Stopwatch.Frequency / Math.Max(1, n);
                string line = string.Format("receive {0:0} fps {1}x{2} | decode {3:0.0} ms, convert {4:0.0} ms, draw {5:0.0} ms | lost {6}, {7:0.0} Mb/s",
                    fps, r.Width, r.Height, Interlocked.Exchange(ref r.DecodeTicks, 0) * k, Interlocked.Exchange(ref r.ConvertTicks, 0) * k,
                    Interlocked.Exchange(ref r.DrawTicks, 0) * k, r.Lost, Interlocked.Exchange(ref r.Bytes, 0) * 8 / 1e6);
                try { File.AppendAllText(logFile, DateTime.Now.ToString("HH:mm:ss ") + line + Environment.NewLine); } catch (IOException) { }
            }
        }

        void StopCast()
        {
            if (caster != null) caster.Stop();
            caster = null;
            if (web != null) web.Dispose();
            web = null;
            goBtn.Text = Texts.Show;
            ipadBtn.Text = Texts.ShowIpad;
            pcGoBtn.Text = Texts.ShowPc;
            castToPc = false;
            StyleButtons();
            ipadMode = "";
            findBtn.Enabled = pcFindBtn.Enabled = soundBox.Enabled = pcSoundBox.Enabled = webSoundBox.Enabled = fpsBox.Enabled
                = goBtn.Enabled = pcGoBtn.Enabled = ipadBtn.Enabled = screenBox.Enabled = true;
        }

        void SaveAll()
        {
            SaveSettings(new[] { "", soundBox.Checked ? "1" : "0", fpsBox.Checked ? "120" : "60", controlBox.Checked ? "1" : "0", ipadCode ?? "", SelectedScreen(),
                webSoundBox.Checked ? "1" : "0", webControlBox.Checked ? "1" : "0" });
        }

        /// <summary>the monitors for the screen box; the saved one (or the primary) selected</summary>
        void FillScreens()
        {
            string keep = screenBox.SelectedItem is ScreenInfo ? ((ScreenInfo)screenBox.SelectedItem).Key : savedScreen;
            List<ScreenInfo> all = TVCast.Capture.ListScreens();
            screenBox.Items.Clear();
            foreach (ScreenInfo s in all) screenBox.Items.Add(s);
            int pick = TVCast.Capture.PickScreen(all.ConvertAll(x => x.Key), all.ConvertAll(x => x.Primary), keep);
            if (pick >= 0) screenBox.SelectedIndex = pick;
        }

        /// <summary>the page of the open-source virtual display driver (the user installs it)</summary>
        internal const string VirtualDisplayUrl = "https://github.com/VirtualDrivers/Virtual-Display-Driver/releases";

        /// <summary>opens a page or a Settings screen; a problem shows in the status card</summary>
        void Open(string target)
        {
            try { Process.Start(target); }
            catch (Exception e) { Say(e.Message, Ui.Danger); }
        }

        string SelectedScreen()
        {
            return screenBox.SelectedItem is ScreenInfo ? ((ScreenInfo)screenBox.SelectedItem).Key : savedScreen;
        }

        /// <param name="toPc">from the "This PC" page: to another PC (its own box and checkboxes);
        /// otherwise the CastLink app page (TV, phone)</param>
        void Toggle(bool toPc = false)
        {
            if (caster != null)
            {
                MoveWindowsHome();
                StopCast();
                Say(Texts.Stopped, Ui.Idle);
                if (homeRefused != null) { Say(homeRefused, Ui.Danger); homeRefused = null; }
                return;
            }
            ComboBox box = toPc ? pcBox : tvBox;
            IPAddress ip = ParseTv(box.Text);
            if (ip == null)
            {
                Say(Texts.EnterIp, Ui.Danger);
                return;
            }
            SaveAll();
            TVCast.Capture.Screen = SelectedScreen(); // (Capture alone is the Form's mouse-capture flag)
            if (!argFps || fpsBox.Checked != (Caster.FPS >= 120)) Caster.FPS = fpsBox.Checked ? 120 : 60;
            castToPc = toPc;
            caster = new Caster(ip, (toPc ? pcSoundBox : soundBox).Checked);
            caster.TvName = Caster.NameOf(box.Text); // to find it again when the USB cable is pulled
            caster.AllowControl = (toPc ? pcControlBox : controlBox).Checked; // the device that shows this PC may control it
            caster.Start();
            FlatButton go = toPc ? pcGoBtn : goBtn;
            go.Text = Texts.Stop;
            go.Focus();
            StyleButtons();
            findBtn.Enabled = pcFindBtn.Enabled = soundBox.Enabled = pcSoundBox.Enabled = webSoundBox.Enabled = fpsBox.Enabled
                = ipadBtn.Enabled = screenBox.Enabled = false;
            (toPc ? goBtn : pcGoBtn).Enabled = false; // one cast at a time
            lastFrames = lastBytes = 0;
            lastTick = Clock.Ms();
        }

        /// <summary>the iPad mode: a small web server the iPad's Safari opens; 60 fps (Safari
        /// plays 60 Hz video), the 120 box only applies to the TV</summary>
        void ToggleIpad()
        {
            if (caster != null)
            {
                MoveWindowsHome();
                StopCast();
                Say(Texts.Stopped, Ui.Idle);
                if (homeRefused != null) { Say(homeRefused, Ui.Danger); homeRefused = null; }
                return;
            }
            SaveAll();
            web = new WebCast(ipadCode);
            ipadCode = web.Code;
            SaveAll();
            web.AllowControl = webControlBox.Checked;
            try { web.Start(); }
            catch (SocketException)
            {
                web = null;
                Say(string.Format(Texts.PortBusy, WebCast.PORT), Ui.Danger);
                return;
            }
            web.CodeChanged += delegate
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    if (web == null) return;
                    ipadCode = web.Code; // after 5 wrong codes: remember the new one
                    SaveAll();
                    Tick();
                });
            };
            try { caPrint = LocalTls.CaFingerprint(); }
            catch (Exception) { caPrint = "-"; } // no HTTPS on this PC; plain http still works
            Caster.FPS = 60;
            TVCast.Capture.Screen = SelectedScreen(); // (Capture alone is the Form's mouse-capture flag)
            caster = new Caster(web, webSoundBox.Checked);
            caster.Start();
            ipadBtn.Text = Texts.Stop;
            ipadBtn.Focus(); // not the TV box: its text would show selected
            StyleButtons();
            findBtn.Enabled = soundBox.Enabled = webSoundBox.Enabled = fpsBox.Enabled = goBtn.Enabled = screenBox.Enabled = false;
            lastFrames = lastBytes = 0;
            lastTick = Clock.Ms();
            Tick();
        }

        bool askedGpu, noAsk;
        string caPrint = "";

        readonly HashSet<string> askedUsb = new HashSet<string>(); // USB networks already asked about

        /// <summary>a phone on the USB cable (tethering): offer once to use it only for the picture,
        /// never for the PC's internet (UsbRoute)</summary>
        void CheckUsbInternet()
        {
            if (noAsk) return;
            KeyValuePair<int, string>? usb = UsbRoute.NotLimited();
            if (usb == null || !askedUsb.Add(usb.Value.Value)) return;
            if (MessageBox.Show(this, Texts.UsbInternetAsk, Texts.Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            string error;
            if (UsbRoute.LimitToPicture(usb.Value.Key, out error)) Say(Texts.UsbInternetDone, Ui.Good);
            else if (error != null) Say(error, Ui.Danger);
        }

        int usbCheck;

        bool askingPair;

        void Tick()
        {
            if (++usbCheck % 2 == 0) CheckUsbInternet(); // every 2 s
            if (caster == null && homeGuard != null) GuardHome();
            if (caster == null) { ReceiveTick(); AutoFindPcs(); return; }
            if (caster.WrongGpu && !askedGpu && !noAsk)
            {
                askedGpu = true;
                bool nvidia = caster.WrongGpuNvidia;
                BeginInvoke((MethodInvoker)delegate { AskGpuFix(nvidia); });
            }
            // a virtual screen on the integrated card's encoder: offer the faster NVIDIA one
            if (!askedFastGpu && !noAsk && !string.IsNullOrEmpty(caster.EncoderName))
            {
                string flag = web != null ? "--ipad" : "--start";
                BeginInvoke((MethodInvoker)delegate { AskFastGpu(flag); });
            }
            long now = Clock.Ms(), f = Interlocked.Read(ref caster.Frames), b = Interlocked.Read(ref caster.Bytes);
            double secs = Math.Max(1, now - lastTick) / 1000.0;
            string line = string.Format(Texts.Showing,
                (f - lastFrames) / secs, (b - lastBytes) * 8 / secs / 1e6, caster.EncoderName);
            if (caster.Status.Length > 0) line += "\n" + caster.Status;
            if (caster.LinkStatus.Length > 0) line = caster.LinkStatus;
            if (caster.PairRefused)
            {
                StopCast();
                Say(Texts.PairRefused, Ui.Danger);
                return;
            }
            string code = caster.PairCode;
            if (code != null && !askingPair)
            {
                askingPair = true;
                Caster c = caster;
                BeginInvoke((MethodInvoker)delegate
                {
                    string who = c.PeerName.Length > 0 ? c.PeerName : "TV";
                    DialogResult r = MessageBox.Show(this, string.Format(Texts.PairAsk, who, code), Texts.Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    c.PairAnswer = r == DialogResult.Yes ? 1 : -1;
                    askingPair = false;
                });
            }
            if (web != null)
            {
                // the iPad needs the address and the code; the rate only matters once it watches
                string viewer = web.Viewer, ev = web.LastEvent;
                System.Text.RegularExpressions.Match mode = System.Text.RegularExpressions.Regex.Match(ev, @"^iPad: video: mode=(\w+)");
                if (mode.Success) ipadMode = mode.Groups[1].Value == "webrtc" ? "WebRTC (UDP)" : "WebSocket (TCP)";
                if (viewer.Length == 0) ipadMode = "";
                string rate = line.Split('\n')[0];
                if (rate.StartsWith(Texts.ShowingWord + " · ")) rate = rate.Substring(Texts.ShowingWord.Length + 3);
                string state = viewer.Length > 0
                    ? string.Format(Texts.IpadConnected, viewer) + (ipadMode.Length > 0 ? " · " + ipadMode : "") + " · " + rate
                    : Texts.IpadWaiting;
                // the page's player reports ("iPad: ...") are diagnostics: log only
                string details = caster.Status;
                if (ev.Length > 0 && !ev.StartsWith("iPad:")) details += (details.Length > 0 ? "\n" : "") + ev;
                urlLabel.Text = WebCast.LocalUrl().Replace("  ·  ", "\n");
                codeLabel.Text = web.Code.Length == 6 ? web.Code.Substring(0, 3) + " " + web.Code.Substring(3) : web.Code;
                printLabel.Text = string.Format(Texts.CaPrint, caPrint).Replace(": ", ":\n");
                Say(state + "\n" + details, viewer.Length > 0 ? Ui.Good : Ui.Waiting);
                line = string.Format(Texts.IpadOpen, WebCast.LocalUrl()) + "\n" + string.Format(Texts.IpadCode, web.Code)
                    + "   " + string.Format(Texts.CaPrint, caPrint) + "\n" + state + (details.Length > 0 ? "\n" + details : "")
                    + (ev.StartsWith("iPad:") ? "\n" + ev : "");
            }
            else Say(line, caster.Status.Length > 0 ? Ui.Waiting : Ui.Good);
            if (logFile != null)
            {
                long en = Interlocked.Exchange(ref caster.EncN, 0), es = Interlocked.Exchange(ref caster.EncNs, 0);
                long cn = Interlocked.Exchange(ref caster.ConvN, 0), cs = Interlocked.Exchange(ref caster.ConvNs, 0);
                string diag = string.Format(" | encoder {0:0.0} ms, NV12 {1:0.0} ms", en > 0 ? es / 1e6 / en : 0, cn > 0 ? cs / 1e6 / cn : 0);
                if (caster.FirstKeyHex.Length > 0) { diag += " | key " + caster.FirstKeyHex; caster.FirstKeyHex = "-"; }
                if (web == null) diag += " | via " + (caster.Usb ? "USB" : "Wi-Fi") + ", moves " + caster.Moves + ", search " + caster.RouteNote + ", heard " + Interlocked.Read(ref caster.Heard) + " " + caster.RecvError;
                try { File.AppendAllText(logFile, DateTime.Now.ToString("HH:mm:ss ") + line.Replace('\n', ' ') + diag + Environment.NewLine); }
                catch (IOException) { }
            }
            lastFrames = f;
            lastBytes = b;
            lastTick = now;
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // WebRTC libraries live in lib\ next to the exe; .NET Framework would also insist on
            // the exact versions their references name (e.g. System.Memory 4.0.1.1 vs 4.0.5.0)
            AppDomain.CurrentDomain.AssemblyResolve += delegate(object s, ResolveEventArgs e)
            {
                string f = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lib", new System.Reflection.AssemblyName(e.Name).Name + ".dll");
                return File.Exists(f) ? System.Reflection.Assembly.LoadFrom(f) : null;
            };
            // physical pixels, so the pointer lands where it is on a scaled (125 %, 150 %) screen
            try { if (!Native.SetProcessDpiAwarenessContext(new IntPtr(-4))) Native.SetProcessDPIAware(); }
            catch (EntryPointNotFoundException) { Native.SetProcessDPIAware(); }
            Application.EnableVisualStyles();
            Application.Run(new MainForm(args));
        }
    }
}
