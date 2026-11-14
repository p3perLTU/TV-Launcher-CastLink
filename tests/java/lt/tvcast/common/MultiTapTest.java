package lt.tvcast.common;

import static lt.tvcast.testing.Assert.*;

/** The remote's number keys typing like an old phone keypad. */
final class MultiTapTest {
    static StringBuilder typed;

    static MultiTap keypad() {
        typed = new StringBuilder();
        return new MultiTap(new MultiTap.Out() {
            @Override public void text(String s) { typed.append(s); }
            @Override public void backspace() { if (typed.length() > 0) typed.setLength(typed.length() - 1); }
        });
    }

    static void testRepeatsCycleTheLetters() {
        MultiTap k = keypad();
        k.press(2, 0);
        assertEquals("a", typed.toString(), "first press");
        k.press(2, 300);
        assertEquals("b", typed.toString(), "second press replaces it");
        k.press(2, 600);
        k.press(2, 900);
        assertEquals("a", typed.toString(), "past the last letter it starts again");
    }

    static void testAPauseKeepsTheLetter() {
        MultiTap k = keypad();
        k.press(4, 0);
        k.press(4, MultiTap.CYCLE_MS);
        assertEquals("gg", typed.toString(), "after the pause it is a new letter");
    }

    static void testAnotherKeyKeepsTheLetter() {
        MultiTap k = keypad();
        k.press(4, 0);
        k.press(4, 100);
        k.press(3, 200);
        assertEquals("hd", typed.toString(), "h, then d");
    }

    static void testZeroIsSpaceAndOneIsPunctuation() {
        MultiTap k = keypad();
        k.press(0, 0);
        k.press(0, 100);
        k.press(1, 200);
        k.press(1, 300);
        assertEquals("  ,", typed.toString(), "space twice (it does not cycle), then . -> ,");
    }

    static void testHoldTypesTheDigit() {
        MultiTap k = keypad();
        k.press(7, 0);
        k.hold(7);
        k.press(7, 100);
        assertEquals("7p", typed.toString(), "the held key's letter became 7; the next press is a new letter");
    }

    static void testCapitals() {
        MultiTap k = keypad();
        assertTrue(k.toggleCase(), "now capitals");
        k.press(6, 0);
        k.press(6, 100);
        assertFalse(k.toggleCase(), "back to small");
        k.press(6, 200);
        assertEquals("Nm", typed.toString(), "N, then a new small m");
    }

    static void testResetEndsTheLetter() {
        MultiTap k = keypad();
        k.press(5, 0);
        k.reset();
        k.press(5, 100);
        assertEquals("jj", typed.toString(), "delete / Enter in between");
    }
}
