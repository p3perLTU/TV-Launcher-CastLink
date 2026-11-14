package lt.tvcast.launcher;

import static lt.tvcast.testing.Assert.*;

import java.util.Arrays;

/** Logo recolouring for the dark and the light glass, and the background un-mixing. */
final class LogoPixelsTest {
    static final int BLACK = 0xFF000000, WHITE = 0xFFFFFFFF, RED = 0xFFE00000, GREY = 0xFF505050;
    static final int CLEAR = 0x00000000;

    static int[] logo(int n, int fill, int rest, int restCount) {
        int[] px = new int[n];
        Arrays.fill(px, fill);
        for (int i = 0; i < restCount; i++) px[i] = rest;
        return px;
    }

    static void testArgbHelpers() {
        int p = LogoPixels.argb(0x80, 0x12, 0x34, 0x56);
        assertEquals(0x80123456, p, "argb");
        assertEquals(0x80, LogoPixels.alpha(p), "alpha");
        assertEquals(0x12, LogoPixels.red(p), "red");
        assertEquals(0x34, LogoPixels.green(p), "green");
        assertEquals(0x56, LogoPixels.blue(p), "blue");
        assertEquals(0xFF, LogoPixels.alpha(WHITE), "alpha of an opaque pixel is not negative");
    }

    static void testWhitenTurnsDarkLetteringWhite() {
        // red logo with black lettering (YouTube style)
        int[] px = logo(100, RED, BLACK, 30);
        px[99] = 0x80202020; // half transparent dark edge pixel
        LogoPixels.whitenDarkParts(px);
        assertEquals(WHITE, px[0], "black lettering became white");
        assertEquals(RED, px[50], "red kept");
        assertEquals(0x80FFFFFF, px[99], "alpha kept");
    }

    static void testWhitenKeepsGreyToo() {
        int[] px = logo(10, RED, GREY, 5);
        LogoPixels.whitenDarkParts(px);
        assertEquals(WHITE, px[0], "dark grey became white");
    }

    static void testWhitenLeavesMostlyWhiteLogos() {
        // white letters with a black shadow (twitch style): the shadow must stay
        int[] px = logo(100, WHITE, BLACK, 20);
        int[] before = px.clone();
        LogoPixels.whitenDarkParts(px);
        assertTrue(Arrays.equals(before, px), "unchanged");
    }

    static void testWhitenSkipsTransparentPixels() {
        int[] px = {CLEAR, BLACK, RED};
        LogoPixels.whitenDarkParts(px);
        assertEquals(CLEAR, px[0], "transparent pixel untouched");
        assertEquals(WHITE, px[1], "black became white");
    }

    static void testDarkenTurnsLightLetteringDark() {
        int[] px = logo(100, RED, WHITE, 30);
        LogoPixels.darkenLightParts(px);
        assertEquals(0xFF000000 | LogoPixels.INK, px[0], "white lettering became ink");
        assertEquals(RED, px[50], "red kept");
    }

    static void testDarkenLeavesMostlyBlackLogos() {
        int[] px = logo(100, BLACK, WHITE, 20);
        int[] before = px.clone();
        LogoPixels.darkenLightParts(px);
        assertTrue(Arrays.equals(before, px), "unchanged");
    }

    static void testDarkenNeedsLightParts() {
        int[] px = logo(10, RED, RED, 0);
        int[] before = px.clone();
        LogoPixels.darkenLightParts(px);
        assertTrue(Arrays.equals(before, px), "nothing light, nothing changed");
    }

    static void testDist() {
        assertEquals(5.0, LogoPixels.dist(LogoPixels.argb(255, 3, 4, 0), 0, 0, 0), 1e-6, "3-4-5");
        assertEquals(0.0, LogoPixels.dist(RED, 0xE0, 0, 0), 1e-6, "same colour");
    }

    static void testUnmix() {
        // logo value 200 drawn at 50 % over background 100 shows as 150
        assertEquals(200, LogoPixels.unmix(150, 100, 0.5f), "half transparent");
        assertEquals(77, LogoPixels.unmix(77, 10, 1f), "opaque pixel is itself");
        assertEquals(255, LogoPixels.unmix(250, 0, 0.5f), "clamped at 255");
        assertEquals(0, LogoPixels.unmix(10, 200, 0.5f), "clamped at 0");
    }
}
