package lt.tvcast.common;

import static lt.tvcast.testing.Assert.*;

/** Wire format helpers shared by the phone sender and the TV receiver. */
final class ProtoTest {
    /**
     * The same bytes are checked in tests/cs/Tests.cs for the PC sender, so a change on one
     * side only cannot slip through.
     */
    static final String GOLDEN_VIDEO_HEADER = "02 01 01020304 0005 0007 1122334455667788";

    static void testVideoHeaderLayout() {
        byte[] pkt = new byte[Proto.VIDEO_HEADER];
        Proto.putVideoHeader(pkt, true, 0x01020304, 5, 7, 0x1122334455667788L);
        assertArrayEquals(unhex(GOLDEN_VIDEO_HEADER), pkt, "VIDEO header");
        assertEquals(18, Proto.VIDEO_HEADER, "header size");
    }

    static void testVideoHeaderWithoutKeyFlag() {
        byte[] pkt = new byte[Proto.VIDEO_HEADER];
        Proto.putVideoHeader(pkt, false, 0, 0, 1, 0);
        assertEquals(Proto.VIDEO, pkt[0], "type");
        assertEquals(0, pkt[1], "flags");
    }

    static void testIntegersRoundTrip() {
        byte[] b = new byte[8];
        for (long v : new long[]{0, 1, -1, Long.MAX_VALUE, Long.MIN_VALUE, 0x00000000FFFFFFFFL, 0xFFFFFFFF00000000L}) {
            Proto.putLong(b, 0, v);
            assertEquals(v, Proto.getLong(b, 0), "long " + v);
        }
        for (int v : new int[]{0, 1, -1, Integer.MAX_VALUE, Integer.MIN_VALUE, 0x80, 0xFF00}) {
            Proto.putInt(b, 2, v);
            assertEquals(v, Proto.getInt(b, 2), "int " + v);
        }
    }

    static void testShortsAreUnsigned() {
        assertEquals(0xFFFF, Proto.getShort(new byte[]{(byte) 0xFF, (byte) 0xFF}, 0), "0xFFFF");
        assertEquals(0x8001, Proto.getShort(new byte[]{(byte) 0x80, 0x01}, 0), "0x8001");
    }

    static void testVideoChunks() {
        assertEquals(0, Proto.videoChunks(0), "empty frame");
        assertEquals(1, Proto.videoChunks(1), "1 byte");
        assertEquals(1, Proto.videoChunks(Proto.MAX_PAYLOAD), "exactly one payload");
        assertEquals(2, Proto.videoChunks(Proto.MAX_PAYLOAD + 1), "one byte more");
        assertEquals(0xFFFF, Proto.videoChunks(0xFFFF * Proto.MAX_PAYLOAD), "largest frame");
        assertEquals(-1, Proto.videoChunks(0xFFFF * Proto.MAX_PAYLOAD + 1), "too large");
    }

    static void testPacketsFitTheMtu() {
        // 1500-byte MTU minus 20 bytes IPv4 and 8 bytes UDP header
        assertTrue(Proto.VIDEO_HEADER + Proto.MAX_PAYLOAD <= 1500 - 28, "VIDEO packet fits the MTU");
        assertTrue(5 + Proto.AUDIO_CHUNK_BYTES <= 1500 - 28, "AUDIO packet fits the MTU");
        assertEquals(960, Proto.AUDIO_CHUNK_BYTES, "5 ms of 48 kHz stereo 16-bit");
    }

    static void testLanAddresses() {
        String[] lan = {"10.0.0.1", "10.255.255.255", "172.16.0.1", "172.31.255.254", "192.168.1.20", "169.254.3.4"};
        String[] notLan = {"8.8.8.8", "172.15.0.1", "172.32.0.1", "192.169.0.1", "11.0.0.1", "0.0.0.0", "255.255.255.255"};
        for (String a : lan) assertTrue(Proto.isLanV4(v4(a)), a + " is a home network address");
        for (String a : notLan) assertFalse(Proto.isLanV4(v4(a)), a + " is not a home network address");
        assertFalse(Proto.isLanV4(new byte[16]), "IPv6 is always refused");
        assertFalse(Proto.isLanV4(null), "no address");
    }

    static byte[] v4(String a) {
        String[] p = a.split("\\.");
        byte[] b = new byte[4];
        for (int i = 0; i < 4; i++) b[i] = (byte) Integer.parseInt(p[i]);
        return b;
    }
}
