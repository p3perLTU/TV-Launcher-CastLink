package lt.tvcast.common;

/**
 * The remote's number keys as an old phone keypad: 2 = a, again within {@link #CYCLE_MS} = b,
 * then c; a pause or another key keeps the letter. Each letter is typed on the PC at once and a
 * repeat replaces it (backspace + the next one), so the PC's screen shows what is being picked.
 * Holding a key types the digit itself instead. 1 = punctuation, 0 = space.
 */
public final class MultiTap {
    public static final long CYCLE_MS = 1000;
    static final String[] LETTERS = {" ", ".,?!;:", "abc", "def", "ghi", "jkl", "mno", "pqrs", "tuv", "wxyz"};

    public interface Out {
        void text(String s);
        void backspace();
    }

    private final Out out;
    private int last = -1, index;
    private long lastAt;
    private boolean upper;

    public MultiTap(Out out) { this.out = out; }

    /** a number key pressed (no repeat) at {@code now} ms */
    public void press(int digit, long now) {
        if (digit < 0 || digit > 9) return;
        String keys = LETTERS[digit];
        if (digit == last && now - lastAt < CYCLE_MS && keys.length() > 1) {
            index = (index + 1) % keys.length();
            out.backspace();
        } else {
            index = 0;
        }
        last = digit;
        lastAt = now;
        String c = String.valueOf(keys.charAt(index));
        out.text(upper ? c.toUpperCase() : c);
    }

    /** the key is being held: the digit replaces the letter its press typed */
    public void hold(int digit) {
        if (digit < 0 || digit > 9) return;
        out.backspace();
        out.text(String.valueOf(digit));
        last = -1;
    }

    /** any other key (delete, Enter, ...) ends the current letter */
    public void reset() { last = -1; }

    /** capital / small letters; returns the new state */
    public boolean toggleCase() {
        upper = !upper;
        last = -1;
        return upper;
    }
}
