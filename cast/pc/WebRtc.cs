// The iPad over UDP: WebRTC through the SIPSorcery library (cast\pc\lib, see
// restore-packages.ps1). TCP (the WebSocket) delivers everything in order, so one lost Wi-Fi
// packet held up picture and sound until it was sent again (100-300 ms); WebRTC sends RTP over
// UDP and the browser's own player skips or recovers what is lost, like Moonlight does.
//
//   1. after the pairing code, the page says {"t":"rtc-hello"} on the WebSocket
//   2. the PC answers {"t":"offer","sdp":..} (H.264 + Opus, send only, DTLS-SRTP encrypted)
//   3. the page sends {"t":"answer","sdp":..} and its {"t":"ice",..} candidates
//   4. once connected, the frames and the sound go out as RTP instead of over the WebSocket;
//      input, the code and the reports stay on the WebSocket
//
// Only private IPv4 candidates are offered or accepted (no IPv6, no public addresses), the
// signalling runs only after the pairing code, and the browser checks the DTLS fingerprint it
// got there. Without the lib folder (or if anything fails) the WebSocket path just continues.

using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using Concentus.Enums;
using Concentus.Structs;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace TVCast
{
    /// <summary>
    /// The calls WebCast makes, with the link passed as object: WebCast's own methods never name
    /// a SIPSorcery type, so when cast\pc\lib is missing the failure (FileNotFoundException while
    /// this class is compiled at run time) happens at one of these calls and is caught there.
    /// </summary>
    static class RtcGlue
    {
        public static bool LibraryPresent
        {
            get { return System.IO.File.Exists(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lib", "SIPSorcery.dll")); }
        }

        /// <summary>with --log: SIPSorcery's own messages (warnings and up) go to that file too</summary>
        public static string LogFile;
        public static int DropEvery { set { RtcLink.DropEvery = value; } }
        static bool logSet;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static object Start(Action<string> sendText, Action keyRequest)
        {
            if (LogFile != null && !logSet) { logSet = true; SIPSorcery.LogFactory.Set(new RtcLog(LogFile)); }
            RtcLink l = new RtcLink(sendText, keyRequest);
            l.Start();
            return l;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static void Answer(object l, string sdp) { ((RtcLink)l).OnAnswer(sdp); }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static void Ice(object l, string c, string m, int i) { ((RtcLink)l).OnIce(c, m, i); }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static bool Connected(object l) { return l != null && ((RtcLink)l).Connected; }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static void Video(object l, byte[] b, int len, uint duration) { ((RtcLink)l).Video(b, len, duration); }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static void Audio(object l, byte[] b, int off, int len, int rate) { ((RtcLink)l).Audio(b, off, len, rate); }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static void Dispose(object l) { if (l != null) ((RtcLink)l).Dispose(); }
    }

    /// <summary>a minimal logger for SIPSorcery (debugging WebRTC with --log)</summary>
    sealed class RtcLog : Microsoft.Extensions.Logging.ILoggerFactory, Microsoft.Extensions.Logging.ILogger
    {
        readonly string file;
        public RtcLog(string file) { this.file = file; }
        public Microsoft.Extensions.Logging.ILogger CreateLogger(string category) { return this; }
        public void AddProvider(Microsoft.Extensions.Logging.ILoggerProvider provider) { }
        public void Dispose() { }
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) { return level >= Microsoft.Extensions.Logging.LogLevel.Debug; }
        public IDisposable BeginScope<TState>(TState state) { return null; }
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId id, TState state, Exception e, Func<TState, Exception, string> format)
        {
            if (!IsEnabled(level)) return;
            string line = DateTime.Now.ToString("HH:mm:ss.fff ") + "rtc " + level + ": " + format(state, e) + (e != null ? " | " + e.GetType().Name + ": " + e.Message : "");
            lock (this) { try { System.IO.File.AppendAllText(file, line + Environment.NewLine); } catch (System.IO.IOException) { } }
        }
    }

    sealed class RtcLink : IDisposable
    {
        readonly RTCPeerConnection pc;
        readonly Action<string> sendText;
        readonly Action keyRequest;
        readonly OpusEncoder opus;
        readonly short[] pcm = new short[960 * 2]; // 20 ms of 48 kHz stereo
        readonly byte[] opusOut = new byte[1500];
        int pcmFill;

        // our own H.264 RTP packets (RFC 6184), so a lost one can be sent again on NACK: SIPSorcery
        // has no retransmission of its own. The last 1024 packets are kept (~1 s at 10 Mb/s).
        const int VideoPt = 96, MaxPayload = 1200, Keep = 1024;
        readonly byte[][] sentPayload = new byte[Keep][];
        readonly uint[] sentTs = new uint[Keep];
        readonly bool[] sentMarker = new bool[Keep];
        readonly ushort[] sentSeq = new ushort[Keep];
        readonly object sendLock = new object();
        ushort seq = (ushort)new Random().Next(0, 65536);
        uint videoTs = (uint)new Random().Next();
        public int Resent, NacksReceived, MissedResends;

        // Pacing: a changed screen (switching apps) gives a frame of a few hundred KB; sent in one go
        // the Wi-Fi drops part of the burst and the picture waits for NACKs. Packets go out from a
        // queue at PaceBytesPerSecond (32 Mb/s) with at most PaceBurst bytes at once.
        internal const int PaceBytesPerSecond = 4000000, PaceBurst = 48000;
        readonly Queue<int> paceQueue = new Queue<int>(); // ring slots waiting to go out
        readonly System.Threading.AutoResetEvent paceWake = new System.Threading.AutoResetEvent(false);
        System.Threading.Thread pacer;
        int frameMax, queueMax; // for the log, per second
        DateTime statAt = DateTime.UtcNow;
        /// <summary>--drop N (testing): every Nth video packet is not sent the first time, as if the Wi-Fi lost it</summary>
        public static int DropEvery;
        volatile bool closed;

        /// <summary>RTP is flowing: the caller stops sending the same over the WebSocket</summary>
        public volatile bool Connected;
        public volatile bool Failed;

        public RtcLink(Action<string> sendText, Action keyRequest)
        {
            this.sendText = sendText;
            this.keyRequest = keyRequest;
            // IPv4 only (the default socket also listened on IPv6, where this PC has public addresses)
            pc = new RTCPeerConnection(new RTCConfiguration { X_UseRtpFeedbackProfile = true, X_BindAddress = IPAddress.Any });
            // for the browser the encoder makes Baseline profile (Encoder profile 66): Constrained
            // Baseline is what every WebRTC browser accepts (Chromium refused High); our level is
            // higher than 3.1, which level-asymmetry-allowed permits
            pc.addTrack(new MediaStreamTrack(new List<VideoFormat> {
                new VideoFormat(VideoCodecsEnum.H264, 96, 90000, "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f") },
                MediaStreamStatusEnum.SendOnly));
            pc.addTrack(new MediaStreamTrack(new List<AudioFormat> {
                new AudioFormat(AudioCodecsEnum.OPUS, 111, 48000, 2, "minptime=10;useinbandfec=1;stereo=1") },
                MediaStreamStatusEnum.SendOnly));
            pc.onicecandidate += c =>
            {
                if (c == null || !IsLanCandidate(c.candidate)) return;
                Send("{\"t\":\"ice\",\"c\":" + Json(c.candidate) + ",\"m\":" + Json(c.sdpMid) + ",\"i\":" + c.sdpMLineIndex + "}");
            };
            pc.onconnectionstatechange += s =>
            {
                if (s == RTCPeerConnectionState.connected)
                {
                    // the iPad's ".local" name is resolved with mDNS, which any device on the network
                    // can answer: only stream when the other end really is a home-network address
                    if (!IsLanEndPoint(pc.AudioDestinationEndPoint))
                    {
                        Failed = true;
                        try { pc.Close("not a home-network address"); } catch (Exception) { }
                        return;
                    }
                    HookRtcp(); Connected = true; keyRequest();
                }
                else if (s == RTCPeerConnectionState.failed || s == RTCPeerConnectionState.closed) { Connected = false; Failed = true; }
            };
            // only a fallback: SIPSorcery keeps one feedback message per compound RTCP packet (and
            // reads only the first entry of a NACK), so most NACKs never got here; HookRtcp reads them all
            pc.OnReceiveReport += (ep, media, report) =>
            {
                if (rawRtcp || media != SDPMediaTypesEnum.video || report == null || report.Feedback == null) return;
                RTCPFeedback fb = report.Feedback;
                if (fb.Header.PacketType == RTCPReportTypesEnum.PSFB)
                {
                    PSFBFeedbackTypesEnum t = fb.Header.PayloadFeedbackMessageType;
                    if (t == PSFBFeedbackTypesEnum.PLI || t == PSFBFeedbackTypesEnum.FIR) keyRequest();
                }
            };
            // The array overloads are marked obsolete in favour of Span<>, which the C# 5 compiler
            // that builds this cannot use; they work the same.
#pragma warning disable 618
            opus = new OpusEncoder(48000, 2, OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY);
#pragma warning restore 618
            opus.Bitrate = 128000;
            opus.UseInbandFEC = true;   // a lost packet can be rebuilt from the next one
            opus.PacketLossPercent = 5;
        }

        /// <summary>makes the offer and sends it to the page</summary>
        public void Start()
        {
            RTCSessionDescriptionInit offer = pc.createOffer(null);
            pc.setLocalDescription(offer).Wait();
            Send("{\"t\":\"offer\",\"sdp\":" + Json(AddVideoFeedback(FilterSdp(offer.sdp))) + "}");
        }

        public void OnAnswer(string sdp)
        {
            SetDescriptionResultEnum r = pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = FilterSdp(sdp) });
            if (r != SetDescriptionResultEnum.OK) Failed = true;
        }

        public void OnIce(string candidate, string mid, int index)
        {
            if (!IsLanCandidate(candidate)) return; // also the iPad's own public IPv6 addresses
            pc.addIceCandidate(new RTCIceCandidateInit { candidate = candidate, sdpMid = mid, sdpMLineIndex = (ushort)index });
        }

        /// <summary>one Annex-B frame (start codes, SPS/PPS on keyframes); duration in 90 kHz units</summary>
        public void Video(byte[] annexB, int len, uint duration)
        {
            if (!Connected || closed) return;
            List<byte[]> packets = Packetize(Fmp4.SplitNals(annexB, len), MaxPayload);
            lock (sendLock)
            {
                videoTs += duration;
                for (int i = 0; i < packets.Count; i++)
                {
                    bool marker = i == packets.Count - 1; // the last packet of the frame
                    int slot = seq % Keep;
                    sentPayload[slot] = packets[i]; sentTs[slot] = videoTs; sentMarker[slot] = marker; sentSeq[slot] = seq;
                    if (DropEvery <= 0 || seq % DropEvery != 0) paceQueue.Enqueue(slot);
                    seq++;
                }
                frameMax = Math.Max(frameMax, len);
                queueMax = Math.Max(queueMax, paceQueue.Count);
                if (pacer == null)
                {
                    pacer = new System.Threading.Thread(PaceLoop) { IsBackground = true, Priority = System.Threading.ThreadPriority.Highest, Name = "rtc pacer" };
                    pacer.Start();
                }
            }
            paceWake.Set();
            if (RtcGlue.LogFile != null && (DateTime.UtcNow - statAt).TotalSeconds >= 1)
            {
                statAt = DateTime.UtcNow;
                try { System.IO.File.AppendAllText(RtcGlue.LogFile, DateTime.Now.ToString("HH:mm:ss.fff ") + "rtc biggest frame " + frameMax / 1024 + " KB, pacer queue max " + queueMax + " packets, nacks " + NacksReceived + ", resent " + Resent + Environment.NewLine); } catch (System.IO.IOException) { }
                frameMax = 0; queueMax = 0;
            }
        }

        /// <summary>16-bit little-endian stereo PCM at 48 kHz, any length: sent as 20 ms Opus packets</summary>
        public void Audio(byte[] b, int off, int len, int rate)
        {
            if (!Connected || closed || rate != 48000) return;
            for (int i = 0; i + 1 < len; i += 2)
            {
                pcm[pcmFill++] = (short)(b[off + i] | b[off + i + 1] << 8);
                if (pcmFill == pcm.Length)
                {
                    pcmFill = 0;
#pragma warning disable 618
                    int n = opus.Encode(pcm, 0, 960, opusOut, 0, opusOut.Length);
#pragma warning restore 618
                    if (n <= 0) continue;
                    byte[] packet = new byte[n];
                    Buffer.BlockCopy(opusOut, 0, packet, 0, n);
                    try { pc.SendAudio(960, packet); } catch (Exception) { }
                }
            }
        }

        volatile bool rawRtcp;

        /// <summary>
        /// wraps the SRTCP decryption of every stream, so each decrypted RTCP packet is read here in
        /// full: NACK -> the lost packets again, PLI / FIR -> a keyframe at once
        /// </summary>
        void HookRtcp()
        {
            if (rawRtcp) return;
            try
            {
                MediaStreamTrack track = pc.VideoLocalTrack;
                if (track == null) return;
                uint ssrc = track.Ssrc;
                foreach (MediaStream ms in new MediaStream[] { pc.AudioStream, pc.VideoStream })
                {
                    SecureContext ctx = ms == null ? null : ms.GetSecurityContext();
                    if (ctx == null) continue;
                    ProtectRtpPacket unprotect = ctx.UnprotectRtcpPacket;
                    ms.SetSecurityContext(ctx.ProtectRtpPacket, ctx.UnprotectRtpPacket, ctx.ProtectRtcpPacket,
                        (byte[] p, int l, out int outLen) =>
                        {
                            int r = unprotect(p, l, out outLen);
                            if (r == 0) OnRtcp(p, outLen, ssrc);
                            return r;
                        });
                    rawRtcp = true;
                }
            }
            catch (Exception) { } // another SIPSorcery version: the OnReceiveReport fallback stays
        }

        readonly List<ushort> lostScratch = new List<ushort>();
        internal const int MaxResendPerReport = 64;

        /// <summary>a private (home-network) IPv4 address; null or anything else is refused</summary>
        internal static bool IsLanEndPoint(IPEndPoint ep)
        {
            if (ep == null) return false;
            IPAddress a = ep.Address;
            if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
            return a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && Proto.IsLanV4(a.GetAddressBytes());
        }

        void OnRtcp(byte[] p, int len, uint ssrc)
        {
            try
            {
                bool key;
                lock (lostScratch)
                {
                    lostScratch.Clear();
                    ReadFeedback(p, len, ssrc, lostScratch, out key);
                    if (lostScratch.Count > 0)
                    {
                        NacksReceived++;
                        if (RtcGlue.LogFile != null && NacksReceived % 20 == 1) { try { System.IO.File.AppendAllText(RtcGlue.LogFile, DateTime.Now.ToString("HH:mm:ss.fff ") + "nack #" + NacksReceived + " first=" + lostScratch[0] + " count=" + lostScratch.Count + " seqNow=" + seq + " resent=" + Resent + " missed=" + MissedResends + Environment.NewLine); } catch (System.IO.IOException) { } }
                        // one RTCP packet may name many packets: at most MaxResendPerReport, so a
                        // viewer cannot make the PC flood its own Wi-Fi
                        for (int k = 0; k < lostScratch.Count && k < MaxResendPerReport; k++) Resend(lostScratch[k]);
                    }
                }
                if (key) keyRequest();
            }
            catch (Exception) { } // never break the receive loop
        }

        /// <summary>
        /// Reads a decrypted compound RTCP packet: sequence numbers in generic NACKs (RFC 4585, every
        /// FCI entry: PID + BLP) for our video SSRC go to <paramref name="lost"/>; PLI or FIR sets
        /// <paramref name="key"/>.
        /// </summary>
        internal static void ReadFeedback(byte[] p, int len, uint ssrc, List<ushort> lost, out bool key)
        {
            key = false;
            int pos = 0;
            while (pos + 4 <= len)
            {
                if ((p[pos] >> 6) != 2) return; // not RTCP version 2: stop
                int fmt = p[pos] & 0x1F, pt = p[pos + 1];
                int size = ((p[pos + 2] << 8 | p[pos + 3]) + 1) * 4;
                if (pos + size > len) return;
                if ((pt == 205 || pt == 206) && size >= 12)
                {
                    uint media = (uint)(p[pos + 8] << 24 | p[pos + 9] << 16 | p[pos + 10] << 8 | p[pos + 11]);
                    if (pt == 205 && fmt == 1 && media == ssrc)
                    {
                        for (int f = pos + 12; f + 4 <= pos + size; f += 4)
                        {
                            ushort pid = (ushort)(p[f] << 8 | p[f + 1]);
                            int blp = p[f + 2] << 8 | p[f + 3];
                            lost.Add(pid);
                            for (int b = 0; b < 16; b++) if ((blp & (1 << b)) != 0) lost.Add((ushort)(pid + b + 1));
                        }
                    }
                    else if (pt == 206 && fmt == 1 && media == ssrc) key = true; // PLI
                    else if (pt == 206 && fmt == 4)
                    {
                        // FIR: the media SSRC field is 0, the SSRC is in each FCI entry
                        for (int f = pos + 12; f + 8 <= pos + size; f += 8)
                            if ((uint)(p[f] << 24 | p[f + 1] << 16 | p[f + 2] << 8 | p[f + 3]) == ssrc) key = true;
                    }
                }
                pos += size;
            }
        }

        /// <summary>sends the queued packets, PaceBytesPerSecond on average (token bucket)</summary>
        void PaceLoop()
        {
            double tokens = PaceBurst;
            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            double last = 0;
            while (!closed)
            {
                double now = clock.Elapsed.TotalSeconds;
                tokens = Math.Min(PaceBurst, tokens + (now - last) * PaceBytesPerSecond);
                last = now;
                bool waited = false;
                lock (sendLock)
                {
                    while (paceQueue.Count > 0 && tokens > 0)
                    {
                        int slot = paceQueue.Dequeue();
                        byte[] p = sentPayload[slot];
                        try { pc.SendRtpRaw(SDPMediaTypesEnum.video, p, sentTs[slot], sentMarker[slot] ? 1 : 0, VideoPt, sentSeq[slot]); } catch (Exception) { }
                        tokens -= p.Length + 40; // + RTP / SRTP / UDP / IP headers
                    }
                    waited = paceQueue.Count > 0;
                }
                if (waited) System.Threading.Thread.Sleep(1); // the app runs timeBeginPeriod(1)
                else paceWake.WaitOne(100);
            }
        }

        void Resend(ushort s)
        {
            lock (sendLock)
            {
                int slot = s % Keep;
                if (sentPayload[slot] == null || sentSeq[slot] != s) { MissedResends++; return; } // too old by now
                try { pc.SendRtpRaw(SDPMediaTypesEnum.video, sentPayload[slot], sentTs[slot], sentMarker[slot] ? 1 : 0, VideoPt, s); Resent++; }
                catch (Exception) { }
            }
        }

        public void Dispose()
        {
            closed = true;
            Connected = false;
            paceWake.Set();
            try { pc.Close("viewer left"); } catch (Exception) { }
        }

        void Send(string json)
        {
            if (!closed) sendText(json);
        }

        // ---- pure helpers (tested) ----------------------------------------------------------

        /// <summary>
        /// "candidate:... 1 udp 2113937663 <address> <port> typ host ..." -> true for a private
        /// IPv4 address (home network) and for an mDNS name "....local", which is how Safari and
        /// Chrome hide their address (it only resolves on the local network). Public IPv6
        /// addresses, other names and anything malformed are dropped.
        /// </summary>
        internal static bool IsLanCandidate(string candidate)
        {
            if (string.IsNullOrEmpty(candidate)) return false;
            string[] f = candidate.Split(' ');
            if (f.Length < 8) return false;
            if (f[4].EndsWith(".local", StringComparison.OrdinalIgnoreCase) && f[4].Length <= 64 && f[4].IndexOf(':') < 0) return true;
            IPAddress a;
            if (!IPAddress.TryParse(f[4], out a) || a.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
            return Proto.IsLanV4(a.GetAddressBytes());
        }

        /// <summary>SDP without candidate lines for addresses outside the home network</summary>
        internal static string FilterSdp(string sdp)
        {
            StringBuilder sb = new StringBuilder();
            foreach (string line in sdp.Split('\n'))
            {
                string l = line.TrimEnd('\r');
                if (l.Length == 0) continue;
                if (l.StartsWith("a=candidate:") && !IsLanCandidate(l.Substring(2))) continue;
                sb.Append(l).Append("\r\n");
            }
            return sb.ToString();
        }

        /// <summary>
        /// RTP payloads for one frame (RFC 6184, packetization-mode 1): a NAL unit that fits goes
        /// alone, a larger one as FU-A fragments. Access unit delimiters are left out.
        /// </summary>
        internal static List<byte[]> Packetize(List<ArraySegment<byte>> nals, int max)
        {
            List<byte[]> outp = new List<byte[]>();
            foreach (ArraySegment<byte> n in nals)
            {
                if (n.Count == 0) continue;
                byte header = n.Array[n.Offset];
                int type = header & 0x1F;
                if (type == 9) continue; // AUD
                if (n.Count <= max)
                {
                    byte[] p = new byte[n.Count];
                    Buffer.BlockCopy(n.Array, n.Offset, p, 0, n.Count);
                    outp.Add(p);
                    continue;
                }
                byte indicator = (byte)((header & 0xE0) | 28); // FU-A, same F and NRI bits
                int pos = 1, chunk = max - 2;
                while (pos < n.Count)
                {
                    int l = Math.Min(chunk, n.Count - pos);
                    byte[] p = new byte[l + 2];
                    p[0] = indicator;
                    p[1] = (byte)((pos == 1 ? 0x80 : 0) | (pos + l == n.Count ? 0x40 : 0) | type); // S, E, type
                    Buffer.BlockCopy(n.Array, n.Offset + pos, p, 2, l);
                    outp.Add(p);
                    pos += l;
                }
            }
            return outp;
        }

        /// <summary>announces NACK, PLI and FIR for the H.264 payload type, so the browser asks for
        /// lost packets and for keyframes (SIPSorcery's offer only had transport-cc)</summary>
        internal static string AddVideoFeedback(string sdp)
        {
            string line = "a=rtpmap:96 H264/90000\r\n";
            int i = sdp.IndexOf(line, StringComparison.Ordinal);
            if (i < 0 || sdp.IndexOf("a=rtcp-fb:96 nack\r\n", StringComparison.Ordinal) >= 0) return sdp;
            return sdp.Insert(i + line.Length, "a=rtcp-fb:96 nack\r\na=rtcp-fb:96 nack pli\r\na=rtcp-fb:96 ccm fir\r\n");
        }

        internal static string Json(string s)
        {
            if (s == null) return "null";
            StringBuilder sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }
    }
}
