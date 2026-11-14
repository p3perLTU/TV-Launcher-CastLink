package lt.tvcast.common;

/**
 * A mouse and a keyboard plugged into (or paired with) the TV or tablet, turned into the PC's
 * input messages (the same bytes as the phone's touches and the iPad page; decoded by
 * cast/pc/RemoteInput.cs).
 *
 *   mouse: moves, left / right / middle button, drag, wheel
 *   keyboard: typed characters as text (any layout, Lithuanian letters too), plus the keys the
 *   PC accepts: Enter, Backspace, Delete, Tab, Esc, Space, arrows, Home / End, PgUp / PgDn.
 *   Shortcuts (Ctrl, Alt, the Windows key) are not passed on, like on the iPad.
 *
 * Plain Java (no Android): ReceiverActivity passes Android's key codes, button bits and screen
 * pixels, so this is unit-tested.
 */
public final class DeskInput {
    // Android's values (android.view.KeyEvent / MotionEvent), copied so no android.jar is needed
    public static final int KEYCODE_DPAD_UP = 19, KEYCODE_DPAD_DOWN = 20, KEYCODE_DPAD_LEFT = 21,
        KEYCODE_DPAD_RIGHT = 22, KEYCODE_TAB = 61, KEYCODE_SPACE = 62, KEYCODE_ENTER = 66,
        KEYCODE_DEL = 67, KEYCODE_PAGE_UP = 92, KEYCODE_PAGE_DOWN = 93, KEYCODE_ESCAPE = 111,
        KEYCODE_FORWARD_DEL = 112, KEYCODE_MOVE_HOME = 122, KEYCODE_MOVE_END = 123,
        KEYCODE_NUMPAD_ENTER = 160, KEYCODE_BACK = 4;
    public static final int BUTTON_PRIMARY = 1, BUTTON_SECONDARY = 2, BUTTON_TERTIARY = 4;

    private final TouchMapper.Out out;
    private final TouchMapper area = new TouchMapper(1, m -> { }); // only for norm()
    private int buttons;        // Android button bits held now
    private int[] last;         // last position sent

    public DeskInput(TouchMapper.Out out) { this.out = out; }

    /** the picture's rectangle on the screen, in screen pixels */
    public void setArea(float x, float y, float w, float h) { area.setArea(x, y, w, h); ownArea(x, y, w, h); }

    /** the pointer moved (hover or drag) */
    public void move(float x, float y) {
        int[] p = area.norm(x, y);
        if (last != null && last[0] == p[0] && last[1] == p[1]) return;
        last = p;
        out.send(TouchMapper.move(p));
    }

    /** the buttons now held (MotionEvent.getButtonState()); presses and releases are sent */
    public void buttons(int state, float x, float y) {
        move(x, y);
        int[] p = area.norm(x, y);
        int[] bits = { BUTTON_PRIMARY, BUTTON_SECONDARY, BUTTON_TERTIARY };
        for (int b = 0; b < 3; b++) {
            boolean was = (buttons & bits[b]) != 0, now = (state & bits[b]) != 0;
            if (was != now) out.send(TouchMapper.button(b, now, p));
        }
        buttons = state;
    }

    /** let go of everything (the stream stopped, the app went to the background) */
    public void release() {
        if (buttons != 0 && last != null) {
            int[] bits = { BUTTON_PRIMARY, BUTTON_SECONDARY, BUTTON_TERTIARY };
            for (int b = 0; b < 3; b++) if ((buttons & bits[b]) != 0) out.send(TouchMapper.button(b, false, last));
        }
        buttons = 0;
    }

    // ---- the captured mouse (Android pointer capture): it drives the PC like a real mouse, so
    // the pointer moves across every screen of the PC, not only the one shown on the TV ----

    private float restX, restY; // the fractions of a pixel not sent yet
    private int hereButtons;

    /** the mouse moved by (dx, dy) counts (a captured MotionEvent's getX / getY) */
    public void moveBy(float dx, float dy) {
        restX += dx;
        restY += dy;
        int x = (int) restX, y = (int) restY;
        if (x == 0 && y == 0) return;
        restX -= x;
        restY -= y;
        x = Math.max(-2000, Math.min(2000, x));
        y = Math.max(-2000, Math.min(2000, y));
        out.send(new byte[] { 6, (byte) (x >> 8), (byte) x, (byte) (y >> 8), (byte) y });
    }

    /** the buttons now held; presses and releases happen where the PC's pointer is */
    public void buttonsHere(int state) {
        int[] bits = { BUTTON_PRIMARY, BUTTON_SECONDARY, BUTTON_TERTIARY };
        for (int b = 0; b < 3; b++) {
            boolean was = (hereButtons & bits[b]) != 0, now = (state & bits[b]) != 0;
            if (was != now) out.send(new byte[] { 7, (byte) b, (byte) (now ? 1 : 0) });
        }
        hereButtons = state;
    }

    /** let go of the captured mouse's buttons (capture lost, stream stopped) */
    public void releaseHere() {
        if (hereButtons != 0) buttonsHere(0);
    }

    // ---- the TV's own pointer: drawn on the TV only, so the PC's pointer stays with the PC's
    // user; a click borrows the PC's pointer for a moment (CLICKAT / WHEELAT, put back after) ----

    private float ax, ay, aw = 1, ah = 1; // the picture's rectangle, as setArea got it
    private float ox = Float.NaN, oy;     // the own pointer, in screen pixels
    private int ownButtons;

    /** the picture's rectangle for the own pointer (call with setArea's values) */
    public void ownArea(float x, float y, float w, float h) {
        ax = x; ay = y; aw = Math.max(1, w); ah = Math.max(1, h);
        if (Float.isNaN(ox)) { ox = ax + aw / 2; oy = ay + ah / 2; }
        ox = Math.max(ax, Math.min(ax + aw - 1, ox));
        oy = Math.max(ay, Math.min(ay + ah - 1, oy));
    }

    public float ownX() { return ox; }
    public float ownY() { return oy; }

    /** the mouse moved by (dx, dy); with a button held the PC's pointer follows (a drag) */
    public void ownMove(float dx, float dy) {
        if (Float.isNaN(ox)) ownArea(ax, ay, aw, ah);
        ox = Math.max(ax, Math.min(ax + aw - 1, ox + dx));
        oy = Math.max(ay, Math.min(ay + ah - 1, oy + dy));
        if (ownButtons != 0) out.send(TouchMapper.move(area.norm(ox, oy)));
    }

    /** the buttons now held: presses and releases at the own pointer */
    public void ownButtons(int state) {
        if (Float.isNaN(ox)) ownArea(ax, ay, aw, ah);
        int[] p = area.norm(ox, oy);
        int[] bits = { BUTTON_PRIMARY, BUTTON_SECONDARY, BUTTON_TERTIARY };
        for (int b = 0; b < 3; b++) {
            boolean was = (ownButtons & bits[b]) != 0, now = (state & bits[b]) != 0;
            if (was != now) out.send(new byte[] { 8, (byte) b, (byte) (now ? 1 : 0), (byte) (p[0] >> 8), (byte) p[0], (byte) (p[1] >> 8), (byte) p[1] });
        }
        ownButtons = state;
    }

    /** the wheel at the own pointer */
    public void ownScroll(float v, float h) {
        if (Float.isNaN(ox)) ownArea(ax, ay, aw, ah);
        int dy = Math.round(v * 120), dx = Math.round(h * 120);
        if (dy == 0 && dx == 0) return;
        int[] p = area.norm(ox, oy);
        out.send(new byte[] { 9, (byte) (dy >> 8), (byte) dy, (byte) (dx >> 8), (byte) dx, (byte) (p[0] >> 8), (byte) p[0], (byte) (p[1] >> 8), (byte) p[1] });
    }

    /** no mouse button is held on the own pointer */
    public boolean ownButtonsUp() { return ownButtons == 0; }

    public void ownRelease() { if (ownButtons != 0) ownButtons(0); }

    /** wheel notches from MotionEvent.AXIS_VSCROLL / AXIS_HSCROLL (+1 = up / right) */
    public void scroll(float v, float h) {
        int dy = Math.round(v * 120), dx = Math.round(h * 120);
        if (dy != 0 || dx != 0) out.send(TouchMapper.wheel(dy, dx));
    }

    /** the PC's virtual-key code for an Android key, 0 if it is not one the PC accepts */
    public static int vk(int keyCode) {
        switch (keyCode) {
            case KEYCODE_DEL: return 0x08;
            case KEYCODE_TAB: return 0x09;
            case KEYCODE_ENTER: case KEYCODE_NUMPAD_ENTER: return 0x0D;
            case KEYCODE_ESCAPE: case KEYCODE_BACK: return 0x1B; // a keyboard's Esc may come as Back
            case KEYCODE_SPACE: return 0x20;
            case KEYCODE_PAGE_UP: return 0x21;
            case KEYCODE_PAGE_DOWN: return 0x22;
            case KEYCODE_MOVE_END: return 0x23;
            case KEYCODE_MOVE_HOME: return 0x24;
            case KEYCODE_DPAD_LEFT: return 0x25;
            case KEYCODE_DPAD_UP: return 0x26;
            case KEYCODE_DPAD_RIGHT: return 0x27;
            case KEYCODE_DPAD_DOWN: return 0x28;
            case KEYCODE_FORWARD_DEL: return 0x2E;
            default: return 0;
        }
    }

    /**
     * A key pressed on the keyboard. {@code unicode} is KeyEvent.getUnicodeChar(metaState) (0 if
     * none); {@code ctrlOrAlt} drops the key, so a shortcut never types a letter.
     * @return true if something was sent
     */
    public boolean key(int keyCode, int unicode, boolean ctrlOrAlt) {
        if (ctrlOrAlt) return false;
        int v = vk(keyCode);
        if (v != 0) {
            out.send(TouchMapper.key(v));
            return true;
        }
        if (unicode > 0 && !Character.isISOControl(unicode)) {
            out.send(TouchMapper.text(new String(Character.toChars(unicode))));
            return true;
        }
        return false;
    }

    /** text that arrives in one go (KeyEvent.ACTION_MULTIPLE, an on-screen keyboard) */
    public boolean text(String s) {
        if (s == null || s.isEmpty()) return false;
        StringBuilder b = new StringBuilder();
        for (int i = 0; i < s.length(); i++) if (!Character.isISOControl(s.charAt(i))) b.append(s.charAt(i));
        if (b.length() == 0) return false;
        out.send(TouchMapper.text(b.toString()));
        return true;
    }
}
