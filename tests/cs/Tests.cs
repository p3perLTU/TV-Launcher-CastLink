// Unit tests for the PC sender (cast/pc/TVCastPC.cs). Compiled together with it by
// tests/run-tests.ps1 (csc /main:TVCast.Tests.TestProgram), so internal classes are reachable.
// C# 5, like the app: the compiler that ships with Windows.
//
//   TVCast-PC-Tests.exe <path to cast/common/src/lt/tvcast/common/Proto.java>

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace TVCast.Tests
{
    static class TestProgram
    {
        static string javaProto;

        static int Main(string[] args)
        {
            // like the app: the WebRTC libraries come from lib\ next to the exe
            AppDomain.CurrentDomain.AssemblyResolve += delegate(object s, ResolveEventArgs e)
            {
                string f = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lib", new AssemblyName(e.Name).Name + ".dll");
                return File.Exists(f) ? Assembly.LoadFrom(f) : null;
            };
            javaProto = args.Length > 0 ? args[0] : null;
            int passed = 0, failed = 0;
            MethodInfo[] methods = typeof(TestProgram).GetMethods(BindingFlags.Static | BindingFlags.NonPublic);
            Array.Sort(methods, delegate(MethodInfo a, MethodInfo b) { return string.CompareOrdinal(a.Name, b.Name); });
            foreach (MethodInfo m in methods)
            {
                if (!m.Name.StartsWith("Test") || m.GetParameters().Length != 0) continue;
                try
                {
                    m.Invoke(null, null);
                    passed++;
                    Console.WriteLine("  ok    " + m.Name);
                }
                catch (TargetInvocationException e)
                {
                    failed++;
                    Console.WriteLine("  FAIL  " + m.Name + ": " + e.InnerException.Message);
                }
            }
            Console.WriteLine("C#: " + passed + " passed, " + failed + " failed");
            return failed == 0 && passed > 0 ? 0 : 1;
        }

        // ---- assertions ---------------------------------------------------------------------

        static void Equal(object expected, object actual, string what)
        {
            if (!object.Equals(expected, actual))
                throw new Exception(what + ": expected " + expected + ", got " + actual);
        }

        static void True(bool v, string what)
        {
            if (!v) throw new Exception(what);
        }

        static string Hex(byte[] b)
        {
            StringBuilder sb = new StringBuilder();
            foreach (byte x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        // ---- the encrypted link (SecureLink.cs; the same values in tests/java/.../SecureLinkTest.java) ----

        const string CsD = "9f26144dc9b4dfcaa1b200317802e2f2df1489e229a7833ccfbf32a4176c8261";
        const string CsP = "0407fad123bbb8703eb1f58fe49ab0fdb034ce8eb7bb04a0ca6c9ed476299c98ceb09584ebd8d64d91f2751cdb45175a2b5e2bdac3d2254738b9754156ac448192";
        const string CeD = "d60cb8efff853209590f197e676d557aca0c03bbb5c2b870d4644503912a68b2";
        const string CeP = "0412b4821b12940bc8e49a3cf9bc7b172009dc4acf29cc6fa950ac6386e0c1bee603ecc09c51a594796617dbe19d52eec973162e957d54269a0ddaa9f61774995b";
        const string RsD = "bb940d30f42070930ca28c30ce22915e088ced671013b5c31eabb2e3e240e243";
        const string RsP = "047351cec262be63c1ad19777b7220c3ab8ca0502d935eba90a8705fa952278f829c826fbeeb3f74ee1c65479fd14dab33e983c61e0ff9d401a9078e0288a9774c";
        const string ReD = "0507654ec1f2617ea46ebe02b882370f6b5ee3ea83feb1862176cd8ffb533975";
        const string ReP = "042ea76c9200d27811b79427e038048c231c1a221baec3a54a19f0bd4d8833b38bcf3d481cf44046e4257c6d0dd5c47c8beb2e52eb29d812e4d5610c7fe2ffeacc";

        static byte[] Unhex(string h)
        {
            byte[] b = new byte[h.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(h.Substring(2 * i, 2), 16);
            return b;
        }

        static SecureLink.Session LinkSide(bool caster, string casterStatic)
        {
            byte[] cn = new byte[16], rn = new byte[16];
            for (int i = 0; i < 16; i++) { cn[i] = (byte)(i + 1); rn[i] = (byte)(0xA0 + i); }
            return SecureLink.Derive(caster,
                caster ? SecureLink.FromScalar(Unhex(CsD), Unhex(CsP)) : SecureLink.FromScalar(Unhex(RsD), Unhex(RsP)),
                caster ? SecureLink.FromScalar(Unhex(CeD), Unhex(CeP)) : SecureLink.FromScalar(Unhex(ReD), Unhex(ReP)),
                cn, rn, Unhex(CeP), Unhex(ReP), Unhex(casterStatic), Unhex(RsP));
        }

        static byte[] SealText(SecureLink.Session s, string text)
        {
            byte[] m = Encoding.ASCII.GetBytes(text), o = new byte[m.Length + SecureLink.OVERHEAD];
            s.Seal(m, m.Length, o);
            return o;
        }

        static void TestSecureLinkMatchesTheJavaSide()
        {
            SecureLink.Session c = LinkSide(true, CsP), r = LinkSide(false, CsP);
            Equal(982123, c.Code, "caster's pairing code (Java: the same)");
            Equal(982123, r.Code, "receiver's pairing code");
            Equal("982 123", c.CodeText, "as shown");
            Equal("160000000000000000c508a0bb51cae79c1dbcd6dcfa9b1d386d3f57513a49f3d6", Hex(SealText(c, "CastLink")), "caster -> receiver packet 0, byte for byte as Java");
            Equal("1600000000000000006cf7ce25eb5050ef0243bdac9a8c0e6838646306acf9b213", Hex(SealText(r, "CastLink")), "receiver -> caster packet 0");
        }

        static void TestSecureLinkOpensChangesAndReplays()
        {
            SecureLink.Session c = LinkSide(true, CsP), r = LinkSide(false, CsP);
            byte[] outb = new byte[64];
            byte[] p0 = SealText(c, "one"), p1 = SealText(c, "two");
            Equal(3, r.Open(p1, p1.Length, outb), "packet 1");
            Equal("two", Encoding.ASCII.GetString(outb, 0, 3), "its text");
            Equal(3, r.Open(p0, p0.Length, outb), "packet 0 late: fine");
            Equal(-1, r.Open(p0, p0.Length, outb), "packet 0 again: replay");
            byte[] p2 = SealText(c, "three");
            p2[12] ^= 1;
            Equal(-1, r.Open(p2, p2.Length, outb), "a changed byte");
            Equal(-1, c.Open(SealText(c, "x"), 1 + SecureLink.OVERHEAD, outb), "its own direction: other key");
        }

        static void TestSecureLinkOtherStaticKeyCannotOpen()
        {
            byte[] other = SecureLink.PointOf(SecureLink.NewKey(false));
            SecureLink.Session c = LinkSide(true, CsP), r = LinkSide(false, Hex(other));
            byte[] p = SealText(c, "hi");
            Equal(-1, r.Open(p, p.Length, new byte[16]), "someone in the middle with another key");
        }

        static void TestSecureLinkRefusesPointsOffTheCurve()
        {
            True(SecureLink.OnCurve(Unhex(CsP)), "a real point");
            byte[] bad = Unhex(CsP);
            bad[64] ^= 1;
            True(!SecureLink.OnCurve(bad), "one bit changed: off the curve");
            True(!SecureLink.OnCurve(new byte[65]), "zeros");
            bool threw = false;
            try { SecureLink.Agree(SecureLink.FromScalar(Unhex(CsD), Unhex(CsP)), bad); }
            catch (System.Security.Cryptography.CryptographicException) { threw = true; }
            True(threw, "no ECDH with such a point");
        }

        static void TestSecureLinkHelloOkFromJavaParses()
        {
            byte[] h = SecureLink.MakeHello(new byte[16], Unhex(CeP), Unhex(CsP), "PC");
            SecureLink.Hello p = SecureLink.Parse(h, h.Length);
            True(p != null && SecureLink.Same(p.Static, Unhex(CsP)) && p.Name == "PC", "HELLO round trip");
            True(SecureLink.Parse(h, SecureLink.HELLO_FIXED - 1) == null, "cut into the fixed part");
            // a HELLO_OK laid out as SecureLink.helloOk (Java) writes it
            byte[] ok = new byte[SecureLink.HELLO_OK_FIXED + 2];
            ok[0] = SecureLink.HELLO_OK; ok[1] = SecureLink.VERSION;
            Buffer.BlockCopy(Unhex(ReP), 0, ok, 2 + 16, 65);
            Buffer.BlockCopy(Unhex(RsP), 0, ok, 2 + 16 + 65, 65);
            ok[SecureLink.HELLO_OK_FIXED - 1] = 1;
            ok[SecureLink.HELLO_OK_FIXED] = (byte)'T'; ok[SecureLink.HELLO_OK_FIXED + 1] = (byte)'V';
            SecureLink.Hello o = SecureLink.Parse(ok, ok.Length);
            True(o != null && o.KnowsYou && o.Name == "TV" && SecureLink.Same(o.Static, Unhex(RsP)), "HELLO_OK fields");
        }

        // ---- protocol -----------------------------------------------------------------------

        // the same bytes are checked in tests/java/lt/tvcast/common/ProtoTest.java
        const string GoldenVideoHeader = "0201010203040005000711223344556677" + "88";

        static void TestVideoHeaderMatchesJava()
        {
            byte[] pkt = new byte[Proto.VIDEO_HEADER];
            Proto.PutVideoHeader(pkt, true, 0x01020304, 5, 7, 0x1122334455667788L);
            Equal(GoldenVideoHeader, Hex(pkt), "VIDEO header");
        }

        static void TestLongRoundTrip()
        {
            byte[] b = new byte[8];
            foreach (long v in new long[] { 0, 1, -1, long.MaxValue, long.MinValue, 0xFFFFFFFFL })
            {
                Proto.PutLong(b, 0, v);
                Equal(v, Proto.GetLong(b, 0), "long " + v);
            }
        }

        static void TestVideoChunks()
        {
            Equal(0, Proto.VideoChunks(0), "empty");
            Equal(1, Proto.VideoChunks(Proto.MAX_PAYLOAD), "one payload");
            Equal(2, Proto.VideoChunks(Proto.MAX_PAYLOAD + 1), "one byte more");
            Equal(-1, Proto.VideoChunks(0xFFFF * Proto.MAX_PAYLOAD + 1), "too large");
        }

        /// <summary>the constants the PC app repeats must match the Java ones the phone and TV use</summary>
        static void TestConstantsMatchJava()
        {
            True(javaProto != null && File.Exists(javaProto), "Proto.java path given as the first argument");
            string src = File.ReadAllText(javaProto);
            int compared = 0;
            foreach (Match m in Regex.Matches(src, @"public static final (?:int|byte) (\w+) = ([^;]+);"))
            {
                FieldInfo f = typeof(Proto).GetField(m.Groups[1].Value);
                if (f == null) continue;
                int java = 0;
                foreach (string part in m.Groups[2].Value.Split('+')) java += int.Parse(part.Trim());
                Equal(java, Convert.ToInt32(f.GetValue(null)), m.Groups[1].Value);
                compared++;
            }
            True(compared >= 11, "compared " + compared + " constants (PORT, packet types, sizes)");
        }

        // ---- picture conversion -------------------------------------------------------------

        static byte[] Solid(int w, int h, byte b, byte g, byte r)
        {
            byte[] px = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++) { px[i * 4] = b; px[i * 4 + 1] = g; px[i * 4 + 2] = r; px[i * 4 + 3] = 255; }
            return px;
        }

        static byte[] ToNv12(byte[] bgra, int sw, int sh, int dw, int dh)
        {
            byte[] dst = new byte[dw * dh * 3 / 2];
            Nv12.Convert(bgra, sw, sh, dst, dw, dh, null);
            return dst;
        }

        static void TestNv12LimitedRange()
        {
            // BT.709 limited range: black Y 16, white Y 235, grey has no colour (U, V ~128)
            byte[] black = ToNv12(Solid(2, 2, 0, 0, 0), 2, 2, 2, 2);
            Equal("10101010" + "8080", Hex(black), "black");
            byte[] white = ToNv12(Solid(2, 2, 255, 255, 255), 2, 2, 2, 2);
            // U is 127, not 128: the integer U coefficients add up to -1, a 1/255 error
            Equal("ebebebeb" + "7f80", Hex(white), "white");
        }

        static void TestNv12Red()
        {
            byte[] red = ToNv12(Solid(2, 2, 0, 0, 255), 2, 2, 2, 2);
            Equal(63, (int)red[0], "Y of red");
            Equal(102, (int)red[4], "U of red");
            Equal(240, (int)red[5], "V of red");
        }

        static void TestNv12Scales()
        {
            // 4x4 left half black, right half white -> 2x2 keeps the split
            byte[] src = Solid(4, 4, 0, 0, 0);
            for (int y = 0; y < 4; y++)
                for (int x = 2; x < 4; x++)
                    for (int k = 0; k < 3; k++) src[(y * 4 + x) * 4 + k] = 255;
            byte[] d = ToNv12(src, 4, 4, 2, 2);
            Equal(16, (int)d[0], "top left black");
            Equal(235, (int)d[1], "top right white");
            Equal(16, (int)d[2], "bottom left black");
            Equal(235, (int)d[3], "bottom right white");
        }

        static void TestNv12Pointer()
        {
            Pointer p = new Pointer();
            p.Visible = true;
            p.X = 1; p.Y = 0; p.W = 1; p.H = 1;
            p.Px = new byte[] { 255, 255, 255, 255 }; // opaque white, premultiplied BGRA
            byte[] dst = new byte[2 * 2 * 3 / 2];
            Nv12.Convert(Solid(2, 2, 0, 0, 0), 2, 2, dst, 2, 2, p);
            Equal(16, (int)dst[0], "picture stays black");
            Equal(235, (int)dst[1], "pointer pixel is white");
        }

        // ---- finding the TV -----------------------------------------------------------------

        static void Label(MemoryStream s, string l)
        {
            byte[] b = Encoding.UTF8.GetBytes(l);
            s.WriteByte((byte)b.Length);
            s.Write(b, 0, b.Length);
        }

        /// <summary>an mDNS answer "_tvcast._udp.local PTR name._tvcast._udp.local", compressed like Android sends it</summary>
        static byte[] PtrAnswer(string service, string name)
        {
            MemoryStream s = new MemoryStream();
            s.Write(new byte[] { 0, 0, 0x84, 0, 0, 0, 0, 1, 0, 0, 0, 0 }, 0, 12);
            Label(s, service); Label(s, "_udp"); Label(s, "local"); s.WriteByte(0);
            s.Write(new byte[] { 0, 12, 0, 1, 0, 0, 0x11, 0x94 }, 0, 8);
            byte[] n = Encoding.UTF8.GetBytes(name);
            int rdlen = 1 + n.Length + 2;
            s.WriteByte((byte)(rdlen >> 8)); s.WriteByte((byte)rdlen);
            Label(s, name);
            s.Write(new byte[] { 0xC0, 12 }, 0, 2); // pointer to the service name at offset 12
            return s.ToArray();
        }

        static void TestDiscoveryReadsInstanceName()
        {
            byte[] b = PtrAnswer("_tvcast", "SONY KD-50X80J");
            Equal("SONY KD-50X80J", Discovery.InstanceName(b, b.Length), "TV name");
        }

        static void TestDiscoveryIgnoresOtherServices()
        {
            byte[] b = PtrAnswer("_googlecast", "Chromecast");
            Equal(null, Discovery.InstanceName(b, b.Length), "other service");
        }

        static void TestDiscoverySurvivesTruncatedPackets()
        {
            byte[] full = PtrAnswer("_tvcast", "TV");
            for (int n = 0; n < full.Length; n++)
            {
                byte[] cut = new byte[n];
                Array.Copy(full, cut, n);
                Discovery.InstanceName(cut, n); // must not throw
            }
        }

        static void TestDiscoveryStopsOnPointerLoop()
        {
            byte[] b = PtrAnswer("_tvcast", "TV");
            // make the name at offset 12 point to itself
            b[12] = 0xC0; b[13] = 12;
            Discovery.InstanceName(b, b.Length); // must return, not hang
        }

        // ---- window texts -------------------------------------------------------------------

        static Dictionary<string, string> TextsFor(string lang)
        {
            Texts.Use(lang);
            Dictionary<string, string> d = new Dictionary<string, string>();
            foreach (FieldInfo f in typeof(Texts).GetFields(BindingFlags.Static | BindingFlags.Public))
                d[f.Name] = (string)f.GetValue(null);
            return d;
        }

        static void TestTextsComplete()
        {
            Dictionary<string, string> en = TextsFor("en"), lt = TextsFor("lt");
            True(en.Count >= 26, "all texts present (" + en.Count + ")");
            foreach (KeyValuePair<string, string> e in en)
            {
                True(!string.IsNullOrEmpty(e.Value), "English " + e.Key + " is set");
                True(!string.IsNullOrEmpty(lt[e.Key]), "Lithuanian " + e.Key + " is set");
                // the same {0} {1:0.0} ... in both, otherwise string.Format throws or drops a value
                Equal(Placeholders(e.Value), Placeholders(lt[e.Key]), "placeholders of " + e.Key);
            }
        }

        static void TestEnglishHasNoLithuanian()
        {
            foreach (KeyValuePair<string, string> e in TextsFor("en"))
                True(!Regex.IsMatch(e.Value, "[ĄČĘĖĮŠŲŪŽąčęėįšųūž„]"), "English " + e.Key + ": " + e.Value);
        }

        static void TestLanguageChoice()
        {
            TextsFor("en");
            Equal("Show in a browser", Texts.ShowIpad, "English");
            TextsFor("lt");
            Equal("Rodyti naršyklėje", Texts.ShowIpad, "Lithuanian");
            TextsFor("de");
            Equal("Show in a browser", Texts.ShowIpad, "any other language falls back to English");
            Equal("Devices found: 2. Press “Show”.", string.Format(Texts.Found, 2), "format string");
        }

        static string Placeholders(string s)
        {
            List<string> p = new List<string>();
            foreach (Match m in Regex.Matches(s, @"\{\d+(:[^}]*)?\}")) p.Add(m.Value);
            p.Sort(string.CompareOrdinal);
            return string.Join(" ", p.ToArray());
        }

        // ---- iPad mode: fMP4 ------------------------------------------------------------------

        static readonly byte[] Sps = { 0x67, 0x64, 0x00, 0x28, 0xAC, 0xD9, 0x40, 0x78 };
        static readonly byte[] Pps = { 0x68, 0xEB, 0xE3, 0xCB };

        /// <summary>an Annex-B keyframe like the encoder's: AUD, SPS, PPS, IDR slice; mixed start codes</summary>
        static byte[] AnnexBKeyframe(byte[] slice)
        {
            MemoryStream m = new MemoryStream();
            m.Write(new byte[] { 0, 0, 0, 1, 0x09, 0xF0 }, 0, 6);
            m.Write(new byte[] { 0, 0, 0, 1 }, 0, 4); m.Write(Sps, 0, Sps.Length);
            m.Write(new byte[] { 0, 0, 1 }, 0, 3); m.Write(Pps, 0, Pps.Length);
            m.Write(new byte[] { 0, 0, 0, 1 }, 0, 4); m.Write(slice, 0, slice.Length);
            return m.ToArray();
        }

        static void TestSplitNals()
        {
            byte[] slice = { 0x65, 0x88, 0x80, 0x10, 0x00, 0x03, 0x01 };
            byte[] b = AnnexBKeyframe(slice);
            List<ArraySegment<byte>> n = Fmp4.SplitNals(b, b.Length);
            Equal(4, n.Count, "AUD, SPS, PPS, slice");
            Equal(9, Fmp4.NalType(n[0]), "AUD");
            Equal(Hex(Sps), Hex(Seg(n[1])), "SPS without its start code or the next one's zero");
            Equal(Hex(Pps), Hex(Seg(n[2])), "PPS after a 3-byte start code");
            Equal(Hex(slice), Hex(Seg(n[3])), "slice to the end (emulation prevention bytes kept)");
        }

        static byte[] Seg(ArraySegment<byte> s)
        {
            byte[] b = new byte[s.Count];
            Buffer.BlockCopy(s.Array, s.Offset, b, 0, s.Count);
            return b;
        }

        static void TestLengthPrefixedDropsParameterSets()
        {
            byte[] slice = { 0x65, 1, 2, 3, 4 };
            byte[] b = AnnexBKeyframe(slice);
            byte[] s = Fmp4.ToLengthPrefixed(Fmp4.SplitNals(b, b.Length));
            Equal("00000005" + Hex(slice), Hex(s), "only the slice, with a 4-byte length");
        }

        static void TestCodecString()
        {
            Equal("avc1.640028", Fmp4.CodecString(Sps), "High profile, level 4.0");
        }

        /// <summary>top-level boxes as (type, size); throws if the sizes do not add up</summary>
        static List<KeyValuePair<string, int>> Boxes(byte[] b, int from, int to)
        {
            List<KeyValuePair<string, int>> r = new List<KeyValuePair<string, int>>();
            int p = from;
            while (p < to)
            {
                int size = b[p] << 24 | b[p + 1] << 16 | b[p + 2] << 8 | b[p + 3];
                True(size >= 8 && p + size <= to, "box size at " + p);
                r.Add(new KeyValuePair<string, int>(Encoding.ASCII.GetString(b, p + 4, 4), size));
                p += size;
            }
            Equal(to, p, "boxes fill the range exactly");
            return r;
        }

        static int Find(byte[] b, string type) { return Find(b, type, 0); }

        static int Find(byte[] b, string type, int from)
        {
            byte[] t = Encoding.ASCII.GetBytes(type);
            for (int i = from + 4; i + 4 <= b.Length; i++)
                if (b[i] == t[0] && b[i + 1] == t[1] && b[i + 2] == t[2] && b[i + 3] == t[3]) return i - 4;
            return -1;
        }

        static void TestInitSegment()
        {
            byte[] init = Fmp4.Init(Sps, Pps, 1920, 1080);
            List<KeyValuePair<string, int>> top = Boxes(init, 0, init.Length);
            Equal("ftyp moov", top[0].Key + " " + top[1].Key, "top-level boxes");
            int moov = top[0].Value;
            List<KeyValuePair<string, int>> inMoov = Boxes(init, moov + 8, moov + top[1].Value);
            Equal("mvhd trak mvex", inMoov[0].Key + " " + inMoov[1].Key + " " + inMoov[2].Key, "inside moov");
            int avcC = Find(init, "avcC");
            True(avcC > 0, "avcC present");
            Equal(1, (int)init[avcC + 8], "avcC version");
            Equal(0x64, (int)init[avcC + 9], "profile from the SPS");
            Equal(0xFF, (int)init[avcC + 12], "4-byte NAL lengths");
            Equal(0xE1, (int)init[avcC + 13], "one SPS");
            int avc1 = Find(init, "avc1", Find(init, "stsd")); // "avc1" is also a brand in ftyp
            Equal(1920, init[avc1 + 32] << 8 | init[avc1 + 33], "width in avc1");
            Equal(1080, init[avc1 + 34] << 8 | init[avc1 + 35], "height in avc1");
        }

        static void TestFragment()
        {
            byte[] sample = { 0, 0, 0, 3, 0x65, 0xAA, 0xBB };
            byte[] f = Fmp4.Fragment(7, 3000, 1500, true, sample);
            List<KeyValuePair<string, int>> top = Boxes(f, 0, f.Length);
            Equal("moof mdat", top[0].Key + " " + top[1].Key, "moof + mdat");
            int trun = Find(f, "trun");
            int dataOffset = f[trun + 16] << 24 | f[trun + 17] << 16 | f[trun + 18] << 8 | f[trun + 19];
            Equal(Hex(sample), Hex(Slice(f, dataOffset, sample.Length)), "trun data offset points at the sample");
            Equal("02000000", Hex(Slice(f, trun + 28, 4)), "keyframe: sync sample");
            Equal("000005dc", Hex(Slice(f, trun + 20, 4)), "duration 1500 = 1/60 s at 90 kHz");
            int tfdt = Find(f, "tfdt");
            Equal("0000000000000bb8", Hex(Slice(f, tfdt + 12, 8)), "decode time 3000");
            byte[] p = Fmp4.Fragment(8, 4500, 1500, false, sample);
            Equal("01010000", Hex(Slice(p, Find(p, "trun") + 28, 4)), "other frames: not sync, depend on others");
        }

        static byte[] Slice(byte[] b, int o, int n)
        {
            byte[] r = new byte[n];
            Buffer.BlockCopy(b, o, r, 0, n);
            return r;
        }

        // ---- iPad mode: HTTP and WebSocket ---------------------------------------------------

        static void TestParseRequest()
        {
            Dictionary<string, string> h = new Dictionary<string, string>();
            Equal("/ws", WebCast.ParseRequest("GET /ws HTTP/1.1\r\nHost: 10.0.0.2:47900\r\nUpgrade: websocket\r\n\r\n", h), "path");
            Equal("10.0.0.2:47900", h["host"], "header names are lower case");
            Equal(null, WebCast.ParseRequest("POST / HTTP/1.1\r\n\r\n", new Dictionary<string, string>()), "only GET");
            Equal(null, WebCast.ParseRequest("GET /\r\n\r\n", new Dictionary<string, string>()), "HTTP/0.9 style refused");
        }

        static void TestReadRequestLimits()
        {
            Dictionary<string, string> h;
            byte[] ok = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: x\r\n\r\n");
            Equal("/", WebCast.ReadRequest(new MemoryStream(ok), out h), "normal request");
            byte[] big = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nX: " + new string('a', 9000) + "\r\n\r\n");
            Equal(null, WebCast.ReadRequest(new MemoryStream(big), out h), "headers over 8 KB refused");
            Equal(null, WebCast.ReadRequest(new MemoryStream(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n")), out h), "cut off");
        }

        static void TestSameOrigin()
        {
            True(WebCast.SameOrigin("http://10.0.0.2:47900", "10.0.0.2:47900"), "our own page");
            True(WebCast.SameOrigin("https://10.0.0.2:47900", "10.0.0.2:47900"), "our own page over HTTPS");
            True(!WebCast.SameOrigin("https://evil.example", "10.0.0.2:47900"), "another HTTPS site");
            True(!WebCast.SameOrigin("http://evil.example", "10.0.0.2:47900"), "another site");
            True(!WebCast.SameOrigin(null, "10.0.0.2:47900"), "no Origin header");
        }

        static void TestWebSocketAccept()
        {
            // the example from RFC 6455, section 1.3
            Equal("s3pPLMBiTxaQ9kYGzzhZRbK+xOo=", WebCast.WebSocketAccept("dGhlIHNhbXBsZSBub25jZQ=="), "accept key");
        }

        static void TestFrameHeader()
        {
            Equal("8205", Hex(WebCast.FrameHeader(2, 5)), "short");
            Equal("827e0100", Hex(WebCast.FrameHeader(2, 256)), "16-bit length");
            Equal("827f0000000000010000", Hex(WebCast.FrameHeader(2, 65536)), "64-bit length");
        }

        static byte[] ClientFrame(int op, byte[] payload, bool mask)
        {
            MemoryStream m = new MemoryStream();
            m.WriteByte((byte)(0x80 | op));
            byte[] key = { 1, 2, 3, 4 };
            if (payload.Length < 126) m.WriteByte((byte)((mask ? 0x80 : 0) | payload.Length));
            else { m.WriteByte((byte)((mask ? 0x80 : 0) | 126)); m.WriteByte((byte)(payload.Length >> 8)); m.WriteByte((byte)payload.Length); }
            if (mask) m.Write(key, 0, 4);
            for (int i = 0; i < payload.Length; i++) m.WriteByte((byte)(payload[i] ^ (mask ? key[i & 3] : 0)));
            return m.ToArray();
        }

        static void TestReadFrame()
        {
            byte[] hello = { 0x10, (byte)'1', (byte)'2', (byte)'3', (byte)'4', (byte)'5', (byte)'6' };
            byte[] got;
            int pings = 0;
            MemoryStream s = new MemoryStream();
            byte[] ping = ClientFrame(9, new byte[] { 7 }, true), msg = ClientFrame(2, hello, true);
            s.Write(ping, 0, ping.Length); s.Write(msg, 0, msg.Length); s.Position = 0;
            Equal(2, WebCast.ReadFrame(s, out got, delegate(int op, byte[] d) { pings++; }), "binary after a ping");
            Equal(1, pings, "ping handed to the callback");
            Equal(Hex(hello), Hex(got), "unmasked payload");
            Equal(-1, WebCast.ReadFrame(new MemoryStream(ClientFrame(2, hello, false)), out got, null), "unmasked frames from a page are refused");
            Equal(-1, WebCast.ReadFrame(new MemoryStream(ClientFrame(2, new byte[600], true)), out got, null), "over 512 bytes refused");
            Equal(-1, WebCast.ReadFrame(new MemoryStream(ClientFrame(8, new byte[0], true)), out got, null), "close");
            Equal(-1, WebCast.ReadFrame(new MemoryStream(new byte[] { 0x82 }), out got, null), "cut off");
        }

        static void TestPageEmbedded()
        {
            string page = Encoding.UTF8.GetString(WebCast.Page());
            True(page.Contains("ManagedMediaSource"), "the iPad page is inside the exe");
            True(page.Contains("CastLink") && page.Contains("CastLink"), "page in both languages");
        }

        static void TestHomeScreenIcon()
        {
            byte[] png = WebCast.Icon();
            Equal("89504e47", Hex(Slice(png, 0, 4)), "a PNG");
            Equal(180, png[16] << 24 | png[17] << 16 | png[18] << 8 | png[19], "180 px wide (IHDR)");
            string page = Encoding.UTF8.GetString(WebCast.Page());
            True(page.Contains("/manifest.webmanifest") && page.Contains("apple-touch-icon"), "the page links the manifest and the icon");
        }

        static void TestLocalUrl()
        {
            string u = WebCast.LocalUrl();
            foreach (string one in u.Split(new[] { "  ·  " }, StringSplitOptions.None))
                True(one.StartsWith("http://") && one.Contains(":" + WebCast.PORT), one);
        }

        static void TestReceiverNameFromTheList()
        {
            Equal("Living room TV", Caster.NameOf("Living room TV (10.0.0.5)"), "name before the address");
            Equal("My (old) phone", Caster.NameOf("My (old) phone (10.0.0.7)"), "brackets in the name");
            True(Caster.NameOf("10.0.0.5") == null, "a typed address has no name");
            True(Caster.NameOf(null) == null, "nothing");
        }

        static void TestNameAnswerFromTheCable()
        {
            byte[] utf = Encoding.UTF8.GetBytes("Žalias phone");
            byte[] pkt = new byte[1 + utf.Length];
            pkt[0] = Proto.NAME;
            Buffer.BlockCopy(utf, 0, pkt, 1, utf.Length);
            Equal("Žalias phone", Discovery.NameReply(pkt, pkt.Length), "UTF-8 name");
            True(Discovery.NameReply(new byte[] { Proto.NAME }, 1) == null, "empty");
            True(Discovery.NameReply(new byte[] { Proto.PING, 65 }, 2) == null, "another packet");
        }

        static void TestUsbNetworkAskedUntilLimited()
        {
            var ruled = new List<string> { "Ethernet 2" };
            True(UsbRoute.Decide(7, "Ethernet 3", ruled) != null, "a phone network without our rules: ask");
            True(UsbRoute.Decide(7, "ethernet 2", ruled) == null, "already limited (names ignore case in Windows)");
            True(UsbRoute.Decide(0, "Ethernet 3", ruled) == null, "no interface");
        }

        static void TestUsbScriptBlocksOnlyTheInternet()
        {
            string s = UsbRoute.Script(7);
            True(s.Contains("$i = 7\n"), "only the number comes in");
            True(s.Contains("-InterfaceMetric 9000"), "Wi-Fi / LAN win");
            True(s.Contains("fMinimizeConnections /t REG_DWORD /d 0"), "wired does not beat the metric");
            True(s.Contains("-InterfaceAlias $a -RemoteAddress '0.0.0.0-9.255.255.255'"), "public IPv4 blocked on that network only");
            True(s.Contains("'2000::/3'") && s.Contains("-RemotePort 53"), "IPv6 and DNS blocked");
            foreach (string r in UsbRoute.Public4)
            {
                string[] ends = r.Split('-');
                foreach (string e in ends)
                {
                    byte[] b = IPAddress.Parse(e).GetAddressBytes();
                    True(!(b[0] == 10 || (b[0] == 192 && b[1] == 168) || (b[0] == 172 && b[1] >= 16 && b[1] < 32)), "a private address is not blocked: " + e);
                }
            }
            string arg = UsbRoute.Arguments(7);
            string back = Encoding.Unicode.GetString(Convert.FromBase64String(arg.Substring(arg.LastIndexOf(' ') + 1)));
            Equal(s, back, "the script travels encoded (no quoting)");
        }

        static void TestTypingOnlyOnTheShownScreen()
        {
            // main screen 0..1920 x 0..1080, a virtual one to its right 1920..4480 x 0..1440
            int[] main = { 0, 0, 1920, 1080 }, virt = { 1920, 0, 4480, 1440 };
            int[] shownVirtual = { 1920, 0, 2560, 1440 };
            True(RemoteInput.SameScreen(virt, shownVirtual), "a window on the shown virtual screen gets the text");
            True(!RemoteInput.SameScreen(main, shownVirtual), "a window on the main screen does not");
            True(RemoteInput.SameScreen(main, new[] { 0, 0, 1920, 1080 }), "showing the main screen: its windows get it");
        }

        static void TestWindowsMoveBackToTheMainScreen()
        {
            int[] virt = { 1920, 0, 2560, 1440 }, main = { 0, 0, 1920, 1032 }; // main work area (taskbar)
            True(WindowMover.CentreOn(new[] { 2000, 100, 800, 600 }, virt), "a window on the virtual screen");
            True(!WindowMover.CentreOn(new[] { 100, 100, 800, 600 }, virt), "a window on the main screen stays");
            int[] r = WindowMover.MapRect(new[] { 1920, 0, 800, 600 }, virt, main);
            Equal(0, r[0], "top-left stays top-left: x");
            Equal(0, r[1], "top-left stays top-left: y");
            r = WindowMover.MapRect(new[] { 1920 + 2560 - 800, 1440 - 600, 800, 600 }, virt, main);
            Equal(1920 - 800, r[0], "bottom-right stays bottom-right: x");
            Equal(1032 - 600, r[1], "bottom-right stays bottom-right: y");
            r = WindowMover.MapRect(new[] { 1920, 0, 2560, 1440 }, virt, main);
            Equal(1920, r[2], "too wide: fits the main screen");
            Equal(1032, r[3], "too tall: fits the main screen");
        }

        static void TestWindowFoundWhereItReallyIs()
        {
            int[] virt = { 1920, 0, 2560, 1440 };
            int[] onVirtual = { 1912, -8, 2576, 1456 }, normalOnMain = { 200, 150, 900, 700 };
            // maximized (3) or snapped (1) on the virtual screen, normal position still on the main one
            True(WindowMover.CentreOn(WindowMover.WhereItIs(3, onVirtual, normalOnMain), virt), "a maximized window is where it is shown");
            True(WindowMover.CentreOn(WindowMover.WhereItIs(1, new[] { 1920, 0, 1280, 1440 }, normalOnMain), virt), "a snapped window is where it is shown");
            // minimized (2): its rectangle is off screen, the restored position decides
            True(WindowMover.CentreOn(WindowMover.WhereItIs(2, new[] { -32000, -32000, 160, 28 }, new[] { 2200, 300, 900, 700 }), virt), "a minimized window by its restored position");
            True(!WindowMover.CentreOn(WindowMover.WhereItIs(2, new[] { -32000, -32000, 160, 28 }, normalOnMain), virt), "a minimized window that belongs on the main screen stays");
        }

        static void TestGpuPreferenceValue()
        {
            Equal(1, MainForm.ParseGpuPreference("GpuPreference=1;"), "power saving");
            Equal(2, MainForm.ParseGpuPreference("SwapEffectUpgradeEnable=1;GpuPreference=2;"), "high performance among other values");
            Equal(0, MainForm.ParseGpuPreference(null), "not set");
            Equal(0, MainForm.ParseGpuPreference("GpuPreference="), "broken value");
        }

        // ---- this PC as a receiver (Receiver.cs) ----

        static byte[] Chunk(int frame, int chunk, int count, bool key, params byte[] payload)
        {
            byte[] p = new byte[Proto.VIDEO_HEADER + payload.Length];
            Proto.PutVideoHeader(p, key, frame, chunk, count, 1000 + frame);
            Buffer.BlockCopy(payload, 0, p, Proto.VIDEO_HEADER, payload.Length);
            return p;
        }

        static void TestReceiverPutsChunksTogether()
        {
            Assembler a = new Assembler(new byte[] { 9 });
            byte[] c1 = Chunk(1, 1, 2, true, 3, 4), c0 = Chunk(1, 0, 2, true, new byte[Proto.MAX_PAYLOAD]);
            True(a.Accept(c1, c1.Length) == null, "half a frame: nothing yet");
            Assembler.Frame f = a.Accept(c0, c0.Length);
            True(f != null && f.Key, "chunks in any order make the keyframe");
            Equal(Proto.MAX_PAYLOAD + 2 + 1, f.Data.Length, "both payloads plus the suffix");
            Equal(9, (int)f.Data[f.Data.Length - 1], "suffix at the end");
            Equal(1001L, f.PtsUs, "the frame's pts");
        }

        static void TestReceiverWaitsForAKeyframeAfterALoss()
        {
            Assembler a = new Assembler(new byte[0]);
            byte[] k = Chunk(1, 0, 1, true, 1);
            True(a.Accept(k, k.Length) != null && !a.WantsKey, "keyframe 1");
            byte[] p3 = Chunk(3, 0, 1, false, 3); // frame 2 never came
            True(a.Accept(p3, p3.Length) == null, "a P-frame after a gap is garbage: dropped");
            True(a.WantsKey && a.Lost == 1, "asks for a keyframe and counts the loss");
            byte[] p4 = Chunk(4, 0, 1, false, 4);
            True(a.Accept(p4, p4.Length) == null, "still waiting for a keyframe");
            byte[] k5 = Chunk(5, 0, 1, true, 5);
            True(a.Accept(k5, k5.Length) != null && !a.WantsKey, "a keyframe starts the picture again");
            byte[] old = Chunk(2, 0, 1, false, 2);
            True(a.Accept(old, old.Length) == null, "a late chunk of an older frame is ignored");
        }

        static void TestReceiverRefusesBrokenChunks()
        {
            Assembler a = new Assembler(new byte[0]);
            byte[] bad = Chunk(1, 2, 2, true, 1); // chunk index past the count
            True(a.Accept(bad, bad.Length) == null, "chunk >= count");
            byte[] zero = Chunk(1, 0, 0, true, 1);
            True(a.Accept(zero, zero.Length) == null, "count 0");
            True(a.Accept(new byte[5], 5) == null, "shorter than the header");
        }

        static void TestReceiverHelloOkMatchesJava()
        {
            byte[] n = new byte[SecureLink.NONCE], e = new byte[SecureLink.POINT], s = new byte[SecureLink.POINT], echo = new byte[SecureLink.NONCE];
            for (int i = 0; i < n.Length; i++) { n[i] = (byte)(0xA0 + i); echo[i] = (byte)(1 + i); }
            e[0] = s[0] = 4;
            byte[] b = SecureLink.MakeHelloOk(n, e, s, echo, true, "PC-1");
            Equal(SecureLink.HELLO_OK_FIXED + 4, b.Length, "fixed part + the name");
            Equal((int)SecureLink.HELLO_OK, (int)b[0], "type");
            Equal(1, (int)b[SecureLink.HELLO_OK_FIXED - 1], "knowsYou byte before the name");
            SecureLink.Hello h = SecureLink.Parse(b, b.Length);
            True(h != null && h.KnowsYou && h.Name == "PC-1" && SecureLink.Same(h.Echo, echo) && SecureLink.Same(h.Nonce, n),
                "the caster reads it back field by field");
        }

        static void TestFindAsksTheWholeHomeNetwork()
        {
            Equal("192.168.0.255", Discovery.Broadcast(IPAddress.Parse("192.168.0.20"), IPAddress.Parse("255.255.255.0")).ToString(), "a /24");
            Equal("10.0.255.255", Discovery.Broadcast(IPAddress.Parse("10.0.0.5"), IPAddress.Parse("255.255.0.0")).ToString(), "a /16");
            True(Discovery.Broadcast(IPAddress.Parse("10.0.0.5"), IPAddress.Parse("255.255.255.255")) == null, "a /32 has no broadcast");
        }

        static void TestGpuPreferenceFollowsTheScreensCard()
        {
            Equal(2, MainForm.WantedGpuPreference(true), "a monitor on the NVIDIA card (HDMI): High performance");
            Equal(1, MainForm.WantedGpuPreference(false), "the laptop panel on the integrated card: Power saving");
            True(Capture.IsNvidiaDeviceId("PCI\\VEN_10DE&DEV_28A0&SUBSYS_13AD1462&REV_A1"), "an NVIDIA card drives the screen");
            True(!Capture.IsNvidiaDeviceId("PCI\\VEN_1002&DEV_1681&SUBSYS_13AD1462&REV_C8"), "an AMD card");
            True(!Capture.IsNvidiaDeviceId("Root\\MttVDD"), "the virtual display driver");
            True(!Capture.IsNvidiaDeviceId(null), "unknown");
        }

        static void TestVirtualDisplayLink()
        {
            Uri u = new Uri(MainForm.VirtualDisplayUrl);
            Equal("https", u.Scheme, "https only");
            Equal("github.com", u.Host, "the driver's own project page");
        }

        static void TestPictureSizeFollowsTheReceiver()
        {
            int w, h;
            Caster.SizeLimit(false, false, 2340, 1080, out w, out h);
            Equal(2340, w, "a phone over Wi-Fi: its own width"); Equal(1080, h, "and height");
            Caster.SizeLimit(false, false, 3840, 2160, out w, out h);
            Equal(2560, w, "a 4K screen: capped"); Equal(1600, h, "capped height");
            Caster.SizeLimit(false, false, 0, 0, out w, out h);
            Equal(Caster.MaxW, w, "an older receiver: the old limit");
            Caster.SizeLimit(true, false, 2340, 1080, out w, out h);
            Equal(Caster.WebMaxW, w, "the iPad page keeps its own limit");
            Equal(20000000, Caster.ScaledBitrate(20000000, 1920, 1080, false), "1080p: as before");
            Equal(24375000, Caster.ScaledBitrate(20000000, 2340, 1080, false), "phone size: more bits");
            Equal(32000000, Caster.ScaledBitrate(20000000, 2560, 1600, false), "at most 1.6x");
            Equal(50000000, Caster.ScaledBitrate(50000000, 2340, 1080, true), "USB keeps its rate");
        }

        static void TestRouteToTheSameReceiver()
        {
            IPAddress now = IPAddress.Parse("10.0.0.5"), other = IPAddress.Parse("10.0.0.9");
            var found = new List<KeyValuePair<string, IPAddress>> {
                new KeyValuePair<string, IPAddress>("Phone", now),
                new KeyValuePair<string, IPAddress>("TV", IPAddress.Parse("10.0.0.7")),
                new KeyValuePair<string, IPAddress>("Phone", other),
            };
            Equal(other, Caster.PickRoute(found, "Phone", now, true), "went quiet: its other address");
            True(Caster.PickRoute(found, "Phone", now, false) == null, "still answering, no cable: stay");
            True(Caster.PickRoute(found, "Laptop", now, true) == null, "never another device");
            True(Caster.PickRoute(new List<KeyValuePair<string, IPAddress>>(), "Phone", now, true) == null, "not found");
        }

        static void TestUsbSubnet()
        {
            IPAddress mask = IPAddress.Parse("255.255.255.0");
            True(WebCast.SameSubnet(IPAddress.Parse("10.1.2.227"), IPAddress.Parse("10.1.2.26"), mask), "the phone on the cable");
            True(!WebCast.SameSubnet(IPAddress.Parse("10.1.3.5"), IPAddress.Parse("10.1.2.26"), mask), "another network");
            True(!WebCast.SameSubnet(IPAddress.Parse("::1"), IPAddress.Parse("10.1.2.26"), mask), "IPv6");
        }

        static void TestUsbTetheringAdapter()
        {
            True(WebCast.IsUsbTethering("SAMSUNG Mobile USB Remote NDIS Network Device"), "Samsung");
            True(WebCast.IsUsbTethering("Remote NDIS Compatible Device #2"), "generic Android");
            True(!WebCast.IsUsbTethering("RZ616 Wi-Fi 6E 160MHz"), "Wi-Fi");
            True(!WebCast.IsUsbTethering("Realtek USB GbE Family Controller"), "a USB Ethernet dongle is a normal network");
            True(!WebCast.IsUsbTethering(null), "no description");
        }

        static void TestPcIsLanV4()
        {
            True(Proto.IsLanV4(new byte[] { 192, 168, 1, 2 }), "192.168");
            True(Proto.IsLanV4(new byte[] { 172, 31, 0, 1 }), "172.31");
            True(!Proto.IsLanV4(new byte[] { 172, 32, 0, 1 }), "172.32");
            True(!Proto.IsLanV4(new byte[] { 8, 8, 8, 8 }), "internet");
            True(!Proto.IsLanV4(new byte[16]), "IPv6");
        }

        static void TestFrameDuration()
        {
            Equal(1500u, WebCast.FrameDuration(-1, 123), "first frame: 1/60 s");
            Equal(1500u, WebCast.FrameDuration(1000000, 1016667), "60 fps");
            Equal(2250u, WebCast.FrameDuration(1000000, 1025000), "40 fps: the real 25 ms, not 1/60");
            Equal(750u, WebCast.FrameDuration(1000000, 1001000), "at most 120 fps");
            Equal(9000u, WebCast.FrameDuration(1000000, 6000000), "a still screen counts at most 0.1 s");
            Equal(750u, WebCast.FrameDuration(1000000, 900000), "time going backwards: the minimum");
        }

        // ---- iPad mode: the local CA ----------------------------------------------------------

        /// <summary>The CA may only vouch for private IPv4 ranges and *.invalid: decoded by
        /// openssl as IP:10.0.0.0/255.0.0.0, 172.16.0.0/255.240.0.0, 192.168.0.0/255.255.0.0,
        /// 169.254.0.0/255.255.0.0, 127.0.0.0/255.0.0.0, DNS:invalid</summary>
        static void TestNameConstraints()
        {
            Equal("3049a047"
                + "300a87080a000000ff000000"
                + "300a8708ac100000fff00000"
                + "300a8708c0a80000ffff0000"
                + "300a8708a9fe0000ffff0000"
                + "300a87087f000000ff000000"
                + "30098207696e76616c6964", Hex(LocalTls.NameConstraints()), "permitted subtrees");
        }

        static void TestDerLengths()
        {
            Equal("0403010203", Hex(LocalTls.Tlv(4, new byte[] { 1, 2, 3 })), "short form");
            Equal("0481c8", Hex(LocalTls.Tlv(4, new byte[200])).Substring(0, 6), "one length byte");
            Equal("0482012c", Hex(LocalTls.Tlv(4, new byte[300])).Substring(0, 8), "two length bytes");
            Equal("3006800401020304", Hex(LocalTls.AuthorityKeyId(new byte[] { 1, 2, 3, 4 })), "authority key id");
        }

        // ---- iPad mode: security ----------------------------------------------------------------

        static void TestCodeGuessingIsThrottled()
        {
            AuthThrottle t = new AuthThrottle();
            long now = 1000000;
            Equal(0L, t.WaitMs(now), "the first try is allowed");
            True(!t.Fail(now), "one wrong code does not lock");
            Equal(2000L, t.WaitMs(now), "2 s until the next try");
            Equal(0L, t.WaitMs(now + 2000), "allowed again after 2 s");
            for (int i = 2; i <= 4; i++) { now += 2000; True(!t.Fail(now), "wrong code " + i); }
            now += 2000;
            True(t.Fail(now), "the 5th wrong code locks (and the caller makes a new code)");
            Equal(5 * 60 * 1000L, t.WaitMs(now), "locked for 5 minutes");
            Equal(0L, t.WaitMs(now + 5 * 60 * 1000), "open again after 5 minutes");
            now += 5 * 60 * 1000;
            t.Fail(now); t.Success();
            now += 2000;
            for (int i = 1; i <= 4; i++) { True(!t.Fail(now), "after a success the count starts over " + i); now += 2000; }
        }

        static void TestOnlyOwnAddressAsHost()
        {
            True(WebCast.IsOwnAddress("192.168.1.20:47900"), "LAN address with port");
            True(WebCast.IsOwnAddress("192.168.137.1"), "hotspot address without port");
            True(WebCast.IsOwnAddress("127.0.0.1:47900"), "this PC");
            True(!WebCast.IsOwnAddress("evil.example:47900"), "a name: DNS rebinding");
            True(!WebCast.IsOwnAddress("localhost:47900"), "names are refused, also localhost");
            True(!WebCast.IsOwnAddress("8.8.8.8:47900"), "an internet address");
            True(!WebCast.IsOwnAddress("10.1:47900"), "short IP forms");
            True(!WebCast.IsOwnAddress("192.168.1.20:99999"), "bad port");
            True(!WebCast.IsOwnAddress("[::1]:47900"), "IPv6");
            True(!WebCast.IsOwnAddress(null) && !WebCast.IsOwnAddress(""), "no Host header");
        }

        static void TestCaFingerprintFormat()
        {
            // SHA-256("abc") = ba7816bf 8f01cfea ..., shown like the iPad does: upper case, spaced
            Equal("BA 78 16 BF 8F 01 CF EA", LocalTls.Fingerprint(Encoding.ASCII.GetBytes("abc")), "first 8 bytes");
        }

        static void TestPageHandlesLockout()
        {
            string page = Encoding.UTF8.GetString(WebCast.Page());
            True(page.Contains("m.t === \"locked\""), "the page explains a lockout");
            True(page.Contains("SHA-256"), "the install steps ask to compare the fingerprint");
        }

        // ---- iPad mode: WebRTC ----------------------------------------------------------------

        static void TestOnlyLanCandidates()
        {
            True(RtcLink.IsLanCandidate("candidate:254334199 1 udp 2113937663 192.168.1.20 61352 typ host generation 0"), "home network IPv4");
            True(RtcLink.IsLanCandidate("candidate:1 1 udp 2113937663 10.0.0.7 5000 typ host"), "10.x");
            True(!RtcLink.IsLanCandidate("candidate:5 1 udp 2113940223 2001:db8::1 61352 typ host generation 0"), "IPv6 (public addresses of the PC or the iPad)");
            True(!RtcLink.IsLanCandidate("candidate:6 1 udp 1677729535 8.8.8.8 3478 typ srflx raddr 0.0.0.0 rport 0"), "an internet address");
            True(RtcLink.IsLanCandidate("candidate:7 1 udp 2113937663 3f2a9c1e-1d2b.local 61352 typ host"), "an mDNS name (how browsers hide their LAN address)");
            True(!RtcLink.IsLanCandidate("candidate:8 1 udp 2113937663 evil.example 61352 typ host"), "any other name");
            True(!RtcLink.IsLanCandidate("") && !RtcLink.IsLanCandidate(null) && !RtcLink.IsLanCandidate("candidate:1 1 udp"), "malformed");
        }

        static void TestSdpKeepsOnlyLanCandidates()
        {
            string sdp = "v=0\r\nm=video 9 UDP/TLS/RTP/SAVP 96\r\n"
                + "a=candidate:1 1 udp 2113937663 192.168.1.20 61352 typ host generation 0\r\n"
                + "a=candidate:2 1 udp 2113940223 2001:db8::5 61352 typ host generation 0\r\n"
                + "a=rtpmap:96 H264/90000\r\n";
            string f = RtcLink.FilterSdp(sdp);
            True(f.Contains("192.168.1.20") && !f.Contains("2001:db8"), "IPv6 candidate removed, LAN kept");
            True(f.Contains("a=rtpmap:96 H264/90000\r\n") && f.StartsWith("v=0\r\n"), "everything else unchanged, CRLF line ends");
        }

        static void TestRtpPacketizing()
        {
            byte[] sps = { 0x67, 1, 2 }, big = new byte[3000];
            big[0] = 0x65; // IDR slice, NRI 3
            for (int i = 1; i < big.Length; i++) big[i] = (byte)i;
            List<ArraySegment<byte>> nals = new List<ArraySegment<byte>> {
                new ArraySegment<byte>(new byte[] { 0x09, 0xF0 }), new ArraySegment<byte>(sps), new ArraySegment<byte>(big) };
            List<byte[]> p = RtcLink.Packetize(nals, 1200);
            Equal(Hex(sps), Hex(p[0]), "a small NAL goes alone, the AUD is left out");
            Equal(4, p.Count, "3000-byte slice -> 3 FU-A packets");
            Equal("7c85", Hex(Slice(p[1], 0, 2)), "FU indicator 0x7C (NRI kept, type 28), header start + type 5");
            Equal("05", Hex(Slice(p[2], 1, 1)), "middle fragment: no start, no end");
            Equal("45", Hex(Slice(p[3], 1, 1)), "last fragment: end bit");
            int total = 1;
            for (int i = 1; i < 4; i++) total += p[i].Length - 2;
            Equal(3000, total, "fragments add up to the NAL");
            True(p.TrueForAll(x => x.Length <= 1200), "no packet over the limit");
        }

        static void TestTouchLandsOnTheShownScreen()
        {
            // main screen 1920x1080 at 0,0; a virtual phone-shaped screen 2340x1080 to its right
            int[] virt = { 1920, 0, 2340, 1080 };
            int vl = 0, vt = 0, vw = 1920 + 2340, vh = 1080;
            int[] tl = RemoteInput.ToVirtualDesk(0, 0, virt, vl, vt, vw, vh);
            Equal((int)Math.Round(1920 * 65535.0 / (vw - 1)), tl[0], "left edge of the virtual screen");
            Equal(0, tl[1], "top");
            int[] br = RemoteInput.ToVirtualDesk(65535, 65535, virt, vl, vt, vw, vh);
            Equal(65535, br[0], "right edge of the desktop");
            Equal(65535, br[1], "bottom");
            int[] main = RemoteInput.ToVirtualDesk(65535, 0, new[] { 0, 0, 1920, 1080 }, vl, vt, vw, vh);
            Equal((int)Math.Round(1919 * 65535.0 / (vw - 1)), main[0], "the main screen ends where the virtual one begins");
            // a screen left of the main one: negative desktop coordinates
            int[] left = RemoteInput.ToVirtualDesk(0, 0, new[] { -1280, 0, 1280, 720 }, -1280, 0, 1280 + 1920, 1080);
            Equal(0, left[0], "left screen, left edge");
        }

        static void TestScreenChoice()
        {
            string[] dev = { "DISPLAY1", "DISPLAY2", "DISPLAY3" };
            bool[] prim = { false, true, false };
            Equal(2, Capture.PickScreen(dev, prim, "DISPLAY3"), "the chosen one");
            Equal(1, Capture.PickScreen(dev, prim, ""), "nothing chosen: the primary");
            Equal(1, Capture.PickScreen(dev, prim, "DISPLAY9"), "chosen one unplugged: the primary");
            // saved as "device|model": Windows renames DISPLAYn when a virtual monitor comes back
            var keys = new List<string> { "DISPLAY1|MONITOR\\AAA1", "DISPLAY11|MONITOR\\VVV9" };
            var kp = new List<bool> { true, false };
            Equal(1, Capture.PickScreen(keys, kp, "DISPLAY10|MONITOR\\VVV9"), "renamed: found by its model");
            Equal(1, Capture.PickScreen(keys, kp, "DISPLAY11|MONITOR\\VVV9"), "same name and model");
            Equal(0, Capture.PickScreen(keys, kp, "DISPLAY11|MONITOR\\GONE"), "that name is another monitor now: the primary");
            Equal(1, Capture.PickScreen(keys, kp, "DISPLAY11"), "old settings: by the name");
            Equal("MONITOR\\VVV9", Capture.HardwareId("MONITOR\\VVV9\\{4d36e96e}\\0003"), "model without the instance");
            Equal("", Capture.HardwareId(""), "no monitor");
            Equal(0, Capture.PickScreen(dev, new[] { false, false, false }, null), "no primary: the first");
            Equal(-1, Capture.PickScreen(new string[0], new bool[0], ""), "no screen");
        }

        static void TestPhoneInputNeedsTheToken()
        {
            byte[] token = { 1, 2, 3, 4, 5, 6, 7, 8 };
            byte[] pkt = { Proto.INPUT, 1, 2, 3, 4, 5, 6, 7, 8, 5, 13 }; // KEY Enter
            Equal("050d", Hex(Caster.InputMessage(pkt, pkt.Length, token)), "the input message after the token");
            byte[] wrong = (byte[])pkt.Clone(); wrong[8] = 9;
            True(Caster.InputMessage(wrong, wrong.Length, token) == null, "another token: refused");
            True(Caster.InputMessage(pkt, 9, token) == null, "cut short: refused");
            byte[] other = (byte[])pkt.Clone(); other[0] = Proto.PING;
            True(Caster.InputMessage(other, other.Length, token) == null, "not INPUT");
        }

        static void TestOnlyLanEndPointsStream()
        {
            True(RtcLink.IsLanEndPoint(new IPEndPoint(IPAddress.Parse("192.168.1.20"), 5000)), "home network");
            True(RtcLink.IsLanEndPoint(new IPEndPoint(IPAddress.Parse("::ffff:10.0.0.7"), 5000)), "IPv4 written as IPv6");
            True(!RtcLink.IsLanEndPoint(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 5000)), "internet address (a lying mDNS answer)");
            True(!RtcLink.IsLanEndPoint(new IPEndPoint(IPAddress.Parse("2001:db8::1"), 5000)), "IPv6");
            True(!RtcLink.IsLanEndPoint(null), "no address yet");
            True(RtcLink.MaxResendPerReport <= 64, "resends per RTCP report are capped");
        }

        static void TestRtcpFeedbackReading()
        {
            const uint me = 0x11223344;
            byte[] p = {
                0x81, 201, 0, 7,  0, 0, 0, 1,  0x11, 0x22, 0x33, 0x44,  0, 0, 0, 0,  0, 0, 0, 0,  0, 0, 0, 0,  0, 0, 0, 0,  0, 0, 0, 0, // RR, one block
                0x81, 205, 0, 4,  0, 0, 0, 1,  0x11, 0x22, 0x33, 0x44,  0x03, 0xE8, 0x00, 0x05,  0x07, 0xD0, 0, 0, // NACK: 1000 (+1001, +1003), 2000
                0x8F, 205, 0, 2,  0, 0, 0, 1,  0x11, 0x22, 0x33, 0x44, // transport-cc (ignored)
                0x81, 206, 0, 2,  0, 0, 0, 1,  0x11, 0x22, 0x33, 0x44  // PLI
            };
            List<ushort> lost = new List<ushort>();
            bool key;
            RtcLink.ReadFeedback(p, p.Length, me, lost, out key);
            Equal("1000,1001,1003,2000", string.Join(",", lost), "every NACK entry and BLP bit, after an RR, next to transport-cc");
            True(key, "PLI in the same compound packet");
            lost.Clear();
            RtcLink.ReadFeedback(p, p.Length, 0x55555555, lost, out key);
            True(lost.Count == 0 && !key, "feedback for another SSRC is ignored");
            byte[] fir = { 0x84, 206, 0, 4,  0, 0, 0, 1,  0, 0, 0, 0,  0x11, 0x22, 0x33, 0x44,  1, 0, 0, 0 };
            RtcLink.ReadFeedback(fir, fir.Length, me, lost, out key);
            True(key, "FIR names the SSRC in its entry");
            RtcLink.ReadFeedback(p, 30, me, lost, out key); // cut short: no exception
        }

        static void TestOfferAnnouncesNackAndPli()
        {
            string sdp = "m=video 9 UDP/TLS/RTP/SAVPF 96\r\na=rtpmap:96 H264/90000\r\na=rtcp-fb:96 transport-cc\r\n";
            string f = RtcLink.AddVideoFeedback(sdp);
            True(f.Contains("a=rtpmap:96 H264/90000\r\na=rtcp-fb:96 nack\r\na=rtcp-fb:96 nack pli\r\na=rtcp-fb:96 ccm fir\r\n"), "added after the rtpmap");
            Equal(f, RtcLink.AddVideoFeedback(f), "added only once");
        }

        static void TestJsonStrings()
        {
            Equal("\"a\\\"b\\\\c\\r\\nd\"", RtcLink.Json("a\"b\\c\r\nd"), "quotes, backslash, line ends");
            Equal("\"\\u0001\"", RtcLink.Json("\u0001"), "control characters");
            Equal("null", RtcLink.Json(null), "null");
        }

        static void TestTextFramesMayBeLarger()
        {
            byte[] got;
            string answer = new string('x', 3000); // an SDP answer is 2-4 KB
            Equal(1, WebCast.ReadFrame(new MemoryStream(ClientFrame(1, Encoding.ASCII.GetBytes(answer), true)), out got, null), "a 3 KB text message is read");
            Equal(-1, WebCast.ReadFrame(new MemoryStream(ClientFrame(2, new byte[3000], true)), out got, null), "binary input stays limited to 512 bytes");
        }

        static void TestPairingCodeKept()
        {
            Equal("123456", new WebCast("123456").Code, "saved code is used again");
            True(WebCast.IsCode(new WebCast(null).Code), "new 6-digit code when none is saved");
            True(new WebCast("12a456").Code != "12a456", "a broken saved code is replaced");
            True(!WebCast.IsCode("12345") && !WebCast.IsCode("1234567") && WebCast.IsCode("000000"), "exactly 6 digits");
        }

        // ---- iPad mode: input messages -------------------------------------------------------

        static void TestDecodeInput()
        {
            InputEvent e = RemoteInput.Decode(new byte[] { 1, 0x80, 0x00, 0xFF, 0xFF }, 5);
            Equal(InputKind.Move, e.Kind, "move");
            Equal(0x8000, e.X, "x");
            Equal(0xFFFF, e.Y, "y");
            e = RemoteInput.Decode(new byte[] { 2, 1, 1, 0, 10, 0, 20 }, 7);
            True(e.Kind == InputKind.Button && e.Button == 1 && e.Down && e.X == 10 && e.Y == 20, "right button down");
            e = RemoteInput.Decode(new byte[] { 3, 0xFF, 0x88, 0x7F, 0xFF }, 5);
            Equal(-120, e.WheelY, "one notch down");
            Equal(1200, e.WheelX, "clamped to 10 notches");
            byte[] t = Encoding.UTF8.GetBytes("\u0004ąčę ok");
            e = RemoteInput.Decode(t, t.Length);
            Equal("ąčę ok", e.Text, "UTF-8 text");
            e = RemoteInput.Decode(new byte[] { 5, 0x0D }, 2);
            Equal((byte)0x0D, e.Key, "Enter");
            e = RemoteInput.Decode(new byte[] { 6, 0xFF, 0xF6, 0, 25 }, 5);
            True(e.Kind == InputKind.MoveBy && e.X == -10 && e.Y == 25, "a TV mouse: 10 left, 25 down");
            e = RemoteInput.Decode(new byte[] { 6, 0x7F, 0xFF, 0x80, 0x00 }, 5);
            True(e.X == 2000 && e.Y == -2000, "a huge jump is clamped");
            e = RemoteInput.Decode(new byte[] { 7, 0, 1 }, 3);
            True(e.Kind == InputKind.ButtonHere && e.Button == 0 && e.Down, "left down where the pointer is");
            e = RemoteInput.Decode(new byte[] { 8, 1, 0, 0x80, 0, 0x40, 0 }, 7);
            True(e.Kind == InputKind.ClickAt && e.Button == 1 && !e.Down && e.X == 0x8000 && e.Y == 0x4000, "right up at the TV's own pointer");
            e = RemoteInput.Decode(new byte[] { 9, 0xFF, 0x88, 0, 0, 0, 10, 0, 20 }, 9);
            True(e.Kind == InputKind.WheelAt && e.WheelY == -120 && e.X == 10 && e.Y == 20, "a wheel notch at the TV's pointer");
            Equal(null, RemoteInput.Decode(new byte[] { 8, 0, 1, 0, 0 }, 5), "short click-at");
        }

        static void TestDecodeInputRefusesOthers()
        {
            Equal(null, RemoteInput.Decode(new byte[] { 5, 0x5B }, 2), "Windows key not allowed");
            Equal(null, RemoteInput.Decode(new byte[] { 5, 0x12 }, 2), "Alt not allowed");
            Equal(null, RemoteInput.Decode(new byte[] { 2, 3, 1, 0, 0, 0, 0 }, 7), "no button 3");
            Equal(null, RemoteInput.Decode(new byte[] { 1, 0, 0, 0 }, 4), "short move");
            byte[] ctl = { 4, (byte)'a', 0x0D };
            Equal(null, RemoteInput.Decode(ctl, 3), "control characters only as KEY");
            byte[] badUtf8 = { 4, 0xC3 };
            Equal(null, RemoteInput.Decode(badUtf8, 2), "broken UTF-8");
            Equal(null, RemoteInput.Decode(new byte[600], 600), "too long");
            Equal(null, RemoteInput.Decode(new byte[] { 9 }, 1), "unknown type");
            Equal(null, RemoteInput.Decode(new byte[] { 6, 0, 1 }, 3), "short move-by");
            Equal(null, RemoteInput.Decode(new byte[] { 7, 3, 1 }, 3), "no button 3 here either");
        }

        // ---- TV box and settings ------------------------------------------------------------

        static void TestParseTv()
        {
            Equal(IPAddress.Parse("10.0.0.5"), MainForm.ParseTv("SONY KD-50X80J (10.0.0.5)"), "found TV entry");
            Equal(IPAddress.Parse("10.1.2.3"), MainForm.ParseTv("  10.1.2.3  "), "typed address");
            Equal(IPAddress.Parse("192.0.2.7"), MainForm.ParseTv("Living room (old) (192.0.2.7)"), "last brackets win");
        }

        static void TestSettingsLookupOrder()
        {
            string dir = Path.Combine(Path.GetTempPath(), "tvcast-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string local = Path.Combine(dir, "local.txt"), app = Path.Combine(dir, "app.txt"), old = Path.Combine(dir, "old.txt");
                string[] olds = { old };
                Equal(null, MainForm.FindSettings(local, app, olds), "nothing saved yet");
                File.WriteAllText(old, "x");
                Equal(old, MainForm.FindSettings(local, app, olds), "settings of an older version");
                File.WriteAllText(app, "x");
                Equal(app, MainForm.FindSettings(local, app, olds), "%APPDATA% before the old file");
                File.WriteAllText(local, "x");
                Equal(local, MainForm.FindSettings(local, app, olds), "next to the exe first");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
    }
}
