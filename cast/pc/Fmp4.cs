// Fragmented MP4 for Media Source Extensions: the iPad's Safari cannot take raw H.264, but it
// plays fMP4 appended piece by piece. One init segment (ftyp + moov with the SPS/PPS), then
// one moof + mdat per encoded frame, so nothing waits for a second frame.
// Plain C# with no Windows APIs: unit tested in tests/cs.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TVCast
{
    static class Fmp4
    {
        public const int Timescale = 90000;

        /// <summary>The NAL units of an Annex-B buffer (start codes 00 00 01 / 00 00 00 01 removed).</summary>
        public static List<ArraySegment<byte>> SplitNals(byte[] b, int len)
        {
            List<ArraySegment<byte>> nals = new List<ArraySegment<byte>>();
            int start = -1;
            int i = 0;
            while (i + 2 < len)
            {
                if (b[i] == 0 && b[i + 1] == 0 && b[i + 2] == 1)
                {
                    if (start >= 0) nals.Add(Trim(b, start, i));
                    i += 3;
                    start = i;
                }
                else i++;
            }
            if (start >= 0 && start < len) nals.Add(new ArraySegment<byte>(b, start, len - start));
            nals.RemoveAll(delegate(ArraySegment<byte> s) { return s.Count == 0; });
            return nals;
        }

        // the zero before a 4-byte start code belongs to the start code, not to the previous NAL
        static ArraySegment<byte> Trim(byte[] b, int start, int end)
        {
            while (end > start && b[end - 1] == 0) end--;
            return new ArraySegment<byte>(b, start, end - start);
        }

        public static int NalType(ArraySegment<byte> n) { return n.Array[n.Offset] & 0x1F; }

        /// <summary>MSE codec string from an SPS, e.g. avc1.640028.</summary>
        public static string CodecString(byte[] sps)
        {
            return string.Format("avc1.{0:x2}{1:x2}{2:x2}", sps[1], sps[2], sps[3]);
        }

        /// <summary>A frame's NAL units with 4-byte lengths instead of start codes, without the
        /// SPS / PPS / AUD (those live in the init segment or mean nothing in MP4).</summary>
        public static byte[] ToLengthPrefixed(List<ArraySegment<byte>> nals)
        {
            MemoryStream m = new MemoryStream();
            foreach (ArraySegment<byte> n in nals)
            {
                int t = NalType(n);
                if (t == 7 || t == 8 || t == 9) continue;
                U32(m, (uint)n.Count);
                m.Write(n.Array, n.Offset, n.Count);
            }
            return m.ToArray();
        }

        // ---- init segment -------------------------------------------------------------------

        public static byte[] Init(byte[] sps, byte[] pps, int width, int height)
        {
            MemoryStream m = new MemoryStream();
            Box(m, "ftyp", delegate(Stream s)
            {
                Ascii(s, "iso5"); U32(s, 512);
                Ascii(s, "iso5"); Ascii(s, "iso6"); Ascii(s, "mp41"); Ascii(s, "avc1");
            });
            Box(m, "moov", delegate(Stream moov)
            {
                Full(moov, "mvhd", 0, 0, delegate(Stream s)
                {
                    U32(s, 0); U32(s, 0); U32(s, 1000); U32(s, 0); // times, timescale, duration
                    U32(s, 0x00010000); U16(s, 0x0100); Zero(s, 10);  // rate, volume, reserved
                    Matrix(s);
                    Zero(s, 24);                                       // pre_defined
                    U32(s, 2);                                         // next track id
                });
                Box(moov, "trak", delegate(Stream trak)
                {
                    Full(trak, "tkhd", 0, 3, delegate(Stream s)       // enabled, in movie
                    {
                        U32(s, 0); U32(s, 0); U32(s, 1); U32(s, 0); U32(s, 0); // times, track 1, reserved, duration
                        Zero(s, 8); U16(s, 0); U16(s, 0); U16(s, 0); U16(s, 0); // reserved, layer, group, volume, reserved
                        Matrix(s);
                        U32(s, (uint)width << 16); U32(s, (uint)height << 16);
                    });
                    Box(trak, "mdia", delegate(Stream mdia)
                    {
                        Full(mdia, "mdhd", 0, 0, delegate(Stream s)
                        {
                            U32(s, 0); U32(s, 0); U32(s, Timescale); U32(s, 0);
                            U16(s, 0x55C4); U16(s, 0);                 // language "und"
                        });
                        Full(mdia, "hdlr", 0, 0, delegate(Stream s)
                        {
                            U32(s, 0); Ascii(s, "vide"); Zero(s, 12); Ascii(s, "video"); s.WriteByte(0);
                        });
                        Box(mdia, "minf", delegate(Stream minf)
                        {
                            Full(minf, "vmhd", 0, 1, delegate(Stream s) { Zero(s, 8); });
                            Box(minf, "dinf", delegate(Stream dinf)
                            {
                                Full(dinf, "dref", 0, 0, delegate(Stream s)
                                {
                                    U32(s, 1);
                                    Full(s, "url ", 0, 1, delegate(Stream u) { }); // media is in this file
                                });
                            });
                            Box(minf, "stbl", delegate(Stream stbl)
                            {
                                Full(stbl, "stsd", 0, 0, delegate(Stream s)
                                {
                                    U32(s, 1);
                                    Box(s, "avc1", delegate(Stream a)
                                    {
                                        Zero(a, 6); U16(a, 1);                 // reserved, data reference index
                                        Zero(a, 16);                           // pre_defined, reserved
                                        U16(a, (ushort)width); U16(a, (ushort)height);
                                        U32(a, 0x00480000); U32(a, 0x00480000); // 72 dpi
                                        U32(a, 0); U16(a, 1);                  // reserved, frame count
                                        Zero(a, 32);                           // compressor name
                                        U16(a, 0x18); U16(a, 0xFFFF);          // depth, pre_defined
                                        Box(a, "avcC", delegate(Stream c) { AvcC(c, sps, pps); });
                                    });
                                });
                                Full(stbl, "stts", 0, 0, delegate(Stream s) { U32(s, 0); });
                                Full(stbl, "stsc", 0, 0, delegate(Stream s) { U32(s, 0); });
                                Full(stbl, "stsz", 0, 0, delegate(Stream s) { U32(s, 0); U32(s, 0); });
                                Full(stbl, "stco", 0, 0, delegate(Stream s) { U32(s, 0); });
                            });
                        });
                    });
                });
                Box(moov, "mvex", delegate(Stream mvex)
                {
                    Full(mvex, "trex", 0, 0, delegate(Stream s)
                    {
                        U32(s, 1); U32(s, 1); U32(s, 0); U32(s, 0); U32(s, 0); // track, description, defaults
                    });
                });
            });
            return m.ToArray();
        }

        static void AvcC(Stream c, byte[] sps, byte[] pps)
        {
            c.WriteByte(1);                       // configuration version
            c.WriteByte(sps[1]); c.WriteByte(sps[2]); c.WriteByte(sps[3]); // profile, compatibility, level
            c.WriteByte(0xFF);                    // 4-byte NAL lengths
            c.WriteByte(0xE1);                    // one SPS
            U16(c, (ushort)sps.Length); c.Write(sps, 0, sps.Length);
            c.WriteByte(1);                       // one PPS
            U16(c, (ushort)pps.Length); c.Write(pps, 0, pps.Length);
            if (sps[1] == 100 || sps[1] == 110 || sps[1] == 122 || sps[1] == 144)
            {
                // High profiles: 4:2:0, 8-bit (NV12 is always that), no SPS extensions
                c.WriteByte(0xFD); c.WriteByte(0xF8); c.WriteByte(0xF8); c.WriteByte(0);
            }
        }

        // ---- one frame ----------------------------------------------------------------------

        /// <summary>moof + mdat for one frame. decodeTime and duration are in Timescale units.</summary>
        public static byte[] Fragment(uint sequence, ulong decodeTime, uint duration, bool key, byte[] sample)
        {
            MemoryStream m = new MemoryStream();
            Box(m, "moof", delegate(Stream moof)
            {
                Full(moof, "mfhd", 0, 0, delegate(Stream s) { U32(s, sequence); });
                Box(moof, "traf", delegate(Stream traf)
                {
                    Full(traf, "tfhd", 0, 0x020000, delegate(Stream s) { U32(s, 1); }); // default-base-is-moof
                    Full(traf, "tfdt", 1, 0, delegate(Stream s) { U64(s, decodeTime); });
                    // data offset, sample duration, size and flags present
                    Full(traf, "trun", 0, 0x000701, delegate(Stream s)
                    {
                        U32(s, 1);
                        U32(s, 0); // data offset, patched below
                        U32(s, duration);
                        U32(s, (uint)sample.Length);
                        U32(s, key ? 0x02000000u : 0x01010000u); // sync sample / depends on others, non-sync
                    });
                });
            });
            byte[] moofBytes = m.ToArray();
            // the data offset counts from the start of moof to the first byte of mdat's payload
            int dataOffset = moofBytes.Length + 8;
            int at = FindTrunDataOffset(moofBytes);
            PutU32(moofBytes, at, (uint)dataOffset);
            MemoryStream outp = new MemoryStream();
            outp.Write(moofBytes, 0, moofBytes.Length);
            U32(outp, (uint)(8 + sample.Length));
            Ascii(outp, "mdat");
            outp.Write(sample, 0, sample.Length);
            return outp.ToArray();
        }

        // trun: size(4) type(4) version+flags(4) sample_count(4) data_offset(4)
        static int FindTrunDataOffset(byte[] b)
        {
            for (int i = 0; i + 4 <= b.Length; i++)
                if (b[i] == 't' && b[i + 1] == 'r' && b[i + 2] == 'u' && b[i + 3] == 'n') return i + 4 + 4 + 4;
            throw new InvalidOperationException("no trun");
        }

        // ---- box writing ----------------------------------------------------------------------

        delegate void Body(Stream s);

        static void Box(Stream outer, string type, Body body)
        {
            MemoryStream b = new MemoryStream();
            body(b);
            U32(outer, (uint)(8 + b.Length));
            Ascii(outer, type);
            b.WriteTo(outer);
        }

        static void Full(Stream outer, string type, int version, int flags, Body body)
        {
            Box(outer, type, delegate(Stream s)
            {
                U32(s, (uint)(version << 24 | flags));
                body(s);
            });
        }

        static void Matrix(Stream s)
        {
            uint[] m = { 0x00010000, 0, 0, 0, 0x00010000, 0, 0, 0, 0x40000000 };
            foreach (uint v in m) U32(s, v);
        }

        static void Ascii(Stream s, string t) { byte[] b = Encoding.ASCII.GetBytes(t); s.Write(b, 0, b.Length); }
        static void Zero(Stream s, int n) { for (int i = 0; i < n; i++) s.WriteByte(0); }
        static void U16(Stream s, ushort v) { s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
        static void U32(Stream s, uint v) { s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
        static void U64(Stream s, ulong v) { U32(s, (uint)(v >> 32)); U32(s, (uint)v); }
        static void PutU32(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
    }
}
