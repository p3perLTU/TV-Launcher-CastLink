// CastLink on a PC as a receiver: another PC casts its screen (and sound) here, like it casts to
// the TV app. The wire format and the encryption are the TV's (Proto, SecureLink); this file is
// the Windows side of cast/receiver/.../ReceiverActivity.java: UDP 47800, the handshake and the
// one-time pairing code, frames put together (Assembler, a port of FrameAssembler.java), the
// Windows H.264 decoder (Media Foundation), the picture drawn 1:1 with GDI when the sizes match,
// and the sound through waveOut with a short queue.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TVCast
{
    /// <summary>
    /// VIDEO chunks back into whole H.264 frames. Same rules as FrameAssembler.java: chunks of a
    /// frame in any order, an unfinished frame counts as lost when a newer one starts, older
    /// chunks are ignored, and after a loss nothing comes out until the next keyframe
    /// (WantsKey tells the caller to ask for one). Unit-tested.
    /// </summary>
    sealed class Assembler
    {
        public const int MAX_CHUNKS = 1500;

        public sealed class Frame
        {
            public byte[] Data;
            public long PtsUs;
            public bool Key;
        }

        readonly byte[] suffix;
        readonly bool[] gotChunk = new bool[MAX_CHUNKS];
        readonly byte[] asm = new byte[MAX_CHUNKS * Proto.MAX_PAYLOAD];
        bool haveFrame, haveDone;
        int curFrame, curCount, curGot, curLast, curFlags, lastDone;
        long curPts;
        volatile bool wantKey = true;
        int lost;

        public Assembler(byte[] suffix) { this.suffix = (byte[])suffix.Clone(); }

        public void Reset() { haveFrame = haveDone = false; wantKey = true; }
        public void RequestKey() { wantKey = true; }
        public void Dropped() { Interlocked.Increment(ref lost); wantKey = true; }
        public bool WantsKey { get { return wantKey; } }
        public int Lost { get { return lost; } }

        static int Short(byte[] b, int o) { return (b[o] << 8) | b[o + 1]; }

        /// <summary>one VIDEO packet (pkt[0] is the type); the frame it completed, or null</summary>
        public Frame Accept(byte[] pkt, int n)
        {
            if (n < Proto.VIDEO_HEADER || pkt[0] != Proto.VIDEO) return null;
            int flags = pkt[1];
            int frame = Proto.GetInt(pkt, 2);
            int chunk = Short(pkt, 6), count = Short(pkt, 8);
            long pts = Proto.GetLong(pkt, 10);
            int len = n - Proto.VIDEO_HEADER;
            if (count == 0 || count > MAX_CHUNKS || chunk >= count || len > Proto.MAX_PAYLOAD) return null;

            if (haveFrame && frame - curFrame < -1000) { haveFrame = haveDone = false; wantKey = true; } // sender restarted
            if (!haveFrame || frame != curFrame)
            {
                if (haveFrame && frame - curFrame < 0) return null; // straggler of an older frame
                if (haveFrame && curGot < curCount) Interlocked.Increment(ref lost);
                haveFrame = true;
                curFrame = frame; curCount = count; curGot = 0; curLast = 0; curFlags = flags; curPts = pts;
                Array.Clear(gotChunk, 0, count);
            }
            if (count != curCount || gotChunk[chunk]) return null;
            gotChunk[chunk] = true;
            curGot++;
            Buffer.BlockCopy(pkt, Proto.VIDEO_HEADER, asm, chunk * Proto.MAX_PAYLOAD, len);
            if (chunk == count - 1) curLast = len;
            if (curGot < curCount) return null;

            bool key = (curFlags & Proto.FLAG_KEY) != 0;
            bool gap = haveDone && frame != lastDone + 1;
            haveDone = true;
            lastDone = frame;
            if (gap && !key) { Interlocked.Increment(ref lost); wantKey = true; }
            if (wantKey && !key) return null;
            if (key) wantKey = false;
            int total = (curCount - 1) * Proto.MAX_PAYLOAD + curLast;
            byte[] data = new byte[total + suffix.Length];
            Buffer.BlockCopy(asm, 0, data, 0, total);
            Buffer.BlockCopy(suffix, 0, data, total, suffix.Length);
            return new Frame { Data = data, PtsUs = curPts, Key = key };
        }
    }

    /// <summary>a monitor's refresh rate, from Windows' current display mode</summary>
    static class DisplayHz
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool EnumDisplaySettings(string device, int mode, byte[] devmode);

        /// <summary>Hz of \\.\DISPLAYn (null: the main screen); 60 if unknown</summary>
        public static int Of(string device)
        {
            byte[] dm = new byte[220]; // DEVMODEW
            BitConverter.GetBytes((short)220).CopyTo(dm, 68); // dmSize
            try
            {
                if (EnumDisplaySettings(device ?? Screen.PrimaryScreen.DeviceName, -1 /*ENUM_CURRENT_SETTINGS*/, dm))
                {
                    int hz = BitConverter.ToInt32(dm, 184); // dmDisplayFrequency
                    if (hz >= 24 && hz <= 1000) return hz;
                }
            }
            catch (Exception) { }
            return 60;
        }
    }

    /// <summary>The Windows H.264 decoder (Media Foundation MFT), H.264 Annex B in, NV12 out.</summary>
    sealed class Decoder : IDisposable
    {
        static Guid CAT_VIDEO_DECODER = new Guid("d6c02d4b-6833-45b4-971a-05a4b04bab91");
        static Guid MF_MT_MAJOR_TYPE = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        static Guid MF_MT_SUBTYPE = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        static Guid MFMediaType_Video = new Guid("73646976-0000-0010-8000-00AA00389B71");
        static Guid MFVideoFormat_H264 = new Guid("34363248-0000-0010-8000-00AA00389B71");
        static Guid MFVideoFormat_NV12 = new Guid("3231564E-0000-0010-8000-00AA00389B71");
        static Guid MF_MT_FRAME_SIZE = new Guid("1652c33d-d6b2-4012-b834-72030849a37d");
        static Guid MF_LOW_LATENCY = new Guid("9c27891a-ed7a-40e1-88e8-b22727a024ee");
        static Guid IID_IMFTransform = new Guid("bf94c121-5b05-4e6f-8000-ba598961414d");
        const int MF_E_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
        const int MF_E_STREAM_CHANGE = unchecked((int)0xC00D6D61);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_GetU64(IntPtr self, ref Guid key, out ulong v);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_GetGuid(IntPtr self, ref Guid key, out Guid v);

        IntPtr mft;
        bool providesSamples;
        uint outSize;
        public int AlignedW, AlignedH; // the decoder's buffer size (height rounded up to 16)
        public string Name = "";

        public Decoder()
        {
            Native.MFStartup(0x20070, 0);
            IntPtr inInfo = TypeInfo(MFVideoFormat_H264), outInfo = TypeInfo(MFVideoFormat_NV12);
            IntPtr acts; uint n;
            int hr = Native.MFTEnumEx(CAT_VIDEO_DECODER, 0x1 | 0x40, inInfo, outInfo, out acts, out n); // sync, sorted
            Marshal.FreeHGlobal(inInfo);
            Marshal.FreeHGlobal(outInfo);
            if (hr < 0 || n == 0) throw new Exception(Texts.NoDecoder);
            try
            {
                IntPtr a = Marshal.ReadIntPtr(acts, 0);
                Guid iid = IID_IMFTransform;
                Com.Check(Com.F<D_Activate>(a, 33)(a, ref iid, out mft), "ActivateObject");
                Name = "Microsoft H.264";
            }
            finally
            {
                for (int i = 0; i < n; i++) Marshal.Release(Marshal.ReadIntPtr(acts, i * IntPtr.Size));
                Native.CoTaskMemFree(acts);
            }
            IntPtr attrs;
            if (Com.F<D_Out>(mft, 8)(mft, out attrs) >= 0 && attrs != IntPtr.Zero)
            {
                Com.F<D_SetU32>(attrs, 21)(attrs, ref MF_LOW_LATENCY, 1); // one frame in, the same frame out
                Com.Release(ref attrs);
            }
            IntPtr t;
            Com.Check(Native.MFCreateMediaType(out t), "MFCreateMediaType");
            Com.F<D_SetGuid>(t, 24)(t, ref MF_MT_MAJOR_TYPE, ref MFMediaType_Video);
            Com.F<D_SetGuid>(t, 24)(t, ref MF_MT_SUBTYPE, ref MFVideoFormat_H264);
            hr = Com.F<D_SetType>(mft, 15)(mft, 0, t, 0);
            Com.Release(ref t);
            Com.Check(hr, "SetInputType");
            PickOutput();
            Com.F<D_Msg>(mft, 23)(mft, 0x10000000, IntPtr.Zero); // BEGIN_STREAMING
            Com.F<D_Msg>(mft, 23)(mft, 0x10000003, IntPtr.Zero); // START_OF_STREAM
        }

        static IntPtr TypeInfo(Guid sub)
        {
            IntPtr p = Marshal.AllocHGlobal(32);
            Marshal.Copy(MFMediaType_Video.ToByteArray(), 0, p, 16);
            Marshal.Copy(sub.ToByteArray(), 0, p + 16, 16);
            return p;
        }

        /// <summary>NV12 output; called again whenever the stream size changes</summary>
        void PickOutput()
        {
            for (uint i = 0; ; i++)
            {
                IntPtr t;
                if (Com.F<D_AvailType>(mft, 14)(mft, 0, i, out t) < 0) break;
                Guid sub;
                Com.F<D_GetGuid>(t, 10)(t, ref MF_MT_SUBTYPE, out sub);
                if (sub == MFVideoFormat_NV12)
                {
                    ulong size;
                    if (Com.F<D_GetU64>(t, 8)(t, ref MF_MT_FRAME_SIZE, out size) >= 0)
                    {
                        AlignedW = (int)(size >> 32);
                        AlignedH = (int)(size & 0xFFFFFFFF);
                    }
                    Com.F<D_SetType>(mft, 16)(mft, 0, t, 0);
                    Com.Release(ref t);
                    break;
                }
                Com.Release(ref t);
            }
            IntPtr info = Marshal.AllocHGlobal(12);
            Com.F<D_StreamInfo>(mft, 7)(mft, 0, info);
            providesSamples = (Marshal.ReadInt32(info, 0) & 0x300) != 0;
            outSize = (uint)Marshal.ReadInt32(info, 4);
            Marshal.FreeHGlobal(info);
        }

        /// <summary>one whole frame (Annex B) in</summary>
        public void Input(byte[] data, long ptsUs)
        {
            IntPtr buf, sample;
            Com.Check(Native.MFCreateMemoryBuffer((uint)data.Length, out buf), "MFCreateMemoryBuffer");
            IntPtr p; uint max, cur;
            Com.F<D_Lock>(buf, 3)(buf, out p, out max, out cur);
            Marshal.Copy(data, 0, p, data.Length);
            Com.F<D_Self>(buf, 4)(buf);
            Com.F<D_SetLen>(buf, 6)(buf, (uint)data.Length);
            Native.MFCreateSample(out sample);
            Com.F<D_Buf>(sample, 42)(sample, buf);
            Com.F<D_SetI64>(sample, 36)(sample, ptsUs * 10);
            int hr = Com.F<D_PIn>(mft, 24)(mft, 0, sample, 0);
            Com.Release(ref sample);
            Com.Release(ref buf);
            Com.Check(hr, "ProcessInput");
        }

        /// <summary>
        /// The next decoded picture as NV12 into nv12 (grown when needed); returns its length,
        /// 0 when there is none yet. AlignedW/AlignedH describe the layout.
        /// </summary>
        public int Output(ref byte[] nv12)
        {
            while (true)
            {
                IntPtr db = Marshal.AllocHGlobal(32);
                for (int i = 0; i < 32; i += 8) Marshal.WriteInt64(db, i, 0);
                IntPtr mine = IntPtr.Zero, mineBuf = IntPtr.Zero;
                if (!providesSamples)
                {
                    Native.MFCreateSample(out mine);
                    Native.MFCreateMemoryBuffer(Math.Max(outSize, (uint)(AlignedW * AlignedH * 3 / 2)), out mineBuf);
                    Com.F<D_Buf>(mine, 42)(mine, mineBuf);
                    Marshal.WriteIntPtr(db, 8, mine);
                }
                uint status;
                int hr = Com.F<D_POut>(mft, 25)(mft, 0, 1, db, out status);
                IntPtr sample = Marshal.ReadIntPtr(db, 8), evs = Marshal.ReadIntPtr(db, 24);
                Marshal.FreeHGlobal(db);
                Com.Release(ref evs);
                try
                {
                    if (hr == MF_E_STREAM_CHANGE) { PickOutput(); continue; } // size known now: try again
                    if (hr == MF_E_NEED_MORE_INPUT || hr < 0 || sample == IntPtr.Zero) return 0;
                    IntPtr cb;
                    Com.Check(Com.F<D_Out>(sample, 41)(sample, out cb), "ConvertToContiguousBuffer");
                    IntPtr p; uint max, len;
                    Com.F<D_Lock>(cb, 3)(cb, out p, out max, out len);
                    if (nv12 == null || nv12.Length < len) nv12 = new byte[len];
                    Marshal.Copy(p, nv12, 0, (int)len);
                    Com.F<D_Self>(cb, 4)(cb);
                    Com.Release(ref cb);
                    return (int)len;
                }
                finally
                {
                    if (sample != mine) Com.Release(ref sample);
                    Com.Release(ref mine);
                    Com.Release(ref mineBuf);
                }
            }
        }

        public void Dispose()
        {
            if (mft != IntPtr.Zero) Com.F<D_Msg>(mft, 23)(mft, 0x10000004, IntPtr.Zero); // END_STREAMING
            Com.Release(ref mft);
        }
    }

    /// <summary>NV12 (BT.709, 16..235, the sender's format) to 32-bit BGRX, rows in parallel.</summary>
    static class Yuv
    {
        /// <param name="stride">bytes per Y row in the decoder's buffer</param>
        /// <param name="rows">Y rows in the buffer (the UV plane starts after them)</param>
        public static void ToBgrx(byte[] nv12, int stride, int rows, int w, int h, int[] bgrx)
        {
            int uvBase = stride * rows;
            Parallel.For(0, h / 2, y2 =>
            {
                for (int dy = 0; dy < 2; dy++)
                {
                    int y = y2 * 2 + dy, yRow = y * stride, uvRow = uvBase + y2 * stride, o = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int c = (nv12[yRow + x] - 16) * 298;
                        int ui = uvRow + (x & ~1);
                        int d = nv12[ui] - 128, e = nv12[ui + 1] - 128;
                        int r = (c + 459 * e + 128) >> 8;
                        int g = (c - 55 * d - 136 * e + 128) >> 8;
                        int b = (c + 541 * d + 128) >> 8;
                        if ((uint)r > 255) r = r < 0 ? 0 : 255;
                        if ((uint)g > 255) g = g < 0 ? 0 : 255;
                        if ((uint)b > 255) b = b < 0 ? 0 : 255;
                        bgrx[o + x] = (r << 16) | (g << 8) | b;
                    }
                }
            });
        }
    }

    /// <summary>16-bit stereo PCM through waveOut; at most ~60 ms queued, the rest is dropped.</summary>
    sealed class AudioOut : IDisposable
    {
        [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr h, int dev, byte[] fmt, IntPtr cb, IntPtr inst, int flags);
        [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr h);

        const int HDR = 48, SLOTS = 24, SLOT_BYTES = 4096, MAX_QUEUED = 12; // 12 x 5 ms
        IntPtr wave;
        readonly IntPtr[] hdrs = new IntPtr[SLOTS], data = new IntPtr[SLOTS];
        int next;

        public AudioOut(int rate, int channels)
        {
            byte[] f = new byte[18]; // WAVEFORMATEX, PCM
            BitConverter.GetBytes((short)1).CopyTo(f, 0);
            BitConverter.GetBytes((short)channels).CopyTo(f, 2);
            BitConverter.GetBytes(rate).CopyTo(f, 4);
            BitConverter.GetBytes(rate * channels * 2).CopyTo(f, 8);
            BitConverter.GetBytes((short)(channels * 2)).CopyTo(f, 12);
            BitConverter.GetBytes((short)16).CopyTo(f, 14);
            if (waveOutOpen(out wave, -1, f, IntPtr.Zero, IntPtr.Zero, 0) != 0) { wave = IntPtr.Zero; return; }
            for (int i = 0; i < SLOTS; i++)
            {
                data[i] = Marshal.AllocHGlobal(SLOT_BYTES);
                hdrs[i] = Marshal.AllocHGlobal(HDR);
                for (int k = 0; k < HDR; k += 8) Marshal.WriteInt64(hdrs[i], k, 0);
                Marshal.WriteIntPtr(hdrs[i], 0, data[i]);
                Marshal.WriteInt32(hdrs[i], 8, SLOT_BYTES);
                waveOutPrepareHeader(wave, hdrs[i], HDR);
                Marshal.WriteInt32(hdrs[i], 24, Marshal.ReadInt32(hdrs[i], 24) | 1); // WHDR_DONE: free
            }
        }

        int Queued()
        {
            int q = 0;
            for (int i = 0; i < SLOTS; i++) if ((Marshal.ReadInt32(hdrs[i], 24) & 1) == 0) q++;
            return q;
        }

        public void Play(byte[] b, int offset, int len)
        {
            if (wave == IntPtr.Zero || len <= 0 || len > SLOT_BYTES) return;
            if (Queued() >= MAX_QUEUED) return; // behind: drop rather than build up delay
            IntPtr h = hdrs[next];
            if ((Marshal.ReadInt32(h, 24) & 1) == 0) return;
            Marshal.Copy(b, offset, data[next], len);
            Marshal.WriteInt32(h, 8, len);
            Marshal.WriteInt32(h, 24, Marshal.ReadInt32(h, 24) & ~1);
            waveOutWrite(wave, h, HDR);
            next = (next + 1) % SLOTS;
        }

        public void Dispose()
        {
            if (wave == IntPtr.Zero) return;
            waveOutReset(wave);
            for (int i = 0; i < SLOTS; i++)
            {
                waveOutUnprepareHeader(wave, hdrs[i], HDR);
                Marshal.FreeHGlobal(hdrs[i]);
                Marshal.FreeHGlobal(data[i]);
            }
            waveOutClose(wave);
            wave = IntPtr.Zero;
        }
    }

    /// <summary>
    /// This PC as a CastLink receiver. Network thread: discovery answers, handshake, pairing,
    /// frames; a decode thread draws on the viewer window. The window (MainForm) polls the
    /// public fields.
    /// </summary>
    sealed class PcReceiver : IDisposable
    {
        const int IDLE_MS = 3000, KEYREQ_EVERY_MS = 300;
        static readonly byte[] AUD = { 0, 0, 0, 1, 0x09, 0xF0 }; // access unit delimiter after each frame

        Socket sock;
        Thread netThread, decodeThread;
        volatile bool running;
        readonly CngKey identity = SecureLink.Identity();
        readonly byte[] identityPoint;
        readonly string myName = Environment.MachineName;

        SecureLink.Session link;
        byte[] linkCasterStatic, linkHelloNonce, helloOkBytes;
        string linkCasterName = "";
        volatile bool linkTrusted;
        long lastLinkAt, lastHelloAt, lastPacketAt, lastPing, lastKeyReq;
        int pings;
        IPEndPoint sender;
        readonly Assembler assembler = new Assembler(AUD);
        readonly Queue<Assembler.Frame> frames = new Queue<Assembler.Frame>();
        readonly AutoResetEvent frameReady = new AutoResetEvent(false);
        AudioOut audio;
        int cfgW, cfgH;
        byte[] inner = new byte[2048];

        /// <summary>shown in the window</summary>
        public volatile string PairCode, CasterName = "", Error;
        public volatile bool Streaming;
        public volatile int ShownFrames, Width, Height, Rejected;
        /// <summary>timing totals for the log: decode, NV12 -> BGRX, draw (100 ns ticks) and frame count</summary>
        public long DecodeTicks, ConvertTicks, DrawTicks, TimedFrames;
        public int Lost { get { return assembler.Lost; } }
        /// <summary>the viewer: Draw(bgrx, w, h) on the decode thread</summary>
        public volatile Action<int[], int, int> Draw;
        public long Bytes;

        public PcReceiver() { identityPoint = SecureLink.PointOf(identity); }

        // control: the caster allows it (CONFIG flag) and signs its INPUT with this token
        volatile byte[] token;
        public volatile bool ControlAllowed;

        /// <summary>a RemoteInput message (mouse, key) to the casting PC; dropped unless it allows control</summary>
        public void SendInput(byte[] msg)
        {
            byte[] t = token;
            if (!ControlAllowed || t == null || msg.Length > RemoteInput.MaxMessage) return;
            byte[] p = new byte[1 + Proto.TOKEN_BYTES + msg.Length];
            p[0] = Proto.INPUT;
            Buffer.BlockCopy(t, 0, p, 1, Proto.TOKEN_BYTES);
            Buffer.BlockCopy(msg, 0, p, 1 + Proto.TOKEN_BYTES, msg.Length);
            SendSealed(p);
        }

        public void Start()
        {
            sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try { sock.Bind(new IPEndPoint(IPAddress.Any, Proto.PORT)); }
            catch (SocketException) { sock.Close(); sock = null; Error = Texts.ReceiverPortBusy; return; }
            sock.ReceiveTimeout = 250;
            sock.ReceiveBufferSize = 4 << 20;
            running = true;
            netThread = new Thread(NetLoop) { IsBackground = true, Name = "receiver net", Priority = ThreadPriority.AboveNormal };
            decodeThread = new Thread(DecodeLoop) { IsBackground = true, Name = "receiver decode", Priority = ThreadPriority.AboveNormal };
            netThread.Start();
            decodeThread.Start();
        }

        void NetLoop()
        {
            byte[] buf = new byte[2048];
            while (running)
            {
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                int n;
                try { n = sock.ReceiveFrom(buf, ref from); }
                catch (SocketException) { CheckIdle(); continue; }
                catch (ObjectDisposedException) { return; }
                IPEndPoint src = (IPEndPoint)from;
                if (n < 1) continue;
                // only home-network devices; while one casts, only that one (via the keys)
                if (!Proto.IsLanV4(src.Address.GetAddressBytes())) { Rejected++; continue; }
                if (buf[0] == Proto.WHO) { SendRaw(Name(), src); continue; }
                if (buf[0] == SecureLink.HELLO) { OnHello(buf, n, src); continue; }
                SecureLink.Session l = link;
                int m = buf[0] == SecureLink.SEALED && l != null ? l.Open(buf, n, inner) : -1;
                if (m < 1) { Rejected++; continue; }
                sender = src; // only the caster has the keys, so it may move: follow it
                long now = Clock.Ms();
                lastLinkAt = now;
                Interlocked.Add(ref Bytes, n);
                byte type = inner[0];
                if (type == SecureLink.PAIR_SHOW) { if (!linkTrusted) PairCode = l.CodeText; continue; }
                if (!linkTrusted)
                {
                    // nothing but the pairing until the user confirmed the code on the caster
                    if (type != Proto.CONFIG || m < Proto.CONFIG_LONG || (inner[14] & SecureLink.CONFIG_PAIRED) == 0) continue;
                    SecureLink.Trust(linkCasterStatic, linkCasterName);
                    linkTrusted = true;
                    PairCode = null;
                }
                lastPacketAt = now;
                switch (type)
                {
                    case Proto.CONFIG: OnConfig(inner, m); break;
                    case Proto.VIDEO: OnVideo(inner, m); break;
                    case Proto.AUDIO: if (audio != null && m > 5) audio.Play(inner, 5, m - 5); break;
                    case Proto.BYE: GoIdle(); break;
                }
                if (Streaming)
                {
                    if (assembler.WantsKey) RequestKey();
                    Ping();
                }
            }
        }

        byte[] Name()
        {
            byte[] nm = System.Text.Encoding.UTF8.GetBytes(myName);
            byte[] b = new byte[1 + nm.Length];
            b[0] = Proto.NAME;
            Buffer.BlockCopy(nm, 0, b, 1, nm.Length);
            return b;
        }

        void OnHello(byte[] buf, int n, IPEndPoint from)
        {
            SecureLink.Hello h = SecureLink.Parse(buf, n);
            if (h == null || h.Echo != null) return;
            if (link != null && helloOkBytes != null && SecureLink.Same(h.Nonce, linkHelloNonce)) { SendRaw(helloOkBytes, from); return; }
            long now = Clock.Ms();
            IPEndPoint cur = sender;
            bool busy = link != null && now - lastLinkAt < IDLE_MS && !SecureLink.Same(h.Static, linkCasterStatic)
                && (cur == null || !cur.Address.Equals(from.Address));
            if (busy || now - lastHelloAt < 250) { Rejected++; return; }
            lastHelloAt = now;
            try
            {
                using (CngKey eph = SecureLink.NewKey(false))
                {
                    byte[] ephPoint = SecureLink.PointOf(eph), rn = SecureLink.RandomNonce();
                    SecureLink.Session s = SecureLink.Derive(false, identity, eph, h.Nonce, rn, h.Ephemeral, ephPoint, h.Static, identityPoint);
                    bool known = SecureLink.Trusted(h.Static);
                    byte[] reply = SecureLink.MakeHelloOk(rn, ephPoint, identityPoint, h.Nonce, known, myName);
                    if (Streaming || link != null) GoIdle();
                    SecureLink.Session old = link;
                    link = s;
                    if (old != null) old.Dispose();
                    linkCasterStatic = h.Static;
                    linkHelloNonce = h.Nonce;
                    linkCasterName = h.Name;
                    CasterName = h.Name;
                    linkTrusted = known;
                    helloOkBytes = reply;
                    PairCode = null;
                    sender = from;
                    lastLinkAt = now;
                    SendRaw(reply, from);
                }
            }
            catch (CryptographicException) { Rejected++; } // a point off the curve
        }

        void OnConfig(byte[] b, int n)
        {
            if (n < 14) return;
            int w = Proto.GetInt(b, 1), h = Proto.GetInt(b, 5), rate = Proto.GetInt(b, 9), ch = b[13];
            if (w <= 0 || h <= 0 || w > 4096 || h > 4096) return;
            Streaming = true;
            if (w != cfgW || h != cfgH)
            {
                cfgW = w; cfgH = h;
                Width = w; Height = h;
                assembler.Reset();
                lock (frames) frames.Clear();
            }
            if (audio == null && (ch == 1 || ch == 2) && rate >= 8000 && rate <= 48000) audio = new AudioOut(rate, ch);
            if (n >= Proto.CONFIG_LONG)
            {
                byte[] t = new byte[Proto.TOKEN_BYTES];
                Buffer.BlockCopy(b, 15, t, 0, Proto.TOKEN_BYTES);
                token = t;
                ControlAllowed = (b[14] & Proto.CONFIG_CONTROL) != 0;
            }
            else ControlAllowed = false;
        }

        void OnVideo(byte[] b, int n)
        {
            if (cfgW == 0) return;
            Assembler.Frame f = assembler.Accept(b, n);
            if (f == null) return;
            lock (frames)
            {
                // the decoder is far behind (a hiccup): start over from a keyframe. Normally every
                // frame is decoded and only the newest one is drawn, so the queue stays short.
                if (frames.Count >= 30) { frames.Clear(); assembler.Dropped(); return; }
                frames.Enqueue(f);
            }
            frameReady.Set();
        }

        void DecodeLoop()
        {
            Decoder dec = null;
            byte[] nv12 = null;
            int[] bgrx = null;
            try
            {
                dec = new Decoder();
                while (running)
                {
                    frameReady.WaitOne(200);
                    while (true)
                    {
                        Assembler.Frame f;
                        lock (frames) { if (frames.Count == 0) break; f = frames.Dequeue(); }
                        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                        try { dec.Input(f.Data, f.PtsUs); }
                        catch (COMException) { assembler.RequestKey(); continue; }
                        int len;
                        while ((len = dec.Output(ref nv12)) > 0)
                        {
                            bool newer;
                            lock (frames) newer = frames.Count > 0;
                            if (newer) { t0 = System.Diagnostics.Stopwatch.GetTimestamp(); continue; } // a newer frame waits: draw that one
                            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                            int w = cfgW, h = cfgH;
                            int rows = dec.AlignedH > 0 ? dec.AlignedH : h;
                            int stride = len * 2 / 3 / rows;
                            if (stride < w || rows < h) continue;
                            if (bgrx == null || bgrx.Length != w * h) bgrx = new int[w * h];
                            Yuv.ToBgrx(nv12, stride, rows, w, h, bgrx);
                            long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
                            Action<int[], int, int> d = Draw;
                            if (d != null) d(bgrx, w, h);
                            long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
                            Interlocked.Add(ref DecodeTicks, t1 - t0);
                            Interlocked.Add(ref ConvertTicks, t2 - t1);
                            Interlocked.Add(ref DrawTicks, t3 - t2);
                            Interlocked.Increment(ref TimedFrames);
                            t0 = t3;
                            ShownFrames++;
                        }
                    }
                }
            }
            catch (Exception e) { Error = e.Message; }
            finally { if (dec != null) dec.Dispose(); }
        }

        void SendRaw(byte[] b, IPEndPoint to)
        {
            Socket s = sock;
            if (s == null || to == null) return;
            try { s.SendTo(b, to); } catch (SocketException) { } catch (ObjectDisposedException) { }
        }

        void SendSealed(byte[] pkt)
        {
            SecureLink.Session l = link;
            IPEndPoint to = sender;
            if (l == null || to == null) return;
            byte[] o = new byte[pkt.Length + SecureLink.OVERHEAD];
            int n = l.Seal(pkt, pkt.Length, o);
            Socket s = sock;
            try { if (s != null) s.SendTo(o, 0, n, SocketFlags.None, to); } catch (SocketException) { } catch (ObjectDisposedException) { }
        }

        void Ping()
        {
            long now = Clock.Ms();
            if (now - lastPing < 500) return;
            lastPing = now;
            if (pings++ % 4 == 0)
            {
                // every 2 s: this screen's size and refresh rate, so the PC sends the picture that
                // big (1:1 here) and as many frames a second as this screen shows
                Rectangle r = Screen.PrimaryScreen.Bounds;
                byte[] info = new byte[13];
                info[0] = Proto.INFO;
                Proto.PutInt(info, 1, Math.Max(r.Width, r.Height));
                Proto.PutInt(info, 5, Math.Min(r.Width, r.Height));
                Proto.PutInt(info, 9, DisplayHz.Of(null));
                SendSealed(info);
            }
            byte[] b = new byte[9];
            b[0] = Proto.PING;
            Proto.PutLong(b, 1, Clock.Nanos());
            SendSealed(b);
        }

        void RequestKey()
        {
            long now = Clock.Ms();
            if (now - lastKeyReq < KEYREQ_EVERY_MS) return;
            lastKeyReq = now;
            SendSealed(new[] { Proto.KEYREQ });
        }

        void CheckIdle()
        {
            long now = Clock.Ms();
            if (Streaming && now - lastPacketAt > IDLE_MS) GoIdle();
            else if (!Streaming && link != null && now - lastLinkAt > IDLE_MS) { link = null; PairCode = null; }
            else if (Streaming) Ping(); // keep the caster's link alive while the picture is still
        }

        void GoIdle()
        {
            Streaming = false;
            ControlAllowed = false;
            token = null;
            cfgW = cfgH = 0;
            assembler.Reset();
            lock (frames) frames.Clear();
            AudioOut a = audio;
            audio = null;
            if (a != null) a.Dispose();
        }

        public void Dispose()
        {
            running = false;
            if (sock != null) sock.Close();
            frameReady.Set();
            if (netThread != null) netThread.Join(1000);
            if (decodeThread != null) decodeThread.Join(1000);
            GoIdle();
            SecureLink.Session l = link;
            link = null;
            if (l != null) l.Dispose();
        }
    }

    /// <summary>
    /// The picture: borderless and full screen on the main monitor, black around it; closing it
    /// stops receiving. When the casting PC allows control, the mouse over the picture and the
    /// whole keyboard (a low-level hook while this window is in front, so Alt+Tab, Win and other
    /// shortcuts go there too) control that PC. Ctrl+Alt+Enter: full screen / window (stays here).
    /// </summary>
    sealed class ViewerForm : Form
    {
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] static extern int StretchDIBits(IntPtr dc, int x, int y, int w, int h, int sx, int sy, int sw, int sh, int[] bits, byte[] info, uint usage, uint rop);
        [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr dc, int mode);

        [StructLayout(LayoutKind.Sequential)] struct KBDLLHOOKSTRUCT { public uint vk, scan, flags, time; public IntPtr extra; }
        delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);

        readonly byte[] bmi = new byte[40];
        readonly PcReceiver receiver;
        bool full = true;
        IntPtr handle, hook;
        HookProc hookProc; // kept alive while hooked
        readonly HashSet<byte> held = new HashSet<byte>();
        volatile int picX, picY, picW = 1, picH = 1; // where the picture is drawn in the window
        int lastX = -1, lastY = -1;
        readonly Cursor blank;

        public ViewerForm(string title, PcReceiver receiver)
        {
            this.receiver = receiver;
            Text = title;
            BackColor = Color.Black;
            StartPosition = FormStartPosition.Manual;
            Bounds = Screen.PrimaryScreen.Bounds;
            FormBorderStyle = FormBorderStyle.None;
            KeyPreview = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque, true);
            BitConverter.GetBytes(40).CopyTo(bmi, 0);
            BitConverter.GetBytes((short)1).CopyTo(bmi, 12);
            BitConverter.GetBytes((short)32).CopyTo(bmi, 14);
            using (Bitmap b = new Bitmap(1, 1)) blank = new Cursor(b.GetHicon()); // the other PC draws its own pointer in the picture
            HandleCreated += delegate { handle = Handle; };
            Activated += delegate { Hook(); };
            Deactivate += delegate { Unhook(); };
            FormClosed += delegate { Unhook(); };
            // without control: no hook, Esc switches full screen / window as before
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape && !receiver.ControlAllowed) ToggleFull(); };
            MouseMove += (s, e) => Pointer(e.X, e.Y, false);
            MouseDown += (s, e) => Press(e, true);
            MouseUp += (s, e) => Press(e, false);
            MouseWheel += (s, e) => { if (receiver.ControlAllowed) receiver.SendInput(new byte[] { 12, (byte)(e.Delta >> 8), (byte)e.Delta, 0, 0 }); };
        }

        /// <summary>window point -> 0..65535 on the picture (clamped to its edges); unit-tested</summary>
        internal static int[] ToRemote(int x, int y, int px, int py, int pw, int ph)
        {
            int rx = pw > 1 ? (int)((long)(x - px) * 65535 / (pw - 1)) : 0;
            int ry = ph > 1 ? (int)((long)(y - py) * 65535 / (ph - 1)) : 0;
            return new[] { Math.Max(0, Math.Min(65535, rx)), Math.Max(0, Math.Min(65535, ry)) };
        }

        void Pointer(int x, int y, bool force)
        {
            bool ctl = receiver.ControlAllowed;
            Cursor = ctl ? blank : Cursors.Default;
            if (!ctl || (!force && x == lastX && y == lastY)) return;
            lastX = x; lastY = y;
            int[] r = ToRemote(x, y, picX, picY, picW, picH);
            receiver.SendInput(new byte[] { 11, (byte)(r[0] >> 8), (byte)r[0], (byte)(r[1] >> 8), (byte)r[1] });
        }

        void Press(MouseEventArgs e, bool down)
        {
            if (!receiver.ControlAllowed) return;
            int b = e.Button == MouseButtons.Left ? 0 : e.Button == MouseButtons.Right ? 1 : e.Button == MouseButtons.Middle ? 2 : -1;
            if (b < 0) return;
            Pointer(e.X, e.Y, true); // first where, then the press there
            receiver.SendInput(new byte[] { 7, (byte)b, (byte)(down ? 1 : 0) });
        }

        void Hook()
        {
            if (hook != IntPtr.Zero) return;
            hookProc = OnKey;
            hook = SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, hookProc, GetModuleHandle(null), 0);
        }

        /// <summary>focus left the picture: let go every key pressed there, then stop catching keys</summary>
        void Unhook()
        {
            byte[] keys;
            lock (held) { keys = new byte[held.Count]; held.CopyTo(keys); held.Clear(); }
            foreach (byte k in keys) receiver.SendInput(new byte[] { 10, k, 0, 0 });
            if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
        }

        IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && receiver.ControlAllowed && GetForegroundWindow() == handle)
            {
                KBDLLHOOKSTRUCT k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                if ((k.flags & 0x10) == 0) // LLKHF_INJECTED: not ours to forward
                {
                    bool down = (k.flags & 0x80) == 0; // LLKHF_UP
                    byte vk = (byte)k.vk;
                    bool ctrl = (Control.ModifierKeys & Keys.Control) != 0, alt = (Control.ModifierKeys & Keys.Alt) != 0;
                    if (vk == 0x0D && ctrl && alt) // Ctrl+Alt+Enter stays here: full screen / window
                    {
                        if (down) BeginInvoke((MethodInvoker)ToggleFull);
                        return (IntPtr)1;
                    }
                    lock (held) { if (down) held.Add(vk); else held.Remove(vk); }
                    receiver.SendInput(new byte[] { 10, vk, (byte)(down ? 1 : 0), (byte)((k.flags & 1) != 0 ? 1 : 0) });
                    return (IntPtr)1; // swallowed here: Alt+Tab, Win etc. act on the other PC only
                }
            }
            return CallNextHookEx(hook, code, wParam, lParam);
        }

        void ToggleFull()
        {
            full = !full;
            if (full) { FormBorderStyle = FormBorderStyle.None; Bounds = Screen.FromControl(this).Bounds; }
            else
            {
                FormBorderStyle = FormBorderStyle.Sizable;
                Rectangle wa = Screen.FromControl(this).WorkingArea;
                Bounds = new Rectangle(wa.X + wa.Width / 8, wa.Y + wa.Height / 8, wa.Width * 3 / 4, wa.Height * 3 / 4);
            }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e) { e.Graphics.Clear(Color.Black); }

        /// <summary>called on the decode thread: fits the picture into the window (1:1 when the sizes match)</summary>
        public void ShowFrame(int[] bgrx, int w, int h)
        {
            IntPtr hw = handle;
            if (hw == IntPtr.Zero) return;
            Size cs;
            try { cs = ClientSize; } catch (ObjectDisposedException) { return; }
            if (cs.Width <= 0 || cs.Height <= 0) return;
            double k = Math.Min(cs.Width / (double)w, cs.Height / (double)h);
            int dw = (int)Math.Round(w * k), dh = (int)Math.Round(h * k);
            int dx = (cs.Width - dw) / 2, dy = (cs.Height - dh) / 2;
            picX = dx; picY = dy; picW = dw; picH = dh;
            BitConverter.GetBytes(w).CopyTo(bmi, 4);
            BitConverter.GetBytes(-h).CopyTo(bmi, 8); // top-down rows
            IntPtr dc = GetDC(hw);
            if (dc == IntPtr.Zero) return;
            try
            {
                SetStretchBltMode(dc, 3); // COLORONCOLOR: HALFTONE took ~11 ms a frame
                StretchDIBits(dc, dx, dy, dw, dh, 0, 0, w, h, bgrx, bmi, 0, 0x00CC0020 /*SRCCOPY*/);
            }
            finally { ReleaseDC(hw, dc); }
        }
    }
}
