package lt.tvcast.common;

import java.nio.charset.StandardCharsets;

/**
 * Touches on the phone's picture of the PC screen turned into the PC's input messages (the
 * same bytes the iPad page sends; decoded by cast/pc/RemoteInput.cs), with the same gestures:
 *
 *   tap = left click · drag = drag with the left button · long press or two-finger tap = right
 *   click · two-finger swipe = scroll wheel
 *
 * Plain Java (no Android), so it is unit-tested; ReceiverActivity feeds it the touch events in
 * screen pixels and calls {@link #tick} for the long press.
 */
public final class TouchMapper {
    /** where the messages go (the activity wraps them in Proto.INPUT packets) */
    public interface Out { void send(byte[] msg); }

    public static final long LONG_PRESS_MS = 550, TWO_FINGER_TAP_MS = 300;

    private final Out out;
    private final float density;
    private float areaX, areaY, areaW = 1, areaH = 1;

    // one finger
    private boolean one, drag, done;
    private float startX, startY;
    private long startAt;
    private int[] p; // its position on the PC screen
    // two fingers
    private boolean two;
    private float twoX, twoY, moved, accY, accX;
    private long twoAt;
    private int[] twoP;

    /** @param density screen pixels per dp (thresholds are in dp, like the page's CSS pixels) */
    public TouchMapper(float density, Out out) {
        this.density = density <= 0 ? 1 : density;
        this.out = out;
    }

    /** the picture's rectangle on the screen, in screen pixels (it may overhang in Fill mode) */
    public void setArea(float x, float y, float w, float h) {
        areaX = x; areaY = y; areaW = Math.max(1, w); areaH = Math.max(1, h);
    }

    /** a screen point as 0..65535 across the PC screen, clamped to its edges */
    public int[] norm(float x, float y) {
        int nx = Math.round((x - areaX) / areaW * 65535), ny = Math.round((y - areaY) / areaH * 65535);
        return new int[] { Math.max(0, Math.min(65535, nx)), Math.max(0, Math.min(65535, ny)) };
    }

    /** a finger this close (dp) to the picture's edge hovers at the very edge: a Mac's menu bar and
     *  window buttons (full screen) only appear with the pointer at y = 0, which a finger hardly hits.
     *  Only the hover snaps; a click stays exactly where the finger is (the buttons are in that zone). */
    public static final float EDGE_SNAP_DP = 10;

    /** where the pointer hovers when a finger touches: like {@link #norm}, snapped to a near edge */
    public int[] hover(float x, float y) {
        return new int[] { hover1(x - areaX, areaW), hover1(y - areaY, areaH) };
    }

    private int hover1(float v, float len) {
        float snap = Math.min(EDGE_SNAP_DP * density, len / 4);
        if (v <= snap) return 0;
        if (v >= len - snap) return 65535;
        return Math.max(0, Math.min(65535, Math.round(v / len * 65535)));
    }

    /** the first finger touches */
    public void start(float x, float y, long t) {
        one = true; drag = false; done = false; two = false;
        startX = x; startY = y; startAt = t;
        p = norm(x, y);
        out.send(move(hover(x, y)));
    }

    /** a second finger joins (cx, cy: between the two) */
    public void two(float cx, float cy, long t) {
        if (one && drag) out.send(button(0, false, p));
        one = false;
        two = true; twoX = cx; twoY = cy; moved = 0; accY = 0; accX = 0; twoAt = t;
        twoP = norm(cx, cy);
    }

    /** fingers move: one finger (its point) or two (the point between them) */
    public void move(int pointers, float x, float y, long t) {
        if (one && pointers == 1) {
            float dx = (x - startX) / density, dy = (y - startY) / density;
            if (!drag && !done && dx * dx + dy * dy > 100) { drag = true; out.send(button(0, true, p)); }
            if (drag) { p = norm(x, y); out.send(move(p)); }
        } else if (two && pointers >= 2) {
            float dy = (y - twoY) / density, dx = (twoX - x) / density;
            accY += dy * 4; accX += dx * 4; moved += Math.abs(dy) + Math.abs(dx);
            twoY = y; twoX = x;
            int sy = (int) (accY / 40) * 40, sx = (int) (accX / 40) * 40;
            if (sy != 0 || sx != 0) { out.send(wheel(sy, sx)); accY -= sy; accX -= sx; }
        }
    }

    /** a finger lifts; remaining = fingers still down */
    public void end(int remaining, long t) {
        if (one && remaining == 0) {
            if (drag) out.send(button(0, false, p));
            else if (!done) { out.send(button(0, true, p)); out.send(button(0, false, p)); }
            one = false;
        }
        if (two && remaining == 0) {
            if (moved < 12 && t - twoAt < TWO_FINGER_TAP_MS) { out.send(button(1, true, twoP)); out.send(button(1, false, twoP)); }
            two = false;
        }
    }

    /** call about LONG_PRESS_MS after start: a finger held still is a right click */
    public void tick(long t) {
        if (one && !drag && !done && t - startAt >= LONG_PRESS_MS) {
            done = true;
            out.send(button(1, true, p)); out.send(button(1, false, p));
        }
    }

    /** the gesture was taken away (three fingers, the system): let go of a held button */
    public void cancel() {
        if (one && drag) out.send(button(0, false, p));
        one = false; two = false;
    }

    // ---- the message bytes (RemoteInput.cs): 1 move, 2 button, 3 wheel, 4 text, 5 key -----

    public static byte[] move(int[] p) {
        return new byte[] { 1, (byte) (p[0] >> 8), (byte) p[0], (byte) (p[1] >> 8), (byte) p[1] };
    }

    public static byte[] button(int btn, boolean down, int[] p) {
        return new byte[] { 2, (byte) btn, (byte) (down ? 1 : 0), (byte) (p[0] >> 8), (byte) p[0], (byte) (p[1] >> 8), (byte) p[1] };
    }

    public static byte[] wheel(int dy, int dx) {
        return new byte[] { 3, (byte) (dy >> 8), (byte) dy, (byte) (dx >> 8), (byte) dx };
    }

    public static byte[] text(String s) {
        byte[] b = s.getBytes(StandardCharsets.UTF_8), m = new byte[b.length + 1];
        m[0] = 4;
        System.arraycopy(b, 0, m, 1, b.length);
        return m;
    }

    public static byte[] key(int vk) {
        return new byte[] { 5, (byte) vk };
    }
}
