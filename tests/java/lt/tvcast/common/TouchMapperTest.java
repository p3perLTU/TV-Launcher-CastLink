package lt.tvcast.common;

import static lt.tvcast.testing.Assert.*;

import java.util.ArrayList;
import java.util.List;

/** Touch gestures on the phone's picture of the PC -> the PC's input messages. */
final class TouchMapperTest {
    static List<byte[]> sent;

    static TouchMapper mapper() {
        sent = new ArrayList<byte[]>();
        TouchMapper m = new TouchMapper(2f, msg -> sent.add(msg));
        m.setArea(0, 0, 2000, 1000); // the picture fills 2000 x 1000 screen pixels
        return m;
    }

    static String kinds() {
        StringBuilder b = new StringBuilder();
        for (byte[] m : sent) b.append(m[0] == 2 ? (m[2] == 1 ? "D" + m[1] : "U" + m[1]) : String.valueOf(m[0])).append(' ');
        return b.toString().trim();
    }

    static void testTapIsALeftClick() {
        TouchMapper m = mapper();
        m.start(1000, 500, 0);
        m.end(0, 80);
        assertEquals("1 D0 U0", kinds(), "move, left down, left up");
        assertArrayEquals(new byte[] { 1, (byte) 0x80, 0, (byte) 0x80, 0 }, sent.get(0), "the middle is 32768 x 32768");
    }

    static void testDragHoldsTheLeftButton() {
        TouchMapper m = mapper();
        m.start(100, 100, 0);
        m.move(1, 110, 100, 10);   // 5 dp: still a tap
        m.move(1, 200, 100, 20);   // 50 dp: a drag
        m.move(1, 300, 100, 30);
        m.end(0, 40);
        assertEquals("1 D0 1 1 U0", kinds(), "move, down, moves, up");
    }

    static void testLongPressIsARightClick() {
        TouchMapper m = mapper();
        m.start(500, 500, 0);
        m.tick(300);
        assertEquals("1", kinds(), "too early");
        m.tick(TouchMapper.LONG_PRESS_MS);
        m.end(0, 700);
        assertEquals("1 D1 U1", kinds(), "right click, and no left click on release");
    }

    static void testTwoFingerTapIsARightClick() {
        TouchMapper m = mapper();
        m.start(500, 500, 0);
        m.two(520, 500, 20);
        m.end(1, 120);
        m.end(0, 140);
        assertEquals("1 D1 U1", kinds(), "right click at the point between the fingers");
    }

    static void testTwoFingerSwipeScrolls() {
        TouchMapper m = mapper();
        m.start(500, 500, 0);
        m.two(500, 500, 10);
        m.move(2, 500, 540, 20);   // 20 dp down: 80 wheel units
        m.end(1, 400);
        m.end(0, 410);
        assertEquals("1 3", kinds(), "one wheel message, no click after a swipe");
        byte[] w = sent.get(1);
        assertEquals(80, (short) ((w[1] & 0xFF) << 8 | (w[2] & 0xFF)), "scroll amount");
    }

    static void testOutsideThePictureClampsToTheEdge() {
        TouchMapper m = mapper();
        m.setArea(100, 0, 1800, 1000); // black bars left and right
        int[] tl = m.norm(10, -5), br = m.norm(5000, 5000);
        assertEquals(0, tl[0], "left bar x");
        assertEquals(0, tl[1], "above y");
        assertEquals(65535, br[0], "beyond the right x");
        assertEquals(65535, br[1], "beyond the bottom y");
    }

    static void testHoverNearTheEdgeSnapsButTheClickDoesNot() {
        java.util.List<byte[]> sent = new java.util.ArrayList<>();
        TouchMapper m = new TouchMapper(2f, msg -> sent.add(msg)); // 10 dp = 20 px
        m.setArea(0, 0, 2000, 1000);
        assertEquals(0, m.hover(500, 15)[1], "15 px from the top hovers at the top edge");
        assertEquals(65535, m.hover(500, 985)[1], "15 px from the bottom hovers at the bottom edge");
        assertEquals(0, m.hover(10, 500)[0], "near the left edge");
        int off = m.hover(500, 100)[1];
        assertEquals(true, off > 5000 && off < 8000, "100 px from the top is not the edge");
        int click = m.norm(500, 15)[1];
        assertEquals(true, click > 700 && click < 1200, "a click 15 px from the top stays exactly there");
        m.start(500, 15, 0); // a quick tap: hover at the edge, then the click where the finger is
        m.end(0, 50);
        assertEquals(0, ((sent.get(0)[3] & 0xFF) << 8) | (sent.get(0)[4] & 0xFF), "the hover is at the edge");
        assertEquals(click, ((sent.get(1)[5] & 0xFF) << 8) | (sent.get(1)[6] & 0xFF), "the click is not snapped");
    }

    static void testCancelReleasesAHeldButton() {
        TouchMapper m = mapper();
        m.start(100, 100, 0);
        m.move(1, 300, 100, 10);
        m.cancel();
        assertEquals("1 D0 1 U0", kinds(), "the drag is let go");
    }

    static void testTextAndKeyBytes() {
        assertArrayEquals(new byte[] { 4, (byte) 0xC4, (byte) 0x85 }, TouchMapper.text("\u0105"), "UTF-8 text");
        assertArrayEquals(new byte[] { 5, 13 }, TouchMapper.key(13), "Enter");
    }

    static void testInputPacketLayout() {
        assertEquals(14, Proto.INPUT, "type");
        assertEquals(23, Proto.CONFIG_LONG, "CONFIG with flags and token");
    }
}
