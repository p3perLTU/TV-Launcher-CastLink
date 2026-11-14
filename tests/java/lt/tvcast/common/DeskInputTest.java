package lt.tvcast.common;

import static lt.tvcast.testing.Assert.*;

import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;

/** A mouse and a keyboard on the TV -> the PC's input messages. */
final class DeskInputTest {
    static List<byte[]> sent;

    static DeskInput input() {
        sent = new ArrayList<byte[]>();
        DeskInput d = new DeskInput(msg -> sent.add(msg));
        d.setArea(0, 0, 1920, 1080); // the picture fills the TV screen
        return d;
    }

    static String kinds() {
        StringBuilder b = new StringBuilder();
        for (byte[] m : sent) b.append(m[0] == 2 ? (m[2] == 1 ? "D" + m[1] : "U" + m[1]) : String.valueOf(m[0])).append(' ');
        return b.toString().trim();
    }

    static void testMoveIsSentOncePerPosition() {
        DeskInput d = input();
        d.move(960, 540);
        d.move(960, 540);
        d.move(961, 540);
        assertEquals("1 1", kinds(), "the same spot twice sends one move");
    }

    static void testClickAndRightClick() {
        DeskInput d = input();
        d.buttons(DeskInput.BUTTON_PRIMARY, 100, 100);
        d.buttons(0, 100, 100);
        d.buttons(DeskInput.BUTTON_SECONDARY, 100, 100);
        d.buttons(0, 100, 100);
        assertEquals("1 D0 U0 D1 U1", kinds(), "left click, then right click");
    }

    static void testDragHoldsTheButton() {
        DeskInput d = input();
        d.buttons(DeskInput.BUTTON_PRIMARY, 100, 100);
        d.move(200, 100);
        d.move(300, 100);
        d.buttons(0, 300, 100);
        assertEquals("1 D0 1 1 U0", kinds(), "down, moves, up");
    }

    static void testReleaseLetsGo() {
        DeskInput d = input();
        d.buttons(DeskInput.BUTTON_PRIMARY, 100, 100);
        d.release();
        d.release();
        assertEquals("1 D0 U0", kinds(), "a held button is let go once");
    }

    static void testCapturedMouseMovesBy() {
        DeskInput d = input();
        d.moveBy(0.4f, 0);
        d.moveBy(0.4f, 0);
        assertEquals(0, sent.size(), "less than a pixel: nothing yet");
        d.moveBy(0.4f, -3);
        assertArrayEquals(new byte[] { 6, 0, 1, (byte) 0xFF, (byte) 0xFD }, sent.get(0), "1 right, 3 up (fractions add up)");
        d.moveBy(9000, 0);
        assertArrayEquals(new byte[] { 6, 0x07, (byte) 0xD0, 0, 0 }, sent.get(1), "clamped to 2000");
    }

    static void testCapturedButtonsClickWhereThePointerIs() {
        DeskInput d = input();
        d.buttonsHere(DeskInput.BUTTON_PRIMARY);
        d.buttonsHere(DeskInput.BUTTON_PRIMARY | DeskInput.BUTTON_SECONDARY);
        d.releaseHere();
        assertArrayEquals(new byte[] { 7, 0, 1 }, sent.get(0), "left down");
        assertArrayEquals(new byte[] { 7, 1, 1 }, sent.get(1), "right down");
        assertEquals(4, sent.size(), "both let go");
    }

    static void testOwnPointerMovesOnlyOnTheTv() {
        DeskInput d = input();
        d.ownMove(100, 50);
        assertEquals(0, sent.size(), "moving the own pointer sends nothing: the PC's pointer stays");
        assertEquals(1060f, d.ownX(), "starts in the middle (960) and moved 100");
        d.ownMove(5000, 5000);
        assertEquals(1919f, d.ownX(), "kept inside the picture");
        assertEquals(1079f, d.ownY(), "bottom edge");
    }

    static void testOwnPointerClickAndDrag() {
        DeskInput d = input();
        d.ownButtons(DeskInput.BUTTON_PRIMARY);
        d.ownMove(10, 0);
        d.ownButtons(0);
        assertEquals(3, sent.size(), "down, a drag move, up");
        assertEquals(8, (int) sent.get(0)[0], "CLICKAT down");
        assertEquals(1, (int) sent.get(0)[2], "pressed");
        assertEquals(1, (int) sent.get(1)[0], "the PC's pointer follows while held");
        assertEquals(8, (int) sent.get(2)[0], "CLICKAT up (the PC puts its pointer back)");
        assertEquals(0, (int) sent.get(2)[2], "released");
    }

    static void testOwnButtonsUpTellsWhenNothingIsHeld() {
        DeskInput d = input();
        assertTrue(d.ownButtonsUp(), "nothing held at the start");
        d.ownButtons(DeskInput.BUTTON_PRIMARY);
        assertFalse(d.ownButtonsUp(), "held");
        d.ownButtons(0);
        assertTrue(d.ownButtonsUp(), "let go");
    }

    static void testOwnPointerWheel() {
        DeskInput d = input();
        d.ownScroll(-1, 0);
        assertEquals(9, (int) sent.get(0)[0], "WHEELAT");
        assertEquals(9, sent.get(0).length, "dy, dx, x, y");
    }

    static void testWheel() {
        DeskInput d = input();
        d.scroll(1, 0);
        d.scroll(0, 0);
        assertEquals(1, sent.size(), "no motion, no message");
        assertArrayEquals(new byte[] { 3, 0, 120, 0, 0 }, sent.get(0), "one notch up = 120");
    }

    static void testKeysThePcAccepts() {
        assertEquals(0x0D, DeskInput.vk(DeskInput.KEYCODE_ENTER), "Enter");
        assertEquals(0x08, DeskInput.vk(DeskInput.KEYCODE_DEL), "Backspace");
        assertEquals(0x25, DeskInput.vk(DeskInput.KEYCODE_DPAD_LEFT), "Left");
        assertEquals(0x1B, DeskInput.vk(DeskInput.KEYCODE_ESCAPE), "Esc");
        assertEquals(0, DeskInput.vk(29), "the letter A is typed as text, not a key");
    }

    static void testTypingLettersAsText() {
        DeskInput d = input();
        assertTrue(d.key(29, 'a', false), "a");
        assertTrue(d.key(0, 'ž', false), "a Lithuanian letter from the layout");
        assertTrue(!d.key(29, 'c', true), "Ctrl+C is not passed on");
        assertTrue(!d.key(0, 0, false), "a key with no character");
        assertEquals(2, sent.size(), "two letters");
        assertEquals("ž", new String(sent.get(1), 1, sent.get(1).length - 1, StandardCharsets.UTF_8), "UTF-8 text");
    }

    static void testEnterIsAKeyNotText() {
        DeskInput d = input();
        d.key(DeskInput.KEYCODE_ENTER, '\n', false);
        assertArrayEquals(new byte[] { 5, 0x0D }, sent.get(0), "KEY Enter");
    }

    static void testTextDropsControlCharacters() {
        DeskInput d = input();
        assertTrue(d.text("labas\n"), "text");
        assertEquals("labas", new String(sent.get(0), 1, sent.get(0).length - 1, StandardCharsets.UTF_8), "no newline");
        assertTrue(!d.text("\n"), "only control characters: nothing");
    }
}
