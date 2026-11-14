// The encrypted link to a receiver (TV or phone): the same as cast/common/.../SecureLink.java,
// see the description there (handshake, keys, SEALED packets, pairing). Golden values in
// tests/cs/Tests.cs and tests/java/.../SecureLinkTest.java keep the two equal.
//
// ECDH P-256 with Windows CNG (ECDiffieHellmanCng, KDF Hash/SHA-256 = SHA-256 of the shared x),
// AES-128-GCM with bcrypt.dll (the .NET Framework has no AesGcm class). The PC's static key is
// kept DPAPI-protected in %APPDATA%\CastLink\identity.dat, the receivers the user confirmed in
// %APPDATA%\CastLink\trusted.txt ("<base64 point> <name>" per line).

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace TVCast
{
    static class SecureLink
    {
        public const byte HELLO = 20, HELLO_OK = 21, SEALED = 22, PAIR_SHOW = 24;
        public const int VERSION = 1, NONCE = 16, POINT = 65, TAG = 16, OVERHEAD = 1 + 8 + TAG;
        public const int CONFIG_PAIRED = 2;
        public const int HELLO_FIXED = 2 + NONCE + POINT + POINT;
        public const int HELLO_OK_FIXED = 2 + NONCE + POINT + POINT + NONCE + 1;

        static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();

        // ---- keys -----------------------------------------------------------------------------

        public static CngKey NewKey(bool exportable)
        {
            CngKeyCreationParameters p = new CngKeyCreationParameters();
            if (exportable) p.ExportPolicy = CngExportPolicies.AllowPlaintextExport;
            return CngKey.Create(CngAlgorithm.ECDiffieHellmanP256, null, p);
        }

        /// <summary>a key pair from a fixed private number and its point (tests)</summary>
        internal static CngKey FromScalar(byte[] d, byte[] point)
        {
            byte[] blob = new byte[8 + 96];
            BitConverter.GetBytes(0x324B4345).CopyTo(blob, 0); // "ECK2": ECDH P-256 private
            BitConverter.GetBytes(32).CopyTo(blob, 4);
            Buffer.BlockCopy(point, 1, blob, 8, 64);
            Buffer.BlockCopy(d, 0, blob, 72, 32);
            return CngKey.Import(blob, CngKeyBlobFormat.EccPrivateBlob);
        }

        /// <summary>04 | X | Y</summary>
        public static byte[] PointOf(CngKey k)
        {
            byte[] blob = k.Export(CngKeyBlobFormat.EccPublicBlob); // magic 4 | length 4 | X | Y
            byte[] p = new byte[POINT];
            p[0] = 4;
            Buffer.BlockCopy(blob, 8, p, 1, 64);
            return p;
        }

        static readonly BigInteger P = BigInteger.Parse("0ffffffff00000001000000000000000000000000ffffffffffffffffffffffff", System.Globalization.NumberStyles.HexNumber);
        static readonly BigInteger B = BigInteger.Parse("05ac635d8aa3a93e7b3ebbd55769886bc651d06b0cc53b0f63bce3c3e27d2604b", System.Globalization.NumberStyles.HexNumber);

        static BigInteger Unsigned(byte[] b, int o)
        {
            byte[] le = new byte[33]; // little-endian with a 0 on top: always positive
            for (int i = 0; i < 32; i++) le[i] = b[o + 31 - i];
            return new BigInteger(le);
        }

        /// <summary>a 65-byte point on P-256 (a point off the curve could leak the static key: the
        /// "invalid curve" attack), so y^2 = x^3 - 3x + b (mod p) is checked before CNG sees it</summary>
        public static bool OnCurve(byte[] p)
        {
            if (p == null || p.Length != POINT || p[0] != 4) return false;
            BigInteger x = Unsigned(p, 1), y = Unsigned(p, 33);
            if (x >= P || y >= P) return false;
            BigInteger right = ((BigInteger.ModPow(x, 3, P) - 3 * x + B) % P + P) % P;
            return BigInteger.ModPow(y, 2, P) == right;
        }

        /// <summary>SHA-256 of the ECDH shared x coordinate</summary>
        internal static byte[] Agree(CngKey mine, byte[] theirPoint)
        {
            if (!OnCurve(theirPoint)) throw new CryptographicException("not a point of P-256");
            byte[] blob = new byte[8 + 64];
            BitConverter.GetBytes(0x314B4345).CopyTo(blob, 0); // "ECK1": ECDH P-256 public
            BitConverter.GetBytes(32).CopyTo(blob, 4);
            Buffer.BlockCopy(theirPoint, 1, blob, 8, 64);
            using (ECDiffieHellmanCng e = new ECDiffieHellmanCng(mine))
            using (ECDiffieHellmanPublicKey pub = ECDiffieHellmanCngPublicKey.FromByteArray(blob, CngKeyBlobFormat.EccPublicBlob))
            {
                e.KeyDerivationFunction = ECDiffieHellmanKeyDerivationFunction.Hash;
                e.HashAlgorithm = CngAlgorithm.Sha256;
                return e.DeriveKeyMaterial(pub);
            }
        }

        public static byte[] RandomNonce()
        {
            byte[] n = new byte[NONCE];
            Rng.GetBytes(n);
            return n;
        }

        // ---- handshake messages -----------------------------------------------------------------

        public sealed class Hello
        {
            public byte[] Nonce, Ephemeral, Static, Echo;
            public bool KnowsYou;
            public string Name = "";
        }

        public static byte[] MakeHello(byte[] nonce, byte[] eph, byte[] stat, string name)
        {
            byte[] nm = Utf8(name);
            byte[] b = new byte[HELLO_FIXED + nm.Length];
            b[0] = HELLO;
            b[1] = VERSION;
            Buffer.BlockCopy(nonce, 0, b, 2, NONCE);
            Buffer.BlockCopy(eph, 0, b, 2 + NONCE, POINT);
            Buffer.BlockCopy(stat, 0, b, 2 + NONCE + POINT, POINT);
            Buffer.BlockCopy(nm, 0, b, HELLO_FIXED, nm.Length);
            return b;
        }

        /// <summary>a HELLO or HELLO_OK of this version, else null</summary>
        public static Hello Parse(byte[] b, int n)
        {
            if (b == null || n < 2 || b[1] != VERSION) return null;
            bool ok = b[0] == HELLO_OK;
            if (b[0] != HELLO && !ok) return null;
            int fixedLen = ok ? HELLO_OK_FIXED : HELLO_FIXED;
            if (n < fixedLen || n > fixedLen + 64) return null;
            Hello h = new Hello();
            h.Nonce = Slice(b, 2, NONCE);
            h.Ephemeral = Slice(b, 2 + NONCE, POINT);
            h.Static = Slice(b, 2 + NONCE + POINT, POINT);
            if (ok)
            {
                h.Echo = Slice(b, 2 + NONCE + 2 * POINT, NONCE);
                h.KnowsYou = b[fixedLen - 1] == 1;
            }
            try { h.Name = new UTF8Encoding(false, false).GetString(b, fixedLen, n - fixedLen); } catch (ArgumentException) { }
            return h;
        }

        static byte[] Utf8(string s)
        {
            byte[] b = Encoding.UTF8.GetBytes(s ?? "");
            if (b.Length > 64) Array.Resize(ref b, 64);
            return b;
        }

        static byte[] Slice(byte[] b, int o, int n)
        {
            byte[] r = new byte[n];
            Buffer.BlockCopy(b, o, r, 0, n);
            return r;
        }

        public static bool Same(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int d = 0;
            for (int i = 0; i < a.Length; i++) d |= a[i] ^ b[i];
            return d == 0;
        }

        // ---- the session --------------------------------------------------------------------

        public static Session Derive(bool caster, CngKey myStatic, CngKey myEphemeral,
            byte[] casterNonce, byte[] receiverNonce, byte[] casterEph, byte[] receiverEph, byte[] casterStatic, byte[] receiverStatic)
        {
            byte[] ee = Agree(myEphemeral, caster ? receiverEph : casterEph);
            byte[] ss = Agree(myStatic, caster ? receiverStatic : casterStatic);
            byte[] salt;
            using (SHA256 h = SHA256.Create())
            {
                MemoryStream m = new MemoryStream();
                byte[] label = Encoding.ASCII.GetBytes("CastLink v1");
                m.Write(label, 0, label.Length);
                foreach (byte[] x in new[] { casterNonce, receiverNonce, casterEph, receiverEph, casterStatic, receiverStatic }) m.Write(x, 0, x.Length);
                salt = h.ComputeHash(m.ToArray());
            }
            byte[] ikm = new byte[64];
            Buffer.BlockCopy(ee, 0, ikm, 0, 32);
            Buffer.BlockCopy(ss, 0, ikm, 32, 32);
            byte[] prk;
            using (HMACSHA256 mac = new HMACSHA256(salt)) prk = mac.ComputeHash(ikm);
            Array.Clear(ikm, 0, ikm.Length);
            byte[] keys = Expand(prk, "keys", 40), code = Expand(prk, "code", 4);
            Array.Clear(prk, 0, prk.Length);
            uint c = (uint)(code[0] << 24 | code[1] << 16 | code[2] << 8 | code[3]);
            int pairCode = (int)(c % 1000000u);
            byte[] c2r = Slice(keys, 0, 16), r2c = Slice(keys, 16, 16), cs = Slice(keys, 32, 4), rs = Slice(keys, 36, 4);
            Array.Clear(keys, 0, keys.Length);
            return caster ? new Session(c2r, cs, r2c, rs, pairCode) : new Session(r2c, rs, c2r, cs, pairCode);
        }

        /// <summary>HKDF-Expand (RFC 5869), an ASCII label as info</summary>
        internal static byte[] Expand(byte[] prk, string info, int len)
        {
            byte[] outb = new byte[len], t = new byte[0], inf = Encoding.ASCII.GetBytes(info);
            int done = 0;
            using (HMACSHA256 mac = new HMACSHA256(prk))
            {
                for (byte i = 1; done < len; i++)
                {
                    byte[] input = new byte[t.Length + inf.Length + 1];
                    Buffer.BlockCopy(t, 0, input, 0, t.Length);
                    Buffer.BlockCopy(inf, 0, input, t.Length, inf.Length);
                    input[input.Length - 1] = i;
                    t = mac.ComputeHash(input);
                    int k = Math.Min(t.Length, len - done);
                    Buffer.BlockCopy(t, 0, outb, done, k);
                    done += k;
                }
            }
            return outb;
        }

        public sealed class Session : IDisposable
        {
            readonly Gcm send, recv;
            readonly byte[] sendSalt, recvSalt;
            public readonly int Code;
            long sendCounter = -1;
            long highest = -1;
            ulong window;
            readonly object recvGate = new object();

            internal Session(byte[] sk, byte[] ss, byte[] rk, byte[] rs, int code)
            {
                send = new Gcm(sk);
                recv = new Gcm(rk);
                sendSalt = ss;
                recvSalt = rs;
                Code = code;
                Array.Clear(sk, 0, sk.Length);
                Array.Clear(rk, 0, rk.Length);
            }

            /// <summary>"123 456"</summary>
            public string CodeText { get { string s = Code.ToString("000000"); return s.Substring(0, 3) + " " + s.Substring(3); } }

            /// <summary>in[0..len) sealed into outb (len + OVERHEAD bytes); returns its length.
            /// Safe from several threads (video, sound, control).</summary>
            public int Seal(byte[] inb, int len, byte[] outb)
            {
                long ctr = System.Threading.Interlocked.Increment(ref sendCounter);
                outb[0] = SEALED;
                Proto.PutLong(outb, 1, ctr);
                send.Encrypt(Nonce(sendSalt, ctr), outb, 0, 9, inb, 0, len, outb, 9, outb, 9 + len);
                return len + OVERHEAD;
            }

            /// <summary>the inner packet length (written to outb), -1 if not ours, changed or replayed</summary>
            public int Open(byte[] b, int n, byte[] outb)
            {
                if (n < OVERHEAD || b[0] != SEALED) return -1;
                long ctr = Proto.GetLong(b, 1);
                lock (recvGate)
                {
                    if (ctr < 0 || Seen(ctr)) return -1;
                    int len = n - OVERHEAD;
                    if (!recv.Decrypt(Nonce(recvSalt, ctr), b, 0, 9, b, 9, len, outb, 0, b, 9 + len)) return -1;
                    Mark(ctr);
                    return len;
                }
            }

            bool Seen(long ctr)
            {
                if (highest < 0 || ctr > highest) return false;
                long back = highest - ctr;
                return back >= 64 || (window & (1UL << (int)back)) != 0;
            }

            void Mark(long ctr)
            {
                if (ctr > highest)
                {
                    long shift = highest < 0 ? 64 : ctr - highest;
                    window = shift >= 64 ? 1UL : (window << (int)shift) | 1UL;
                    highest = ctr;
                }
                else window |= 1UL << (int)(highest - ctr);
            }

            static byte[] Nonce(byte[] salt, long ctr)
            {
                byte[] n = new byte[12];
                Buffer.BlockCopy(salt, 0, n, 0, 4);
                Proto.PutLong(n, 4, ctr);
                return n;
            }

            public void Dispose() { send.Dispose(); recv.Dispose(); }
        }

        // ---- AES-128-GCM with bcrypt.dll ---------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        struct AuthInfo // BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO
        {
            public int cbSize, dwInfoVersion;
            public IntPtr pbNonce; public int cbNonce;
            public IntPtr pbAuthData; public int cbAuthData;
            public IntPtr pbTag; public int cbTag;
            public IntPtr pbMacContext; public int cbMacContext;
            public int cbAAD; public long cbData; public int dwFlags;
        }

        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)] static extern int BCryptOpenAlgorithmProvider(out IntPtr alg, string id, string impl, int flags);
        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)] static extern int BCryptSetProperty(IntPtr h, string prop, byte[] input, int len, int flags);
        [DllImport("bcrypt.dll")] static extern int BCryptGenerateSymmetricKey(IntPtr alg, out IntPtr key, IntPtr obj, int objLen, byte[] secret, int len, int flags);
        [DllImport("bcrypt.dll")] static extern int BCryptDestroyKey(IntPtr key);
        [DllImport("bcrypt.dll")] static extern unsafe int BCryptEncrypt(IntPtr key, byte* input, int inLen, ref AuthInfo info, byte* iv, int ivLen, byte* output, int outLen, out int result, int flags);
        [DllImport("bcrypt.dll")] static extern unsafe int BCryptDecrypt(IntPtr key, byte* input, int inLen, ref AuthInfo info, byte* iv, int ivLen, byte* output, int outLen, out int result, int flags);

        static IntPtr gcmAlg;
        static readonly object algGate = new object();

        static IntPtr Alg()
        {
            lock (algGate)
            {
                if (gcmAlg != IntPtr.Zero) return gcmAlg;
                IntPtr a;
                Check(BCryptOpenAlgorithmProvider(out a, "AES", null, 0), "open AES");
                byte[] mode = Encoding.Unicode.GetBytes("ChainingModeGCM\0");
                Check(BCryptSetProperty(a, "ChainingMode", mode, mode.Length, 0), "GCM mode");
                gcmAlg = a;
                return a;
            }
        }

        static void Check(int status, string what) { if (status != 0) throw new CryptographicException(what + " 0x" + status.ToString("x8")); }

        sealed class Gcm : IDisposable
        {
            IntPtr key;
            readonly object gate = new object(); // one call at a time on a key handle

            public Gcm(byte[] k)
            {
                Check(BCryptGenerateSymmetricKey(Alg(), out key, IntPtr.Zero, 0, k, k.Length, 0), "AES key");
            }

            public unsafe void Encrypt(byte[] nonce, byte[] aad, int aadOff, int aadLen, byte[] input, int inOff, int len,
                byte[] output, int outOff, byte[] tag, int tagOff)
            {
                lock (gate)
                {
                    fixed (byte* pn = nonce, pa = aad, pi = input, po = output, pt = tag)
                    {
                        AuthInfo ai = Info(pn, pa + aadOff, aadLen, pt + tagOff);
                        int got;
                        Check(BCryptEncrypt(key, len > 0 ? pi + inOff : null, len, ref ai, null, 0, len > 0 ? po + outOff : null, len, out got, 0), "seal");
                    }
                }
            }

            public unsafe bool Decrypt(byte[] nonce, byte[] aad, int aadOff, int aadLen, byte[] input, int inOff, int len,
                byte[] output, int outOff, byte[] tag, int tagOff)
            {
                lock (gate)
                {
                    fixed (byte* pn = nonce, pa = aad, pi = input, po = output, pt = tag)
                    {
                        AuthInfo ai = Info(pn, pa + aadOff, aadLen, pt + tagOff);
                        int got;
                        // STATUS_AUTH_TAG_MISMATCH (0xC000A002) or anything else: not ours
                        return BCryptDecrypt(key, len > 0 ? pi + inOff : null, len, ref ai, null, 0, len > 0 ? po + outOff : null, len, out got, 0) == 0;
                    }
                }
            }

            static unsafe AuthInfo Info(byte* nonce, byte* aad, int aadLen, byte* tag)
            {
                AuthInfo ai = new AuthInfo();
                ai.cbSize = Marshal.SizeOf(typeof(AuthInfo));
                ai.dwInfoVersion = 1;
                ai.pbNonce = (IntPtr)nonce; ai.cbNonce = 12;
                ai.pbAuthData = (IntPtr)aad; ai.cbAuthData = aadLen;
                ai.pbTag = (IntPtr)tag; ai.cbTag = TAG;
                return ai;
            }

            public void Dispose()
            {
                lock (gate) { if (key != IntPtr.Zero) { BCryptDestroyKey(key); key = IntPtr.Zero; } }
            }
        }

        // ---- the PC's identity and the receivers it trusts -------------------------------------------

        static string Folder()
        {
            string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CastLink");
            Directory.CreateDirectory(d);
            return d;
        }

        static CngKey identity;

        /// <summary>this PC's static key: made once, kept DPAPI-protected for this Windows user</summary>
        public static CngKey Identity()
        {
            lock (algGate)
            {
                if (identity != null) return identity;
                string f = Path.Combine(Folder(), "identity.dat");
                if (File.Exists(f))
                {
                    try
                    {
                        byte[] blob = ProtectedData.Unprotect(File.ReadAllBytes(f), null, DataProtectionScope.CurrentUser);
                        identity = CngKey.Import(blob, CngKeyBlobFormat.EccPrivateBlob);
                        Array.Clear(blob, 0, blob.Length);
                        return identity;
                    }
                    catch (CryptographicException) { } // another user's / damaged: make a new one
                }
                CngKey k = NewKey(true);
                byte[] priv = k.Export(CngKeyBlobFormat.EccPrivateBlob);
                File.WriteAllBytes(f, ProtectedData.Protect(priv, null, DataProtectionScope.CurrentUser));
                Array.Clear(priv, 0, priv.Length);
                identity = k;
                return k;
            }
        }

        static string TrustFile() { return Path.Combine(Folder(), "trusted.txt"); }

        /// <summary>the user confirmed this receiver's key once (pairing code)</summary>
        public static bool Trusted(byte[] point)
        {
            string b64 = Convert.ToBase64String(point);
            try
            {
                if (!File.Exists(TrustFile())) return false;
                foreach (string l in File.ReadAllLines(TrustFile()))
                    if (l.StartsWith(b64 + " ") || l == b64) return true;
            }
            catch (IOException) { }
            return false;
        }

        public static void Trust(byte[] point, string name)
        {
            if (Trusted(point)) return;
            string clean = (name ?? "").Replace('\r', ' ').Replace('\n', ' ');
            File.AppendAllText(TrustFile(), Convert.ToBase64String(point) + " " + clean + Environment.NewLine);
        }
    }
}
