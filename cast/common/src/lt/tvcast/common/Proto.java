package lt.tvcast.common;

/**
 * Wire format shared by the phone sender and the TV receiver. UDP on one port; the phone
 * sends to the TV, the TV answers to whatever address the packets came from. Big-endian
 * (ByteBuffer default). All packets below travel encrypted inside SecureLink.SEALED after
 * the SecureLink handshake (see SecureLink); only WHO / NAME discovery stays in the clear.
 *
 *   CONFIG  type | width int | height int | sampleRate int | channels byte      (1 s, also keepalive)
 *           [ | flags byte | token 8 bytes ]   from the PC: CONFIG_CONTROL = the receiver may send
 *           INPUT, signed with this random token (a phone that shows the PC controls it)
 *   VIDEO   type | flags byte | frame int | chunk short | count short | ptsUs long | H.264 bytes
 *   AUDIO   type | seq int | 16-bit PCM, little-endian, interleaved
 *   KEYREQ  type                                                                (TV -> phone)
 *   BYE     type                                                                (phone -> TV)
 *   PING    type | tvNanos long                                                 (TV -> phone, 2/s)
 *   PONG    type | tvNanos long | phoneNanos long                               (phone -> TV)
 *   INPUT   type | token 8 bytes | input message (cast/pc/RemoteInput.cs)       (receiver -> PC)
 *   INFO    type | width int | height int  the receiver's screen, landscape  (receiver -> PC, 2 s)
 *           so the PC sends the picture at that size (a phone: 2340 x 1080, not 1920 x 886)
 *   WHO     type                                                                (PC -> receiver)
 *   NAME    type | UTF-8 name, the same as its mDNS name                        (receiver -> PC)
 *           Android's mDNS does not answer on USB tethering, so the PC asks the phone on the
 *           cable (the tethering network's gateway) directly
 *
 * ptsUs is the phone's System.nanoTime() / 1000 at capture, so with the PING/PONG clock
 * offset the TV can show the real capture-to-decoded delay.
 */
public final class Proto {
    private Proto() {}

    public static final int PORT = 47800;
    public static final String NSD_TYPE = "_tvcast._udp.";

    public static final byte CONFIG = 1;
    public static final byte VIDEO = 2;
    public static final byte AUDIO = 3;
    public static final byte KEYREQ = 10;
    public static final byte BYE = 11;
    public static final byte PING = 12;
    public static final byte PONG = 13;
    public static final byte INPUT = 14;
    public static final byte WHO = 15;
    public static final byte NAME = 16;
    public static final byte INFO = 17;
    /** a phone receiver's answer to WHO: like NAME, so the PC shows a phone icon */
    public static final byte PHONENAME = 19;
    public static final int CONFIG_CONTROL = 1;
    public static final int TOKEN_BYTES = 8;
    /** CONFIG with the control flag and the token */
    public static final int CONFIG_LONG = 14 + 1 + 8; // + TOKEN_BYTES (a number: tests/cs compares the values)

    public static final int FLAG_KEY = 1;

    public static final int VIDEO_HEADER = 1 + 1 + 4 + 2 + 2 + 8;
    /** keeps a packet under a 1500-byte Ethernet/Wi-Fi MTU with IP + UDP headers */
    public static final int MAX_PAYLOAD = 1400;
    public static final int MAX_PACKET = 1500;

    public static final int SAMPLE_RATE = 48000;
    public static final int CHANNELS = 2;
    /** 5 ms of 48 kHz stereo 16-bit */
    public static final int AUDIO_CHUNK_BYTES = 48 * 5 * CHANNELS * 2;

    /** VIDEO packets needed for a frame of {@code len} bytes, or -1 if it does not fit the count field */
    public static int videoChunks(int len) {
        int count = (len + MAX_PAYLOAD - 1) / MAX_PAYLOAD;
        return count > 0xFFFF ? -1 : count;
    }

    /** writes the VIDEO header into {@code pkt[0..VIDEO_HEADER)}; the payload follows it */
    public static void putVideoHeader(byte[] pkt, boolean key, int frame, int chunk, int count, long ptsUs) {
        pkt[0] = VIDEO;
        pkt[1] = (byte) (key ? FLAG_KEY : 0);
        putInt(pkt, 2, frame);
        pkt[6] = (byte) (chunk >> 8);
        pkt[7] = (byte) chunk;
        pkt[8] = (byte) (count >> 8);
        pkt[9] = (byte) count;
        putLong(pkt, 10, ptsUs);
    }

    /**
     * Private IPv4 ranges and link-local, i.e. home networks, never the internet. The TV only
     * accepts packets from these; {@code addr} is {@code InetAddress.getAddress()} (4 bytes
     * for IPv4, 16 for IPv6, which is always refused).
     */
    public static boolean isLanV4(byte[] addr) {
        if (addr == null || addr.length != 4) return false;
        int b0 = addr[0] & 0xFF, b1 = addr[1] & 0xFF;
        return b0 == 10 || (b0 == 172 && b1 >= 16 && b1 <= 31) || (b0 == 192 && b1 == 168)
                || (b0 == 169 && b1 == 254);
    }

    public static void putInt(byte[] b, int o, int v) {
        b[o] = (byte) (v >> 24);
        b[o + 1] = (byte) (v >> 16);
        b[o + 2] = (byte) (v >> 8);
        b[o + 3] = (byte) v;
    }

    public static void putLong(byte[] b, int o, long v) {
        putInt(b, o, (int) (v >> 32));
        putInt(b, o + 4, (int) v);
    }

    public static int getInt(byte[] b, int o) {
        return (b[o] & 0xFF) << 24 | (b[o + 1] & 0xFF) << 16 | (b[o + 2] & 0xFF) << 8 | (b[o + 3] & 0xFF);
    }

    /** unsigned */
    public static int getShort(byte[] b, int o) {
        return (b[o] & 0xFF) << 8 | (b[o + 1] & 0xFF);
    }

    public static long getLong(byte[] b, int o) {
        return (long) getInt(b, o) << 32 | (getInt(b, o + 4) & 0xFFFFFFFFL);
    }
}
