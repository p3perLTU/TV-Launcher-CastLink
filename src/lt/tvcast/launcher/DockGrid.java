package lt.tvcast.launcher;

/**
 * Home screen layout: 8 tiles as two 2x2 blocks (left and right of the weather), seen by the
 * remote as one grid of 2 rows x 4 columns. Index order is block by block, row by row:
 *
 *   0 1 | weather | 4 5
 *   2 3 |         | 6 7
 *
 * Plain Java (no Android), unit tested in tests/java.
 */
final class DockGrid {
    private DockGrid() {}

    static final int ROWS = 2, COLS = 4;

    static int row(int i) {
        return (i % 4) / 2;
    }

    static int col(int i) {
        return (i / 4) * 2 + i % 2;
    }

    /** the tile at (row, col), or -1 when that is outside the grid or past the last of {@code size} tiles */
    static int at(int row, int col, int size) {
        if (row < 0 || row >= ROWS || col < 0 || col >= COLS) return -1;
        int i = (col / 2) * 4 + row * 2 + col % 2;
        return i < size ? i : -1;
    }
}
