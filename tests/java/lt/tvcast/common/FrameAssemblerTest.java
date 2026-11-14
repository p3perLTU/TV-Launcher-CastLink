package lt.tvcast.common;

import static lt.tvcast.testing.Assert.*;

import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.Random;

/** The TV's reassembly of VIDEO chunks into frames, including loss and keyframe recovery. */
final class FrameAssemblerTest {
    static final byte[] AUD = {0, 0, 0, 1, 0x09, (byte) 0xF0};

    /** the packets a sender makes for one frame, the same way CastService / TVCastPC do */
    static List<byte[]> packets(int frame, boolean key, byte[] data, long pts) {
        List<byte[]> out = new ArrayList<>();
        int count = Proto.videoChunks(data.length);
        for (int c = 0; c < count; c++) {
            int o = c * Proto.MAX_PAYLOAD, l = Math.min(Proto.MAX_PAYLOAD, data.length - o);
            byte[] p = new byte[Proto.VIDEO_HEADER + l];
            Proto.putVideoHeader(p, key, frame, c, count, pts);
            System.arraycopy(data, o, p, Proto.VIDEO_HEADER, l);
            out.add(p);
        }
        return out;
    }

    static byte[] data(int len, int seed) {
        byte[] b = new byte[len];
        new Random(seed).nextBytes(b);
        return b;
    }

    static byte[] withAud(byte[] d) {
        byte[] r = new byte[d.length + AUD.length];
        System.arraycopy(d, 0, r, 0, d.length);
        System.arraycopy(AUD, 0, r, d.length, AUD.length);
        return r;
    }

    /** feeds all packets, returns the frame the last one completed (null if none) */
    static FrameAssembler.Frame feed(FrameAssembler a, List<byte[]> pkts) {
        FrameAssembler.Frame done = null;
        for (byte[] p : pkts) {
            FrameAssembler.Frame f = a.accept(p, p.length);
            if (f != null) done = f;
        }
        return done;
    }

    static void testSingleChunkKeyframe() {
        FrameAssembler a = new FrameAssembler(AUD);
        byte[] d = data(500, 1);
        FrameAssembler.Frame f = feed(a, packets(0, true, d, 1234));
        assertNotNull(f, "frame");
        assertArrayEquals(withAud(d), f.data, "data + delimiter");
        assertEquals(1234, f.ptsUs, "pts");
        assertTrue(f.key, "keyframe");
        assertFalse(a.wantsKey(), "no keyframe wanted after one arrived");
    }

    static void testChunksInAnyOrder() {
        FrameAssembler a = new FrameAssembler(new byte[0]);
        byte[] d = data(Proto.MAX_PAYLOAD * 3 + 17, 2);
        List<byte[]> p = packets(7, true, d, 0);
        Collections.reverse(p);
        FrameAssembler.Frame f = feed(a, p);
        assertNotNull(f, "frame");
        assertArrayEquals(d, f.data, "reassembled data");
    }

    static void testFramesInARow() {
        FrameAssembler a = new FrameAssembler(new byte[0]);
        assertNotNull(feed(a, packets(1, true, data(3000, 3), 0)), "keyframe");
        for (int i = 2; i < 10; i++) {
            byte[] d = data(2000 + i, i);
            FrameAssembler.Frame f = feed(a, packets(i, false, d, i));
            assertNotNull(f, "P-frame " + i);
            assertArrayEquals(d, f.data, "P-frame " + i + " data");
        }
        assertEquals(0, a.lost(), "nothing lost");
    }

    static void testWaitsForKeyframeAtStart() {
        FrameAssembler a = new FrameAssembler(new byte[0]);
        assertTrue(a.wantsKey(), "wants a keyframe at start");
        assertNull(feed(a, packets(0, false, data(100, 4), 0)), "P-frame before any keyframe");
        assertNotNull(feed(a, packets(1, true, data(100, 5), 0)), "keyframe");
    }

    static void testDuplicateChunkDoesNotCompleteFrame() {
        FrameAssembler a = new FrameAssembler(new byte[0]);
        List<byte[]> p = packets(0, true, data(Proto.MAX_PAYLOAD * 2, 6), 0);
        assertNull(a.accept(p.get(0), p.get(0).length), "first chunk");
        assertNull(a.accept(p.get(0), p.get(0).length), "same chunk again");
        assertNotNull(a.accept(p.get(1), p.get(1).length), "second chunk completes it");
    }

    static void testIncompleteFrameIsLostUntilKeyframe() {
        FrameAssembler a = new FrameAssembler(new byte[0]);
        feed(a, packets(0, true, data(100, 7), 0));
        List<byte[]> p1 = packets(1, false, data(Proto.MAX_PAYLOAD * 2, 8), 0);
        a.accept(p1.get(0), p1.get(0).length); // the second chunk never arrives
        assertNull(feed(a, packets(2, false, data(100, 9), 0)), "P-frame after a loss");
        assertEquals(2, a.lost(), "frame 1 (incomplete) and the gap before frame 2");
        assertTrue(a.wantsKey(), "asks for a keyframe");
        assertNull(feed(a, packets(3, false, data(100, 10), 0)), "still no keyframe");
        assertNotNull(feed(a, packets(4, true, data(100, 11), 0)), "keyframe recovers");
        assertFalse(a.wantsKey(), "recovered");
        assertNotNull(feed(a, packets(5, false, data(100, 12), 0)), "P-frames flow again");
    }

    static void testMissingWholeFrame() {
        FrameAssembler a = new FrameAssembler(new byte[0]);
        feed(a, packets(1, true, data(100, 13), 0));
        assertNull(feed(a, packets(3, false, data(100, 14), 0)), "frame 2 never came");
        assertEquals(1, a.lost(), "one lost");
        assertTrue(a.wantsKey(), "asks for a keyframe");
    }

    static void testStragglerOfOlderFrameIgnored() {
        FrameAssembler a = new FrameAssembler(new byte[0]);
        List<byte[]> old = packets(1, true, data(Proto.MAX_PAYLOAD * 2, 15), 0);
        a.accept(old.get(0), old.get(0).length);
        assertNotNull(feed(a, packets(2, true, data(100, 16), 0)), "newer keyframe");
        assertNull(a.accept(old.get(1), old.get(1).length), "late chunk of frame 1");
        assertNotNull(feed(a, packets(3, false, data(100, 17), 0)), "stream goes on");
    }

    static void testSenderRestartStartsOver() {
        FrameAssembler a = new FrameAssembler(new byte[0]);
        feed(a, packets(50000, true, data(100, 18), 0));
        // a restarted sender counts from 0 again: not a straggler, a new stream
        assertNull(feed(a, packets(0, false, data(100, 19), 0)), "P-frame of the new stream");
        assertNotNull(feed(a, packets(1, true, data(100, 20), 0)), "keyframe of the new stream");
    }

    static void testMalformedPacketsIgnored() {
        FrameAssembler a = new FrameAssembler(new byte[0]);
        byte[] p = new byte[Proto.VIDEO_HEADER + 10];
        Proto.putVideoHeader(p, true, 0, 0, 0, 0);
        assertNull(a.accept(p, p.length), "count 0");
        Proto.putVideoHeader(p, true, 0, 3, 3, 0);
        assertNull(a.accept(p, p.length), "chunk >= count");
        Proto.putVideoHeader(p, true, 0, 0, FrameAssembler.MAX_CHUNKS + 1, 0);
        assertNull(a.accept(p, p.length), "too many chunks");
        assertNull(a.accept(p, Proto.VIDEO_HEADER - 1), "shorter than the header");
        byte[] big = new byte[Proto.VIDEO_HEADER + Proto.MAX_PAYLOAD + 1];
        Proto.putVideoHeader(big, true, 0, 0, 1, 0);
        assertNull(a.accept(big, big.length), "payload larger than allowed");
        p[0] = Proto.AUDIO;
        assertNull(a.accept(p, p.length), "not a VIDEO packet");
        assertNotNull(feed(a, packets(0, true, data(10, 21), 0)), "still works afterwards");
    }

    static void testDroppedAndRequestKey() {
        FrameAssembler a = new FrameAssembler(new byte[0]);
        feed(a, packets(0, true, data(10, 22), 0));
        a.dropped();
        assertEquals(1, a.lost(), "dropped counts as lost");
        assertNull(feed(a, packets(1, false, data(10, 23), 0)), "waits for a keyframe after a drop");
        feed(a, packets(2, true, data(10, 24), 0));
        a.requestKey();
        assertNull(feed(a, packets(3, false, data(10, 25), 0)), "waits after requestKey");
        assertEquals(1, a.lost(), "requestKey is not a loss");
    }

    static void testResetWaitsForKeyframe() {
        FrameAssembler a = new FrameAssembler(new byte[0]);
        feed(a, packets(0, true, data(10, 26), 0));
        a.reset();
        assertTrue(a.wantsKey(), "wants a keyframe after reset");
        assertNull(feed(a, packets(1, false, data(10, 27), 0)), "P-frame after reset");
        assertEquals(0, a.lost(), "reset is not a loss");
    }
}
