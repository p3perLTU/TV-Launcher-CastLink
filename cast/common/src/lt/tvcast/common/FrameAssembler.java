package lt.tvcast.common;

import java.util.Arrays;
import java.util.concurrent.atomic.AtomicInteger;

/**
 * Puts VIDEO chunks back together into whole H.264 frames (TV side). Plain Java, no Android,
 * so it is unit tested on the PC (tests/java).
 *
 * Rules: chunks of one frame may come in any order; a frame that is still incomplete when a
 * newer one starts counts as lost; chunks of older frames are ignored; after any loss nothing
 * is returned until the next keyframe (a P-frame without its reference would only show
 * garbage), and {@link #wantsKey()} tells the caller to ask the sender for one.
 *
 * Only the receive thread calls {@link #accept}; {@link #requestKey()} may be called from any
 * thread.
 */
public final class FrameAssembler {
    public static final int MAX_CHUNKS = 1500;

    /** a finished frame: H.264 bytes (plus the suffix), sender pts and keyframe flag */
    public static final class Frame {
        public final byte[] data;
        public final long ptsUs;
        public final boolean key;

        Frame(byte[] data, long ptsUs, boolean key) {
            this.data = data;
            this.ptsUs = ptsUs;
            this.key = key;
        }
    }

    private final byte[] suffix;
    private final boolean[] gotChunk = new boolean[MAX_CHUNKS];
    private final byte[] asm = new byte[MAX_CHUNKS * Proto.MAX_PAYLOAD];
    private boolean haveFrame, haveDone;
    private int curFrame, curCount, curGot, curLast, curFlags, lastDone;
    private long curPts;
    private volatile boolean wantKey = true;
    private final AtomicInteger lost = new AtomicInteger();

    /** @param suffix bytes appended to every frame (e.g. an access unit delimiter), may be empty */
    public FrameAssembler(byte[] suffix) {
        this.suffix = suffix.clone();
    }

    /** back to "nothing received yet" (new stream or new picture size); waits for a keyframe */
    public void reset() {
        haveFrame = haveDone = false;
        wantKey = true;
    }

    /** drop everything up to the next keyframe (decoder trouble, full queue) */
    public void requestKey() {
        wantKey = true;
    }

    /** the caller dropped a finished frame: counts it as lost and waits for a keyframe */
    public void dropped() {
        lost.incrementAndGet();
        wantKey = true;
    }

    public boolean wantsKey() {
        return wantKey;
    }

    /** frames lost so far (never reset) */
    public int lost() {
        return lost.get();
    }

    /**
     * One VIDEO packet: {@code pkt[0]} is the type byte, {@code n} the packet length.
     * Returns the frame this chunk completed, or null.
     */
    public Frame accept(byte[] pkt, int n) {
        if (n < Proto.VIDEO_HEADER || pkt[0] != Proto.VIDEO) return null;
        int flags = pkt[1] & 0xFF;
        int frame = Proto.getInt(pkt, 2);
        int chunk = Proto.getShort(pkt, 6);
        int count = Proto.getShort(pkt, 8);
        long pts = Proto.getLong(pkt, 10);
        int len = n - Proto.VIDEO_HEADER;
        if (count == 0 || count > MAX_CHUNKS || chunk >= count || len > Proto.MAX_PAYLOAD) return null;

        if (haveFrame && frame - curFrame < -1000) {
            // numbering started over: the sender restarted without a BYE (crash, killed);
            // a real straggler is never this far behind
            haveFrame = haveDone = false;
            wantKey = true;
        }
        if (!haveFrame || frame != curFrame) {
            if (haveFrame && frame - curFrame < 0) return null; // straggler of an older frame
            if (haveFrame && curGot < curCount) lost.incrementAndGet();
            haveFrame = true;
            curFrame = frame;
            curCount = count;
            curGot = 0;
            curLast = 0;
            curFlags = flags;
            curPts = pts;
            Arrays.fill(gotChunk, 0, count, false);
        }
        if (count != curCount || gotChunk[chunk]) return null;
        gotChunk[chunk] = true;
        curGot++;
        System.arraycopy(pkt, Proto.VIDEO_HEADER, asm, chunk * Proto.MAX_PAYLOAD, len);
        if (chunk == count - 1) curLast = len;
        if (curGot < curCount) return null;

        boolean key = (curFlags & Proto.FLAG_KEY) != 0;
        boolean gap = haveDone && frame != lastDone + 1;
        haveDone = true;
        lastDone = frame;
        if (gap && !key) {
            // a whole frame went missing: everything until the next keyframe would be garbage
            lost.incrementAndGet();
            wantKey = true;
        }
        if (wantKey && !key) return null;
        if (key) wantKey = false;
        int total = (curCount - 1) * Proto.MAX_PAYLOAD + curLast;
        byte[] data = Arrays.copyOf(asm, total + suffix.length);
        System.arraycopy(suffix, 0, data, total, suffix.length);
        return new Frame(data, curPts, key);
    }
}
