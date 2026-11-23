// Sharing between PCs, the way the user starts it: the PC that shares its screen only switches
// sharing on and waits; the PC that shows it finds the sharing PCs and asks one of them to start.
//
// UDP port 47802, in the clear (like WHO / NAME on 47800; the cast itself is encrypted as always):
//   SHARE_WHO   30                          viewer -> broadcast  "who shares?"
//   SHARE_NAME  31 | UTF-8 name             sharer -> viewer     only while sharing is on
//   PULL        32 | UTF-8 viewer name      viewer -> sharer     "cast to me" (the sharer then casts
//                                                                to the viewer's port 47800 as before)
// The sharer accepts PULL only from private IPv4 addresses, at most twice a second, and only while
// its user has sharing switched on. The first time a viewer connects the sharer's user still confirms
// the pairing code, as with every new device.
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace TVCast
{
    sealed class ShareHost : IDisposable
    {
        public const int PORT = 47802;
        public const byte WHO = 30, NAME = 31, PULL = 32;

        readonly Socket socket;
        volatile bool running = true;
        long lastPull;
        public string Error;
        /// <summary>a viewer asked for the picture (called on the network thread)</summary>
        public Action<IPAddress, string> Pulled;

        public ShareHost()
        {
            try
            {
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.Bind(new IPEndPoint(IPAddress.Any, PORT));
            }
            catch (SocketException)
            {
                Error = string.Format(Texts.SharePortBusy, PORT);
                if (socket != null) socket.Close();
                running = false;
                return;
            }
            new Thread(Loop) { IsBackground = true, Name = "share" }.Start();
        }

        void Loop()
        {
            byte[] buf = new byte[300];
            while (running)
            {
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                int n;
                try { n = socket.ReceiveFrom(buf, ref from); }
                catch (SocketException) { if (!running) return; continue; }
                catch (ObjectDisposedException) { return; }
                IPEndPoint src = (IPEndPoint)from;
                if (n < 1 || !Proto.IsLanV4(src.Address.GetAddressBytes())) continue;
                if (buf[0] == WHO)
                {
                    try { socket.SendTo(Packet(NAME, Environment.MachineName), src); }
                    catch (SocketException) { }
                }
                else if (buf[0] == PULL)
                {
                    long now = Clock.Ms();
                    if (now - Interlocked.Read(ref lastPull) < 500) continue;
                    Interlocked.Exchange(ref lastPull, now);
                    string who = Text(buf, n);
                    Action<IPAddress, string> h = Pulled;
                    if (h != null) h(src.Address, who ?? src.Address.ToString());
                }
            }
        }

        /// <summary>type | UTF-8 text (at most 255 bytes); unit-tested</summary>
        internal static byte[] Packet(byte type, string text)
        {
            byte[] t = Encoding.UTF8.GetBytes(text ?? "");
            byte[] p = new byte[1 + Math.Min(t.Length, 255)];
            p[0] = type;
            Buffer.BlockCopy(t, 0, p, 1, p.Length - 1);
            return p;
        }

        /// <summary>the text after the type byte, null when empty; unit-tested</summary>
        internal static string Text(byte[] b, int n)
        {
            if (n < 2) return null;
            string s = Encoding.UTF8.GetString(b, 1, Math.Min(n - 1, 255)).Trim();
            return s.Length > 0 ? s : null;
        }

        /// <summary>the PCs on the home network that share their screen now ("name", address)</summary>
        public static List<KeyValuePair<string, IPAddress>> Find(int ms)
        {
            List<KeyValuePair<string, IPAddress>> found = new List<KeyValuePair<string, IPAddress>>();
            List<IPAddress> own = WebCast.UpIPv4(false);
            using (Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                try
                {
                    s.EnableBroadcast = true;
                    s.Bind(new IPEndPoint(IPAddress.Any, 0));
                    byte[] who = { WHO };
                    List<IPAddress> targets = Discovery.LanBroadcasts();
                    targets.Add(IPAddress.Broadcast);
                    foreach (IPAddress t in targets)
                    {
                        try { s.SendTo(who, new IPEndPoint(t, PORT)); }
                        catch (SocketException) { }
                    }
                    byte[] buf = new byte[300];
                    long end = Clock.Ms() + ms;
                    while (true)
                    {
                        long left = end - Clock.Ms();
                        if (left <= 0) break;
                        s.ReceiveTimeout = (int)Math.Max(1, left);
                        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                        int n;
                        try { n = s.ReceiveFrom(buf, ref from); }
                        catch (SocketException) { break; }
                        IPEndPoint src = (IPEndPoint)from;
                        if (n < 2 || buf[0] != NAME || src.Port != PORT || own.Contains(src.Address)) continue;
                        string name = Text(buf, n);
                        if (name == null) continue;
                        bool dup = false;
                        foreach (KeyValuePair<string, IPAddress> f in found) dup |= f.Value.Equals(src.Address);
                        if (!dup) found.Add(new KeyValuePair<string, IPAddress>(name, src.Address));
                    }
                }
                catch (SocketException) { }
            }
            return found;
        }

        /// <summary>asks a sharing PC to cast to this one</summary>
        public static void AskToCast(IPAddress sharer)
        {
            try
            {
                using (Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                    s.SendTo(Packet(PULL, Environment.MachineName), new IPEndPoint(sharer, PORT));
            }
            catch (SocketException) { }
        }

        public void Dispose()
        {
            running = false;
            if (socket != null) socket.Close();
        }
    }
}
