// Cast to an iPad (or any tablet) through its web browser: no app to install there.
//
//   http://<this PC>:47900/      the page (web/ipad.html, embedded in the exe)
//   ws://<this PC>:47900/ws      the stream and the touch input
//   https:// and wss:// on the same port (the first byte tells TLS from plain HTTP), with a
//   certificate from this PC's own CA (LocalTls.cs); /ca.cer serves that CA for the iPad to
//   install once. Only HTTPS lets Safari keep the screen on (Screen Wake Lock).
//
// Server -> page:  text   {"t":"config","codec":..,"w":..,"h":..,"rate":..,"control":..} / {"t":"ok"} / {"t":"badcode"}
//                  binary 0x01 + fMP4 init segment, 0x02 / 0x04 + fMP4 fragment of one frame (0x04 = keyframe),
//                         0x03 + 20 ms of 16-bit little-endian stereo PCM
// Page -> server:  binary 0x10 + the 6-digit pairing code (must be the first message),
//                         0x11 = please send a keyframe, 1..5 = input (see RemoteInput.cs)
//
// Safety: listens only while the iPad mode is on; private IPv4 addresses and this PC only;
// the Host header must be one of this PC's IP addresses (no names: a website using DNS
// rebinding cannot reach the server from the iPad's browser); the WebSocket must come from the
// page this server served (Origin check); the page must send the code shown in the PC window,
// at most one try every 2 s and 5 wrong codes lock everyone out for 5 minutes and make a new
// code (AuthThrottle); at most 24 connections at a time; input is replayed only while
// "Allow control" is ticked, and buttons still held are released when the viewer leaves.
// Plain http:// is not encrypted; https:// (LocalTls.cs) is.
//
// Plain TcpListener with a minimal HTTP/WebSocket implementation, because HttpListener needs
// an administrator-made URL reservation for anything but localhost.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace TVCast
{
    /// <summary>
    /// Guessing the 6-digit code: at most one try every 2 s, and after 5 wrong codes nobody can
    /// try for 5 minutes (the caller then makes a new code). Counted for all addresses together:
    /// an attacker on the Wi-Fi can use many addresses. 5 guesses per 5 minutes against a
    /// million codes is about 95 years for a 50 % chance. Pure: tested in tests/cs.
    /// </summary>
    sealed class AuthThrottle
    {
        public const int MaxFailures = 5;
        public const long DelayMs = 2000, LockMs = 5 * 60 * 1000;
        int failures;
        long nextTry, lockedUntil;

        /// <summary>0 when a try is allowed now, else how long to wait</summary>
        public long WaitMs(long now)
        {
            return Math.Max(0, Math.Max(nextTry, lockedUntil) - now);
        }

        /// <summary>a wrong code; true when this one started the lock (time for a new code)</summary>
        public bool Fail(long now)
        {
            nextTry = now + DelayMs;
            if (++failures < MaxFailures) return false;
            failures = 0;
            lockedUntil = now + LockMs;
            return true;
        }

        public void Success()
        {
            failures = 0;
        }
    }

    sealed class WebCast : IDisposable
    {
        /// <summary>47900; --port N for testing next to a running copy</summary>
        public static int PORT = 47900;
        const int MaxHeader = 8192;
        /// <summary>pictures waiting to be sent before the backlog is dropped: 0.5 s at 60 fps.
        /// 6 (0.1 s) dropped on every short Wi-Fi hiccup, and a dropped keyframe meant the next
        /// one was needed, which was dropped again: the picture stopped while sound went on.</summary>
        const int MaxQueuedFrames = 30;
        static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(20);

        TcpListener listener;
        Thread acceptThread;
        volatile bool on;
        readonly object gate = new object();
        volatile WebClient active;
        readonly AuthThrottle throttle = new AuthThrottle();
        int connections;
        const int MaxConnections = 24;

        public volatile bool AllowControl = true;
        /// <summary>the 6-digit code the page must send; changes after 5 wrong tries</summary>
        public string Code { get; private set; }
        public event Action CodeChanged;
        /// <summary>set by the caster: the next encoded frame should be a keyframe</summary>
        public Action KeyRequest;
        public volatile string Viewer = "";
        /// <summary>what happened last (disconnect reason, problems the page reported), for the window</summary>
        public volatile string LastEvent = "";

        // stream state (encoder and audio threads)
        byte[] sps, pps;
        volatile int width, height, audioRate = 48000;

        /// <param name="code">the code saved last time, so a viewer that remembers it can come back
        /// without typing; a new one is made when it is missing or not 6 digits</param>
        public WebCast(string code)
        {
            Code = IsCode(code) ? code : NewCode();
        }

        public static bool IsCode(string c)
        {
            if (c == null || c.Length != 6) return false;
            foreach (char ch in c) if (ch < '0' || ch > '9') return false;
            return true;
        }

        public void Start()
        {
            listener = new TcpListener(IPAddress.Any, PORT);
            listener.Start();
            on = true;
            acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "web accept" };
            acceptThread.Start();
        }

        public void Dispose()
        {
            on = false;
            try { listener.Stop(); } catch (SocketException) { }
            WebClient c;
            lock (gate) { c = active; active = null; }
            if (c != null) c.Close();
        }

        /// <summary>
        /// http://address:port of this PC for the window: a phone's USB tethering first (the cable:
        /// no Wi-Fi stalls), then the home network, then a Windows "Mobile hotspot" if it is on,
        /// so an iPad joined directly to the PC (bypassing a busy router) knows where to go.
        /// Link-local addresses (adapters without a network) are left out.
        /// </summary>
        public static string LocalUrl()
        {
            List<string> usb = new List<string>(), withGateway = new List<string>(), other = new List<string>();
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                IPInterfaceProperties p = ni.GetIPProperties();
                bool cable = IsUsbTethering(ni.Description);
                foreach (UnicastIPAddressInformation a in p.UnicastAddresses)
                {
                    byte[] b = a.Address.GetAddressBytes();
                    if (a.Address.AddressFamily != AddressFamily.InterNetwork || !Proto.IsLanV4(b) || b[0] == 169) continue;
                    string url = "http://" + a.Address + ":" + PORT;
                    if (cable) usb.Add(url + " " + Texts.UsbCable);
                    else (p.GatewayAddresses.Count > 0 ? withGateway : other).Add(url);
                }
            }
            usb.AddRange(withGateway);
            usb.AddRange(other);
            return usb.Count > 0 ? string.Join("  ·  ", usb.ToArray()) : "http://<PC IP>:" + PORT;
        }

        /// <summary>the address is on this PC's USB tethering network (a phone on the cable)</summary>
        public static bool IsUsbAddress(IPAddress target)
        {
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || !IsUsbTethering(ni.Description)) continue;
                    foreach (UnicastIPAddressInformation a in ni.GetIPProperties().UnicastAddresses)
                        if (a.Address.AddressFamily == AddressFamily.InterNetwork && a.IPv4Mask != null
                            && SameSubnet(target, a.Address, a.IPv4Mask)) return true;
                }
            }
            catch (NetworkInformationException) { }
            return false;
        }

        /// <summary>a phone's USB tethering adapter is up and has an IPv4 address</summary>
        public static bool HasUsbTethering()
        {
            return UpIPv4(true).Count > 0;
        }

        /// <summary>this PC's IPv4 addresses on adapters that are up (only USB tethering ones, or all)</summary>
        public static List<IPAddress> UpIPv4(bool usbOnly)
        {
            List<IPAddress> list = new List<IPAddress>();
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (usbOnly && !IsUsbTethering(ni.Description)) continue;
                    foreach (UnicastIPAddressInformation a in ni.GetIPProperties().UnicastAddresses)
                        if (a.Address.AddressFamily == AddressFamily.InterNetwork) list.Add(a.Address);
                }
            }
            catch (NetworkInformationException) { }
            return list;
        }

        /// <summary>the gateways of the USB tethering networks: the phone on the cable</summary>
        public static List<IPAddress> UsbGateways()
        {
            List<IPAddress> list = new List<IPAddress>();
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || !IsUsbTethering(ni.Description)) continue;
                    foreach (GatewayIPAddressInformation g in ni.GetIPProperties().GatewayAddresses)
                        if (g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))
                            list.Add(g.Address);
                }
            }
            catch (NetworkInformationException) { }
            return list;
        }

        internal static bool SameSubnet(IPAddress x, IPAddress y, IPAddress mask)
        {
            if (x == null || x.AddressFamily != AddressFamily.InterNetwork) return false;
            byte[] a = x.GetAddressBytes(), b = y.GetAddressBytes(), m = mask.GetAddressBytes();
            for (int i = 0; i < 4; i++) if ((a[i] & m[i]) != (b[i] & m[i])) return false;
            return true;
        }

        /// <summary>a phone's USB tethering shows up in Windows as a "Remote NDIS" network adapter
        /// (Samsung: "SAMSUNG Mobile USB Remote NDIS Network Device")</summary>
        internal static bool IsUsbTethering(string adapterDescription)
        {
            if (string.IsNullOrEmpty(adapterDescription)) return false;
            string d = adapterDescription.ToLowerInvariant();
            return d.Contains("remote ndis") || d.Contains("rndis");
        }

        static string NewCode()
        {
            byte[] b = new byte[4];
            using (RNGCryptoServiceProvider r = new RNGCryptoServiceProvider()) r.GetBytes(b);
            return (BitConverter.ToUInt32(b, 0) % 1000000).ToString("000000");
        }

        // ---- from the caster -----------------------------------------------------------------

        public void SetSize(int w, int h)
        {
            width = w;
            height = h;
        }

        public void SetAudioRate(int rate)
        {
            audioRate = rate;
        }

        public void Video(byte[] data, int len, bool key, long pts)
        {
            if (key)
            {
                foreach (ArraySegment<byte> n in Fmp4.SplitNals(data, len))
                {
                    int t = Fmp4.NalType(n);
                    if (t == 7) sps = Copy(n);
                    else if (t == 8) pps = Copy(n);
                }
            }
            WebClient c = active;
            if (c == null || !c.Ready) return;
            if (!c.InitSent)
            {
                // a viewer starts with the init segment and a keyframe
                if (!key || sps == null || pps == null) { RequestKey(); return; }
                c.SendText(string.Format("{{\"t\":\"config\",\"codec\":\"{0}\",\"w\":{1},\"h\":{2},\"rate\":{3},\"control\":{4}}}",
                    Fmp4.CodecString(sps), width, height, audioRate, AllowControl ? "true" : "false"));
                c.SendBinary(0x01, Fmp4.Init(sps, pps, width, height));
                c.InitSent = true;
            }
            uint duration = FrameDuration(c.LastPts, pts);
            c.LastPts = pts;
            if (RtcOn(c))
            {
                try { RtcGlue.Video(c.Rtc, data, len, duration); } catch (Exception) { }
                return;
            }
            if (c.SkipToKey && !key) return;
            byte[] sample = Fmp4.ToLengthPrefixed(Fmp4.SplitNals(data, len));
            byte[] frag = Fmp4.Fragment(++c.Sequence, c.DecodeTime, duration, key, sample);
            c.DecodeTime += duration;
            if (!c.EnqueueFrame(frag, key)) RequestKey();
        }

        public void Audio(byte[] pcm, int off, int len)
        {
            WebClient c = active;
            if (c == null || !c.InitSent) return;
            if (RtcOn(c))
            {
                try { RtcGlue.Audio(c.Rtc, pcm, off, len, audioRate); } catch (Exception) { }
                return;
            }
            c.AddAudio(pcm, off, len, audioRate / 50 * 4); // 20 ms per message
        }

        /// <summary>
        /// How long a frame stays on screen, in Timescale units: the real time since the previous
        /// frame (capture pts, microseconds), 1/120 s .. 0.1 s. The encoder skips frames when the
        /// screen does not change, so 40-50 fps is common; a fixed 1/60 made the iPad play faster
        /// than frames came and run out of picture every few seconds. A still screen ends at
        /// 0.1 s: the player then waits at the end of its buffer and goes on with the next frame.
        /// </summary>
        internal static uint FrameDuration(long lastPts, long pts)
        {
            if (lastPts < 0) return Fmp4.Timescale / 60;
            long us = Math.Max(1000000 / 120, Math.Min(100000, pts - lastPts));
            return (uint)((us * Fmp4.Timescale + 500000) / 1000000);
        }

        void RequestKey()
        {
            Action k = KeyRequest;
            if (k != null) k();
        }

        static byte[] Copy(ArraySegment<byte> s)
        {
            byte[] b = new byte[s.Count];
            Buffer.BlockCopy(s.Array, s.Offset, b, 0, s.Count);
            return b;
        }

        // ---- connections ---------------------------------------------------------------------

        void AcceptLoop()
        {
            while (on)
            {
                TcpClient tcp;
                try { tcp = listener.AcceptTcpClient(); }
                catch (SocketException) { if (on) continue; return; }
                catch (ObjectDisposedException) { return; }
                IPAddress from = ((IPEndPoint)tcp.Client.RemoteEndPoint).Address;
                if (!IPAddress.IsLoopback(from) && !Proto.IsLanV4(from.GetAddressBytes())) { tcp.Close(); continue; }
                if (Interlocked.Increment(ref connections) > MaxConnections)
                {
                    Interlocked.Decrement(ref connections); // a flood of connections: no thread for them
                    tcp.Close();
                    continue;
                }
                Thread t = new Thread(delegate()
                {
                    // whatever goes wrong with one connection must not take the app down
                    try { Serve(tcp, from); }
                    catch (Exception) { try { tcp.Close(); } catch (Exception) { } }
                    finally { Interlocked.Decrement(ref connections); }
                }) { IsBackground = true, Name = "web client" };
                t.Start();
            }
        }

        void Serve(TcpClient tcp, IPAddress from)
        {
            tcp.NoDelay = true;
            tcp.ReceiveTimeout = 5000;
            tcp.SendTimeout = 5000;
            Stream s = tcp.GetStream();
            try
            {
                // a TLS handshake starts with 0x16: the same port serves https/wss and http/ws
                byte[] first = new byte[1];
                if (tcp.Client.Receive(first, 0, 1, SocketFlags.Peek) == 1 && first[0] == 0x16)
                {
                    SslStream ssl = new SslStream(s, false);
                    IPAddress local = ((IPEndPoint)tcp.Client.LocalEndPoint).Address;
                    try { ssl.AuthenticateAsServer(LocalTls.ServerCertificate(local), false, SslProtocols.Tls12, false); }
                    catch (AuthenticationException) { tcp.Close(); return; } // the iPad does not trust the CA (yet)
                    s = ssl;
                }
                Dictionary<string, string> h;
                string path = ReadRequest(s, out h);
                if (path == null) { tcp.Close(); return; }
                string hostHeader;
                h.TryGetValue("host", out hostHeader);
                if (!IsOwnAddress(hostHeader)) { tcp.Close(); return; } // a name (DNS rebinding) or someone else's address
                if (path == "/ca.cer")
                {
                    // the iPad offers to install it as a profile (Settings -> Profile Downloaded)
                    byte[] cer = LocalTls.CaCertificate();
                    WriteHead(s, "200 OK", "application/x-x509-ca-cert\r\nContent-Disposition: attachment; filename=\"TVCast-local-CA.cer\"", cer.Length);
                    s.Write(cer, 0, cer.Length);
                    s.Flush();
                    tcp.Close();
                    return;
                }
                if (path == "/manifest.webmanifest" || path == "/icon.png")
                {
                    // what "Add to Home Screen" uses: name, icon, full screen without Safari's bars
                    byte[] body = path == "/icon.png" ? Icon() : Encoding.UTF8.GetBytes(Manifest);
                    WriteHead(s, "200 OK", path == "/icon.png" ? "image/png" : "application/manifest+json", body.Length);
                    s.Write(body, 0, body.Length);
                    s.Flush();
                    tcp.Close();
                    return;
                }
                if (path == "/" || path.StartsWith("/?"))
                {
                    byte[] page = Page();
                    WriteHead(s, "200 OK", "text/html; charset=utf-8", page.Length);
                    s.Write(page, 0, page.Length);
                    tcp.Close();
                    return;
                }
                string key, upgrade, origin, host;
                h.TryGetValue("sec-websocket-key", out key);
                h.TryGetValue("upgrade", out upgrade);
                h.TryGetValue("origin", out origin);
                h.TryGetValue("host", out host);
                if (path != "/ws" || key == null || upgrade == null || upgrade.ToLowerInvariant() != "websocket"
                    || !SameOrigin(origin, host))
                {
                    byte[] nf = Encoding.ASCII.GetBytes("not found");
                    WriteHead(s, "404 Not Found", "text/plain", nf.Length);
                    s.Write(nf, 0, nf.Length);
                    tcp.Close();
                    return;
                }
                string accept = WebSocketAccept(key);
                byte[] resp = Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n");
                s.Write(resp, 0, resp.Length);
                RunSocket(new WebClient(tcp, s, from));
            }
            catch (IOException) { tcp.Close(); }
            catch (SocketException) { tcp.Close(); }
            catch (ObjectDisposedException) { }
            catch (CryptographicException) { tcp.Close(); } // making the certificate failed: plain HTTP still works
        }

        void RunSocket(WebClient c)
        {
            c.Tcp.ReceiveTimeout = (int)HelloTimeout.TotalMilliseconds;
            byte[] msg;
            int op = c.ReadMessage(out msg);
            if (op != 2 || msg.Length != 7 || msg[0] != 0x10) { c.Close(); return; }
            bool ok, newCode = false;
            long wait;
            lock (gate)
            {
                long now = Clock.Ms();
                wait = throttle.WaitMs(now);
                ok = wait == 0 && CodeMatches(msg);
                if (wait == 0 && !ok && throttle.Fail(now)) { Code = NewCode(); newCode = true; }
                if (ok) throttle.Success();
            }
            if (!ok)
            {
                // a guess while locked is not even compared, so it tells nothing about the code
                c.SendNow(1, Encoding.UTF8.GetBytes(wait > 0
                    ? "{\"t\":\"locked\",\"s\":" + ((wait + 999) / 1000) + "}" : "{\"t\":\"badcode\"}"));
                // a try while locked keeps the lock message in the window
                if (wait == 0) LastEvent = string.Format(newCode ? Texts.CodeLocked : Texts.CodeWrong, c.From);
                c.Close();
                if (newCode && CodeChanged != null) CodeChanged();
                return;
            }
            WebClient old;
            lock (gate) { old = active; active = c; }
            // one viewer at a time: the newest wins. The old page is told so it does not connect
            // again (two tabs, or a phone and an iPad, would otherwise keep pushing each other out)
            if (old != null) { old.ClosedByUs = true; old.SendText("{\"t\":\"replaced\"}"); old.Close(); }
            LastEvent = "";
            c.Tcp.ReceiveTimeout = 0;
            c.StartSender();
            c.SendText("{\"t\":\"ok\"}");
            Viewer = c.From.ToString();
            c.Ready = true;
            RequestKey();
            try
            {
                while (on)
                {
                    op = c.ReadMessage(out msg);
                    if (op < 0) break;
                    if (op == 1)
                    {
                        string t = Encoding.UTF8.GetString(msg);
                        if (t.StartsWith("{")) { Signal(c, t); continue; }
                        // the page reports a problem (no codec support, playback error, ...)
                        LastEvent = "iPad: " + (t.Length > 500 ? t.Substring(0, 500) : t);
                        continue;
                    }
                    if (op != 2 || msg.Length == 0) continue;
                    if (msg[0] == 0x11) { c.SkipToKey = true; RequestKey(); continue; }
                    if (!AllowControl) continue;
                    InputEvent e = RemoteInput.Decode(msg, msg.Length);
                    if (e != null) RemoteInput.Apply(e);
                }
            }
            finally
            {
                try { RtcGlue.Dispose(c.Rtc); } catch (Exception) { }
                c.Rtc = null;
                RemoteInput.ReleaseButtons(); // a drag cut off by the Wi-Fi must not leave a button held
                lock (gate) { if (active == c) { active = null; Viewer = ""; } }
                if (c.WriteFailed) LastEvent = Texts.ViewerSlow;
                else if (!c.ClosedByUs) LastEvent = Texts.ViewerLeft;
                c.Close();
            }
        }

        /// <summary>WebRTC signalling from the page (only after the pairing code): rtc-hello,
        /// answer, ice. Any failure leaves the viewer on the WebSocket path.</summary>
        void Signal(WebClient c, string json)
        {
            Dictionary<string, object> m;
            try { m = new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>; }
            catch (ArgumentException) { return; }
            if (m == null || !m.ContainsKey("t")) return;
            string t = m["t"] as string;
            try
            {
                if (t == "rtc-hello" && c.Rtc == null)
                {
                    // a few tries per connection (each one opens a UDP socket)
                    if (++c.RtcStarts > 3) { c.SendText("{\"t\":\"rtc-off\"}"); return; }
                    if (!RtcGlue.LibraryPresent) { c.SendText("{\"t\":\"rtc-off\"}"); return; }
                    WebClient viewer = c;
                    c.Rtc = RtcGlue.Start(delegate(string s) { viewer.SendText(s); }, RequestKey);
                }
                else if (t == "answer" && c.Rtc != null && m.ContainsKey("sdp")) RtcGlue.Answer(c.Rtc, m["sdp"] as string);
                else if (t == "ice" && c.Rtc != null && m.ContainsKey("c"))
                    RtcGlue.Ice(c.Rtc, m["c"] as string, m.ContainsKey("m") ? m["m"] as string : "0", m.ContainsKey("i") ? Convert.ToInt32(m["i"]) : 0);
            }
            catch (Exception e)
            {
                // the lib folder is missing or WebRTC failed: the WebSocket path keeps going
                LastEvent = "WebRTC: " + e.GetType().Name + " " + e.Message;
                try { RtcGlue.Dispose(c.Rtc); } catch (Exception) { }
                c.Rtc = null;
                c.SendText("{\"t\":\"rtc-off\"}");
            }
        }

        static bool RtcOn(WebClient c)
        {
            if (c.Rtc == null) return false;
            try { return RtcGlue.Connected(c.Rtc); } catch (Exception) { return false; }
        }

        bool CodeMatches(byte[] msg)
        {
            string code = Code;
            int diff = 0;
            for (int i = 0; i < 6; i++) diff |= msg[1 + i] ^ code[i];
            return diff == 0;
        }

        // ---- HTTP / WebSocket helpers (pure parts are unit tested) ------------------------------

        /// <summary>request path, or null if the request is not a plain GET; header names lower case</summary>
        internal static string ReadRequest(Stream s, out Dictionary<string, string> headers)
        {
            headers = new Dictionary<string, string>();
            StringBuilder head = new StringBuilder();
            while (head.Length < 4 || head[head.Length - 1] != '\n' || head[head.Length - 2] != '\r'
                   || head[head.Length - 3] != '\n' || head[head.Length - 4] != '\r')
            {
                int b = s.ReadByte();
                if (b < 0 || head.Length >= MaxHeader) return null;
                head.Append((char)b);
            }
            return ParseRequest(head.ToString(), headers);
        }

        internal static string ParseRequest(string text, Dictionary<string, string> headers)
        {
            string[] lines = text.Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] first = lines[0].Split(' ');
            if (first.Length != 3 || first[0] != "GET" || !first[2].StartsWith("HTTP/1.")) return null;
            for (int i = 1; i < lines.Length; i++)
            {
                int c = lines[i].IndexOf(':');
                if (c > 0) headers[lines[i].Substring(0, c).Trim().ToLowerInvariant()] = lines[i].Substring(c + 1).Trim();
            }
            return first[1];
        }

        /// <summary>the WebSocket must be opened by our own page: Origin http(s)://host[:port] equal to Host</summary>
        internal static bool SameOrigin(string origin, string host)
        {
            if (origin == null || host == null) return false;
            return string.Equals(origin, "http://" + host, StringComparison.OrdinalIgnoreCase)
                || string.Equals(origin, "https://" + host, StringComparison.OrdinalIgnoreCase);
        }

        internal static string WebSocketAccept(string key)
        {
            using (SHA1 sha = SHA1.Create())
                return Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(key.Trim() + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        }

        /// <summary>the page only talks to this server; no framing (clickjacking), no forms, no
        /// other origins; inline script and style because the page is one file</summary>
        const string Csp = "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src 'self'; "
            + "media-src 'self' blob:; connect-src 'self' ws: wss:; manifest-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

        /// <summary>The Host header must be an IPv4 address of this PC's kind (private or
        /// loopback), with or without a port. A host name would mean a website reached us through
        /// DNS rebinding: refused.</summary>
        internal static bool IsOwnAddress(string host)
        {
            if (string.IsNullOrEmpty(host) || host.Length > 64) return false;
            int colon = host.IndexOf(':');
            string ip = colon >= 0 ? host.Substring(0, colon) : host;
            if (colon >= 0)
            {
                int port;
                if (!int.TryParse(host.Substring(colon + 1), out port) || port < 1 || port > 65535) return false;
            }
            string[] parts = ip.Split('.');
            if (parts.Length != 4) return false; // also refuses short forms like "10.1" that parse as IPs
            IPAddress a;
            if (!IPAddress.TryParse(ip, out a) || a.AddressFamily != AddressFamily.InterNetwork) return false;
            return IPAddress.IsLoopback(a) || Proto.IsLanV4(a.GetAddressBytes());
        }

        static void WriteHead(Stream s, string status, string type, int length)
        {
            byte[] b = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\nContent-Type: " + type + "\r\nContent-Length: " + length
                + "\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nX-Frame-Options: DENY\r\nReferrer-Policy: no-referrer"
                + "\r\nContent-Security-Policy: " + Csp + "\r\nConnection: close\r\n\r\n");
            s.Write(b, 0, b.Length);
        }

        static byte[] page, icon;

        const string Manifest = "{\"name\":\"CastLink\",\"short_name\":\"CastLink\",\"start_url\":\"/\",\"scope\":\"/\","
            + "\"display\":\"standalone\",\"background_color\":\"#000000\",\"theme_color\":\"#000000\","
            + "\"icons\":[{\"src\":\"/icon.png\",\"sizes\":\"180x180\",\"type\":\"image/png\"}]}";

        /// <summary>the home-screen icon (iOS wants a PNG): a white screen on blue, drawn once</summary>
        internal static byte[] Icon()
        {
            if (icon == null) icon = AppIcon.Png(180, true); // the app's icon; iOS rounds the corners itself
            return icon;
        }

        internal static byte[] Page()
        {
            if (page == null)
            {
                using (Stream r = Assembly.GetExecutingAssembly().GetManifestResourceStream("TVCast.ipad.html"))
                {
                    MemoryStream m = new MemoryStream();
                    if (r != null) r.CopyTo(m);
                    page = m.ToArray();
                }
            }
            return page;
        }

        /// <summary>WebSocket frame header for a server message (never masked)</summary>
        internal static byte[] FrameHeader(int opcode, long length)
        {
            if (length < 126) return new byte[] { (byte)(0x80 | opcode), (byte)length };
            if (length < 65536) return new byte[] { (byte)(0x80 | opcode), 126, (byte)(length >> 8), (byte)length };
            byte[] h = new byte[10];
            h[0] = (byte)(0x80 | opcode);
            h[1] = 127;
            for (int i = 0; i < 8; i++) h[2 + i] = (byte)(length >> (56 - 8 * i));
            return h;
        }

        /// <summary>text messages (WebRTC offer/answer, reports) may be this long</summary>
        public const int MaxText = 16384;

        /// <summary>One message from the page. Returns the opcode (1 text, 2 binary) or -1 when the
        /// connection is over. Pages must mask; anything larger than RemoteInput.MaxMessage ends it.</summary>
        internal static int ReadFrame(Stream s, out byte[] payload, Action<int, byte[]> control)
        {
            payload = null;
            while (true)
            {
                int b0 = s.ReadByte(), b1 = s.ReadByte();
                if (b0 < 0 || b1 < 0) return -1;
                int op = b0 & 0x0F;
                bool fin = (b0 & 0x80) != 0, masked = (b1 & 0x80) != 0;
                long len = b1 & 0x7F;
                if (len == 126) len = ReadN(s, 2);
                else if (len == 127) return -1;
                if (!masked || !fin || len < 0 || len > (op == 1 ? MaxText : RemoteInput.MaxMessage)) return -1;
                byte[] mask = new byte[4];
                if (!ReadAll(s, mask)) return -1;
                byte[] data = new byte[len];
                if (!ReadAll(s, data)) return -1;
                for (int i = 0; i < data.Length; i++) data[i] ^= mask[i & 3];
                if (op == 8) return -1;                                  // close
                if (op == 9 || op == 10) { if (control != null) control(op, data); continue; } // ping / pong
                if (op != 1 && op != 2) return -1;                       // continuation frames: not used by browsers here
                payload = data;
                return op;
            }
        }

        static long ReadN(Stream s, int n)
        {
            long v = 0;
            for (int i = 0; i < n; i++)
            {
                int b = s.ReadByte();
                if (b < 0) return -1;
                v = v << 8 | (uint)b;
            }
            return v;
        }

        static bool ReadAll(Stream s, byte[] b)
        {
            int got = 0;
            while (got < b.Length)
            {
                int n = s.Read(b, got, b.Length - got);
                if (n <= 0) return false;
                got += n;
            }
            return true;
        }

        // ---- one viewer ------------------------------------------------------------------------

        sealed class WebClient
        {
            public readonly TcpClient Tcp;
            public readonly IPAddress From;
            readonly Stream stream;
            readonly Queue<byte[]> queue = new Queue<byte[]>();
            int queuedFrames;
            volatile bool closed;
            /// <summary>sending stalled for 5 s or the connection broke while sending</summary>
            public volatile bool WriteFailed;
            /// <summary>closed by this server (a newer viewer, the cast stopped)</summary>
            public volatile bool ClosedByUs;
            /// <summary>WebRTC connections this viewer has asked for</summary>
            public int RtcStarts;
            byte[] audioBuf = new byte[0];
            int audioFill;

            public volatile bool Ready, InitSent;
            /// <summary>frames were dropped: send nothing until the next keyframe</summary>
            public volatile bool SkipToKey;
            public uint Sequence;
            /// <summary>the WebRTC link (RtcLink, kept as object, see RtcGlue) or null</summary>
            public volatile object Rtc;
            public ulong DecodeTime;
            public long LastPts = -1;

            public WebClient(TcpClient tcp, Stream stream, IPAddress from)
            {
                Tcp = tcp;
                From = from;
                this.stream = stream; // the TCP stream, or TLS over it
            }

            public int ReadMessage(out byte[] msg)
            {
                try
                {
                    return ReadFrame(stream, out msg, delegate(int op, byte[] data) { if (op == 9) Enqueue(Frame(10, data)); });
                }
                catch (IOException) { msg = null; return -1; }
                catch (ObjectDisposedException) { msg = null; return -1; }
            }

            public void StartSender()
            {
                new Thread(SendLoop) { IsBackground = true, Name = "web send" }.Start();
            }

            public void SendText(string t)
            {
                byte[] b = Encoding.UTF8.GetBytes(t);
                Enqueue(Frame(1, b));
            }

            /// <summary>written at once, before the sender thread runs (the "wrong code" answer)</summary>
            public void SendNow(int opcode, byte[] payload)
            {
                byte[] f = Frame(opcode, payload);
                try { stream.Write(f, 0, f.Length); }
                catch (IOException) { }
            }

            public void SendBinary(byte type, byte[] body)
            {
                byte[] b = new byte[body.Length + 1];
                b[0] = type;
                Buffer.BlockCopy(body, 0, b, 1, body.Length);
                Enqueue(Frame(2, b));
            }

            /// <summary>false if the viewer fell behind and the backlog was dropped</summary>
            public bool EnqueueFrame(byte[] fragment, bool key)
            {
                byte[] b = new byte[fragment.Length + 1];
                b[0] = (byte)(key ? 0x04 : 0x02);
                Buffer.BlockCopy(fragment, 0, b, 1, fragment.Length);
                lock (queue)
                {
                    bool ok = true;
                    if (queuedFrames >= MaxQueuedFrames)
                    {
                        // The Wi-Fi cannot keep up: drop the queued pictures (sound and texts stay).
                        // A keyframe still waiting in the queue is kept unless a newer one is here:
                        // dropping it too meant asking for keyframe after keyframe and never
                        // getting one through.
                        byte[] lastKey = null;
                        Queue<byte[]> keep = new Queue<byte[]>();
                        foreach (byte[] q in queue)
                        {
                            if (!IsPicture(q)) keep.Enqueue(q);
                            else if (PayloadType(q) == 0x04) lastKey = q;
                        }
                        queue.Clear();
                        if (!key && lastKey != null) queue.Enqueue(lastKey);
                        foreach (byte[] q in keep) queue.Enqueue(q);
                        queuedFrames = !key && lastKey != null ? 1 : 0;
                        if (!key)
                        {
                            SkipToKey = true; // the pictures after a kept keyframe are gone: wait for the next one
                            Monitor.Pulse(queue);
                            return false;
                        }
                        ok = false; // this keyframe goes out, but ask for a fresher one
                    }
                    if (key) SkipToKey = false;
                    queue.Enqueue(Frame(2, b));
                    queuedFrames++;
                    Monitor.Pulse(queue);
                    return ok;
                }
            }

            public void AddAudio(byte[] pcm, int off, int len, int chunk)
            {
                if (audioBuf.Length != chunk + 1) { audioBuf = new byte[chunk + 1]; audioBuf[0] = 0x03; audioFill = 0; }
                while (len > 0)
                {
                    int n = Math.Min(len, chunk - audioFill);
                    Buffer.BlockCopy(pcm, off, audioBuf, 1 + audioFill, n);
                    audioFill += n; off += n; len -= n;
                    if (audioFill == chunk)
                    {
                        lock (queue) { if (queue.Count > 40) return; } // far behind: sound is dropped, not delayed
                        Enqueue(Frame(2, (byte[])audioBuf.Clone()));
                        audioFill = 0;
                    }
                }
            }

            // a queued picture (binary message 0x02 / 0x04): what gets dropped when behind
            static bool IsPicture(byte[] frame)
            {
                if ((frame[0] & 0x0F) != 2) return false;
                int t = PayloadType(frame);
                return t == 0x02 || t == 0x04;
            }

            // the type byte of a queued binary message (after the 2..10 byte frame header)
            static int PayloadType(byte[] frame)
            {
                int l = frame[1] & 0x7F;
                int at = l < 126 ? 2 : l == 126 ? 4 : 10;
                return at < frame.Length ? frame[at] : -1;
            }

            static byte[] Frame(int opcode, byte[] payload)
            {
                byte[] h = FrameHeader(opcode, payload.Length);
                byte[] f = new byte[h.Length + payload.Length];
                Buffer.BlockCopy(h, 0, f, 0, h.Length);
                Buffer.BlockCopy(payload, 0, f, h.Length, payload.Length);
                return f;
            }

            void Enqueue(byte[] frame)
            {
                lock (queue)
                {
                    queue.Enqueue(frame);
                    Monitor.Pulse(queue);
                }
            }

            void SendLoop()
            {
                while (!closed)
                {
                    byte[] f;
                    lock (queue)
                    {
                        while (queue.Count == 0 && !closed) Monitor.Wait(queue, 500);
                        if (closed) return;
                        f = queue.Dequeue();
                        if (IsPicture(f)) queuedFrames--;
                    }
                    try { stream.Write(f, 0, f.Length); }
                    catch (IOException) { WriteFailed = true; Close(); }
                    catch (ObjectDisposedException) { Close(); }
                }
            }

            public void Close()
            {
                closed = true;
                lock (queue) Monitor.PulseAll(queue);
                try { Tcp.Close(); } catch (SocketException) { }
            }
        }
    }
}
