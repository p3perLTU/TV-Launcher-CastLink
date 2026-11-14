package lt.tvcast.launcher;

/**
 * Pixel operations on app logos cut out of their TV banners. Pixels are ARGB ints as in
 * android.graphics.Bitmap; plain Java (no Android) so they are unit tested in tests/java.
 */
final class LogoPixels {
    private LogoPixels() {}

    /** the ink colour dark UI elements use, as RGB */
    static final int INK = 0x14141A;

    static int alpha(int p) { return p >>> 24; }
    static int red(int p)   { return (p >> 16) & 0xFF; }
    static int green(int p) { return (p >> 8) & 0xFF; }
    static int blue(int p)  { return p & 0xFF; }

    static int argb(int a, int r, int g, int b) {
        return a << 24 | r << 16 | g << 8 | b;
    }

    private static int sat(int p) {
        int r = red(p), g = green(p), b = blue(p);
        return Math.max(r, Math.max(g, b)) - Math.min(r, Math.min(g, b));
    }

    private static int lum(int p) {
        return (red(p) * 299 + green(p) * 587 + blue(p) * 114) / 1000;
    }

    // Black/grey lettering (YouTube, YouTube Music, Google Play) disappears on the dark glass,
    // so neutral dark pixels become white; coloured parts are kept. Logos that already have
    // a lot of white (twitch: white letters with a black shadow) are left alone, otherwise
    // the shadow would merge with the letters.
    static void whitenDarkParts(int[] px) {
        int opaque = 0, white = 0, dark = 0;
        for (int p : px) {
            if (alpha(p) < 128) continue;
            opaque++;
            int s = sat(p), l = lum(p);
            if (s < 40 && l > 200) white++;
            if (s < 40 && l < 140) dark++;
        }
        if (dark == 0 || white > opaque / 4) return;
        for (int i = 0; i < px.length; i++) {
            int p = px[i];
            if (alpha(p) == 0) continue;
            if (sat(p) < 40 && lum(p) < 140) px[i] = argb(alpha(p), 255, 255, 255);
        }
    }

    // The mirror of whitenDarkParts for the light glass: neutral light pixels become the dark
    // ink colour, unless the logo already has a lot of black (twitch's outlined letters).
    static void darkenLightParts(int[] px) {
        int opaque = 0, white = 0, dark = 0;
        for (int p : px) {
            if (alpha(p) < 128) continue;
            opaque++;
            int s = sat(p), l = lum(p);
            if (s < 40 && l > 170) white++;
            if (s < 40 && l < 90) dark++;
        }
        if (white == 0 || dark > opaque / 4) return;
        for (int i = 0; i < px.length; i++) {
            int p = px[i];
            if (alpha(p) == 0) continue;
            if (sat(p) < 40 && lum(p) > 170) px[i] = alpha(p) << 24 | INK;
        }
    }

    /** RGB distance between pixel {@code p} and the colour (r, g, b) */
    static float dist(int p, int r, int g, int b) {
        int dr = red(p) - r, dg = green(p) - g, db = blue(p) - b;
        return (float) Math.sqrt(dr * dr + dg * dg + db * db);
    }

    /**
     * One channel of a pixel that was drawn with opacity {@code a} over the background
     * channel {@code bgc}: the logo's own value before the mix, clamped to 0..255.
     */
    static int unmix(int c, int bgc, float a) {
        return Math.max(0, Math.min(255, Math.round((c - bgc * (1f - a)) / a)));
    }
}
